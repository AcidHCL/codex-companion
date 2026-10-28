using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopPet;

internal static class Ui
{
    public static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
    public static TextBlock Text(string text, double size = 14, string color = "#21392F", bool bold = false)
        => new() { Text = text, FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontFamily = new FontFamily("Microsoft YaHei UI") };
    public static TextBlock VersionLabel(bool productName = true, string color = "#61715D", double size = 11)
    {
        var label = Text(productName ? AppVersion.ProductLabel : AppVersion.Label, size, color);
        label.Name = "AppVersionLabel";
        label.ToolTip = AppVersion.ProductLabel;
        System.Windows.Automation.AutomationProperties.SetName(label, "AI Pet 版本 " + AppVersion.Number);
        return label;
    }
    public static Button Button(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text };
        button.Click += click;
        return button;
    }

    public static ResourceDictionary CreateResources()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style TargetType="Window">
    <Setter Property="FontFamily" Value="Microsoft YaHei UI"/>
  </Style>
  <Style TargetType="Button">
    <Setter Property="FontFamily" Value="Microsoft YaHei UI"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Foreground" Value="#294739"/>
    <Setter Property="Background" Value="#FFFFFF"/>
    <Setter Property="BorderBrush" Value="#DCE5DB"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="14,9"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="12" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="chrome" Property="Opacity" Value="0.82"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="chrome" Property="Opacity" Value="0.65"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="chrome" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="CheckBox">
    <Setter Property="FontFamily" Value="Microsoft YaHei UI"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Foreground" Value="#385346"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Margin" Value="0,7,0,7"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="36"/></Grid.ColumnDefinitions>
            <ContentPresenter Grid.Column="0" VerticalAlignment="Center"/>
            <Border x:Name="track" Grid.Column="1" Width="34" Height="20" Background="#CFD8CE" CornerRadius="10">
              <Ellipse x:Name="thumb" Width="14" Height="14" Fill="White" HorizontalAlignment="Left" Margin="3"/>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="track" Property="Background" Value="#39644D"/>
              <Setter TargetName="thumb" Property="HorizontalAlignment" Value="Right"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="track" Property="Opacity" Value="0.8"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }
}
