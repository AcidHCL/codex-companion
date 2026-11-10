using System;
using System.Collections.Generic;

namespace DesktopPet.WorkCompanion;

internal static class CompanionActivityChecks
{
    // Pure process-label and timestamp fixtures. No operating-system reads,
    // game integration, screen capture, network, model or settings access.
    internal static object Run()
    {
        var checks = new List<string>();
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Companion activity: " + label);
            checks.Add(label);
        }
        Require(CompanionActivityClassifier.Classify(" Code.EXE ") == WorkActivity.Work &&
            CompanionActivityClassifier.Classify("Zotero") == WorkActivity.Work &&
            CompanionActivityClassifier.Classify("WINWORD") == WorkActivity.Work,
            "known_work_and_study_app_names_are_case_insensitive");
        Require(CompanionActivityClassifier.Classify("cs2") == WorkActivity.Game &&
            CompanionActivityClassifier.Classify("VALORANT-Win64-Shipping.exe") == WorkActivity.Game &&
            CompanionActivityClassifier.Classify("Stardew Valley") == WorkActivity.Game,
            "known_actual_game_process_names_have_game_hints");
        Require(CompanionActivityClassifier.Classify("Spotify") == WorkActivity.Media &&
            CompanionActivityClassifier.Classify("vlc.exe") == WorkActivity.Media &&
            CompanionActivityClassifier.Classify("PotPlayerMini64") == WorkActivity.Media,
            "known_music_and_video_players_have_media_hints");
        foreach (string name in new[] { "chrome", "msedge", "firefox", "wechat", "discord", "steam", "EpicGamesLauncher", "javaw", "unknown-game-tool" })
            Require(CompanionActivityClassifier.Classify(name) == WorkActivity.Daily,
                "ambiguous_process_defaults_to_daily_without_claiming_gameplay_or_content_" + name);
        Require(CompanionActivityClassifier.Classify("mygame.exe", new Dictionary<string, string> { ["MYGAME"] = "game" }) == WorkActivity.Game &&
            CompanionActivityClassifier.Classify("cs2", new Dictionary<string, string> { ["cs2.exe"] = "work" }) == WorkActivity.Work,
            "explicit_user_classification_takes_priority_with_case_and_exe_normalization");
        Require(CompanionActivityClassifier.Classify("Code", new Dictionary<string, string> { ["code"] = "not-an-activity" }) == WorkActivity.Work &&
            CompanionActivityClassifier.Classify(@"C:\private\cs2.exe") == WorkActivity.Daily &&
            CompanionActivityClassifier.Classify("cs2\n") == WorkActivity.Game &&
            CompanionActivityClassifier.Classify("cs\n2") == WorkActivity.Daily,
            "invalid_override_or_path_cannot_manufacture_a_game_hint");
        Require(CompanionActivityClassifier.Key(WorkActivity.Work) == "work" &&
            CompanionActivityClassifier.Key(WorkActivity.Game) == "game" &&
            CompanionActivityClassifier.Key(WorkActivity.Media) == "media" &&
            CompanionActivityClassifier.Key(WorkActivity.Daily) == "daily" &&
            CompanionActivityClassifier.TryParse(" MEDIA ", out var media) && media == WorkActivity.Media &&
            !CompanionActivityClassifier.TryParse("entertainment", out _),
            "activity_keys_are_a_strict_four_value_contract");

        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        WorkObservation Sample(int second, WorkActivity activity, string? app = null,
            bool fullscreen = false, int idle = 5) => new(start.AddSeconds(second),
                app ?? CompanionActivityClassifier.Key(activity), null,
                app ?? CompanionActivityClassifier.Key(activity), TimeSpan.FromSeconds(idle), true, fullscreen, false)
                { Activity = activity };

        var policy = new WorkTriggerPolicy();
        policy.Observe(Sample(0, WorkActivity.Work));
        WorkReactionCandidate? entry = null;
        for (int second = 5; second <= 20; second += 5)
            entry = policy.Observe(Sample(second, WorkActivity.Game), breakAfterMinutes: 1) ?? entry;
        Require(entry is { Kind: "app_changed", Activity: WorkActivity.Game, PreviousActivity: WorkActivity.Work, ActiveMinutes: 0 },
            "stable_game_entry_retains_previous_work_activity_without_reusing_work_minutes");
        policy.MarkHandled(entry!, start.AddSeconds(20));
        WorkReactionCandidate? gameBreak = null;
        for (int second = 25; second <= 65; second += 5)
            gameBreak = policy.Observe(Sample(second, WorkActivity.Game), breakAfterMinutes: 1) ?? gameBreak;
        Require(gameBreak is { Kind: "game_break", Activity: WorkActivity.Game, PreviousActivity: null, ActiveMinutes: 1 },
            "game_reminder_uses_game_duration_and_explicit_kind");
        Require(gameBreak != null && policy.IsCurrent(gameBreak, start.AddSeconds(65)), "game_break_can_be_revalidated");
        policy.Observe(Sample(70, WorkActivity.Daily));
        Require(gameBreak != null && !policy.IsCurrent(gameBreak, start.AddSeconds(70)) && policy.ActiveMinutes == 0,
            "activity_change_invalidates_game_reply_and_resets_duration");
        WorkReactionCandidate? leaving = null;
        for (int second = 75; second <= 85; second += 5)
            leaving = policy.Observe(Sample(second, WorkActivity.Daily)) ?? leaving;
        Require(leaving is { Kind: "app_changed", Activity: WorkActivity.Daily, PreviousActivity: WorkActivity.Game },
            "return_to_daily_has_game_context_but_no_claim_of_game_ending_or_result");

