using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace DesktopPet;

internal static class DesktopMovementIntegrationChecks
{
    // Caller must supply the isolated, non-persisting --scene-test pet. No input
    // events are injected and SetWindowPos only ever receives that pet's HWND.
    public static async Task<object> RunAsync(PetWindow pet)
    {
        pet.Dispatcher.VerifyAccess();
        var hwnd = new WindowInteropHelper(pet).Handle;
        if (!pet.IsLoaded || hwnd == IntPtr.Zero || pet.MovementController == null)
            throw new InvalidOperationException("Movement integration needs a loaded test pet.");
        var checks = new List<string>();
        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Native movement check failed: " + description);
            checks.Add(description);
        }
        var initial = NativeMethods.GetWindowBoundsPixels(hwnd);
        double initialSize = pet.Settings.Size;
        bool initialMovement = pet.Settings.MovementEnabled, initialMotion = pet.Settings.MotionEnabled, initialVisible = pet.IsVisible;
        bool initialAutoScenes = pet.Settings.SceneAutoEnabled;
        var nativeSnapshot = DesktopWindowPlatforms.Capture(hwnd);
        DesktopMovementController? fixture = null;
        double actualFallPixels = 0, actualThrowPixels = 0, footErrorPixels = 0;
        try
        {
            pet.SetSceneAuto(false);
            pet.SetMovement(false);
            pet.SetMotion(true);
            if (!pet.IsVisible) pet.Show();
            pet.SetSize(160);
            pet.UpdateLayout();
            // DeepSeek now initializes an asynchronous default renderer. Let its
            // first frame and contact geometry settle before testing a manual pause.
            if (pet.CurrentSkin.Id == "deepseek" && pet.CurrentLive2DModel == null)
            {
                await WaitUntilAsync(() => pet.UsesBuiltInScenes && pet.IsScenePlaying && !pet.IsSceneLoading, 20000);
                await Task.Delay(250);
            }
            var own = NativeMethods.GetWindowBoundsPixels(hwnd);
            var work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
            Require(work.Width - own.Width >= 250 && work.Height - own.Height >= 180, "monitor has safe interior room for non-persisting test pet");
            int x = (int)Math.Round(work.Left + (work.Width - own.Width) * .32);
            int y = (int)Math.Round(work.Top + Math.Min(80, (work.Height - own.Height) * .2));
            void Place(int px, int py)
            {
                NativeMethods.SetWindowPositionPixels(hwnd, px, py);
                pet.MovementController!.NotifyExternalMove();
            }
            Place(x, y);
            pet.SetMovement(true);
            // The test is explicit and temporary even if desktop UI animations
            // are disabled; normal product operation continues to respect that.
            pet.MovementController!.SetPaused(false);
            pet.MovementController.SetAutonomous(false);
            var start = NativeMethods.GetWindowBoundsPixels(hwnd);
            await Task.Delay(250);
            var falling = NativeMethods.GetWindowBoundsPixels(hwnd);
            actualFallPixels = falling.Top - start.Top;
            Require(actualFallPixels > 4, "enabled 30Hz controller moves the real pet HWND downward");
            pet.MovementController.SetPaused(true);
            var paused = NativeMethods.GetWindowBoundsPixels(hwnd);
            await Task.Delay(200);
            Require(SamePosition(paused, NativeMethods.GetWindowBoundsPixels(hwnd)), "paused controller leaves native coordinates unchanged");
            pet.SetMovement(false);
            var disabled = NativeMethods.GetWindowBoundsPixels(hwnd);
            await Task.Delay(200);
            Require(!pet.MovementController.IsEnabled && SamePosition(disabled, NativeMethods.GetWindowBoundsPixels(hwnd)), "disable immediately stops timer motion without forcing a landing");

            Place(x, y);
            pet.SetMovement(true); pet.MovementController.SetPaused(false); pet.MovementController.SetAutonomous(false);
            pet.Hide();
            var hidden = NativeMethods.GetWindowBoundsPixels(hwnd);
            await Task.Delay(250);
            Require(!pet.IsVisible && SamePosition(hidden, NativeMethods.GetWindowBoundsPixels(hwnd)), "hidden pet does not continue simulating off-screen");
            pet.Show(); pet.MovementController.SetPaused(false);
            await Task.Delay(200);
            Require(NativeMethods.GetWindowBoundsPixels(hwnd).Top > hidden.Top + 2, "show resumes from current placement without hidden-time catch-up");
            pet.SetMovement(false);

            own = NativeMethods.GetWindowBoundsPixels(hwnd);
            Point Anchor() => new(own.Width / 2, own.Height - 36 * NativeMethods.GetDpiScale(hwnd));
            double platformY = work.Top + own.Height + Math.Min(160, (work.Height - own.Height) / 2);
            var platform = new DesktopPlatform(new IntPtr(-77), work.Left, work.Right, platformY,
                new Rect(work.Left, platformY, work.Width, Math.Max(1, work.Bottom - platformY)));
            // Same-process windows are intentionally excluded by production
            // enumeration. Inject a geometry-only carrier to get deterministic
            // native landing/follow/removal checks without any external window.
            var snapshot = new DesktopPlatformSnapshot(new[] { work }, new[] { platform });
            fixture = new DesktopMovementController(pet, Anchor, _ => snapshot);
            NativeMethods.SetWindowPositionPixels(hwnd, x, (int)Math.Round(platformY - Anchor().Y - 90));
            fixture.SetEnabled(true); fixture.SetAutonomous(false);
            await WaitUntilAsync(() => fixture.State == "idle" && Math.Abs(NativeMethods.GetWindowBoundsPixels(hwnd).Top + Anchor().Y - platformY) < 2, 2200);
            var landed = NativeMethods.GetWindowBoundsPixels(hwnd);
            footErrorPixels = Math.Abs(landed.Top + Anchor().Y - platformY);
            Require(footErrorPixels < 2, "real HWND settles with character-container foot on injected platform within 2 physical pixels");
            platform = new DesktopPlatform(platform.Handle, work.Left + 30, work.Right + 30, platformY - 20,
                new Rect(work.Left + 30, platformY - 20, work.Width, Math.Max(1, work.Bottom - platformY)));
            snapshot = new DesktopPlatformSnapshot(new[] { work }, new[] { platform });
            await Task.Delay(400);
            var followed = NativeMethods.GetWindowBoundsPixels(hwnd);
            Require(Math.Abs(followed.Left - landed.Left - 30) <= 2 && Math.Abs(followed.Top - landed.Top + 20) <= 2,
                "native position follows injected carrier translation on low-frequency scan");
            snapshot = new DesktopPlatformSnapshot(new[] { work }, Array.Empty<DesktopPlatform>());
            await Task.Delay(450);
            Require(NativeMethods.GetWindowBoundsPixels(hwnd).Top > followed.Top + 4, "removed carrier starts native falling motion");

            // Model the same BeginDrag/ObserveDrag/EndDrag path used by DragMove,
            // with only explicit moves of the test pet, not synthetic mouse input.
            fixture.SetPaused(true);
            NativeMethods.SetWindowPositionPixels(hwnd, x, y + 80);
            fixture.NotifyExternalMove(); fixture.BeginDrag();
            for (int i = 1; i <= 4; i++)
            {
                await Task.Delay(30);
                NativeMethods.SetWindowPositionPixels(hwnd, x + i * 20, y + 80 - i * 8);
                fixture.ObserveDrag();
            }
            fixture.SetPaused(false); fixture.EndDrag();
            var released = NativeMethods.GetWindowBoundsPixels(hwnd);
            await Task.Delay(160);
            actualThrowPixels = NativeMethods.GetWindowBoundsPixels(hwnd).Left - released.Left;
            Require(actualThrowPixels > 12, "timed native drag samples produce horizontal throw after release");
            Require(work.Contains(NativeMethods.GetWindowBoundsPixels(hwnd)), "native movement remains within physical monitor work area");

            return new
            {
                passed = true, count = checks.Count, checks, actualFallPixels, actualThrowPixels, footErrorPixels,
                readOnlyNativeScan = new { monitors = nativeSnapshot.WorkAreas.Count, exposedEdges = nativeSnapshot.Platforms.Count },
                externalWindowsMoved = 0, syntheticMouseOrKeyboardInput = false, persisted = false,
                landingCarrier = "injected geometry; only actual test pet HWND moved",
                realCrossProcessLandingNotExercised = true
            };
        }
        finally
        {
            fixture?.Dispose();
            pet.SetMovement(false);
            pet.SetSize(initialSize);
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(initial.Left), (int)Math.Round(initial.Top));
            pet.MovementController?.NotifyExternalMove();
            pet.SetMotion(initialMotion);
            if (initialVisible) pet.Show(); else pet.Hide();
            pet.SetSceneAuto(initialAutoScenes);
            pet.SetMovement(initialMovement);
        }
    }

    private static bool SamePosition(Rect a, Rect b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs) { if (condition()) return; await Task.Delay(30); }
    }
}
