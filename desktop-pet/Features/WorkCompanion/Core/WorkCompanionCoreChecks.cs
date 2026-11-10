using System;
using System.Collections.Generic;

namespace DesktopPet.WorkCompanion;

internal static class WorkCompanionCoreChecks
{
    // Synthetic timestamps and foreground samples only. No screen/window reads,
    // model calls, file writes, timers, or changes to the user's configuration.
    public static object Run()
    {
        var checks = new List<string>();
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Work companion check failed: " + label);
            checks.Add(label);
        }
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        WorkObservation Sample(int seconds, string app = "Code", string window = "code-1", int idleSeconds = 5,
            string? title = null) => new(start.AddSeconds(seconds), app, title, window,
                TimeSpan.FromSeconds(idleSeconds), true, false, false);

        var policy = new WorkTriggerPolicy();
        bool firstGreeting = false;
        for (int seconds = 0; seconds <= 20; seconds += 5)
            firstGreeting |= policy.Observe(Sample(seconds)) != null;
        Require(!firstGreeting && policy.ActiveMinutes == 0, "startup establishes a silent baseline without invented work time");
        Require(policy.Observe(Sample(25, "Reader", "reader-1", 0)) == null &&
            policy.Observe(Sample(30, "Reader", "reader-1", 0)) == null &&
            policy.Observe(Sample(35, "Reader", "reader-1", 0)) == null,
            "a new foreground window needs fifteen stable seconds");
        Require(policy.Observe(Sample(40, "Reader", "reader-1", 0)) == null,
            "a stable app switch waits while the user is entering input");
        var changed = policy.Observe(Sample(45, "Reader", "reader-1"));
        Require(changed is { Kind: "app_changed", App: "Reader", PreviousApp: "Code", Title: null },
            "quiet stable app switch emits only app metadata when titles are off");
        Require(changed != null && policy.IsCurrent(changed, start.AddSeconds(45)), "fresh candidate can be used by a delayed planner");
        Require(policy.Observe(Sample(50, "Reader", "reader-1")) == null, "one observation does not repeatedly queue the same transition");
        policy.Observe(Sample(55, "Reader", "reader-1", 0));
        Require(changed != null && !policy.IsCurrent(changed, start.AddSeconds(55)), "resuming input invalidates an in-flight app reaction");

        policy.Reset();
        policy.Observe(Sample(0));
        policy.Observe(Sample(5, "Reader", "reader-1"));
        policy.Observe(Sample(10, "Browser", "browser-1"));
        policy.Observe(Sample(15));
        bool fastSwitch = false;
        for (int seconds = 20; seconds <= 40; seconds += 5) fastSwitch |= policy.Observe(Sample(seconds)) != null;
        Require(!fastSwitch, "rapid app switching and returning to baseline produce no reaction");

        policy.Reset();
        policy.Observe(Sample(0));
        for (int seconds = 5; seconds <= 15; seconds += 5) policy.Observe(Sample(seconds, "Reader", "reader-1", title: "A paper"));
        var expiring = policy.Observe(Sample(20, "Reader", "reader-1", title: "A paper"));
        Require(expiring?.Title == "A paper", "explicitly supplied window title can accompany a stable candidate");
        for (int seconds = 25; seconds < 50; seconds += 5) policy.Observe(Sample(seconds, "Reader", "reader-1", title: "A paper"));
        Require(expiring != null && !policy.IsCurrent(expiring, start.AddSeconds(50)), "candidate expires after thirty seconds even in the same window");
        policy.Observe(Sample(50, "Reader", "reader-1", title: "A paper"));
        Require(policy.Observe(Sample(55, "Reader", "reader-1", title: "A paper")) == null,
            "expired transition does not requeue later when the model becomes available");

        policy.Reset();
        policy.Observe(Sample(0));
        for (int seconds = 5; seconds <= 15; seconds += 5) policy.Observe(Sample(seconds, "Reader", "reader-1"));
        var handled = policy.Observe(Sample(20, "Reader", "reader-1"));
        Require(handled != null, "topic fixture creates first candidate");
        policy.MarkHandled(handled!, start.AddSeconds(20));
        for (int seconds = 25; seconds <= 40; seconds += 5)
        {
            var other = policy.Observe(Sample(seconds));
            if (other != null) policy.MarkHandled(other, start.AddSeconds(seconds));
        }
        bool repeatedTopic = false;
        for (int seconds = 45; seconds <= 65; seconds += 5)
            repeatedTopic |= policy.Observe(Sample(seconds, "Reader", "reader-1")) != null;
        Require(!repeatedTopic, "recently handled window topic is suppressed after switching back");

        policy.Reset();
        WorkReactionCandidate? deferredContext = null;
        for (int seconds = 0; seconds <= 15; seconds += 5)
            deferredContext = policy.Observe(Sample(seconds), contextRequestKey: "manual:1:code-1") ?? deferredContext;
        Require(deferredContext is { Kind: "context_requested" }, "deferred context fixture offers an initial request");
        policy.ReleaseCandidate(deferredContext!);
        Require(policy.Observe(Sample(20, idleSeconds: 0), contextRequestKey: "manual:1:code-1") == null,
            "released request still waits for a fresh input pause");
        var refreshedContext = policy.Observe(Sample(25), contextRequestKey: "manual:1:code-1");
        Require(refreshedContext is { Kind: "context_requested" } && refreshedContext.Id != deferredContext!.Id &&
            refreshedContext.At == start.AddSeconds(25) && !policy.IsCurrent(deferredContext, start.AddSeconds(25)),
            "released same-key request creates a fresh candidate rather than replaying stale data");
        policy.ReleaseCandidate(deferredContext!);
        Require(policy.IsCurrent(refreshedContext!, start.AddSeconds(25)), "late release of an older offer cannot clear its replacement");
        policy.MarkHandled(refreshedContext!, start.AddSeconds(25));
        Require(policy.Observe(Sample(30), contextRequestKey: "manual:1:code-1") == null,
            "a handled context request is not automatically retried");

