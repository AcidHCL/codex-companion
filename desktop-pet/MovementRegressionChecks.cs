using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

// Explicit test mode only. The carrier is a separate process of this executable;
// no other application window is moved, activated, resized or closed.
internal static class MovementRegressionChecks
{
    private sealed record FixtureReport(int ProcessId, int ParentProcessId, long WindowHandle, string Kind);
    private sealed record DownwardSample(int Frame, double ElapsedMs, double IntervalMs, double CarrierTop,
        double PhysicsFootY, double NativeFootY, double GapPixels, string State, bool Grounded, long SupportHandle);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Main must dispatch this before the desktop-pet single-instance mutex.
    public static int RunFixture(string[] args)
    {
        int option = Array.IndexOf(args, "--movement-fixture");
        int parentOption = Array.IndexOf(args, "--fixture-parent");
        if (option < 0 || option + 1 >= args.Length || parentOption < 0 || parentOption + 1 >= args.Length ||
            !int.TryParse(args[parentOption + 1], out int parentId)) return 2;
        string reportPath = Path.GetFullPath(args[option + 1]);
        string allowed = Path.Combine(FindProjectRoot(), "diagnostics", "movement-regression") + Path.DirectorySeparatorChar;
        if (!reportPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using var parent = Process.GetProcessById(parentId);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            Title = "AI Pet · isolated movement regression carrier",
            Width = 560, Height = 150, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = true, ShowActivated = false,
            Topmost = true, Background = new SolidColorBrush(Color.FromRgb(34, 63, 85)),
            Content = new TextBlock
            {
                Text = "桌宠移动回归 · 独立测试承载窗口\n测试结束后自动关闭",
                Foreground = Brushes.White, FontSize = 16, Margin = new Thickness(24)
            }
        };
        var lifetime = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.SetNoActivate(hwnd);
            NativeMethods.SetToolWindow(hwnd, false);
        };
        window.Loaded += (_, _) =>
        {
            var report = new FixtureReport(Environment.ProcessId, parentId, new WindowInteropHelper(window).Handle.ToInt64(), "movement-carrier-v1");
            string temporary = reportPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, JsonOptions));
            File.Move(temporary, reportPath, true);
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            if (lifetime.Elapsed > TimeSpan.FromSeconds(90) || parent.HasExited || File.Exists(reportPath + ".close")) window.Close();
        };
        window.Closed += (_, _) => timer.Stop();
        return app.Run(window);
    }

    public static async Task<object> RunAsync(PetWindow pet)
    {
        pet.Dispatcher.VerifyAccess();
        var hwnd = new WindowInteropHelper(pet).Handle;
        var controller = pet.MovementController;
        if (!pet.IsLoaded || hwnd == IntPtr.Zero || controller == null)
            throw new InvalidOperationException("Movement regression requires a loaded non-persisting test pet.");
        if (controller.IsDragging) throw new InvalidOperationException("Do not run regression during an active drag.");
        string directory = Path.Combine(FindProjectRoot(), "diagnostics", "movement-regression", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string fixturePath = Path.Combine(directory, "fixture.json");
        var checks = new List<string>();
        var measurements = new Dictionary<string, object?>();
        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Movement regression failed: " + description);
            checks.Add(description);
        }
        var original = NativeMethods.GetWindowBoundsPixels(hwnd);
        double originalSize = pet.Settings.Size;
        bool originalMotion = pet.Settings.MotionEnabled, originalMovement = pet.Settings.MovementEnabled;
        bool originalSceneAuto = pet.Settings.SceneAutoEnabled, originalVisible = pet.IsVisible, originalTopmost = pet.Settings.Topmost;
        double? originalLeft = pet.Settings.Left, originalTop = pet.Settings.Top;
        string? originalMonitor = pet.Settings.DpiMonitor;
        Process? helper = null;
        IntPtr carrier = IntPtr.Zero;
        object? result = null;
        string? failure = null;
        try
        {
            Require(SystemParameters.ClientAreaAnimation, "system UI animations permit the production movement controller");
            pet.SetSceneAuto(false); pet.SetMovement(false); pet.SetMotion(false);
            pet.SetTopmost(true); if (!pet.IsVisible) pet.Show();
            pet.SetSize(160); pet.UpdateLayout();
            await Task.Delay(100);
            Rect work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
            Point localFoot = pet.VisibleFootPixels;
            Rect visible = pet.CharacterVisibleBoundsPixels;
            Require(work.Width >= 500 && work.Height >= localFoot.Y - visible.Top + 260,
                "work area has room for visible character and isolated carrier");
            int carrierWidth = (int)Math.Min(560, work.Width - 120);
            int carrierHeight = 140;
            int carrierX = (int)Math.Round(work.Left + (work.Width - carrierWidth) / 2);
            int carrierY = (int)Math.Round(Math.Clamp(work.Top + work.Height * .62,
                work.Top + localFoot.Y - visible.Top + 120, work.Bottom - carrierHeight - 30));
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path for regression fixture.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(MovementRegressionChecks).Assembly.Location);
            start.ArgumentList.Add("--movement-fixture"); start.ArgumentList.Add(fixturePath);
            start.ArgumentList.Add("--fixture-parent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            helper = Process.Start(start) ?? throw new InvalidOperationException("Unable to start regression fixture.");
            await WaitUntilAsync(() => File.Exists(fixturePath) || helper.HasExited, 12000);
            Require(File.Exists(fixturePath) && !helper.HasExited, "separate carrier process started and published its HWND");
            var fixture = JsonSerializer.Deserialize<FixtureReport>(File.ReadAllText(fixturePath))
                ?? throw new InvalidOperationException("Invalid fixture handshake.");
            carrier = new IntPtr(fixture.WindowHandle);
            GetWindowThreadProcessId(carrier, out uint ownerPid);
            Require(fixture.Kind == "movement-carrier-v1" && fixture.ProcessId == helper.Id && fixture.ParentProcessId == Environment.ProcessId &&
                ownerPid == (uint)helper.Id && ownerPid != (uint)Environment.ProcessId,
                "carrier HWND belongs to the exact child process, not a user application");
            void MoveCarrier(int x, int y)
            {
                // Revalidate ownership before every native mutation; HWNDs can be reused.
                GetWindowThreadProcessId(carrier, out uint currentPid);
                if (helper.HasExited || currentPid != (uint)helper.Id) throw new InvalidOperationException("Fixture HWND ownership changed.");
                if (!SetWindowPos(carrier, new IntPtr(-1), x, y, carrierWidth, carrierHeight, 0x0010 | 0x0040))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            MoveCarrier(carrierX, carrierY);
            await Task.Delay(120);
            DesktopPlatform? RealCarrier() => DesktopWindowPlatforms.Capture(hwnd).Platforms.FirstOrDefault(p => p.Handle == carrier);
            var edge = RealCarrier();
            Require(edge != null, "production DesktopWindowPlatforms.Capture detects the actual other-process window");
            double footX = (edge!.Left + edge.Right) / 2;
            Point NativeFoot()
            {
                Rect bounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                Point anchor = pet.VisibleFootPixels;
                return new Point(bounds.Left + anchor.X, bounds.Top + anchor.Y);
            }
            void PlaceFoot(double x, double y)
            {
                Point anchor = pet.VisibleFootPixels;
                NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(x - anchor.X), (int)Math.Round(y - anchor.Y));
                controller.NotifyExternalMove();
            }
            PlaceFoot(footX, edge.Top - 90);
            pet.SetMovement(true); controller.SetAutonomous(false);
            controller.ResetFrameStats();
            Point fallStart = NativeFoot();
            await Task.Delay(220);
            double fallDistance = NativeFoot().Y - fallStart.Y;
            measurements["fallWithMotionDisabledPixels"] = fallDistance;
            Require(!pet.Settings.MotionEnabled && pet.Settings.MovementEnabled && fallDistance > 5,
                "MotionEnabled=false with MovementEnabled=true still moves the actual HWND downward");
            await WaitUntilAsync(() => controller.Grounded && controller.SupportHandle == carrier, 2500);
            Require(controller.Grounded && controller.SupportHandle == carrier, "fall lands on the real cross-process carrier");

            // Exercise the actual release path, with stale samples cleared and a
            // 200 ms stationary hold: measured release speed is zero, no input injected.
            controller.BeginDrag();
            Point anchorAtRelease = pet.VisibleFootPixels;
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(footX - anchorAtRelease.X),
                (int)Math.Round(edge.Top + 3 - anchorAtRelease.Y));
            controller.ObserveDrag();
            await Task.Delay(200);
            controller.ObserveDrag();
            double releaseOvershoot = NativeFoot().Y - edge.Top;
            Require(releaseOvershoot >= 2 && releaseOvershoot <= 4, "slow release starts a few physical pixels below the true top edge");
            controller.EndDrag();
            await WaitUntilAsync(() => controller.SupportHandle == carrier && Math.Abs(NativeFoot().Y - edge.Top) <= 2, 1200);
            double snapError = Math.Abs(NativeFoot().Y - edge.Top);
            measurements["slowReleaseOvershootPixels"] = releaseOvershoot;
            measurements["visibleFootWindowTopErrorPixels"] = snapError;
            Require(controller.Grounded && controller.SupportHandle == carrier && snapError <= 2,
                "low-speed release snaps visible foot to native window top within 2 physical pixels");
            var stableStart = NativeFoot();
            var stability = Stopwatch.StartNew();
            double maxStableError = 0;
            while (stability.ElapsedMilliseconds < 2100)
            {
                await Task.Delay(30);
                maxStableError = Math.Max(maxStableError, (NativeFoot() - stableStart).Length);
                RequireSupport(controller, carrier);
            }
            measurements["stableDurationMs"] = stability.Elapsed.TotalMilliseconds;
            measurements["stableMaximumDriftPixels"] = maxStableError;
            Require(maxStableError <= 2, "real carrier support remains stable for at least 2 seconds");

            controller.ResetFrameStats();
            var beforeMove = NativeFoot();
            var previous = beforeMove;
            var carrierClock = Stopwatch.StartNew();
            var changedAt = new List<double>();
            double maxHorizontalLag = 0, maxVerticalError = 0;
            for (int i = 1; i <= 60; i++)
            {
                MoveCarrier(carrierX + i * 2, carrierY - i / 4);
                await Task.Delay(20);
                var point = NativeFoot();
                if ((point - previous).Length >= .5) changedAt.Add(carrierClock.Elapsed.TotalMilliseconds);
                previous = point;
                maxHorizontalLag = Math.Max(maxHorizontalLag, Math.Abs(point.X - beforeMove.X - i * 2));
                maxVerticalError = Math.Max(maxVerticalError, Math.Abs(point.Y - (edge.Top - i / 4)));
                RequireSupport(controller, carrier);
            }
            await Task.Delay(160);
            var finalEdge = RealCarrier() ?? throw new InvalidOperationException("Carrier became occluded during regression.");
            var afterMove = NativeFoot();
            double finalXError = Math.Abs(afterMove.X - beforeMove.X - 120);
            double finalYError = Math.Abs(afterMove.Y - finalEdge.Top);
            double[] observedIntervals = changedAt.Zip(changedAt.Skip(1), (a, b) => b - a).OrderBy(x => x).ToArray();
            var frameStats = controller.GetFrameStats();
            measurements["carrierFollowing"] = new
            {
                durationMs = carrierClock.Elapsed.TotalMilliseconds, commands = 60, actualChangedSamples = changedAt.Count,
                maxHorizontalLagPixels = maxHorizontalLag, maxVerticalErrorPixels = maxVerticalError,
                finalXErrorPixels = finalXError, finalYErrorPixels = finalYError,
                observedChangedPositionIntervalsMs = new
                {
                    count = observedIntervals.Length, mean = observedIntervals.Length == 0 ? 0 : observedIntervals.Average(),
                    p95 = Percentile95(observedIntervals), max = observedIntervals.Length == 0 ? 0 : observedIntervals[^1],
                    note = "20 ms delayed native-position samples; this is an observation cadence, not display FPS"
                },
                actualControllerFrameStatistics = frameStats
            };
            Require(frameStats.FrameCount >= 10 && frameStats.MovedFrames >= 10 && changedAt.Count >= 10,
                "multiple actual controller frames and native window movements were measured");
            Require(finalXError <= 2 && finalYError <= 2 && maxHorizontalLag <= 8 && maxVerticalError <= 4,
                "visible foot follows smooth real carrier motion with bounded lag and final error at most 2 pixels");

            // These observers subscribe after the production controller. Move the
            // owned carrier at the end of one Rendering callback and observe the
            // controller's response in the next distinct display frame. Delayed
            // Task continuations cannot skip the detachment/first-fall evidence.
            int downwardBaseX = carrierX + 120, downwardBaseY = carrierY - 15;
            async Task CheckDownwardCarrierAsync(string name, int initialDrop, int pixelsPerFrame, int commands)
            {
                MoveCarrier(downwardBaseX, downwardBaseY);
                await ObserveRenderingAsync(_ => controller.Grounded && controller.SupportHandle == carrier &&
                    Math.Abs(NativeFoot().Y - finalEdge.Top) <= 2, 1800);
                RequireSupport(controller, carrier);
                Point initialFoot = controller.FootScreen;
                Point initialNativeFoot = NativeFoot();
                double carrierTopOffset = finalEdge.Top - NativeMethods.GetWindowBoundsPixels(carrier).Top;
                int totalDrop = initialDrop + pixelsPerFrame * (commands - 1);
                Require(finalEdge.Top + totalDrop < work.Bottom - 12,
                    name + " downward carrier test has room above the actual floor");
                var samples = new List<DownwardSample>();
                // Retain partial observations even if the watchdog/conditions fail.
                measurements["downwardCarrier-" + name] = new { initialDrop, pixelsPerFrame, commands, samples };
                int sent = 0;
                double previousTime = 0, previousPhysicsY = initialFoot.Y;
                double? previousFreeFallSpeed = null;
                bool previousWasFreeFall = false, accelerationObserved = false;
                double firstSpeed = 0, maximumFreeFallSpeed = 0;
                controller.ResetFrameStats();
                await ObserveRenderingAsync(elapsed =>
                {
                    if (sent > 0)
                    {
                        Point physicsFoot = controller.FootScreen, nativeFoot = NativeFoot();
                        double actualTop = NativeMethods.GetWindowBoundsPixels(carrier).Top + carrierTopOffset;
                        double intervalMs = elapsed.TotalMilliseconds - previousTime;
                        var sample = new DownwardSample(samples.Count + 1, elapsed.TotalMilliseconds, intervalMs,
                            actualTop, physicsFoot.Y, nativeFoot.Y, actualTop - nativeFoot.Y,
                            controller.State, controller.Grounded, controller.SupportHandle.ToInt64());
                        samples.Add(sample);
                        bool freeFall = !sample.Grounded && sample.SupportHandle == 0 && sample.State == "fall";
                        // Match the production physics stall cap. Native HWND
                        // rounding is avoided here by measuring the physics foot.
                        double seconds = Math.Clamp(intervalMs / 1000, .001, .05);
                        double speed = (physicsFoot.Y - previousPhysicsY) / seconds;
                        if (freeFall)
                        {
                            if (samples.Count == 1) firstSpeed = speed;
                            maximumFreeFallSpeed = Math.Max(maximumFreeFallSpeed, speed);
                            if (previousWasFreeFall && previousFreeFallSpeed.HasValue && speed > previousFreeFallSpeed.Value + 5)
                                accelerationObserved = true;
                            previousFreeFallSpeed = speed;
                        }
                        else previousFreeFallSpeed = null;
                        previousWasFreeFall = freeFall;
                        previousPhysicsY = physicsFoot.Y;
                        if (sent == commands && sample.Grounded && controller.SupportHandle == carrier && Math.Abs(sample.GapPixels) <= 2)
                            return true;
                    }
                    if (sent < commands)
                    {
                        int drop = initialDrop + pixelsPerFrame * sent;
                        MoveCarrier(downwardBaseX, downwardBaseY + drop);
                        sent++;
                    }
                    previousTime = elapsed.TotalMilliseconds;
                    return false;
                }, 5000);
                var first = samples.FirstOrDefault() ?? throw new InvalidOperationException("No downward carrier render samples.");
                var last = samples[^1];
                double firstNativeDescent = first.NativeFootY - initialNativeFoot.Y;
                double firstPhysicsDescent = first.PhysicsFootY - initialFoot.Y;
                double landingError = Math.Abs(last.GapPixels);
                measurements["downwardCarrier-" + name] = new
                {
                    initialDropPixels = initialDrop, subsequentPixelsPerRenderFrame = pixelsPerFrame, commands, totalDropPixels = totalDrop,
                    firstFrameNativeDescentPixels = firstNativeDescent, firstFramePhysicsDescentPixels = firstPhysicsDescent,
                    firstFrameGapPixels = first.GapPixels, maximumGapPixels = samples.Max(s => s.GapPixels),
                    firstObservedFreeFallSpeedPixelsPerSecond = firstSpeed, maximumObservedFreeFallSpeedPixelsPerSecond = maximumFreeFallSpeed,
                    accelerationObserved, finalLandingErrorPixels = landingError, samples,
                    actualControllerFrameStatistics = controller.GetFrameStats(),
                    sampling = "CompositionTarget.Rendering after controller; initial 8/24 px separation then slow/fast steps, no Task.Delay sampling"
                };
                Require(!first.Grounded && first.SupportHandle == 0 && first.State == "fall",
                    name + " downward movement releases support and enters fall on the first observed display frame");
                Require(first.GapPixels > .5 && firstNativeDescent < initialDrop - .5 && firstPhysicsDescent >= 0,
                    name + " downward carrier leaves a real first-frame gap instead of teleporting the foot down with it");
                Require(accelerationObserved && maximumFreeFallSpeed > firstSpeed + 5,
                    name + " downward release naturally accelerates across consecutive falling display frames");
                Require(controller.Grounded && controller.SupportHandle == carrier && landingError <= 2,
                    name + " downward carrier stops and receives a natural landing within 2 physical pixels");
            }
            // An initial small separation leaves enough observable airborne time
            // even after the controller's maximum 50 ms physics step. Subsequent
            // steps distinguish slower dragging from fast downward dragging.
            await CheckDownwardCarrierAsync("slow", 8, 3, 16);
            await CheckDownwardCarrierAsync("fast", 24, 12, 8);

            // Start below all but the very lowest edges. No helper or user window
            // is hidden or moved out of the way to manufacture an empty desktop.
            controller.BeginDrag();
            Point floorAnchor = pet.VisibleFootPixels;
            var floorSnapshot = DesktopWindowPlatforms.Capture(hwnd);
            double floorX = Enumerable.Range(1, 9).Select(i => work.Left + work.Width * i / 10)
                .FirstOrDefault(x => x - floorAnchor.X + visible.Left >= work.Left && x - floorAnchor.X + visible.Right <= work.Right &&
                    !floorSnapshot.Platforms.Any(p => p.Contains(x) && p.Top >= work.Bottom - 22 && p.Top < work.Bottom - 1), double.NaN);
            Require(double.IsFinite(floorX), "a floor-test column has no intervening native top edge");
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(floorX - floorAnchor.X), (int)Math.Round(work.Bottom - 20 - floorAnchor.Y));
            controller.ObserveDrag(); await Task.Delay(200); controller.ObserveDrag(); controller.EndDrag();
            await WaitUntilAsync(() => controller.Grounded && controller.SupportHandle == IntPtr.Zero, 1800);
            double floorError = Math.Abs(NativeFoot().Y - work.Bottom);
            measurements["visibleFootWorkAreaBottomErrorPixels"] = floorError;
            Require(controller.Grounded && controller.SupportHandle == IntPtr.Zero && floorError <= 2,
                "visible foot rests on physical work-area bottom within 2 pixels, regardless of transparent HWND padding");
            result = new
            {
                passed = true, count = checks.Count, checks, measurements, fixtureProcessId = helper.Id,
                productionCrossProcessCapture = true, userApplicationWindowsMoved = 0,
                syntheticMouseOrKeyboardInput = false, persisted = false, diagnosticDirectory = directory,
                frameRateStatement = "Frame intervals are actual controller measurements; no theoretical 60 FPS claim is made."
            };
            return result;
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            try
            {
                if (controller.IsDragging) controller.EndDrag();
                pet.SetMovement(false); pet.SetSize(originalSize); pet.SetMotion(originalMotion); pet.SetTopmost(originalTopmost);
                if (originalVisible) pet.Show(); else pet.Hide();
                pet.SetSceneAuto(originalSceneAuto); pet.SetMovement(originalMovement);
                NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(original.Left), (int)Math.Round(original.Top));
                controller.NotifyExternalMove();
                pet.Settings.Left = originalLeft; pet.Settings.Top = originalTop; pet.Settings.DpiMonitor = originalMonitor;
            }
            finally
            {
                if (helper != null)
                {
                    try
                    {
                        File.WriteAllText(fixturePath + ".close", "close owned fixture");
                        await WaitUntilAsync(() => helper.HasExited, 1800);
                        if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); }
                    }
                    finally { helper.Dispose(); }
                }
                File.WriteAllText(Path.Combine(directory, "regression-result.json"),
                    JsonSerializer.Serialize(result ?? new { passed = false, error = failure, checks, measurements }, JsonOptions));
            }
        }
    }

    private static void RequireSupport(DesktopMovementController controller, IntPtr carrier)
    {
        if (!controller.Grounded || controller.SupportHandle != carrier)
            throw new InvalidOperationException("Movement regression failed: lost real carrier support.");
    }
    private static double Percentile95(double[] sorted) => sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95) - 1)];
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
    }
    private static async Task ObserveRenderingAsync(Func<TimeSpan, bool> observe, int timeoutMs)
    {
        var completion = new TaskCompletionSource<bool>();
        var clock = Stopwatch.StartNew();
        TimeSpan? previousRenderingTime = null;
        var watchdog = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
        EventHandler rendered = (_, args) =>
        {
            if (completion.Task.IsCompleted) return;
            if (args is RenderingEventArgs frame)
            {
                if (previousRenderingTime == frame.RenderingTime) return;
                previousRenderingTime = frame.RenderingTime;
            }
            try { if (observe(clock.Elapsed)) completion.TrySetResult(true); }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        EventHandler timedOut = (_, _) => completion.TrySetException(new TimeoutException("Timed out observing real movement display frames."));
        watchdog.Tick += timedOut;
        CompositionTarget.Rendering += rendered;
        watchdog.Start();
        try { await completion.Task; }
        finally
        {
            watchdog.Stop(); watchdog.Tick -= timedOut;
            CompositionTarget.Rendering -= rendered;
        }
    }
    private static string FindProjectRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "DesktopPet.csproj"))) return current.FullName;
        throw new InvalidOperationException("Movement fixture is only supported inside the source workspace build tree.");
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
