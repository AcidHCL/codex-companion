using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DesktopPet;

// Desktop controls shared by the standalone settings page. This view changes
// the existing PetWindow preferences; it does not maintain another copy.
internal sealed class GeneralSettingsView : UserControl, IDisposable
{
    private readonly PetWindow _pet;
    private readonly TextBlock _role, _sizeLabel, _volumeLabel, _behaviorHint;
    private readonly Slider _slider, _volume;
    private readonly CheckBox _topmost, _motion, _clickThrough, _movement, _sound, _llmBehavior;
    private readonly Button _visibility, _stopPerformance, _playEmotion, _climbLeft, _climbRight;
    private readonly ComboBox _emotion;
    private readonly ComboBox _language = new() { Name = "AppLanguage", MinHeight = 34, MinWidth = 190 };
    private readonly TextBlock _languageStatus = Note("", "AppLanguageStatus");
    private readonly Button _restart;
    private bool _syncing, _disposed;

    public GeneralSettingsView(PetWindow pet, Action<bool>? setLlmBehavior = null, Action? restart = null)
    {
        _pet = pet;
        Name = "GeneralSettingsView";
        var body = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        body.Children.Add(Ui.Text(L.T("桌宠与互动", "Pet & interaction"), 23, "#203E2C", true));
        _role = Note("", "GeneralCurrentRole"); _role.Margin = new Thickness(0, 8, 0, 18);
        body.Children.Add(_role);
        var language = Section(body, "Language / 语言", "GeneralLanguageSection");
        _language.Items.Add(new ComboBoxItem { Content = "简体中文", Tag = "zh-CN" });
        _language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en-US" });
        _language.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(_language, "Language / 语言");
        _language.SelectionChanged += (_, _) =>
        {
            if (_syncing || _disposed || _language.SelectedItem is not ComboBoxItem item || item.Tag is not string value) return;
            try { _pet.SetLanguage(value); Refresh(); }
            catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
            {
                Refresh();
                _languageStatus.Text = L.T("语言保存失败：", "Could not save language: ") + ex.Message;
            }
        };
        var languageActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        languageActions.Children.Add(_language);
        _restart = Button(L.T("重启桌宠", "Restart AI Pet"), "RestartPet", (sender, _) =>
        {
            if (_disposed || restart == null || sender is not Button restartButton || !restartButton.IsEnabled) return;
            restartButton.IsEnabled = false;
            _language.IsEnabled = false;
            _languageStatus.Text = L.T("正在保存设置并重启桌宠…", "Saving settings and restarting AI Pet…");
            try { restart(); }
            catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
            {
                restartButton.IsEnabled = true;
                _language.IsEnabled = true;
                _languageStatus.Text = L.T("重启失败：", "Could not restart: ") + ex.Message;
            }
        });
        _restart.Margin = new Thickness(12, 0, 0, 0);
        _restart.MinHeight = 34;
        _restart.IsEnabled = restart != null;
        _restart.ToolTip = L.T("保存设置并重启，应用所选语言。正在进行的生成和播放会停止。", "Save settings and restart to apply the selected language. Active generation and playback will stop.");
        languageActions.Children.Add(_restart);
        language.Children.Add(languageActions); language.Children.Add(_languageStatus);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(right, 1); columns.Children.Add(left); columns.Children.Add(right); body.Children.Add(columns);

        var desktop = Section(left, L.T("显示与桌面", "Display & desktop"), "GeneralDisplaySection");
        _sizeLabel = Note("", "PetSizeLabel"); desktop.Children.Add(_sizeLabel);
        _slider = new Slider { Name = "PetSize", Minimum = 160, Maximum = 440, TickFrequency = 10,
            IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 12), Foreground = Ui.Brush("#3C6850") };
        _slider.ValueChanged += (_, _) => { if (!_syncing) _pet.SetSize(_slider.Value); };
        AutomationProperties.SetName(_slider, L.T("桌宠显示大小", "Pet display size")); desktop.Children.Add(_slider);
        _topmost = Toggle(L.T("始终置顶", "Always on top"), "PetTopmost", value => _pet.SetTopmost(value)); desktop.Children.Add(_topmost);
        _motion = Toggle(L.T("呼吸动画", "Breathing animation"), "PetMotion", value => _pet.SetMotion(value)); desktop.Children.Add(_motion);
        _clickThrough = Toggle(L.T("鼠标穿透", "Click-through"), "PetClickThrough", value => _pet.SetClickThrough(value)); desktop.Children.Add(_clickThrough);
        desktop.Children.Add(Note(L.T("开启穿透后，从托盘打开设置，即可恢复角色互动。", "To restore interaction after enabling click-through, open Settings from the system tray."), "PetClickThroughHint"));
        var visibilityButtons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        _visibility = Button("", "PetVisibility", (_, _) => { if (_pet.IsVisible) _pet.Hide(); else _pet.Show(); Refresh(); });
        _visibility.Margin = new Thickness(0, 0, 4, 0); visibilityButtons.Children.Add(_visibility);
        var reset = Button(L.T("回到屏幕角落", "Reset position"), "ResetPetPosition", (_, _) => { _pet.Show(); _pet.ResetPosition(); _pet.Say(L.T("我在这里。", "I'm here.")); });
        reset.Margin = new Thickness(4, 0, 0, 0); visibilityButtons.Children.Add(reset); desktop.Children.Add(visibilityButtons);

