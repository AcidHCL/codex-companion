using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopPet;

// Explicit opt-in real TTS test. All spoken text is fixed below; no conversation,
// Ollama request, microphone, account or saved desktop settings are accessed.
internal static class PetStreamingSpeechLiveChecks
{
    private const string WarmText = "哼，任务完成啦，记得请本鲸吃白米饭！";
    private static readonly string[] ChatText =
    {
        "今天的任务已经完成，我们可以稍微休息一下。",
        "本鲸已经检查过结果，接下来一起安排明天的事情吧。"
    };

    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            var checks = new List<string>();
            void Require(bool condition, string name)
            { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
            var overall = Stopwatch.StartNew();
            string phase = "configuration"; string? error = null;
            double? preloadSeconds = null, unloadSeconds = null;
            int? workerPid = null;
            PetVoiceHealth? afterPreload = null, retainedHealth = null;
            bool ownedWorkerGone = false;
            int port; using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            using var client = new PetVoiceClient(port: port);
            using var synth = new ObservedSynth(client);
            using var output = new ObservedOutput();
            using var voice = new PetSpeechService(new DesktopSettings { SkinId = "deepseek", SpeechEnabled = true, SpeechVolume = .35 },
                app.Dispatcher, synth, new PetSpeechPlayer(app.Dispatcher), streamPlayer: output);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                Require(StartupModePolicy.IsIsolatedDiagnostic(new[] { "--speech-stream-live-test" }), "live_stream_test_is_isolated_from_real_settings_and_account");
                Require(client.SupportsStreaming, "configured_backend_is_faster_before_loading_any_model");

                phase = "manual_preload";
                var clock = Stopwatch.StartNew();
                await voice.PreloadAsync().WaitAsync(TimeSpan.FromMinutes(5), deadline.Token);
                preloadSeconds = clock.Elapsed.TotalSeconds;
                workerPid = client.OwnedWorkerProcessId;
                afterPreload = await client.ReadHealthAsync(deadline.Token);
                Require(workerPid != null && afterPreload is { Backend: "faster", Loaded: true, Retiring: false },
                    "manual_preload_completes_real_faster_worker_and_model_loading");

                phase = "chat_retention";
                voice.SetChatSessionActive(true);
                await voice.PreloadAsync().WaitAsync(TimeSpan.FromMinutes(1), deadline.Token);
                int retainedCalls = synth.ActiveRetentionCalls;
                retainedHealth = await client.ReadHealthAsync(deadline.Token);
                Require(retainedHealth is { Loaded: true, Retained: true } && client.OwnedWorkerProcessId == workerPid,
                    "opening_chat_holds_loaded_voice_in_same_worker");

                phase = "warm_speech";
                voice.Speak(WarmText, PetSpeechSource.Preview);
                PlaybackObservation warm = await output.WaitForCallAsync(1, deadline.Token);
                await warm.Done.Task.WaitAsync(TimeSpan.FromMinutes(2), deadline.Token);
                await UntilAsync(() => !voice.IsSpeaking && voice.StatusText.Contains("待命"), deadline.Token);
                var warmState = warm.Snapshot();
                Require(warmState.FirstPlaybackStartSeconds is >= 0 and < 5,
                    "warm_voice_starts_playback_within_five_seconds");
                Require(warmState.PlaybackStarts == 1 && warmState.RebufferCount == 0,
                    "warm_voice_completes_without_synthesis_wait_gaps");

                phase = "incremental_chat";
                int beforeRequests = synth.Snapshot().Length;
                long turn = voice.BeginChatResponse();
                Require(turn >= 0, "incremental_chat_response_is_accepted");
                voice.AppendChatResponse(turn, ChatText[0]);
                PlaybackObservation chat = await output.WaitForCallAsync(2, deadline.Token);
                await chat.FirstPlayback.Task.WaitAsync(TimeSpan.FromMinutes(1), deadline.Token);
                Require(!chat.Done.Task.IsCompleted, "first_chat_segment_plays_before_remaining_text_is_submitted");
                voice.AppendChatResponse(turn, ChatText[1]);
                voice.CompleteChatResponse(turn);
                await chat.Done.Task.WaitAsync(TimeSpan.FromMinutes(3), deadline.Token);
                await UntilAsync(() => !voice.IsSpeaking && voice.StatusText.Contains("待命"), deadline.Token);
                var chatState = chat.Snapshot();
                var spoken = synth.Snapshot().Skip(beforeRequests).ToArray();
                Require(output.Count == 2 && spoken.Select(item => item.Text).SequenceEqual(ChatText),
                    "incremental_segments_are_synthesized_once_in_order_in_one_playback_call");
                Require(chatState.PlaybackStarts == 1 && chatState.RebufferCount == 0,
                    "incremental_chat_has_no_mid_reply_rebuffering");
                Require(synth.LegacyCalls == 0, "real_audio_uses_stream_endpoint_without_whole_wav_fallback");

                phase = "retention_renewal";
                await UntilAsync(() => synth.ActiveRetentionCalls > retainedCalls, deadline.Token, 20);
                retainedHealth = await client.ReadHealthAsync(deadline.Token);
                Require(retainedHealth is { Loaded: true, Retained: true, Retiring: false } && client.OwnedWorkerProcessId == workerPid,
                    "chat_timer_renews_server_lease_without_loading_another_worker");

                phase = "manual_unload";
                clock.Restart();
                await voice.UnloadAsync().WaitAsync(TimeSpan.FromSeconds(15), deadline.Token);
                unloadSeconds = clock.Elapsed.TotalSeconds;
                ownedWorkerGone = workerPid is { } id && Gone(id);
                Require(ownedWorkerGone && client.OwnedWorkerProcessId == null,
                    "manual_unload_exits_owned_worker_to_release_cuda_graph_resources");
                // Old text arriving after manual unload cannot recreate it.
                voice.AppendChatResponse(turn, "这段过期文字不应朗读。");
                voice.CompleteChatResponse(turn);
                await Task.Delay(250, deadline.Token);
                Require(client.OwnedWorkerProcessId == null && output.Count == 2,
                    "stale_chat_segments_do_not_reload_manually_unloaded_voice");
                voice.SetChatSessionActive(false);
                result = 0; phase = "complete";
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally
            {
                voice.Dispose();
                client.Dispose();
                if (workerPid is { } id) ownedWorkerGone = Gone(id);
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "speech-stream-live-validation.json"), JsonSerializer.Serialize(new
            {
                passed = result == 0, assertions = checks.Count, checks, phase, error,
                preloadSeconds, unloadSeconds, workerPid, ownedWorkerGone, afterPreload, retainedHealth,
                playback = output.Snapshot(), requests = synth.Snapshot(), activeRetentionCalls = synth.ActiveRetentionCalls,
                elapsedSeconds = overall.Elapsed.TotalSeconds, realAudioOutput = true, audioVolume = .35,
                textSource = "fixed_public_test_phrases", modelBackend = "faster_qwen3_tts",
                realOllamaRequests = 0, realAccountRequests = 0, microphoneOpened = false, userSettingsWrites = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(result);
        };
        app.Run(); return result;
    }

    private static bool Gone(int pid)
    { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
    private static async Task UntilAsync(Func<bool> condition, CancellationToken token, int seconds = 10)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }

    private sealed record RunReport(double? FirstPcmSeconds, double? FirstPlaybackStartSeconds, double ElapsedSeconds,
        double AudioSeconds, int PcmChunks, int PlaybackStarts, int RebufferCount, double[] RebufferSeconds);
    private sealed class PlaybackObservation
    {
        private readonly object _guard = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double? _firstPcm, _firstPlayback, _rebufferStart;
        private double _audioSeconds;
        private int _chunks, _starts, _refills;
        private readonly List<double> _refillDurations = new();
        internal readonly TaskCompletionSource FirstPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Chunk(PetPcmChunk chunk)
        { lock (_guard) { _firstPcm ??= _clock.Elapsed.TotalSeconds; _chunks++; _audioSeconds += (double)chunk.Data.Length / chunk.Format.ByteRate; } }
        internal void State(bool playing)
        {
            lock (_guard)
            {
                if (playing)
                {
                    _firstPlayback ??= _clock.Elapsed.TotalSeconds; _starts++;
                    if (_rebufferStart is { } start) { _refillDurations.Add(_clock.Elapsed.TotalSeconds - start); _rebufferStart = null; }
                    FirstPlayback.TrySetResult();
                }
                else { _refills++; _rebufferStart ??= _clock.Elapsed.TotalSeconds; }
            }
        }
        internal void Finish(Exception? error)
        {
            lock (_guard)
            {
                if (_rebufferStart is { } start) { _refillDurations.Add(_clock.Elapsed.TotalSeconds - start); _rebufferStart = null; }
                _clock.Stop();
            }
            if (error != null) { FirstPlayback.TrySetException(error); Done.TrySetException(error); }
            else Done.TrySetResult();
        }
        internal RunReport Snapshot()
        { lock (_guard) return new(_firstPcm, _firstPlayback, _clock.Elapsed.TotalSeconds, _audioSeconds, _chunks, _starts, _refills, _refillDurations.ToArray()); }
    }

    private sealed class ObservedOutput : IPetPcmStreamPlayer
    {
        private readonly PetPcmStreamPlayer _inner = new();
        private readonly object _guard = new();
        private readonly List<PlaybackObservation> _runs = new();
        internal int Count { get { lock (_guard) return _runs.Count; } }
        internal RunReport[] Snapshot() { lock (_guard) return _runs.Select(run => run.Snapshot()).ToArray(); }
        internal async Task<PlaybackObservation> WaitForCallAsync(int count, CancellationToken token)
        { await UntilAsync(() => Count >= count, token); lock (_guard) return _runs[count - 1]; }
        public async Task PlayAsync(IAsyncEnumerable<PetPcmChunk> source, double volume, Action<bool> state, CancellationToken token)
        {
            var run = new PlaybackObservation(); lock (_guard) _runs.Add(run);
            async IAsyncEnumerable<PetPcmChunk> Observe([EnumeratorCancellation] CancellationToken ct)
            { await foreach (var chunk in source.WithCancellation(ct).ConfigureAwait(false)) { run.Chunk(chunk); yield return chunk; } }
            try { await _inner.PlayAsync(Observe(token), volume, playing => { run.State(playing); state(playing); }, token); run.Finish(null); }
            catch (Exception ex) { run.Finish(ex); throw; }
        }
        public void SetVolume(double volume) => _inner.SetVolume(volume);
        public void Stop() => _inner.Stop();
        public void Dispose() => _inner.Dispose();
    }

    private sealed record RequestReport(string Text, double? FirstPcmSeconds, double TransportSeconds, double AudioSeconds, int PcmChunks);
    private sealed class ObservedSynth(PetVoiceClient client) : IPetSpeechSynthesizer, IPetStreamingSpeechSynthesizer, IPetVoiceLifecycle
    {
        private readonly object _guard = new();
        private readonly List<RequestReport> _requests = new();
        private int _retention, _legacy;
        public bool SupportsStreaming => client.SupportsStreaming;
        internal int ActiveRetentionCalls => Volatile.Read(ref _retention);
        internal int LegacyCalls => Volatile.Read(ref _legacy);
        internal RequestReport[] Snapshot() { lock (_guard) return _requests.ToArray(); }
        public async IAsyncEnumerable<PetPcmChunk> StreamAsync(string text, string id, [EnumeratorCancellation] CancellationToken token)
        {
            var clock = Stopwatch.StartNew(); double? first = null; double audioSeconds = 0; int chunks = 0;
            try
            {
                await foreach (var chunk in client.StreamAsync(text, id, token).ConfigureAwait(false))
                { first ??= clock.Elapsed.TotalSeconds; chunks++; audioSeconds += (double)chunk.Data.Length / chunk.Format.ByteRate; yield return chunk; }
            }
            finally { lock (_guard) _requests.Add(new(text, first, clock.Elapsed.TotalSeconds, audioSeconds, chunks)); }
        }
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token)
        { Interlocked.Increment(ref _legacy); return client.SynthesizeAsync(text, id, token); }
        public Task PreloadAsync(string id, CancellationToken token) => client.PreloadAsync(id, token);
        public async Task SetRetentionAsync(bool active, CancellationToken token)
        { await client.SetRetentionAsync(active, token); if (active) Interlocked.Increment(ref _retention); }
        public Task UnloadAsync(CancellationToken token) => client.UnloadAsync(token);
        public Task CancelAsync(string id) => client.CancelAsync(id);
        public Task ReleaseAsync() => client.ReleaseAsync();
        public void Dispose() => client.Dispose();
    }
}
