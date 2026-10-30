using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace DesktopPet;

internal sealed partial class ModelSettingsView
{
    private readonly ListBox _voiceLibraryList = new() { Name = "VoiceLibraryList" };
    private readonly TextBox _voiceLibraryName = StudioTextBox("VoiceLibraryName", 60, 34);
    private readonly Button _voiceLibraryApply = new() { Name = "VoiceLibraryApply", Content = L.T("使用所选音色", "Use selected voice") };
    private readonly Button _voiceLibraryRename = new() { Name = "VoiceLibraryRename", Content = L.T("重命名", "Rename") };
    private readonly Button _voiceLibraryDelete = new() { Name = "VoiceLibraryDelete", Content = L.T("删除", "Delete") };
    private readonly TextBlock _voiceLibraryStatus = ModelText("", "VoiceLibraryStatus");
    private bool _syncVoiceLibrary;
    private string? _voiceLibraryRole;
    private IReadOnlyList<VoiceLibraryChoice> _voiceLibraryChoices = Array.Empty<VoiceLibraryChoice>();

    private sealed record VoiceLibraryChoice(string Key, string Details, string Name, string ModelId,
        PetSavedVoiceInfo? Saved = null)
    {
        internal bool IsBuiltIn => Saved == null;
    }

    private void BuildVoiceLibrary(StackPanel body)
    {
        var content = new StackPanel();
        content.Children.Add(ModelText(L.T("已保存音色", "Saved voices"), "VoiceLibraryTitle", 16, true));
        content.Children.Add(ModelText(L.T("选择名字，点击「使用所选音色」即可为此角色启用。你保存的音色可在这里重命名或删除。", "Select a name and click Use selected voice to enable it for this character. Rename or delete your saved voices here."), "VoiceLibraryHint"));
        _voiceLibraryList.MinHeight = 132; _voiceLibraryList.MaxHeight = 240;
        _voiceLibraryList.Margin = new Thickness(0, 0, 0, 12);
        _voiceLibraryList.BorderBrush = Ui.Brush("#DDE5D6"); _voiceLibraryList.Background = Ui.Brush("#FAFBF7");
        _voiceLibraryList.Padding = new Thickness(5); _voiceLibraryList.SelectionMode = SelectionMode.Single;
        ScrollViewer.SetVerticalScrollBarVisibility(_voiceLibraryList, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_voiceLibraryList, ScrollBarVisibility.Disabled);
        _voiceLibraryList.ItemTemplate = (DataTemplate)XamlReader.Parse("""
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <StackPanel>
    <TextBlock Text="{Binding Name}" FontSize="13" FontWeight="SemiBold" Foreground="#294B36" TextTrimming="CharacterEllipsis" ToolTip="{Binding Name}"/>
    <TextBlock Text="{Binding Details}" FontSize="11" Foreground="#71826D" Margin="0,3,0,0" TextWrapping="Wrap"/>
  </StackPanel>
</DataTemplate>
""");
        _voiceLibraryList.ItemContainerStyle = (Style)XamlReader.Parse("""
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
  <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
  <Setter Property="Padding" Value="10,8"/>
  <Setter Property="Margin" Value="0,2"/>
  <Setter Property="Cursor" Value="Hand"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="ListBoxItem">
        <Border x:Name="row" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="8" Padding="{TemplateBinding Padding}">
          <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="row" Property="Background" Value="#F0F4EB"/></Trigger>
          <Trigger Property="IsSelected" Value="True"><Setter TargetName="row" Property="Background" Value="#E2EDD9"/><Setter TargetName="row" Property="BorderBrush" Value="#AFC69D"/></Trigger>
          <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="row" Property="BorderBrush" Value="#39644D"/></Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter TargetName="row" Property="Opacity" Value="0.5"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
""");
        _voiceLibraryList.SelectedValuePath = nameof(VoiceLibraryChoice.Key);
        content.Children.Add(_voiceLibraryList);
        content.Children.Add(ModelText(L.T("修改所选音色名称（最多 60 字）", "Edit selected voice name (up to 60 characters)"), "VoiceLibraryNameLabel", 11, true));
        _voiceLibraryName.AcceptsReturn = false; _voiceLibraryName.MaxHeight = 34;
        content.Children.Add(_voiceLibraryName);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        foreach (var button in new[] { _voiceLibraryApply, _voiceLibraryRename, _voiceLibraryDelete })
        {
            button.Margin = new Thickness(0, 0, 8, 6); actions.Children.Add(button);
            AutomationProperties.SetName(button, button.Content.ToString());
        }
        content.Children.Add(actions); content.Children.Add(_voiceLibraryStatus);
        content.Children.Add(ModelText(L.T("DeepSeek 的 Let Me Go 和 GPT 龙娘的默认音色已锁定，不能删除。删除自定义音色时，本机保留恢复副本。", "DeepSeek's Let Me Go and GPT Dragon's default voice are protected from deletion. Deleted custom voices retain a local recovery copy."), "VoiceLibraryProtectedHint"));
        var card = Card(content); card.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(card);
        AutomationProperties.SetName(_voiceLibraryList, L.T("当前角色的默认与已保存音色", "Default and saved voices for the current character"));
        AutomationProperties.SetName(_voiceLibraryName, L.T("固定音色名称，最多60字", "Saved voice name, up to 60 characters"));
        _voiceLibraryList.SelectionChanged += (_, _) => { if (!_syncVoiceLibrary) VoiceLibrarySelectionChanged(); };
        _voiceLibraryName.TextChanged += (_, _) => { if (!_syncVoiceLibrary) RenderVoiceLibrary(); };
        _voiceLibraryApply.Click += (_, _) => ApplySelectedSavedVoice();
        _voiceLibraryRename.Click += (_, _) => RenameSelectedSavedVoice();
        _voiceLibraryDelete.Click += (_, _) => ConfirmDeleteSelectedSavedVoice();
    }

