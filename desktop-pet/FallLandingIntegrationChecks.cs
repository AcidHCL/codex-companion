using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace DesktopPet;

// Explicit --fall-land-test only. This runs on the caller's isolated, non-persisting
// test pet. Only that pet's own HWND is moved; no mouse/keyboard input is injected.
internal static class FallLandingIntegrationChecks
{
    public static async Task<object> RunAsync(PetWindow pet)
    {
        pet.Dispatcher.VerifyAccess();
        IntPtr hwnd = new WindowInteropHelper(pet).Handle;
        if (!pet.IsLoaded || hwnd == IntPtr.Zero || pet.MovementController == null)
            throw new InvalidOperationException("Fall/landing checks require a loaded isolated test pet.");
        if (!SystemParameters.ClientAreaAnimation)
            throw new InvalidOperationException("Fall/landing physical integration requires Windows animation effects to be enabled.");

        var movement = pet.MovementController;
        Rect originalBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
        string output = Path.Combine(AppContext.BaseDirectory, "fall-land-evidence");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var captures = new List<object>();
        var impacts = new List<object>();
        int landingEvents = 0;
        void Landed() => landingEvents++;
        movement.Landed += Landed;

        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Fall/landing integration: " + label);
            checks.Add(label);
        }
        SceneAnimationView Renderer() => pet.SceneRenderer ?? throw new InvalidOperationException("DeepSeek scene renderer is unavailable.");
        async Task<JsonElement> Read()
        {
            string json = JsonSerializer.Deserialize<string>(await Renderer().GetDiagnosticsAsync())
                ?? throw new InvalidOperationException("Scene diagnostics are empty.");
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        async Task<JsonElement> WaitState(string action, int timeoutMs = 6000)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds <= timeoutMs)
            {
                if (pet.UsesBuiltInScenes && !pet.IsSceneLoading && pet.IsScenePlaying && pet.CurrentSceneAction == action)
                {
                    var state = await Read();
                    if (state.GetProperty("playing").GetBoolean() && state.GetProperty("hasFrame").GetBoolean() &&
                        state.GetProperty("action").GetString() == action && state.GetProperty("displayedAction").GetString() == action)
                        return state;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("Expected " + action + "; movement=" + movement.State + ", scene=" + pet.CurrentSceneAction + ": " + pet.SceneStatus);
        }
        async Task<string> CapturePreview(string action, Point expectedAnchor)
        {
            await pet.PreviewSceneAsync(action);
            await WaitState(action);
            var renderer = Renderer();
            renderer.SetPaused(true);
            var watch = Stopwatch.StartNew();
            JsonElement state;
            do
            {
                state = await Read();
                if (state.GetProperty("paused").GetBoolean() && !state.GetProperty("timerActive").GetBoolean()) break;
                if (watch.ElapsedMilliseconds > 2500) throw new TimeoutException("Unable to freeze " + action + " for capture.");
                await Task.Delay(10);
            } while (true);
            string file = Path.Combine(output, action + ".png");
            try
            {
                await Task.Delay(60);
                await renderer.CaptureAsync(file);
                using var bitmap = new System.Drawing.Bitmap(file);
                int visible = 0, transparent = 0;
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        byte alpha = bitmap.GetPixel(x, y).A;
                        if (alpha >= 16) visible++;
                        else if (alpha == 0) transparent++;
                    }
                Require(visible > 100 && transparent > 100, action + " renders artwork on a transparent canvas");
                Require((pet.VisibleFootPixels - expectedAnchor).Length <= .25, action + " retains the shared production foot anchor");
                captures.Add(new { action, file, visiblePixels = visible, transparentPixels = transparent, bitmap.Width, bitmap.Height,
                    frame = state.GetProperty("frame").GetInt32(), footX = pet.VisibleFootPixels.X, footY = pet.VisibleFootPixels.Y });
                return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
            finally { renderer.SetPaused(false); pet.StopScenePreview(); }
        }

        CodexSnapshot Snapshot(bool working) => CodexSnapshot.Empty with
        {
            Connected = true,
            Tasks = working ? new[] { new CodexTaskInfo("fall-land-test", "fall-land-test", "active", "codex", null, null, 0, false) }
                : Array.Empty<CodexTaskInfo>()
        };

        // Choose a clear vertical lane above the real work-area floor. Existing
        // windows are read only; no external HWND is moved to prepare the test.
        (double X, double Top, double Floor, double Height) DropPosition()
        {
            var work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
            var own = NativeMethods.GetWindowBoundsPixels(hwnd);
            Point anchor = pet.VisibleFootPixels;
            Rect visible = pet.CharacterVisibleBoundsPixels;
            var snapshot = DesktopWindowPlatforms.Capture(hwnd);
            double left = work.Left + Math.Max(own.Width / 2, anchor.X - visible.Left + 10);
            double right = work.Right - Math.Max(own.Width / 2, visible.Right - anchor.X + 10);
            var options = Enumerable.Range(0, 21).Select(i => left + (right - left) * i / 20.0)
                .Select(x =>
                {
                    double obstacle = snapshot.Platforms.Where(p => p.Contains(x) && p.Top < work.Bottom)
                        .Select(p => p.Top).DefaultIfEmpty(work.Top).Max();
                    double room = Math.Min(work.Bottom - obstacle - 12, work.Height - anchor.Y + visible.Top - 12);
                    return (X: x - anchor.X, Top: work.Bottom - Math.Min(220, room) - anchor.Y,
                        Floor: work.Bottom, Height: Math.Min(220, room));
                }).OrderByDescending(p => p.Height).ThenBy(p => Math.Abs(p.X + anchor.X - (work.Left + work.Width / 2))).ToArray();
            var choice = options.FirstOrDefault();
            if (choice.Height < 150) throw new InvalidOperationException("No unobstructed 150 px drop lane is available above the current desktop floor.");
            return choice;
        }
        (double Floor, double StartY) StartDrop(bool resetEnabled)
        {
            if (resetEnabled) pet.SetMovement(false);
            var target = DropPosition();
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(target.X), (int)Math.Round(target.Top));
            movement.NotifyExternalMove();
            if (resetEnabled) pet.SetMovement(true);
            return (target.Floor, NativeMethods.GetWindowBoundsPixels(hwnd).Top);
        }

