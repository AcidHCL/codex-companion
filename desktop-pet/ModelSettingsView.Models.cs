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
    private readonly Button _modelRefresh = new() { Name = "ModelRefresh", Content = "刷新状态" };
    private readonly Button _llmLoad = new() { Name = "DashboardLlmLoad", Content = "加载 LLM" };
    private readonly Button _llmUnload = new() { Name = "DashboardLlmUnload", Content = "卸载 LLM" };
    private readonly Button _voiceLoad = new() { Name = "DashboardVoiceLoad", Content = "加载语音" };
    private readonly Button _voiceUnload = new() { Name = "DashboardVoiceUnload", Content = "卸载语音" };
    private readonly TextBlock _modelUpdated = ModelText("打开此页后读取状态", "ModelUpdated");
    private readonly ComboBox _llmSelector = new() { Name = "DashboardLlmSelector" };
    private readonly TextBlock _llmListStatus = ModelText("正在读取已安装模型…", "DashboardLlmListStatus");
    private readonly TextBlock _llmEndpoint = ModelText("", "DashboardLlmEndpoint");
    private readonly TextBlock _llmState = ModelText("等待查询", "DashboardLlmState", 14, true);
    private readonly TextBlock _llmMemory = ModelText("", "DashboardLlmMemory");
    private readonly TextBlock _llmOthers = ModelText("", "DashboardLlmOthers");
    private readonly TextBlock _llmModelAction = ModelText("", "DashboardLlmAction");
    private readonly TextBlock _voiceState = ModelText("等待查询", "DashboardVoiceState", 14, true);
    private readonly TextBlock _voiceName = ModelText("Qwen3-TTS · 1.7B", "DashboardVoiceName", 16, true);
    private readonly TextBlock _voiceBackend = ModelText("", "DashboardVoiceBackend");
    private readonly TextBlock _voiceMemory = ModelText("", "DashboardVoiceMemory");
    private readonly TextBlock _voiceModelAction = ModelText("", "DashboardVoiceAction");
    private readonly ComboBox _voiceCharacter = new() { Name = "DashboardVoiceCharacter" };
    private readonly ComboBox _voiceModel = new() { Name = "DashboardVoiceModel" };
    private readonly ComboBox _voiceSpeaker = new() { Name = "DashboardVoiceSpeaker" };
    private readonly TextBox _voiceInstruction = new() { Name = "DashboardVoiceInstruction", MaxLength = 600, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 76, MaxHeight = 110 };
    private readonly Button _voiceProfileSave = new() { Name = "DashboardVoiceProfileSave", Content = "应用生成方式" };
    private readonly TextBlock _voiceProfileStatus = ModelText("", "DashboardVoiceProfileStatus");
    private readonly TextBlock _voiceProfileHint = ModelText("", "DashboardVoiceProfileHint");
    private readonly TextBlock _voiceCurrentRole = ModelText("", "DashboardVoiceCurrentRole", 11, true);
    private readonly TextBlock _voiceInstructionLabel = ModelText("音色与语气描述", "DashboardVoiceInstructionLabel", 11, true);
    private StackPanel? _voiceSpeakerField, _voiceInstructionField;
    private bool _syncVoiceProfile, _voiceProfileDirty;
    private PetVoiceProfile? _voiceProfileLoaded;
    private PetVoiceProfile? _observedVoiceProfile;
    private string? _observedVoiceSkin;
    // These choices describe how speech is produced; persisted model IDs remain
    // unchanged so existing role profiles and saved references stay compatible.
    private static readonly PetVoiceChoice[] VoiceGenerationModes = {
        new("disabled", "不启用此角色语音"), new("fixed", "使用已保存音色（Base）"),
        new("custom-voice", "使用预置说话人（CustomVoice）"), new("voice-design", "按文字即时生成（VoiceDesign）")
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
        caption.Children.Add(ModelText("模型与显存", "ModelPageTitle", 23, true));
        caption.Children.Add(_modelUpdated); heading.Children.Add(caption); body.Children.Add(heading);
        body.Children.Add(BuildSpeechPreferences());
        var cards = new Grid();
        cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
        var voice = new StackPanel();
        voice.Children.Add(ModelText("当前角色 / 语音", "VoiceModelLabel", 11, true));
        voice.Children.Add(_voiceCurrentRole);
        voice.Children.Add(_voiceName); voice.Children.Add(_voiceState); voice.Children.Add(_voiceBackend);
        voice.Children.Add(_voiceMemory);
        voice.Children.Add(ModelText("显存按语音进程的 PyTorch 统计；“保留”包含可复用的缓存。", "VoiceMemoryHint"));
        var voiceButtons = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        _voiceLoad.Margin = new Thickness(0, 0, 8, 0); voiceButtons.Children.Add(_voiceLoad); voiceButtons.Children.Add(_voiceUnload);
        voice.Children.Add(voiceButtons); voice.Children.Add(_voiceModelAction);
        _voiceLoad.ToolTip = "加载当前桌宠角色保存的声音模型；需要开启语音播报";
        _voiceUnload.ToolTip = "停止播报并释放语音显存；下次主动播报时可以重新加载";
        _voiceLoad.Click += async (_, _) => await ChangeSpeechModelLoadAsync(true);
        _voiceUnload.Click += async (_, _) => await ChangeSpeechModelLoadAsync(false);
        var voiceCard = Card(voice); voiceCard.Margin = new Thickness(0, 0, 7, 0); cards.Children.Add(voiceCard);
        var llm = new StackPanel();
        llm.Children.Add(ModelText("OLLAMA / 聊天", "LlmModelLabel", 11, true));
        ConfigureCombo(_llmSelector);
        _llmSelector.FontSize = 13; _llmSelector.MinHeight = 36;
        _llmSelector.Margin = new Thickness(0, 0, 0, 7);
        _llmSelector.DisplayMemberPath = nameof(OllamaModelChoice.DisplayName);
        _llmSelector.SelectedValuePath = nameof(OllamaModelChoice.Id);
        _llmSelector.ToolTip = "切换当前聊天模型，与对话窗口同步；需要时再点击加载";
        AutomationProperties.SetName(_llmSelector, "当前聊天模型（与对话窗口同步）");
        _llmSelector.SelectionChanged += async (_, _) => await SelectDashboardLlmAsync();
        llm.Children.Add(_llmSelector); llm.Children.Add(_llmListStatus);
        llm.Children.Add(_llmState); llm.Children.Add(_llmEndpoint);
        llm.Children.Add(_llmMemory);
        var llmButtons = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        _llmLoad.Margin = new Thickness(0, 0, 8, 0); llmButtons.Children.Add(_llmLoad); llmButtons.Children.Add(_llmUnload);
        llm.Children.Add(llmButtons); llm.Children.Add(_llmModelAction); llm.Children.Add(_llmOthers);
        llm.Children.Add(ModelText("操作聊天中当前选中的模型。卸载不会删除文件；其他软件共用此模型时也会受影响。", "LlmMemoryHint"));
        _llmLoad.ToolTip = "提前加载当前聊天模型；沿用 Ollama 原有保留时长，不发送消息";
        _llmUnload.ToolTip = "仅卸载当前选中的模型，不删除模型文件";
        _llmLoad.Click += async (_, _) => await ChangeLlmModelLoadAsync(true);
        _llmUnload.Click += async (_, _) => await ChangeLlmModelLoadAsync(false);
        var llmCard = Card(llm); llmCard.Margin = new Thickness(7, 0, 0, 0); Grid.SetColumn(llmCard, 1); cards.Children.Add(llmCard);
        body.Children.Add(cards);
        BuildVoiceProfileEditor(body);
        var gpu = new StackPanel(); gpu.Children.Add(ModelText("本机显卡", "GpuModelLabel", 16, true));
        gpu.Children.Add(_gpuRows);
        gpu.Children.Add(ModelText("模型颜色保持固定。语音使用 PyTorch 保留量（包含已分配量）；远程 Ollama 单独列出。", "GpuMemoryHint"));
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
        string? voiceBlocked = !_settings.SpeechEnabled ? "语音播报已关闭；在本页上方开启后可加载。" :
            !voiceEnabled ? "当前角色尚未启用语音；可在下方选择并应用生成方式。" : null;
        if (voiceBlocked != null) _voiceModelAction.Text = voiceBlocked;
        else if (_lastVoiceBlockReason != null && _voiceModelAction.Text == _lastVoiceBlockReason) _voiceModelAction.Text = "";
        _lastVoiceBlockReason = voiceBlocked;
        _voiceCurrentRole.Text = "当前桌宠：" + PetVoiceProfiles.CharacterName(_settings.SkinId);
        SyncVoiceProfileEditor();
        RenderVoiceStudio();
    }

    private void BuildVoiceProfileEditor(StackPanel body)
    {
        var content = new StackPanel();
        content.Children.Add(ModelText("语音生成方式", "VoiceProfileTitle", 16, true));
        content.Children.Add(ModelText("先选择角色和生成方式。使用已保存音色时，在下方音色库选择具体声音；配置按角色独立保存。", "VoiceProfileScopeHint"));
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(right, 1); columns.Children.Add(left); columns.Children.Add(right);
        foreach (var combo in new[] { _voiceCharacter, _voiceModel, _voiceSpeaker })
        {
            ConfigureCombo(combo); combo.HorizontalAlignment = HorizontalAlignment.Stretch; combo.MinHeight = 34;
            combo.Margin = new Thickness(0, 0, 0, 9); combo.DisplayMemberPath = nameof(PetVoiceChoice.Label);
            combo.SelectedValuePath = nameof(PetVoiceChoice.Id);
        }
        _voiceCharacter.ItemsSource = PetVoiceProfiles.Characters;
        _voiceModel.ItemsSource = VoiceGenerationModes;
        _voiceSpeaker.ItemsSource = PetVoiceProfiles.Speakers;
        left.Children.Add(ModelText("编辑角色", "VoiceProfileCharacterLabel", 11, true)); left.Children.Add(_voiceCharacter);
        left.Children.Add(ModelText("生成方式", "VoiceProfileModelLabel", 11, true)); left.Children.Add(_voiceModel);
        _voiceSpeakerField = new StackPanel();
        _voiceSpeakerField.Children.Add(ModelText("预置音色", "VoiceProfileSpeakerLabel", 11, true));
        _voiceSpeakerField.Children.Add(_voiceSpeaker); left.Children.Add(_voiceSpeakerField);
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
        AutomationProperties.SetName(_voiceCharacter, "编辑声音的桌宠角色");
        AutomationProperties.SetName(_voiceModel, "此角色的语音生成方式");
        AutomationProperties.SetName(_voiceSpeaker, "预置说话人音色");
        AutomationProperties.SetName(_voiceInstruction, "音色与语气描述，最多600字");
        AutomationProperties.SetName(_voiceProfileSave, "应用生成方式");
        _voiceCharacter.SelectionChanged += (_, _) => { if (!_syncVoiceProfile) LoadVoiceProfileDraft(); };
        _voiceModel.SelectionChanged += (_, _) =>
        {
            if (_syncVoiceProfile) return;
            if (_voiceModel.SelectedValue as string == "voice-design" && string.IsNullOrWhiteSpace(_voiceInstruction.Text))
                _voiceInstruction.Text = PetVoiceProfiles.DefaultInstruction(_voiceCharacter.SelectedValue as string ?? _settings.SkinId);
            VoiceProfileDraftChanged();
        };
        _voiceSpeaker.SelectionChanged += (_, _) => { if (!_syncVoiceProfile) VoiceProfileDraftChanged(); };
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
            _voiceInstruction.Text = profile.Instruct;
            _voiceProfileDirty = false;
        }
        finally { _syncVoiceProfile = false; }
        RefreshVoiceLibrary(VoiceLibraryKey(_voiceProfileLoaded!));
        _voiceProfileStatus.Text = "当前使用：" + PetVoiceProfiles.CharacterName(skinId) + " · " + CurrentVoiceDescription(_voiceProfileLoaded!);
        RenderVoiceProfileDraft();
        LoadVoiceStudioDraft();
    }

    private void VoiceProfileDraftChanged()
    {
        _voiceProfileDirty = true; _voiceProfileStatus.Text = "尚未保存";
        RenderVoiceProfileDraft();
    }

    private static string VoiceGenerationMode(PetVoiceProfile profile) =>
        profile.ModelId is "base" or "gpt-fixed" or "saved-voice" ? "fixed" : profile.ModelId;

    private string CurrentVoiceDescription(PetVoiceProfile profile) => profile.ModelId switch
    {
        "base" => "Let Me Go",
        "gpt-fixed" => "GPT 龙娘固定音色",
        "saved-voice" => _voiceLibraryChoices.FirstOrDefault(x => x.Key == profile.ReferenceId)?.Name ?? "所选音色暂不可用",
        "custom-voice" => PetVoiceProfiles.Speakers.FirstOrDefault(x => x.Id == profile.Speaker)?.Label ?? profile.Speaker,
        "voice-design" => "按文字即时生成（VoiceDesign）",
        _ => "未启用语音"
    };

    private void RenderVoiceProfileDraft()
    {
        string model = _voiceModel.SelectedValue as string ?? "disabled";
        _voiceModel.IsEnabled = _voiceSpeaker.IsEnabled = _voiceInstruction.IsEnabled = !_voiceStudioGenerating;
        if (_voiceSpeakerField != null) _voiceSpeakerField.Visibility = model == "custom-voice" ? Visibility.Visible : Visibility.Collapsed;
        if (_voiceInstructionField != null) _voiceInstructionField.Visibility = model is "custom-voice" or "voice-design" ? Visibility.Visible : Visibility.Collapsed;
        _voiceInstructionLabel.Text = model == "voice-design" ? "音色描述（必填，最多 600 字）" : "语气要求（可选，最多 600 字）";
        string hint = model switch
        {
            "fixed" => "用 Base 复用音色库中的声音，保持固定音色。请在下方选择具体音色；要制作新声音，可使用独立的音色工坊。",
            "custom-voice" => "使用 CustomVoice 的预置说话人，可附加语气要求；无需参考音频。",
            "voice-design" => "每次朗读都由 VoiceDesign 按文字描述即时生成，音色可能变化。想固定声音，请使用下方音色工坊生成并保存。",
            _ => "此角色不朗读文字；其他角色的语音配置保持独立。"
        };
        if (model != "disabled") hint += PetVoiceProfiles.IsModelInstalled(model == "fixed" ? "base" : model) ? "\n模型文件已安装；应用不会自动加载。" : "\n未检测到本地模型文件，使用前需完成下载。";
        _voiceProfileHint.Text = hint;
        _voiceProfileSave.IsEnabled = _speechHost != null && !_speechModelOperation && !_voiceStudioGenerating && _voiceProfileDirty;
    }

    private void SaveVoiceProfileDraft()
    {
        if (_speechHost == null || _speechModelOperation || _voiceStudioGenerating || !_voiceProfileDirty || _voiceCharacter.SelectedValue is not string skinId ||
            _voiceModel.SelectedValue is not string model) return;
        if (model == "voice-design" && string.IsNullOrWhiteSpace(_voiceInstruction.Text))
        { _voiceProfileStatus.Text = "请先填写希望生成的声音描述。"; return; }
        try
        {
            string? referenceId = null;
            string language = _voiceProfileLoaded?.Language ?? "Chinese";
            string speaker = _voiceSpeaker.SelectedValue as string ?? "Vivian", instruction = _voiceInstruction.Text;
            if (model == "fixed")
            {
                if (_voiceLibraryRole != skinId || _voiceLibraryList.SelectedItem is not VoiceLibraryChoice selectedVoice)
                {
                    _voiceProfileStatus.Text = "请先在下方音色库选择要使用的声音。";
                    return;
                }
                model = selectedVoice.ModelId; speaker = "Vivian"; instruction = ""; language = "Chinese";
                if (model == "saved-voice")
                {
                    var selectedReference = (_voiceStudio?.ListSavedVoices(skinId) ?? Array.Empty<PetSavedVoiceInfo>())
                        .FirstOrDefault(x => x.ReferenceId == selectedVoice.Key && string.Equals(x.SkinId, skinId, StringComparison.OrdinalIgnoreCase));
                    if (selectedReference == null)
                    { _voiceProfileStatus.Text = "所选音色已不存在，请从音色库重新选择。"; return; }
                    referenceId = selectedReference.ReferenceId; language = selectedReference.Language;
                }
                else if (!selectedVoice.IsBuiltIn || model is not ("base" or "gpt-fixed"))
                { _voiceProfileStatus.Text = "请先在下方音色库选择要使用的声音。"; return; }
            }
            var profile = PetVoiceProfiles.Normalize(new(model, speaker, language, instruction, referenceId), skinId);
            if (skinId.Equals(_settings.SkinId, StringComparison.OrdinalIgnoreCase)) InvalidateModelRead();
            _speechHost.SetCharacterVoiceProfile(skinId, profile);
            _voiceProfileLoaded = profile; _voiceProfileDirty = false;
            _voiceProfileStatus.Text = "当前使用：" + PetVoiceProfiles.CharacterName(skinId) + " · " + CurrentVoiceDescription(profile) + "；" +
                (skinId.Equals(_settings.SkinId, StringComparison.OrdinalIgnoreCase) ? "下一次朗读使用新声音。" : "切换到该角色后使用。");
            RenderVoiceProfileDraft(); RenderSpeechControls(); RenderModelButtons();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _voiceProfileStatus.Text = "保存未完成：" + ex.Message; }
    }

    private void VoiceProfileSelectionChanged()
    {
        var profile = _settings.GetVoiceProfile(_settings.SkinId);
        if (_observedVoiceSkin == _settings.SkinId && _observedVoiceProfile == profile) return;
        _observedVoiceSkin = _settings.SkinId; _observedVoiceProfile = profile;
        InvalidateModelRead();
        _voiceName.Text = PetVoiceProfiles.ModelDisplayName(profile.ModelId);
        _voiceState.Text = "正在查询…"; _voiceMemory.Text = "";
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
            _llmListStatus.Text = "连接已更换，正在读取已安装模型…";
        }
        SyncModelSelector(); RenderModelButtons();
        _llmState.Text = "正在查询…"; _llmMemory.Text = ""; _llmOthers.Text = "";
        if (_showModelPage) _ = RefreshModelPageAsync();
    }

    private void RenderInstalledModels(IReadOnlyList<string>? models, string? error, string endpoint)
    {
        if (_modelServices.ReadInstalledModels == null)
        {
            _installedListAvailable = false; _llmListStatus.Text = "请从桌宠打开设置选择模型。";
        }
        else if (error != null || models == null)
        {
            _installedListAvailable = false; _llmListStatus.Text = "模型列表暂不可用：" + error;
        }
        else
        {
            _installedListAvailable = true; _installedModelsEndpoint = endpoint;
            _dashboardInstalledModels = models;
            _llmListStatus.Text = models.Count == 0 ? "此服务器尚无已安装模型。" :
                models.Contains(_settings.OllamaModel, StringComparer.Ordinal) ? "与聊天窗口同步 · " + models.Count + " 个已安装模型" : "当前选择未安装，请选择其他模型。";
        }
        if (!_selectingLlm) SyncModelSelector();
    }

    private void SyncModelSelector()
    {
        string selected = _settings.OllamaModel;
        var choices = _dashboardInstalledModels.Select(id => new OllamaModelChoice(id,
            OllamaModels.Supported.FirstOrDefault(m => m.Id == id)?.DisplayName ?? id)).ToList();
        if (!choices.Any(x => x.Id == selected)) choices.Insert(0, new(selected, selected + (_installedListAvailable ? "（未安装）" : "（当前选择）")));
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
        _llmState.Text = "正在切换…"; _llmModelAction.Text = "正在切换聊天模型…"; RenderModelButtons();
        try { _llmModelAction.Text = await _modelServices.SelectLlmModel(selected); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _llmModelAction.Text = "模型切换未完成：" + ex.Message; }
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
        _llmModelAction.Text = load ? "正在加载聊天模型…" : "正在卸载聊天模型…";
        _llmState.Text = load ? "加载中" : "卸载中"; RenderModelButtons();
        try { _llmModelAction.Text = await _modelServices.SetLlmLoaded(load); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _llmModelAction.Text = "模型操作未完成：" + ex.Message; }
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
            _llmState.Text = "正在查询…"; _llmMemory.Text = ""; _llmOthers.Text = "";
        }
        if (!_selectingLlm && !string.Equals(_llmSelector.SelectedValue as string, model, StringComparison.Ordinal)) SyncModelSelector();
        if (_installedModelsEndpoint != endpoint) _installedListAvailable = false;
        bool remote = true;
        try { remote = !OllamaEndpoint.Parse(endpoint).IsLoopback; } catch (ArgumentException) { }
        _llmEndpoint.Text = (remote ? "服务器 · " : "本机服务 · ") + endpoint;
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
            _modelUpdated.Text = "每 5 秒刷新 · 最近读取 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + " · 查看状态不会加载模型";
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
        catch (OperationCanceledException) { return (default, "查询超时或已取消"); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { return (default, ex.Message); }
    }

    private void RenderLlmRuntime(IReadOnlyList<OllamaRunningModel>? models, string? error, string selected)
    {
        if (error != null || models == null)
        {
            _llmState.Text = "状态不可用"; _llmMemory.Text = "无法读取 Ollama 模型状态：" + error;
            _llmOthers.Text = "显存占用未提供"; return;
        }
        var current = models.FirstOrDefault(m => m.Name.Equals(selected, StringComparison.Ordinal));
        _llmState.Text = _modelServices.LlmOperation?.Invoke() ?? (_llmModelOperation ? "正在操作模型…" : current == null ? "未加载" : "已加载");
        _llmMemory.Text = current == null ? "所选模型当前未驻留在 Ollama 中。" :
            "显存 · " + MemorySize(current.VramBytes) + "    总驻留 · " + MemorySize(current.SizeBytes) +
            "\n上下文 · " + (current.ContextLength?.ToString("N0", CultureInfo.CurrentCulture) ?? "未提供") +
            "    保留至 · " + (current.ExpiresAt is { } at ? (at.Year > 2100 ? "持续保留" : at.ToLocalTime().ToString("HH:mm:ss")) : "未提供");
        var others = models.Where(m => !m.Name.Equals(selected, StringComparison.Ordinal)).ToArray();
        _llmOthers.Text = others.Length == 0 ? "没有其他已加载的 Ollama 模型。" :
            "其他已加载模型：\n" + string.Join("\n", others.Take(8).Select(m => m.Name + " · 显存 " + MemorySize(m.VramBytes))) +
            (others.Length > 8 ? "\n另有 " + (others.Length - 8) + " 个模型" : "");
    }

    private void RenderVoiceRuntime(PetVoiceHealth? health, string? error)
    {
        if (error != null)
        {
            _voiceState.Text = "状态不可用"; _voiceBackend.Text = error; _voiceMemory.Text = "显存占用未提供"; return;
        }
        _voiceName.Text = string.IsNullOrWhiteSpace(health?.Model) ? PetVoiceProfiles.ModelDisplayName(_settings.GetVoiceProfile(_settings.SkinId).ModelId) : health.Model;
        _voiceState.Text = _speechModelOperation ? "正在操作模型…" : health == null ? "未加载 · 语音服务未启动" :
            health.Retiring ? "正在释放显存" : health.Status switch
            {
                "loading" => "加载中", "unloading" => "卸载中", "error" => "引擎异常",
                _ => health.Loaded ? "已加载" : "未加载"
            };
        _voiceBackend.Text = health == null ? (_speechHost?.SpeechStatusText ?? "语音服务未启动") :
            "后端 · " + health.Backend + "    设备 · " + (health.Device ?? "未提供") +
            (health.Retained ? "\n聊天期间保持加载" : "\n按原有空闲策略释放");
        _voiceMemory.Text = health == null ? "语音工作进程未运行，无模型显存驻留。" :
            "已分配 · " + MemorySize(health.AllocatedBytes) + "    已保留 · " + MemorySize(health.ReservedBytes);
    }

    internal static string MemorySize(long? bytes) => bytes is not { } value ? "未提供" :
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
