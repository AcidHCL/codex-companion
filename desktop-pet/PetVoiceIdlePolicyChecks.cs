using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

// Synthetic lifecycle only: no worker, GPU, audio or persisted user preferences.
internal static class PetVoiceIdlePolicyChecks
{
    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> require)
    {
        var settings = new DesktopSettings { SkinId = "deepseek", SpeechEnabled = true, SpeechIdleUnloadMinutes = 5 };
        var synth = new Synth();
        using var service = new PetSpeechService(settings, dispatcher, synth, new Player());
        require(synth.Preloads == 0 && synth.Policies.Last() == (false, 5), "finite_voice_policy_is_configured_without_loading_model");
        service.SetChatSessionActive(true);
        await Task.Yield();
        require(synth.Preloads == 1 && synth.Policies.Last() == (false, 5), "finite_voice_idle_timeout_is_not_pinned_by_open_chat");
        settings.SpeechIdleUnloadMinutes = -1;
        service.ApplyRetentionPolicy();
        service.SetChatSessionActive(false);
        require(synth.Preloads == 1 && synth.Policies.Last() == (true, -1), "never_voice_policy_keeps_lease_after_chat_closes_without_reload");
        service.SetSuspended(true);
        require(synth.Releases == 0, "never_voice_policy_preserves_model_during_microphone_or_song_suspension");
        service.SetSuspended(false);
        await service.UnloadAsync();
        int preloads = synth.Preloads;
        settings.SpeechIdleUnloadMinutes = 30; service.ApplyRetentionPolicy();
        settings.SpeechIdleUnloadMinutes = -1; service.ApplyRetentionPolicy();
        service.SetChatSessionActive(true);
        require(synth.Unloads == 1 && synth.Preloads == preloads && synth.Policies.Last() == (false, -1),
            "manual_voice_unload_survives_policy_changes_and_chat_open");
        await service.PreloadAsync();
        require(synth.Preloads == preloads + 1 && synth.Policies.Last() == (true, -1), "explicit_preload_restores_selected_never_policy");
        settings.SpeechIdleUnloadMinutes = 10; service.ApplyRetentionPolicy();
        require(synth.Policies.Last() == (false, 10) && synth.Preloads == preloads + 1,
            "switching_to_finite_policy_releases_pin_without_preloading");
        service.SetSuspended(true);
        require(synth.Releases == 0, "finite_idle_policy_is_not_replaced_by_immediate_suspend_unload");
        service.SetSuspended(false);
        settings.SpeechIdleUnloadMinutes = 0; service.ApplyRetentionPolicy();
        require(synth.Policies.Last() == (true, 0), "legacy_policy_preserves_chat_session_lease");
        service.SetChatSessionActive(false);
        require(synth.Policies.Last() == (false, 0), "legacy_policy_releases_lease_when_chat_closes");

        var isolated = new Synth();
        using var isolatedService = new PetSpeechService(settings, dispatcher, isolated, new Player(), enabledForProcess: false);
        settings.SpeechIdleUnloadMinutes = -1; isolatedService.ApplyRetentionPolicy();
        require(isolated.Policies.Count == 0 && isolated.Preloads == 0, "isolated_voice_policy_settings_never_contact_engine");
        int starts = 0;
        using var client = new PetVoiceClient(() => { starts++; throw new InvalidOperationException("Worker must stay stopped."); });
        await client.SetRetentionAsync(true, -1, CancellationToken.None);
        await client.SetRetentionAsync(false, 30, CancellationToken.None);
        require(starts == 0 && client.OwnedWorkerProcessId == null, "real_voice_client_remembers_policy_without_starting_worker_or_loading_model");
    }

    private sealed class Synth : IPetSpeechSynthesizer, IPetVoiceLifecycle, IPetVoiceRetentionPolicy
    {
        internal readonly List<(bool, int)> Policies = new();
        internal int Preloads, Unloads, Releases;
        public Task SetRetentionAsync(bool active, int minutes, CancellationToken token) { Policies.Add((active, minutes)); return Task.CompletedTask; }
        public Task SetRetentionAsync(bool active, CancellationToken token) => throw new InvalidOperationException("Policy-aware engine must receive idle minutes.");
        public Task PreloadAsync(string id, CancellationToken token) { Preloads++; return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken token) { Unloads++; return Task.CompletedTask; }
        public Task ReleaseAsync() { Releases++; return Task.CompletedTask; }
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token) => throw new InvalidOperationException("No speech in retention fixture.");
        public Task CancelAsync(string id) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class Player : IPetSpeechPlayer
    {
        public Task PlayAsync(byte[] bytes, double volume, CancellationToken token) => throw new InvalidOperationException("No audio in retention fixture.");
        public void SetVolume(double volume) { }
        public void Stop() { }
        public void Dispose() { }
    }
}
