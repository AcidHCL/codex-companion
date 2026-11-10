using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopPet;

// A configuration editor only: the owner applies the update through the chat
// gate. Opening this view never sends a model request or reveals a saved key.
internal sealed class ChatApiConnectionView : UserControl, IDisposable
{
    private readonly Func<ChatApiConnectionUpdate, CancellationToken, Task> _apply;
    private readonly Func<Uri, bool> _hasSavedKey;
    private readonly Func<ChatApiConnectionUpdate, CancellationToken, Task<IReadOnlyList<string>>>? _listModels;
    private readonly ComboBox _provider;
    private readonly ComboBox _modelList;
    private readonly TextBox _address, _model;
    private readonly PasswordBox _key;
    private readonly CheckBox _images, _tools;
    private readonly StackPanel _apiFields;
    private readonly TextBlock _keyStatus, _status;
    private readonly Button _save, _clearKey, _refreshModels;
    private readonly CancellationTokenSource _lifetime = new();
    private ChatApiProfile _current;
    private bool _busy, _disposed, _sync, _deleteKey;

    internal ChatApiConnectionView(ChatApiProfile current, bool useApi,
        Func<ChatApiConnectionUpdate, CancellationToken, Task> apply,
        Func<Uri, bool>? hasSavedKey = null,
        Func<ChatApiConnectionUpdate, CancellationToken, Task<IReadOnlyList<string>>>? listModels = null)
    {
        _current = current.Normalize(); _apply = apply;
        VerticalAlignment = VerticalAlignment.Top;
        _hasSavedKey = hasSavedKey ?? (_ => false);
        _listModels = listModels;
        _status = Text("ChatApiConnectionStatus", "");
        var body = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        Content = new Border
        {
            Background = Ui.Brush("#FFFFFF"), BorderBrush = Ui.Brush("#DFE7D8"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = body,
            Margin = new Thickness(0, 0, 0, 14)
        };
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        _provider = new ComboBox { Name = "ChatApiProvider", SelectedValuePath = "Tag", FontSize = 13,
            Padding = new Thickness(9, 6, 9, 6), Width = 244, Margin = new Thickness(18, 0, 0, 0) };
        _provider.Items.Add(new ComboBoxItem { Content = L.T("本地 / 自建 Ollama", "Local / self-hosted Ollama"), Tag = false });
        _provider.Items.Add(new ComboBoxItem { Content = L.T("OpenAI 兼容 API", "OpenAI-compatible API"), Tag = true });
        AutomationProperties.SetName(_provider, L.T("聊天模型连接方式", "Chat model connection type"));
        DockPanel.SetDock(_provider, Dock.Right); heading.Children.Add(_provider);
        var title = Text("ChatApiConnectionTitle", L.T("模型连接", "Model connection"), 18, true);
        title.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(title); body.Children.Add(heading);
        _apiFields = new StackPanel(); body.Children.Add(_apiFields);
        _address = Field(_apiFields, "ChatApiBaseUrl", L.T("服务地址", "Base URL"), 2048);
        _apiFields.Children.Add(Text("ChatApiKeyLabel", "API Key", 12, true));
        _key = new PasswordBox { Name = "ChatApiKeyInput", MaxLength = 8192, FontSize = 13,
            Padding = new Thickness(9, 7, 9, 7) };
        AutomationProperties.SetName(_key, L.T("API 密钥（留空保留此地址已保存的密钥）", "API key (leave empty to keep this address's saved key)"));
        _clearKey = Button("ChatApiClearKey", L.T("清除密钥", "Clear key"), () =>
        {
            _deleteKey = true; _key.Clear();
            _status.Text = "";
            RefreshKeyStatus();
        });
        _apiFields.Children.Add(ActionRow(_key, _clearKey));
        _keyStatus = Text("ChatApiKeyStatus", ""); _apiFields.Children.Add(_keyStatus);
        _refreshModels = Button("ChatApiRefreshModels", L.T("读取模型列表", "Read model list"), async () => await ReadModelsAsync());
        _model = Field(_apiFields, "ChatApiModelId", L.T("模型 ID", "Model ID"), 256, _refreshModels);
        _modelList = new ComboBox { Name = "ChatApiAvailableModels", FontSize = 13, Padding = new Thickness(9, 6, 9, 6),
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 3, 0, 5) };
        AutomationProperties.SetName(_modelList, L.T("API 可用模型列表", "Available API models"));
        _modelList.SelectionChanged += (_, _) => { if (_modelList.SelectedItem is string name) _model.Text = name; };
        _apiFields.Children.Add(_modelList);
        _images = new CheckBox { Name = "ChatApiSupportsImages", Content = L.T("支持图片", "Image support"), Margin = new Thickness(0, 8, 20, 8) };
        _tools = new CheckBox { Name = "ChatApiSupportsTools", Content = L.T("支持工具调用", "Tool support"), Margin = new Thickness(20, 8, 0, 8) };
        AutomationProperties.SetName(_images, L.T("API 模型支持图片输入", "API model supports image input"));
        AutomationProperties.SetName(_tools, L.T("API 模型支持工具调用", "API model supports tool calling"));
        var capabilities = new Grid { Margin = new Thickness(0, 6, 0, 4) };
        capabilities.ColumnDefinitions.Add(new ColumnDefinition()); capabilities.ColumnDefinitions.Add(new ColumnDefinition());
        capabilities.Children.Add(_images); Grid.SetColumn(_tools, 1); capabilities.Children.Add(_tools); _apiFields.Children.Add(capabilities);
        _apiFields.Children.Add(Text("ChatApiDataHint", L.T(
            "聊天、发送的图片及已开启的主动功能会请求此服务，可能产生费用。",
            "Chat, sent images and enabled proactive features use this service and may incur charges.")));
        var help = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        help.Children.Add(Text("ChatApiBaseUrlHint", L.T(
            "OpenAI：https://api.openai.com/v1。其他服务填写基础地址，不含 /chat/completions；本机地址支持 HTTP。",
            "OpenAI: https://api.openai.com/v1. Enter other providers' base URLs without /chat/completions. Local addresses support HTTP.")));
        help.Children.Add(Text("ChatApiModelHint", L.T(
            "模型 ID 可直接填写，也可从读取的列表中选择。按服务商说明开启图片和工具能力。",
            "Enter a model ID or choose from the fetched list. Enable image and tool support according to your provider.")));
        help.Children.Add(Text("ChatApiModelListHint", L.T(
            "读取列表不发送对话，也不保存当前设置。修改需点击“保存并应用”才生效。",
            "Reading the list sends no chat and does not save these settings. Changes take effect with Save and apply.")));
        help.Children.Add(Text("ChatApiKeyHelp", L.T(
            "密钥在本机加密保存，分别绑定完整服务地址；留空保留当前地址的密钥。",
            "Keys are encrypted on this computer and bound to each full service address. Leave the field empty to keep this address's key.")));
        help.Children.Add(Text("ChatApiCompanionHint", L.T(
            "聊天与主动功能共用此模型，语音独立。屏幕内容须在“屏幕理解”中允许远程发送；其他开关与安静规则仍有效。",
            "Chat and proactive features share this model; speech is independent. Screen content requires remote sharing in Screen understanding. Existing switches and quiet rules still apply.")));
        _apiFields.Children.Add(new Expander { Name = "ChatApiHelp", Header = L.T("连接帮助", "Connection help"),
            Content = help, FontSize = 12, Foreground = Ui.Brush("#59735B"), Margin = new Thickness(0, 5, 0, 0) });
        _save = Button("ChatApiApply", L.T("保存并应用", "Save and apply"), async () => await ApplyAsync());
        _save.VerticalAlignment = VerticalAlignment.Center;
        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(_save, Dock.Left); footer.Children.Add(_save);
        _status.Margin = new Thickness(12, 0, 0, 0); _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status); body.Children.Add(footer);
        _provider.SelectionChanged += (_, _) => RefreshControls();
        _address.TextChanged += (_, _) =>
        {
            if (_sync) return;
            // A pasted replacement also belongs to the address at which it was
            // entered. Never carry it to another provider while editing.
            _deleteKey = false; _key.Clear(); ClearModelList(); RefreshKeyStatus();
        };
        _key.PasswordChanged += (_, _) => { if (!_sync && _key.Password.Length > 0) _deleteKey = false; RefreshKeyStatus(); };
        SetConfiguration(_current, useApi);
    }

    internal void SetConfiguration(ChatApiProfile profile, bool useApi)
    {
        if (_disposed || _busy) return;
        _sync = true;
        try
        {
            _current = profile.Normalize(); _provider.SelectedValue = useApi;
            _address.Text = _current.BaseUrl; _model.Text = _current.Model;
            _images.IsChecked = _current.SupportsImages; _tools.IsChecked = _current.SupportsTools;
            _deleteKey = false; _key.Clear();
            ClearModelList();
        }
        finally { _sync = false; }
        RefreshControls(); RefreshKeyStatus();
    }

    internal async Task ApplyAsync()
    {
        if (_disposed || _busy) return;
        ChatApiConnectionUpdate update;
        try
        {
            bool useApi = _provider.SelectedValue is true;
            var profile = useApi ? new ChatApiProfile { BaseUrl = _address.Text, Model = _model.Text,
                SupportsImages = _images.IsChecked == true, SupportsTools = _tools.IsChecked == true }.Validate() : _current;
            var keyChange = !useApi ? ApiKeyChange.Keep : _deleteKey ? ApiKeyChange.Delete :
                _key.Password.Length > 0 ? ApiKeyChange.Replace : ApiKeyChange.Keep;
            string? newKey = keyChange == ApiKeyChange.Replace ? ApiKeyStore.ValidateKey(_key.Password) : null;
            update = new ChatApiConnectionUpdate(useApi, profile, keyChange, newKey);
        }
        catch (ArgumentException ex) { _status.Text = ex.Message; return; }
        _busy = true; RefreshControls();
        _status.Text = L.T("正在保存连接设置…", "Saving connection settings…");
        try
        {
            await _apply(update, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            _current = update.Profile; _sync = true;
            _key.Clear(); _deleteKey = false; _sync = false;
            _status.Text = update.UseApi ? L.T("API 连接已保存。", "API connection saved.") :
                L.T("已切回 Ollama。", "Switched to Ollama.");
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = L.T("操作已取消，请确认当前连接状态。", "The operation was cancelled. Check the current connection status."); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { if (!_disposed) _status.Text = L.T("未能应用连接。请检查地址、模型名、密钥和本地存储权限后重试。", "Could not apply the connection. Check the URL, model ID, key and local storage permissions, then retry."); }
        finally { _busy = false; _sync = false; if (!_disposed) { RefreshControls(); RefreshKeyStatus(); } }
    }

    internal async Task ReadModelsAsync()
    {
        if (_disposed || _busy || _listModels == null) return;
        ChatApiConnectionUpdate update;
        try
        {
            var profile = new ChatApiProfile { BaseUrl = _address.Text, Model = _model.Text,
                SupportsImages = _images.IsChecked == true, SupportsTools = _tools.IsChecked == true }.Validate(requireModel: false);
            var change = _deleteKey ? ApiKeyChange.Delete : _key.Password.Length > 0 ? ApiKeyChange.Replace : ApiKeyChange.Keep;
            update = new ChatApiConnectionUpdate(true, profile, change,
                change == ApiKeyChange.Replace ? ApiKeyStore.ValidateKey(_key.Password) : null);
        }
        catch (ArgumentException ex) { _status.Text = ex.Message; return; }
        _busy = true; RefreshControls(); ClearModelList();
        _status.Text = L.T("正在读取模型列表…", "Reading available models…");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var models = await _listModels(update, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            var names = models.Where(ChatApiModel.IsValid).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(2000).ToArray();
            _modelList.ItemsSource = names;
            _modelList.Visibility = names.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = names.Length > 0 ? L.T($"读取到 {names.Length} 个模型；选择后再保存应用。", $"Found {names.Length} models. Select one, then save and apply.") :
                L.T("没有取得可用模型；仍可手动填写模型 ID 后保存。", "No usable models were returned. You can still enter a model ID and save.");
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = L.T("读取已取消或超时；可以重试或手动填写模型 ID。", "Reading was cancelled or timed out. Retry or enter a model ID manually."); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { if (!_disposed) _status.Text = L.T("未能读取模型列表。请检查服务地址和密钥；也可直接手动填写模型 ID。", "Could not read the model list. Check the service URL and key, or enter a model ID manually."); }
        finally { _busy = false; if (!_disposed) RefreshControls(); }
    }

    private void ClearModelList() { _modelList.ItemsSource = null; _modelList.Visibility = Visibility.Collapsed; }

    private void RefreshControls()
    {
        bool useApi = _provider.SelectedValue is true;
        _apiFields.Visibility = useApi ? Visibility.Visible : Visibility.Collapsed;
        _provider.IsEnabled = _save.IsEnabled = !_busy && !_disposed;
        _apiFields.IsEnabled = !_busy && !_disposed;
        _refreshModels.IsEnabled = !_busy && !_disposed && _listModels != null;
    }

    private void RefreshKeyStatus()
    {
        if (_sync || _disposed) return;
        if (_deleteKey) { _keyStatus.Text = L.T("保存后会清除此地址的密钥。", "Saving will remove the key for this address."); return; }
        if (_key.Password.Length > 0) { _keyStatus.Text = L.T("已输入新密钥；保存后替换此地址的密钥。", "A new key has been entered; saving replaces the key for this address."); return; }
        try
        {
            bool saved = _hasSavedKey(ChatApiEndpoint.Parse(_address.Text));
            _keyStatus.Text = saved ? L.T("此地址的密钥已保存 · 留空保留", "Key saved for this address · leave empty to keep") :
                L.T("未保存密钥 · 无需密钥的服务可留空", "No saved key · optional for keyless services");
        }
        catch (ArgumentException) { _keyStatus.Text = L.T("填写有效基础地址后显示对应密钥状态。", "Enter a valid base URL to see its saved-key status."); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _keyStatus.Text = L.T("暂时无法读取已保存的密钥状态。", "Saved-key status is temporarily unavailable."); }
    }

    private static TextBlock Text(string name, string value, double size = 12, bool bold = false)
    {
        var text = Ui.Text(value, size, bold ? "#34593F" : "#71836C", bold);
        text.Name = name; text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 5, 0, 5);
        return text;
    }
    private static TextBox Field(Panel body, string name, string label, int maximum, Button? action = null)
    {
        body.Children.Add(Text(name + "Label", label, 12, true));
        var box = new TextBox { Name = name, MaxLength = maximum, FontSize = 13, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 2, 0, 2) };
        AutomationProperties.SetName(box, label); body.Children.Add(action == null ? box : ActionRow(box, action)); return box;
    }
    private static Grid ActionRow(FrameworkElement field, Button action)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        action.Margin = new Thickness(10, 0, 0, 0); action.Padding = new Thickness(12, 7, 12, 7);
        action.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(field); Grid.SetColumn(action, 1); row.Children.Add(action); return row;
    }
    private static Button Button(string name, string text, Action action)
    {
        var button = Ui.Button(text, (_, _) => action()); button.Name = name;
        AutomationProperties.SetName(button, text); return button;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _key.Clear(); _lifetime.Dispose();
    }
}
