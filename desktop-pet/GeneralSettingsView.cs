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
    private bool _syncing, _disposed;

    public GeneralSettingsView(PetWindow pet, Action<bool>? setLlmBehavior = null)
    {
        _pet = pet;
        Name = "GeneralSettingsView";
        var body = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        body.Children.Add(Ui.Text("桌宠与互动", 23, "#203E2C", true));
        _role = Note("", "GeneralCurrentRole"); _role.Margin = new Thickness(0, 8, 0, 18);
        body.Children.Add(_role);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(right, 1); columns.Children.Add(left); columns.Children.Add(right); body.Children.Add(columns);

        var desktop = Section(left, "显示与桌面", "GeneralDisplaySection");
        _sizeLabel = Note("", "PetSizeLabel"); desktop.Children.Add(_sizeLabel);
        _slider = new Slider { Name = "PetSize", Minimum = 160, Maximum = 440, TickFrequency = 10,
            IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 12), Foreground = Ui.Brush("#3C6850") };
        _slider.ValueChanged += (_, _) => { if (!_syncing) _pet.SetSize(_slider.Value); };
        AutomationProperties.SetName(_slider, "桌宠显示大小"); desktop.Children.Add(_slider);
        _topmost = Toggle("始终置顶", "PetTopmost", value => _pet.SetTopmost(value)); desktop.Children.Add(_topmost);
        _motion = Toggle("呼吸动画", "PetMotion", value => _pet.SetMotion(value)); desktop.Children.Add(_motion);
        _clickThrough = Toggle("鼠标穿透", "PetClickThrough", value => _pet.SetClickThrough(value)); desktop.Children.Add(_clickThrough);
        desktop.Children.Add(Note("开启穿透后，从托盘打开设置，即可恢复角色互动。", "PetClickThroughHint"));
        var visibilityButtons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        _visibility = Button("", "PetVisibility", (_, _) => { if (_pet.IsVisible) _pet.Hide(); else _pet.Show(); Refresh(); });
        _visibility.Margin = new Thickness(0, 0, 4, 0); visibilityButtons.Children.Add(_visibility);
        var reset = Button("回到屏幕角落", "ResetPetPosition", (_, _) => { _pet.Show(); _pet.ResetPosition(); _pet.Say("我在这里。"); });
        reset.Margin = new Thickness(4, 0, 0, 0); visibilityButtons.Children.Add(reset); desktop.Children.Add(visibilityButtons);

        var movement = Section(right, "行走与行为", "GeneralBehaviorSection");
        _movement = Toggle("桌面行走与窗口互动", "PetMovement", value => _pet.SetMovement(value)); movement.Children.Add(_movement);
        _llmBehavior = Toggle("LLM 行为模式", "LlmBehaviorEnabled", value =>
        {
            if (setLlmBehavior != null) setLlmBehavior(value);
            else { _pet.Settings.LlmBehaviorEnabled = value; _pet.SaveNow(); }
            Refresh();
        });
        movement.Children.Add(_llmBehavior);
        _behaviorHint = Note("", "LlmBehaviorStatus"); movement.Children.Add(_behaviorHint);
        var jump = Button("跳到附近窗口", "JumpNearbyWindow", (_, _) => _pet.JumpToNearbyWindow());
        jump.Margin = new Thickness(0, 12, 0, 0); movement.Children.Add(jump);
        var climbButtons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
        _climbLeft = Button("向左攀爬", "ClimbLeft", (_, _) => _pet.ClimbNearbyWindow(-1)); _climbLeft.Margin = new Thickness(0, 0, 4, 0);
        _climbRight = Button("向右攀爬", "ClimbRight", (_, _) => _pet.ClimbNearbyWindow(1)); _climbRight.Margin = new Thickness(4, 0, 0, 0);
        climbButtons.Children.Add(_climbLeft); climbButtons.Children.Add(_climbRight); movement.Children.Add(climbButtons);
        movement.Children.Add(Note("把我放在窗口侧边附近，朝窗沿攀爬后会自动翻上顶边。", "PetClimbHint"));

        var music = Section(left, "音乐与唱歌", "GeneralMusicSection");
        _sound = Toggle("音乐开关", "SoundEnabled", value => _pet.SetSoundEnabled(value)); music.Children.Add(_sound);
        _volumeLabel = Note("", "SoundVolumeLabel"); music.Children.Add(_volumeLabel);
        _volume = new Slider { Name = "SoundVolume", Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 8, 0, 12), Foreground = Ui.Brush("#3C6850") };
        AutomationProperties.SetName(_volume, "音乐音量");
        _volume.ValueChanged += (_, _) => { if (!_syncing) _pet.SetSoundVolume(_volume.Value / 100); }; music.Children.Add(_volume);
        var performances = Section(right, "表演与情绪", "GeneralPerformanceSection");
        _emotion = new ComboBox { Name = "PetEmotion", MinHeight = 34, Margin = new Thickness(0, 4, 0, 10) };
        AutomationProperties.SetName(_emotion, "选择表演与情绪动画"); performances.Children.Add(_emotion);
        _playEmotion = Button("播放所选表演", "PlayPetEmotion", async (_, _) =>
        { if (_emotion.SelectedItem is ComboBoxItem item && item.Tag is string action) await _pet.PreviewSceneAsync(action); });
        var performanceButtons = new UniformGrid { Columns = 2 };
        _playEmotion.Margin = new Thickness(0, 0, 4, 0); performanceButtons.Children.Add(_playEmotion);
        _stopPerformance = Button("停止表演", "StopPetPerformance", (_, _) => { _pet.StopScenePreview(); Refresh(); });
        _stopPerformance.Margin = new Thickness(4, 0, 0, 0); performanceButtons.Children.Add(_stopPerformance);
        performances.Children.Add(performanceButtons);
        _emotion.SelectionChanged += (_, _) => { if (!_syncing) RefreshPerformanceAvailability(); };
        performances.Children.Add(Note("Let Me Go 与保持 Q 弹会播放配套音乐，音量由左侧控制。停止表演后恢复日常状态；开启 LLM 模式后也可对话点播。", "PetPerformanceHint"));

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
            bool live2D = _pet.CurrentLive2DModel != null;
            _role.Text = "当前角色：" + _pet.CurrentDisplayName + " · 设置立即生效，并随桌宠保存。";
            _motion.Content = live2D ? "眨眼与呼吸" : "呼吸动画";
            _motion.ToolTip = live2D ? "驱动模型已有的标准眼睛与呼吸参数；不影响桌面行走与场景动画。系统减少动态时保持静止。" : "只控制站立呼吸；关闭后仍可桌面行走、下落和窗口互动。";
            _sizeLabel.Text = "显示大小    " + (int)_pet.Settings.Size + " px";
            _slider.Value = _pet.Settings.Size;
            _topmost.IsChecked = _pet.Settings.Topmost;
            _motion.IsChecked = _pet.Settings.MotionEnabled;
            _clickThrough.IsChecked = _pet.Settings.ClickThrough;
            _movement.IsChecked = _pet.Settings.MovementEnabled;
            _llmBehavior.IsChecked = _pet.Settings.LlmBehaviorEnabled;
            _behaviorHint.Text = !_pet.Settings.LlmBehaviorEnabled
                ? "默认模式：沿用当前行为，不自动调用模型。"
                : (_pet.IsLlmBehaviorActive ? "LLM 模式已连接。" : _pet.CanLlmReact ? "LLM 可回应互动；动作使用默认逻辑。" : "LLM 暂不可用，继续默认行为并自动重试。")
                  + "点击、拖拽等互动会通知所选 Ollama 模型；可对话请求唱歌、跳舞、敲碗、表情和移动。移动仍需开启上方开关。";
            _sound.IsChecked = _pet.Settings.SoundEnabled;
            _volume.Value = _pet.Settings.SoundVolume * 100;
            _volume.IsEnabled = _pet.Settings.SoundEnabled;
            _volumeLabel.Text = "音量    " + Math.Round(_pet.Settings.SoundVolume * 100) + "%" + (_pet.Settings.SoundEnabled ? "" : " · 已静音");
            RefreshPerformanceChoices(); RefreshPerformanceAvailability();
            _climbLeft.IsEnabled = _climbRight.IsEnabled = _pet.UsesBuiltInScenes && _pet.CompanionSurfacesAvailable;
            _visibility.Content = _pet.IsVisible ? "暂时隐藏桌宠" : "唤回桌宠";
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
        _emotion.ToolTip = _emotion.Items.Count == 0 ? "当前角色的动画包尚未安装可选表演。" : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pet.SettingsChanged -= Refresh;
        _pet.BehaviorStateChanged -= Refresh;
    }
}
