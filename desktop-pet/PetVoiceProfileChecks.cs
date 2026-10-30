using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal static class PetVoiceProfileChecks
{
    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> require)
    {
        var settings = new DesktopSettings { SkinId = "deepseek", SpeechEnabled = true };
        require(settings.GetVoiceProfile("deepseek").ModelId == "base" &&
            settings.GetVoiceProfile("gpt").ModelId == "gpt-fixed" &&
            PetVoiceProfiles.Characters.Where(c => c.Id is not ("deepseek" or "gpt")).All(c => settings.GetVoiceProfile(c.Id).ModelId == "disabled"),
            "known_characters_use_their_own_bundled_defaults_without_enabling_unconfigured_others");
        var disabledDragon = new DesktopSettings();
        disabledDragon.SetVoiceProfile("gpt", new("disabled", "Vivian", "Chinese", ""));
        var disabledRoundtrip = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(disabledDragon))!;
        disabledRoundtrip.Normalize();
        require(disabledRoundtrip.GetVoiceProfile("gpt").ModelId == "disabled" &&
            PetVoiceProfiles.Normalize(null, "GPT").ModelId == "gpt-fixed",
            "explicitly_disabled_gpt_stays_disabled_while_missing_profile_uses_its_bundled_default");
        var dragon = new PetVoiceProfile("custom-voice", "Serena", "Chinese", "温柔自信");
        settings.SetVoiceProfile("gpt", dragon);
        var roundtrip = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings))!;
        roundtrip.Normalize();
        require(roundtrip.GetVoiceProfile("gpt") == dragon && roundtrip.GetVoiceProfile("deepseek").ModelId == "base",
            "character_voice_profiles_persist_independently_through_settings_serialization");
        var fixedDragon = PetVoiceProfiles.Normalize(new("gpt-fixed", "Vivian", "Chinese", "原设计提示词"), "gpt");
        roundtrip.SetVoiceProfile("gpt", fixedDragon);
        var fixedRoundtrip = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(roundtrip))!;
        fixedRoundtrip.Normalize();
        require(fixedRoundtrip.GetVoiceProfile("gpt") == fixedDragon && fixedDragon.Instruct == "" &&
            PetVoiceProfiles.IsEnabled(fixedDragon) && fixedRoundtrip.GetVoiceProfile("deepseek").ModelId == "base",
            "fixed_dragon_voice_persists_without_reusing_design_prompt_or_replacing_deepseek");
        require(PetVoiceProfiles.ModelFolderName("gpt-fixed") == PetVoiceProfiles.ModelFolderName("base") &&
            PetVoiceProfiles.ModelDisplayName("gpt-fixed").Contains("GPT 龙娘固定音色"),
            "fixed_dragon_selection_reuses_base_weights_with_distinct_display_name");
        string voiceId = new string('a', 32);
        var saved = PetVoiceProfiles.Normalize(new("saved-voice", "Vivian", "Chinese", "原始设计提示词", voiceId), "gpt");
        roundtrip.SetVoiceProfile("gpt", saved);
        var savedRoundtrip = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(roundtrip))!;
        savedRoundtrip.Normalize();
        require(savedRoundtrip.GetVoiceProfile("gpt") == saved && saved.ReferenceId == voiceId && saved.Instruct == "" &&
            PetVoiceProfiles.IsEnabled(saved) && savedRoundtrip.GetVoiceProfile("deepseek").ModelId == "base",
            "user_fixed_voice_preserves_opaque_reference_without_description_or_cross_character_changes");
        require(PetVoiceProfiles.ModelFolderName("saved-voice") == PetVoiceProfiles.ModelFolderName("base") &&
            PetVoiceProfiles.Normalize(saved with { ReferenceId = "../reference.wav" }, "deepseek").ModelId == "disabled" &&
            PetVoiceProfiles.Normalize(saved with { ReferenceId = voiceId.ToUpperInvariant() }, "gpt").ModelId == "disabled" &&
            !PetVoiceProfiles.IsEnabled(saved with { ReferenceId = null }) &&
            PetVoiceProfiles.Normalize(saved with { ModelId = "base" }, "deepseek").ReferenceId == null,
            "invalid_saved_reference_is_disabled_not_replaced_with_deepseek_and_other_modes_drop_reference_id");
        settings.OllamaModel = "unrelated-chat-model";
        require(settings.GetVoiceProfile("gpt") == dragon, "changing_ollama_model_does_not_change_character_voice");
        var selected = PetVoiceProfiles.Normalize(new("voice-design", "unknown", "unknown", ""), "gpt");
        require(selected.Speaker == "Vivian" && selected.Language == "Chinese" && selected.Instruct.Contains("自信") &&
            PetVoiceProfiles.Normalize(selected with { Instruct = new string('字', 700) + '\0' }, "gpt").Instruct.Length == 600,
            "voice_profile_normalization_bounds_and_sanitizes_descriptions");

        var synth = new ProfileSynth(); var player = new SilentPlayer();
        using var service = new PetSpeechService(settings, dispatcher, synth, player);
        service.Speak("相同的预设提示", PetSpeechSource.Preset);
        await Until(() => player.Played == 1);
        require(synth.Spoken.Count == 1 && synth.Spoken[0].Profile.ModelId == "base", "deepseek_first_preset_uses_original_base_profile");
        service.Speak("相同的预设提示", PetSpeechSource.Preset);
        await Until(() => player.Played == 2);
        require(synth.Spoken.Count == 1, "unchanged_profile_can_reuse_its_in_memory_preset_audio");

        settings.SkinId = "gpt"; service.ApplySettings();
        service.Speak("相同的预设提示", PetSpeechSource.Preset);
        await Until(() => player.Played == 3);
        require(synth.Spoken.Count == 2 && synth.Spoken[^1].Profile == dragon,
            "switching_character_discards_cached_other_character_voice");
        synth.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = synth.Hold;
        long oldId = service.BeginChatResponse(); service.AppendChatResponse(oldId, "旧角色的回答。"); service.CompleteChatResponse(oldId);
        await Until(() => synth.Spoken.Count == 3);
        var designed = new PetVoiceProfile("voice-design", "Vivian", "Chinese", "清亮有主见的女声");
        settings.SetVoiceProfile("gpt", designed); service.ApplySettings();
        synth.Hold = null;
        service.AppendChatResponse(oldId, "迟到的旧回答。"); service.CompleteChatResponse(oldId);
        service.Speak("新音色的回答", PetSpeechSource.Preview);
        await Until(() => player.Played == 4);
        require(synth.Spoken.Count == 4 && synth.Spoken[^1].Profile == designed && synth.Cancelled > 0,
            "voice_switch_cancels_inflight_response_and_routes_new_text_to_selected_profile");
        gate.SetResult(); await Task.Delay(25);
        require(player.Played == 4, "late_audio_from_previous_profile_is_never_played");
        synth.FailuresRemaining = 2;
        settings.SetVoiceProfile("gpt", dragon); service.ApplySettings();
        bool unloadFailed = false;
        try { await service.UnloadAsync(); } catch (InvalidOperationException) { unloadFailed = true; }
        require(unloadFailed && synth.Profile == designed, "failed_selection_and_unload_do_not_falsely_publish_new_profile");
        service.Speak("切换失败恢复后的新音色", PetSpeechSource.Preview);
        await Until(() => player.Played == 5);
        require(synth.Spoken[^1].Profile == dragon, "failed_profile_barrier_is_retried_before_next_speech_after_unload_failure");
        settings.SetVoiceProfile("gpt", designed with { ModelId = "disabled" }); service.ApplySettings();
        service.Speak("禁用角色不会合成", PetSpeechSource.Chat);
        await Task.Delay(20);
        require(synth.Spoken.Count == 5, "disabled_character_profile_blocks_synthesis");
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(5, timeout.Token); }

    private sealed class ProfileSynth : IPetSpeechSynthesizer, IPetVoiceProfileSelector
    {
        internal PetVoiceProfile Profile = PetVoiceProfiles.DefaultForSkin("deepseek");
        internal readonly List<(string Text, PetVoiceProfile Profile)> Spoken = new();
        internal TaskCompletionSource? Hold;
        internal int Cancelled;
        internal int FailuresRemaining;
        public Task SelectProfileAsync(PetVoiceProfile profile, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailuresRemaining-- > 0) return Task.FromException(new InvalidOperationException("Fixture selection failed")); Profile = profile; return Task.CompletedTask; }
        public async Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token)
        {
            Spoken.Add((text, Profile));
            if (Hold is { } hold) await hold.Task; // Intentionally ignore cancellation to test stale results.
            return Wave();
        }
        public Task CancelAsync(string id) { Cancelled++; return Task.CompletedTask; }
        public Task ReleaseAsync() => Task.CompletedTask;
        public void Dispose() { }
        private static byte[] Wave()
        {
            using var stream = new System.IO.MemoryStream(); using var writer = new System.IO.BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(40); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(4); writer.Write(0);
            return stream.ToArray();
        }
    }
    private sealed class SilentPlayer : IPetSpeechPlayer
    {
        internal int Played;
        public Task PlayAsync(byte[] wave, double volume, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Played++; return Task.CompletedTask; }
        public void Stop() { }
        public void SetVolume(double volume) { }
        public void Dispose() { }
    }
}
