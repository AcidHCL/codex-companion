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

// Explicit --drag-state-test only, with the caller's non-persisting test pet.
// The controller's real BeginDrag/ObserveDrag/EndDrag path is exercised without
// injecting input or moving any external window.
internal static class DragAnimationIntegrationChecks
{
    public static async Task<object> RunAsync(PetWindow pet)
    {
        pet.Dispatcher.VerifyAccess();
        IntPtr hwnd = new WindowInteropHelper(pet).Handle;
        if (!pet.IsLoaded || hwnd == IntPtr.Zero || pet.MovementController == null)
            throw new InvalidOperationException("Drag animation checks require a loaded isolated test pet.");
        if (!SystemParameters.ClientAreaAnimation)
            throw new InvalidOperationException("Drag physical integration requires Windows animation effects to be enabled.");
        var controller = pet.MovementController;
        Rect originalBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
        string output = Path.Combine(AppContext.BaseDirectory, "drag-state-evidence");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var captures = new List<object>();
        int landingEvents = 0;
        string stage = "initialize", expectedState = "", failure = "";
        JsonElement? lastDiagnostics = null;
        void Trace(string? nextStage = null, Exception? exception = null)
        {
            if (nextStage != null) stage = nextStage;
            // This isolated test uses synthetic tasks and known bundled actions.
            // Record only test-owned state, never raw exception messages, user
            // settings, Codex connection data, or the global production log.
            File.WriteAllText(Path.Combine(output, "drag-state-trace.json"), JsonSerializer.Serialize(new
            {
                stage, expectedState, failure, assertions = checks.Count, checks, landingEvents,
                movementState = controller.State, controller.IsDragging, controller.IsEnabled, controller.Grounded,
                sceneAction = pet.CurrentSceneAction,
                sceneStatus = pet.SceneStatus, lastDiagnostics,
                exceptionType = exception?.GetType().Name, hresult = exception?.HResult,
                syntheticDataOnly = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Landed() => landingEvents++;
        controller.Landed += Landed;
        void Require(bool condition, string label)
        {
            if (!condition) { failure = label; throw new InvalidOperationException("Drag animation integration: " + label); }
            checks.Add(label);
        }
        SceneAnimationView Renderer() => pet.SceneRenderer ?? throw new InvalidOperationException("DeepSeek scene renderer is unavailable.");
        async Task<JsonElement> Read()
        {
            string json = JsonSerializer.Deserialize<string>(await Renderer().GetDiagnosticsAsync())
                ?? throw new InvalidOperationException("Scene diagnostics are empty.");
            using var document = JsonDocument.Parse(json);
            var state = document.RootElement.Clone();
            lastDiagnostics = state;
            return state;
        }
        async Task<JsonElement> WaitState(string action, string? phase = null, int minimumCycles = 0, int timeoutMs = 6000)
        {
            expectedState = action + "/" + phase + "; cycles >= " + minimumCycles;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds <= timeoutMs)
            {
                if (pet.UsesBuiltInScenes && !pet.IsSceneLoading && pet.IsScenePlaying && pet.CurrentSceneAction == action)
                {
                    var state = await Read();
                    if (state.GetProperty("playing").GetBoolean() && state.GetProperty("hasFrame").GetBoolean() &&
                        state.GetProperty("action").GetString() == action && state.GetProperty("displayedAction").GetString() == action &&
                        (phase == null || state.GetProperty("phase").GetString() == phase) && state.GetProperty("cycles").GetInt32() >= minimumCycles)
                        return state;
                }
                await Task.Delay(10);
            }
            failure = "Timed out waiting for " + expectedState;
            throw new TimeoutException("Expected " + action + "/" + phase + "; movement=" + controller.State + ", scene=" + pet.CurrentSceneAction + ": " + pet.SceneStatus);
        }
        async Task<string> Capture(string phase, Point expectedAnchor)
        {
            await WaitState("drag", phase);
            var renderer = Renderer();
            renderer.SetPaused(true);
            var clock = Stopwatch.StartNew();
            JsonElement state;
            do
            {
                state = await Read();
                if (state.GetProperty("paused").GetBoolean() && !state.GetProperty("timerActive").GetBoolean()) break;
                if (clock.ElapsedMilliseconds > 2000) throw new TimeoutException("Unable to pause drag for a frame capture.");
                await Task.Delay(10);
            } while (true);
            string file = Path.Combine(output, "drag-" + phase + ".png");
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
                Require(state.GetProperty("phase").GetString() == phase && visible > 100 && transparent > 100,
                    "drag " + phase + " renders visible artwork over a transparent background");
                Require((pet.VisibleFootPixels - expectedAnchor).Length <= .25, "drag " + phase + " preserves the shared contact anchor");
                captures.Add(new { phase, file, visiblePixels = visible, transparentPixels = transparent,
                    bitmap.Width, bitmap.Height, frame = state.GetProperty("frame").GetInt32() });
                return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
            finally { renderer.SetPaused(false); }
        }
        CodexSnapshot Snapshot(bool working) => CodexSnapshot.Empty with
        {
            Connected = true,
            Tasks = working ? new[] { new CodexTaskInfo("drag-test", "drag-test", "active", "codex", null, null, 0, false) }
                : Array.Empty<CodexTaskInfo>()
        };
        void PlaceOwnWindowAboveFloor()
        {
            var work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
            var own = NativeMethods.GetWindowBoundsPixels(hwnd);
            Point anchor = pet.VisibleFootPixels;
            Rect visible = pet.CharacterVisibleBoundsPixels;
            var snapshot = DesktopWindowPlatforms.Capture(hwnd);
            double left = work.Left + Math.Max(own.Width / 2, anchor.X - visible.Left + 10);
            double right = work.Right - Math.Max(own.Width / 2, visible.Right - anchor.X + 10);
            var target = Enumerable.Range(0, 21).Select(i => left + (right - left) * i / 20.0).Select(x =>
            {
                double obstacle = snapshot.Platforms.Where(p => p.Contains(x) && p.Top < work.Bottom)
                    .Select(p => p.Top).DefaultIfEmpty(work.Top).Max();
                double room = Math.Min(work.Bottom - obstacle - 12, work.Height - anchor.Y + visible.Top - 12);
                return new { X = x - anchor.X, Top = work.Bottom - Math.Min(220, room) - anchor.Y, Height = Math.Min(220, room) };
            }).OrderByDescending(p => p.Height).ThenBy(p => Math.Abs(p.X + anchor.X - (work.Left + work.Width / 2))).First();
            if (target.Height < 150) throw new InvalidOperationException("No unobstructed 150 px release lane is available above the desktop floor.");
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(target.X), (int)Math.Round(target.Top));
            controller.ObserveDrag();
        }

        try
        {
            Trace("load-deepseek");
            pet.SetMovement(false);
            pet.SetMotion(false);
            pet.SetCodexStatus(Snapshot(false));
            pet.SelectSkin("deepseek");
            await WaitState("idle", timeoutMs: 20000);
            var drag = pet.CurrentScenePack!.Actions["drag"];
            Require(drag.Start?.Type == "frames" && drag.Start.Frames.Length >= 2 && drag.Loop.Type == "frames" && drag.Loop.Frames.Length >= 2 && drag.Repeat,
                "drag is an intro followed by a repeating PNG struggle loop");
            var startClip = drag.Start!;
            double startMs = startClip.DurationsMs?.Sum() ?? startClip.Frames.Length * 1000.0 / startClip.Fps;
            Point anchor = pet.VisibleFootPixels;
            int blankBefore = (await Read()).GetProperty("blankClears").GetInt32();

            Trace("hold-with-movement-disabled");
            controller.BeginDrag();
            var start = await WaitState("drag", "start");
            string? request = start.GetProperty("requestId").GetString();
            Require(controller.IsDragging && !controller.IsEnabled && !pet.Settings.MotionEnabled,
                "grabbing selects the drag intro while desktop movement and breathing are disabled");
            string startHash = await Capture("start", anchor);
            var loop = await WaitState("drag", "loop");
            string loopHash = await Capture("loop", anchor);
            Require(startHash != loopHash && loop.GetProperty("requestId").GetString() == request,
                "intro flows into distinct struggle artwork within the same animation request");
            for (int i = 0; i < 4; i++) { controller.ObserveDrag(); pet.SetCodexStatus(Snapshot(i % 2 == 0)); }
            var repeated = await WaitState("drag", "loop", minimumCycles: 2);
            Require(repeated.GetProperty("requestId").GetString() == request && repeated.GetProperty("draws").GetInt32() > loop.GetProperty("draws").GetInt32(),
                "holding and repeated task polls loop the struggle without replaying the intro");
            controller.EndDrag();
            await WaitState("idle");
            Require(landingEvents == 0 && !controller.IsDragging, "release with movement disabled returns to idle without a false fall or landing");

            Trace("release-during-intro");
            pet.SetCodexStatus(Snapshot(true));
            await WaitState("working");
            controller.BeginDrag();
            var secondStart = await WaitState("drag", "start");
            Require(secondStart.GetProperty("requestId").GetString() != request, "a new grab restarts its intro once");
            controller.EndDrag(); // Release before the intro has time to finish.
            await WaitState("working");
            await Task.Delay((int)Math.Ceiling(startMs + 80));
            Require(pet.CurrentSceneAction == "working" && (await Read()).GetProperty("displayedAction").GetString() == "working",
                "release during the intro cancels its pending loop and restores the active task");

            Trace("interrupt-manual-preview");
            pet.SetCodexStatus(Snapshot(false));
            await WaitState("idle");
            await pet.PreviewSceneAsync("working");
            await WaitState("working");
            controller.BeginDrag();
            await WaitState("drag", "start");
            controller.EndDrag();
            await WaitState("idle");
            Require(pet.IsScenePlaying, "a real grab interrupts a library preview and release restores automatic idle");

            // Toggle autonomous movement while holding, then release through the
            // same production controller path used by WPF DragMove.
            Trace("toggle-movement-while-held");
            pet.SetCodexStatus(Snapshot(true));
            controller.BeginDrag();
            await WaitState("drag", "start");
            pet.SetMovement(true);
            pet.SetMovement(false);
            Require(pet.CurrentSceneAction == "drag" && controller.IsDragging, "changing autonomous movement while holding does not end the drag animation");
            pet.SetMovement(true);
            PlaceOwnWindowAboveFloor();
            await WaitState("drag", "loop");
            await Task.Delay(220); // Clear any toss velocity before release.
            controller.ObserveDrag();
            double beforeReleaseY = NativeMethods.GetWindowBoundsPixels(hwnd).Top;
            int beforeLanding = landingEvents;
            Trace("release-to-fall-and-land");
            controller.EndDrag();
            await WaitState("fall");
            Require(!controller.IsDragging && !controller.Grounded, "enabled-movement release switches immediately from struggle to fall");
            await WaitState("land");
            Require(landingEvents == beforeLanding + 1 && controller.Grounded && NativeMethods.GetWindowBoundsPixels(hwnd).Top - beforeReleaseY > 140,
                "released test HWND physically descends and plays its one-shot landing");
            Trace("grab-landing-and-disable-before-release");
            controller.BeginDrag();
            await WaitState("drag", "start");
            Require(controller.IsDragging && !controller.TryWalk(1), "grabbing again interrupts the still-active landing pose and keeps walking suspended");
            pet.SetMovement(false);
            controller.EndDrag();
            await WaitState("working");
            Require((pet.VisibleFootPixels - anchor).Length <= .25 && (await Read()).GetProperty("blankClears").GetInt32() == blankBefore,
                "intro, struggle, release and landing keep one anchor with no blank transition");

            Trace("other-skin-regression");
            pet.SelectSkin("gemini");
            controller.BeginDrag();
            await Task.Delay(100);
            Require(pet.CurrentSkin.Id == "gemini" && pet.CurrentScenePack == null && pet.SceneRenderer == null,
                "other characters retain their current appearance while dragged");
            controller.EndDrag();
            pet.SelectSkin("deepseek");
            await WaitState("working", timeoutMs: 20000);
            Trace("complete");
            return new { passed = true, assertions = checks.Count, checks, captures, startDurationMs = startMs,
                landingEvents, output, syntheticMouseOrKeyboardInput = false, settingsPersisted = false, onlyOwnTestWindowMoved = true };
        }
        catch (Exception ex)
        {
            Trace(exception: ex);
            throw;
        }
        finally
        {
            controller.Landed -= Landed;
            pet.SetMovement(false);
            if (controller.IsDragging) controller.EndDrag();
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(originalBounds.Left), (int)Math.Round(originalBounds.Top));
            controller.NotifyExternalMove();
        }
    }
}
