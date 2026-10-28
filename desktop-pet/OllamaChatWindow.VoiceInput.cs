using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ShapePath = System.Windows.Shapes.Path;

namespace DesktopPet;

internal sealed partial class OllamaChatWindow
{
    private const string MicrophoneGeometry = "M 6,3 A 2,2 0 0 1 10,3 L 10,8 A 2,2 0 0 1 6,8 Z M 4,7 L 4,8 A 4,4 0 0 0 12,8 L 12,7 M 8,12 L 8,15 M 5,15 L 11,15";
    private PetVoiceInputSession? _voiceInput;
    private readonly Action<bool>? _voiceInputActive;
    private Button? _microphone;
    private TextBlock? _voiceInputStatus;
    private string? _voiceConversation, _voiceEndpoint, _voicePersona, _voiceModel;
    private bool _reportedVoiceInputActive, _voiceEventsSubscribed, _voiceInputMessageVisible;
    private long _voiceSafetyEpoch, _recordingSafetyEpoch;
    private string? _voiceInputResultHint;
    private bool VoiceInputBusy => _voiceInput?.State is PetVoiceInputState.Recording or PetVoiceInputState.Transcribing;

    private void AddVoiceInputButton(DockPanel actions)
    {
        _microphone = IconButton(MicrophoneGeometry, "语音输入：点击录音，再次点击转文字；本机识别，不自动发送", async (_, _) => await ToggleVoiceInputAsync());
        _microphone.Name = "OllamaMicrophone"; _microphone.Margin = new Thickness(5, 0, 0, 0);
        actions.Children.Add(_microphone);
    }

