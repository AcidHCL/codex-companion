using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet;

// One settings window owns the existing pet settings and model controls. Views
// stay mounted across navigation so unsaved voice drafts are not discarded.
internal sealed class AppSettingsWindow : Window
{
    private readonly PetWindow _pet;
    private readonly GeneralSettingsView _general;
    private readonly ModelSettingsView _models;
    private readonly Button _generalNav, _modelNav;
    private readonly TextBlock _currentRole;

    internal AppSettingsWindow(PetWindow pet, Action? openSkins = null, Action? openDashboard = null,
        Action? openChat = null, Action<bool>? setLlmBehavior = null,
        DashboardModelServices? modelServices = null, IPetVoiceStudio? voiceStudio = null)
    {
        _pet = pet;
        Title = AppVersion.WindowTitle("设置");
        Width = 1220; Height = 860; MinWidth = 1060; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("#F4F6F0");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(202) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var navigation = new Grid { Background = Ui.Brush("#EAF0E6"), Margin = new Thickness(0) };
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navigation.RowDefinitions.Add(new RowDefinition());
        navigation.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new StackPanel { Margin = new Thickness(24, 28, 18, 0) };
        brand.Children.Add(Ui.Text("AI PET", 23, "#214332", true));
        var subtitle = Ui.Text("偏好与模型", 12, "#71826D"); subtitle.Margin = new Thickness(0, 8, 0, 0);
        brand.Children.Add(subtitle); navigation.Children.Add(brand);
        var choices = new StackPanel { Margin = new Thickness(14, 30, 14, 0) };
        Grid.SetRow(choices, 1);
        _generalNav = Navigation("桌宠与互动", "SettingsNavGeneral", () => SelectPage(false));
        _modelNav = Navigation("模型与语音", "SettingsNavModels", () => SelectPage(true));
        choices.Children.Add(_generalNav); choices.Children.Add(_modelNav); navigation.Children.Add(choices);
        var footer = new StackPanel { Margin = new Thickness(24, 18, 18, 24) };
        _currentRole = Ui.Text("", 12, "#597D61", true); _currentRole.Name = "SettingsCurrentRole";
        _currentRole.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_currentRole);
        var version = Ui.VersionLabel(); version.Margin = new Thickness(0, 10, 0, 0); footer.Children.Add(version);
        Grid.SetRow(footer, 2); navigation.Children.Add(footer); root.Children.Add(navigation);

        var main = new Grid { Margin = new Thickness(24, 24, 24, 20) };
        Grid.SetColumn(main, 1);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 22) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AddLink(actions, "角色换装", "SettingsOpenSkins", openSkins);
        AddLink(actions, "工作看板", "SettingsOpenDashboard", openDashboard);
        AddLink(actions, "聊天", "SettingsOpenChat", openChat);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        header.Children.Add(Ui.Text("设置", 28, "#203E2C", true)); main.Children.Add(header);
        var pages = new Grid { Name = "SettingsPages" }; Grid.SetRow(pages, 1);
        _general = new GeneralSettingsView(pet, setLlmBehavior);
        _models = new ModelSettingsView(pet.Settings, pet, modelServices, voiceStudio) { Visibility = Visibility.Collapsed };
        pages.Children.Add(_general); pages.Children.Add(_models); main.Children.Add(pages);
        root.Children.Add(main); Content = root;
        SelectPage(false); RefreshRole();
        _pet.SettingsChanged += RefreshRole;
        Closed += (_, _) =>
        {
            _pet.SettingsChanged -= RefreshRole;
            _models.Dispose(); _general.Dispose();
        };
    }

    private static Button Navigation(string label, string name, Action select)
    {
        var button = Ui.Button(label, (_, _) => select()); button.Name = name;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(16, 12, 10, 12); button.Margin = new Thickness(0, 0, 0, 10);
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static void AddLink(Panel actions, string label, string name, Action? action)
    {
        if (action == null) return;
        var button = Ui.Button(label, (_, _) => action()); button.Name = name;
        button.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(button);
    }

    private void SelectPage(bool models)
    {
        _general.Visibility = models ? Visibility.Collapsed : Visibility.Visible;
        _models.Visibility = models ? Visibility.Visible : Visibility.Collapsed;
        MarkNavigation(_generalNav, !models); MarkNavigation(_modelNav, models);
    }

    private static void MarkNavigation(Button button, bool selected)
    {
        button.Background = selected ? Ui.Brush("#D8E7D1") : Brushes.Transparent;
        button.BorderBrush = selected ? Ui.Brush("#B3CBA9") : Brushes.Transparent;
        button.Foreground = Ui.Brush(selected ? "#244A34" : "#71826D");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        AutomationProperties.SetItemStatus(button, selected ? "当前页面" : "");
    }

    private void RefreshRole() => _currentRole.Text = "当前角色\n" + _pet.CurrentDisplayName;
    internal void ModelSelectionChanged() => _models.ModelSelectionChanged();

    internal void SavePreview(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
