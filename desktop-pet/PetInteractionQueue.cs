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
    // This is the host's cumulative count for its latest short click burst,
    // not a sum of successive 1, 2, 3... samples. Keep it independently of the
    // bounded recent-kind trail so rapid clicks cannot truncate the count.
    private PetInteractionEvent? _latestClick;
    private readonly List<PetInteractionEvent> _recent = new();
    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;
    internal bool HasPending => _pending != null;
    internal void Offer(PetInteractionEvent item)
    {
        if (item.Kind is not ("clicked" or "drag_started" or "released" or "fall_started" or "landed")) return;
        if (_pending != null && item.OccurredAt < _pending.OccurredAt) return;
        if (item.Kind == "clicked" && _latestClick != null && item.OccurredAt < _latestClick.OccurredAt) return;
        if (_latestClick != null && item.OccurredAt - _latestClick.OccurredAt > MaximumAge) _latestClick = null;
        if (item.Kind == "clicked")
        {
            item = item with
            {
                ClickCount = Math.Max(1, PetInteractionEvent.BoundedClickCount(item.ClickCount)),
                ClickReaction = PetInteractionEvent.KnownClickReaction(item.ClickReaction)
            };
            _latestClick = item;
        }
        else item = item with { ClickCount = 0, ClickReaction = null };
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
        var click = _latestClick != null && now - _latestClick.OccurredAt <= MaximumAge && _latestClick.OccurredAt <= now
            ? _latestClick : null;
        item = _pending with
        {
            RecentKinds = _recent.Where(previous => now - previous.OccurredAt <= MaximumAge).Select(previous => previous.Kind).ToArray(),
            ClickCount = click?.ClickCount ?? 0,
            ClickReaction = click?.ClickReaction
        };
        Clear(); _lastStarted = now; return true;
    }
    // A new physical state invalidates an old plan, but the next event can still
    // describe the short drag/release/fall/landing sequence in one request.
    internal void InvalidatePending() => _pending = null;
    internal void Clear() { _pending = null; _latestClick = null; _recent.Clear(); }
}
