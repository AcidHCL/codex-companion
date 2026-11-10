using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace DesktopPet;

// Reads are independent from inference. The action delegate is supplied by the
// shared chat host so dashboard and chat operations use the same model gate.
internal sealed record DashboardModelServices(
    Func<string, CancellationToken, Task<IReadOnlyList<OllamaRunningModel>>> ReadOllama,
    Func<CancellationToken, Task<PetVoiceHealth?>> ReadVoice,
    Func<CancellationToken, Task<IReadOnlyList<LocalGpuInfo>>> ReadGpu,
    Func<bool, Task<string>>? SetLlmLoaded = null,
    Func<string?>? LlmOperation = null,
    Func<string?>? LlmBlockedReason = null,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>>? ReadInstalledModels = null,
    Func<string, Task<string>>? SelectLlmModel = null,
    Func<string, CancellationToken, Task<OllamaGpuActivity>>? ReadOllamaActivity = null,
    Func<string, OllamaRequestSnapshot>? ReadOwnActivity = null,
    Func<ChatApiConnectionUpdate, CancellationToken, Task>? ApplyChatApiConnection = null,
    Func<Uri, bool>? HasChatApiKey = null,
    Func<IReadOnlyList<string>>? ReadChatApiModels = null,
    Func<ChatApiConnectionUpdate, CancellationToken, Task<IReadOnlyList<string>>>? ListChatApiModels = null)
{
    internal static DashboardModelServices Create(PetWindow? pet,
        Func<bool, Task<string>>? setLlmLoaded = null, Func<string?>? operation = null,
        Func<string?>? blockedReason = null, Func<string, Task<string>>? selectLlmModel = null,
        Func<ChatApiConnectionUpdate, CancellationToken, Task>? applyChatApiConnection = null,
        Func<Uri, bool>? hasChatApiKey = null, Func<IReadOnlyList<string>>? readChatApiModels = null,
        Func<ChatApiConnectionUpdate, CancellationToken, Task<IReadOnlyList<string>>>? listChatApiModels = null) => new(
        async (address, ct) =>
        {
            using var client = new OllamaClient(OllamaEndpoint.Parse(address));
            return await client.ListRunningModelsAsync(ct);
        },
        ct => pet?.ReadSpeechModelHealthAsync(ct) ?? Task.FromResult<PetVoiceHealth?>(null),
        LocalGpuTelemetry.ReadAsync, setLlmLoaded, operation, blockedReason,
        async (address, ct) =>
        {
            using var client = new OllamaClient(OllamaEndpoint.Parse(address));
            return await client.ListModelsAsync(ct);
        }, selectLlmModel, OllamaActivityTelemetry.ReadAsync, OllamaRequestActivity.Snapshot,
        applyChatApiConnection, hasChatApiKey, readChatApiModels, listChatApiModels);
}

internal sealed partial class ModelSettingsView
{
    private readonly DashboardModelServices _modelServices;
    private readonly Button _modelRefresh = new() { Name = "ModelRefresh", Content = L.T("刷新状态", "Refresh status") };
    private readonly Button _llmLoad = new() { Name = "DashboardLlmLoad", Content = L.T("加载 LLM", "Load LLM") };
    private readonly Button _llmUnload = new() { Name = "DashboardLlmUnload", Content = L.T("卸载 LLM", "Unload LLM") };
    private readonly Button _voiceLoad = new() { Name = "DashboardVoiceLoad", Content = L.T("加载语音", "Load speech") };
    private readonly Button _voiceUnload = new() { Name = "DashboardVoiceUnload", Content = L.T("卸载语音", "Unload speech") };
    private readonly TextBlock _modelUpdated = ModelText(L.T("打开此页后读取状态", "Status is read when this page opens"), "ModelUpdated");
    private readonly ComboBox _llmSelector = new() { Name = "DashboardLlmSelector" };
    private readonly TextBlock _llmListStatus = ModelText(L.T("正在读取已安装模型…", "Reading installed models…"), "DashboardLlmListStatus");
    private readonly TextBlock _llmEndpoint = ModelText("", "DashboardLlmEndpoint");
    private readonly TextBlock _llmState = ModelText(L.T("等待查询", "Waiting for status"), "DashboardLlmState", 14, true);
    private readonly TextBlock _llmMemory = ModelText("", "DashboardLlmMemory");
    private readonly TextBlock _llmOthers = ModelText("", "DashboardLlmOthers");
    private readonly TextBlock _llmModelAction = ModelText("", "DashboardLlmAction");
    private readonly TextBlock _voiceState = ModelText(L.T("等待查询", "Waiting for status"), "DashboardVoiceState", 14, true);
    private readonly TextBlock _voiceName = ModelText("Qwen3-TTS · 1.7B", "DashboardVoiceName", 16, true);
    private readonly TextBlock _voiceBackend = ModelText("", "DashboardVoiceBackend");
    private readonly TextBlock _voiceMemory = ModelText("", "DashboardVoiceMemory");
    private readonly TextBlock _voiceModelAction = ModelText("", "DashboardVoiceAction");
    private readonly ComboBox _llmIdleUnload = new() { Name = "DashboardLlmIdleUnload" };
    private readonly ComboBox _voiceIdleUnload = new() { Name = "DashboardVoiceIdleUnload" };
    private readonly TextBlock _llmIdleHint = ModelText("", "DashboardLlmIdleHint");
    private readonly TextBlock _voiceIdleHint = ModelText("", "DashboardVoiceIdleHint");
    private bool _syncIdleUnload;
    private sealed record ModelIdleChoice(int Minutes, string Label);
    private readonly ComboBox _voiceCharacter = new() { Name = "DashboardVoiceCharacter" };
    private readonly ComboBox _voiceModel = new() { Name = "DashboardVoiceModel" };
    private readonly ComboBox _voiceSpeaker = new() { Name = "DashboardVoiceSpeaker" };
    private readonly ComboBox _voiceLanguage = new() { Name = "DashboardVoiceLanguage" };
    private readonly TextBox _voiceInstruction = new() { Name = "DashboardVoiceInstruction", MaxLength = 600, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 76, MaxHeight = 110 };
    private readonly Button _voiceProfileSave = new() { Name = "DashboardVoiceProfileSave", Content = L.T("应用生成方式", "Apply method") };
    private readonly TextBlock _voiceProfileStatus = ModelText("", "DashboardVoiceProfileStatus");
    private readonly TextBlock _voiceProfileHint = ModelText("", "DashboardVoiceProfileHint");
    private readonly TextBlock _voiceCurrentRole = ModelText("", "DashboardVoiceCurrentRole", 11, true);
    private readonly TextBlock _voiceInstructionLabel = ModelText(L.T("音色与语气描述", "Voice & delivery description"), "DashboardVoiceInstructionLabel", 11, true);
    private StackPanel? _voiceSpeakerField, _voiceInstructionField;
    private bool _syncVoiceProfile, _voiceProfileDirty, _voiceLanguageDirty;
    private PetVoiceProfile? _voiceProfileLoaded;
    private PetVoiceProfile? _observedVoiceProfile;
    private string? _observedVoiceSkin;
    // These choices describe how speech is produced; persisted model IDs remain
    // unchanged so existing role profiles and saved references stay compatible.
    private static PetVoiceChoice[] VoiceGenerationModes => new PetVoiceChoice[] {
        new("disabled", L.T("不启用此角色语音", "Disable speech for this character")), new("fixed", L.T("使用已保存音色（Base）", "Use a saved voice (Base)")),
        new("custom-voice", L.T("使用预置说话人（CustomVoice）", "Use a preset speaker (CustomVoice)")), new("voice-design", L.T("按文字即时生成（VoiceDesign）", "Generate from a description (VoiceDesign)"))
    };
    private readonly StackPanel _gpuRows = new() { Name = "DashboardGpuRows" };
    private readonly DispatcherTimer _modelTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _modelPageLifetime = new();
    private ScrollViewer? _modelPage;
    private CancellationTokenSource? _modelReadCancellation;
    private bool _showModelPage, _modelRefreshing, _llmModelOperation, _modelRefreshPending;
    private long _modelReadEpoch;
    private string? _lastLlmBlockReason, _lastVoiceBlockReason;
    private string? _modelSnapshotKey;
    private string? _installedModelsEndpoint;
    private IReadOnlyList<string> _dashboardInstalledModels = Array.Empty<string>();
    private bool _syncModelSelector, _installedListAvailable, _selectingLlm;
    private ChatApiConnectionView? _chatApiConnection;
    private Expander? _chatApiExpander;
    private ChatApiProfile? _chatApiSnapshot;
    private bool _chatApiSnapshotEnabled;
    private readonly TextBlock _llmLabel = ModelText("", "LlmModelLabel", 14, true);
    private readonly TextBlock _llmMemoryHint = ModelText("", "LlmMemoryHint");
    private string ActiveChatModel => _settings.UseChatApi ? _settings.ApiProfile.Model : _settings.OllamaModel;
    private string ActiveModelScope => _settings.UseChatApi ? "api:" + _settings.ApiProfile.BaseUrl : _settings.OllamaAddress;