        policy.Reset();
        policy.Observe(Sample(0));
        WorkReactionCandidate? deferredSwitch = null;
        for (int seconds = 5; seconds <= 20; seconds += 5)
            deferredSwitch = policy.Observe(Sample(seconds, "Reader", "reader-1")) ?? deferredSwitch;
        policy.ReleaseCandidate(deferredSwitch!);
        WorkReactionCandidate? newSwitch = null;
        for (int seconds = 25; seconds <= 40; seconds += 5)
            newSwitch = policy.Observe(Sample(seconds, "Browser", "browser-1")) ?? newSwitch;
        Require(newSwitch is { App: "Browser" } && newSwitch.At == start.AddSeconds(40),
            "a window change during deferral creates only a new stable target reaction");

        policy.Reset();
        WorkReactionCandidate? deferredBreak = null;
        for (int seconds = 0; seconds <= 60; seconds += 5)
            deferredBreak = policy.Observe(Sample(seconds), reactAppChanges: false, breakAfterMinutes: 1) ?? deferredBreak;
        Require(deferredBreak is { Kind: "work_break" }, "deferred break fixture reaches its active-time threshold");
        policy.ReleaseCandidate(deferredBreak!);
        var refreshedBreak = policy.Observe(Sample(65), reactAppChanges: false, breakAfterMinutes: 1);
        Require(refreshedBreak is { Kind: "work_break" } && refreshedBreak.Id != deferredBreak!.Id,
            "a temporarily blocked break reminder gets a fresh opportunity");
        policy.MarkHandled(refreshedBreak!, start.AddSeconds(65));
        Require(policy.Observe(Sample(70), reactAppChanges: false, breakAfterMinutes: 1) == null,
            "a started break attempt remains limited to one per work period");

        foreach (var hidden in new[]
        {
            WorkObservation.Unavailable(start.AddSeconds(25), "excluded"),
            WorkObservation.Unavailable(start.AddSeconds(25), "session_inactive"),
            WorkObservation.Unavailable(start.AddSeconds(25), "secure_desktop"),
            Sample(25) with { IsFullscreen = true },
            Sample(25) with { IsOwnApp = true },
            Sample(25, idleSeconds: 300)
        })
        {
            policy.Reset();
            for (int seconds = 0; seconds <= 20; seconds += 5) policy.Observe(Sample(seconds));
            policy.Observe(hidden);
            bool crossedBoundary = false;
            for (int seconds = 30; seconds <= 50; seconds += 5)
                crossedBoundary |= policy.Observe(Sample(seconds, "Reader", "reader-1")) != null;
            Require(!crossedBoundary && policy.ActiveMinutes == 0,
                "excluded, locked, fullscreen, own-app or away intervals restart the baseline: " + hidden.Status + "/" + hidden.IsFullscreen + "/" + hidden.IsOwnApp + "/" + hidden.IdleFor.TotalSeconds);
        }

        policy.Reset();
        for (int seconds = 0; seconds <= 60; seconds += 5) policy.Observe(Sample(seconds));
        Require(policy.ActiveMinutes == 1, "work duration counts consecutive valid observations");
        policy.Observe(Sample(3600, "Reader", "reader-1"));
        Require(policy.ActiveMinutes == 0 && policy.Observe(Sample(3605, "Reader", "reader-1")) == null,
            "sleep or a sampling interruption cannot count as work or fabricate a switch");
        policy.Observe(Sample(3000));
        Require(policy.ActiveMinutes == 0, "backwards clocks reset timing safely");

        policy.Reset();
        int breaks = 0;
        WorkReactionCandidate? workBreak = null;
        for (int seconds = 0; seconds <= 46 * 60; seconds += 5)
        {
            var reaction = policy.Observe(Sample(seconds), reactAppChanges: false);
            if (reaction?.Kind != "work_break") continue;
            breaks++;
            workBreak = reaction;
            policy.MarkHandled(reaction, start.AddSeconds(seconds));
        }
        Require(breaks == 1 && workBreak?.ActiveMinutes == 45,
            "forty-five active minutes produce exactly one break reminder per work session");
        policy.Observe(Sample(46 * 60 + 5, idleSeconds: 300));
        Require(policy.ActiveMinutes == 0, "five minutes without input resets the work session");

        policy.Reset();
        bool disabledTrigger = false;
        for (int seconds = 0; seconds <= 120; seconds += 5)
            disabledTrigger |= policy.Observe(Sample(seconds, seconds < 10 ? "Code" : "Reader",
                seconds < 10 ? "code-1" : "reader-1"), false, false, 1) != null;
        Require(!disabledTrigger, "app-change and break settings independently disable policy output");

        Require(WindowsWorkObserver.IsAlwaysExcluded(" KeePassXC.exe ") &&
            WindowsWorkObserver.IsAlwaysExcluded("1Password") && !WindowsWorkObserver.IsAlwaysExcluded("Code"),
            "password manager exclusions are exact and case insensitive");
        Require(WindowsWorkObserver.NormalizeAppName("C:\\private\\app.exe") == "" &&
            WindowsWorkObserver.NormalizeAppName("Code.EXE") == "Code", "exclusion configuration accepts names, not local paths");
        Require(WindowsWorkObserver.SanitizeTitle(" \r\nA paper\t ") == "A paper" &&
            WindowsWorkObserver.SanitizeTitle(new string('x', 400))?.Length == 256,
            "opt-in titles are bounded and stripped of control characters");

        return new { success = true, count = checks.Count, checks };
    }
}
