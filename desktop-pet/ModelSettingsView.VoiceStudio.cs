using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopPet;

internal sealed partial class ModelSettingsView
{
    private readonly IPetVoiceStudio? _voiceStudio;
    private readonly Dictionary<string, VoiceStudioDraft> _voiceStudioDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _studioRole = ModelText("", "VoiceStudioRole", 16, true);
    private readonly TextBox _studioName = StudioTextBox("VoiceStudioName", 60, 34);
    private readonly TextBox _studioInstruction = StudioTextBox("VoiceStudioInstruction", 600, 92);
    private readonly TextBox _studioText = StudioTextBox("VoiceStudioText", 100, 70);
    private readonly ComboBox _studioLanguage = new() { Name = "VoiceStudioLanguage" };
    private readonly Button _studioGenerate = new() { Name = "VoiceStudioGenerate", Content = L.T("生成试听", "Generate audition") };
    private readonly Button _studioPlay = new() { Name = "VoiceStudioPlay", Content = L.T("试听", "Audition") };
    private readonly Button _studioStop = new() { Name = "VoiceStudioStop", Content = L.T("停止试听", "Stop audition") };
    private readonly Button _studioCancel = new() { Name = "VoiceStudioCancel", Content = L.T("取消生成", "Cancel generation") };
    private readonly Button _studioSave = new() { Name = "VoiceStudioSave", Content = L.T("固定并保存使用", "Save and use voice") };
    private readonly TextBlock _studioStatus = ModelText("", "VoiceStudioStatus");
    private string? _voiceStudioRole;
    private bool _syncVoiceStudio, _voiceStudioGenerating, _voiceStudioPlaying;
    private CancellationTokenSource? _studioGeneration, _studioPlayback;
    private long _studioPlaybackEpoch;
    private bool VoiceStudioOwnsAudio => _voiceStudioGenerating || _voiceStudioPlaying || _speechHost?.VoiceStudioActive == true;

    private sealed class VoiceStudioDraft
    {
        internal string Name = "", Instruction = "", Text = "", Language = L.IsEnglish ? "English" : "Chinese", Status = L.T("填写声音特点，再生成一段试听。", "Describe the voice, then generate an audition sample.");
        internal PetVoiceDesignSample? Sample;
    }

    private static TextBox StudioTextBox(string name, int maximum, double height) => new()
    {
        Name = name, MaxLength = maximum, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = height, MaxHeight = height + 30,
        Padding = new Thickness(8), FontSize = 12, BorderBrush = Ui.Brush("#DDE5D6"), Background = Ui.Brush("#FAFBF7")
    };

