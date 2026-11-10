using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.WorkCompanion;

internal static class WorkCompanionControllerChecks
{
    // Host, time, observations, generation, presentation and persistence are all
    // fakes. These checks never construct WPF windows or access real user state.
    public static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Work companion controller check failed: " + label);
            checks.Add(label);
        }

        using (var fixture = new Fixture(new WorkCompanionOptions()))
        {
            await fixture.Step(0);
            await fixture.Step(5);
            Require(fixture.Observer.Reads == 0 && fixture.Host.Captures == 0 && fixture.Host.Generations == 0,
                "disabled feature performs no observation, host reads, or model requests");
            fixture.Controller.UpdateOptions(fixture.Controller.Options with { Enabled = true, ObserveApplications = false });
            await fixture.Step(10);
            Require(fixture.Observer.Reads == 0 && fixture.Host.Generations == 0,
                "disabling application observation stops all sensor reads");
        }

        using (var fixture = new Fixture())
        {
            fixture.Host.State = fixture.Host.State with { Available = false, CanObserve = false };
            await fixture.Step(0);
            Require(fixture.Observer.Reads == 0 && fixture.Host.Generations == 0,
                "unsafe or unavailable observation context prevents sensor reads and generation");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Step(0);
            for (int second = 5; second <= 15; second += 5) await fixture.Step(second, "Reader");
            fixture.Host.State = fixture.Host.State with { Available = false };
            await fixture.Step(20, "Reader");
            Require(fixture.Observer.Reads == 5 && fixture.Host.Generations == 0,
                "temporarily busy host still observes without starting a model attempt");
            fixture.Host.State = fixture.Host.State with { Available = true };
            for (int second = 25; second <= 45; second += 5) await fixture.Step(second, "Reader");
            Require(fixture.Host.Generations == 1 && fixture.Host.LastCandidate?.At == fixture.Clock.Now.AddSeconds(-20),
                "busy host recovery uses a new current observation and starts exactly one attempt");
            for (int second = 50; second <= 65; second += 5) await fixture.Step(second, "Browser");
            Require(fixture.Host.Generations == 1, "busy-host recovery still enforces the normal cooldown for later events");
        }

        foreach (bool codexNotification in new[] { false, true })
        {
            using var fixture = new Fixture(new WorkCompanionOptions
            {
                Enabled = true, ReactToAppChanges = false, BreakReminders = true, CooldownMinutes = 3
            });
            for (int second = 0; second <= 45 * 60; second += 2)
            {
                // A two-second busy sample models an ordinary short pet bubble.
                fixture.Host.State = fixture.Host.State with { Available = codexNotification || second != 1200 };
                if (codexNotification && second == 900)
                {
                    fixture.Clock.Set(second);
                    fixture.Controller.ObserveCodexTransition();
                }
                await fixture.Step(second);
            }
            Require(fixture.Observer.Reads == 1351 && fixture.Host.Generations == 1 &&
                fixture.Host.LastCandidate is { Kind: "work_break", ActiveMinutes: 45 },
                codexNotification ? "Codex notification preserves the continuous forty-five-minute work period" :
                "a short busy-host sample preserves work duration and still reaches the forty-five-minute reminder");
        }

        using (var fixture = new Fixture(new WorkCompanionOptions
        {
            Enabled = true, ReactToAppChanges = false, BreakReminders = true, CooldownMinutes = 3
        }))
        {
            for (int second = 0; second <= 45 * 60; second += 2)
            {
                bool safe = second != 1200;
                fixture.Host.State = fixture.Host.State with { Available = safe, CanObserve = safe };
                await fixture.Step(second);
            }
            Require(fixture.Observer.Reads == 1350 && fixture.Host.Generations == 0,
                "an unsafe observation context skips reading and resets the accumulated work duration");
            for (int second = 2702; second <= 3902; second += 2) await fixture.Step(second);
            Require(fixture.Host.Generations == 1 && fixture.Host.LastCandidate is { Kind: "work_break", ActiveMinutes: 45 },
                "after an unsafe context a full new work period is required before a break reminder");
        }

        using (var fixture = new Fixture())
        {
            var generation = fixture.Host.BlockGeneration();
            fixture.Controller.UpdateOptions(fixture.Controller.Options with { SpeakReactions = true });
            await fixture.Step(0);
            await fixture.Step(5, "Reader");
            await fixture.Step(10, "Reader");
            await fixture.Step(15, "Reader");
            Require(fixture.Host.Generations == 0, "controller does not trigger before the new app is stable");
            await fixture.Step(20, "Reader");
            Require(fixture.Host.Generations == 1 && !fixture.Controller.PendingTask.IsCompleted,
                "quiet stable switch starts one separate generation task");
            await fixture.Step(25, "Reader");
            await fixture.Step(30, "Reader");
            Require(fixture.Observer.Reads == 7 && fixture.Host.Generations == 1,
                "observations keep updating while generation is pending");
            fixture.Clock.Set(31);
            generation.SetResult(new WorkReactionReply("A brief companion response.", "happy"));
            await fixture.Controller.PendingTask;
            Require(fixture.Observer.Reads == 8 && fixture.Host.Presentations == 1 &&
                fixture.Host.LastSpeak && fixture.Controller.Status.LastReaction == "A brief companion response.",
                "reply receives a fresh observation before display and forwards the speech preference");
        }

        foreach (bool changeWindow in new[] { false, true })
        {
            using var fixture = new Fixture();
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            await fixture.Step(25, changeWindow ? "Browser" : "Reader", changeWindow ? 5 : 0);
            Require(fixture.Host.GenerationToken.IsCancellationRequested,
                changeWindow ? "foreground-window change cancels an in-flight reaction" : "new user input cancels an in-flight reaction");
            fixture.Clock.Set(26);
            generation.SetResult(new WorkReactionReply("Late output must be ignored.", null));
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Presentations == 0 && fixture.Controller.Status.LastReaction == null,
                changeWindow ? "window-change cancellation drops a late model result" : "input cancellation drops a late model result");
        }

        using (var fixture = new Fixture())
        {
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            fixture.Host.State = fixture.Host.State with { Revision = 2 };
            fixture.Clock.Set(21);
            generation.SetResult(new WorkReactionReply("Old context.", null));
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Presentations == 0, "host context revision is checked again before display");
        }

        using (var fixture = new Fixture())
        {
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            fixture.Clock.Set(51);
            generation.SetResult(new WorkReactionReply("Expired output.", null));
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Presentations == 0, "reply after candidate expiration is discarded without a timer tick");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Trigger(); // Default fake generation returns null (cold/busy/silent).
            await fixture.Controller.PendingTask;
            for (int second = 25; second <= 45; second += 5) await fixture.Step(second, "Reader");
            for (int second = 50; second <= 70; second += 5) await fixture.Step(second, "Browser");
            Require(fixture.Host.Generations == 1 && fixture.Host.Presentations == 0,
                "cold or silent model result does not cause retries or a new-window request storm");
            for (int second = 75; second <= 195; second += 5) await fixture.Step(second, "Browser");
            Require(fixture.Host.Generations == 1, "deferring a new event during cooldown never retries the model every sample");
            await fixture.Step(200, "Browser");
            Require(fixture.Host.Generations == 2 && fixture.Host.LastCandidate?.App == "Browser" &&
                fixture.Host.LastCandidate.At == fixture.Clock.Now,
                "the current event can start once cooldown expires without requiring another app switch");
            for (int second = 205; second <= 220; second += 5) await fixture.Step(second, "Editor");
            Require(fixture.Host.Generations == 2, "the recovered event starts a new cooldown rather than releasing an inference burst");
        }

        foreach (string action in new[] { "clear", "pause", "disable" })
        {
            using var fixture = new Fixture();
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            fixture.Clock.Set(21);
            switch (action)
            {
                case "clear": fixture.Controller.ClearTransientState(); break;
                case "pause": fixture.Controller.SetPaused(true); break;
                case "disable": fixture.Controller.UpdateOptions(fixture.Controller.Options with { Enabled = false }); break;
            }
            var status = fixture.Controller.Status;
            Require(fixture.Host.GenerationToken.IsCancellationRequested, action + " cancels pending generation");
            generation.SetResult(new WorkReactionReply("Must not restore cleared state.", null));
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Presentations == 0 && fixture.Controller.Status == status &&
                fixture.Controller.Status.LastReaction == null, action + " prevents a late result from restoring state");
            int reads = fixture.Observer.Reads;
            await fixture.Step(25, "Reader");
            Require(action == "clear" ? fixture.Observer.Reads == reads + 1 : fixture.Observer.Reads == reads,
                action + " has the expected observation lifecycle");
        }

        using (var fixture = new Fixture())
        {
            var delayedRead = new TaskCompletionSource<WorkObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Observer.DelayedRead = delayedRead.Task;
            var reading = fixture.Step(0);
            await fixture.Controller.TickAsync();
            Require(fixture.Observer.Reads == 1 && !reading.IsCompleted,
                "overlapping timer ticks do not start overlapping native reads");
            fixture.Controller.UpdateOptions(fixture.Controller.Options with { Enabled = false });
            delayedRead.SetResult(fixture.Observer.Current());
            await reading;
            Require(fixture.Host.Generations == 0 && !fixture.Controller.ShouldObserve,
                "an observation finishing after disable cannot trigger work");
        }

        using (var fixture = new Fixture())
        {
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            fixture.Clock.Set(21);
            fixture.Controller.ObserveCodexTransition();
            Require(fixture.Host.GenerationToken.IsCancellationRequested, "Codex notifications interrupt an in-flight optional reaction");
            generation.SetResult(new WorkReactionReply("Duplicate celebration.", "happy"));
            await fixture.Controller.PendingTask;
            for (int second = 25; second <= 45; second += 5) await fixture.Step(second, "Reader");
            for (int second = 50; second <= 70; second += 5) await fixture.Step(second, "Browser");
            Require(fixture.Host.Generations == 1 && fixture.Host.Presentations == 0,
                "Codex transition only reserves priority and cooldown, without generating a duplicate message");
            fixture.Controller.UpdateOptions(fixture.Controller.Options with { UseCodexEvents = false });
            var status = fixture.Controller.Status;
            fixture.Controller.ObserveCodexTransition();
            Require(fixture.Controller.Status == status && fixture.Host.Generations == 1,
                "disabled Codex-event coordination leaves module state unchanged");
        }

        using (var fixture = new Fixture())
        {
            fixture.SaveFails = true;
            bool failed = false;
            try { fixture.Controller.UpdateOptions(fixture.Controller.Options with { Enabled = false, IncludeWindowTitles = true }); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed && fixture.Controller.Options.Enabled && !fixture.Controller.Options.IncludeWindowTitles &&
                fixture.Controller.ShouldObserve, "failed settings persistence leaves the old live options intact");
            var options = fixture.Controller.Options;
            options.ExcludedApps[0] = "mutated";
            Require(fixture.Controller.Options.ExcludedApps[0] != "mutated", "callers cannot mutate live exclusion settings through the options getter");
        }

        // A future async presentation adapter may finish after cancellation. It
        // must not resurrect cleared UI status even if it returns true anyway.
        foreach (string action in new[] { "clear", "pause", "disable" })
        {
            using var fixture = new Fixture();
            var generation = fixture.Host.BlockGeneration();
            var presentation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var presentationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Host.Presentation = (_, _, _, _) =>
            {
                presentationEntered.TrySetResult(true);
                return presentation.Task;
            };
            await fixture.Trigger();
            fixture.Clock.Set(21);
            generation.SetResult(new WorkReactionReply("Too late to remember.", null));
            await presentationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Host.State = fixture.Host.State with { Available = false, Revision = 1 };
            int reads = fixture.Observer.Reads;
            await fixture.Step(22, "Reader");
            Require(fixture.Observer.Reads == reads && !fixture.Host.GenerationToken.IsCancellationRequested,
                "presentation is not cancelled by its own bubble or expression: " + action);
            switch (action)
            {
                case "clear": fixture.Controller.ClearTransientState(); break;
                case "pause": fixture.Controller.SetPaused(true); break;
                case "disable": fixture.Controller.UpdateOptions(fixture.Controller.Options with { Enabled = false }); break;
            }
            var status = fixture.Controller.Status;
            presentation.SetResult(true);
            await fixture.Controller.PendingTask;
            Require(fixture.Controller.Status == status && fixture.Controller.Status.LastReaction == null,
                "late async presentation completion cannot restore cleared reaction state: " + action);
        }

        using (var fixture = new Fixture())
        {
            fixture.Controller.UpdateOptions(fixture.Controller.Options with
            { WorkEnabled = false, GameEnabled = false, MediaEnabled = false, DailyEnabled = false });
            await fixture.Step(0);
            Require(fixture.Observer.Reads == 0, "all activity switches off stops sampling entirely");
        }

        using (var fixture = new Fixture())
        {
            fixture.Controller.UpdateOptions(fixture.Controller.Options with { GameEnabled = false });
            await fixture.Step(0);
            for (int seconds = 5; seconds <= 30; seconds += 5) await fixture.Step(seconds, "cs2");
            Require(fixture.Host.Generations == 0, "disabled game context never enters the LLM lane");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Step(0);
            for (int seconds = 5; seconds <= 20; seconds += 5) await fixture.Step(seconds, "cs2");
            Require(fixture.Host.LastCandidate is { Activity: WorkActivity.Game, PreviousActivity: WorkActivity.Work },
                "controller classifies game app and forwards the previous work activity");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Step(0);
            for (int seconds = 5; seconds <= 40; seconds += 5) await fixture.Step(seconds, "cs2", fullscreen: true);
            Require(fixture.Host.Generations == 0, "default fullscreen quiet tracks activity without any model generation");
            for (int seconds = 45; seconds <= 60; seconds += 5) await fixture.Step(seconds, "chrome");
            Require(fixture.Host.LastCandidate is { Activity: WorkActivity.Daily, PreviousActivity: WorkActivity.Game },
                "returning from quiet fullscreen game preserves context for a later daily-use remark");
        }

        using (var fixture = new Fixture())
        {
            fixture.Controller.UpdateOptions(fixture.Controller.Options with { QuietFullscreen = false });
            await fixture.Step(0);
            for (int seconds = 5; seconds <= 20; seconds += 5) await fixture.Step(seconds, "cs2", fullscreen: true);
            Require(fixture.Host.Generations == 1, "explicit fullscreen opt-in allows one quiet stable game candidate");
        }

        using (var fixture = new Fixture())
        {
            var generation = fixture.Host.BlockGeneration();
            await fixture.Step(0);
            for (int seconds = 5; seconds <= 20; seconds += 5) await fixture.Step(seconds, "cs2");
            await fixture.Step(25, "cs2", fullscreen: true);
            Require(fixture.Host.GenerationToken.IsCancellationRequested, "entering fullscreen quiet cancels an in-flight reply");
            generation.SetResult(new WorkReactionReply("Stale game reply", null));
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Presentations == 0, "fullscreen cancellation discards late model output");
        }

        using (var fixture = new Fixture())
        {
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            fixture.Clock.Set(21); fixture.Observer.Fullscreen = true;
            generation.SetResult(new WorkReactionReply("Late fullscreen reply", null));
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Presentations == 0, "fresh display-time sample suppresses fullscreen even between timer ticks");
        }

        foreach (var scenario in new[] { ("cs2", "game_break", 30), ("vlc", "media_break", 60), ("chrome", "daily_break", 90) })
        {
            using var fixture = new Fixture(new WorkCompanionOptions
            {
                Enabled = true, ReactToAppChanges = false, GameBreakAfterMinutes = 30,
                MediaBreakAfterMinutes = 60, DailyBreakAfterMinutes = 90
            });
            for (int seconds = 0; seconds <= scenario.Item3 * 60; seconds += 5)
                await fixture.Step(seconds, scenario.Item1);
            Require(fixture.Host.Generations == 1 && fixture.Host.LastCandidate?.Kind == scenario.Item2 &&
                fixture.Host.LastCandidate.ActiveMinutes == scenario.Item3,
                "each activity uses its own reminder threshold: " + scenario.Item2);
        }

        using (var fixture = new Fixture())
        {
            fixture.Controller.UpdateOptions(fixture.Controller.Options with
            { AppActivities = new Dictionary<string, string> { ["custom-game"] = "game" } });
            var copy = fixture.Controller.Options; copy.AppActivities["custom-game"] = "work";
            await fixture.Step(0);
            for (int seconds = 5; seconds <= 20; seconds += 5) await fixture.Step(seconds, "custom-game");
            Require(fixture.Host.LastCandidate?.Activity == WorkActivity.Game,
                "custom app classification is applied and options snapshots cannot mutate the live mapping");
        }

        using (var fixture = new Fixture(extension: new FakeExtension()))
        {
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            Require(fixture.Host.LastCandidate is { Kind: "context_requested", WindowKey: "Reader-window", ContextRequestKey: "fixture-context-1" } &&
                fixture.Host.LastCandidate.ExpiresAt - fixture.Host.LastCandidate.At == TimeSpan.FromSeconds(50),
                "optional context request retains the exact target and grants a bounded fifty-second candidate");
            fixture.Clock.Set(21);
            generation.SetResult(new WorkReactionReply("", null) { ContextOnly = true });
            await fixture.Controller.PendingTask;
            Require(fixture.Host.FinishedAccepted == 1 && fixture.Host.FinishedRejected == 0 &&
                fixture.Host.Presentations == 0 && fixture.Controller.Status.LastReaction == null,
                "silent context result commits only after fresh validation and never displays a bubble or speaks");
        }

        foreach (string stale in new[] { "window", "input", "revision", "fullscreen", "extension", "pause", "clear" })
        {
            var extension = new FakeExtension();
            using var fixture = new Fixture(extension: extension);
            var generation = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            fixture.Clock.Set(21);
            switch (stale)
            {
                case "window": fixture.Observer.App = "Browser"; break;
                case "input": fixture.Observer.IdleSeconds = 0; break;
                case "revision": fixture.Host.State = fixture.Host.State with { Revision = 2 }; break;
                case "fullscreen": fixture.Observer.Fullscreen = true; break;
                case "extension": extension.Invalidate(); break;
                case "pause": fixture.Controller.SetPaused(true); break;
                case "clear": fixture.Controller.ClearTransientState(); break;
            }
            generation.SetResult(new WorkReactionReply("", null) { ContextOnly = true });
            await fixture.Controller.PendingTask;
            Require(fixture.Host.FinishedAccepted == 0 && fixture.Host.FinishedRejected == 1 && fixture.Host.Presentations == 0,
                "stale context output cannot commit application memory or display after " + stale);
        }

        foreach (bool explicitRequest in new[] { false, true })
        {
            var extension = new FakeExtension();
            using var fixture = new Fixture(extension: extension);
            await fixture.Trigger();
            await fixture.Controller.PendingTask;
            extension.ContextRequest = "fixture-context-2"; extension.Explicit = explicitRequest;
            await fixture.Step(25, "Reader");
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Generations == (explicitRequest ? 2 : 1), explicitRequest
                ? "explicit context request may bypass the ordinary reaction cooldown"
                : "automatic context request still respects the ordinary reaction cooldown");
        }

        foreach (bool explicitRequest in new[] { false, true })
        {
            var extension = new FakeExtension { Explicit = explicitRequest };
            using var fixture = new Fixture(extension: extension);
            fixture.Host.State = fixture.Host.State with { Available = false };
            for (int second = 0; second <= 25; second += 5) await fixture.Step(second, "Reader");
            Require(fixture.Host.Generations == 0 && extension.ContextRequest == "fixture-context-1",
                "busy host preserves the same " + (explicitRequest ? "manual request" : "automatic time-bucket request"));
            fixture.Host.State = fixture.Host.State with { Available = true };
            await fixture.Step(30, "Reader", idleSeconds: 0);
            Require(fixture.Host.Generations == 0, "deferred context still waits when the user resumes input: " + explicitRequest);
            await fixture.Step(35, "Reader");
            await fixture.Controller.PendingTask;
            Require(fixture.Host.Generations == 1 && fixture.Host.LastCandidate?.ContextRequestKey == "fixture-context-1" &&
                fixture.Host.LastCandidate.At == fixture.Clock.Now,
                "same-window context resumes once with a fresh candidate after host recovery: " + explicitRequest);
            for (int second = 40; second <= 60; second += 5) await fixture.Step(second, "Reader");
            Require(fixture.Host.Generations == 1, "a started context attempt is not retried every sample: " + explicitRequest);
        }

        {
            var extension = new FakeExtension();
            using var fixture = new Fixture(extension: extension);
            await fixture.Trigger(); await fixture.Controller.PendingTask;
            // The next automatic bucket arrives during the global cooldown.
            // It must survive as an opportunity, without retaining a candidate.
            extension.ContextRequest = "fixture-context-next-bucket";
            for (int second = 25; second <= 195; second += 5) await fixture.Step(second, "Reader");
            Require(fixture.Host.Generations == 1, "automatic same-bucket deferral never bypasses the global cooldown");
            await fixture.Step(200, "Reader"); await fixture.Controller.PendingTask;
            Require(fixture.Host.Generations == 2 && fixture.Host.LastCandidate?.ContextRequestKey == "fixture-context-next-bucket" &&
                fixture.Host.LastCandidate.At == fixture.Clock.Now,
                "an automatic bucket remains eligible when cooldown ends in the same window");
            for (int second = 205; second <= 230; second += 5) await fixture.Step(second, "Reader");
            Require(fixture.Host.Generations == 2, "one resumed bucket generates at most one completed attempt");
        }

        {
            var extension = new FakeExtension();
            using var fixture = new Fixture(extension: extension);
            var oldGeneration = fixture.Host.BlockGeneration();
            await fixture.Trigger();
            Task oldTask = fixture.Controller.PendingTask;
            extension.ContextRequest = "manual-after-old-task"; extension.Explicit = true;
            extension.Invalidate();
            for (int second = 25; second <= 40; second += 5) await fixture.Step(second, "Reader");
            Require(fixture.Host.Generations == 1 && !oldTask.IsCompleted && fixture.Host.GenerationToken.IsCancellationRequested,
                "queued manual request waits for an older cancelled generation to drain");
            fixture.Clock.Set(41); oldGeneration.SetResult(null); await oldTask;
            await fixture.Step(45, "Reader"); await fixture.Controller.PendingTask;
            Require(fixture.Host.Generations == 2 && fixture.Host.LastCandidate?.ContextRequestKey == "manual-after-old-task" &&
                fixture.Host.LastCandidate.At == fixture.Clock.Now,
                "manual request is not lost when the older task finishes in the same window");
        }

        using (var fixture = new Fixture(extension: new FakeExtension { Activity = WorkActivity.Game }))
        {
            await fixture.Trigger();
            Require(fixture.Host.LastCandidate?.Activity == WorkActivity.Game,
                "remembered application category is applied before building a context candidate");
        }
        using (var fixture = new Fixture(new WorkCompanionOptions
        {
            Enabled = true, BreakReminders = false,
            AppActivities = new Dictionary<string, string> { ["reader"] = "work" }
        }, new FakeExtension { Activity = WorkActivity.Game }))
        {
            await fixture.Trigger();
            Require(fixture.Host.LastCandidate?.Activity == WorkActivity.Work,
                "explicit application category takes priority over the optional learned category");
        }

        return new { success = true, count = checks.Count, checks };
    }

    private sealed class Fixture : IDisposable
    {
        public FakeClock Clock { get; } = new();
        public FakeObserver Observer { get; }
        public FakeHost Host { get; } = new();
        public WorkCompanionController Controller { get; }
        public bool SaveFails { get; set; }

        public Fixture(WorkCompanionOptions? options = null, IWorkContextExtension? extension = null)
        {
            Observer = new FakeObserver(Clock);
            Controller = new WorkCompanionController(Observer, Host, options ?? new WorkCompanionOptions
            {
                Enabled = true, CooldownMinutes = 3, BreakReminders = false
            }, _ =>
            {
                if (SaveFails) throw new InvalidOperationException("Synthetic save failure");
            }, () => Clock.Now, extension);
        }

        public Task Step(int seconds, string app = "Code", int idleSeconds = 5, bool fullscreen = false)
        {
            Clock.Set(seconds);
            Observer.App = app;
            Observer.IdleSeconds = idleSeconds;
            Observer.Fullscreen = fullscreen;
            return Controller.TickAsync();
        }

        public async Task Trigger()
        {
            await Step(0);
            for (int seconds = 5; seconds <= 20; seconds += 5) await Step(seconds, "Reader");
        }

        public void Dispose() => Controller.Dispose();
    }

    private sealed class FakeClock
    {
        private readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Set(int seconds) => Now = _start.AddSeconds(seconds);
    }

    private sealed class FakeObserver : IDesktopObserver
    {
        private readonly FakeClock _clock;
        public int Reads { get; private set; }
        public string App { get; set; } = "Code";
        public int IdleSeconds { get; set; } = 5;
        public bool Fullscreen { get; set; }
        public Task<WorkObservation>? DelayedRead { get; set; }

        public FakeObserver(FakeClock clock) => _clock = clock;
        public WorkObservation Current() => new(_clock.Now, App, null, App + "-window",
            TimeSpan.FromSeconds(IdleSeconds), true, Fullscreen, false);
        public Task<WorkObservation> ReadAsync(bool includeTitles, IReadOnlyList<string> excludedApps, CancellationToken cancellationToken)
        {
            Reads++;
            var delayed = DelayedRead;
            DelayedRead = null;
            return delayed ?? Task.FromResult(Current());
        }
    }

    private sealed class FakeHost : IWorkCompanionHost
    {
        public WorkHostState State { get; set; } = new(true, "fixture-context", 0);
        public int Captures { get; private set; }
        public int Generations { get; private set; }
        public int Presentations { get; private set; }
        public int FinishedAccepted { get; private set; }
        public int FinishedRejected { get; private set; }
        public WorkReactionCandidate? LastCandidate { get; private set; }
        public bool LastSpeak { get; private set; }
        public CancellationToken GenerationToken { get; private set; }
        public Func<WorkReactionReply, WorkHostState, bool, CancellationToken, Task<bool>>? Presentation { get; set; }
        private Task<WorkReactionReply?>? _generation;

        public TaskCompletionSource<WorkReactionReply?> BlockGeneration()
        {
            var pending = new TaskCompletionSource<WorkReactionReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _generation = pending.Task;
            return pending;
        }
        public WorkHostState Capture() { Captures++; return State; }
        public Task<WorkReactionReply?> GenerateAsync(WorkReactionCandidate candidate, CancellationToken ct)
        {
            Generations++;
            LastCandidate = candidate;
            GenerationToken = ct;
            var generation = _generation;
            _generation = null;
            return generation ?? Task.FromResult<WorkReactionReply?>(null);
        }
        public Task<bool> PresentAsync(WorkReactionReply reply, WorkHostState expected, bool speak, CancellationToken ct)
        {
            Presentations++;
            LastSpeak = speak;
            return Presentation?.Invoke(reply, expected, speak, ct) ?? Task.FromResult(true);
        }
        public void FinishCandidate(WorkReactionCandidate candidate, bool accepted)
        { if (accepted) FinishedAccepted++; else FinishedRejected++; }
    }

    private sealed class FakeExtension : IWorkContextExtension
    {
        public event Action? Invalidated;
        public TimeSpan ResponseTimeout => TimeSpan.FromSeconds(45);
        public string? ContextRequest { get; set; } = "fixture-context-1";
        public bool Explicit { get; set; }
        public WorkActivity? Activity { get; set; }
        public void Configure(WorkCompanionOptions options, bool paused) { }
        public Task<WorkActivity?> RecallActivityAsync(WorkObservation observation, CancellationToken ct) => Task.FromResult(Activity);
        public string? RequestContext(WorkObservation observation) => ContextRequest;
        public bool IsExplicitRequest(WorkReactionCandidate candidate) => Explicit;
        public Task<WorkReactionReply?> GenerateAsync(IOllamaClient client, string model, string persona,
            WorkReactionCandidate candidate, Func<Task<WorkReactionReply?>> fallback, CancellationToken ct) => fallback();
        public void FinishCandidate(WorkReactionCandidate candidate, bool accepted) { }
        public void ClearTransientState() { }
        internal void Invalidate() => Invalidated?.Invoke();
    }
}
