using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal static class PetVoiceHealthChecks
{
    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> require)
    {
        using (var data = JsonDocument.Parse("{\"status\":\"ready\",\"backend\":\"faster\",\"loaded\":true,\"retained\":true,\"model\":\"Qwen3-TTS\",\"device\":\"cuda:0\",\"allocated_bytes\":2147483648,\"reserved_bytes\":3221225472}"))
        {
            var health = PetVoiceClient.ParseHealth(data.RootElement);
            require(health.Loaded && health.Retained && health.Backend == "faster" && health.Model == "Qwen3-TTS" &&
                health.Device == "cuda:0" && health.AllocatedBytes == 2147483648L && health.ReservedBytes == 3221225472L,
                "voice_health_preserves_loaded_state_and_64_bit_allocator_metrics");
        }
        using (var data = JsonDocument.Parse("{\"status\":\"idle\",\"allocated_bytes\":0,\"reserved_bytes\":0}"))
        {
            var health = PetVoiceClient.ParseHealth(data.RootElement);
            require(!health.Loaded && health.AllocatedBytes == 0 && health.ReservedBytes == 0 && health.Device == null,
                "voice_health_preserves_measured_zero_distinct_from_missing_metrics");
        }
        foreach (string fields in new[] { "", ",\"allocated_bytes\":null,\"reserved_bytes\":null", ",\"allocated_bytes\":-1,\"reserved_bytes\":\"123\"", ",\"allocated_bytes\":1.2,\"reserved_bytes\":99999999999999999999" })
        {
            using var data = JsonDocument.Parse("{\"status\":\"idle\"" + fields + "}");
            var health = PetVoiceClient.ParseHealth(data.RootElement);
            require(health.AllocatedBytes == null && health.ReservedBytes == null, "voice_health_unknown_or_invalid_metrics_are_never_fabricated_as_zero");
        }
        int resolutions = 0;
        using (var client = new PetVoiceClient(() => { resolutions++; throw new InvalidOperationException("Must never start a worker for telemetry."); }))
        {
            require(await client.ReadHealthAsync(CancellationToken.None) == null && resolutions == 0 && client.OwnedWorkerProcessId == null,
                "voice_health_poll_does_not_resolve_runtime_start_worker_or_load_model");
        }
        var synth = new DiagnosticsSynth();
        using var service = new PetSpeechService(new DesktopSettings { SpeechEnabled = false }, dispatcher, synth, new SilentPlayer());
        int mutationsBeforeRead = synth.Mutations;
        var state = await service.ReadModelHealthAsync(CancellationToken.None);
        require(ReferenceEquals(state, synth.Health) && synth.Reads == 1 && synth.Mutations == mutationsBeforeRead,
            "voice_service_poll_reports_worker_state_even_when_audio_disabled_without_mutation");
        synth.Fail = true;
        bool failed = false;
        try { await service.ReadModelHealthAsync(CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        require(failed, "voice_health_errors_propagate_instead_of_claiming_model_unloaded");
    }

    private sealed class DiagnosticsSynth : IPetSpeechSynthesizer, IPetVoiceDiagnostics
    {
        internal readonly PetVoiceHealth Health = new("ready", "faster", true, false, false);
        internal int Reads, Mutations;
        internal bool Fail;
        public Task<PetVoiceHealth?> ReadHealthAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            return Fail ? Task.FromException<PetVoiceHealth?>(new InvalidOperationException("fixture")) : Task.FromResult<PetVoiceHealth?>(Health);
        }
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token) { Mutations++; throw new InvalidOperationException("Unexpected synthesis"); }
        public Task CancelAsync(string id) { Mutations++; return Task.CompletedTask; }
        public Task ReleaseAsync() { Mutations++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class SilentPlayer : IPetSpeechPlayer
    {
        public Task PlayAsync(byte[] wave, double volume, CancellationToken token) => throw new InvalidOperationException("Unexpected playback");
        public void SetVolume(double volume) { }
        public void Stop() { }
        public void Dispose() { }
    }
}