    private static bool ValidVoiceLibraryName(string? value)
    {
        try { PetVoiceLibrary.ValidateName(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string VoiceLibraryKey(PetVoiceProfile profile) => profile.ModelId == "saved-voice"
        ? profile.ReferenceId ?? "" : "builtin:" + profile.ModelId;

    private void RefreshVoiceLibrary(string? preferredKey = null)
    {
        if (_voiceCharacter.SelectedValue is not string role) return;
        string defaultModel = PetVoiceProfiles.DefaultForSkin(role).ModelId;
        var choices = new List<VoiceLibraryChoice>();
        foreach (var (id, name) in new[] { ("base", "Let Me Go"), ("gpt-fixed", L.T("GPT 龙娘固定音色", "GPT Dragon voice")) })
        {
            string owner = id == "base" ? L.T("DeepSeek 默认", "DeepSeek default") : L.T("GPT 默认", "GPT default");
            choices.Add(new("builtin:" + id, owner + L.T(" · 已保护，不可删除", " · Protected from deletion") + (id == defaultModel ? L.T(" · 本角色默认", " · Default for this character") : ""), name, id));
        }
        string? error = null;
        try
        {
            foreach (var voice in _voiceStudio?.ListSavedVoices(role) ?? Array.Empty<PetSavedVoiceInfo>())
            {
                if (!PetVoiceProfiles.IsReferenceId(voice.ReferenceId) || !string.Equals(voice.SkinId, role, StringComparison.OrdinalIgnoreCase)) continue;
                string name = string.IsNullOrWhiteSpace(voice.Name) ? L.T("未命名音色", "Unnamed voice") : voice.Name;
                string details = L.T("自定义 · ", "Custom · ") + voice.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " · " + voice.ReferenceId[..6];
                choices.Add(new(voice.ReferenceId, details, name, "saved-voice", voice));
            }
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { error = L.T("音色库暂不可用：", "Voice library unavailable: ") + ex.Message; }
        var profile = _settings.GetVoiceProfile(role);
        string? selected = preferredKey ?? VoiceLibraryKey(profile);
        // A missing reference or a non-library mode must not silently select a
        // different voice. Applying a named voice is always an explicit choice.
        if (!choices.Any(x => x.Key == selected)) selected = null;
        _syncVoiceLibrary = true;
        try
        {
            _voiceLibraryRole = role; _voiceLibraryChoices = choices;
            _voiceLibraryList.ItemsSource = choices; _voiceLibraryList.SelectedValue = selected;
            _voiceLibraryName.Text = (_voiceLibraryList.SelectedItem as VoiceLibraryChoice)?.Name ?? "";
        }
        finally { _syncVoiceLibrary = false; }
        _voiceLibraryStatus.Text = error ?? (selected == null
            ? L.T("请从列表选择一个音色后点击使用。", "Select a voice from the list, then click Use selected voice.") : L.T("选择音色后点击「使用所选音色」生效；修改名称不会改变声音。", "Click Use selected voice to activate your selection. Renaming does not change the sound."));
        RenderVoiceLibrary();
    }

    private void VoiceLibrarySelectionChanged()
    {
        if (_voiceLibraryList.SelectedItem is not VoiceLibraryChoice choice)
        { _voiceLibraryName.Text = ""; RenderVoiceLibrary(); return; }
        _syncVoiceLibrary = true;
        try { _voiceLibraryName.Text = choice.Name; }
        finally { _syncVoiceLibrary = false; }
        _voiceLibraryStatus.Text = choice.IsBuiltIn ? L.T("内置音色受到保护，可以使用，不能重命名或删除。", "This built-in voice is protected. You can use it, but cannot rename or delete it.") : L.T("已选中「", "Selected “") + choice.Name + L.T("」；点击使用后才会切换当前角色的音色。", "”. Click Use selected voice to change this character's voice.");
        if (_voiceModel.SelectedValue as string == "fixed") VoiceProfileDraftChanged();
        RenderVoiceLibrary();
    }

    private void RenderVoiceLibrary()
    {
        if (_closed) return;
        var selected = _voiceLibraryList.SelectedItem as VoiceLibraryChoice;
        bool busy = VoiceStudioOwnsAudio || _speechModelOperation;
        bool available = _speechHost != null && !busy && selected != null;
        bool editable = available && _voiceStudio != null && selected?.IsBuiltIn == false;
        _voiceLibraryList.IsEnabled = !busy;
        _voiceLibraryApply.IsEnabled = available;
        _voiceLibraryName.IsEnabled = editable;
        _voiceLibraryRename.IsEnabled = editable && ValidVoiceLibraryName(_voiceLibraryName.Text) && _voiceLibraryName.Text.Trim() != selected?.Name;
        _voiceLibraryDelete.IsEnabled = editable;
    }

    private bool IsCurrentSavedChoice(VoiceLibraryChoice choice, string role) => !choice.IsBuiltIn &&
        _voiceStudio != null && _voiceStudio.ListSavedVoices(role).Any(x => x.ReferenceId == choice.Key &&
            string.Equals(x.SkinId, role, StringComparison.OrdinalIgnoreCase));

    private void ApplySelectedSavedVoice()
    {
        if (_closed || _speechHost == null || VoiceStudioOwnsAudio || _speechModelOperation ||
            _voiceCharacter.SelectedValue is not string role || _voiceLibraryRole != role ||
            _voiceLibraryList.SelectedItem is not VoiceLibraryChoice choice) return;
        try
        {
            if (!choice.IsBuiltIn && !IsCurrentSavedChoice(choice, role)) throw new InvalidOperationException(L.T("所选音色已不存在，请刷新或重新生成。", "The selected voice no longer exists. Refresh the list or generate it again."));
            var previous = _settings.GetVoiceProfile(role);
            bool sameVoice = previous.ModelId == choice.ModelId && previous.ReferenceId == choice.Saved?.ReferenceId;
            var profile = new PetVoiceProfile(choice.ModelId, "Vivian", choice.Saved?.Language ?? (L.IsEnglish ? "English" : "Chinese"), "", choice.Saved?.ReferenceId,
                FollowAppLanguage: choice.IsBuiltIn);
            // Re-applying the active voice is not a request to reset a user's
            // explicit speech language (including legacy non-Chinese profiles).
            if (sameVoice) profile = profile with { Language = previous.Language, FollowAppLanguage = PetVoiceProfiles.FollowsAppLanguage(previous) };
            _speechHost.SetCharacterVoiceProfile(role, profile);
            LoadVoiceProfileDraft();
            _voiceLibraryStatus.Text = L.T("已为 ", "Set ") + PetVoiceProfiles.CharacterName(role) + L.T(" 使用「", " to use “") + choice.Name + L.T("」。", "”.");
            RenderSpeechControls(); RenderModelButtons();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _voiceLibraryStatus.Text = L.T("切换未完成：", "Could not switch: ") + ex.Message; }
    }

    private void RenameSelectedSavedVoice()
    {
        if (_closed || _voiceStudio == null || VoiceStudioOwnsAudio || _speechModelOperation ||
            _voiceCharacter.SelectedValue is not string role || _voiceLibraryRole != role ||
            _voiceLibraryList.SelectedItem is not VoiceLibraryChoice choice || choice.IsBuiltIn) return;
        if (!ValidVoiceLibraryName(_voiceLibraryName.Text)) { _voiceLibraryStatus.Text = L.T("请填写 1–60 字名称，不能包含换行或控制字符。", "Enter a name of 1–60 characters without line breaks or control characters."); return; }
        try
        {
            if (!IsCurrentSavedChoice(choice, role)) throw new InvalidOperationException(L.T("所选音色已不存在。", "The selected voice no longer exists."));
            var renamed = _voiceStudio.RenameSavedVoice(choice.Key, _voiceLibraryName.Text.Trim());
            // The studio names a new candidate. Its draft is independent from
            // this saved reference, even when both happen to share a name.
            RefreshVoiceLibrary(choice.Key);
            if (!_voiceProfileDirty && _voiceProfileLoaded?.ReferenceId == choice.Key)
                _voiceProfileStatus.Text = L.T("当前使用：", "Currently using: ") + PetVoiceProfiles.CharacterName(role) + " · " + renamed.Name;
            _voiceLibraryStatus.Text = L.T("已重命名为「", "Renamed to “") + renamed.Name + L.T("」；参考音频与正在使用的音色不变。", "”. The reference audio and active voice are unchanged.");
            RenderVoiceStudio();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { _voiceLibraryStatus.Text = L.T("重命名未完成：", "Could not rename: ") + ex.Message; }
    }

    private void ConfirmDeleteSelectedSavedVoice()
    {
        if (_closed || VoiceStudioOwnsAudio || _speechModelOperation ||
            _voiceLibraryList.SelectedItem is not VoiceLibraryChoice choice || choice.IsBuiltIn) return;
        string message = L.T("将「", "Remove “") + choice.Name + L.T("」移出音色库？\n\n正在使用它的角色会先恢复默认声音。本机保留恢复副本，不会删除语音模型权重。", "” from the voice library?\n\nCharacters using it will return to their default voices. A local recovery copy is kept. Speech model weights will not be deleted.");
        var owner = Window.GetWindow(this);
        var result = owner == null ? MessageBox.Show(message, L.T("移出固定音色", "Remove saved voice"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, message, L.T("移出固定音色", "Remove saved voice"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes) DeleteSelectedSavedVoice(confirmed: true);
    }

    // Fixtures can exercise the confirmed branch without opening a modal. The
    // button above always gets the user's explicit confirmation first.
    internal void DeleteSelectedSavedVoice(bool confirmed = false)
    {
        if (!confirmed || _closed || _voiceStudio == null || _speechHost == null || VoiceStudioOwnsAudio || _speechModelOperation ||
            _voiceCharacter.SelectedValue is not string role || _voiceLibraryRole != role ||
            _voiceLibraryList.SelectedItem is not VoiceLibraryChoice choice || choice.IsBuiltIn) return;
        try
        {
            if (!IsCurrentSavedChoice(choice, role)) throw new InvalidOperationException(L.T("所选音色已不存在。", "The selected voice no longer exists."));
            _speechHost.DeleteSavedVoiceReference(choice.Key, () => _voiceStudio.DeleteSavedVoice(choice.Key));
            LoadVoiceProfileDraft(); RefreshVoiceLibrary();
            _voiceLibraryStatus.Text = L.T("已将「", "Removed “") + choice.Name + L.T("」移出音色库，本机保留恢复副本；使用它的角色已恢复默认声音。", "” from the library. A local recovery copy is kept, and affected characters have returned to their default voices.");
            RenderSpeechControls(); RenderModelButtons();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            RefreshVoiceLibrary(choice.Key);
            _voiceLibraryStatus.Text = L.T("删除未完成：", "Could not delete: ") + ex.Message;
        }
    }
}
