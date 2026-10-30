using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPet;

internal static class PetSpeechChecks
{
    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>(); int result = 1;
        void Require(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        app.Startup += async (_, _) =>
        {
            string? error = null;
            try
            {
                await PetVoiceHealthChecks.RunAsync(app.Dispatcher, Require);
                await PetSpeechStreamingChecks.RunAsync(app.Dispatcher, Require);
                await PetVoiceProfileChecks.RunAsync(app.Dispatcher, Require);
                PetVoiceLibraryChecks.Run(Require);
                await PetVoiceStudioChecks.RunAsync(app.Dispatcher, Require);
                await CheckLatestUtteranceAsync(app.Dispatcher, Require);
                string fence = new((char)96, 3);
                string normalized = PetSpeechText.Normalize("<think>秘密思考</think> **任务完成**。[1]\n[天气](https://example.com)\n" + fence + "python\nprint('代码')\n" + fence + "\n![图](https://example.com/a.png)");
                Require(normalized == "任务完成。天气", "only_visible_prose_no_thought_code_citations_urls_or_images");
                string longText = new string('鲸', 229) + "🐳一起吃饭。";
                var chunks = PetSpeechText.Split(longText);
                Require(string.Concat(chunks) == longText && chunks.All(chunk => chunk.Length <= 101 && !char.IsLowSurrogate(chunk[0]) && !char.IsHighSurrogate(chunk[^1])), "full_answer_chunks_preserve_unicode_and_all_text");
                string shortSentences = "今天的任务完成啦！我们一起吃饭吧。吃完再继续工作！";
                Require(PetSpeechText.Split(shortSentences).SequenceEqual(new[] { shortSentences }), "short_sentences_share_one_synthesis_without_losing_punctuation");
                using (var joined = new PetSpeechWave.Builder())
                {
                    joined.Append(Synth.Wave); joined.Append(Synth.Wave);
                    byte[] wave = joined.Complete();
                    Require(wave.Length == 60 && wave.AsSpan(44, 8).SequenceEqual(Synth.Wave.AsSpan(44)) &&
                        wave.AsSpan(52, 8).SequenceEqual(Synth.Wave.AsSpan(44)) &&
                        Math.Abs(PetSpeechWave.Duration(wave).TotalSeconds - 16d / 48000) < .000001,
                        "pcm_segments_join_in_order_with_no_added_silence_or_metadata");
                }
                using (var bounded = new PetSpeechWave.Builder(52))
                {
                    bounded.Append(Synth.Wave);
                    bool rejected = false;
                    try { bounded.Append(Synth.Wave); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("过长"); }
                    Require(rejected && PetSpeechWave.MaximumBufferedBytes == 64 * 1024 * 1024 && bounded.Complete().Length == 52,
                        "buffer_limit_rejects_overflow_without_silent_partial_playback");
                }
                using (var mismatched = new PetSpeechWave.Builder())
                {
                    mismatched.Append(Synth.Wave);
                    byte[] otherRate = (byte[])Synth.Wave.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(otherRate.AsSpan(24), 16000);
                    BinaryPrimitives.WriteUInt32LittleEndian(otherRate.AsSpan(28), 32000);
                    bool rejected = false;
                    try { mismatched.Append(otherRate); } catch (InvalidOperationException) { rejected = true; }
                    Require(rejected, "different_pcm_formats_are_never_joined");
                }
                bool truncatedRejected = false;
                try { PetSpeechWave.Duration(Synth.Wave[..^1]); } catch (InvalidOperationException) { truncatedRejected = true; }
                Require(truncatedRejected, "truncated_pcm_payload_is_rejected_before_playback");
                byte[] longWave = new byte[44 + 8000 * 2 * 301];
                Synth.Wave.AsSpan(0, 44).CopyTo(longWave);
                BinaryPrimitives.WriteUInt32LittleEndian(longWave.AsSpan(4), (uint)longWave.Length - 8);
                BinaryPrimitives.WriteUInt32LittleEndian(longWave.AsSpan(24), 8000);
                BinaryPrimitives.WriteUInt32LittleEndian(longWave.AsSpan(28), 16000);
                BinaryPrimitives.WriteUInt32LittleEndian(longWave.AsSpan(40), (uint)longWave.Length - 44);
                Require(PetSpeechWave.Duration(longWave) == TimeSpan.FromSeconds(301), "whole_reply_duration_can_exceed_old_five_minute_playback_timeout");
                bool invalidRejected = false; try { PetVoiceClient.ValidateWave(new byte[44]); } catch (InvalidOperationException) { invalidRejected = true; }
                Require(invalidRejected, "non_audio_server_response_is_rejected");
                using var body = PetVoiceClient.JsonBody(new { input = "中文参考声音", voice = "deepseek-let-me-go", request_id = "fixture" });
                Require(body.Headers.ContentLength == (await body.ReadAsByteArrayAsync()).Length && body.Headers.ContentLength > 0, "json_requests_have_known_byte_length_for_strict_local_server");
                Require(StartupModePolicy.IsIsolatedDiagnostic(new[] { "--speech-test" }) && StartupModePolicy.IsIsolatedDiagnostic(new[] { "--speech-ui-test" }), "speech_diagnostics_cannot_touch_real_pairing");
                var settings = new DesktopSettings { SkinId = "deepseek", SpeechEnabled = false };
                var synth = new Synth(); var player = new Player();
                using var voice = new PetSpeechService(settings, app.Dispatcher, synth, player);
                voice.Speak("关闭时不能发声", PetSpeechSource.Chat);
                Require(synth.Texts.Count == 0, "disabled_speech_never_starts_synthesis");
                settings.SpeechEnabled = true; settings.SkinId = "gemini"; voice.ApplySettings();
                voice.Speak("其他角色不能发声", PetSpeechSource.Preview);
                Require(synth.Texts.Count == 0, "other_skin_never_uses_deepseek_voice");
                settings.SkinId = "deepseek"; voice.ApplySettings();
                voice.Speak(longText, PetSpeechSource.Chat);
                int speechChunks = PetSpeechText.Split(PetSpeechText.Normalize(longText), 100).Count;
                await Until(() => synth.Texts.Count == speechChunks && !voice.IsSpeaking && voice.StatusText.Contains("待命"));
                Require(string.Concat(synth.Texts) == PetSpeechText.Normalize(longText) && player.Played == 1 &&
                    player.LastWave?.Length == 44 + speechChunks * 8, "long_chat_read_completely_in_one_continuous_playback");
                Require(player.MaximumConcurrent == 1, "playback_never_overlaps");

                synth.Hold = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                synth.HoldAtCall = synth.Texts.Count + 2;
                int bufferedBefore = player.Played;
                voice.Speak(longText, PetSpeechSource.Chat);
                await Until(() => synth.Held);
                Require(player.Played == bufferedBefore && !voice.IsSpeaking && voice.StatusText.Contains("（1/"),
                    "slow_next_sentence_is_buffered_before_audio_starts_instead_of_causing_mid_reply_silence");
                synth.Hold.SetResult(Synth.Wave);
                await Until(() => player.Played == bufferedBefore + 1 && voice.StatusText.Contains("待命"));
                Require(player.LastWave?.Length == 44 + speechChunks * 8, "all_buffered_sentences_play_together_after_synthesis_finishes");
                synth.Hold = null; synth.HoldAtCall = null; synth.Held = false;

                synth.Hold = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                synth.HoldAtCall = synth.Texts.Count + 2;
                bufferedBefore = player.Played;
                voice.Speak(longText, PetSpeechSource.Chat);
                await Until(() => synth.Held);
                settings.SpeechEnabled = false; voice.ApplySettings();
                synth.Hold.SetResult(Synth.Wave); await Task.Delay(30);
                Require(player.Played == bufferedBefore && !voice.IsSpeaking, "toggle_off_discards_every_part_of_a_partially_buffered_reply");
                synth.Hold = null; synth.HoldAtCall = null; synth.Held = false;
                settings.SpeechEnabled = true; voice.ApplySettings();

                int calls = synth.Texts.Count;
                voice.Speak("这次任务已经完成啦！", PetSpeechSource.Preset, "completed-task-fixture");
                voice.Speak("这次任务已经完成啦！", PetSpeechSource.Preset, "completed-task-fixture");
                await Until(() => synth.Texts.Count > calls && voice.StatusText.Contains("待命"));
                Require(synth.Texts.Count == calls + 1, "explicitly_identified_duplicate_notice_is_not_read_twice");

                synth.Hold = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                int before = player.Played;
                voice.Speak("旧回答正在生成", PetSpeechSource.Chat);
                await Until(() => synth.Held);
                voice.Stop(); synth.Hold.SetResult(Synth.Wave);
                await Task.Delay(30);
                Require(player.Played == before && synth.Cancelled > 0, "late_synthesis_result_cannot_play_after_stop");
                synth.Hold = null; synth.Held = false;

                player.Hold = true;
                voice.Speak("这段声音应该立即停止", PetSpeechSource.Chat);
                await Until(() => voice.IsSpeaking);
                settings.SpeechEnabled = false; voice.ApplySettings();
                Require(!voice.IsSpeaking && player.Active == 0, "toggle_off_stops_active_playback_immediately");
                settings.SpeechEnabled = true; voice.ApplySettings(); player.Hold = false;
                await Task.Delay(30);
                before = player.Played;
                voice.SetSuspended(true); voice.Speak("锁屏期间不能排队朗读", PetSpeechSource.Chat);
                voice.SetSuspended(false); await Task.Delay(20);
                Require(player.Played == before, "resume_does_not_replay_suspended_messages");
                synth.ReleaseHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                settings.SpeechEnabled = false; voice.ApplySettings();
                settings.SpeechEnabled = true; voice.ApplySettings();
                calls = synth.Texts.Count;
                voice.Speak("重新开启后的新回答", PetSpeechSource.Preview);
                await Task.Delay(30);
                Require(synth.Texts.Count == calls, "new_synthesis_waits_for_previous_unload_acknowledgement");
                synth.ReleaseHold.SetResult(); synth.ReleaseHold = null;
                await Until(() => player.Played > before && voice.StatusText.Contains("待命"));
                before = player.Played;
                synth.Fail = true;
                voice.Speak("失败仍保留文字回答", PetSpeechSource.Chat);
                await Until(() => voice.StatusText == "模拟合成失败");
                synth.Fail = false;
                voice.Speak("重试能够恢复", PetSpeechSource.Preview);
                await Until(() => player.Played > before && voice.StatusText.Contains("待命"));
                Require(!voice.IsSpeaking, "backend_failure_is_recoverable");
                voice.Dispose(); Require(synth.Disposed && player.Disposed, "exit_disposes_audio_and_owned_backend");
                result = 0;
            }
            catch (Exception ex) { error = ex.ToString(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "speech-validation.json"), JsonSerializer.Serialize(new
            { passed = result == 0, assertions = checks.Count, checks, error, realAudioOutput = false, realModelRequests = 0, userSettingsWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(result);
        };
        app.Run(); return result;
    }

    private static async Task CheckLatestUtteranceAsync(Dispatcher dispatcher, Action<bool, string> require)
    {
        var settings = new DesktopSettings { SkinId = "deepseek", SpeechEnabled = true };
        var synth = new Synth(); var player = new Player { Hold = true };
        using var voice = new PetSpeechService(settings, dispatcher, synth, player);
        voice.Speak("旧的完整回答正在播放。", PetSpeechSource.Chat);
        await Until(() => voice.IsSpeaking && player.Played == 1);
        voice.Speak("新的点击提示。", PetSpeechSource.Preset);
        require(!voice.IsSpeaking && player.Active == 0, "new_preset_immediately_stops_an_older_chat_wav");
        await Until(() => voice.IsSpeaking && player.Played == 2);
        voice.Speak("现在播放新的试听。", PetSpeechSource.Preview);
        require(player.Active == 0, "preview_immediately_replaces_active_preset_wav");
        await Until(() => voice.IsSpeaking && player.Played == 3);
        voice.Speak("重复点击也要重新回答。", PetSpeechSource.Preset);
        await Until(() => voice.IsSpeaking && player.Played == 4);
        voice.Speak("重复点击也要重新回答。", PetSpeechSource.Preset);
        require(player.Active == 0, "same_text_click_interrupts_and_restarts_without_implicit_deduplication");
        await Until(() => voice.IsSpeaking && player.Played == 5);
        require(synth.Texts.Count(text => text == "重复点击也要重新回答") == 1 && player.MaximumConcurrent == 1,
            "repeated_preset_reuses_voice_cache_without_overlapping_playback");
        voice.Stop(); await Task.Delay(20);

        var lateWave = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        synth.Hold = lateWave; synth.HoldAtCall = synth.Texts.Count + 1; synth.Held = false;
        int before = player.Played;
        voice.Speak("旧合成忽略取消并很晚才返回。", PetSpeechSource.Chat);
        await Until(() => synth.Held);
        synth.Hold = null;
        voice.Speak("被更新提示替换的待播消息。", PetSpeechSource.Preset);
        voice.Speak("只应播放最新消息。", PetSpeechSource.Preset);
        await Until(() => voice.IsSpeaking && player.Played == before + 1);
        require(!lateWave.Task.IsCompleted && synth.Cancelled > 0,
            "latest_message_bypasses_cancelled_synthesis_and_sends_backend_cancellation");
        // The dispatcher may resume the cancelled pump inline. In that case
        // the intermediate message legitimately starts preparing immediately;
        // the next call must cancel it before it ever reaches playback.
        bool middleCancelled = !synth.RequestTokens.TryGetValue("被更新提示替换的待播消息", out var middleToken) || middleToken.IsCancellationRequested;
        require(middleCancelled && synth.Texts.Last() == "只应播放最新消息" && player.Played == before + 1,
            "intermediate_message_is_discarded_or_cancelled_and_only_latest_reaches_playback");
        lateWave.SetResult(Synth.Wave); await Task.Delay(30);
        require(player.Played == before + 1 && voice.IsSpeaking,
            "late_cancelled_wave_cannot_restart_or_stop_the_newer_utterance");
        voice.Stop();
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Synth : IPetSpeechSynthesizer
    {
        internal static readonly byte[] Wave = MakeWave();
        internal List<string> Texts = new();
        internal readonly Dictionary<string, CancellationToken> RequestTokens = new();
        internal bool Fail, Held, Disposed;
        internal int Cancelled;
        internal int? HoldAtCall;
        internal TaskCompletionSource<byte[]>? Hold;
        internal TaskCompletionSource? ReleaseHold;
        public async Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token)
        {
            Texts.Add(text); RequestTokens[text] = token;
            if (Hold != null && (HoldAtCall == null || Texts.Count == HoldAtCall)) { Held = true; return await Hold.Task; } // Deliberately ignore cancellation to model a late backend.
            await Task.Delay(5, token);
            if (Fail) throw new InvalidOperationException("模拟合成失败");
            return Wave;
        }
        public Task CancelAsync(string id) { Cancelled++; return Task.CompletedTask; }
        public Task ReleaseAsync() => ReleaseHold?.Task ?? Task.CompletedTask;
        public void Dispose() => Disposed = true;
        private static byte[] MakeWave()
        {
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.ASCII, true);
            writer.Write("RIFF"u8); writer.Write(44); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(8);
            writer.Write(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }); return memory.ToArray();
        }
    }

    private sealed class Player : IPetSpeechPlayer
    {
        internal int Played, Active, MaximumConcurrent;
        internal bool Hold, Disposed;
        internal byte[]? LastWave;
        private TaskCompletionSource? _pending;
        public async Task PlayAsync(byte[] wave, double volume, CancellationToken token)
        {
            LastWave = wave; Played++; Active++; MaximumConcurrent = Math.Max(MaximumConcurrent, Active);
            try
            {
                if (Hold) { _pending = new(TaskCreationOptions.RunContinuationsAsynchronously); await _pending.Task.WaitAsync(token); }
                else await Task.Delay(5, token);
            }
            finally { Active = 0; _pending = null; }
        }
        public void SetVolume(double volume) { }
        public void Stop() { Active = 0; _pending?.TrySetCanceled(); }
        public void Dispose() { Stop(); Disposed = true; }
    }
}
