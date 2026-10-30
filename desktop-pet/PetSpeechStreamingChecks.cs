using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal static class PetSpeechStreamingChecks
{
    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> require)
    {
        const string format = "{\"type\":\"format\",\"sample_rate\":24000,\"channels\":1,\"sample_format\":\"s16le\"}\n";
        const string pcm = "{\"type\":\"audio\",\"data\":\"AQACAA==\"}\n";
        const string done = "{\"type\":\"done\"}\n";
        var decoded = new List<PetPcmChunk>();
        using (var input = new FragmentedStream(Encoding.UTF8.GetBytes(format + pcm + pcm + done)))
            await foreach (var part in PetPcmProtocol.ReadAsync(input, CancellationToken.None)) decoded.Add(part);
        require(decoded.Count == 2 && decoded.All(item => item.Format == new PetPcmFormat(24000, 1) && item.Data.SequenceEqual(new byte[] { 1, 0, 2, 0 })),
            "fragmented_stream_records_preserve_pcm_order_and_format");
        foreach (var invalid in new[] { format + pcm, pcm + done, format + format + pcm + done, format + pcm + done + pcm,
            format + "{\"type\":\"audio\",\"data\":\"AQ==\"}\n" + done })
        {
            bool rejected = false;
            try
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
                await foreach (var part in PetPcmProtocol.ReadAsync(input, CancellationToken.None)) { }
            }
            catch (InvalidOperationException) { rejected = true; }
            require(rejected, "malformed_or_truncated_pcm_stream_is_rejected");
        }
        var policy = new PetPcmBufferPolicy();
        policy.Append(24000);
        require(!policy.ShouldStart(48000, false) && policy.ShouldStart(48000, true), "stream_short_final_clip_can_start_but_partial_half_second_waits");
        policy.Append(24000);
        require(policy.ShouldStart(48000, false), "stream_starts_at_one_second_prebuffer");
        policy.Start(); policy.Complete(48000); policy.Underrun(); policy.Append(48000);
        require(!policy.ShouldStart(48000, false) && policy.StartupSeconds == 1.5, "underrun_increases_rebuffer_watermark_without_reopening_device");

        var settings = new DesktopSettings { SkinId = "deepseek", SpeechEnabled = true };
        var synth = new StreamSynth(); var output = new StreamOutput();
        using var service = new PetSpeechService(settings, dispatcher, synth, new LegacyOutput(), streamPlayer: output);
        service.SetChatSessionActive(true);
        await Until(() => synth.Preloaded == 1 && synth.Retention.LastOrDefault());
        require(synth.Preloaded == 1 && synth.Retention.Last(), "opening_chat_preloads_and_retains_voice_model_only");

        long id = service.BeginChatResponse();
        service.AppendChatResponse(id, "第一句话已经完成。");
        await Until(() => output.Chunks == 1 && service.IsSpeaking);
        require(output.Starts == 1 && synth.Texts.Count == 1, "finalized_first_text_plays_before_entire_chat_reply_completes");
        service.AppendChatResponse(id, "第二句话继续播放。"); service.CompleteChatResponse(id);
        await Until(() => output.Chunks == 2 && !service.IsSpeaking && service.StatusText.Contains("待命"));
        require(output.Starts == 1 && synth.Texts.SequenceEqual(new[] { "第一句话已经完成。", "第二句话继续播放。" }),
            "incremental_reply_uses_one_pcm_stream_for_all_sentences");
        id = service.BeginChatResponse(); service.AppendChatResponse(id, "随后取消的回答。");
        await Until(() => output.Chunks == 3);
        service.Stop(); service.AppendChatResponse(id, "过期内容不可进入新会话。"); service.CompleteChatResponse(id);
        await Task.Delay(30);
        require(synth.Texts.Count == 3 && !service.IsSpeaking, "cancelled_incremental_reply_discards_late_text_and_audio");

        int releases = synth.Releases;
        service.SetSuspended(true); await Task.Delay(20);
        require(synth.Releases == releases, "microphone_or_song_suspension_preserves_chat_retained_voice_model");
        service.SetSuspended(false);
        await service.UnloadAsync();
        service.SetChatSessionActive(false); service.SetChatSessionActive(true); await Task.Delay(20);
        require(synth.Unloaded == 1 && synth.Preloaded == 1 && !synth.Retention.Last(),
            "manual_voice_unload_is_not_undone_by_open_chat_or_retention");
        await service.PreloadAsync();
        require(synth.Preloaded == 2 && synth.Retention.Last(), "manual_preload_resumes_voice_retention_without_changing_ollama");
        id = service.BeginChatResponse(); service.AppendChatResponse(id, "卸载前正在朗读的句子。");
        await Until(() => output.Chunks == 4 && service.IsSpeaking);
        await service.PreloadAsync();
        require(service.IsSpeaking, "preloading_during_playback_preserves_speaking_status");
        await service.UnloadAsync();
        service.AppendChatResponse(id, "卸载后同一回复的后续句子不再朗读。"); service.CompleteChatResponse(id);
        await Task.Delay(30);
        require(synth.Texts.Count == 4 && !service.IsSpeaking && synth.Unloaded == 2,
            "manual_unload_invalidates_inflight_chat_id_and_late_segments_cannot_reload_voice");
        service.SetChatSessionActive(false); await service.PreloadAsync();
        require(!service.StatusText.Contains("已加载") && service.StatusText.Contains("按需加载"),
            "unretained_idle_status_does_not_claim_permanent_model_residency");

        int beforeStudioPreloads = synth.Preloaded, beforeStudioSpeech = synth.Texts.Count;
        service.SetStudioBusy(true);
        await service.UnloadAsync();
        service.SetChatSessionActive(true);
        await service.PreloadAsync();
        service.Speak("音色生成期间不要插入普通播报。", PetSpeechSource.Preset);
        await Task.Delay(20);
        require(synth.Preloaded == beforeStudioPreloads && synth.Texts.Count == beforeStudioSpeech && !synth.Retention.Last(),
            "voice_studio_excludes_chat_retention_manual_preload_and_normal_synthesis");
        service.SetStudioBusy(false);
        await service.PreloadAsync();
        require(synth.Preloaded == beforeStudioPreloads + 1 && synth.Retention.Last(),
            "normal_voice_can_be_loaded_after_voice_studio_lease_is_released");

        var isolated = new StreamSynth();
        using var isolatedService = new PetSpeechService(settings, dispatcher, isolated, new LegacyOutput(), enabledForProcess: false, streamPlayer: new StreamOutput());
        isolatedService.SetChatSessionActive(true); await isolatedService.PreloadAsync(); await isolatedService.UnloadAsync();
        require(isolated.Preloaded == 0 && isolated.Unloaded == 0 && isolated.Retention.Count == 0, "isolated_ui_fixture_never_starts_or_unloads_real_voice_models");
        await CheckStreamInterruptionAsync(dispatcher, require);
    }

    private static async Task CheckStreamInterruptionAsync(Dispatcher dispatcher, Action<bool, string> require)
    {
        var settings = new DesktopSettings { SkinId = "deepseek", SpeechEnabled = true };
        var synth = new InterruptSynth(); var output = new InterruptOutput();
        using var service = new PetSpeechService(settings, dispatcher, synth, new LegacyOutput(), streamPlayer: output);
        var oldSynthesis = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presetSynthesis = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synth.Holds["旧模型回答。"] = oldSynthesis;
        synth.Holds["新点击回应"] = presetSynthesis;
        long oldId = service.BeginChatResponse();
        service.AppendChatResponse(oldId, "旧模型回答。");
        await Until(() => output.Chunks == 1 && service.IsSpeaking);
        Action<bool> stalePlaybackState = output.LastState!;
        int stops = output.Stops;
        service.Speak("新点击回应。", PetSpeechSource.Preset);
        require(output.Stops == stops + 1 && !output.Active && !service.IsSpeaking,
            "new_preset_immediately_stops_older_pcm_and_cancels_its_reply");
        await Until(() => output.Chunks == 2 && service.IsSpeaking);
        require(!oldSynthesis.Task.IsCompleted && synth.Cancelled.Count > 0,
            "new_pcm_starts_without_waiting_for_late_cancelled_backend_chunks");
        stops = output.Stops;
        service.AppendChatResponse(oldId, "旧回答迟到的后半句。");
        service.CompleteChatResponse(oldId); service.CancelChatResponse(oldId);
        stalePlaybackState(false); oldSynthesis.SetResult();
        await Task.Delay(30);
        require(output.Chunks == 2 && output.Stops == stops && service.IsSpeaking && !synth.Texts.Contains("旧回答迟到的后半句。"),
            "old_reply_text_cancel_pcm_and_status_callbacks_cannot_modify_new_preset");

        output.HoldAtEnd = true;
        long latestId = service.BeginChatResponse();
        require(output.Stops == stops + 1 && !output.Active, "new_chat_response_immediately_preempts_active_preset_pcm");
        service.AppendChatResponse(latestId, "新回答第一句。");
        await Until(() => output.Chunks == 3 && service.IsSpeaking);
        int starts = output.Starts; stops = output.Stops;
        service.AppendChatResponse(latestId, "新回答第二句。"); service.CompleteChatResponse(latestId);
        await Until(() => output.Chunks == 4 && output.WaitingAtEnd);
        service.CancelChatResponse(oldId); presetSynthesis.SetResult();
        await Task.Delay(30);
        require(output.Starts == starts && output.Stops == stops && service.IsSpeaking && output.Chunks == 4,
            "same_reply_sentences_share_pcm_stream_and_older_cancel_does_not_interrupt_them");
        service.CancelChatResponse(latestId);
        require(!service.IsSpeaking && !output.Active && output.Stops == stops + 1,
            "completed_chat_input_retains_ownership_of_audio_until_it_is_cancelled");
        output.HoldAtEnd = false;
        long newerId = service.BeginChatResponse(); service.AppendChatResponse(newerId, "另一条新回复。");
        await Until(() => output.Chunks == 5 && service.IsSpeaking);
        stops = output.Stops; service.CancelChatResponse(latestId);
        require(service.IsSpeaking && output.Stops == stops, "late_cancel_of_previous_chat_cannot_stop_new_chat");
        service.CompleteChatResponse(newerId);
        await Until(() => !service.IsSpeaking && service.StatusText.Contains("待命"));
        require(output.MaximumConcurrent == 1, "preempted_pcm_sessions_never_overlap_playback");
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], token);
    }
    private sealed class StreamSynth : IPetSpeechSynthesizer, IPetStreamingSpeechSynthesizer, IPetVoiceLifecycle
    {
        internal readonly List<string> Texts = new();
        internal readonly List<bool> Retention = new();
        internal int Preloaded, Unloaded, Releases;
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token) => throw new InvalidOperationException("Streaming must not use the legacy path.");
        public async IAsyncEnumerable<PetPcmChunk> StreamAsync(string text, string id, [EnumeratorCancellation] CancellationToken token)
        { token.ThrowIfCancellationRequested(); Texts.Add(text); await Task.Delay(1, token); yield return new(new(24000, 1), new byte[9600]); }
        public Task PreloadAsync(string id, CancellationToken token) { token.ThrowIfCancellationRequested(); Preloaded++; return Task.CompletedTask; }
        public Task SetRetentionAsync(bool active, CancellationToken token) { Retention.Add(active); return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken token) { Unloaded++; return Task.CompletedTask; }
        public Task ReleaseAsync() { Releases++; return Task.CompletedTask; }
        public Task CancelAsync(string id) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class StreamOutput : IPetPcmStreamPlayer
    {
        internal int Starts, Chunks;
        public async Task PlayAsync(IAsyncEnumerable<PetPcmChunk> source, double volume, Action<bool> state, CancellationToken token)
        {
            Starts++;
            await foreach (var chunk in source.WithCancellation(token)) { Chunks++; state(true); }
        }
        public void SetVolume(double volume) { }
        public void Stop() { }
        public void Dispose() { }
    }
    private sealed class LegacyOutput : IPetSpeechPlayer
    {
        public Task PlayAsync(byte[] wave, double volume, CancellationToken token) => throw new InvalidOperationException("Streaming must not reopen a WAV player.");
        public void SetVolume(double volume) { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class InterruptSynth : IPetSpeechSynthesizer, IPetStreamingSpeechSynthesizer
    {
        internal readonly List<string> Texts = new();
        internal readonly List<string> Cancelled = new();
        internal readonly Dictionary<string, TaskCompletionSource> Holds = new();
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token) => throw new InvalidOperationException("Expected PCM.");
        public async IAsyncEnumerable<PetPcmChunk> StreamAsync(string text, string id, [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Texts.Add(text);
            await Task.Delay(1, token);
            yield return new(new(24000, 1), new byte[9600]);
            if (Holds.TryGetValue(text, out var held))
            {
                await held.Task; // Model a late backend that ignores cancellation.
                yield return new(new(24000, 1), new byte[9600]);
            }
        }
        public Task CancelAsync(string id) { Cancelled.Add(id); return Task.CompletedTask; }
        public Task ReleaseAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class InterruptOutput : IPetPcmStreamPlayer
    {
        internal int Starts, Stops, Chunks, MaximumConcurrent;
        private int _concurrent;
        internal bool Active, HoldAtEnd, WaitingAtEnd;
        internal Action<bool>? LastState;
        public async Task PlayAsync(IAsyncEnumerable<PetPcmChunk> source, double volume, Action<bool> state, CancellationToken token)
        {
            Starts++; Active = true; WaitingAtEnd = false; LastState = state;
            MaximumConcurrent = Math.Max(MaximumConcurrent, ++_concurrent);
            try
            {
                await foreach (var chunk in source.WithCancellation(token)) { Chunks++; state(true); }
                if (HoldAtEnd) { WaitingAtEnd = true; await Task.Delay(Timeout.Infinite, token); }
            }
            finally { Active = false; _concurrent--; }
        }
        public void SetVolume(double volume) { }
        public void Stop() { Stops++; Active = false; }
        public void Dispose() { }
    }
}
