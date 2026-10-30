using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DesktopPet;

internal sealed class SettingsWindow : Window
{
    private readonly PetWindow _pet;
    private readonly Dictionary<string, Button> _cards = new();
    private readonly Dictionary<string, TextBlock> _labels = new();
    private readonly Image _preview;
    private readonly TextBlock _name;
    private readonly TextBlock _live2DPreview;
    private readonly TextBlock _status;
    private string? _previewId;

    public SettingsWindow(PetWindow pet, IReadOnlyList<SkinInfo> skins, Action? openDashboard = null, Action? openChat = null, Action<bool>? setLlmBehavior = null, Action? openSettings = null)
    {
        _pet = pet;
        Title = AppVersion.WindowTitle(L.T("角色换装", "Character wardrobe"));
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

        var sideBody = new StackPanel { Margin = new Thickness(20, 32, 20, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(sideBody, 1);
        var previewStage = new Grid { Height = 260, Margin = new Thickness(0, 0, 0, 2) };
        previewStage.Children.Add(new Ellipse { Width = 218, Height = 218, Fill = Ui.Brush("#DDE8D9"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        _preview = new Image { Width = 236, Height = 260, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        previewStage.Children.Add(_preview);
        _live2DPreview = Ui.Text(L.T("Live2D\n模型显示在桌面", "Live2D\nModel on your desktop"), 17, "#597D61", true);
        _live2DPreview.TextAlignment = TextAlignment.Center; _live2DPreview.VerticalAlignment = VerticalAlignment.Center;
        _live2DPreview.Visibility = Visibility.Collapsed;
        previewStage.Children.Add(_live2DPreview);
        sideBody.Children.Add(previewStage);
        var caption = Ui.Text(L.T("当前角色", "Current character"), 11, "#81937F"); caption.TextAlignment = TextAlignment.Center;
        sideBody.Children.Add(caption);
        _name = Ui.Text("", 26, "#193D2D", true); _name.TextAlignment = TextAlignment.Center; _name.Margin = new Thickness(0, 3, 0, 4);
        sideBody.Children.Add(_name);
        _status = Ui.Text("", 12, "#597D61"); _status.TextAlignment = TextAlignment.Center;
        sideBody.Children.Add(_status);
        side.Children.Add(sideBody);
        var bottom = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        Grid.SetRow(bottom, 2);
        var settingsHint = Ui.Text(L.T("尺寸、桌面互动、音乐与声音等设置，已统一放到设置页面。", "Size, desktop interaction, music, and speech are now together in Settings."), 11, "#71826D");
        settingsHint.TextWrapping = TextWrapping.Wrap; settingsHint.LineHeight = 19;
        settingsHint.Margin = new Thickness(0, 0, 0, 12); bottom.Children.Add(settingsHint);
        var settings = Ui.Button(L.T("设置", "Settings"), (_, _) => openSettings?.Invoke()); settings.Name = "OpenSettings";
        settings.IsEnabled = openSettings != null;
        settings.Background = Ui.Brush("#214F38"); settings.Foreground = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetName(settings, L.T("打开独立设置页面", "Open the dedicated settings window"));
        bottom.Children.Add(settings); side.Children.Add(bottom);
        root.Children.Add(side);

        var main = new Grid { Margin = new Thickness(28, 24, 28, 20) };
        Grid.SetColumn(main, 1);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        var headerStack = new StackPanel();
        headerStack.Children.Add(Ui.Text(L.T("选一位，陪你工作", "Choose your companion"), 25, "#203E2C", true));
        var desc = Ui.Text(L.T("你的 AI 角色，现在住在桌面上。", "Your AI characters, now on your desktop."), 13, "#899487");
        desc.Margin = new Thickness(0, 8, 0, 17); headerStack.Children.Add(desc);
        header.Children.Add(headerStack);
        var badge = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 5, 10, 5), Background = Ui.Brush("#E3ECDC"), Child = Ui.Text(L.T("9 款皮肤", "9 skins"), 11, "#66815C") };
        header.Children.Add(badge);
        main.Children.Add(header);

        var gallery = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3 };
        foreach (var skin in skins)
        {
            var card = new Button { Name = "SkinCard_" + skin.Id, Padding = new Thickness(7), Margin = new Thickness(0, 0, 10, 11), BorderThickness = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
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
            card.ToolTip = L.T("切换为 ", "Switch to ") + skin.Name;
            card.Click += (_, _) => { _pet.SelectSkin(skin.Id); _pet.Show(); _pet.Say(L.T("你好，我是 ", "Hello, I'm ") + skin.Name + L.T("。", ".")); };
            _cards[skin.Id] = card; _labels[skin.Id] = label;
            gallery.Children.Add(card);
        }
        Grid.SetRow(gallery, 1); main.Children.Add(gallery);

        var footer = new Border { CornerRadius = new CornerRadius(12), Background = Ui.Brush("#EAF0E5"), Padding = new Thickness(17, 13, 17, 13), Margin = new Thickness(0, 6, 10, 0) };
        var footerStack = new StackPanel();
        footerStack.Children.Add(Ui.Text(L.T("小小操作，很快上手", "Simple controls"), 12, "#4D6A50", true));
        var tips = Ui.Text(L.T("左键表情，连点生气  ·  拖动移动  ·  右键聊天", "Left-click to react; repeat to annoy · Drag to move · Right-click for chat"), 11, "#7B8D78"); tips.TextWrapping = TextWrapping.Wrap; tips.Margin = new Thickness(0, 7, 0, 0); footerStack.Children.Add(tips);
        var version = Ui.VersionLabel(); version.Margin = new Thickness(0, 8, 0, 0); footerStack.Children.Add(version);
        var mediaButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        var live2D = Ui.Button(L.T("Live2D 模型与导入…", "Live2D models & import…"), (_, _) => _pet.OpenLive2DLibrary());
        live2D.Margin = new Thickness(0, 8, 5, 0); mediaButtons.Children.Add(live2D);
        var scenes = Ui.Button(L.T("场景动画与导入…", "Scene animations & import…"), (_, _) => _pet.OpenSceneLibrary());
        scenes.Margin = new Thickness(5, 8, 0, 0); mediaButtons.Children.Add(scenes);
        footerStack.Children.Add(mediaButtons);
        var companionButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = openDashboard != null && openChat != null ? 2 : 1 };
        if (openChat != null) { var chat = Ui.Button(L.T("和桌宠聊天 →", "Chat with your pet →"), (_, _) => openChat()); chat.Name = "OpenPetChat"; chat.Margin = new Thickness(0, 8, 5, 0); companionButtons.Children.Add(chat); }
        if (openDashboard != null) { var dashboard = Ui.Button(L.T("Codex 工作看板 →", "Codex work dashboard →"), (_, _) => openDashboard()); dashboard.Margin = new Thickness(0, 8, 0, 0); companionButtons.Children.Add(dashboard); }
        footerStack.Children.Add(companionButtons);
        footer.Child = footerStack; Grid.SetRow(footer, 2); main.Children.Add(footer);
        root.Children.Add(main);
        Content = root;
        _pet.SettingsChanged += Refresh;
        _pet.BehaviorStateChanged += Refresh;
        Closed += (_, _) => { _pet.SettingsChanged -= Refresh; _pet.BehaviorStateChanged -= Refresh; };
        Refresh();
    }

    private void Refresh()
    {
        if (_previewId != _pet.CurrentSkin.Id) { _preview.Source = AssetLoader.Load(_pet.CurrentSkin.ImagePath, 420); _previewId = _pet.CurrentSkin.Id; }
        bool live2D = _pet.CurrentLive2DModel != null;
        _preview.Visibility = live2D ? Visibility.Collapsed : Visibility.Visible;
        _live2DPreview.Visibility = live2D ? Visibility.Visible : Visibility.Collapsed;
        _name.Text = _pet.CurrentDisplayName;
        _name.FontSize = live2D ? 18 : 26;
        _status.Text = _pet.IsVisible ? (_pet.Settings.ClickThrough ? L.T("●  安静陪伴 · 已穿透", "●  Quiet company · Click-through") : L.T("●  在桌面陪伴你", "●  Here on your desktop")) : L.T("○  暂时休息中", "○  Taking a break");
        foreach (var pair in _cards)
        {
            bool selected = !live2D && pair.Key == _pet.CurrentSkin.Id;
            pair.Value.Background = Ui.Brush(selected ? "#EAF3E5" : "#FFFFFF");
            pair.Value.BorderBrush = Ui.Brush(selected ? "#668D67" : "#E5EADF");
            _labels[pair.Key].Foreground = Ui.Brush(selected ? "#2B563B" : "#526253");
        }
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
