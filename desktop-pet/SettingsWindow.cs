using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DesktopPet;

internal sealed class SettingsWindow : Window
{
    private readonly PetWindow _pet;
    private readonly IReadOnlyList<SkinInfo> _skins;
    private readonly Dictionary<string, Button> _cards = new();
    private readonly Dictionary<string, TextBlock> _labels = new();
    private readonly Image _preview;
    private readonly TextBlock _name;
    private readonly TextBlock _live2DPreview;
    private readonly TextBlock _sizeLabel, _volumeLabel;
    private readonly TextBlock _status;
    private readonly Slider _slider, _volume;
    private readonly CheckBox _topmost, _motion, _clickThrough, _movement, _sound, _llmBehavior;
    private readonly TextBlock _behaviorHint;
    private readonly Button _visibility, _sing, _stopSong, _playEmotion, _climbLeft, _climbRight;
    private readonly ComboBox _emotion;
    private bool _syncing;
    private string? _previewId;

    public SettingsWindow(PetWindow pet, IReadOnlyList<SkinInfo> skins, Action? openDashboard = null, Action? openChat = null, Action<bool>? setLlmBehavior = null)
    {
        _pet = pet;
        _skins = skins;
        Title = AppVersion.WindowTitle("桌面小伙伴");
        Width = 1050; Height = 810; MinWidth = 930; MinHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("#F4F6F0");
        var root = new Grid { Background = Background };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(276) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var side = new Grid { Margin = new Thickness(0), Background = Ui.Brush("#EAF0E6") };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new StackPanel { Margin = new Thickness(26, 25, 24, 0) };
        brand.Children.Add(Ui.Text("AI  PET", 22, "#214332", true));
        var subtitle = Ui.Text("DESKTOP COMPANION", 10, "#7A8D7C");
        subtitle.Margin = new Thickness(1, 5, 0, 0);
        brand.Children.Add(subtitle);
        side.Children.Add(brand);

        var sideBody = new StackPanel { Margin = new Thickness(26, 8, 26, 0) };
        Grid.SetRow(sideBody, 1);
        var previewStage = new Grid { Height = 182, Margin = new Thickness(0, 0, 0, 2) };
        previewStage.Children.Add(new Ellipse { Width = 168, Height = 168, Fill = Ui.Brush("#DDE8D9"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        _preview = new Image { Width = 182, Height = 182, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        previewStage.Children.Add(_preview);
        _live2DPreview = Ui.Text("Live2D\n模型显示在桌面", 17, "#597D61", true);
        _live2DPreview.TextAlignment = TextAlignment.Center; _live2DPreview.VerticalAlignment = VerticalAlignment.Center;
        _live2DPreview.Visibility = Visibility.Collapsed;
        previewStage.Children.Add(_live2DPreview);
        sideBody.Children.Add(previewStage);
        var caption = Ui.Text("当前角色", 11, "#81937F"); caption.TextAlignment = TextAlignment.Center;
        sideBody.Children.Add(caption);
        _name = Ui.Text("", 26, "#193D2D", true); _name.TextAlignment = TextAlignment.Center; _name.Margin = new Thickness(0, 3, 0, 4);
        sideBody.Children.Add(_name);
        _status = Ui.Text("", 12, "#597D61"); _status.TextAlignment = TextAlignment.Center;
        sideBody.Children.Add(_status);
        sideBody.Children.Add(new Border { Height = 1, Background = Ui.Brush("#D3DFCF"), Margin = new Thickness(0, 20, 0, 18) });
        _sizeLabel = Ui.Text("", 12, "#4D6854");
        sideBody.Children.Add(_sizeLabel);
        _slider = new Slider { Minimum = 160, Maximum = 440, TickFrequency = 10, IsSnapToTickEnabled = true, Margin = new Thickness(0, 10, 0, 13), Foreground = Ui.Brush("#3C6850") };
        _slider.ValueChanged += (_, _) => { if (!_syncing) _pet.SetSize(_slider.Value); };
        sideBody.Children.Add(_slider);
        _topmost = Toggle("始终置顶", value => _pet.SetTopmost(value)); sideBody.Children.Add(_topmost);
        _motion = Toggle("呼吸动画", value => _pet.SetMotion(value)); sideBody.Children.Add(_motion);
        _clickThrough = Toggle("鼠标穿透", value => _pet.SetClickThrough(value)); sideBody.Children.Add(_clickThrough);
        _movement = Toggle("桌面行走与窗口互动", value => _pet.SetMovement(value)); sideBody.Children.Add(_movement);
        _llmBehavior = Toggle("LLM 行为模式", value =>
        {
            if (setLlmBehavior != null) setLlmBehavior(value);
            else { _pet.Settings.LlmBehaviorEnabled = value; _pet.SaveNow(); }
            Refresh();
        });
        _llmBehavior.Name = "LlmBehaviorEnabled"; sideBody.Children.Add(_llmBehavior);
        _behaviorHint = Ui.Text("", 10, "#80907E"); _behaviorHint.Name = "LlmBehaviorStatus";
        _behaviorHint.TextWrapping = TextWrapping.Wrap; _behaviorHint.LineHeight = 17;
        sideBody.Children.Add(_behaviorHint);
        var jump = Ui.Button("跳到附近窗口", (_, _) => _pet.JumpToNearbyWindow());
        jump.Margin = new Thickness(0, 8, 0, 0); sideBody.Children.Add(jump);
        var climbButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        _climbLeft = Ui.Button("向左攀爬", (_, _) => _pet.ClimbNearbyWindow(-1)); _climbLeft.Name = "ClimbLeft"; _climbLeft.Margin = new Thickness(0, 0, 3, 0);
        _climbRight = Ui.Button("向右攀爬", (_, _) => _pet.ClimbNearbyWindow(1)); _climbRight.Name = "ClimbRight"; _climbRight.Margin = new Thickness(3, 0, 0, 0);
        climbButtons.Children.Add(_climbLeft); climbButtons.Children.Add(_climbRight); sideBody.Children.Add(climbButtons);
        var climbHint = Ui.Text("把我放在窗口侧边附近，朝窗沿攀爬后会自动翻上顶边。", 10, "#80907E");
        climbHint.TextWrapping = TextWrapping.Wrap; climbHint.LineHeight = 17; climbHint.Margin = new Thickness(0, 5, 0, 0); sideBody.Children.Add(climbHint);
        var clickHint = Ui.Text("开启穿透后，从托盘打开设置\n即可恢复角色互动。", 10, "#80907E");
        clickHint.LineHeight = 17; clickHint.Margin = new Thickness(0, 3, 0, 0);
        sideBody.Children.Add(clickHint);
        sideBody.Children.Add(new Border { Height = 1, Background = Ui.Brush("#D3DFCF"), Margin = new Thickness(0, 16, 0, 12) });
        _sound = Toggle("音乐与声音", value => _pet.SetSoundEnabled(value));
        _sound.Name = "SoundEnabled"; sideBody.Children.Add(_sound);
        _volumeLabel = Ui.Text("", 12, "#4D6854"); _volumeLabel.Margin = new Thickness(0, 6, 0, 0); sideBody.Children.Add(_volumeLabel);
        _volume = new Slider { Name = "SoundVolume", Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 10), Foreground = Ui.Brush("#3C6850") };
        _volume.ValueChanged += (_, _) => { if (!_syncing) _pet.SetSoundVolume(_volume.Value / 100); }; sideBody.Children.Add(_volume);
        var songButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        _sing = Ui.Button("唱 Let Me Go", async (_, _) => await _pet.SingAsync()); _sing.Name = "StartSinging"; _sing.Margin = new Thickness(0, 0, 3, 0);
        _stopSong = Ui.Button("停止唱歌", (_, _) => _pet.StopSinging()); _stopSong.Name = "StopSinging"; _stopSong.Margin = new Thickness(3, 0, 0, 0);
        songButtons.Children.Add(_sing); songButtons.Children.Add(_stopSong); sideBody.Children.Add(songButtons);
        var soundHint = Ui.Text("可手动点唱；开启 LLM 模式后也可对话点唱。拖动、隐藏或锁屏会停止。", 10, "#80907E");
        soundHint.TextWrapping = TextWrapping.Wrap; soundHint.LineHeight = 17; soundHint.Margin = new Thickness(0, 5, 0, 0); sideBody.Children.Add(soundHint);
        sideBody.Children.Add(Ui.Text("表演与情绪", 12, "#4D6854", true));
        _emotion = new ComboBox { Name = "PetEmotion", Margin = new Thickness(0, 6, 0, 6) };
        sideBody.Children.Add(_emotion);
        _playEmotion = Ui.Button("播放所选动画", async (_, _) =>
        { if (_emotion.SelectedItem is ComboBoxItem item && item.Tag is string action) await _pet.PreviewSceneAsync(action); });
        _playEmotion.Name = "PlayPetEmotion"; sideBody.Children.Add(_playEmotion);
        _emotion.SelectionChanged += (_, _) => { if (!_syncing) RefreshPerformanceAvailability(); };
        var performanceHint = Ui.Text("播放一次后恢复日常状态；舞蹈与烟花暂不含声音。", 10, "#80907E");
        performanceHint.TextWrapping = TextWrapping.Wrap; performanceHint.LineHeight = 17;
        performanceHint.Margin = new Thickness(0, 5, 0, 0); sideBody.Children.Add(performanceHint);
        var sideScroll = new ScrollViewer { Content = sideBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(sideScroll, 1); side.Children.Add(sideScroll);

        var bottom = new StackPanel { Margin = new Thickness(26, 12, 26, 23) };
        Grid.SetRow(bottom, 2);
        _visibility = Ui.Button("", (_, _) => { if (_pet.IsVisible) _pet.Hide(); else _pet.Show(); Refresh(); });
        _visibility.Background = Ui.Brush("#214F38"); _visibility.Foreground = Brushes.White; _visibility.BorderThickness = new Thickness(0);
        bottom.Children.Add(_visibility);
        var reset = Ui.Button("回到屏幕角落", (_, _) => { _pet.Show(); _pet.ResetPosition(); _pet.Say("我在这里。"); });
        reset.Margin = new Thickness(0, 8, 0, 0); reset.Background = Brushes.Transparent;
        bottom.Children.Add(reset);
        side.Children.Add(bottom);
        root.Children.Add(side);

        var main = new Grid { Margin = new Thickness(28, 24, 28, 20) };
        Grid.SetColumn(main, 1);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        var headerStack = new StackPanel();
        headerStack.Children.Add(Ui.Text("选一位，陪你工作", 25, "#203E2C", true));
        var desc = Ui.Text("你的 AI 角色，现在住在桌面上。", 13, "#899487");
        desc.Margin = new Thickness(0, 8, 0, 17); headerStack.Children.Add(desc);
        header.Children.Add(headerStack);
        var badge = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 5, 10, 5), Background = Ui.Brush("#E3ECDC"), Child = Ui.Text("9 款皮肤", 11, "#66815C") };
        header.Children.Add(badge);
        main.Children.Add(header);

        var gallery = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3 };
        foreach (var skin in skins)
        {
            var card = new Button { Padding = new Thickness(7), Margin = new Thickness(0, 0, 10, 11), BorderThickness = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            var contents = new Grid();
            contents.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contents.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            var picture = new Image { Source = AssetLoader.Load(skin.ImagePath, 320), Stretch = Stretch.Uniform, Margin = new Thickness(8, 2, 8, 0), MaxHeight = 139 };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            contents.Children.Add(picture);
            var labelRow = new Grid { Margin = new Thickness(6, 4, 6, 0) };
            Grid.SetRow(labelRow, 1);
            var label = Ui.Text(skin.Name, 13, "#526253", true); label.VerticalAlignment = VerticalAlignment.Center; labelRow.Children.Add(label);
            var dot = new Ellipse { Width = 7, Height = 7, Fill = Ui.Brush(skin.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            labelRow.Children.Add(dot); contents.Children.Add(labelRow);
            card.Content = contents;
            card.ToolTip = "切换为 " + skin.Name;
            card.Click += (_, _) => { _pet.SelectSkin(skin.Id); _pet.Show(); _pet.Say("你好，我是 " + skin.Name + "。"); };
            _cards[skin.Id] = card; _labels[skin.Id] = label;
            gallery.Children.Add(card);
        }
        Grid.SetRow(gallery, 1); main.Children.Add(gallery);

        var footer = new Border { CornerRadius = new CornerRadius(12), Background = Ui.Brush("#EAF0E5"), Padding = new Thickness(17, 13, 17, 13), Margin = new Thickness(0, 6, 10, 0) };
        var footerStack = new StackPanel();
        footerStack.Children.Add(Ui.Text("小小操作，很快上手", 12, "#4D6A50", true));
        var tips = Ui.Text("拖动移动  ·  滚轮缩放  ·  双击聊天  ·  右键图标栏", 11, "#7B8D78"); tips.Margin = new Thickness(0, 7, 0, 0); footerStack.Children.Add(tips);
        var version = Ui.VersionLabel(); version.Margin = new Thickness(0, 8, 0, 0); footerStack.Children.Add(version);
        var mediaButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        var live2D = Ui.Button("Live2D 模型与导入…", (_, _) => _pet.OpenLive2DLibrary());
        live2D.Margin = new Thickness(0, 8, 5, 0); mediaButtons.Children.Add(live2D);
        var scenes = Ui.Button("场景动画与导入…", (_, _) => _pet.OpenSceneLibrary());
        scenes.Margin = new Thickness(5, 8, 0, 0); mediaButtons.Children.Add(scenes);
        footerStack.Children.Add(mediaButtons);
        var companionButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = openDashboard != null && openChat != null ? 2 : 1 };
        if (openChat != null) { var chat = Ui.Button("和桌宠聊天 →", (_, _) => openChat()); chat.Name = "OpenPetChat"; chat.Margin = new Thickness(0, 8, 5, 0); companionButtons.Children.Add(chat); }
        if (openDashboard != null) { var dashboard = Ui.Button("Codex 工作看板 →", (_, _) => openDashboard()); dashboard.Margin = new Thickness(0, 8, 0, 0); companionButtons.Children.Add(dashboard); }
        footerStack.Children.Add(companionButtons);
        footer.Child = footerStack; Grid.SetRow(footer, 2); main.Children.Add(footer);
        root.Children.Add(main);
        Content = root;
        _pet.SettingsChanged += Refresh;
        _pet.BehaviorStateChanged += Refresh;
        Closed += (_, _) => { _pet.SettingsChanged -= Refresh; _pet.BehaviorStateChanged -= Refresh; };
        Refresh();
    }

    private CheckBox Toggle(string label, Action<bool> setter)
    {
        var toggle = new CheckBox { Content = label };
        toggle.Checked += (_, _) => { if (!_syncing) setter(true); };
        toggle.Unchecked += (_, _) => { if (!_syncing) setter(false); };
        return toggle;
    }

    private void Refresh()
    {
        _syncing = true;
        try
        {
            if (_previewId != _pet.CurrentSkin.Id) { _preview.Source = AssetLoader.Load(_pet.CurrentSkin.ImagePath, 420); _previewId = _pet.CurrentSkin.Id; }
            bool live2D = _pet.CurrentLive2DModel != null;
            _preview.Visibility = live2D ? Visibility.Collapsed : Visibility.Visible;
            _live2DPreview.Visibility = live2D ? Visibility.Visible : Visibility.Collapsed;
            _name.Text = _pet.CurrentDisplayName;
            _name.FontSize = live2D ? 18 : 26;
            _motion.IsEnabled = true;
            _motion.Content = live2D ? "眨眼与呼吸" : "呼吸动画";
            _motion.ToolTip = live2D ? "驱动模型已有的标准眼睛与呼吸参数；不影响桌面行走与场景动画。系统减少动态时保持静止。" : "只控制站立呼吸；关闭后仍可桌面行走、下落和窗口互动。";
            _status.Text = _pet.IsVisible ? (_pet.Settings.ClickThrough ? "●  安静陪伴 · 已穿透" : "●  在桌面陪伴你") : "○  暂时休息中";
            _sizeLabel.Text = "显示大小    " + (int)_pet.Settings.Size + " px";
            _slider.Value = _pet.Settings.Size;
            _topmost.IsChecked = _pet.Settings.Topmost;
            _motion.IsChecked = _pet.Settings.MotionEnabled;
            _clickThrough.IsChecked = _pet.Settings.ClickThrough;
            _movement.IsChecked = _pet.Settings.MovementEnabled;
            _llmBehavior.IsChecked = _pet.Settings.LlmBehaviorEnabled;
            _behaviorHint.Text = !_pet.Settings.LlmBehaviorEnabled
                ? "默认模式：沿用当前行为，不自动调用模型。"
                : (_pet.IsLlmBehaviorActive ? "LLM 模式已连接。" : "LLM 暂不可用，继续默认行为并自动重试。")
                  + "点击、拖拽等互动会通知所选 Ollama 模型；可对话请求唱歌、跳舞、敲碗、表情和移动。移动仍需开启上方开关。";
            _sound.IsChecked = _pet.Settings.SoundEnabled;
            _volume.Value = _pet.Settings.SoundVolume * 100;
            _volume.IsEnabled = _pet.Settings.SoundEnabled;
            _volumeLabel.Text = "音量    " + Math.Round(_pet.Settings.SoundVolume * 100) + "%" + (_pet.Settings.SoundEnabled ? "" : " · 已静音");
            _sing.IsEnabled = _pet.CanSing && !_pet.IsSinging;
            _stopSong.IsEnabled = _pet.IsSinging;
            RefreshPerformanceChoices();
            RefreshPerformanceAvailability();
            _climbLeft.IsEnabled = _climbRight.IsEnabled = _pet.UsesBuiltInScenes && _pet.CompanionSurfacesAvailable;
            _visibility.Content = _pet.IsVisible ? "暂时隐藏桌宠" : "唤回桌宠";
            foreach (var pair in _cards)
            {
                bool selected = !live2D && pair.Key == _pet.CurrentSkin.Id;
                pair.Value.Background = Ui.Brush(selected ? "#EAF3E5" : "#FFFFFF");
                pair.Value.BorderBrush = Ui.Brush(selected ? "#668D67" : "#E5EADF");
                _labels[pair.Key].Foreground = Ui.Brush(selected ? "#2B563B" : "#526253");
            }
        }
        finally { _syncing = false; }
    }

    private void RefreshPerformanceChoices()
    {
        var installed = PetPerformanceCatalog.Installed(_pet.CurrentScenePack).Where(action => action != "sing").ToArray();
        var current = _emotion.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string).ToArray();
        if (current.SequenceEqual(installed)) return;
        string? selected = (_emotion.SelectedItem as ComboBoxItem)?.Tag as string;
        _emotion.Items.Clear();
        foreach (string action in installed)
            _emotion.Items.Add(new ComboBoxItem { Content = PetPerformanceCatalog.Label(action), Tag = action });
        int index = Array.IndexOf(installed, selected);
        _emotion.SelectedIndex = installed.Length == 0 ? -1 : Math.Max(0, index);
    }

    private void RefreshPerformanceAvailability()
    {
        _emotion.IsEnabled = _emotion.Items.Count > 0;
        _playEmotion.IsEnabled = _emotion.SelectedItem is ComboBoxItem item && item.Tag is string action && _pet.CanPlayPerformance(action);
        _emotion.ToolTip = _emotion.Items.Count == 0 ? "当前角色的动画包尚未安装可选表演。" : null;
    }

    public void SavePreview(string path)
    {
        UpdateLayout();
        if (Content is not FrameworkElement root) return;
        var target = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path); png.Save(stream);
    }
}