    private void BuildVoiceStudio(StackPanel body)
    {
        var content = new StackPanel();
        content.Children.Add(_studioRole);
        content.Children.Add(ModelText(L.T("先生成，反复试听，满意后再固定。生成不会替换角色当前的声音。", "Generate a sample and audition it as often as you like before saving. Generation does not replace the character's current voice."), "VoiceStudioHint"));
        content.Children.Add(ModelText(L.T("音色名称（保存时必填，最多 60 字）", "Voice name (required to save, up to 60 characters)"), "VoiceStudioNameLabel", 11, true));
        _studioName.AcceptsReturn = false; _studioName.MaxHeight = 34; _studioName.Margin = new Thickness(0, 0, 0, 10);
        content.Children.Add(_studioName);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(right, 1);
        left.Children.Add(ModelText(L.T("希望她 / 他说话的声音（最多 600 字）", "Describe the voice (up to 600 characters)"), "VoiceStudioInstructionLabel", 11, true));
        left.Children.Add(_studioInstruction);
        right.Children.Add(ModelText(L.T("试听台词（单段，最多 100 字，建议 3–30 秒）", "Audition text (one paragraph, up to 100 characters, 3–30 seconds suggested)"), "VoiceStudioTextLabel", 11, true));
        _studioText.AcceptsReturn = false;
        right.Children.Add(_studioText);
        var languages = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        languages.Children.Add(ModelText(L.T("语言", "Language"), "VoiceStudioLanguageLabel", 11, true));
        ConfigureCombo(_studioLanguage); _studioLanguage.MinWidth = 130; _studioLanguage.MinHeight = 30;
        _studioLanguage.Margin = new Thickness(12, 0, 0, 0); _studioLanguage.ItemsSource = PetVoiceProfiles.Languages;
        languages.Children.Add(_studioLanguage); right.Children.Add(languages);
        columns.Children.Add(left); columns.Children.Add(right); content.Children.Add(columns);
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 5) };
        foreach (var button in new[] { _studioGenerate, _studioPlay, _studioStop, _studioCancel, _studioSave })
        {
            button.Margin = new Thickness(0, 0, 8, 7); buttons.Children.Add(button);
            AutomationProperties.SetName(button, button.Content.ToString());
        }
        content.Children.Add(buttons); content.Children.Add(_studioStatus);
        content.Children.Add(ModelText(L.T("使用本地 VoiceDesign 生成，固定后由 Base 复用参考声音。生成或试听时会暂时释放当前语音模型，不改变 Ollama。未固定的试听只保留到关闭设置窗口。", "Local VoiceDesign generates the sample; Base reuses it after saving. Generation or audition may release the current speech model, without changing Ollama. Unsaved samples are discarded when Settings closes."), "VoiceStudioLocalHint"));
        var card = Card(content); card.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(card);
        AutomationProperties.SetName(_studioInstruction, L.T("声音设计提示词，最多600字", "Voice design prompt, up to 600 characters"));
        AutomationProperties.SetName(_studioName, L.T("新音色名称，最多60字", "New voice name, up to 60 characters"));
        AutomationProperties.SetName(_studioText, L.T("声音试听台词，最多100字", "Audition text, up to 100 characters"));
        AutomationProperties.SetName(_studioLanguage, L.T("声音试听语言", "Audition language"));
        _studioInstruction.TextChanged += (_, _) => VoiceStudioDraftChanged();
        _studioText.TextChanged += (_, _) => VoiceStudioDraftChanged();
        _studioLanguage.SelectionChanged += (_, _) => VoiceStudioDraftChanged();
        _studioName.TextChanged += (_, _) =>
        {
            if (_syncVoiceStudio || _voiceStudioRole == null || !_voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var draft)) return;
            draft.Name = _studioName.Text;
            // Naming is library metadata, not synthesis input. A rename never
            // discards a candidate or requires another expensive generation.
            RenderVoiceStudio();
        };
        _studioGenerate.Click += async (_, _) => await GenerateVoiceStudioAsync();
        _studioPlay.Click += async (_, _) => await PlayVoiceStudioAsync();
        _studioStop.Click += (_, _) => StopVoiceStudioPlayback();
        _studioCancel.Click += (_, _) => CancelVoiceStudioGeneration();
        _studioSave.Click += (_, _) => SaveVoiceStudioSample();
    }

    private void LoadVoiceStudioDraft()
    {
        if (_voiceCharacter.SelectedValue is not string skinId || _voiceStudioRole == skinId) return;
        CancelVoiceStudioGeneration(); StopVoiceStudioPlayback();
        _voiceStudioRole = skinId;
        if (!_voiceStudioDrafts.TryGetValue(skinId, out var draft))
        {
            var profile = _settings.GetVoiceProfile(skinId);
            var saved = _voiceStudio?.ReadSavedVoice(skinId);
            draft = new VoiceStudioDraft
            {
                Name = saved?.Name ?? PetVoiceProfiles.CharacterName(skinId) + L.T(" · 新音色", " · New voice"),
                Instruction = saved?.Instruction ?? (profile.ModelId == "voice-design" ? profile.Instruct : PetVoiceProfiles.DefaultInstruction(skinId)),
                Language = saved?.Language ?? PetVoiceProfiles.ResolveForPlayback(profile).Language,
                Text = saved?.Text ?? L.T("你好，我已经准备好了。今天有什么想聊的，就慢慢告诉我吧。我们一起把事情做好，也别忘了休息。", "Hello, I'm here for you. Let's take things one at a time, and remember to rest.")
            };
            _voiceStudioDrafts.Add(skinId, draft);
        }
        _syncVoiceStudio = true;
        try
        {
            _studioName.Text = draft.Name; _studioInstruction.Text = draft.Instruction; _studioText.Text = draft.Text; _studioLanguage.SelectedItem = draft.Language;
        }
        finally { _syncVoiceStudio = false; }
        RenderVoiceStudio();
    }

    private void VoiceStudioDraftChanged()
    {
        if (_syncVoiceStudio || _voiceStudioRole == null || !_voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var draft)) return;
        StopVoiceStudioPlayback();
        draft.Instruction = _studioInstruction.Text; draft.Text = _studioText.Text;
        draft.Language = _studioLanguage.SelectedItem as string ?? (L.IsEnglish ? "English" : "Chinese");
        draft.Status = draft.Sample == null ? L.T("填写声音特点，再生成一段试听。", "Describe the voice, then generate an audition sample.") : L.T("已修改草稿；可试听上次结果，固定前请重新生成。", "Draft changed. You can audition the previous result, but generate again before saving.");
        RenderVoiceStudio();
    }

    private static bool SampleMatches(VoiceStudioDraft draft) => draft.Sample is { } sample &&
        sample.Instruction == draft.Instruction.Trim() && sample.Text == draft.Text.Trim() && sample.Language == draft.Language;

    private void RenderVoiceStudio()
    {
        if (_closed) return;
        VoiceStudioDraft? draft = _voiceStudioRole != null && _voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var value) ? value : null;
        bool available = _voiceStudio != null && _speechHost != null && draft != null;
        bool valid = draft != null && !string.IsNullOrWhiteSpace(draft.Instruction) && !string.IsNullOrWhiteSpace(draft.Text);
        _studioRole.Text = L.T("音色工坊", "Voice Studio") + (_voiceStudioRole == null ? "" : " · " + PetVoiceProfiles.CharacterName(_voiceStudioRole));
        _voiceCharacter.IsEnabled = !_voiceStudioGenerating;
        _studioInstruction.IsEnabled = _studioText.IsEnabled = _studioLanguage.IsEnabled = !_voiceStudioGenerating;
        _studioGenerate.IsEnabled = available && valid && !_voiceStudioGenerating && !_speechModelOperation;
        _studioPlay.IsEnabled = available && draft?.Sample != null && !_voiceStudioGenerating && !_voiceStudioPlaying;
        _studioStop.IsEnabled = _voiceStudioPlaying;
        _studioCancel.IsEnabled = _voiceStudioGenerating && _studioGeneration?.IsCancellationRequested == false;
        _studioSave.IsEnabled = available && draft != null && ValidVoiceLibraryName(draft.Name) && SampleMatches(draft) && !_voiceStudioGenerating && !_voiceStudioPlaying && !_speechModelOperation;
        _studioPlay.Content = draft?.Sample != null && !SampleMatches(draft) ? L.T("试听上次结果", "Audition previous result") : L.T("试听", "Audition");
        _studioStatus.Text = !available ? L.T("请从桌宠打开设置设计声音。", "Open Settings from the pet to design a voice.") : draft!.Status;
        RenderVoiceLibrary();
    }

    private async Task GenerateVoiceStudioAsync()
    {
        if (_closed || _voiceStudio == null || _speechHost == null || _voiceStudioGenerating || _speechModelOperation ||
            _voiceStudioRole == null || !_voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var draft)) return;
        if (string.IsNullOrWhiteSpace(draft.Instruction) || string.IsNullOrWhiteSpace(draft.Text))
        { draft.Status = L.T("请填写声音提示词和试听台词。", "Enter both a voice prompt and audition text."); RenderVoiceStudio(); return; }
        string role = _voiceStudioRole, instruction = draft.Instruction.Trim(), text = draft.Text.Trim(), language = draft.Language;
        StopVoiceStudioPlayback();
        using var operation = new CancellationTokenSource(); _studioGeneration = operation;
        _voiceStudioGenerating = true; draft.Status = L.T("正在加载 VoiceDesign 并生成试听，首次可能需要稍等…", "Loading VoiceDesign and generating an audition. The first run may take a moment…");
        RenderVoiceStudio(); RenderVoiceProfileDraft(); RenderSpeechControls(); RenderModelButtons();
        try
        {
            var sample = await _voiceStudio.GenerateAsync(role, instruction, text, language, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (sample.SkinId != role || sample.Instruction != instruction || sample.Text != text || sample.Language != language)
                throw new InvalidOperationException(L.T("生成结果与本次角色或草稿不匹配，请重试。", "The generated sample does not match this character or draft. Please try again."));
            draft.Sample = sample;
            draft.Status = SampleMatches(draft) ? L.T("生成完成。先点试听；满意后固定并保存，当前声音尚未改变。", "Sample ready. Audition it, then save when satisfied. The current voice has not changed.") : L.T("生成完成，但草稿已修改；固定前请重新生成。", "Sample ready, but the draft has changed. Generate again before saving.");
        }
        catch (OperationCanceledException) { draft.Status = L.T("已取消生成，角色原来的声音保持不变。", "Generation canceled. The character's previous voice is unchanged."); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { draft.Status = L.T("生成未完成：", "Generation failed: ") + ex.Message; }
        finally
        {
            if (ReferenceEquals(_studioGeneration, operation)) _studioGeneration = null;
            _voiceStudioGenerating = false;
            if (!_closed) { RenderVoiceStudio(); RenderVoiceProfileDraft(); RenderSpeechControls(); RenderModelButtons(); }
        }
    }

    private async Task PlayVoiceStudioAsync()
    {
        if (_closed || _voiceStudio == null || _voiceStudioGenerating || _voiceStudioPlaying || _voiceStudioRole == null ||
            !_voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var draft) || draft.Sample == null) return;
        using var operation = new CancellationTokenSource(); _studioPlayback = operation;
        long epoch = ++_studioPlaybackEpoch;
        _voiceStudioPlaying = true; draft.Status = L.T("正在播放已生成的试听，不会重新生成声音。", "Playing the existing audition sample without generating new audio.");
        RenderVoiceStudio(); RenderSpeechControls(); RenderModelButtons();
        try
        {
            await _voiceStudio.PlayAsync(draft.Sample, _settings.SpeechVolume, operation.Token);
            if (epoch == _studioPlaybackEpoch) draft.Status = SampleMatches(draft) ? L.T("试听结束。满意后可以固定并保存。", "Audition finished. Save the voice if you are happy with it.") : L.T("上次结果试听结束；草稿已修改，固定前请重新生成。", "Previous audition finished. The draft has changed; generate again before saving.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { if (epoch == _studioPlaybackEpoch) draft.Status = L.T("试听未完成：", "Audition failed: ") + ex.Message; }
        finally
        {
            if (ReferenceEquals(_studioPlayback, operation)) _studioPlayback = null;
            if (epoch == _studioPlaybackEpoch) _voiceStudioPlaying = false;
            if (!_closed) { RenderVoiceStudio(); RenderSpeechControls(); RenderModelButtons(); }
        }
    }

    private void StopVoiceStudioPlayback()
    {
        _studioPlaybackEpoch++; _studioPlayback?.Cancel(); _voiceStudio?.StopPlayback();
        if (_voiceStudioPlaying && _voiceStudioRole != null && _voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var draft))
            draft.Status = L.T("试听已停止。", "Audition stopped.");
        _voiceStudioPlaying = false;
        if (!_closed) RenderVoiceStudio();
    }

    private void CancelVoiceStudioGeneration()
    {
        if (_studioGeneration == null || _studioGeneration.IsCancellationRequested) return;
        _studioGeneration.Cancel();
        if (_voiceStudioRole != null && _voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var draft)) draft.Status = L.T("正在取消生成并释放语音模型…", "Canceling generation and releasing the speech model…");
        if (!_closed) RenderVoiceStudio();
    }

    private void SaveVoiceStudioSample()
    {
        if (_closed || _voiceStudio == null || _speechHost == null || _voiceStudioGenerating || _voiceStudioPlaying || _speechModelOperation ||
            _voiceStudioRole == null || !_voiceStudioDrafts.TryGetValue(_voiceStudioRole, out var draft) || draft.Sample == null) return;
        if (!SampleMatches(draft)) { draft.Status = L.T("提示词或台词已修改，请重新生成后再固定。", "The prompt or text has changed. Generate again before saving."); RenderVoiceStudio(); return; }
        if (!ValidVoiceLibraryName(draft.Name)) { draft.Status = L.T("请填写 1–60 字的音色名称，名称不能包含换行或控制字符。", "Enter a voice name of 1–60 characters without line breaks or control characters."); RenderVoiceStudio(); return; }
        try
        {
            var profile = _voiceStudio.Save(draft.Sample, draft.Name.Trim());
            if (profile.ModelId != "saved-voice" || string.IsNullOrEmpty(profile.ReferenceId)) throw new InvalidOperationException(L.T("固定音色保存结果无效。", "The saved voice result is invalid."));
            _speechHost.SetCharacterVoiceProfile(draft.Sample.SkinId, profile);
            draft.Status = L.T("已保存「", "Saved “") + draft.Name.Trim() + L.T("」并用于 ", "” for ") + PetVoiceProfiles.CharacterName(draft.Sample.SkinId) + L.T("；以后会复用这段参考声音。", ". Future speech will reuse this reference voice.");
            LoadVoiceProfileDraft(); RefreshVoiceLibrary(profile.ReferenceId); RenderSpeechControls(); RenderModelButtons();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { draft.Status = L.T("保存未完成，原声音未替换：", "Could not save; the previous voice is unchanged: ") + ex.Message; }
        RenderVoiceStudio();
    }

    private void CloseVoiceStudio()
    {
        _studioGeneration?.Cancel(); StopVoiceStudioPlayback(); _voiceStudio?.Dispose(); _voiceStudioDrafts.Clear();
    }
}