        var movement = Section(right, L.T("行走与行为", "Movement & behavior"), "GeneralBehaviorSection");
        _movement = Toggle(L.T("桌面行走与窗口互动", "Walking & window interaction"), "PetMovement", value => _pet.SetMovement(value)); movement.Children.Add(_movement);
        _llmBehavior = Toggle(L.T("LLM 行为模式", "LLM behavior mode"), "LlmBehaviorEnabled", value =>
        {
            if (setLlmBehavior != null) setLlmBehavior(value);
            else { _pet.Settings.LlmBehaviorEnabled = value; _pet.SaveNow(); }
            Refresh();
        });
        movement.Children.Add(_llmBehavior);
        _behaviorHint = Note("", "LlmBehaviorStatus"); movement.Children.Add(_behaviorHint);
        var jump = Button(L.T("跳到附近窗口", "Jump to nearby window"), "JumpNearbyWindow", (_, _) => _pet.JumpToNearbyWindow());
        jump.Margin = new Thickness(0, 12, 0, 0); movement.Children.Add(jump);
        var climbButtons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
        _climbLeft = Button(L.T("向左攀爬", "Climb left"), "ClimbLeft", (_, _) => _pet.ClimbNearbyWindow(-1)); _climbLeft.Margin = new Thickness(0, 0, 4, 0);
        _climbRight = Button(L.T("向右攀爬", "Climb right"), "ClimbRight", (_, _) => _pet.ClimbNearbyWindow(1)); _climbRight.Margin = new Thickness(4, 0, 0, 0);
        climbButtons.Children.Add(_climbLeft); climbButtons.Children.Add(_climbRight); movement.Children.Add(climbButtons);
        movement.Children.Add(Note(L.T("把我放在窗口侧边附近，朝窗沿攀爬后会自动翻上顶边。", "Place me near the side of a window. I can climb toward its ledge and pull myself onto the top."), "PetClimbHint"));

