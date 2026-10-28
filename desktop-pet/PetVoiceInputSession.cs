using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal enum PetVoiceInputState { Idle, Recording, Transcribing }

internal sealed class PetVoiceInputSession : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IPetMicrophoneRecorder _recorder;
    private readonly IPetSpeechRecognizer _recognizer;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _operation;
    private Action? _limitHandler;
    private long _epoch;
    private bool _disposed;
    public event Action? Changed;
    public event Action<string>? Transcribed;
    public PetVoiceInputState State { get; private set; }
    public string StatusText { get; private set; } = "点击麦克风录音，转成文字后确认发送";
    public double ElapsedSeconds => Math.Min(60, _clock.Elapsed.TotalSeconds);

    public PetVoiceInputSession(Dispatcher dispatcher, IPetMicrophoneRecorder? recorder = null, IPetSpeechRecognizer? recognizer = null)
    {
        _dispatcher = dispatcher;
        _recorder = recorder ?? new PetMicrophoneRecorder();
        _recognizer = recognizer ?? new PetSpeechRecognizer();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += TimerTick;
    }

    private void SetState(PetVoiceInputState state, string status)
    { State = state; StatusText = status; if (!_disposed) Changed?.Invoke(); }

    public void Start()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || State != PetVoiceInputState.Idle) return;
        long epoch = ++_epoch;
        _clock.Reset();
        try
        {
            // Notify the host to mute its own voice before opening the microphone.
            SetState(PetVoiceInputState.Recording, "正在录音 0 秒 · 再点麦克风结束 · Esc 取消");
            if (_disposed || epoch != _epoch) return;
            _limitHandler = () => _dispatcher.BeginInvoke(new Action(() =>
            { if (!_disposed && epoch == _epoch && State == PetVoiceInputState.Recording) _ = StopAsync(); }));
            _recorder.LimitReached += _limitHandler;
            _recorder.Start();
            _clock.Start(); _timer.Start();
        }
        catch (Exception ex)
        {
            DetachLimit(); DiscardRecording();
            SetState(PetVoiceInputState.Idle, ErrorText(ex));
        }
    }

    private void TimerTick(object? sender, EventArgs e)
    {
        if (_disposed || State != PetVoiceInputState.Recording) return;
        if (!_recorder.IsRecording || ElapsedSeconds >= 60) { _ = StopAsync(); return; }
        StatusText = $"正在录音 {ElapsedSeconds:0} 秒 / 60 秒 · 再点结束 · Esc 取消";
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || State != PetVoiceInputState.Recording) return;
        long epoch = _epoch;
        _timer.Stop(); _clock.Stop(); DetachLimit();
        using var operation = new CancellationTokenSource();
        _operation = operation;
        try
        {
            SetState(PetVoiceInputState.Transcribing, "正在本地识别… · 再点麦克风或按 Esc 取消");
            if (_disposed || epoch != _epoch) return;
            byte[] wave = _recorder.Stop();
            PetSpeechRecognizer.ValidateWave(wave);
            if (wave.Length < 8_044) { SetState(PetVoiceInputState.Idle, "录音太短，请说完一句话后再结束。"); return; }
            string text = await _recognizer.TranscribeAsync(wave, operation.Token);
            if (_disposed || epoch != _epoch || operation.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(text)) SetState(PetVoiceInputState.Idle, "没有识别到清晰语音，请靠近麦克风重试。");
            else
            {
                Transcribed?.Invoke(text);
                if (!_disposed && epoch == _epoch) SetState(PetVoiceInputState.Idle, "转写已填入输入框，可编辑后发送。");
            }
        }
        catch (OperationCanceledException) { if (!_disposed && epoch == _epoch) SetState(PetVoiceInputState.Idle, "已取消语音输入。"); }
        catch (Exception ex)
        {
            DiscardRecording();
            if (!_disposed && epoch == _epoch) SetState(PetVoiceInputState.Idle, ErrorText(ex));
        }
        finally { if (ReferenceEquals(_operation, operation)) _operation = null; }
    }

    public void Cancel()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || State == PetVoiceInputState.Idle) return;
        _epoch++; _timer.Stop(); _clock.Stop(); DetachLimit();
        _operation?.Cancel();
        _recorder.Cancel();
        SetState(PetVoiceInputState.Idle, "已取消语音输入，原有文字已保留。");
    }

    private void DetachLimit()
    { if (_limitHandler != null) { _recorder.LimitReached -= _limitHandler; _limitHandler = null; } }

    private void DiscardRecording()
    { try { _recorder.Cancel(); } catch (Exception) { /* Preserve the original device error and keep the UI usable. */ } }

    private static string ErrorText(Exception ex) => ex is InvalidOperationException ? ex.Message : "语音输入暂不可用，请检查麦克风及本地识别环境。";

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        Cancel(); _disposed = true;
        _timer.Stop(); _timer.Tick -= TimerTick;
        _recorder.Dispose(); _recognizer.Dispose();
        Changed = null; Transcribed = null;
    }
}
