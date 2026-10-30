using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopPet;

// Owns model/voice settings independently of the Codex monitor or wardrobe.
// Merely constructing this view never starts a poll or loads a model.
internal sealed partial class ModelSettingsView : UserControl, IDisposable
{
    private readonly DesktopSettings _settings;
    private readonly PetWindow? _speechHost;
    private bool _rendering, _closed;
    private Window? _hostWindow;
    private readonly CheckBox _speechEnabled = new() { Name = "SpeechEnabled" };
    private readonly Slider _speechVolume = new() { Name = "SpeechVolume", Minimum = 0, Maximum = 100, Width = 140, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly TextBlock _speechLevel = Ui.Text("60%", 11, "#647A5C");
    private readonly TextBlock _speechStatus = Ui.Text("", 10, "#71826D");
    private readonly Button _speechPreview = new() { Name = "SpeechPreview", Content = L.T("试听", "Audition") };
    private readonly Button _speechPreload = new() { Name = "SpeechPreload", Content = L.T("预加载语音", "Preload speech") };
    private readonly Button _speechUnload = new() { Name = "SpeechUnload", Content = L.T("卸载语音", "Unload speech") };
    private bool _speechModelOperation;
    private readonly Button _speechStop = new() { Name = "SpeechStop", Content = L.T("停止朗读", "Stop speech") };

    internal ModelSettingsView(DesktopSettings settings, PetWindow? speechHost = null,
        DashboardModelServices? modelServices = null, IPetVoiceStudio? voiceStudio = null)
    {
        _settings = settings; _speechHost = speechHost;
        _modelServices = modelServices ?? DashboardModelServices.Create(speechHost);
        _voiceStudio = voiceStudio ?? (speechHost == null ? null : new PetVoiceStudio(speechHost));
        _rendering = true;
        BuildModelPage();
        _rendering = false;
        if (_speechHost != null) _speechHost.SettingsChanged += SpeechSettingsChanged;
        Loaded += ViewLoaded;
        Unloaded += ViewUnloaded;
        IsVisibleChanged += ViewVisibilityChanged;
        RenderSpeechControls(); RenderModelButtons();
    }

    private void ViewLoaded(object sender, RoutedEventArgs args)
    {
        if (_closed) return;
        var host = Window.GetWindow(this);
        if (!ReferenceEquals(host, _hostWindow))
        {
            DetachHost(); _hostWindow = host;
            if (_hostWindow != null) _hostWindow.StateChanged += HostStateChanged;
        }
        UpdatePageActivity();
    }

    private void ViewUnloaded(object sender, RoutedEventArgs args)
    {
        _showModelPage = false; PauseModelReads();
        if (!_closed) StopVoiceStudioPlayback();
        DetachHost();
    }

    private void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdatePageActivity();
    private void HostStateChanged(object? sender, EventArgs args) => UpdatePageActivity();

    private void UpdatePageActivity()
    {
        if (_closed) return;
        bool active = IsLoaded && IsVisible && _hostWindow != null && _hostWindow.WindowState != WindowState.Minimized;
        if (_showModelPage == active) return;
        _showModelPage = active;
        if (active)
        {
            RenderSpeechControls(); RenderModelButtons();
            _modelTimer.Start(); _ = RefreshModelPageAsync();
        }
        else { PauseModelReads(); StopVoiceStudioPlayback(); }
    }

    private void DetachHost()
    {
        if (_hostWindow != null) _hostWindow.StateChanged -= HostStateChanged;
        _hostWindow = null;
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_closed) return;
        _closed = true; _showModelPage = false;
        Loaded -= ViewLoaded; Unloaded -= ViewUnloaded; IsVisibleChanged -= ViewVisibilityChanged;
        DetachHost();
        if (_speechHost != null) _speechHost.SettingsChanged -= SpeechSettingsChanged;
        CloseModelPage(); CloseVoiceStudio();
    }

