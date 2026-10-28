using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopPet;

// Only state edges enter this queue. Repeated input replaces one pending item;
// elapsed time never creates a model request without an actual interaction.
internal sealed class PetInteractionQueue
{
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(650);
    private PetInteractionEvent? _pending;
    private readonly List<PetInteractionEvent> _recent = new();
    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;
    internal bool HasPending => _pending != null;
    internal void Offer(PetInteractionEvent item)
    {
        if (item.Kind is not ("clicked" or "drag_started" or "released" or "fall_started" or "landed")) return;
        if (_pending != null && item.OccurredAt < _pending.OccurredAt) return;
        _recent.RemoveAll(previous => item.OccurredAt - previous.OccurredAt > MaximumAge);
        _recent.Add(item);
        if (_recent.Count > 8) _recent.RemoveAt(0);
        _pending = item;
    }
    internal bool TryTake(DateTimeOffset now, bool available, out PetInteractionEvent? item)
    {
        item = null;
        if (_pending == null) return false;
        if (now - _pending.OccurredAt > MaximumAge || _pending.OccurredAt - now > TimeSpan.FromSeconds(2)) { Clear(); return false; }
        if (!available || now - _pending.OccurredAt < SettleDelay || now - _lastStarted < Cooldown) return false;
        item = _pending with { RecentKinds = _recent.Where(previous => now - previous.OccurredAt <= MaximumAge).Select(previous => previous.Kind).ToArray() };
        Clear(); _lastStarted = now; return true;
    }
    // A new physical state invalidates an old plan, but the next event can still
    // describe the short drag/release/fall/landing sequence in one request.
    internal void InvalidatePending() => _pending = null;
    internal void Clear() { _pending = null; _recent.Clear(); }
}
