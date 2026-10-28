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

// Explicit --deepseek-state-test only. The caller supplies a non-persisting pet;
// snapshots are synthetic and only this test pet's own HWND may be repositioned.
internal static class DeepSeekAnimationIntegrationChecks
{
    public static async Task<object> RunAsync(PetWindow pet)
    {
        pet.Dispatcher.VerifyAccess();
        IntPtr hwnd = new WindowInteropHelper(pet).Handle;
        if (!pet.IsLoaded || hwnd == IntPtr.Zero || pet.MovementController == null)
            throw new InvalidOperationException("DeepSeek animation checks require a loaded isolated test pet.");
        var movement = pet.MovementController;
        string output = Path.Combine(AppContext.BaseDirectory, "deepseek-state-evidence");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var captures = new List<object>();
        Rect originalBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
        bool actualWalkObserved = false;
        string? observedWalkAction = null;
        var actualWalkChecks = new List<object>();
        int automaticBlankClears = 0, preservedTransitions = 0;
        bool forcedClock = !SystemParameters.ClientAreaAnimation;
        string syntheticPhase = "initializing";
        JsonElement? lastDiagnostics = null;
        void TraceFailure(string expected)
        {
            // Explicit synthetic animation test only; do not relax production
            // exception logging or inspect any real Codex/user configuration.
            File.WriteAllText(Path.Combine(output, "deepseek-state-trace.json"), JsonSerializer.Serialize(new
            {
                expected, syntheticPhase, assertions = checks.Count, checks,
                skin = pet.CurrentSkin.Id, pack = pet.CurrentScenePack?.Id, pet.IsSceneLoading, pet.IsScenePlaying,
                action = pet.CurrentSceneAction, sceneStatus = pet.SceneStatus, pet.IsVisible,
                movement = movement.State, movement.IsEnabled, movement.IsDragging, lastDiagnostics, syntheticDataOnly = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("DeepSeek state integration: " + label);
            checks.Add(label);
        }
        SceneAnimationView Renderer() => pet.SceneRenderer ?? throw new InvalidOperationException("DeepSeek scene renderer is unavailable.");
        async Task<JsonElement> Read()
        {
            var state = Decode(await Renderer().GetDiagnosticsAsync());
            lastDiagnostics = state;
            return state;
        }
        async Task Wait(Func<bool> predicate, string label, int timeoutMs = 10000)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate())
            {
                if (clock.ElapsedMilliseconds > timeoutMs) { TraceFailure(label); throw new TimeoutException(label + ": " + pet.SceneStatus); }
                await Task.Delay(40);
            }
        }
        async Task<JsonElement> WaitState(string action, int timeoutMs = 20000)
        {
            syntheticPhase = "wait-" + action;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds <= timeoutMs)
            {
                if (pet.UsesBuiltInScenes && !pet.IsSceneLoading && pet.CurrentSceneAction == action && pet.IsScenePlaying)
                {
                    // The explicit test still exercises finite previews when Windows
                    // has reduced motion enabled; product settings are not changed.
                    if (forcedClock) Renderer().SetPaused(false);
                    var state = await Read();
                    if (state.GetProperty("playing").GetBoolean() && state.GetProperty("hasFrame").GetBoolean() &&
                        state.GetProperty("action").GetString() == action && state.GetProperty("displayedAction").GetString() == action)
                        return state;
                }
                await Task.Delay(40);
            }
            TraceFailure(action);
            throw new TimeoutException("Expected DeepSeek state " + action + ", got " + pet.CurrentSceneAction + ": " + pet.SceneStatus);
        }
        async Task<string> Capture(string action, Point expectedFoot)
        {
            await WaitState(action);
            var renderer = Renderer();
            renderer.SetPaused(true);
            var watch = Stopwatch.StartNew();
            JsonElement state;
            do
            {
                state = await Read();
                if (state.GetProperty("paused").GetBoolean() && !state.GetProperty("timerActive").GetBoolean()) break;
                if (watch.ElapsedMilliseconds > 3000) throw new TimeoutException("Unable to freeze " + action + " for capture.");
                await Task.Delay(30);
            } while (true);
            await Task.Delay(90); // Wait for the acknowledged canvas to reach the compositor.
            string file = Path.Combine(output, action + ".png");
            try
            {
                await renderer.CaptureAsync(file);
                using var bitmap = new System.Drawing.Bitmap(file);
                int visiblePixels = 0, transparentPixels = 0;
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        byte alpha = bitmap.GetPixel(x, y).A;
                        if (alpha >= 16) visiblePixels++;
                        else if (alpha == 0) transparentPixels++;
                    }
                Require(visiblePixels > 100 && transparentPixels > 100, action + " capture contains visible artwork and a transparent background");
                Point foot = pet.VisibleFootPixels;
                Require((foot - expectedFoot).Length <= .25, action + " keeps the same production contact anchor");
                captures.Add(new { action, file, frame = state.GetProperty("frame").GetInt32(), bitmap.Width, bitmap.Height,
                    visiblePixels, transparentPixels, footX = foot.X, footY = foot.Y });
                return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
            finally { renderer.SetPaused(false); }
        }

        CodexTaskInfo TaskInfo(string id, string status) => new(id, id, status, "codex", null, null, 0, false);
        CodexSnapshot Snapshot(params CodexTaskInfo[] tasks) => CodexSnapshot.Empty with { Connected = true, Tasks = tasks };
        var idle = Snapshot();
        var working = Snapshot(TaskInfo("animation-test-running", "active"));
        try
        {
            pet.SetMovement(false);
            pet.SetMotion(false);
            pet.SetCodexStatus(idle);
            pet.SelectSkin("gemini");
            await Wait(() => !pet.IsSceneLoading && pet.CurrentScenePack == null && pet.SceneRenderer == null, "Gemini default remained attached to an animation");
            Require(pet.CurrentSkin.Id == "gemini" && !pet.IsScenePlaying, "other skins retain their existing static default");
            pet.SetSceneAuto(false);
            pet.SelectSkin("deepseek");
            var firstIdle = await WaitState("idle");
            Require(pet.CurrentScenePack?.Id == BundledSceneAnimations.DeepSeekId && Renderer().Opacity == 1, "DeepSeek automatically loads and displays its built-in idle animation");
            Require(!pet.Settings.SceneAutoEnabled && !pet.Settings.MotionEnabled, "DeepSeek default states work with optional scene automation and breathing disabled");
            Point footAnchor = pet.VisibleFootPixels;
            await Capture("idle", footAnchor);
            int blankBefore = firstIdle.GetProperty("blankClears").GetInt32();

            pet.SetCodexStatus(working);
            await WaitState("working");
            await Capture("working", footAnchor);
            pet.SetSceneAuto(false);
            var active = await WaitState("working");
            string? request = active.GetProperty("requestId").GetString();
            int draws = active.GetProperty("draws").GetInt32();
            pet.SetCodexStatus(working); pet.SetCodexStatus(working);
            await Task.Delay(360);
            var repeat = await Read();
            Require(repeat.GetProperty("requestId").GetString() == request, "repeated Codex polls do not restart the working animation");
            Require(repeat.GetProperty("draws").GetInt32() > draws && pet.CurrentSceneAction == "working", "working frames advance even when breathing and optional scene automation are off");

            pet.SetCodexStatus(Snapshot(TaskInfo("animation-test-waiting", "waitingForInput"), TaskInfo("animation-test-running", "active")));
            await WaitState("working");
            Require((await Read()).GetProperty("requestId").GetString() == request, "a separate task needing attention does not override an active working state");
            pet.SetCodexStatus(idle);
            await WaitState("idle");
            pet.SetCodexStatus(working);
            await WaitState("working");
            pet.SetCodexStatus(working with { Connected = false });
            await WaitState("idle");
            Require(pet.CurrentSceneAction == "idle", "finished tasks and a disconnected snapshot both return to idle");

            for (int i = 0; i < 6; i++)
            {
                pet.SetCodexStatus(working);
                await Task.Delay(15);
                pet.SetCodexStatus(idle);
            }
            pet.SetCodexStatus(working);
            await WaitState("working");
            pet.SetCodexStatus(idle);
            var settled = await WaitState("idle");
            await Task.Delay(200);
            Require(pet.CurrentSceneAction == "idle" && (await Read()).GetProperty("displayedAction").GetString() == "idle", "rapid state changes settle on the newest requested state");
            automaticBlankClears = settled.GetProperty("blankClears").GetInt32() - blankBefore;
            preservedTransitions = settled.GetProperty("preservedTransitions").GetInt32();
            Require(automaticBlankClears == 0 && preservedTransitions > 0, "automatic state changes retain the previous frame without clearing the canvas");

            await pet.PreviewSceneAsync("walk-left");
            string leftHash = await Capture("walk-left", footAnchor);
            pet.StopScenePreview();
            await WaitState("idle");
            Require(pet.IsScenePlaying, "ending a manual preview restores animated idle");
            pet.SetCodexStatus(working);
            await WaitState("working");
            await pet.PreviewSceneAsync("walk-right");
            string rightHash = await Capture("walk-right", footAnchor);
            Require(leftHash != rightHash, "left and right previews render different direction-specific artwork");
            await WaitState("working", 6000);
            Require(pet.IsScenePlaying, "a finite preview finishes and restores the current working state");

            pet.ClearScenePack();
            await WaitState("working");
            Require(pet.UsesBuiltInScenes, "restoring the character default reloads the DeepSeek state animation");

            // Exercise cancellation during asynchronous WebView initialization.
            pet.SelectSkin("gemini");
            pet.SelectSkin("deepseek");
            pet.SelectSkin("gemini");
            await Task.Delay(400);
            Require(pet.CurrentSkin.Id == "gemini" && pet.CurrentScenePack == null && pet.SceneRenderer == null && !pet.IsSceneLoading,
                "a superseded DeepSeek load cannot attach itself to Gemini");
            pet.SelectSkin("deepseek");
            pet.SelectSkin("gemini");
            pet.SelectSkin("deepseek");
            pet.SetCodexStatus(idle);
            await WaitState("idle");
            Require(pet.CurrentSkin.Id == "deepseek" && pet.UsesBuiltInScenes, "rapid skin changes finish with the last selected skin and its default animation");

            // Schedule a direction on the production controller, then require its
            // ordinary physics tick to move the HWND and trigger the matching animation.
            if (SystemParameters.ClientAreaAnimation)
            {
                Rect own = NativeMethods.GetWindowBoundsPixels(hwnd), work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
                Point anchor = pet.VisibleFootPixels;
                NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(work.Left + (work.Width - own.Width) / 2), (int)Math.Round(work.Bottom - anchor.Y));
                movement.NotifyExternalMove();
                pet.SetMovement(true);
                await Wait(() => movement.Grounded, "test pet must land before walking");
                foreach (int direction in new[] { 1, -1 })
                {
                    double beforeX = NativeMethods.GetWindowBoundsPixels(hwnd).Left;
                    string expected = direction < 0 ? "walk-left" : "walk-right";
                    Require(movement.TryWalk(direction), "production controller accepts grounded " + expected);
                    await WaitState(expected, 5000);
                    await Task.Delay(250);
                    double deltaX = NativeMethods.GetWindowBoundsPixels(hwnd).Left - beforeX;
                    Require(direction * deltaX > 4 && movement.FacingDirection == direction,
                        expected + " animation follows actual HWND displacement and controller direction");
                    actualWalkChecks.Add(new { action = expected, deltaPixels = deltaX });
                    actualWalkObserved = true; observedWalkAction = expected;
                }
                Require(!pet.Settings.MotionEnabled && !pet.Settings.SceneAutoEnabled, "real desktop walking works with unrelated animation toggles disabled");
                pet.SetCodexStatus(working);
                await WaitState("working");
                double workingX = NativeMethods.GetWindowBoundsPixels(hwnd).Left;
                await Task.Delay(250);
                Require(movement.State != "walk" && !movement.TryWalk(1) &&
                    Math.Abs(NativeMethods.GetWindowBoundsPixels(hwnd).Left - workingX) < 1,
                    "starting a Codex task stops autonomous walking and plays working in place");
                pet.SetCodexStatus(idle);
                pet.SetMovement(false);
                await WaitState("idle");
            }

            return new { passed = true, assertions = checks.Count, checks, captures, automaticBlankClears, preservedTransitions,
                actualWalkObserved, observedWalkAction, actualWalkChecks,
                walkObservation = actualWalkObserved ? "Both directions moved the actual HWND and matched the rendered state." : "Windows reduced-motion setting disabled physical movement; direction policy was checked separately.",
                animationClockForcedForTest = forcedClock, output, syntheticMouseOrKeyboardInput = false, settingsPersisted = false };
        }
        finally
        {
            pet.SetMovement(false);
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(originalBounds.Left), (int)Math.Round(originalBounds.Top));
            movement.NotifyExternalMove();
        }
    }

    private static JsonElement Decode(string result)
    {
        string json = JsonSerializer.Deserialize<string>(result) ?? throw new InvalidOperationException("Scene diagnostics are empty.");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
