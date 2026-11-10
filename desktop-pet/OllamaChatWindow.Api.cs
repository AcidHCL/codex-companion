using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPet;

internal sealed partial class OllamaChatWindow
{
    private readonly ApiKeyStore _apiKeys;
    private readonly Func<ChatApiProfile, IOllamaClient> _apiClientFactory;
    private readonly Dictionary<string, IReadOnlyList<string>> _apiModelCatalogs = new(StringComparer.Ordinal);
    internal IReadOnlyList<string> AvailableApiModels => _settings.UseChatApi ? _installedModels : Array.Empty<string>();
    internal bool HasChatApiKey(Uri endpoint) => _apiKeys.HasKey(endpoint);

    private void RestoreConfiguredApiReadiness()
    {
        if (!_settings.UseChatApi || _client is not OpenAiCompatibleClient api ||
            !ChatApiModel.IsValid(_model) || _shutdown || _switching || _configuring) return;
        _capabilities = new(true, _settings.ApiProfile.SupportsImages, false, _settings.ApiProfile.SupportsTools);
        if (!_installedModels.Contains(_model, StringComparer.Ordinal)) SetModelList(new[] { _model }, _model);
        _connected = api.CanAttemptBackgroundRequest;
    }

    private IOllamaClient CreateApiClient(ChatApiProfile profile)
    {
        var endpoint = profile.Endpoint;
        return new OpenAiCompatibleClient(endpoint, () => _apiKeys.Read(endpoint), profile.Model,
            profile.SupportsImages, profile.SupportsTools);
    }

    internal async Task<IReadOnlyList<string>> ReadChatApiModelsAsync(ChatApiConnectionUpdate update, CancellationToken ct)
    {
        var profile = update.Profile.Validate(requireModel: false);
        var endpoint = profile.Endpoint;
        string? key = update.KeyChange switch
        {
            ApiKeyChange.Replace => ApiKeyStore.ValidateKey(update.NewApiKey ?? ""),
            ApiKeyChange.Delete => null,
            _ => _apiKeys.Read(endpoint)
        };
        using var candidate = new OpenAiCompatibleClient(endpoint, () => key, profile.Model, profile.SupportsImages, profile.SupportsTools);
        var models = await candidate.ListModelsAsync(ct);
        if (!candidate.ModelsListedRemotely) return Array.Empty<string>();
        _apiModelCatalogs[endpoint.AbsoluteUri] = models;
        return models;
    }