    private static TextBlock ModelText(string text, string name, double size = 11, bool bold = false)
    {
        var view = Ui.Text(text, size, bold ? "#294B36" : "#71826D", bold);
        view.Name = name; view.TextWrapping = TextWrapping.Wrap;
        view.Margin = new Thickness(0, 0, 0, 8);
        var style = new Style(typeof(TextBlock));
        var empty = new DataTrigger { Binding = new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self }, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); style.Triggers.Add(empty); view.Style = style;
        return view;
    }

    private void BuildChatConnectionEditor(StackPanel body)
    {
        _chatApiConnection = new ChatApiConnectionView(_settings.ApiProfile, _settings.UseChatApi,
            async (update, ct) =>
            {
                if (_closed || _modelServices.ApplyChatApiConnection == null)
                    throw new InvalidOperationException(L.T("请从桌宠打开设置修改连接。", "Open Settings from the pet to change the connection."));
                InvalidateModelRead();
                await _modelServices.ApplyChatApiConnection(update, ct);
                if (!_closed) ModelSelectionChanged();
            }, _modelServices.HasChatApiKey, _modelServices.ListChatApiModels);
        _chatApiExpander = new Expander { Name = "ChatApiConnectionExpander", Content = _chatApiConnection,
            Margin = new Thickness(0, 0, 0, 12), IsExpanded = false };
        body.Children.Add(_chatApiExpander);
        SyncChatConnectionEditor();
    }

    private void SyncChatConnectionEditor()
    {
        if (_chatApiExpander != null) _chatApiExpander.Header = _settings.UseChatApi
            ? L.T("模型连接 · API", "Model connection · API")
            : L.T("模型连接 · Ollama", "Model connection · Ollama");
        if (_chatApiSnapshot == _settings.ApiProfile && _chatApiSnapshotEnabled == _settings.UseChatApi) return;
        _chatApiSnapshot = _settings.ApiProfile; _chatApiSnapshotEnabled = _settings.UseChatApi;
        _chatApiConnection?.SetConfiguration(_settings.ApiProfile, _settings.UseChatApi);
    }

    private void BuildModelPage()
    {
        var body = new StackPanel();
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_modelRefresh, Dock.Right); heading.Children.Add(_modelRefresh);
        var caption = new StackPanel();
        caption.Children.Add(ModelText(L.T("模型与语音", "Models & speech"), "ModelPageTitle", 23, true));
        caption.Children.Add(_modelUpdated); heading.Children.Add(caption); body.Children.Add(heading);
        BuildChatConnectionEditor(body);
        body.Children.Add(BuildSpeechPreferences());
        var cards = new Grid();
        cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
        var voice = new StackPanel();
        var voiceHeading = new DockPanel { LastChildFill = true };
        _voiceCurrentRole.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(_voiceCurrentRole, Dock.Right); voiceHeading.Children.Add(_voiceCurrentRole);
        voiceHeading.Children.Add(ModelText(L.T("语音模型", "Speech model"), "VoiceModelLabel", 14, true));
        voice.Children.Add(voiceHeading);
        voice.Children.Add(_voiceName); voice.Children.Add(_voiceState); voice.Children.Add(_voiceBackend);
        voice.Children.Add(_voiceMemory);
        _voiceMemory.ToolTip = L.T("语音进程的 PyTorch 统计；保留量包含可复用缓存。", "PyTorch memory for the speech process. Reserved memory includes reusable cache.");
        var voiceButtons = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        _voiceLoad.Margin = new Thickness(0, 0, 8, 0); voiceButtons.Children.Add(_voiceLoad); voiceButtons.Children.Add(_voiceUnload);
        voice.Children.Add(voiceButtons); voice.Children.Add(_voiceModelAction);
        BuildIdleUnloadChoice(voice, _voiceIdleUnload, _voiceIdleHint, speech: true);
        _voiceLoad.ToolTip = L.T("加载当前桌宠角色保存的声音模型；需要开启语音播报", "Load the current pet's speech model; Pet speech must be enabled");
        _voiceUnload.ToolTip = L.T("停止播报并释放语音显存；下次主动播报时可以重新加载", "Stop speech and release GPU memory; the next speech request can reload it");
        _voiceLoad.Click += async (_, _) => await ChangeSpeechModelLoadAsync(true);
        _voiceUnload.Click += async (_, _) => await ChangeSpeechModelLoadAsync(false);
        var voiceCard = Card(voice); voiceCard.Margin = new Thickness(0, 0, 7, 0); cards.Children.Add(voiceCard);
        var llm = new StackPanel();
        llm.Children.Add(_llmLabel);
        ConfigureCombo(_llmSelector);
        _llmSelector.FontSize = 13; _llmSelector.MinHeight = 36;
        _llmSelector.Margin = new Thickness(0, 0, 0, 7);
        _llmSelector.DisplayMemberPath = nameof(OllamaModelChoice.DisplayName);
        _llmSelector.SelectedValuePath = nameof(OllamaModelChoice.Id);
        _llmSelector.ToolTip = L.T("切换当前聊天模型，与对话窗口同步；需要时再点击加载", "Switch the chat model in sync with the chat window; load it when needed");
        AutomationProperties.SetName(_llmSelector, L.T("当前聊天模型（与对话窗口同步）", "Current chat model (synchronized with chat)"));
        _llmSelector.SelectionChanged += async (_, _) => await SelectDashboardLlmAsync();
        llm.Children.Add(_llmSelector); llm.Children.Add(_llmListStatus);
        llm.Children.Add(_llmState); llm.Children.Add(_llmEndpoint);
        llm.Children.Add(_llmMemory);
        var llmButtons = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        _llmLoad.Margin = new Thickness(0, 0, 8, 0); llmButtons.Children.Add(_llmLoad); llmButtons.Children.Add(_llmUnload);
        llm.Children.Add(llmButtons); llm.Children.Add(_llmModelAction);
        BuildIdleUnloadChoice(llm, _llmIdleUnload, _llmIdleHint, speech: false);
        llm.Children.Add(_llmOthers);
        llm.Children.Add(_llmMemoryHint);
        _llmLoad.ToolTip = L.T("提前加载当前聊天模型，使用下方的自动卸载设置；不发送聊天消息", "Preload the chat model using the auto-unload setting below, without sending a chat message");
        _llmUnload.ToolTip = L.T("仅卸载当前选中的模型，不删除模型文件", "Unload only the selected model without deleting its files");
        _llmLoad.Click += async (_, _) => await ChangeLlmModelLoadAsync(true);
        _llmUnload.Click += async (_, _) => await ChangeLlmModelLoadAsync(false);
        var llmCard = Card(llm); llmCard.Margin = new Thickness(7, 0, 0, 0); Grid.SetColumn(llmCard, 1); cards.Children.Add(llmCard);
        body.Children.Add(cards);
        var gpu = new StackPanel(); gpu.Children.Add(ModelText(L.T("本机显卡", "Local GPUs"), "GpuModelLabel", 16, true));
        gpu.Children.Add(_gpuRows);
        _gpuRows.ToolTip = L.T("语音显存使用 PyTorch 保留量，包含已分配量。远程 Ollama 单独列出。", "Speech uses PyTorch reserved memory, including allocated memory. Remote Ollama is listed separately.");
        var gpuCard = Card(gpu); gpuCard.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(gpuCard);
        BuildOllamaActivityCard(body);
        BuildVoiceProfileEditor(body);
        _modelPage = new ScrollViewer { Name = "ModelPage", Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0) };
        Content = _modelPage;
        _modelRefresh.Click += async (_, _) => await RefreshModelPageAsync();
        _modelTimer.Tick += async (_, _) => await RefreshModelPageAsync();
        foreach (var button in new[] { _modelRefresh, _llmLoad, _llmUnload, _voiceLoad, _voiceUnload })
            AutomationProperties.SetName(button, button.Content?.ToString() ?? button.Name);
    }

    private void RenderModelButtons()
    {
        SyncIdleUnloadChoices();
        bool llmBusy = _llmModelOperation || _modelServices.LlmOperation?.Invoke() != null;
        string? blocked = _modelServices.LlmBlockedReason?.Invoke();
        _llmLoad.IsEnabled = _llmUnload.IsEnabled = !_settings.UseChatApi && !llmBusy && blocked == null && _modelServices.SetLlmLoaded != null;
        _llmLabel.Text = _settings.UseChatApi ? L.T("全局模型 · API", "Global model · API") : L.T("全局模型 · Ollama", "Global model · Ollama");
        _llmMemoryHint.Text = _settings.UseChatApi ? L.T("聊天与主动功能共用此模型，调用可能计费。语音独立。", "Chat and proactive features share this model and may incur charges. Speech is separate.") :
            L.T("卸载会影响共用此模型的其他软件，模型文件会保留。", "Unloading affects other apps sharing this model. Its files are kept.");
        _llmSelector.IsEnabled = !llmBusy && blocked == null && _installedListAvailable &&
            _modelServices.SelectLlmModel != null && _dashboardInstalledModels.Count > 0;
        bool voiceEnabled = PetVoiceProfiles.IsEnabled(_settings.GetVoiceProfile(_settings.SkinId));
        _voiceLoad.IsEnabled = !VoiceStudioOwnsAudio && !_speechModelOperation && _speechHost != null && _settings.SpeechEnabled && voiceEnabled;
        _voiceUnload.IsEnabled = !VoiceStudioOwnsAudio && !_speechModelOperation && _speechHost != null;
        if (blocked != null && !_llmModelOperation) _llmModelAction.Text = blocked;
        else if (_lastLlmBlockReason != null && _llmModelAction.Text == _lastLlmBlockReason) _llmModelAction.Text = "";
        _lastLlmBlockReason = blocked;
        string? voiceBlocked = !_settings.SpeechEnabled ? L.T("语音播报已关闭；在本页上方开启后可加载。", "Pet speech is off. Enable it at the top of this page before loading.") :
            !voiceEnabled ? L.T("当前角色尚未启用语音；可在下方选择并应用生成方式。", "Speech is disabled for this character. Choose and apply a generation method below.") : null;
        if (voiceBlocked != null) _voiceModelAction.Text = voiceBlocked;
        else if (_lastVoiceBlockReason != null && _voiceModelAction.Text == _lastVoiceBlockReason) _voiceModelAction.Text = "";
        _lastVoiceBlockReason = voiceBlocked;
        _voiceCurrentRole.Text = PetVoiceProfiles.CharacterName(_settings.SkinId);
        SyncVoiceProfileEditor();
        RenderVoiceStudio();
    }

    private void BuildIdleUnloadChoice(StackPanel parent, ComboBox selector, TextBlock hint, bool speech)
    {
        parent.Children.Add(ModelText(L.T("空闲后自动卸载", "Unload after inactivity"),
            speech ? "DashboardVoiceIdleLabel" : "DashboardLlmIdleLabel", 11, true));
        ConfigureCombo(selector);
        selector.MinHeight = 34; selector.Margin = new Thickness(0, 0, 0, 8);
        selector.HorizontalAlignment = HorizontalAlignment.Stretch;
        selector.DisplayMemberPath = nameof(ModelIdleChoice.Label);
        selector.SelectedValuePath = nameof(ModelIdleChoice.Minutes);
        var choices = new List<ModelIdleChoice>();
        if (speech) choices.Add(new(0, L.T("聊天期间保持加载（默认）", "Keep loaded while chatting (default)")));
        choices.AddRange(new[] { 1, 5, 10, 30, 60 }.Select(minutes => new ModelIdleChoice(minutes,
            L.T($"空闲 {minutes} 分钟后", $"After {minutes} minute{(minutes == 1 ? "" : "s")} idle"))));
        choices.Add(new(-1, L.T("永不自动卸载", "Never unload automatically")));
        selector.ItemsSource = choices;
        AutomationProperties.SetName(selector, speech
            ? L.T("语音模型自动卸载时间", "Speech model auto-unload time")
            : L.T("聊天模型自动卸载时间", "Chat model auto-unload time"));
        selector.SelectionChanged += (_, _) => ChangeIdleUnloadChoice(speech);
        parent.Children.Add(selector); parent.Children.Add(hint);
    }

    private void SyncIdleUnloadChoices()
    {
        _syncIdleUnload = true;
        try
        {
            _llmIdleUnload.SelectedValue = _settings.OllamaKeepAliveMinutes;
            _voiceIdleUnload.SelectedValue = _settings.SpeechIdleUnloadMinutes;
            _voiceIdleUnload.IsEnabled = _speechHost != null;
            _llmIdleUnload.IsEnabled = !_settings.UseChatApi && _speechHost != null;
            _llmIdleHint.Text = _settings.UseChatApi ? L.T("此项仅用于 Ollama；API 模式不会修改原来的自动卸载时间。", "This setting applies to Ollama only. API mode leaves its auto-unload time unchanged.") : L.T(
                "保存后，从下一次使用模型或手动加载开始生效；空闲时间从该次请求结束计算。仅控制桌宠发给 Ollama 的请求，不修改服务器全局设置；其他软件的请求也可能影响卸载时间。仍可手动卸载。",
                "Saved for the next model request or manual load. Idle time starts after that request finishes. Applies only to AI Pet requests, without changing Ollama's global settings; requests from other apps may also affect unloading. Manual unload remains available.");
            _voiceIdleHint.Text = _settings.SpeechIdleUnloadMinutes switch
            {
                0 => L.T("聊天窗口打开期间保持加载；关闭后按最近一次使用起算，空闲满 1 分钟即可卸载。手动卸载后不会因聊天窗口仍打开而重新加载。",
                    "Keep loaded while chat is open. After closing it, unload once 1 minute has passed since the last use. Leaving chat open does not undo a manual unload."),
                -1 => L.T("桌宠运行期间不因空闲自动卸载；手动卸载、关闭语音或退出桌宠仍会释放模型。选择本项不会主动加载模型。",
                    "No idle unload while AI Pet is running. Manual unload, disabling speech, or exiting still releases the model. Selecting this does not load it."),
                int minutes => L.T($"最后一次合成或预加载结束后，空闲 {minutes} 分钟自动卸载，即使聊天窗口仍打开。下次朗读时按需重新加载；选择时不会主动加载模型。",
                    $"Unload after {minutes} minute{(minutes == 1 ? "" : "s")} idle following synthesis or preloading, even with chat open. Reload on the next readout; choosing a setting does not load it.")
            };
            _llmIdleUnload.ToolTip = _llmIdleHint.Text;
            _voiceIdleUnload.ToolTip = _voiceIdleHint.Text;
            _llmIdleHint.Text = _settings.UseChatApi ? L.T("仅适用于 Ollama。", "Ollama only.") :
                L.T("下一次请求生效；其他软件的请求也会影响卸载时间。", "Applies to the next model request. Other apps may affect unloading.");
            _voiceIdleHint.Text = _settings.SpeechIdleUnloadMinutes switch
            {
                0 => L.T("关闭聊天后，空闲 1 分钟卸载。", "After chat closes, unload after 1 minute idle."),
                -1 => L.T("仍可手动卸载。", "Manual unload remains available."),
                _ => L.T("聊天打开时同样生效。", "Also applies while chat is open.")
            };
        }
        finally { _syncIdleUnload = false; }
    }

    private void ChangeIdleUnloadChoice(bool speech)
    {
        if (_syncIdleUnload || _rendering || _closed || _speechHost == null || !speech && _settings.UseChatApi) return;
        var selector = speech ? _voiceIdleUnload : _llmIdleUnload;
        if (selector.SelectedValue is not int minutes ||
            minutes == (speech ? _settings.SpeechIdleUnloadMinutes : _settings.OllamaKeepAliveMinutes)) return;
        try
        {
            if (speech) _speechHost.SetSpeechIdleUnloadMinutes(minutes);
            else _speechHost.SetOllamaKeepAliveMinutes(minutes);
            (speech ? _voiceModelAction : _llmModelAction).Text = L.T("自动卸载设置已保存。", "Auto-unload setting saved.");
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { (speech ? _voiceModelAction : _llmModelAction).Text = L.T("设置未保存：", "Could not save setting: ") + ex.Message; }
        finally { SyncIdleUnloadChoices(); }
    }

    private void BuildVoiceProfileEditor(StackPanel body)
    {
        var content = new StackPanel();
        content.Children.Add(ModelText(L.T("语音生成方式", "Speech generation method"), "VoiceProfileTitle", 16, true));
        _voiceCharacter.ToolTip = L.T("按角色保存，不切换当前桌宠。", "Saved per character; does not switch the current pet.");
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(right, 1); columns.Children.Add(left); columns.Children.Add(right);
        foreach (var combo in new[] { _voiceCharacter, _voiceModel, _voiceSpeaker, _voiceLanguage })
        {
            ConfigureCombo(combo); combo.HorizontalAlignment = HorizontalAlignment.Stretch; combo.MinHeight = 34;
            combo.Margin = new Thickness(0, 0, 0, 9); combo.DisplayMemberPath = nameof(PetVoiceChoice.Label);
            combo.SelectedValuePath = nameof(PetVoiceChoice.Id);
        }
        _voiceCharacter.ItemsSource = PetVoiceProfiles.Characters;
        _voiceModel.ItemsSource = VoiceGenerationModes;
        _voiceSpeaker.ItemsSource = PetVoiceProfiles.Speakers;
        _voiceLanguage.ItemsSource = new[] { new PetVoiceChoice("auto", L.T("跟随软件语言", "Follow app language")) }
            .Concat(PetVoiceProfiles.Languages.Select(value => new PetVoiceChoice(value, value))).ToArray();
        left.Children.Add(ModelText(L.T("角色", "Character"), "VoiceProfileCharacterLabel", 11, true)); left.Children.Add(_voiceCharacter);
        left.Children.Add(ModelText(L.T("生成方式", "Generation method"), "VoiceProfileModelLabel", 11, true)); left.Children.Add(_voiceModel);
        _voiceSpeakerField = new StackPanel();
        _voiceSpeakerField.Children.Add(ModelText(L.T("预置音色", "Preset speaker"), "VoiceProfileSpeakerLabel", 11, true));
        _voiceSpeakerField.Children.Add(_voiceSpeaker); left.Children.Add(_voiceSpeakerField);
        right.Children.Add(ModelText(L.T("朗读语言", "Speech language"), "VoiceProfileLanguageLabel", 11, true));
        right.Children.Add(_voiceLanguage);
        _voiceInstructionField = new StackPanel();
        _voiceInstructionField.Children.Add(_voiceInstructionLabel);
        _voiceInstruction.Padding = new Thickness(8); _voiceInstruction.FontSize = 12;
        _voiceInstruction.BorderBrush = Ui.Brush("#DDE5D6"); _voiceInstruction.Background = Ui.Brush("#FAFBF7");
        _voiceInstructionField.Children.Add(_voiceInstruction); right.Children.Add(_voiceInstructionField);
        _voiceProfileHint.Margin = new Thickness(0, 9, 0, 7); right.Children.Add(_voiceProfileHint);
        content.Children.Add(columns);
        var save = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        _voiceProfileSave.Margin = new Thickness(0, 0, 12, 0); save.Children.Add(_voiceProfileSave);
        _voiceProfileStatus.VerticalAlignment = VerticalAlignment.Center; _voiceProfileStatus.MaxWidth = 650; save.Children.Add(_voiceProfileStatus);
        content.Children.Add(save);
        var card = Card(content); card.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(card);
        BuildVoiceLibrary(body);
        BuildVoiceStudio(body);
        AutomationProperties.SetName(_voiceCharacter, L.T("编辑声音的桌宠角色", "Character whose voice you are editing"));
        AutomationProperties.SetName(_voiceModel, L.T("此角色的语音生成方式", "Speech generation method for this character"));
        AutomationProperties.SetName(_voiceSpeaker, L.T("预置说话人音色", "Preset speaker voice"));
        AutomationProperties.SetName(_voiceLanguage, L.T("此角色的朗读语言", "Speech language for this character"));
        AutomationProperties.SetName(_voiceInstruction, L.T("音色与语气描述，最多600字", "Voice and delivery description, up to 600 characters"));
        AutomationProperties.SetName(_voiceProfileSave, L.T("应用生成方式", "Apply method"));
        _voiceCharacter.SelectionChanged += (_, _) => { if (!_syncVoiceProfile) LoadVoiceProfileDraft(); };
        _voiceModel.SelectionChanged += (_, _) =>
        {
            if (_syncVoiceProfile) return;
            if (_voiceModel.SelectedValue as string == "voice-design" && string.IsNullOrWhiteSpace(_voiceInstruction.Text))
                _voiceInstruction.Text = PetVoiceProfiles.DefaultInstruction(_voiceCharacter.SelectedValue as string ?? _settings.SkinId);
            VoiceProfileDraftChanged();
        };
        _voiceSpeaker.SelectionChanged += (_, _) => { if (!_syncVoiceProfile) VoiceProfileDraftChanged(); };
        _voiceLanguage.SelectionChanged += (_, _) => { if (!_syncVoiceProfile) { _voiceLanguageDirty = true; VoiceProfileDraftChanged(); } };
        _voiceInstruction.TextChanged += (_, _) => { if (!_syncVoiceProfile) VoiceProfileDraftChanged(); };
        _voiceProfileSave.Click += (_, _) => SaveVoiceProfileDraft();
        _voiceCharacter.SelectedValue = PetVoiceProfiles.KnownSkin(_settings.SkinId) ? _settings.SkinId.ToLowerInvariant() : "deepseek";
        LoadVoiceProfileDraft();
    }

    private void SyncVoiceProfileEditor()
    {
        if (_voiceCharacter.SelectedValue is not string skinId) return;
        if (!_voiceProfileDirty && _voiceProfileLoaded != _settings.GetVoiceProfile(skinId)) LoadVoiceProfileDraft();
        _voiceProfileSave.IsEnabled = _speechHost != null && !_speechModelOperation && !_voiceStudioGenerating && _voiceProfileDirty;
    }

    private void LoadVoiceProfileDraft()
    {
        if (_voiceCharacter.SelectedValue is not string skinId) return;
        _syncVoiceProfile = true;
        try
        {
            var profile = _settings.GetVoiceProfile(skinId); _voiceProfileLoaded = profile;
            _voiceModel.SelectedValue = VoiceGenerationMode(profile); _voiceSpeaker.SelectedValue = profile.Speaker;
            _voiceLanguage.SelectedValue = PetVoiceProfiles.FollowsAppLanguage(profile) ? "auto" : profile.Language;
            _voiceInstruction.Text = profile.Instruct;
            _voiceProfileDirty = false; _voiceLanguageDirty = false;
        }
        finally { _syncVoiceProfile = false; }
        RefreshVoiceLibrary(VoiceLibraryKey(_voiceProfileLoaded!));
        _voiceProfileStatus.Text = L.T("当前使用：", "Currently using: ") + PetVoiceProfiles.CharacterName(skinId) + " · " + CurrentVoiceDescription(_voiceProfileLoaded!);
        RenderVoiceProfileDraft();
        LoadVoiceStudioDraft();
    }

    private void VoiceProfileDraftChanged()
    {
        _voiceProfileDirty = true; _voiceProfileStatus.Text = L.T("尚未保存", "Not saved yet");
        RenderVoiceProfileDraft();
    }

    private static string VoiceGenerationMode(PetVoiceProfile profile) =>
        profile.ModelId is "base" or "gpt-fixed" or "saved-voice" ? "fixed" : profile.ModelId;

    private string CurrentVoiceDescription(PetVoiceProfile profile) => profile.ModelId switch
    {
        "base" => "Let Me Go",
        "gpt-fixed" => L.T("GPT 龙娘固定音色", "GPT Dragon voice"),
        "saved-voice" => _voiceLibraryChoices.FirstOrDefault(x => x.Key == profile.ReferenceId)?.Name ?? L.T("所选音色暂不可用", "The selected voice is unavailable"),
        "custom-voice" => PetVoiceProfiles.Speakers.FirstOrDefault(x => x.Id == profile.Speaker)?.Label ?? profile.Speaker,
        "voice-design" => L.T("按文字即时生成（VoiceDesign）", "Generate from a description (VoiceDesign)"),
        _ => L.T("未启用语音", "Speech disabled")
    };

    private void RenderVoiceProfileDraft()
    {
        string model = _voiceModel.SelectedValue as string ?? "disabled";
        _voiceModel.IsEnabled = _voiceSpeaker.IsEnabled = _voiceInstruction.IsEnabled = _voiceLanguage.IsEnabled = !_voiceStudioGenerating;
        if (_voiceSpeakerField != null) _voiceSpeakerField.Visibility = model == "custom-voice" ? Visibility.Visible : Visibility.Collapsed;
        if (_voiceInstructionField != null) _voiceInstructionField.Visibility = model is "custom-voice" or "voice-design" ? Visibility.Visible : Visibility.Collapsed;
        _voiceInstructionLabel.Text = model == "voice-design" ? L.T("音色描述", "Voice description") : L.T("语气要求（可选）", "Delivery instructions (optional)");
        _voiceInstruction.ToolTip = L.T("最多 600 字。", "Up to 600 characters.");
        string hint = model switch
        {
            "fixed" => L.T("从下方音色库选择声音。", "Choose a voice from the library below."),
            "custom-voice" => "",
            "voice-design" => L.T("音色可能随每次朗读变化；要固定声音，请使用音色工坊。", "The voice may vary between readouts. Use Voice Studio to save a consistent voice."),
            _ => ""
        };
        if (model != "disabled" && !PetVoiceProfiles.IsModelInstalled(model == "fixed" ? "base" : model))
            hint += (hint.Length == 0 ? "" : "\n") + L.T("模型未安装，请先下载。", "Model not installed. Download it before use.");
        _voiceProfileHint.Text = hint;
        _voiceProfileSave.IsEnabled = _speechHost != null && !_speechModelOperation && !_voiceStudioGenerating && _voiceProfileDirty;
    }

    private void SaveVoiceProfileDraft()
    {
        if (_speechHost == null || _speechModelOperation || _voiceStudioGenerating || !_voiceProfileDirty || _voiceCharacter.SelectedValue is not string skinId ||
            _voiceModel.SelectedValue is not string model) return;
        if (model == "voice-design" && string.IsNullOrWhiteSpace(_voiceInstruction.Text))
        { _voiceProfileStatus.Text = L.T("请先填写希望生成的声音描述。", "Enter a description of the voice you want to generate first."); return; }
        try
        {
            string? referenceId = null;
            bool followLanguage = _voiceLanguage.SelectedValue as string == "auto";
            string language = followLanguage ? (L.IsEnglish ? "English" : "Chinese") : _voiceLanguage.SelectedValue as string ?? _voiceProfileLoaded?.Language ?? (L.IsEnglish ? "English" : "Chinese");
            string speaker = _voiceSpeaker.SelectedValue as string ?? "Vivian", instruction = _voiceInstruction.Text;
            if (model == "fixed")
            {
                if (_voiceLibraryRole != skinId || _voiceLibraryList.SelectedItem is not VoiceLibraryChoice selectedVoice)
                {
                    _voiceProfileStatus.Text = L.T("请先在下方音色库选择要使用的声音。", "Select a voice from the library below first.");
                    return;
                }
                model = selectedVoice.ModelId; speaker = "Vivian"; instruction = "";
                if (model == "saved-voice")
                {
                    var selectedReference = (_voiceStudio?.ListSavedVoices(skinId) ?? Array.Empty<PetSavedVoiceInfo>())
                        .FirstOrDefault(x => x.ReferenceId == selectedVoice.Key && string.Equals(x.SkinId, skinId, StringComparison.OrdinalIgnoreCase));
                    if (selectedReference == null)
                    { _voiceProfileStatus.Text = L.T("所选音色已不存在，请从音色库重新选择。", "The selected voice no longer exists. Choose another from the library."); return; }
                    referenceId = selectedReference.ReferenceId;
                    if (!_voiceLanguageDirty && _voiceProfileLoaded?.ReferenceId != referenceId)
                    { language = selectedReference.Language; followLanguage = false; }
                }
                else if (!selectedVoice.IsBuiltIn || model is not ("base" or "gpt-fixed"))
                { _voiceProfileStatus.Text = L.T("请先在下方音色库选择要使用的声音。", "Select a voice from the library below first."); return; }
            }
            var profile = PetVoiceProfiles.Normalize(new(model, speaker, language, instruction, referenceId, FollowAppLanguage: followLanguage), skinId);
            if (skinId.Equals(_settings.SkinId, StringComparison.OrdinalIgnoreCase)) InvalidateModelRead();
            _speechHost.SetCharacterVoiceProfile(skinId, profile);
            _voiceProfileLoaded = profile; _voiceProfileDirty = false; _voiceLanguageDirty = false;
            _syncVoiceProfile = true;
            try { _voiceLanguage.SelectedValue = followLanguage ? "auto" : language; }
            finally { _syncVoiceProfile = false; }
            _voiceProfileStatus.Text = L.T("当前使用：", "Currently using: ") + PetVoiceProfiles.CharacterName(skinId) + " · " + CurrentVoiceDescription(profile) + L.T("；", "; ") +
                (skinId.Equals(_settings.SkinId, StringComparison.OrdinalIgnoreCase) ? L.T("下一次朗读使用新声音。", "The next readout will use the new voice.") : L.T("切换到该角色后使用。", "Used when you switch to that character."));
            RenderVoiceProfileDraft(); RenderSpeechControls(); RenderModelButtons();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _voiceProfileStatus.Text = L.T("保存未完成：", "Could not save: ") + ex.Message; }
    }

    private void VoiceProfileSelectionChanged()
    {
        var profile = _settings.GetVoiceProfile(_settings.SkinId);
        if (_observedVoiceSkin == _settings.SkinId && _observedVoiceProfile == profile) return;
        _observedVoiceSkin = _settings.SkinId; _observedVoiceProfile = profile;
        InvalidateModelRead();
        _voiceName.Text = PetVoiceProfiles.ModelDisplayName(profile.ModelId);
        _voiceState.Text = L.T("正在查询…", "Reading status…"); _voiceMemory.Text = "";
        if (_showModelPage) _ = RefreshModelPageAsync();
    }

    internal void ModelSelectionChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ModelSelectionChanged)); return; }
        InvalidateModelRead(); _modelSnapshotKey = null; ResetOllamaActivity();
        SyncChatConnectionEditor();
        if (_installedModelsEndpoint != ActiveModelScope)
        {
            _dashboardInstalledModels = Array.Empty<string>(); _installedListAvailable = false;
            _llmListStatus.Text = L.T("连接已更换，正在读取已安装模型…", "Connection changed. Reading installed models…");
        }
        if (_settings.UseChatApi) RenderApiModels();
        SyncModelSelector(); RenderModelButtons();
        _llmState.Text = L.T("正在查询…", "Reading status…"); _llmMemory.Text = ""; _llmOthers.Text = "";
        if (_showModelPage) _ = RefreshModelPageAsync();
    }

    private void RenderInstalledModels(IReadOnlyList<string>? models, string? error, string endpoint)
    {
        if (_modelServices.ReadInstalledModels == null)
        {
            _installedListAvailable = false; _llmListStatus.Text = L.T("请从桌宠打开设置选择模型。", "Open Settings from the pet to choose a model.");
        }
        else if (error != null || models == null)
        {
            _installedListAvailable = false; _llmListStatus.Text = L.T("模型列表暂不可用：", "Model list unavailable: ") + error;
        }
        else
        {
            _installedListAvailable = true; _installedModelsEndpoint = endpoint;
            _dashboardInstalledModels = models;
            _llmListStatus.Text = models.Count == 0 ? L.T("此服务器尚无已安装模型。", "No models are installed on this server.") :
                models.Contains(_settings.OllamaModel, StringComparer.Ordinal) ? "" : L.T("当前选择未安装，请选择其他模型。", "The current model is not installed. Select another model.");
        }
        if (!_selectingLlm) SyncModelSelector();
    }

    // The shared chat host supplies its last metadata result. Polling the dashboard
    // does not make API requests or read the credential; manual list refresh lives
    // in the connection editor.
    private void RenderApiModels()
    {
        IReadOnlyList<string> cached;
        try { cached = _modelServices.ReadChatApiModels?.Invoke() ?? Array.Empty<string>(); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { cached = Array.Empty<string>(); }
        _dashboardInstalledModels = cached.Append(_settings.ApiProfile.Model).Where(ChatApiModel.IsValid)
            .Distinct(StringComparer.Ordinal).ToArray();
        _installedListAvailable = _dashboardInstalledModels.Count > 0;
        _installedModelsEndpoint = ActiveModelScope;
        _llmListStatus.Text = "";
        if (!_selectingLlm) SyncModelSelector();
    }

    private void SyncModelSelector()
    {
        string selected = ActiveChatModel;
        var choices = _dashboardInstalledModels.Select(id => new OllamaModelChoice(id,
            OllamaModels.Supported.FirstOrDefault(m => m.Id == id)?.DisplayName ?? id)).ToList();
        if (!choices.Any(x => x.Id == selected)) choices.Insert(0, new(selected, selected + (_installedListAvailable && !_settings.UseChatApi ? L.T("（未安装）", " (not installed)") : L.T("（当前选择）", " (current selection)"))));
        _syncModelSelector = true;
        try
        {
            // Keep an open dropdown stable during the five-second read cycle.
            var existing = (_llmSelector.ItemsSource as IEnumerable<OllamaModelChoice>)?.ToArray();
            if (existing == null || !existing.SequenceEqual(choices)) _llmSelector.ItemsSource = choices;
            _llmSelector.SelectedValue = selected;
        }
        finally { _syncModelSelector = false; }
    }

    private async Task SelectDashboardLlmAsync()
    {
        if (_syncModelSelector || _closed || _llmSelector.SelectedValue is not string selected || selected == ActiveChatModel) return;
        if (_llmModelOperation || !_installedListAvailable || !_dashboardInstalledModels.Contains(selected, StringComparer.Ordinal) ||
            _modelServices.SelectLlmModel == null || _modelServices.LlmBlockedReason?.Invoke() != null)
        { SyncModelSelector(); return; }
        InvalidateModelRead(); _llmModelOperation = true; _selectingLlm = true;
        _llmState.Text = L.T("正在切换…", "Switching…"); _llmModelAction.Text = L.T("正在切换聊天模型…", "Switching chat model…"); RenderModelButtons();
        try { _llmModelAction.Text = await _modelServices.SelectLlmModel(selected); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _llmModelAction.Text = L.T("模型切换未完成：", "Could not switch models: ") + ex.Message; }
        finally
        {
            _llmModelOperation = false; _selectingLlm = false;
            if (!_closed) { SyncModelSelector(); RenderModelButtons(); await RefreshModelPageAsync(); }
        }
    }

    private async Task ChangeLlmModelLoadAsync(bool load)
    {
        if (_settings.UseChatApi || _llmModelOperation || _modelServices.SetLlmLoaded == null) return;
        InvalidateModelRead();
        _llmModelOperation = true;
        _llmModelAction.Text = load ? L.T("正在加载聊天模型…", "Loading chat model…") : L.T("正在卸载聊天模型…", "Unloading chat model…");
        _llmState.Text = load ? L.T("加载中", "Loading") : L.T("卸载中", "Unloading"); RenderModelButtons();
        try { _llmModelAction.Text = await _modelServices.SetLlmLoaded(load); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _llmModelAction.Text = L.T("模型操作未完成：", "Model operation failed: ") + ex.Message; }
        finally
        {
            _llmModelOperation = false;
            if (!_closed) { RenderModelButtons(); await RefreshModelPageAsync(); }
        }
    }

    // No overlapping polls, no delayed results after changing page/endpoint/model.
    private async Task RefreshModelPageAsync()
    {
        if (_closed || !_showModelPage || !IsLoaded || !IsVisible || _hostWindow?.WindowState == WindowState.Minimized) return;
        if (_modelRefreshing) { _modelRefreshPending = true; return; }
        _modelRefreshPending = false;
        _modelRefreshing = true; _modelRefresh.IsEnabled = false;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(_modelPageLifetime.Token);
        read.CancelAfter(TimeSpan.FromSeconds(6)); _modelReadCancellation = read;
        long epoch = ++_modelReadEpoch;
        string endpoint = _settings.OllamaAddress, model = ActiveChatModel, modelScope = ActiveModelScope;
        bool useApi = _settings.UseChatApi;
        string snapshotKey = endpoint + "\n" + modelScope + "\n" + model;
        if (_modelSnapshotKey != snapshotKey)
        {
            _modelSnapshotKey = snapshotKey;
            ResetOllamaActivity();
            _llmState.Text = L.T("正在查询…", "Reading status…"); _llmMemory.Text = ""; _llmOthers.Text = "";
        }
        if (!_selectingLlm && !string.Equals(_llmSelector.SelectedValue as string, model, StringComparison.Ordinal)) SyncModelSelector();
        if (_installedModelsEndpoint != modelScope) _installedListAvailable = false;
        bool remote = true;
        try { remote = !OllamaEndpoint.Parse(endpoint).IsLoopback; } catch (ArgumentException) { }
        _llmEndpoint.Text = useApi ? L.T("API 服务 · ", "API service · ") + _settings.ApiProfile.BaseUrl :
            (remote ? L.T("服务器 · ", "Server · ") : L.T("本机服务 · ", "Local service · ")) + endpoint;
        SyncChatConnectionEditor();
        RenderModelButtons();
        try
        {
            var llmTask = ReadSafely(() => _modelServices.ReadOllama(endpoint, read.Token), read.Token);
            var voiceTask = ReadSafely(() => _modelServices.ReadVoice(read.Token), read.Token);
            var gpuTask = ReadSafely(() => _modelServices.ReadGpu(read.Token), read.Token);
            var activityTask = ReadSafely(() => remote
                ? Task.FromResult(new OllamaGpuActivity(false, L.T("远程 Ollama 无法读取服务器进程的 GPU 活动。", "GPU activity of a remote Ollama server cannot be read locally."), null, Array.Empty<OllamaGpuProcess>(), DateTimeOffset.UtcNow))
                : _modelServices.ReadOllamaActivity?.Invoke(endpoint, read.Token) ??
                    Task.FromResult(new OllamaGpuActivity(false, L.T("当前未提供进程 GPU 监测。", "Process GPU monitoring is not available."), null, Array.Empty<OllamaGpuProcess>(), DateTimeOffset.UtcNow)), read.Token);
            var installedTask = ReadSafely(() => !useApi ? _modelServices.ReadInstalledModels?.Invoke(endpoint, read.Token) ??
                Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()) :
                Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()), read.Token);
            await Task.WhenAll(llmTask, voiceTask, gpuTask, installedTask, activityTask);
            if (_closed || !_showModelPage || epoch != _modelReadEpoch) return;
            if (endpoint != _settings.OllamaAddress || model != ActiveChatModel || modelScope != ActiveModelScope || useApi != _settings.UseChatApi)
            { _modelRefreshPending = true; return; }
            if (useApi) RenderApiRuntime(llmTask.Result.Value, llmTask.Result.Error);
            else RenderLlmRuntime(llmTask.Result.Value, llmTask.Result.Error, model);
            RenderOllamaActivity(endpoint, llmTask.Result.Value, llmTask.Result.Error,
                activityTask.Result.Value, activityTask.Result.Error, _modelServices.ReadOwnActivity?.Invoke(endpoint));
            RenderVoiceRuntime(voiceTask.Result.Value, voiceTask.Result.Error);
            if (useApi) RenderApiModels();
            else RenderInstalledModels(installedTask.Result.Value, installedTask.Result.Error, endpoint);
            _gpuRows.Children.Clear();
            _gpuRows.Children.Add(ModelVramBreakdown.Build(gpuTask.Result.Value, gpuTask.Result.Error,
                llmTask.Result.Value, llmTask.Result.Error, voiceTask.Result.Value, voiceTask.Result.Error, !remote));
            _modelUpdated.Text = L.T("最近读取 ", "Last read ") + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            _modelUpdated.ToolTip = L.T("每 5 秒刷新；查看状态不会加载模型。", "Refreshes every 5 seconds without loading models.");
        }
        finally
        {
            if (ReferenceEquals(_modelReadCancellation, read)) _modelReadCancellation = null;
            _modelRefreshing = false;
            if (!_closed) { _modelRefresh.IsEnabled = true; RenderModelButtons(); }
            if (_modelRefreshPending && !_closed) _ = RefreshModelPageAsync();
        }
    }

    private static async Task<(T? Value, string? Error)> ReadSafely<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try { return (await read().WaitAsync(ct), null); }
        catch (OperationCanceledException) { return (default, L.T("查询超时或已取消", "Status request timed out or was canceled")); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { return (default, ex.Message); }
    }

    private void RenderLlmRuntime(IReadOnlyList<OllamaRunningModel>? models, string? error, string selected)
    {
        if (error != null || models == null)
        {
            _llmState.Text = L.T("状态不可用", "Status unavailable"); _llmMemory.Text = L.T("无法读取 Ollama 模型状态：", "Could not read Ollama model status: ") + error;
            _llmOthers.Text = L.T("显存占用未提供", "GPU memory usage not provided"); return;
        }
        var current = models.FirstOrDefault(m => m.Name.Equals(selected, StringComparison.Ordinal));
        _llmState.Text = _modelServices.LlmOperation?.Invoke() ?? (_llmModelOperation ? L.T("正在操作模型…", "Model operation in progress…") : current == null ? L.T("未加载", "Not loaded") : L.T("已加载", "Loaded"));
        _llmMemory.Text = current == null ? "" :
            L.T("显存 · ", "GPU memory · ") + MemorySize(current.VramBytes) + L.T("    总驻留 · ", "    Total resident · ") + MemorySize(current.SizeBytes) +
            L.T("\n上下文 · ", "\nContext · ") + (current.ContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? L.T("未提供", "Not provided")) +
            L.T("    保留至 · ", "    Keep until · ") + (current.ExpiresAt is { } at ? (at.Year > 2100 ? L.T("持续保留", "Indefinitely") : at.ToLocalTime().ToString("HH:mm:ss")) : L.T("未提供", "Not provided"));
        var others = models.Where(m => !m.Name.Equals(selected, StringComparison.Ordinal)).ToArray();
        _llmOthers.Text = others.Length == 0 ? "" :
            L.T("其他已加载模型：\n", "Other loaded models:\n") + string.Join("\n", others.Take(8).Select(m => m.Name + L.T(" · 显存 ", " · GPU memory ") + MemorySize(m.VramBytes))) +
            (others.Length > 8 ? L.T("\n另有 ", "\nAnd ") + (others.Length - 8) + L.T(" 个模型", " more models") : "");
    }

    private void RenderApiRuntime(IReadOnlyList<OllamaRunningModel>? ollamaModels, string? ollamaError)
    {
        _llmState.Text = _modelServices.LlmOperation?.Invoke() ?? L.T("API 模式 · 按需请求", "API mode · Requests on demand");
        bool localApi = false;
        try { localApi = _settings.ApiProfile.Endpoint.IsLoopback; } catch (ArgumentException) { }
        _llmMemory.Text = localApi
            ? L.T("本机兼容 API：此协议不提供模型显存或驻留状态。", "Local compatible API: this protocol does not report model GPU memory or residency.")
            : L.T("远程模型不占本机模型显存；服务端的显存与加载状态不在此显示。", "The remote model does not occupy local model GPU memory. Server memory and residency are not reported here.");
        _llmOthers.Text = ollamaError != null ? L.T("原 Ollama 服务状态暂不可用；不影响 API 聊天。", "The previous Ollama service status is unavailable; API chat is independent.") :
            ollamaModels == null || ollamaModels.Count == 0 ? L.T("原 Ollama 服务暂无已加载模型。", "The previous Ollama service reports no loaded models.") :
            L.T("Ollama 仍驻留的模型（独立于 API 聊天）：\n", "Models still loaded in Ollama (separate from API chat):\n") +
                string.Join("\n", ollamaModels.Take(8).Select(item => item.Name + L.T(" · 显存 ", " · GPU memory ") + MemorySize(item.VramBytes)));
    }

    private void RenderVoiceRuntime(PetVoiceHealth? health, string? error)
    {
        if (error != null)
        {
            _voiceState.Text = L.T("状态不可用", "Status unavailable"); _voiceBackend.Text = error; _voiceMemory.Text = L.T("显存占用未提供", "GPU memory usage not provided"); return;
        }
        _voiceName.Text = string.IsNullOrWhiteSpace(health?.Model) ? PetVoiceProfiles.ModelDisplayName(_settings.GetVoiceProfile(_settings.SkinId).ModelId) : health.Model;
        _voiceState.Text = _speechModelOperation ? L.T("正在操作模型…", "Model operation in progress…") : health == null ? L.T("未加载 · 语音服务未启动", "Not loaded · Speech service is not running") :
            health.Retiring ? L.T("正在释放显存", "Releasing GPU memory") : health.Status switch
            {
                "loading" => L.T("加载中", "Loading"), "unloading" => L.T("卸载中", "Unloading"), "error" => L.T("引擎异常", "Engine error"),
                _ => health.Loaded ? L.T("已加载", "Loaded") : L.T("未加载", "Not loaded")
            };
        _voiceBackend.Text = health == null ? "" :
            L.T("后端 · ", "Backend · ") + health.Backend + L.T("    设备 · ", "    Device · ") + (health.Device ?? L.T("未提供", "Not provided")) +
            (health.Retained ? L.T("\n聊天期间保持加载", "\nKept loaded while chatting") : "");
        _voiceMemory.Text = health == null ? "" :
            L.T("已分配 · ", "Allocated · ") + MemorySize(health.AllocatedBytes) + L.T("    已保留 · ", "    Reserved · ") + MemorySize(health.ReservedBytes);
    }

    internal static string MemorySize(long? bytes) => bytes is not { } value ? L.T("未提供", "Not provided") :
        value >= 1024L * 1024 * 1024 ? (value / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.CurrentCulture) + " GiB" :
        (value / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MiB";

    private void PauseModelReads()
    {
        _modelTimer.Stop(); _modelRefreshPending = false; InvalidateModelRead(); ResetOllamaActivity();
    }

    private void InvalidateModelRead() { _modelReadEpoch++; _modelReadCancellation?.Cancel(); }

    private void CloseModelPage()
    {
        PauseModelReads(); _chatApiConnection?.Dispose(); _modelPageLifetime.Cancel(); _modelPageLifetime.Dispose();
    }
}
