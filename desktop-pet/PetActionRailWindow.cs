using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DesktopPet;

// New features can register another action without changing the pet renderer.
internal sealed record PetQuickAction(string Id, string Label, string Glyph, Action Execute);

internal sealed class PetActionRailWindow : Window
{
    internal PetActionRailWindow(IEnumerable<PetQuickAction> actions)
    {
        Title = AppVersion.WindowTitle("桌宠快捷操作");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Width = 78;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var list = new StackPanel { Margin = new Thickness(5) };
        var buttonStyle = CreateButtonStyle();
        foreach (var action in actions)
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = action.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5) });
            content.Children.Add(new TextBlock { Text = action.Label, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Name = "PetAction_" + action.Id, Content = content, Style = buttonStyle, Height = 55,
                ToolTip = action.Label, Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand };
            AutomationProperties.SetName(button, action.Label);
            button.Click += (_, _) => action.Execute();
            list.Children.Add(button);
        }
        var version = Ui.VersionLabel(false, "#789078", 8);
        version.HorizontalAlignment = HorizontalAlignment.Center;
        version.Margin = new Thickness(0, 5, 0, 2);
        list.Children.Add(version);
        Content = new Border { Margin = new Thickness(5), Background = Ui.Brush("#FAFCF7"), BorderBrush = Ui.Brush("#D9E5D4"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = list,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 9, ShadowDepth = 1, Opacity = .12 } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Deactivated += (_, _) => Hide();
    }

    private static Style CreateButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Brush("#365D43")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("#E5EFDE")));
        style.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("#E5EFDE")));
        style.Triggers.Add(focus);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("#CDDFC5")));
        style.Triggers.Add(pressed);
        return style;
    }
}
