using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Explicit diagnostic of the output device only. PCM is generated in memory;
// this class has no TTS client, Ollama client, microphone or settings store.
internal static class PetPcmLiveChecks
{
    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var checks = new List<string>();
        void Require(bool value, string name)
        { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        var overall = Stopwatch.StartNew();
        string phase = "isolation", errorType = "", error = "";
        double? firstPlaybackSeconds = null, continuousElapsedSeconds = null;
        double? cancelSeconds = null, disposeSeconds = null;
        int result = 1;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var player = new PetPcmStreamPlayer();
        try
        {
            Require(StartupModePolicy.IsIsolatedDiagnostic(new[] { "--speech-pcm-live-test" }),
                "pcm_device_diagnostic_is_isolated_from_real_app_state");
            phase = "continuous_silence";
            var clock = Stopwatch.StartNew();
            int starts = 0, refills = 0;
            await player.PlayAsync(Generate(1500, false, limit.Token), .05, playing =>
            {
                if (playing) { Interlocked.Increment(ref starts); firstPlaybackSeconds ??= clock.Elapsed.TotalSeconds; }
                else Interlocked.Increment(ref refills);
            }, limit.Token).WaitAsync(TimeSpan.FromSeconds(8), limit.Token);
            continuousElapsedSeconds = clock.Elapsed.TotalSeconds;
            Require(starts == 1 && refills == 0, "continuous_pcm_uses_one_start_without_chunk_restarts_or_underruns");
            Require(firstPlaybackSeconds is >= 0 and < 5 && continuousElapsedSeconds >= 1.2,
                "native_output_waits_for_audio_completion_instead_of_only_enqueueing");

            phase = "cancel_playback";
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            var started = NewSignal();
            Task playback = player.PlayAsync(Generate(10000, false, cancelled.Token), .05,
                playing => { if (playing) started.TrySetResult(); }, cancelled.Token);
            await AwaitStartAsync(playback, started.Task, limit.Token);
            await Task.Delay(120, limit.Token);
            clock.Restart(); cancelled.Cancel();
            bool stopped = false;
            try { await playback.WaitAsync(TimeSpan.FromSeconds(4), limit.Token); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested && !limit.IsCancellationRequested) { stopped = true; }
            cancelSeconds = clock.Elapsed.TotalSeconds;
            Require(stopped && cancelSeconds < 3, "cancellation_resets_native_device_and_finishes_promptly");

            phase = "replay_muted";
            started = NewSignal();
            // Nonzero source samples exercise the software volume=0 path.
            // They are multiplied to zero before being copied to waveOut.
            player.SetVolume(0);
            playback = player.PlayAsync(Generate(350, true, limit.Token), 0,
                playing => { if (playing) started.TrySetResult(); }, limit.Token);
            await playback.WaitAsync(TimeSpan.FromSeconds(5), limit.Token);
            Require(started.Task.IsCompletedSuccessfully, "player_reopens_after_cancellation_and_accepts_zero_volume");

            phase = "dispose_playback";
            started = NewSignal();
            playback = player.PlayAsync(Generate(10000, false, limit.Token), 0,
                playing => { if (playing) started.TrySetResult(); }, limit.Token);
            await AwaitStartAsync(playback, started.Task, limit.Token);
            clock.Restart(); player.Dispose();
            stopped = false;
            try { await playback.WaitAsync(TimeSpan.FromSeconds(4), limit.Token); }
            catch (OperationCanceledException) when (!limit.IsCancellationRequested) { stopped = true; }
            disposeSeconds = clock.Elapsed.TotalSeconds;
            Require(stopped && disposeSeconds < 3, "dispose_cancels_active_native_playback_without_model_or_microphone_access");
            bool rejected = false;
            try { await player.PlayAsync(Generate(100, false, limit.Token), 0, _ => { }, limit.Token); }
            catch (ObjectDisposedException) { rejected = true; }
            Require(rejected, "disposed_stream_player_cannot_open_another_device");
            result = 0; phase = "complete";
        }
        catch (Exception ex) { errorType = ex.GetType().Name; error = ex.Message; }
        finally { player.Stop(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "speech-pcm-live-validation.json"), JsonSerializer.Serialize(new
        {
            passed = result == 0, assertions = checks.Count, checks, phase,
            firstPlaybackSeconds, continuousElapsedSeconds, cancelSeconds, disposeSeconds,
            elapsedSeconds = overall.Elapsed.TotalSeconds, errorType, error,
            realAudioOutputDevice = true, audibleTestTone = false,
            pcmSource = "in_memory_silence_and_zero_volume_sine", microphoneOpened = false,
            realModelRequests = 0, realAccountRequests = 0, userSettingsWrites = 0
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task AwaitStartAsync(Task playback, Task started, CancellationToken token)
    {
        Task first = await Task.WhenAny(playback, started).WaitAsync(TimeSpan.FromSeconds(5), token);
        if (ReferenceEquals(first, playback)) await playback;
        await started.WaitAsync(TimeSpan.FromSeconds(5), token);
    }

    private static async IAsyncEnumerable<PetPcmChunk> Generate(int milliseconds, bool nonzero,
        [EnumeratorCancellation] CancellationToken token)
    {
        const int rate = 24000, samplesPerChunk = 2880; // 120 ms, deliberately crosses 100 ms device blocks.
        int total = rate * milliseconds / 1000;
        for (int offset = 0; offset < total; offset += samplesPerChunk)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(samplesPerChunk, total - offset);
            var pcm = new byte[count * 2];
            if (nonzero)
                for (int sample = 0; sample < count; sample++)
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 2, 2),
                        (short)(Math.Sin(2 * Math.PI * 220 * (offset + sample) / rate) * 1000));
            yield return new(new(rate, 1), pcm);
            await Task.Yield();
        }
    }
}
