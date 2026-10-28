using System;
using System.Collections.Generic;

namespace DesktopPet;

internal static class PetInteractionQueueChecks
{
    internal static object Run()
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        var now = DateTimeOffset.UtcNow;
        var queue = new PetInteractionQueue();
        Check(!queue.TryTake(now, true, out _), "no_idle_model_request_without_event");
        queue.Offer(new("frame", now, 1));
        Check(!queue.HasPending, "animation_frames_never_enter_reaction_queue");
        queue.Offer(new("clicked", now, 1));
        Check(!queue.TryTake(now.AddMilliseconds(300), true, out _), "brief_click_burst_waits_before_inference");
        queue.Offer(new("drag_started", now.AddMilliseconds(400), 2));
        queue.InvalidatePending();
        queue.Offer(new("landed", now.AddMilliseconds(600), 3));
        Check(!queue.TryTake(now.AddSeconds(2), false, out _), "busy_user_chat_has_priority");
        Check(queue.TryTake(now.AddSeconds(2), true, out var item) && item!.Kind == "landed", "newest_event_replaces_obsolete_drag_and_click");
        Check(item!.RecentKinds?.Count == 3 && item.RecentKinds[1] == "drag_started", "physical_invalidation_preserves_recent_event_sequence_for_one_reaction");
        Check(!queue.HasPending, "taking_event_consumes_it_once");
        queue.Offer(new("clicked", now.AddSeconds(3), 4));
        Check(!queue.TryTake(now.AddSeconds(4), true, out _), "cooldown_limits_repeated_interaction_inference");
        queue.Offer(new("released", now.AddSeconds(11), 5));
        Check(queue.TryTake(now.AddSeconds(12), true, out item) && item!.Kind == "released", "latest_event_runs_after_cooldown");
        queue.Offer(new("clicked", now.AddSeconds(13), 6));
        Check(!queue.TryTake(now.AddSeconds(40), true, out _) && !queue.HasPending, "stale_events_expire_instead_of_surprising_late_reactions");
        queue.Offer(new("clicked", now.AddSeconds(41), 7)); queue.Clear();
        Check(!queue.TryTake(now.AddSeconds(43), true, out _), "mode_and_context_changes_can_discard_pending_reactions");
        queue.Offer(new("clicked", now.AddSeconds(100), 8));
        Check(!queue.TryTake(now.AddSeconds(44), true, out _) && !queue.HasPending, "future_dated_events_cannot_block_queue");
        queue.Offer(new("landed", now.AddSeconds(45), 9));
        queue.Offer(new("fall_started", now.AddSeconds(44), 8));
        Check(queue.TryTake(now.AddSeconds(47), true, out item) && item!.Kind == "landed", "out_of_order_delivery_cannot_replace_newer_state");
        Check(item!.RecentKinds?.Count == 1, "context_clear_removes_earlier_interaction_sequence");
        return new { assertions = checks.Count, checks };
    }
}