    private Border BuildSpeechPreferences()
    {
        var speechPanel = new StackPanel { Margin = new Thickness(0) };
        var speechRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        _speechEnabled.Content = L.T("桌宠语音播报", "Pet speech");
        _speechEnabled.VerticalAlignment = VerticalAlignment.Center; _speechEnabled.Margin = new Thickness(0, 0, 20, 0);
        _speechEnabled.ToolTip = L.T("朗读点击回应、预设提示和本地模型回答；新的一条话立即打断旧播报，同一回答内连续播放。与唱歌音量分别控制。", "Read click reactions, preset notices, and local model replies aloud. New messages interrupt earlier speech; sentences within one reply play continuously. Speech and music have separate volume controls.");
        _speechEnabled.Checked += (_, _) => { if (!_rendering) _speechHost?.SetSpeechEnabled(true); };
        _speechEnabled.Unchecked += (_, _) => { if (!_rendering) _speechHost?.SetSpeechEnabled(false); };
        speechRow.Children.Add(_speechEnabled);
        var volumeLabel = Ui.Text(L.T("语音音量", "Speech volume"), 11, "#647A5C"); volumeLabel.VerticalAlignment = VerticalAlignment.Center;
        volumeLabel.Margin = new Thickness(0, 0, 8, 0); speechRow.Children.Add(volumeLabel);
        _speechVolume.VerticalAlignment = VerticalAlignment.Center;
        _speechVolume.ValueChanged += (_, _) => { if (!_rendering) _speechHost?.SetSpeechVolume(_speechVolume.Value / 100); };
        AutomationProperties.SetName(_speechVolume, L.T("桌宠语音音量", "Pet speech volume")); speechRow.Children.Add(_speechVolume);
        _speechLevel.Width = 42; _speechLevel.Margin = new Thickness(8, 0, 14, 0); _speechLevel.VerticalAlignment = VerticalAlignment.Center;
        speechRow.Children.Add(_speechLevel);
        _speechPreview.Margin = new Thickness(0, 0, 8, 0); _speechPreview.MinWidth = 64;
        _speechPreview.Click += (_, _) => _speechHost?.PreviewSpeech(); speechRow.Children.Add(_speechPreview);
        _speechStop.MinWidth = 90; _speechStop.Click += (_, _) => _speechHost?.StopSpeech(); speechRow.Children.Add(_speechStop);
        _speechPreload.Margin = new Thickness(8, 0, 8, 0);
        _speechPreload.ToolTip = L.T("提前加载当前角色保存的声音模型；不加载或修改 Ollama 聊天模型", "Preload the current character's speech model without loading or changing the Ollama chat model");
        _speechUnload.ToolTip = L.T("停止播报并释放语音模型显存；下一次主动播报或预加载时再加载", "Stop speech and release its GPU memory; load again on the next speech request or preload");
        _speechPreload.Click += async (_, _) => await ChangeSpeechModelLoadAsync(true);
        _speechUnload.Click += async (_, _) => await ChangeSpeechModelLoadAsync(false);
        speechRow.Children.Add(_speechPreload); speechRow.Children.Add(_speechUnload);
        speechPanel.Children.Add(speechRow);
        _speechStatus.Name = "SpeechStatus"; _speechStatus.TextWrapping = TextWrapping.Wrap;
        _speechStatus.Margin = new Thickness(0, 5, 0, 0); speechPanel.Children.Add(_speechStatus);
        var card = Card(speechPanel); card.Margin = new Thickness(0, 0, 0, 14);
        return card;
    }

    private void SpeechSettingsChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(SpeechSettingsChanged)); return; }
        VoiceProfileSelectionChanged();
        RenderSpeechControls();
        RenderModelButtons();
    }

    private void RenderSpeechControls()
    {
        bool wasRendering = _rendering;
        _rendering = true;
        try
        {
            _speechEnabled.IsChecked = _settings.SpeechEnabled;
            _speechEnabled.IsEnabled = _speechHost != null;
            _speechVolume.Value = _settings.SpeechVolume * 100;
            _speechVolume.IsEnabled = _speechHost != null && _settings.SpeechEnabled;
            _speechLevel.Text = (_settings.SpeechVolume * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            _speechPreview.IsEnabled = _speechHost != null && _settings.SpeechEnabled && _settings.SpeechVolume > 0 && PetVoiceProfiles.IsEnabled(_settings.GetVoiceProfile(_settings.SkinId));
            _speechStop.IsEnabled = _speechHost != null;
            _speechPreview.IsEnabled = _speechPreview.IsEnabled && !VoiceStudioOwnsAudio;
            _speechPreload.IsEnabled = !VoiceStudioOwnsAudio && !_speechModelOperation && _speechHost != null && _settings.SpeechEnabled && PetVoiceProfiles.IsEnabled(_settings.GetVoiceProfile(_settings.SkinId));
            _speechUnload.IsEnabled = !VoiceStudioOwnsAudio && !_speechModelOperation && _speechHost != null;
            _speechStatus.Text = L.T("本地音色合成 · ", "Local speech synthesis · ") + (_speechHost?.SpeechStatusText ?? L.T("请从桌宠打开设置使用语音", "Open Settings from the pet to use speech")) + L.T(" · 唱歌、锁屏时暂停，不补读旧消息", " · Paused during singing or screen lock; earlier messages are not replayed");
        }
        finally { _rendering = wasRendering; }
    }

    private async System.Threading.Tasks.Task ChangeSpeechModelLoadAsync(bool load)
    {
        if (_speechModelOperation || _speechHost == null || VoiceStudioOwnsAudio) return;
        InvalidateModelRead();
        _speechModelOperation = true; RenderSpeechControls(); RenderModelButtons();
        _voiceState.Text = load ? L.T("加载中", "Loading") : L.T("卸载中", "Unloading");
        _voiceModelAction.Text = load ? L.T("正在加载当前角色的声音模型…", "Loading the current character's speech model…") : L.T("正在停止播报并释放语音显存…", "Stopping speech and releasing GPU memory…");
        string? failure = null;
        try
        {
            if (load) await _speechHost.PreloadSpeechModelAsync();
            else await _speechHost.UnloadSpeechModelAsync();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            failure = L.T("语音模型操作未完成：", "Speech model operation failed: ") + ex.Message;
        }
        finally
        {
            _speechModelOperation = false;
            if (!_closed)
            {
                RenderSpeechControls(); RenderModelButtons();
                if (failure != null) _speechStatus.Text = failure;
                _voiceModelAction.Text = failure ?? (_speechHost.SpeechStatusText);
                if (_showModelPage) await RefreshModelPageAsync();
            }
        }
    }

    private static Border Card(UIElement child, string color = "#FFFFFF") => new()
    {
        Background = Ui.Brush(color), BorderBrush = Ui.Brush("#E5EADF"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(15), Padding = new Thickness(19, 16, 19, 15), Child = child
    };

    private static void ConfigureCombo(ComboBox combo)
    {
        combo.FontFamily = new FontFamily("Microsoft YaHei UI"); combo.FontSize = 12; combo.Padding = new Thickness(8, 5, 8, 5);
        combo.Foreground = Ui.Brush("#46613E"); combo.Background = Brushes.White;
        combo.BorderBrush = Ui.Brush("#DCE5D6"); combo.BorderThickness = new Thickness(1);
    }
}
