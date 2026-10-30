using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal enum PetSpeechSource { Preset, Chat, Preview }

// Queue state and playback live on the WPF dispatcher; synthesis never blocks it.
internal sealed class PetSpeechService : IDisposable
{
    private sealed record Utterance(string Text, PetSpeechSource Source, long Epoch, ChannelReader<string>? Incoming = null);
    private readonly DesktopSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly bool _enabledForProcess;
    private readonly IPetSpeechSynthesizer _synthesizer;
    private readonly IPetSpeechPlayer _player;
    private readonly IPetPcmStreamPlayer? _streamPlayer;
    private readonly DispatcherTimer _retentionTimer;
    private readonly Queue<Utterance> _queue = new();
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly Dictionary<string, byte[]> _presetCache = new();
    private int _cachedBytes;
    private CancellationTokenSource? _current;
    private string? _requestId;
    private bool _pumping, _suspended, _disposed;
    private bool _chatSessionActive, _manualUnload, _modelReady, _retentionBusy;
    private CancellationTokenSource? _preloadCancellation;
    private string? _preloadId;
    private Task? _preloading;
    private Channel<string>? _chatInput;
    private long _chatInputId = -1;
    private int _chatInputCharacters;
    private long _epoch;
    private Task _pendingRelease = Task.CompletedTask;
    private PetVoiceProfile? _selectedProfile;
    private string? _profileSkin;
    public event Action? Changed;
    public string StatusText { get; private set; } = L.T("本地参考音色待命", "Local reference voice ready");
    public bool IsSpeaking { get; private set; }

