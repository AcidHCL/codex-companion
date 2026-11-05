using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopPet;

internal sealed record OllamaRequestSnapshot(int ActiveInference, int ActiveMaintenance,
    DateTimeOffset? LastFinishedAt, DateTimeOffset? GraceUntil);

// Process-local bookkeeping only: no prompts, responses, files, network probes,
// model retention changes, or global Ollama settings. All OllamaClient instances
// participate, including stateless scheduled summaries and background reactions.
internal static class OllamaRequestActivity
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly OllamaRequestSnapshot Empty = new(0, 0, null, null);
    internal static readonly TimeSpan CompletedGrace = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan InterruptedGrace = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        internal int Inference, Maintenance;
        internal DateTimeOffset? LastFinishedAt, GraceUntil;
    }

    internal static RequestScope Begin(Uri endpoint, bool maintenance = false)
    {
        string key = Key(endpoint);
        lock (Sync)
        {
            // Endpoints may be edited over the lifetime of the application. Keep
            // recent history bounded without ever evicting an active request.
            if (Entries.Count >= 64)
            {
                DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
                foreach (string stale in Entries.Where(pair => pair.Value.Inference == 0 && pair.Value.Maintenance == 0 &&
                    pair.Value.LastFinishedAt < cutoff).Select(pair => pair.Key).ToArray()) Entries.Remove(stale);
            }
            if (!Entries.TryGetValue(key, out Entry? entry)) Entries[key] = entry = new();
            if (maintenance) entry.Maintenance++; else entry.Inference++;
        }
        return new RequestScope(key, maintenance);
    }

    internal static OllamaRequestSnapshot Snapshot(string endpoint)
    {
        string key;
        try { key = Key(OllamaEndpoint.Parse(endpoint)); }
        catch (ArgumentException) { return Empty; }
        lock (Sync)
            return Entries.TryGetValue(key, out Entry? entry)
                ? new(entry.Inference, entry.Maintenance, entry.LastFinishedAt, entry.GraceUntil)
                : Empty;
    }

    private static string Key(Uri endpoint)
    {
        Uri normalized = OllamaEndpoint.Parse(endpoint.OriginalString);
        // Conservative aliasing prevents a localhost request from looking like
        // an outside caller when the settings page uses 127.0.0.1 or ::1.
        string host = normalized.IsLoopback ? "loopback" : normalized.IdnHost.ToLowerInvariant();
        return normalized.Scheme.ToLowerInvariant() + "://" + host + ":" + normalized.Port;
    }

    internal sealed class RequestScope : IDisposable
    {
        private readonly string _key;
        private readonly bool _maintenance;
        private bool _completed, _disposed;

        internal RequestScope(string key, bool maintenance) { _key = key; _maintenance = maintenance; }

        // Only call after the server confirms completion. Cancellation, network
        // errors and early stream disposal can leave server-side work in flight.
        internal void CompleteResponse()
        {
            lock (Sync) if (!_disposed) _completed = true;
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed) return;
                _disposed = true;
                Entry entry = Entries[_key];
                if (_maintenance) entry.Maintenance--; else entry.Inference--;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                entry.LastFinishedAt = now;
                DateTimeOffset grace = now + (_completed ? CompletedGrace : InterruptedGrace);
                if (entry.GraceUntil == null || grace > entry.GraceUntil) entry.GraceUntil = grace;
            }
        }
    }
}
