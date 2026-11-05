using System;

namespace DesktopPet;

internal enum OllamaActivityState { Unknown, Unloaded, OwnRequest, Maintenance, Settling, Quiet, Observing, SuspectedExternal }
internal sealed record OllamaActivityAssessment(OllamaActivityState State, int ConsecutiveSamples = 0);

// GPU activity is a service-level hint, not proof of inference or caller identity.
// A short burst, canceled local request, missing counter, or hidden page must not
// become a confident-looking external inference badge.
internal sealed class OllamaActivityClassifier
{
    internal const double BusyThreshold = 5;
    private string? _endpoint;
    private int? _server;
    private DateTimeOffset? _firstBusy, _lastSample, _lastCounted;
    private int _busySamples;

    internal void Reset()
    {
        _endpoint = null; _server = null; ResetSequence();
    }

    private void ResetSequence()
    {
        _firstBusy = _lastSample = _lastCounted = null; _busySamples = 0;
    }

    internal OllamaActivityAssessment Evaluate(string endpoint, int? loadedModels,
        OllamaGpuActivity? gpu, OllamaRequestSnapshot? own, DateTimeOffset now)
    {
        if (_endpoint != endpoint || _server != gpu?.ServerProcessId)
        {
            ResetSequence(); _endpoint = endpoint; _server = gpu?.ServerProcessId;
        }
        OllamaActivityAssessment Stop(OllamaActivityState state)
        { ResetSequence(); return new(state); }
        if (own?.ActiveMaintenance > 0) return Stop(OllamaActivityState.Maintenance);
        if (own?.ActiveInference > 0) return Stop(OllamaActivityState.OwnRequest);
        if (own == null || loadedModels == null) return Stop(OllamaActivityState.Unknown);
        if (own.GraceUntil > now) return Stop(OllamaActivityState.Settling);
        if (loadedModels == 0) return Stop(OllamaActivityState.Unloaded);
        if (gpu == null || !gpu.Available || gpu.UtilizationPercent is not double percent ||
            !double.IsFinite(percent) || percent < 0 || percent > 100 || gpu.Processes.Count == 0 ||
            now - gpu.SampledAt > TimeSpan.FromSeconds(15) || gpu.SampledAt > now.AddSeconds(2))
            return Stop(OllamaActivityState.Unknown);
        if (percent < BusyThreshold) return Stop(OllamaActivityState.Quiet);

        DateTimeOffset sample = gpu.SampledAt;
        if (_lastSample is { } previous && (sample < previous || sample - previous > TimeSpan.FromSeconds(12)))
            ResetSequence();
        _lastSample = sample;
        _firstBusy ??= sample;
        // Pressing Refresh repeatedly cannot replace sustained observations.
        if (_lastCounted == null || sample - _lastCounted >= TimeSpan.FromSeconds(3))
        { _busySamples++; _lastCounted = sample; }
        return _busySamples >= 3 && sample - _firstBusy >= TimeSpan.FromSeconds(8)
            ? new(OllamaActivityState.SuspectedExternal, _busySamples)
            : new(OllamaActivityState.Observing, _busySamples);
    }
}
