using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopPet;

internal static class PetVoiceInputChecks
{
    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>(); int result = 1;
        void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        app.Startup += async (_, _) =>
        {
            string? error = null;
            var recorder = new Recorder(); var recognizer = new Recognizer();
            using var session = new PetVoiceInputSession(app.Dispatcher, recorder, recognizer);
            var transcripts = new List<string>();
            session.Transcribed += transcripts.Add;
            try
            {
                Require(recorder.Starts == 0 && recognizer.Calls == 0 && session.State == PetVoiceInputState.Idle, "constructing_chat_never_opens_microphone_or_model");
                bool mutedBeforeRecording = false;
                session.Changed += () => { if (session.State == PetVoiceInputState.Recording && recorder.Starts == 0) mutedBeforeRecording = true; };
                session.Start();
                Require(mutedBeforeRecording && recorder.IsRecording && session.State == PetVoiceInputState.Recording, "host_is_notified_before_hardware_recording_starts");
                session.Start(); Require(recorder.Starts == 1, "duplicate_start_cannot_open_second_microphone");
                await session.StopAsync();
                Require(!recorder.IsRecording && session.State == PetVoiceInputState.Idle && transcripts.Count == 1 && transcripts[0] == Recognizer.Answer, "stop_releases_microphone_and_delivers_transcript_once");
                await session.StopAsync(); Require(recognizer.Calls == 1, "duplicate_stop_does_not_transcribe_twice");
                session.Start(); session.Cancel();
                Require(!recorder.IsRecording && recognizer.Calls == 1 && session.State == PetVoiceInputState.Idle, "cancel_during_recording_discards_audio_without_transcription");

                recognizer.Pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Start(); Task old = session.StopAsync();
                Require(session.State == PetVoiceInputState.Transcribing, "transcribing_state_is_visible");
                session.Cancel(); session.Start();
                recognizer.Pending.SetResult("旧会话不应收到这段话"); await old;
                Require(transcripts.Count == 1 && session.State == PetVoiceInputState.Recording, "late_result_cannot_enter_new_recording_or_conversation");
                recognizer.Pending = null; session.Cancel();

                session.Start(); Action? oldLimit = recorder.CapturedLimit;
                session.Cancel(); session.Start(); oldLimit?.Invoke(); await Task.Delay(30);
                Require(session.State == PetVoiceInputState.Recording, "stale_limit_event_cannot_stop_new_recording");
                recorder.ReachLimit();
                await Until(() => session.State == PetVoiceInputState.Idle);
                Require(transcripts.Count == 2, "sixty_second_limit_automatically_finishes_and_transcribes");

                recognizer.Result = ""; session.Start(); await session.StopAsync();
                Require(transcripts.Count == 2 && session.StatusText.Contains("没有识别"), "silence_does_not_insert_hallucinated_or_empty_message");
                recorder.Short = true; int calls = recognizer.Calls;
                session.Start(); await session.StopAsync();
                Require(recognizer.Calls == calls && session.StatusText.Contains("太短"), "very_short_recording_never_loads_model"); recorder.Short = false;
                recorder.StartError = true; session.Start();
                Require(session.State == PetVoiceInputState.Idle && session.StatusText.Contains("麦克风"), "microphone_failure_restores_idle_state"); recorder.StartError = false;
                int cancellations = recorder.Cancels;
                session.Start(); recorder.StopError = true; recorder.Unplug();
                await Until(() => session.State == PetVoiceInputState.Idle);
                Require(session.StatusText.Contains("断开"), "device_loss_is_reported_and_recording_does_not_continue"); recorder.StopError = false;
                Require(recorder.Cancels > cancellations, "failed_stop_explicitly_discards_and_cleans_capture_before_retry");
                recognizer.Fail = true; session.Start(); await session.StopAsync();
                Require(session.State == PetVoiceInputState.Idle && session.StatusText.Contains("识别失败"), "recognizer_error_is_recoverable_without_losing_draft");
                recognizer.Fail = false; recognizer.Result = Recognizer.Answer;
                session.Start(); await session.StopAsync();
                Require(transcripts.Count == 3, "recording_can_be_retried_after_error");
                Require(PetSpeechRecognizer.ParseResult("{\"text\":\"中文测试\",\"language\":\"zh\"}") == "中文测试", "utf8_chinese_result_parses");
                bool badResult = false; try { PetSpeechRecognizer.ParseResult("{\"message\":\"missing text\"}"); } catch (InvalidOperationException) { badResult = true; }
                Require(badResult, "invalid_result_is_not_inserted_as_text");
                bool badAudio = false; try { PetSpeechRecognizer.ValidateWave(new byte[44]); } catch (InvalidOperationException) { badAudio = true; }
                Require(badAudio, "invalid_recording_is_rejected_before_launch");
                Require(StartupModePolicy.IsIsolatedDiagnostic(new[] { "--voice-input-test" }) && StartupModePolicy.IsIsolatedDiagnostic(new[] { "--voice-input-live-test" }), "voice_input_checks_are_isolated_from_pairing");
                session.Dispose(); Require(recorder.Disposed && recognizer.Disposed, "shutdown_releases_capture_and_recognizer");
                result = 0;
            }
            catch (Exception ex) { error = ex.ToString(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "voice-input-validation.json"), JsonSerializer.Serialize(new
            { passed = result == 0, assertions = checks.Count, checks, error, realMicrophoneOpened = false, realAccountRequests = 0, userSettingsWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(result);
        };
        app.Run(); return result;
    }

    internal static int RunLive() => RunLiveAsync().GetAwaiter().GetResult();
    private static async Task<int> RunLiveAsync()
    {
        var clock = Stopwatch.StartNew(); string? text = null, error = null; int result = 1;
        using var recognizer = new PetSpeechRecognizer();
        try
        {
            string? fixture = null;
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "development-checks", "asr-engine", "fixture-16k.wav");
                if (File.Exists(candidate)) { fixture = candidate; break; }
            }
            if (fixture == null) throw new InvalidOperationException("缺少固定测试音频，不会自动打开麦克风。");
            text = await recognizer.TranscribeAsync(await File.ReadAllBytesAsync(fixture), CancellationToken.None);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("固定语音样本未识别出文字。");
            result = 0;
        }
        catch (Exception ex) { error = ex.Message; }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "voice-input-live-validation.json"), JsonSerializer.Serialize(new
        { passed = result == 0, elapsedSeconds = clock.Elapsed.TotalSeconds, fixedFixtureText = text, error, realMicrophoneOpened = false, realModelUsed = true, realAccountRequests = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }

    private sealed class Recorder : IPetMicrophoneRecorder
    {
        public bool IsRecording { get; private set; }
        public event Action? LimitReached;
        internal Action? CapturedLimit => LimitReached;
        internal int Starts, Cancels;
        internal bool Short, StartError, StopError, Disposed;
        public void Start() { Starts++; if (StartError) throw new InvalidOperationException("麦克风不可用。"); IsRecording = true; }
        public byte[] Stop() { IsRecording = false; if (StopError) throw new InvalidOperationException("麦克风已断开。"); return MakeWave(Short ? 1 : 16_000); }
        public void Cancel() { Cancels++; IsRecording = false; }
        public void Dispose() { Cancel(); Disposed = true; }
        internal void ReachLimit() { IsRecording = false; LimitReached?.Invoke(); }
        internal void Unplug() => IsRecording = false;
    }

    private sealed class Recognizer : IPetSpeechRecognizer
    {
        internal const string Answer = "请帮我解释一下这张图片。";
        internal string Result = Answer;
        internal int Calls;
        internal bool Fail, Disposed;
        internal TaskCompletionSource<string>? Pending;
        public async Task<string> TranscribeAsync(byte[] wave, CancellationToken token)
        { Calls++; if (Pending != null) return await Pending.Task; await Task.Delay(5, token); if (Fail) throw new InvalidOperationException("语音识别失败。"); return Result; }
        public void Dispose() => Disposed = true;
    }

    private static byte[] MakeWave(int samples)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16_000); writer.Write(32_000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); writer.Write(new byte[samples * 2]);
        return stream.ToArray();
    }
}
