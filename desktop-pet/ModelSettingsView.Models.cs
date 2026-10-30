using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    Func<string, Task<string>>? SelectLlmModel = null)
{
    internal static DashboardModelServices Create(PetWindow? pet,
        Func<bool, Task<string>>? setLlmLoaded = null, Func<string?>? operation = null,
        Func<string?>? blockedReason = null, Func<string, Task<string>>? selectLlmModel = null) => new(
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
        }, selectLlmModel);
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

    private static TextBlock ModelText(string text, string name, double size = 11, bool bold = false)
    {
        var view = Ui.Text(text, size, bold ? "#294B36" : "#71826D", bold);
        view.Name = name; view.TextWrapping = TextWrapping.Wrap;
        view.Margin = new Thickness(0, 0, 0, 8);
        return view;
    }

    private void BuildModelPage()
    {
        var body = new StackPanel();
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_modelRefresh, Dock.Right); heading.Children.Add(_modelRefresh);
        var caption = new StackPanel();
        caption.Children.Add(ModelText(L.T("模型与显存", "Models & GPU memory"), "ModelPageTitle", 23, true));
        caption.Children.Add(_modelUpdated); heading.Children.Add(caption); body.Children.Add(heading);
        body.Children.Add(BuildSpeechPreferences());
        var cards = new Grid();
        cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
        var voice = new StackPanel();
        voice.Children.Add(ModelText(L.T("当前角色 / 语音", "CURRENT CHARACTER / SPEECH"), "VoiceModelLabel", 11, true));
        voice.Children.Add(_voiceCurrentRole);
        voice.Children.Add(_voiceName); voice.Children.Add(_voiceState); voice.Children.Add(_voiceBackend);
        voice.Children.Add(_voiceMemory);
        voice.Children.Add(ModelText(L.T("显存按语音进程的 PyTorch 统计；“保留”包含可复用的缓存。", "Speech GPU memory is reported by PyTorch. Reserved memory includes reusable cache."), "VoiceMemoryHint"));
        var voiceButtons = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        _voiceLoad.Margin = new Thickness(0, 0, 8, 0); voiceButtons.Children.Add(_voiceLoad); voiceButtons.Children.Add(_voiceUnload);
        voice.Children.Add(voiceButtons); voice.Children.Add(_voiceModelAction);
        _voiceLoad.ToolTip = L.T("加载当前桌宠角色保存的声音模型；需要开启语音播报", "Load the current pet's speech model; Pet speech must be enabled");
        _voiceUnload.ToolTip = L.T("停止播报并释放语音显存；下次主动播报时可以重新加载", "Stop speech and release GPU memory; the next speech request can reload it");
        _voiceLoad.Click += async (_, _) => await ChangeSpeechModelLoadAsync(true);
        _voiceUnload.Click += async (_, _) => await ChangeSpeechModelLoadAsync(false);
        var voiceCard = Card(voice); voiceCard.Margin = new Thickness(0, 0, 7, 0); cards.Children.Add(voiceCard);
        var llm = new StackPanel();
        llm.Children.Add(ModelText(L.T("OLLAMA / 聊天", "OLLAMA / CHAT"), "LlmModelLabel", 11, true));
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
        llm.Children.Add(llmButtons); llm.Children.Add(_llmModelAction); llm.Children.Add(_llmOthers);
        llm.Children.Add(ModelText(L.T("操作聊天中当前选中的模型。卸载不会删除文件；其他软件共用此模型时也会受影响。", "Controls the selected chat model. Unloading keeps its files, but also affects other apps using the same model."), "LlmMemoryHint"));
        _llmLoad.ToolTip = L.T("提前加载当前聊天模型；沿用 Ollama 原有保留时长，不发送消息", "Preload the chat model without sending a message; preserve Ollama's retention setting");
        _llmUnload.ToolTip = L.T("仅卸载当前选中的模型，不删除模型文件", "Unload only the selected model without deleting its files");
        _llmLoad.Click += async (_, _) => await ChangeLlmModelLoadAsync(true);
        _llmUnload.Click += async (_, _) => await ChangeLlmModelLoadAsync(false);
        var llmCard = Card(llm); llmCard.Margin = new Thickness(7, 0, 0, 0); Grid.SetColumn(llmCard, 1); cards.Children.Add(llmCard);
        body.Children.Add(cards);
        BuildVoiceProfileEditor(body);
        var gpu = new StackPanel(); gpu.Children.Add(ModelText(L.T("本机显卡", "Local GPUs"), "GpuModelLabel", 16, true));
        gpu.Children.Add(_gpuRows);
        gpu.Children.Add(ModelText(L.T("模型颜色保持固定。语音使用 PyTorch 保留量（包含已分配量）；远程 Ollama 单独列出。", "Model colors remain consistent. Speech uses PyTorch reserved memory (including allocated memory). Remote Ollama is listed separately."), "GpuMemoryHint"));
        var gpuCard = Card(gpu); gpuCard.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(gpuCard);
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
        bool llmBusy = _llmModelOperation || _modelServices.LlmOperation?.Invoke() != null;
        string? blocked = _modelServices.LlmBlockedReason?.Invoke();
        _llmLoad.IsEnabled = _llmUnload.IsEnabled = !llmBusy && blocked == null && _modelServices.SetLlmLoaded != null;
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
        _voiceCurrentRole.Text = L.T("当前桌宠：", "Current pet: ") + PetVoiceProfiles.CharacterName(_settings.SkinId);
        SyncVoiceProfileEditor();
        RenderVoiceStudio();
    }

    private void BuildVoiceProfileEditor(StackPanel body)
    {
        var content = new StackPanel();
        content.Children.Add(ModelText(L.T("语音生成方式", "Speech generation method"), "VoiceProfileTitle", 16, true));
        content.Children.Add(ModelText(L.T("先选择角色和生成方式。使用已保存音色时，在下方音色库选择具体声音；配置按角色独立保存。", "Choose a character and generation method. For saved voices, select a voice in the library below. Settings are saved separately for each character."), "VoiceProfileScopeHint"));
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
        left.Children.Add(ModelText(L.T("编辑角色", "Edit character"), "VoiceProfileCharacterLabel", 11, true)); left.Children.Add(_voiceCharacter);
        left.Children.Add(ModelText(L.T("生成方式", "Generation method"), "VoiceProfileModelLabel", 11, true)); left.Children.Add(_voiceModel);
        _voiceSpeakerField = new StackPanel();
        _voiceSpeakerField.Children.Add(ModelText(L.T("预置音色", "Preset speaker"), "VoiceProfileSpeakerLabel", 11, true));
        _voiceSpeakerField.Children.Add(_voiceSpeaker); left.Children.Add(_voiceSpeakerField);
        left.Children.Add(ModelText(L.T("朗读语言", "Speech language"), "VoiceProfileLanguageLabel", 11, true));
        left.Children.Add(_voiceLanguage);
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
        _voiceInstructionLabel.Text = model == "voice-design" ? L.T("音色描述（必填，最多 600 字）", "Voice description (required, up to 600 characters)") : L.T("语气要求（可选，最多 600 字）", "Delivery instructions (optional, up to 600 characters)");
        string hint = model switch
        {
            "fixed" => L.T("用 Base 复用音色库中的声音，保持固定音色。请在下方选择具体音色；要制作新声音，可使用独立的音色工坊。", "Base reuses a library voice to keep its identity consistent. Select one below, or create a new voice in Voice Studio."),
            "custom-voice" => L.T("使用 CustomVoice 的预置说话人，可附加语气要求；无需参考音频。", "Use a CustomVoice preset speaker with optional delivery instructions. No reference audio is needed."),
            "voice-design" => L.T("每次朗读都由 VoiceDesign 按文字描述即时生成，音色可能变化。想固定声音，请使用下方音色工坊生成并保存。", "VoiceDesign generates speech from the description each time, so the voice may vary. To save a consistent voice, use Voice Studio below."),
            _ => L.T("此角色不朗读文字；其他角色的语音配置保持独立。", "This character does not read text aloud. Other characters keep their own speech settings.")
        };
        if (model != "disabled") hint += PetVoiceProfiles.IsModelInstalled(model == "fixed" ? "base" : model) ? L.T("\n模型文件已安装；应用不会自动加载。", "\nModel files are installed. Applying settings does not load the model.") : L.T("\n未检测到本地模型文件，使用前需完成下载。", "\nLocal model files were not found. Download them before use.");
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
        InvalidateModelRead(); _modelSnapshotKey = null;
        if (_installedModelsEndpoint != _settings.OllamaAddress)
        {
            _dashboardInstalledModels = Array.Empty<string>(); _installedListAvailable = false;
            _llmListStatus.Text = L.T("连接已更换，正在读取已安装模型…", "Connection changed. Reading installed models…");
        }
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
                models.Contains(_settings.OllamaModel, StringComparer.Ordinal) ? L.T("与聊天窗口同步 · ", "Synchronized with chat · ") + models.Count + L.T(" 个已安装模型", " installed models") : L.T("当前选择未安装，请选择其他模型。", "The current model is not installed. Select another model.");
        }
        if (!_selectingLlm) SyncModelSelector();
    }

    private void SyncModelSelector()
    {
        string selected = _settings.OllamaModel;
        var choices = _dashboardInstalledModels.Select(id => new OllamaModelChoice(id,
            OllamaModels.Supported.FirstOrDefault(m => m.Id == id)?.DisplayName ?? id)).ToList();
        if (!choices.Any(x => x.Id == selected)) choices.Insert(0, new(selected, selected + (_installedListAvailable ? L.T("（未安装）", " (not installed)") : L.T("（当前选择）", " (current selection)"))));
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
        if (_syncModelSelector || _closed || _llmSelector.SelectedValue is not string selected || selected == _settings.OllamaModel) return;
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
        if (_llmModelOperation || _modelServices.SetLlmLoaded == null) return;
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
        string endpoint = _settings.OllamaAddress, model = _settings.OllamaModel;
        string snapshotKey = endpoint + "\n" + model;
        if (_modelSnapshotKey != snapshotKey)
        {
            _modelSnapshotKey = snapshotKey;
            _llmState.Text = L.T("正在查询…", "Reading status…"); _llmMemory.Text = ""; _llmOthers.Text = "";
        }
        if (!_selectingLlm && !string.Equals(_llmSelector.SelectedValue as string, model, StringComparison.Ordinal)) SyncModelSelector();
        if (_installedModelsEndpoint != endpoint) _installedListAvailable = false;
        bool remote = true;
        try { remote = !OllamaEndpoint.Parse(endpoint).IsLoopback; } catch (ArgumentException) { }
        _llmEndpoint.Text = (remote ? L.T("服务器 · ", "Server · ") : L.T("本机服务 · ", "Local service · ")) + endpoint;
        RenderModelButtons();
        try
        {
            var llmTask = ReadSafely(() => _modelServices.ReadOllama(endpoint, read.Token), read.Token);
            var voiceTask = ReadSafely(() => _modelServices.ReadVoice(read.Token), read.Token);
            var gpuTask = ReadSafely(() => _modelServices.ReadGpu(read.Token), read.Token);
            var installedTask = ReadSafely(() => _modelServices.ReadInstalledModels?.Invoke(endpoint, read.Token) ??
                Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()), read.Token);
            await Task.WhenAll(llmTask, voiceTask, gpuTask, installedTask);
            if (_closed || !_showModelPage || epoch != _modelReadEpoch) return;
            if (endpoint != _settings.OllamaAddress || model != _settings.OllamaModel) { _modelRefreshPending = true; return; }
            RenderLlmRuntime(llmTask.Result.Value, llmTask.Result.Error, model);
            RenderVoiceRuntime(voiceTask.Result.Value, voiceTask.Result.Error);
            RenderInstalledModels(installedTask.Result.Value, installedTask.Result.Error, endpoint);
            _gpuRows.Children.Clear();
            _gpuRows.Children.Add(ModelVramBreakdown.Build(gpuTask.Result.Value, gpuTask.Result.Error,
                llmTask.Result.Value, llmTask.Result.Error, voiceTask.Result.Value, voiceTask.Result.Error, !remote));
            _modelUpdated.Text = L.T("每 5 秒刷新 · 最近读取 ", "Refreshes every 5 seconds · Last read ") + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + L.T(" · 查看状态不会加载模型", " · Viewing status does not load models");
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
        _llmMemory.Text = current == null ? L.T("所选模型当前未驻留在 Ollama 中。", "The selected model is not currently loaded in Ollama.") :
            L.T("显存 · ", "GPU memory · ") + MemorySize(current.VramBytes) + L.T("    总驻留 · ", "    Total resident · ") + MemorySize(current.SizeBytes) +
            L.T("\n上下文 · ", "\nContext · ") + (current.ContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? L.T("未提供", "Not provided")) +
            L.T("    保留至 · ", "    Keep until · ") + (current.ExpiresAt is { } at ? (at.Year > 2100 ? L.T("持续保留", "Indefinitely") : at.ToLocalTime().ToString("HH:mm:ss")) : L.T("未提供", "Not provided"));
        var others = models.Where(m => !m.Name.Equals(selected, StringComparison.Ordinal)).ToArray();
        _llmOthers.Text = others.Length == 0 ? L.T("没有其他已加载的 Ollama 模型。", "No other Ollama models are loaded.") :
            L.T("其他已加载模型：\n", "Other loaded models:\n") + string.Join("\n", others.Take(8).Select(m => m.Name + L.T(" · 显存 ", " · GPU memory ") + MemorySize(m.VramBytes))) +
            (others.Length > 8 ? L.T("\n另有 ", "\nAnd ") + (others.Length - 8) + L.T(" 个模型", " more models") : "");
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
        _voiceBackend.Text = health == null ? (_speechHost?.SpeechStatusText ?? L.T("语音服务未启动", "Speech service is not running")) :
            L.T("后端 · ", "Backend · ") + health.Backend + L.T("    设备 · ", "    Device · ") + (health.Device ?? L.T("未提供", "Not provided")) +
            (health.Retained ? L.T("\n聊天期间保持加载", "\nKept loaded while chatting") : L.T("\n按原有空闲策略释放", "\nUnloaded according to the existing idle policy"));
        _voiceMemory.Text = health == null ? L.T("语音工作进程未运行，无模型显存驻留。", "The speech worker is not running; no speech model occupies GPU memory.") :
            L.T("已分配 · ", "Allocated · ") + MemorySize(health.AllocatedBytes) + L.T("    已保留 · ", "    Reserved · ") + MemorySize(health.ReservedBytes);
    }

    internal static string MemorySize(long? bytes) => bytes is not { } value ? L.T("未提供", "Not provided") :
        value >= 1024L * 1024 * 1024 ? (value / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.CurrentCulture) + " GiB" :
        (value / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MiB";

    private void PauseModelReads()
    {
        _modelTimer.Stop(); _modelRefreshPending = false; InvalidateModelRead();
    }

    private void InvalidateModelRead() { _modelReadEpoch++; _modelReadCancellation?.Cancel(); }

    private void CloseModelPage()
    {
        PauseModelReads(); _modelPageLifetime.Cancel(); _modelPageLifetime.Dispose();
    }
}