        var music = Section(left, L.T("音乐与唱歌", "Music & singing"), "GeneralMusicSection");
        _sound = Toggle(L.T("音乐开关", "Enable music"), "SoundEnabled", value => _pet.SetSoundEnabled(value)); music.Children.Add(_sound);
        _volumeLabel = Note("", "SoundVolumeLabel"); music.Children.Add(_volumeLabel);
        _volume = new Slider { Name = "SoundVolume", Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 8, 0, 12), Foreground = Ui.Brush("#3C6850") };
        AutomationProperties.SetName(_volume, L.T("音乐音量", "Music volume"));
        _volume.ValueChanged += (_, _) => { if (!_syncing) _pet.SetSoundVolume(_volume.Value / 100); }; music.Children.Add(_volume);
        var performances = Section(right, L.T("表演与情绪", "Performances & emotions"), "GeneralPerformanceSection");
        _emotion = new ComboBox { Name = "PetEmotion", MinHeight = 34, Margin = new Thickness(0, 4, 0, 10) };
        AutomationProperties.SetName(_emotion, L.T("选择表演与情绪动画", "Choose a performance or emotion")); performances.Children.Add(_emotion);
        _playEmotion = Button(L.T("播放所选表演", "Play selection"), "PlayPetEmotion", async (_, _) =>
        { if (_emotion.SelectedItem is ComboBoxItem item && item.Tag is string action) await _pet.PreviewSceneAsync(action); });
        var performanceButtons = new UniformGrid { Columns = 2 };
        _playEmotion.Margin = new Thickness(0, 0, 4, 0); performanceButtons.Children.Add(_playEmotion);
        _stopPerformance = Button(L.T("停止表演", "Stop performance"), "StopPetPerformance", (_, _) => { _pet.StopScenePreview(); Refresh(); });
        _stopPerformance.Margin = new Thickness(4, 0, 0, 0); performanceButtons.Children.Add(_stopPerformance);
        performances.Children.Add(performanceButtons);
        _emotion.SelectionChanged += (_, _) => { if (!_syncing) RefreshPerformanceAvailability(); };
        performances.Children.Add(Note(L.T("Let Me Go 与保持 Q 弹会播放配套音乐，音量由左侧控制。停止表演后恢复日常状态；开启 LLM 模式后也可对话点播。", "Let Me Go and Bouncy Dance include music, controlled on the left. Stopping restores the normal state. With LLM mode enabled, you can also request performances in chat."), "PetPerformanceHint"));

        Content = new ScrollViewer { Name = "GeneralSettingsScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _pet.SettingsChanged += Refresh;
        _pet.BehaviorStateChanged += Refresh;
        Refresh();
    }

    private static StackPanel Section(Panel parent, string title, string name)
    {
        var content = new StackPanel();
        var heading = Ui.Text(title, 16, "#2B513B", true); heading.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(heading);
        parent.Children.Add(new Border { Name = name, Background = Brushes.White, BorderBrush = Ui.Brush("#E1E8DC"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 16), Child = content });
        return content;
    }

    private static TextBlock Note(string text, string name) => new()
    {
        Name = name, Text = text, FontSize = 11, Foreground = Ui.Brush("#71826D"), TextWrapping = TextWrapping.Wrap,
        LineHeight = 18, Margin = new Thickness(0, 6, 0, 2)
    };

    private static Button Button(string label, string name, RoutedEventHandler click)
    {
        var button = Ui.Button(label, click); button.Name = name;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private CheckBox Toggle(string label, string name, Action<bool> setter)
    {
        var toggle = new CheckBox { Content = label, Name = name };
        AutomationProperties.SetName(toggle, label);
        toggle.Checked += (_, _) => { if (!_syncing && !_disposed) setter(true); };
        toggle.Unchecked += (_, _) => { if (!_syncing && !_disposed) setter(false); };
        return toggle;
    }

    private void Refresh()
    {
        if (_disposed) return;
        _syncing = true;
        try
        {
            _language.SelectedIndex = _pet.Settings.Language == "en-US" ? 1 : 0;
            bool languagePending = (_pet.Settings.Language == "en-US") != L.IsEnglish;
            _languageStatus.Text = languagePending
                ? "语言已保存，请重启桌宠生效。 / Language saved. Restart AI Pet to apply."
                : L.T("切换后重启桌宠生效。界面、人设提示词及跟随软件语言的语音会一起切换；自定义内容保留。", "Restart AI Pet after changing language. The interface, character prompts, and speech set to follow the app will switch together. Custom content is preserved.");
            bool live2D = _pet.CurrentLive2DModel != null;
            _role.Text = L.T("当前角色：", "Current character: ") + _pet.CurrentDisplayName + L.T(" · 设置会随桌宠自动保存。", " · Preferences are saved automatically.");
            _motion.Content = live2D ? L.T("眨眼与呼吸", "Blinking & breathing") : L.T("呼吸动画", "Breathing animation");
            _motion.ToolTip = live2D ? L.T("驱动模型已有的标准眼睛与呼吸参数；不影响桌面行走与场景动画。系统减少动态时保持静止。", "Uses the model's standard eye and breathing parameters without affecting walking or scene animations. Remains still when reduced motion is enabled.") : L.T("只控制站立呼吸；关闭后仍可桌面行走、下落和窗口互动。", "Only controls idle breathing. Walking, falling, and window interaction still work when disabled.");
            _sizeLabel.Text = L.T("显示大小    ", "Display size    ") + (int)_pet.Settings.Size + " px";
            _slider.Value = _pet.Settings.Size;
            _topmost.IsChecked = _pet.Settings.Topmost;
            _motion.IsChecked = _pet.Settings.MotionEnabled;
            _clickThrough.IsChecked = _pet.Settings.ClickThrough;
            _movement.IsChecked = _pet.Settings.MovementEnabled;
            _llmBehavior.IsChecked = _pet.Settings.LlmBehaviorEnabled;
            _behaviorHint.Text = !_pet.Settings.LlmBehaviorEnabled
                ? L.T("默认模式：沿用当前行为，不自动调用模型。", "Default mode: use existing behavior without automatic model requests.")
                : (_pet.IsLlmBehaviorActive ? L.T("LLM 模式已连接。", "LLM behavior is connected. ") : _pet.CanLlmReact ? L.T("LLM 可回应互动；动作使用默认逻辑。", "The LLM can respond to interactions; movement follows default behavior. ") : L.T("LLM 暂不可用，继续默认行为并自动重试。", "LLM unavailable. Continuing default behavior and retrying automatically. "))
                  + L.T("点击、拖拽等互动会通知所选 Ollama 模型；可对话请求唱歌、跳舞、敲碗、表情和移动。移动仍需开启上方开关。", "Clicks and dragging notify the selected Ollama model. Ask it to sing, dance, tap bowls, react, or move. Movement also requires the switch above.");
            _sound.IsChecked = _pet.Settings.SoundEnabled;
            _volume.Value = _pet.Settings.SoundVolume * 100;
            _volume.IsEnabled = _pet.Settings.SoundEnabled;
            _volumeLabel.Text = L.T("音量    ", "Volume    ") + Math.Round(_pet.Settings.SoundVolume * 100) + "%" + (_pet.Settings.SoundEnabled ? "" : L.T(" · 已静音", " · Muted"));
            RefreshPerformanceChoices(); RefreshPerformanceAvailability();
            _climbLeft.IsEnabled = _climbRight.IsEnabled = _pet.UsesBuiltInScenes && _pet.CompanionSurfacesAvailable;
            _visibility.Content = _pet.IsVisible ? L.T("暂时隐藏桌宠", "Hide pet") : L.T("唤回桌宠", "Show pet");
            AutomationProperties.SetName(_visibility, _visibility.Content.ToString());
        }
        finally { _syncing = false; }
    }

    private void RefreshPerformanceChoices()
    {
        var installed = PetPerformanceCatalog.Installed(_pet.CurrentScenePack).ToArray();
        var current = _emotion.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string).ToArray();
        if (current.SequenceEqual(installed)) return;
        string? selected = (_emotion.SelectedItem as ComboBoxItem)?.Tag as string;
        _emotion.Items.Clear();
        foreach (string action in installed) _emotion.Items.Add(new ComboBoxItem { Content = PetPerformanceCatalog.Label(action), Tag = action });
        int index = Array.IndexOf(installed, selected);
        _emotion.SelectedIndex = installed.Length == 0 ? -1 : Math.Max(0, index);
    }

    private void RefreshPerformanceAvailability()
    {
        _emotion.IsEnabled = _emotion.Items.Count > 0;
        _playEmotion.IsEnabled = _emotion.SelectedItem is ComboBoxItem item && item.Tag is string action && _pet.CanPlayPerformance(action);
        _stopPerformance.IsEnabled = _pet.IsScenePlaying && _pet.CurrentSceneAction is string playing && PetPerformanceCatalog.Names.Contains(playing);
        _emotion.ToolTip = _emotion.Items.Count == 0 ? L.T("当前角色的动画包尚未安装可选表演。", "No selectable performances are installed for this character.") : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pet.SettingsChanged -= Refresh;
        _pet.BehaviorStateChanged -= Refresh;
    }
}
