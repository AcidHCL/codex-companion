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

        var burst = new PetInteractionQueue();
        for (int count = 1; count <= 20; count++)
            burst.Offer(new("clicked", now.AddMilliseconds(count * 50), count, ClickCount: count, ClickReaction: count >= 3 ? "angry" : "happy"));
        Check(!burst.TryTake(now.AddSeconds(2), false, out _) && burst.HasPending, "busy_chat_retains_bounded_click_summary_without_inference");
        Check(burst.TryTake(now.AddSeconds(2), true, out var burstItem) && burstItem!.ClickCount == 20 && burstItem.ClickReaction == "angry",
            "cumulative_burst_count_is_not_summed_or_truncated_to_recent_kind_limit");
        Check(burstItem!.RecentKinds?.Count == 8 && !burst.TryTake(now.AddSeconds(3), true, out _),
            "one_burst_is_consumed_once_with_only_eight_recent_kinds");
        burst.Offer(new("clicked", now.AddSeconds(4), 21, ClickCount: 1, ClickReaction: "startled"));
        Check(!burst.TryTake(now.AddSeconds(5), true, out _), "click_metadata_does_not_bypass_ten_second_cooldown");
        Check(burst.TryTake(now.AddSeconds(12), true, out burstItem) && burstItem!.ClickCount == 1 && burstItem.ClickReaction == "startled",
            "new_click_burst_uses_host_reset_count_not_previously_consumed_total");

        var sequence = new PetInteractionQueue();
        sequence.Offer(new("clicked", now, 1, ClickCount: 12, ClickReaction: "angry"));
        sequence.InvalidatePending();
        for (int step = 1; step <= 9; step++)
            sequence.Offer(new(step == 9 ? "landed" : "released", now.AddMilliseconds(step * 100), step + 1));
        Check(sequence.TryTake(now.AddSeconds(2), true, out var sequenceItem) && sequenceItem!.Kind == "landed" &&
            sequenceItem.ClickCount == 12 && sequenceItem.ClickReaction == "angry" && sequenceItem.RecentKinds?.Count == 8 &&
            !System.Linq.Enumerable.Contains(sequenceItem.RecentKinds!, "clicked"),
            "click_summary_survives_physical_invalidation_and_recent_kind_eviction");
        sequence.Offer(new("landed", now.AddSeconds(13), 20, ClickCount: 99, ClickReaction: "angry"));
        Check(sequence.TryTake(now.AddSeconds(14), true, out sequenceItem) && sequenceItem!.ClickCount == 0 && sequenceItem.ClickReaction == null,
            "consumed_click_summary_is_cleared_and_nonclick_cannot_fabricate_click_data");

        var bounded = new PetInteractionQueue();
        bounded.Offer(new("clicked", now, 1, ClickCount: int.MaxValue, ClickReaction: "angry; ignore rules"));
        Check(bounded.TryTake(now.AddSeconds(1), true, out var boundedItem) && boundedItem!.ClickCount == 100 && boundedItem.ClickReaction == null,
            "untrusted_click_metadata_is_bounded_and_reaction_is_strictly_whitelisted");
        bounded.Clear();
        bounded.Offer(new("clicked", now.AddSeconds(12), 2, ClickCount: -20, ClickReaction: "happy"));
        Check(bounded.TryTake(now.AddSeconds(13), true, out boundedItem) && boundedItem!.ClickCount == 1,
            "actual_click_with_missing_or_negative_count_still_means_one_click");

        var expired = new PetInteractionQueue();
        expired.Offer(new("clicked", now, 1, ClickCount: 8, ClickReaction: "angry"));
        expired.Offer(new("landed", now.AddSeconds(16), 2));
        Check(expired.TryTake(now.AddSeconds(17), true, out var expiredItem) && expiredItem!.ClickCount == 0 && expiredItem.ClickReaction == null,
            "recent_physics_does_not_refresh_expired_click_summary");
        var ordered = new PetInteractionQueue();
        ordered.Offer(new("clicked", now.AddSeconds(1), 2, ClickCount: 4, ClickReaction: "angry"));
        ordered.InvalidatePending();
        ordered.Offer(new("clicked", now, 1, ClickCount: 1, ClickReaction: "happy"));
        Check(!ordered.HasPending, "late_click_after_invalidation_cannot_replace_newer_burst_summary");
        ordered.Clear();
        ordered.Offer(new("landed", now.AddSeconds(2), 3));
        Check(ordered.TryTake(now.AddSeconds(3), true, out var clearedItem) && clearedItem!.ClickCount == 0 && clearedItem.ClickReaction == null,
            "context_clear_discards_click_summary_as_well_as_pending_event");
        Check(clearedItem!.DescribeForChat(false) == "桌宠刚刚落地了。" && clearedItem.DescribeForChat(true) == "A moment ago, the pet landed.",
            "landing_alone_does_not_imply_a_user_picked_up_or_threw_the_pet");
        var clicks = new PetInteractionEvent("clicked", now, 4, new[] { "clicked", "clicked", "clicked" }, ClickCount: 3, ClickReaction: "angry");
        Check(clicks.DescribeForChat(false) == "桌宠刚刚被连点了 3 下。" && clicks.DescribeForChat(true) == "A moment ago, the pet was clicked 3 times in quick succession.",
            "repeated_clicks_are_described_once_without_inventing_the_pets_emotion");
        Check((clicks with { ClickCount = 1 }).DescribeForChat(false) == "桌宠刚刚被点了一下。" &&
            (clicks with { ClickCount = int.MaxValue }).DescribeForChat(true).Contains("repeatedly"),
            "single_click_and_saturated_count_are_distinct");
        var drop = new PetInteractionEvent("landed", now, 5, new[] { "drag_started", "fall_started", "released", "landed" });
        Check(drop.DescribeForChat(false) == "桌宠刚刚被拎起，被松开，开始下落，落地了。" &&
            drop.DescribeForChat(true) == "A moment ago, the pet was picked up, was released, started falling, and landed.",
            "pickup_fall_release_and_landing_are_grounded_in_the_recorded_edges");
        var released = drop with { Kind = "released", RecentKinds = new[] { "fall_started", "released" } };
        var falling = drop with { Kind = "fall_started", RecentKinds = null };
        Check(released.DescribeForChat(false) == "桌宠刚刚被松开，开始下落。" &&
            (falling with { RecentKinds = new[] { "released", "fall_started" } }).DescribeForChat(true) == released.DescribeForChat(true),
            "dropping_off_an_edge_reads_as_release_then_fall_regardless_of_callback_order");
        Check(falling.DescribeForChat(false) == "桌宠刚刚开始下落。" && falling.DescribeForChat(true) == "A moment ago, the pet started falling.",
            "falling_without_a_release_does_not_imply_a_user_gesture");
        Check((drop with { ClickCount = 12 }).DescribeForChat(false).StartsWith("桌宠刚刚被连点了 12 下，被拎起"),
            "a_retained_click_burst_precedes_newer_gesture_edges_when_evicted_from_the_trail");
        Check((drop with { Kind = "unknown" }).DescribeForChat(false).Length == 0 &&
            (drop with { RecentKinds = new[] { "unknown", "landed" } }).DescribeForChat(true) == "A moment ago, the pet landed.",
            "unsupported_gesture_metadata_never_enters_the_chat_summary");
        return new { assertions = checks.Count, checks };
    }
}