    private void AddVoiceInputStatus(StackPanel composer)
    {
        _voiceInputStatus = Ui.Text("", 10, "#718267"); _voiceInputStatus.Name = "OllamaVoiceInputStatus";
        _voiceInputStatus.Margin = new Thickness(1, 5, 0, 0); _voiceInputStatus.TextWrapping = TextWrapping.Wrap;
        _voiceInputStatus.MaxHeight = 32; _voiceInputStatus.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_voiceInputStatus, AutomationLiveSetting.Polite);
        composer.Children.Add(_voiceInputStatus);
    }

    private void InitializeVoiceInput(PetVoiceInputSession? injected)
    {
        _voiceInput = injected ?? new PetVoiceInputSession(Dispatcher);
        _voiceInput.Changed += VoiceInputChanged;
        _voiceInput.Transcribed += VoiceInputTranscribed;
        SystemEvents.SessionSwitch += VoiceInputSessionSwitch;
        SystemEvents.PowerModeChanged += VoiceInputPowerChanged;
        _voiceEventsSubscribed = true;
        RefreshVoiceInputControls();
    }

    internal async Task ToggleVoiceInputAsync()
    {
        if (_shutdown || _voiceInput == null) return;
        if (_voiceInput.State == PetVoiceInputState.Transcribing) { CancelVoiceInput(); return; }
        if (_voiceInput.State == PetVoiceInputState.Recording) { await _voiceInput.StopAsync(); return; }
        if (!CanStartVoiceInput()) return;
        _voiceConversation = _conversation!.Id; _voiceEndpoint = EndpointScope(_client.Endpoint);
        _recordingSafetyEpoch = Volatile.Read(ref _voiceSafetyEpoch);
        _voicePersona = _settings.SkinId.ToLowerInvariant(); _voiceModel = _model; _voiceInputResultHint = null;
        _voiceInputMessageVisible = true;
        _lastInputActivity = DateTimeOffset.UtcNow;
        CancelBehaviorReaction(); _stopSpeech?.Invoke();
        // Pause pet playback before opening the microphone, preventing acoustic feedback.
        SetVoiceInputPlaybackPause(true);
        _voiceInput.Start();
        if (_voiceInput.State == PetVoiceInputState.Idle) SetVoiceInputPlaybackPause(false);
    }

    private bool CanStartVoiceInput() => !_shutdown && IsVisible && !_generating && !_userSendPending && !_switching &&
        !_configuring && !_sessionSwitching && !_preparingImage && !ModelMemoryBusy && _conversation != null &&
        _conversation.Endpoint == EndpointScope(_client.Endpoint) && _conversation.PersonaKey == _settings.SkinId.ToLowerInvariant() &&
        _input.Text.Length < MaxInputCharacters;

    private void VoiceInputChanged()
    {
        if (_shutdown) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(VoiceInputChanged)); return; }
        SetVoiceInputPlaybackPause(VoiceInputBusy);
        RefreshControls();
    }

    private void SetVoiceInputPlaybackPause(bool active)
    {
        if (_reportedVoiceInputActive == active) return;
        _reportedVoiceInputActive = active;
        _voiceInputActive?.Invoke(active);
    }

    private void VoiceInputTranscribed(string text)
    {
        if (_shutdown || !IsVisible || _recordingSafetyEpoch != Volatile.Read(ref _voiceSafetyEpoch) || _voiceConversation == null || _conversation?.Id != _voiceConversation ||
            _voiceEndpoint != EndpointScope(_client.Endpoint) || _voicePersona != _settings.SkinId.ToLowerInvariant() || _voiceModel != _model) return;
        text = text.Trim(); if (text.Length == 0) return;
        string separator = string.IsNullOrWhiteSpace(_input.Text) ? "" : Environment.NewLine;
        int available = MaxInputCharacters - _input.Text.Length - separator.Length;
        if (available < text.Length)
        {
            _voiceInputResultHint = "输入框空间不足，转写未写入。请缩短草稿后重新录音。";
            RefreshVoiceInputControls(); return;
        }
        _input.Text += separator + text;
        _input.CaretIndex = _input.Text.Length;
        _voiceInputResultHint = "已转成文字，可编辑后发送。";
        _voiceConversation = null;
        QueueDraftSave(); RefreshControls();
        _input.Focus();
    }

    private void RefreshVoiceInputControls()
    {
        if (_microphone == null || _voiceInputStatus == null || _voiceInput == null) return;
        bool recording = _voiceInput.State == PetVoiceInputState.Recording;
        bool transcribing = _voiceInput.State == PetVoiceInputState.Transcribing;
        _microphone.IsEnabled = VoiceInputBusy || CanStartVoiceInput();
        string label = recording ? "结束录音并转文字" : transcribing ? "取消语音识别" : "语音输入：点击录音，再次点击转文字；本机识别，不自动发送";
        _microphone.ToolTip = label; AutomationProperties.SetName(_microphone, label);
        _microphone.Background = recording ? Ui.Brush("#FCE6E2") : transcribing ? Ui.Brush("#E6EEDD") : Brushes.White;
        if (_microphone.Content is ShapePath icon)
        {
            icon.Data = Geometry.Parse(recording ? "M 4,4 L 12,4 L 12,12 L 4,12 Z" : transcribing ? "M 4,4 L 12,12 M 12,4 L 4,12" : MicrophoneGeometry);
            icon.Stroke = Ui.Brush(recording ? "#A84235" : "#526D4F");
        }
        string hint = recording
            ? "● 正在录音 " + Math.Floor(_voiceInput.ElapsedSeconds).ToString(CultureInfo.InvariantCulture) + " / 60 秒 · 点击麦克风完成，Esc 取消"
            : transcribing ? "正在本机转成文字… 点击麦克风或 Esc 取消" : _voiceInputResultHint ?? _voiceInput.StatusText;
        _voiceInputStatus.Text = hint; _voiceInputStatus.ToolTip = hint;
        _voiceInputStatus.Foreground = Ui.Brush(recording ? "#A84235" : "#718267");
        _voiceInputStatus.Visibility = !_voiceInputMessageVisible || string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void CancelVoiceInput()
    {
        _voiceConversation = null; _voiceEndpoint = _voicePersona = _voiceModel = null;
        _voiceInputResultHint = null;
        _voiceInputMessageVisible = false;
        _voiceInput?.Cancel(); SetVoiceInputPlaybackPause(false);
        if (!_shutdown) RefreshVoiceInputControls();
    }

    private void VoiceInputSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            QueueVoiceInputCancellation();
    }

    private void VoiceInputPowerChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Suspend) QueueVoiceInputCancellation(); }

    private void QueueVoiceInputCancellation()
    {
        // Invalidate results on the system-event thread before queued UI cleanup;
        // a completed recognizer continuation may otherwise win the dispatcher race.
        Interlocked.Increment(ref _voiceSafetyEpoch);
        if (_shutdown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_shutdown) CancelVoiceInput(); }));
    }

    private void DisposeVoiceInput()
    {
        CancelVoiceInput();
        if (_voiceEventsSubscribed)
        {
            SystemEvents.SessionSwitch -= VoiceInputSessionSwitch; SystemEvents.PowerModeChanged -= VoiceInputPowerChanged;
            _voiceEventsSubscribed = false;
        }
        if (_voiceInput == null) return;
        _voiceInput.Changed -= VoiceInputChanged; _voiceInput.Transcribed -= VoiceInputTranscribed;
        _voiceInput.Dispose(); _voiceInput = null;
    }
}