    internal Task<PetVoiceHealth?> ReadModelHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return !_disposed && _synthesizer is IPetVoiceDiagnostics diagnostics
            ? diagnostics.ReadHealthAsync(cancellationToken) : Task.FromResult<PetVoiceHealth?>(null);
    }

    public PetSpeechService(DesktopSettings settings, Dispatcher dispatcher, bool enabledForProcess = true)
        : this(settings, dispatcher, new PetVoiceClient(), new PetSpeechPlayer(dispatcher), enabledForProcess) { }

    internal PetSpeechService(DesktopSettings settings, Dispatcher dispatcher,
        IPetSpeechSynthesizer synthesizer, IPetSpeechPlayer player, bool enabledForProcess = true,
        IPetPcmStreamPlayer? streamPlayer = null)
    {
        _settings = settings; _dispatcher = dispatcher; _synthesizer = synthesizer;
        _player = player; _enabledForProcess = enabledForProcess;
        _streamPlayer = streamPlayer ?? (synthesizer is IPetStreamingSpeechSynthesizer ? new PetPcmStreamPlayer() : null);
        _retentionTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        _retentionTimer.Tick += RetentionTick;
        ApplySettings();
    }

    private bool _studioBusy;
    private bool Allowed => !_disposed && !_studioBusy && _enabledForProcess && _settings.SpeechEnabled &&
        PetVoiceProfiles.IsEnabled(_settings.GetVoiceProfile(_settings.SkinId)) && !_suspended;
    private bool ModelAllowed => !_disposed && !_studioBusy && _enabledForProcess && _settings.SpeechEnabled &&
        PetVoiceProfiles.IsEnabled(_settings.GetVoiceProfile(_settings.SkinId));

    private string IdleStatus => !_settings.SpeechEnabled ? L.T("语音已关闭", "Speech is off") :
        !PetVoiceProfiles.IsEnabled(_settings.GetVoiceProfile(_settings.SkinId)) ? L.T("当前角色未选择语音模型", "No speech model selected for this character") :
        _suspended ? L.T("语音已暂停", "Speech paused") : !_enabledForProcess ? L.T("测试模式：不播放声音", "Test mode: audio playback disabled") :
        _manualUnload ? L.T("语音模型已卸载，下次朗读时加载", "Speech model unloaded; the next readout will load it") :
        _modelReady && _chatSessionActive ? L.T("语音模型已加载，待命", "Speech model loaded and ready") : L.T("本地语音待命，按需加载", "Local speech ready; model loads on demand");

    private void SetStatus(string value, bool speaking = false)
    {
        if (_disposed || (StatusText == value && IsSpeaking == speaking)) return;
        StatusText = value; IsSpeaking = speaking; Changed?.Invoke();
    }

    public void Speak(string text, PetSpeechSource source, string? dedupeKey = null)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Speak(text, source, dedupeKey)); return; }
        if (_pendingRelease.IsFaulted || _pendingRelease.IsCanceled) ApplySettings();
        if (!Allowed) return;
        string normalized;
        try { normalized = PetSpeechText.Normalize(text); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { SetStatus(L.T("这段文本过于复杂，暂未朗读。", "This text is too complex to read aloud.")); return; }
        if (normalized.Length == 0) return;
        // Only callers that identify the same underlying notice opt into
        // deduplication. Separate clicks can legitimately say the same words.
        if (source == PetSpeechSource.Preset && !string.IsNullOrEmpty(dedupeKey))
        {
            var now = DateTime.UtcNow;
            string key = dedupeKey;
            if (_recent.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(5)) return;
            if (_recent.Count >= 64) _recent.Clear();
            _recent[key] = now;
        }
        // A new utterance replaces synthesis, queued sentences and playback
        // from the previous one. AppendChatResponse remains within one epoch.
        StopCore();
        _manualUnload = false;
        _queue.Enqueue(new Utterance(normalized, source, _epoch));
        if (!_pumping) _ = PumpAsync();
    }

    public long BeginChatResponse()
    {
        _dispatcher.VerifyAccess();
        if (_pendingRelease.IsFaulted || _pendingRelease.IsCanceled) ApplySettings();
        if (!Allowed) return -1;
        StopCore(); _manualUnload = false;
        _chatInput = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _chatInputId = _epoch; _chatInputCharacters = 0;
        _queue.Enqueue(new Utterance("", PetSpeechSource.Chat, _epoch, _chatInput.Reader));
        if (!_pumping) _ = PumpAsync();
        return _chatInputId;
    }

    public void AppendChatResponse(long id, string finalizedProse)
    {
        _dispatcher.VerifyAccess();
        if (!Allowed || id < 0 || id != _epoch || id != _chatInputId || _chatInput == null) return;
        // The chat sanitizer already finalized this plain-text segment. The
        // second normalizer is defense in depth for other future callers.
        string text = PetSpeechText.Normalize(finalizedProse);
        if (text.Length == 0) return;
        if (finalizedProse.TrimEnd().EndsWith('。') && !text.EndsWith('。')) text += "。";
        if ((_chatInputCharacters += text.Length) > 16000)
        { _chatInput.Writer.TryComplete(new InvalidOperationException(L.T("这条语音回复过长，已保留文字。", "This reply is too long for speech. The text has been kept."))); return; }
        _chatInput.Writer.TryWrite(text);
    }

    public void CompleteChatResponse(long id)
    {
        _dispatcher.VerifyAccess();
        if (id < 0 || id != _chatInputId) return;
        _chatInput?.Writer.TryComplete(); _chatInput = null; _chatInputId = -1;
    }

    public void CancelChatResponse(long id)
    {
        _dispatcher.VerifyAccess();
        // Complete only closes the input: its audio may still be playing.
        // A late cancel from an older reply must never stop a newer message.
        if (!_disposed && id > 0 && id == _epoch) StopCore();
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (Allowed && _queue.TryDequeue(out var item))
            {
                using var cancellation = new CancellationTokenSource();
                _current = cancellation;
                try
                {
                    // A late unload from a previous toggle must finish before a new request.
                    await _pendingRelease;
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_synthesizer is IPetStreamingSpeechSynthesizer { SupportsStreaming: true } streaming && _streamPlayer != null)
                    {
                        SetStatus(L.T("正在准备连续语音…", "Preparing streamed speech…"));
                        await _streamPlayer.PlayAsync(StreamUtteranceAsync(item, streaming, cancellation.Token), _settings.SpeechVolume,
                            speaking => _dispatcher.BeginInvoke(() =>
                            {
                                if (Allowed && item.Epoch == _epoch && !cancellation.IsCancellationRequested)
                                    SetStatus(speaking ? L.T("正在说话…", "Speaking…") : L.T("正在缓冲连续语音…", "Buffering speech…"), speaking);
                            }), cancellation.Token);
                        if (item.Epoch == _epoch) SetStatus(IdleStatus);
                        continue;
                    }
                    var text = new StringBuilder(item.Text);
                    if (item.Incoming != null)
                        await foreach (var part in item.Incoming.ReadAllAsync(cancellation.Token)) text.Append(part);
                    if (text.Length == 0) { SetStatus(IdleStatus); continue; }
                    var parts = PetSpeechText.Split(text.ToString(), 100);
                    byte[] completeWave;
                    // This local voice generates slower than real-time. Buffer
                    // the complete utterance before playback so there is never
                    // a sentence gap while waiting for the next inference.
                    using (var buffered = new PetSpeechWave.Builder())
                    {
                        for (int index = 0; index < parts.Count; index++)
                        {
                            if (!Allowed || item.Epoch != _epoch) throw new OperationCanceledException();
                            cancellation.Token.ThrowIfCancellationRequested();
                            SetStatus(L.T($"正在准备连续语音（{index}/{parts.Count}）…", $"Preparing speech ({index}/{parts.Count})…"));
                            byte[] wave = await GenerateChunkAsync(parts[index], item.Source, cancellation.Token);
                            cancellation.Token.ThrowIfCancellationRequested();
                            if (!Allowed || item.Epoch != _epoch) throw new OperationCanceledException();
                            buffered.Append(wave);
                        }
                        completeWave = buffered.Complete();
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!Allowed || item.Epoch != _epoch) throw new OperationCanceledException();
                    SetStatus(L.T("正在说话…", "Speaking…"), true);
                    await _player.PlayAsync(completeWave, _settings.SpeechVolume, cancellation.Token);
                    if (item.Epoch == _epoch) SetStatus(IdleStatus);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (Allowed && item.Epoch == _epoch)
                        SetStatus(ex is InvalidOperationException ? ex.Message : L.T("语音暂不可用，请稍后点击试听重试。", "Speech is unavailable. Click Audition to try again shortly."));
                }
                finally
                {
                    cancellation.Cancel();
                    if (ReferenceEquals(_current, cancellation))
                    {
                        string? pendingId = _requestId;
                        if (pendingId != null) _ = IgnoreFailureAsync(() => _synthesizer.CancelAsync(pendingId));
                        _current = null; _requestId = null;
                    }
                }
            }
        }
        finally { _pumping = false; }
    }

    private async IAsyncEnumerable<PetPcmChunk> StreamUtteranceAsync(Utterance item, IPetStreamingSpeechSynthesizer streaming,
        [EnumeratorCancellation] CancellationToken token)
    {
        async IAsyncEnumerable<string> Texts([EnumeratorCancellation] CancellationToken ct)
        {
            if (item.Text.Length > 0) yield return item.Text;
            if (item.Incoming != null)
                await foreach (var text in item.Incoming.ReadAllAsync(ct).ConfigureAwait(false)) yield return text;
        }
        await foreach (string text in Texts(token).ConfigureAwait(false))
        {
            foreach (string part in PetSpeechText.Split(text, 100))
            {
                token.ThrowIfCancellationRequested();
                string id = Guid.NewGuid().ToString("N");
                await _dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Epoch != _epoch) throw new OperationCanceledException();
                    _requestId = id;
                });
                try
                {
                    await foreach (var chunk in ReadCancelableChunksAsync(streaming.StreamAsync(part, id, token), token).ConfigureAwait(false))
                    { token.ThrowIfCancellationRequested(); yield return chunk; }
                    await _dispatcher.InvokeAsync(() => { if (item.Epoch == _epoch) _modelReady = true; });
                }
                finally
                {
                    if (token.IsCancellationRequested) _ = IgnoreFailureAsync(() => _synthesizer.CancelAsync(id));
                    await _dispatcher.InvokeAsync(() => { if (_requestId == id) _requestId = null; });
                }
            }
        }
    }

    private static async IAsyncEnumerable<PetPcmChunk> ReadCancelableChunksAsync(IAsyncEnumerable<PetPcmChunk> source,
        [EnumeratorCancellation] CancellationToken token)
    {
        var reader = source.GetAsyncEnumerator(token);
        Task<bool>? pending = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                pending = reader.MoveNextAsync().AsTask();
                if (!await pending.WaitAsync(token).ConfigureAwait(false)) break;
                pending = null;
                token.ThrowIfCancellationRequested();
                yield return reader.Current;
            }
        }
        finally
        {
            // A backend may deliver a final chunk after cancellation. Do not
            // let that delay the new message or dispose an active MoveNext.
            if (token.IsCancellationRequested)
                _ = IgnoreFailureAsync(async () =>
                {
                    try { if (pending != null) await pending.ConfigureAwait(false); }
                    finally { await reader.DisposeAsync().ConfigureAwait(false); }
                });
            else await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<byte[]> GenerateChunkAsync(string part, PetSpeechSource source, CancellationToken token)
    {
        if (source == PetSpeechSource.Preset && _presetCache.TryGetValue(part, out var cached)) return cached;
        string id = Guid.NewGuid().ToString("N");
        _requestId = id;
        try
        {
            var wave = await _synthesizer.SynthesizeAsync(part, id, token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            // Preset cache stays in memory; private chat speech is never archived.
            if (source == PetSpeechSource.Preset && wave.Length <= 4 * 1024 * 1024)
            {
                if (_cachedBytes + wave.Length > 32 * 1024 * 1024) { _presetCache.Clear(); _cachedBytes = 0; }
                _presetCache[part] = wave; _cachedBytes += wave.Length;
            }
            return wave;
        }
        finally
        {
            if (token.IsCancellationRequested) _ = IgnoreFailureAsync(() => _synthesizer.CancelAsync(id));
            if (_requestId == id) _requestId = null;
        }
    }

    private void StopCore()
    {
        // Cancellation can resume the old pump inline and clear _requestId.
        // Capture its identity before signalling, so the server still receives
        // the cancellation even when the local wait has already unwound.
        string? requestId = _requestId;
        _epoch++; _queue.Clear();
        _chatInput?.Writer.TryComplete(); _chatInput = null; _chatInputId = -1;
        _current?.Cancel(); _player.Stop(); _streamPlayer?.Stop();
        if (requestId != null) _ = IgnoreFailureAsync(() => _synthesizer.CancelAsync(requestId));
        SetStatus(IdleStatus);
    }

    public void Stop()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(Stop); return; }
        if (!_disposed) StopCore();
    }

    public void ApplySettings()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(ApplySettings); return; }
        if (_disposed) return;
        PetVoiceProfile profile = _settings.GetVoiceProfile(_settings.SkinId);
        bool profileChanged = _selectedProfile != profile || _profileSkin != _settings.SkinId;
        if (profileChanged || _pendingRelease.IsFaulted || _pendingRelease.IsCanceled)
        {
            bool hadProfile = _selectedProfile != null;
            _selectedProfile = profile; _profileSkin = _settings.SkinId;
            StopCore(); CancelPreload(); _preloading = null; _modelReady = false;
            _presetCache.Clear(); _cachedBytes = 0; _recent.Clear();
            _manualUnload = false;
            if (_enabledForProcess)
                _pendingRelease = ConfigureProfileAfterAsync(_pendingRelease, profile, hadProfile);
        }
        _player.SetVolume(_settings.SpeechVolume);
        _streamPlayer?.SetVolume(_settings.SpeechVolume);
        if (!Allowed)
        {
            StopCore();
            if (_enabledForProcess && !(ModelAllowed && _chatSessionActive && !_manualUnload))
            {
                CancelPreload(); _modelReady = false;
                _pendingRelease = ReleaseAfterAsync(_pendingRelease);
            }
        }
        else if (!IsSpeaking && !_pumping) SetStatus(IdleStatus);
    }

    public void SetChatSessionActive(bool active)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => SetChatSessionActive(active)); return; }
        if (_disposed || active == _chatSessionActive) return;
        _chatSessionActive = active;
        if (active)
        {
            _retentionTimer.Start();
            if (ModelAllowed && !_manualUnload) _ = IgnoreFailureAsync(PreloadAsync);
        }
        else
        {
            _retentionTimer.Stop();
            _ = UpdateRetentionAsync();
            if (!_pumping && _preloading is not { IsCompleted: false }) SetStatus(IdleStatus);
        }
    }

    public Task PreloadAsync()
    {
        _dispatcher.VerifyAccess();
        if (_pendingRelease.IsFaulted || _pendingRelease.IsCanceled) ApplySettings();
        if (!ModelAllowed || _synthesizer is not IPetVoiceLifecycle) return Task.CompletedTask;
        if (_preloading is { IsCompleted: false }) return _preloading;
        _manualUnload = false;
        return _preloading = PreloadCoreAsync();
    }

    private async Task PreloadCoreAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _preloadCancellation = cancellation;
        string id = Guid.NewGuid().ToString("N"); _preloadId = id;
        try
        {
            await _pendingRelease;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_pumping && !IsSpeaking) SetStatus(L.T("正在预加载语音模型…", "Preloading speech model…"));
            await ((IPetVoiceLifecycle)_synthesizer).PreloadAsync(id, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _modelReady = true;
            await UpdateRetentionAsync();
            if (!_pumping) SetStatus(IdleStatus);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { if (!_disposed && !_pumping && !IsSpeaking) SetStatus(ex is InvalidOperationException ? ex.Message : L.T("语音模型预加载失败，请重试。", "Speech model preload failed. Please try again.")); throw; }
        finally
        {
            if (ReferenceEquals(_preloadCancellation, cancellation)) { _preloadCancellation = null; _preloadId = null; }
        }
    }

    public async Task UnloadAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        if (_pendingRelease.IsFaulted || _pendingRelease.IsCanceled) ApplySettings();
        _manualUnload = true; StopCore(); CancelPreload();
        if (!_enabledForProcess) return;
        SetStatus(L.T("正在卸载语音模型…", "Unloading speech model…"));
        try
        {
            Task previous = _pendingRelease;
            _pendingRelease = UnloadCoreAsync(previous);
            await _pendingRelease;
            _modelReady = false;
            if (!_disposed) SetStatus(IdleStatus);
        }
        catch (Exception ex)
        { if (!_disposed) SetStatus(ex is InvalidOperationException ? ex.Message : L.T("语音模型卸载未完成，请重试。", "The speech model has not finished unloading. Please try again.")); throw; }
        // Keep a failed configuration/release barrier visible. The next explicit
        // request retries selection before speaking instead of reusing an old voice.
    }

    private async Task UnloadCoreAsync(Task previous)
    {
        await previous;
        await UpdateRetentionAsync();
        if (_synthesizer is IPetVoiceLifecycle lifecycle) await lifecycle.UnloadAsync(CancellationToken.None);
        else await _synthesizer.ReleaseAsync();
    }

    private void CancelPreload()
    {
        _preloadCancellation?.Cancel();
        if (_preloadId is { } id) _ = IgnoreFailureAsync(() => _synthesizer.CancelAsync(id));
    }

    private async void RetentionTick(object? sender, EventArgs args)
    {
        if (_retentionBusy) return;
        _retentionBusy = true;
        try { await UpdateRetentionAsync(); }
        finally { _retentionBusy = false; }
    }

    private async Task UpdateRetentionAsync()
    {
        if (_disposed || !_enabledForProcess || _synthesizer is not IPetVoiceLifecycle lifecycle) return;
        bool active = ModelAllowed && _chatSessionActive && !_manualUnload;
        await IgnoreFailureAsync(() => lifecycle.SetRetentionAsync(active, CancellationToken.None));
    }

    public void SetSuspended(bool suspended)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => SetSuspended(suspended)); return; }
        if (_disposed || suspended == _suspended) return;
        _suspended = suspended;
        ApplySettings();
    }

    internal void SetStudioBusy(bool busy)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _studioBusy == busy) return;
        _studioBusy = busy;
        ApplySettings();
    }

    private static async Task IgnoreFailureAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception) { /* Best effort cancellation/unload, never a UI crash. */ }
    }

    private async Task ReleaseAfterAsync(Task previous)
    {
        await previous;
        if (!_disposed) await IgnoreFailureAsync(_synthesizer.ReleaseAsync);
    }

    private async Task ConfigureProfileAfterAsync(Task previous, PetVoiceProfile profile, bool hadProfile)
    {
        try { await previous; }
        catch (Exception) { /* Explicit retry still asks the owner to stop its old worker. */ }
        if (_disposed) return;
        if (_synthesizer is IPetVoiceProfileSelector selector)
            await selector.SelectProfileAsync(profile, CancellationToken.None);
        else if (hadProfile)
            await _synthesizer.ReleaseAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopCore(); CancelPreload(); _disposed = true;
        _retentionTimer.Stop(); _retentionTimer.Tick -= RetentionTick;
        _player.Dispose(); _streamPlayer?.Dispose(); _synthesizer.Dispose();
        _presetCache.Clear(); _recent.Clear(); Changed = null;
    }
}
