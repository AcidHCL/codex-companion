using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal static class PetVoiceStudioChecks
{
    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> require)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "voice-studio-evidence", Guid.NewGuid().ToString("N"));
        var synth = new Synth(); var player = new Player(); int begins = 0, ends = 0;
        using var studio = new PetVoiceStudio(dispatcher, _ => { begins++; return Task.CompletedTask; }, () => ends++,
            () => synth, player, new PetVoiceLibrary(folder));
        var sample = await studio.GenerateAsync("gemini", "温柔清亮的成年女声", "你好，今天一起开始吧。", "Chinese", CancellationToken.None);
        require(synth.Profile?.ModelId == "voice-design" && sample.SkinId == "gemini" && begins == 1 && ends == 1 &&
            synth.Releases == 1 && synth.Disposes == 1 && !Directory.Exists(folder),
            "voice_studio_generation_releases_temporary_model_without_writing_character_settings_or_library");
        await studio.PlayAsync(sample, .5, CancellationToken.None);
        require(player.Played == sample.Wave && synth.Generations == 1 && begins == 2 && ends == 2,
            "voice_studio_audition_replays_identical_candidate_without_regeneration");
        var profile = studio.Save(sample, "双子 · 轻柔陪伴");
        require(profile.ModelId == "saved-voice" && File.Exists(Path.Combine(folder, profile.ReferenceId!, "reference.wav")),
            "voice_studio_explicit_save_creates_a_persistent_fixed_voice");
        var stored = studio.ListSavedVoices("gemini").Single();
        require(stored.ReferenceId == profile.ReferenceId && stored.Name == "双子 · 轻柔陪伴" &&
            studio.ListSavedVoices("gpt").Count == 0, "voice_studio_named_reference_list_is_scoped_to_its_character");
        byte[] savedWave = File.ReadAllBytes(Path.Combine(folder, profile.ReferenceId!, "reference.wav"));
        var renamed = studio.RenameSavedVoice(profile.ReferenceId!, "双子 · 清亮版");
        require(renamed.ReferenceId == profile.ReferenceId && renamed.Name == "双子 · 清亮版" &&
            studio.ReadSavedVoice("gemini")?.Name == renamed.Name &&
            File.ReadAllBytes(Path.Combine(folder, profile.ReferenceId!, "reference.wav")).SequenceEqual(savedWave),
            "voice_studio_rename_preserves_reference_id_and_exact_audio");
        var other = studio.Save(sample with { SkinId = "claude" }, "克劳德 · 温和");
        studio.DeleteSavedVoice(profile.ReferenceId!);
        require(studio.ListSavedVoices("gemini").Count == 0 && studio.ReadSavedVoice("gemini") == null &&
            studio.ListSavedVoices("claude").Single().ReferenceId == other.ReferenceId,
            "voice_studio_delete_removes_only_selected_custom_reference");
        bool builtinProtected = false;
        try { studio.DeleteSavedVoice("builtin:base"); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { builtinProtected = true; }
        require(builtinProtected && begins == 2 && synth.Generations == 1,
            "voice_studio_management_never_deletes_builtin_voice_or_starts_model_generation");
        bool rejected = false;
        try { await studio.GenerateAsync("gpt", " ", "你好。", "Chinese", CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        require(rejected && begins == 2, "voice_studio_rejects_empty_description_before_releasing_current_voice");

        synth.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        Task<PetVoiceDesignSample> pending = studio.GenerateAsync("claude", "自然温和", "你好。", "Chinese", cancel.Token);
        cancel.Cancel();
        bool cancelled = false;
        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        require(cancelled && begins == ends && synth.Releases == 2 && synth.Disposes == 2,
            "voice_studio_cancellation_retires_worker_and_restores_normal_speech_lease");

        synth.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PetVoiceDesignSample> closing = studio.GenerateAsync("gpt", "清亮自信", "你好。", "Chinese", CancellationToken.None);
        studio.Dispose();
        cancelled = false;
        try { await closing; } catch (OperationCanceledException) { cancelled = true; }
        require(cancelled && begins == ends && synth.Disposes >= 3 && player.Disposed,
            "voice_studio_close_cancels_pending_generation_and_stops_candidate_playback");
    }

    private sealed class Synth : IPetSpeechSynthesizer, IPetVoiceProfileSelector
    {
        public PetVoiceProfile? Profile; public int Releases, Disposes, Generations;
        public TaskCompletionSource<byte[]>? Pending;
        public Task SelectProfileAsync(PetVoiceProfile profile, CancellationToken ct) { Profile = profile; return Task.CompletedTask; }
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken ct)
        { Generations++; return Pending?.Task.WaitAsync(ct) ?? Task.FromResult(Wave()); }
        public Task CancelAsync(string id) => Task.CompletedTask;
        public Task ReleaseAsync() { Releases++; return Task.CompletedTask; }
        public void Dispose() { Disposes++; }
    }

    private sealed class Player : IPetSpeechPlayer
    {
        public byte[]? Played; public bool Disposed;
        public Task PlayAsync(byte[] wave, double volume, CancellationToken ct) { Played = wave; return Task.CompletedTask; }
        public void Stop() { }
        public void SetVolume(double value) { }
        public void Dispose() { Disposed = true; }
    }

    internal static byte[] Wave()
    {
        byte[] wave = new byte[44 + 24000 * 2];
        "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 24000); BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)wave.Length - 44);
        for (int i = 44; i < wave.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(i), (short)(i % 200 < 100 ? 1200 : -1200));
        return wave;
    }
}