        try
        {
            pet.SetMovement(false);
            pet.SetMotion(false);
            pet.SetCodexStatus(Snapshot(false));
            pet.SelectSkin("deepseek");
            await WaitState("idle", 20000);
            var pack = pet.CurrentScenePack ?? throw new InvalidOperationException("Built-in scene pack missing.");
            Require(pack.Actions.ContainsKey("fall") && pack.Actions.ContainsKey("land"), "built-in DeepSeek pack includes fall and land states");
            Require(pack.Actions["fall"].Repeat && !pack.Actions["land"].Repeat, "fall loops while landing is a one-shot");
            Require(pack.Actions["fall"].Loop.Frames.Length == 8 && pack.Actions["land"].Loop.Frames.Length == 8,
                "fall and landing each load all eight PNG frames");
            var landingClip = pack.Actions["land"].Loop;
            double landingDurationMs = landingClip.DurationsMs?.Sum() ?? landingClip.Frames.Length * 1000.0 / landingClip.Fps;
            Require(landingDurationMs >= 400 && landingDurationMs <= 2000, "landing has a finite, readable recovery duration");
            Point contactAnchor = pet.VisibleFootPixels;
            string fallHash = await CapturePreview("fall", contactAnchor);
            await WaitState("idle");
            string landHash = await CapturePreview("land", contactAnchor);
            await WaitState("idle");
            Require(fallHash != landHash, "fall and landing display distinct animation artwork");
            Require(landingEvents == 0, "manual animation previews do not manufacture physical landing events");

            foreach (bool working in new[] { false, true })
            {
                pet.SetCodexStatus(Snapshot(working));
                await WaitState(working ? "working" : "idle");
                int before = landingEvents;
                int blankBefore = (await Read()).GetProperty("blankClears").GetInt32();
                var drop = StartDrop(true);
                var fall = await WaitState("fall");
                Require(!movement.Grounded && movement.State == "fall", "physical descent selects fall while " + (working ? "a task is running" : "idle"));
                var land = await WaitState("land");
                var landingWatch = Stopwatch.StartNew();
                Require(land.GetProperty("draws").GetInt32() - fall.GetProperty("draws").GetInt32() >= 3,
                    "fall animation advances through multiple frames during actual descent");
                double landedTop = NativeMethods.GetWindowBoundsPixels(hwnd).Top;
                Require(landedTop - drop.StartY >= 145 && movement.Grounded && landingEvents == before + 1,
                    "actual HWND descent produces one grounded landing while " + (working ? "working" : "idle"));
                Require(Math.Abs(movement.FootScreen.Y - drop.Floor) <= .6 && (pet.VisibleFootPixels - contactAnchor).Length <= .25,
                    "physical landing aligns the shared foot anchor to the desktop floor");
                Require(!movement.TryWalk(1), "landing pose blocks autonomous walking while keeping ground contact");
                string? landRequest = land.GetProperty("requestId").GetString();
                for (int i = 0; i < 3; i++) pet.SetCodexStatus(Snapshot(working));
                var polled = await Read();
                Require(pet.CurrentSceneAction == "land" && polled.GetProperty("requestId").GetString() == landRequest,
                    "repeated Codex snapshots do not restart or interrupt landing");
                await WaitState(working ? "working" : "idle");
                var resumed = await Read();
                Require(landingWatch.Elapsed.TotalMilliseconds >= landingDurationMs - 180 &&
                    resumed.GetProperty("draws").GetInt32() - land.GetProperty("draws").GetInt32() >= 4,
                    "landing visibly advances through recovery before returning to its baseline state");
                Require(landingEvents == before + 1 && resumed.GetProperty("blankClears").GetInt32() == blankBefore,
                    "landing finishes once and resumes " + (working ? "working" : "idle") + " without a blank canvas");
                impacts.Add(new { whileWorking = working, startTop = drop.StartY, landedTop, floor = drop.Floor,
                    landingEvents, landingRequestId = landRequest, landingDurationMs,
                    observedLandingMs = landingWatch.Elapsed.TotalMilliseconds, resumedAction = pet.CurrentSceneAction });
                pet.SetMovement(false);
            }

            // A fresh fall must interrupt a still-running landing, and only its
            // real second contact may start the next one-shot.
            pet.SetCodexStatus(Snapshot(true));
            await WaitState("working");
            int interruptionBefore = landingEvents;
            StartDrop(true);
            await WaitState("fall");
            var firstLanding = await WaitState("land");
            string? firstLandingRequest = firstLanding.GetProperty("requestId").GetString();
            StartDrop(false);
            await WaitState("fall");
            Require(!movement.Grounded && landingEvents == interruptionBefore + 1,
                "a new descent interrupts an unfinished landing immediately");
            var secondLanding = await WaitState("land");
            Require(landingEvents == interruptionBefore + 2 && secondLanding.GetProperty("requestId").GetString() != firstLandingRequest,
                "the second real touchdown starts a fresh landing one-shot");
            await WaitState("working");

            int stableEvents = landingEvents;
            movement.SetPaused(true);
            await Task.Delay(120);
            movement.SetPaused(false);
            pet.Hide();
            await Task.Delay(120);
            pet.Show();
            pet.SetCodexStatus(Snapshot(true));
            await WaitState("working");
            pet.SetMovement(false);
            pet.SetMovement(true); // Snapping at the same floor is not a fall.
            await Task.Delay(180);
            Require(landingEvents == stableEvents && pet.CurrentSceneAction == "working",
                "pause, hide/show, repeated snapshots, and ground snapping do not create landing events");

            // Disable during descent: it must cancel physical and animation
            // state instead of pretending that the interrupted fall landed.
            StartDrop(true);
            await WaitState("fall");
            int disabledBefore = landingEvents;
            pet.SetMovement(false);
            await WaitState("working");
            await Task.Delay(180);
            Require(landingEvents == disabledBefore && movement.State == "idle" && !movement.IsEnabled,
                "disabling movement during a fall restores working without a false landing");
            Require(!pet.Settings.MotionEnabled, "fall and landing work with the unrelated breathing animation disabled");

            return new { passed = true, assertions = checks.Count, checks, captures, impacts, landingEvents,
                output, syntheticMouseOrKeyboardInput = false, settingsPersisted = false, onlyOwnTestWindowMoved = true };
        }
        finally
        {
            movement.Landed -= Landed;
            pet.SetMovement(false);
            if (!pet.IsVisible) pet.Show();
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(originalBounds.Left), (int)Math.Round(originalBounds.Top));
            movement.NotifyExternalMove();
        }
    }
}