    // All provider changes run through the same gate as chat, voice input and
    // reactions. No inference, preload or unload occurs when saving settings.
    internal async Task ApplyChatApiConnectionAsync(ChatApiConnectionUpdate update, CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        if (_shutdown || _switching || ModelMemoryBusy || _preparingImage || _sessionSwitching)
            throw new InvalidOperationException(L.T("聊天正在切换或准备输入，请稍后再应用。", "Chat is switching or preparing input. Apply again shortly."));
        var profile = update.Profile.Validate(requireModel: update.UseApi);
        var oldProfile = _settings.ApiProfile;
        bool oldUseApi = _settings.UseChatApi, wasConnected = _connected;
        var endpoint = profile.Endpoint;
        string? oldKey = update.KeyChange == ApiKeyChange.Keep ? null : _apiKeys.Read(endpoint);
        if (update.KeyChange == ApiKeyChange.Replace) ApiKeyStore.ValidateKey(update.NewApiKey ?? "");
        var (operation, revision) = BeginConfiguration(ct);
        IOllamaClient? candidate = null;
        bool committed = false, keyChanged = false, entered = false;
        try
        {
            await StopGenerationAsync(); CheckConfiguration(operation, revision);
            await _modelCallGate.WaitAsync(operation.Token); entered = true;
            if (!SaveCurrentDraft()) throw new InvalidDataException(L.T("请先保存当前聊天草稿，再切换连接。", "Save the current chat draft before switching connections."));
            candidate = update.UseApi ? _apiClientFactory(profile) : _clientFactory(OllamaEndpoint.Parse(_settings.OllamaAddress));
            string model = update.UseApi ? profile.Model : _settings.OllamaModel;
            string scope = ChatModelConnection.Scope(candidate.Endpoint, update.UseApi);
            bool scopeChanged = scope != EndpointScope(_client.Endpoint);
            string persona = _settings.SkinId.ToLowerInvariant();
            ChatConversation? destination = null;
            if (scopeChanged)
            {
                var active = _chatStore.GetActiveConversationId(scope, persona);
                destination = active == null ? null : _chatStore.GetConversation(active);
                destination ??= _chatStore.CreateConversation(scope, persona, model);
                foreach (var turn in _chatStore.ReadTurns(destination.Id, HistoryPageSize))
                    foreach (var image in turn.Images) _ = OllamaChatImage.FromStored(image);
                foreach (var image in destination.DraftImages) _ = OllamaChatImage.FromStored(image);
            }
            CheckConfiguration(operation, revision);
            if (update.KeyChange == ApiKeyChange.Replace) { _apiKeys.Save(endpoint, update.NewApiKey!); keyChanged = true; }
            else if (update.KeyChange == ApiKeyChange.Delete) { _apiKeys.Delete(endpoint); keyChanged = true; }
            _settings.UseChatApi = update.UseApi; _settings.ApiProfile = profile;
            try { _saveSettings(); }
            catch
            {
                _settings.UseChatApi = oldUseApi; _settings.ApiProfile = oldProfile;
                if (keyChanged)
                {
                    if (oldKey == null) _apiKeys.Delete(endpoint); else _apiKeys.Save(endpoint, oldKey);
                    keyChanged = false;
                }
                throw;
            }
            InvalidateBehaviorContext();
            _libraryWindow?.Close(); _memoryWindow?.Close();
            var previous = _client;
            _client = candidate; candidate = null; committed = true;
            ModelConnectionRevision++;
            _model = model; _lastUsedModel = null;
            if (update.UseApi && _connectionWindow is OllamaConnectionWindow) _connectionWindow.Close();
            _capabilities = update.UseApi ? new(true, profile.SupportsImages, false, profile.SupportsTools) : new(null, null, null);
            _connected = update.UseApi && update.KeyChange != ApiKeyChange.Delete && model.Length > 0;
            var available = update.UseApi && _apiModelCatalogs.TryGetValue(endpoint.AbsoluteUri, out var listed)
                ? listed.Append(model).Distinct(StringComparer.Ordinal).ToArray() : new[] { model };
            SetModelList(update.UseApi ? available : Array.Empty<string>(), model);
            if (destination != null) RestoreConversation(destination); else RefreshWelcome();
            _contextNote.Text = ConnectionNote();
            try { previous.Dispose(); } catch { }
            SetStatus(update.UseApi
                ? L.T("API 设置已保存 · 模型尚未验证，可刷新列表或发送消息；按服务商规则计费。", "API settings saved · Model not yet verified. Refresh or send a message; provider pricing applies.")
                : L.T("已切回 Ollama，原地址和模型保持不变。", "Switched back to Ollama with the original address and model."));
        }
        catch
        {
            if (!committed && revision == _configurationRevision) _connected = wasConnected;
            throw;
        }
        finally
        {
            candidate?.Dispose();
            if (entered) _modelCallGate.Release();
            FinishConfiguration(operation);
            if (committed) ModelSelectionChanged?.Invoke();
        }
        if (!update.UseApi) await CheckConnectionAsync();
    }

    private string ApiConnectionStatus(IOllamaClient client) => client is OpenAiCompatibleClient { ModelsListedRemotely: false }
        ? L.T("API 未提供模型列表 · 使用手动模型，推理权限尚未验证", "API model list unavailable · Using manual model; inference access not verified")
        : L.T("已读取 API 模型列表 · 推理权限以实际请求为准", "API model list read · Inference access depends on the actual request");

    private void OpenConnectionSettings()
    {
        if (_shutdown || _switching || _preparingImage || ModelMemoryBusy) return;
        if (_connectionWindow != null) { _connectionWindow.Activate(); return; }
        CancelVoiceInput(); CancelBehaviorReaction();
        _configuring = true; RefreshControls();
        var view = new ChatApiConnectionView(_settings.ApiProfile, _settings.UseChatApi,
            ApplyChatApiConnectionAsync, HasChatApiKey, ReadChatApiModelsAsync);
        var body = new StackPanel(); body.Children.Add(view);
        var local = Ui.Button(L.T("设置 Ollama 地址与端口…", "Configure Ollama address & port…"), (_, _) =>
        {
            if (_settings.UseChatApi) { SetStatus(L.T("先选择 Ollama 并应用，再设置其地址。", "Select and apply Ollama before editing its address."), true); return; }
            _connectionWindow?.Close(); OpenOllamaConnectionSettings();
        });
        local.Name = "ChatApiEditOllama";
        local.Margin = new Thickness(18, 0, 18, 16); body.Children.Add(local);
        var version = Ui.VersionLabel(false); version.Margin = new Thickness(18, 0, 18, 12); body.Children.Add(version);
        var window = new Window { Title = AppVersion.WindowTitle(L.T("聊天模型连接", "Chat model connection")),
            Width = 580, Height = 700, MinWidth = 440, MinHeight = 400, Owner = this, Topmost = Topmost,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        window.Resources.MergedDictionaries.Add(Ui.CreateResources());
        _connectionWindow = window;
        window.Closed += (_, _) => { view.Dispose(); if (ReferenceEquals(_connectionWindow, window)) _connectionWindow = null; _configuring = false; if (!_shutdown) RefreshControls(); };
        window.Show();
    }
}