        foreach (WorkActivity activity in Enum.GetValues<WorkActivity>())
        {
            policy.Reset();
            WorkReactionCandidate? reminder = null;
            for (int second = 0; second <= 65; second += 5)
                reminder = policy.Observe(Sample(second, activity), reactAppChanges: false, breakAfterMinutes: 1) ?? reminder;
            Require(reminder?.Kind == CompanionActivityClassifier.Key(activity) + "_break" && reminder.Activity == activity && reminder.ActiveMinutes == 1,
                "each_activity_has_its_own_break_kind_" + CompanionActivityClassifier.Key(activity));
            policy.Observe(Sample(70, activity), false, false, 1);
            Require(!policy.IsCurrent(reminder!, start.AddSeconds(70)),
                "disabling_reminders_invalidates_pending_kind_" + CompanionActivityClassifier.Key(activity));
        }

        policy.Reset();
        for (int second = 0; second <= 55; second += 5)
            policy.Observe(Sample(second, WorkActivity.Game, "first-game"), false, false);
        policy.Observe(Sample(60, WorkActivity.Game, "second-game"), false, false);
        Require(policy.ActiveMinutes == 1, "switching_apps_within_one_activity_keeps_continuous_minutes");

        policy.Reset();
        for (int second = 0; second <= 60; second += 5)
            policy.Observe(Sample(second, WorkActivity.Work, "same-process"), false, false);
        policy.Observe(Sample(65, WorkActivity.Media, "same-process"), breakAfterMinutes: 1);
        WorkReactionCandidate? reclassified = null;
        for (int second = 70; second <= 80; second += 5)
            reclassified = policy.Observe(Sample(second, WorkActivity.Media, "same-process"), breakAfterMinutes: 1) ?? reclassified;
        Require(reclassified is { Activity: WorkActivity.Media, PreviousActivity: WorkActivity.Work, ActiveMinutes: 0 },
            "activity_is_part_of_window_identity_even_for_the_same_process");

        policy.Reset();
        policy.Observe(Sample(0, WorkActivity.Work));
        WorkReactionCandidate? fullscreenEntry = null;
        for (int second = 5; second <= 25; second += 5)
        {
            var item = policy.Observe(Sample(second, WorkActivity.Game, fullscreen: true), allowFullscreen: true);
            if (item != null) { fullscreenEntry = item; policy.MarkHandled(item, start.AddSeconds(second)); }
        }
        Require(fullscreenEntry?.Activity == WorkActivity.Game, "opted_in_fullscreen_tracking_can_settle_the_game_context");
        WorkReactionCandidate? fullscreenReturn = null;
        for (int second = 30; second <= 45; second += 5)
            fullscreenReturn = policy.Observe(Sample(second, WorkActivity.Daily), allowFullscreen: true) ?? fullscreenReturn;
        Require(fullscreenReturn is { Activity: WorkActivity.Daily, PreviousActivity: WorkActivity.Game },
            "alt_tab_from_a_tracked_fullscreen_game_preserves_the_previous_activity");
        policy.Reset();
        for (int second = 0; second <= 60; second += 5)
            policy.Observe(Sample(second, WorkActivity.Game, fullscreen: true), false, false);
        Require(policy.ActiveMinutes == 0, "legacy_callers_keep_fullscreen_observation_disabled_by_default");

        policy.Reset();
        policy.Observe(Sample(0, WorkActivity.Daily), allowFullscreen: true);
        WorkReactionCandidate? pendingFullscreen = null;
        for (int second = 5; second <= 20; second += 5)
            pendingFullscreen = policy.Observe(Sample(second, WorkActivity.Game, fullscreen: true), allowFullscreen: true) ?? pendingFullscreen;
        Require(pendingFullscreen != null && policy.IsCurrent(pendingFullscreen, start.AddSeconds(20)),
            "current_candidate_uses_the_explicit_fullscreen_tracking_policy");
        policy.Observe(Sample(25, WorkActivity.Game, fullscreen: true), allowFullscreen: false);
        Require(!policy.IsCurrent(pendingFullscreen!, start.AddSeconds(25)), "revoking_fullscreen_tracking_invalidates_pending_candidate");

        policy.Reset();
        policy.Observe(Sample(0, WorkActivity.Work));
        Require(policy.Observe(Sample(5, (WorkActivity)999)) == null && policy.ActiveMinutes == 0,
            "undefined_activity_values_fail_closed");
        return new { success = true, count = checks.Count, checks };
    }
}
