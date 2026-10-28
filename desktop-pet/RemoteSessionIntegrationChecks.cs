using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopPet;

// Explicit CLI regression only. Session and animation preferences are injected
// into the production host. It never changes real Windows session/preferences,
// directly unpauses a renderer/controller, or moves another application's HWND.
internal static class RemoteSessionIntegrationChecks
{
    internal static int Run()
    {
        string report = Path.Combine(AppContext.BaseDirectory, "remote-session-validation.json");
        string profile = Path.Combine(AppContext.BaseDirectory, "remote-session-test-evidence", Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        var evidence = new List<object>();
        var elapsed = Stopwatch.StartNew();
        int assertions = 0, result = 1, queries = 0, landings = 0;
        bool finished = false;
        string phase = "initializing";
        PetWindow? pet = null;
        WindowsSessionSnapshot session = new(42, true, true, false);
        bool animationsEnabled = false;
        WindowsSessionSnapshot actualSession = WindowsSessionState.QueryCurrent();
        bool actualWindowsAnimationsEnabled = SystemParameters.ClientAreaAnimation;
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(180) };

        void Check(bool condition, string description)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }

        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true;
            timeout.Stop();
            DesktopMovementFrameStats? movementFrameStats = pet?.MovementController?.FrameStats;
            try { pet?.Close(); }
            catch (Exception ex) { passed = false; error = "Cleanup: " + ex.GetType().Name + ": " + ex.Message; }
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions, checks, evidence, error, lastPhase = phase,
                elapsedMilliseconds = elapsed.ElapsedMilliseconds, sessionQueries = queries, landingEvents = landings,
                actualReadOnlyEnvironment = new { session = actualSession, windowsClientAreaAnimation = actualWindowsAnimationsEnabled },
                movementFrameStats,
                actualLocalSceneRenderer = true, actualOwnWindowPhysics = true,
                animationPreferenceInjected = true, injectedWindowsAnimationsEnabled = false,
                rendererOrControllerForceUnpaused = false, physicalMovementSkipped = false,
                nativeSessionNotificationsInjected = true, realSessionChanged = false, realWindowsPreferencesChanged = false,
                browserProfileIsolated = true, externalWindowsMoved = 0, syntheticMouseOrKeyboardInput = false,
                userSettingsWrites = 0, realAccountRequests = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }

        timeout.Tick += (_, _) => Finish(false, "REMOTE_SESSION_CHECK_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Finish(false, "Dispatcher: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
        };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                phase = "policy-and-isolated-production-host";
                Check(!new MotionPreferences(false, false).AllowsAnimation, "Local reduced-motion policy remains disabled");
                Check(new MotionPreferences(false, true).AllowsAnimation, "Active remote transport preference permits pet animation");
                Check(new MotionPreferences(true, false).AllowsAnimation, "Local Windows animation preference is respected");
                await CheckInitiallyLockedRemoteSessionAsync(app.Dispatcher, Check);
                Directory.CreateDirectory(profile);
                var settings = new DesktopSettings
                {
                    SkinId = "deepseek", Size = 160, Left = 160, Top = 80,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false,
                    Topmost = false, ClickThrough = true, MovementEnabled = false,
                    MotionEnabled = false, SceneAutoEnabled = false
                };
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false,
                    motionPreferences: () => new MotionPreferences(animationsEnabled, session.IsRemoteActive),
                    querySession: () => { queries++; return session; }, subscribeToSystemEvents: false,
                    movementPlatformProvider: hwnd => new DesktopPlatformSnapshot(
                        new[] { NativeMethods.GetWindowWorkAreaPixels(hwnd) }, Array.Empty<DesktopPlatform>()))
                {
                    RendererProfileDirectory = profile
                };
                pet.Show();
                await WaitForAsync(() => Task.FromResult(pet.IsLoaded && pet.DisplayLifecycle != null && pet.MovementController != null), "pet startup");
                var lifecycle = pet.DisplayLifecycle!;
                var movement = pet.MovementController!;
                movement.Landed += () => landings++;
                var hwnd = new WindowInteropHelper(pet).Handle;
                Check(hwnd != IntPtr.Zero && lifecycle.SessionId == 42 && lifecycle.IsRemoteSession,
                    "Production host starts with the injected current remote session identity");
                Check(!lifecycle.PowerNotificationsRegistered, "Isolated host does not register real display notifications");
                if (lifecycle.IsSuspended) await lifecycle.RestorePendingAsync();
                await WaitStateAsync(pet, "idle", 45000);
                await RequireFrameProgressAsync(pet, "Remote idle advances while Windows client-area animation is false", Check, evidence);
                Check(!settings.MotionEnabled && !settings.SceneAutoEnabled,
                    "Remote baseline animation does not require optional breathing or scene-automation settings");

                phase = "remote-physical-display-off";
                var originalRenderer = pet.SceneRenderer!;
                lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
                Check(!lifecycle.IsSuspended && ReferenceEquals(originalRenderer, pet.SceneRenderer),
                    "Turning off physical monitors does not detach an active remote renderer");
                await RequireFrameProgressAsync(pet, "Remote animation continues while physical monitors remain off", Check, evidence);

                phase = "remote-fall-and-land";
                Rect work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
                Rect own = NativeMethods.GetWindowBoundsPixels(hwnd);
                Point anchor = pet.VisibleFootPixels;
                Check(work.Height - anchor.Y >= 240 && work.Width - own.Width >= 180,
                    "Test work area has sufficient room for a real drop and both walking directions");
                int centerX = (int)Math.Round(work.Left + (work.Width - own.Width) / 2);
                int dropTop = (int)Math.Round(work.Bottom - anchor.Y - 220);
                NativeMethods.SetWindowPositionPixels(hwnd, centerX, dropTop);
                movement.NotifyExternalMove();
                pet.SetMovement(true);
                var fall = await WaitStateAsync(pet, "fall");
                Check(!movement.Grounded && movement.State == "fall", "Production host selects fall from actual downward physics");
                var land = await WaitStateAsync(pet, "land");
                double landedTop = NativeMethods.GetWindowBoundsPixels(hwnd).Top;
                Check(landedTop - dropTop >= 210 && movement.Grounded && landings == 1,
                    "Actual HWND drops over 210 pixels and emits exactly one landing event with system animation false");
                Check(land.GetProperty("draws").GetInt64() - fall.GetProperty("draws").GetInt64() >= 2,
                    "Fall artwork advances multiple frames during the physical drop");
                Check(Math.Abs(movement.FootScreen.Y - work.Bottom) <= .6,
                    "Actual remote-mode landing places the production foot anchor on the work-area floor");
                Check(!movement.TryWalk(1), "Landing one-shot blocks walking until the recovery pose completes");
                await WaitStateAsync(pet, "idle");
                Check((await SceneStateAsync(pet)).GetProperty("draws").GetInt64() - land.GetProperty("draws").GetInt64() >= 4,
                    "Landing artwork visibly completes before returning to remote idle");
                evidence.Add(new { phase, actualDropPixels = landedTop - dropTop, footErrorPixels = Math.Abs(movement.FootScreen.Y - work.Bottom), movementFrameStats = movement.FrameStats });

                phase = "remote-left-and-right-walking";
                foreach (int direction in new[] { 1, -1 })
                {
                    string action = direction < 0 ? "walk-left" : "walk-right";
                    double beforeX = NativeMethods.GetWindowBoundsPixels(hwnd).Left;
                    Check(movement.TryWalk(direction), "Production controller accepts " + action + " through the remote host gate");
                    await WaitStateAsync(pet, action);
                    await RequireFrameProgressAsync(pet, action + " advances actual frame indices while physical movement runs", Check, evidence);
                    double delta = NativeMethods.GetWindowBoundsPixels(hwnd).Left - beforeX;
                    Check(direction * delta > 8 && movement.FacingDirection == direction,
                        action + " moves the actual HWND in the matching direction");
                    evidence.Add(new { phase, action, deltaPixels = delta, movementFrameStats = movement.FrameStats });
                }
                pet.SetMovement(false);
                await WaitStateAsync(pet, "idle");

                phase = "local-reduced-motion-remains-respected";
                session = new WindowsSessionSnapshot(42, false, true, false);
                lifecycle.RefreshSessionState();
                lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
                if (lifecycle.IsSuspended) await lifecycle.RestorePendingAsync();
                pet.RefreshMotionPreferences();
                await WaitStateAsync(pet, "idle", 45000);
                await WaitForAsync(async () => (await SceneStateAsync(pet)).GetProperty("paused").GetBoolean(), "local reduced-motion pause");
                pet.SetMovement(true);
                await Task.Delay(120); // Let the changed host layout settle before observing a paused canvas.
                long pausedDraws = (await SceneStateAsync(pet)).GetProperty("draws").GetInt64();
                Rect pausedBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                long pausedTicks = movement.FrameStats.FrameCount;
                await Task.Delay(420);
                Check((await SceneStateAsync(pet)).GetProperty("draws").GetInt64() == pausedDraws,
                    "Local reduced-motion preference stops real scene frame updates");
                Check(!movement.TryWalk(1) && SamePosition(pausedBounds, NativeMethods.GetWindowBoundsPixels(hwnd)) && movement.FrameStats.FrameCount == pausedTicks,
                    "Local reduced-motion preference stops actual HWND movement and physics ticks");
                Check(!settings.MotionEnabled && settings.MovementEnabled && !settings.SceneAutoEnabled,
                    "Session transitions preserve independent user motion and movement preferences");

                phase = "return-to-remote-mode";
                pet.SetMovement(false); // Keep this idle clock observation independent of random autonomous walking.
                session = new WindowsSessionSnapshot(42, true, true, false);
                lifecycle.RefreshSessionState();
                if (lifecycle.IsSuspended) await lifecycle.RestorePendingAsync();
                pet.RefreshMotionPreferences();
                await WaitStateAsync(pet, "idle", 45000);
                await RequireFrameProgressAsync(pet, "Returning to remote mode resumes real scene frames through host policy", Check, evidence);
                pet.SetMovement(true);
                Check(movement.TryWalk(1), "Returning to active remote mode restores physical walking");
                await WaitStateAsync(pet, "walk-right");

                phase = "native-session-filter-and-lock";
                int queriesBefore = queries;
                var beforeForeignEvent = pet.SceneRenderer;
                lifecycle.HandleWindowMessage(0x02B1, new IntPtr(7), new IntPtr(43)); // Another session's lock.
                Check(!lifecycle.IsSuspended && ReferenceEquals(beforeForeignEvent, pet.SceneRenderer) && queries == queriesBefore,
                    "Native session lock notification for another session cannot pause this pet or query its state");
                session = new WindowsSessionSnapshot(42, true, true, true);
                lifecycle.HandleWindowMessage(0x02B1, new IntPtr(7), new IntPtr(42));
                pet.RefreshMotionPreferences();
                Check(lifecycle.IsSessionLocked && lifecycle.IsSuspended && pet.SceneRenderer == null && beforeForeignEvent!.Children.Count == 0,
                    "Native own-session lock releases the composition renderer");
                Rect lockedBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                long lockedTicks = movement.FrameStats.FrameCount;
                await Task.Delay(360);
                Check(SamePosition(lockedBounds, NativeMethods.GetWindowBoundsPixels(hwnd)) && movement.FrameStats.FrameCount == lockedTicks,
                    "Locked remote host stops HWND motion and physics work");
                lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
                lifecycle.HandleWindowMessage(0x02B1, new IntPtr(8), new IntPtr(43));
                Check(!await lifecycle.RestorePendingAsync() && lifecycle.IsSessionLocked,
                    "Physical display-on and another session's unlock cannot revive a locked remote host");
                session = new WindowsSessionSnapshot(42, true, true, false);
                lifecycle.HandleWindowMessage(0x02B1, new IntPtr(8), new IntPtr(42));
                Check(await lifecycle.RestorePendingAsync(), "Native own-session unlock allows renderer recovery");
                pet.RefreshMotionPreferences();
                Check(movement.TryWalk(1), "Remote unlock restores the production movement gate");
                await WaitStateAsync(pet, "walk-right", 45000);
                Check(!ReferenceEquals(beforeForeignEvent, pet.SceneRenderer), "Remote unlock creates a fresh composition renderer");
                await RequireFrameProgressAsync(pet, "Remote unlock resumes visible animation frames", Check, evidence);

                phase = "native-disconnect-and-reconnect";
                var beforeDisconnect = pet.SceneRenderer!;
                lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
                session = new WindowsSessionSnapshot(42, true, false, false);
                lifecycle.HandleWindowMessage(0x02B1, new IntPtr(4), new IntPtr(42));
                pet.RefreshMotionPreferences();
                Check(lifecycle.IsSessionDisconnected && lifecycle.IsSuspended && pet.SceneRenderer == null && beforeDisconnect.Children.Count == 0,
                    "Native remote disconnect releases the old renderer even with monitors off");
                Rect disconnectedBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                long disconnectedTicks = movement.FrameStats.FrameCount;
                await Task.Delay(360);
                Check(SamePosition(disconnectedBounds, NativeMethods.GetWindowBoundsPixels(hwnd)) && movement.FrameStats.FrameCount == disconnectedTicks,
                    "Disconnected remote host stops native movement and physics ticks");
                Check(!await lifecycle.RestorePendingAsync(), "Disconnected host does not rebuild in the background");
                session = new WindowsSessionSnapshot(42, true, true, false);
                lifecycle.HandleWindowMessage(0x02B1, new IntPtr(3), new IntPtr(42));
                Check(await lifecycle.RestorePendingAsync(), "Remote reconnect can recover while physical monitors stay off");
                pet.RefreshMotionPreferences();
                Check(movement.TryWalk(1), "Remote reconnect restores the production movement gate");
                await WaitStateAsync(pet, "walk-right", 45000);
                Check(!ReferenceEquals(beforeDisconnect, pet.SceneRenderer) && !lifecycle.IsSessionDisconnected,
                    "Remote reconnect replaces the composition renderer and clears the disconnect blocker");
                await RequireFrameProgressAsync(pet, "Reconnected remote renderer advances frames without forcing its pause state", Check, evidence);
                double reconnectX = NativeMethods.GetWindowBoundsPixels(hwnd).Left;
                Check(movement.TryWalk(-1), "Reconnected remote host accepts physical movement");
                await WaitStateAsync(pet, "walk-left");
                await Task.Delay(300);
                Check(NativeMethods.GetWindowBoundsPixels(hwnd).Left < reconnectX - 5,
                    "Reconnected remote host moves its actual HWND again");

                phase = "host-hide-close-and-independence";
                pet.Hide();
                Rect hiddenBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                long hiddenTicks = movement.FrameStats.FrameCount;
                await Task.Delay(360);
                Check(!pet.IsVisible && SamePosition(hiddenBounds, NativeMethods.GetWindowBoundsPixels(hwnd)) && movement.FrameStats.FrameCount == hiddenTicks,
                    "User-hidden remote pet remains hidden with no native movement work");
                Check(!settings.CodexEnabled && !settings.CodexMessagesEnabled && !settings.MotionEnabled && settings.MovementEnabled && !settings.SceneAutoEnabled,
                    "Remote compatibility leaves account, breathing, automation and movement settings unchanged");
                evidence.Add(new { phase, movementFrameStats = movement.FrameStats });
                pet.Close(); pet = null;
                Check(lifecycle.IsDisposed && !await lifecycle.RestorePendingAsync(), "Closing a remote pet cannot resurrect its renderer");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run();
        return result;
    }

    private static async Task RequireFrameProgressAsync(PetWindow pet, string description, Action<bool, string> check, List<object> evidence)
    {
        var before = await SceneStateAsync(pet);
        long initial = before.GetProperty("draws").GetInt64();
        string? request = before.GetProperty("requestId").GetString();
        int previousFrame = before.GetProperty("frame").GetInt32(), changedFrames = 0;
        var observedFrames = new List<int> { previousFrame };
        JsonElement after = before;
        bool sameRequest = true;
        // Bundled idle deliberately holds its first frame for 1.4 seconds.
        // Require real frame-index changes within a bounded source-aware wait,
        // rather than mistaking that authored hold for a stopped animation.
        var sampling = Stopwatch.StartNew();
        while (sampling.ElapsedMilliseconds < 4500)
        {
            await Task.Delay(55);
            after = await SceneStateAsync(pet);
            sameRequest &= after.GetProperty("requestId").GetString() == request;
            int currentFrame = after.GetProperty("frame").GetInt32();
            if (currentFrame != previousFrame) changedFrames++;
            previousFrame = currentFrame;
            observedFrames.Add(currentFrame);
            if (changedFrames >= 2 && sampling.ElapsedMilliseconds >= 350) break;
        }
        long delta = after.GetProperty("draws").GetInt64() - initial;
        evidence.Add(new { description, drawnFrames = delta, observedFrameChanges = changedFrames, observedFrames,
            requestUnchanged = sameRequest, paused = after.GetProperty("paused").GetBoolean(), samplingMilliseconds = sampling.ElapsedMilliseconds,
            before, after, action = after.GetProperty("displayedAction").GetString(),
            movementFrameStats = pet.MovementController?.FrameStats });
        check(!after.GetProperty("paused").GetBoolean() && sameRequest && delta >= 2 && changedFrames >= 2, description);
    }

    private static async Task CheckInitiallyLockedRemoteSessionAsync(Dispatcher dispatcher, Action<bool, string> check)
    {
        int suspended = 0, restored = 0, resumed = 0;
        WindowsSessionSnapshot initiallyLocked = new(84, true, true, true);
        using var lifecycle = new DisplayLifecycleController(dispatcher, IntPtr.Zero, () => suspended++,
            _ => { restored++; return Task.FromResult(true); }, () => resumed++, subscribeToSystemEvents: false,
            querySession: () => initiallyLocked);
        lifecycle.SetHostVisible(true);
        check(lifecycle.IsSessionLocked && lifecycle.IsSuspended && suspended == 1,
            "Starting inside an already-locked remote session immediately suspends the host");
        check(!await lifecycle.RestorePendingAsync() && restored == 0,
            "Initial remote connection does not permit rebuilding while its session is locked");
        lifecycle.HandleWindowMessage(0x02B1, new IntPtr(3), new IntPtr(84));
        check(lifecycle.IsSessionLocked && !await lifecycle.RestorePendingAsync() && restored == 0,
            "A remote-connect notification cannot clear an authoritative existing session lock");
        initiallyLocked = new WindowsSessionSnapshot(84, true, true, false);
        lifecycle.HandleWindowMessage(0x02B1, new IntPtr(8), new IntPtr(84));
        check(await lifecycle.RestorePendingAsync() && restored == 1 && resumed == 1,
            "The matching session's unlock releases initial remote lock suspension exactly once");
    }

    private static async Task<JsonElement> WaitStateAsync(PetWindow pet, string action, int timeoutMilliseconds = 6000)
    {
        JsonElement state = default;
        await WaitForAsync(async () =>
        {
            if (pet.SceneRenderer == null || pet.IsSceneLoading || !pet.IsScenePlaying || pet.CurrentSceneAction != action) return false;
            state = await SceneStateAsync(pet);
            return state.GetProperty("playing").GetBoolean() && state.GetProperty("hasFrame").GetBoolean() &&
                state.GetProperty("displayedAction").GetString() == action;
        }, "scene " + action + "; host=" + pet.SceneStatus, timeoutMilliseconds);
        return state;
    }

    private static async Task<JsonElement> SceneStateAsync(PetWindow pet)
    {
        var scene = pet.SceneRenderer ?? throw new InvalidOperationException("The production host has no attached scene renderer.");
        using var outer = JsonDocument.Parse(await scene.GetDiagnosticsAsync());
        using var inner = JsonDocument.Parse(outer.RootElement.GetString()!);
        return inner.RootElement.Clone();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string expected, int timeoutMilliseconds = 10000)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (await condition()) return;
            await Task.Delay(15);
        }
        throw new TimeoutException("Remote production host did not reach " + expected + ".");
    }

    private static bool SamePosition(Rect a, Rect b) => Math.Abs(a.Left - b.Left) < .5 && Math.Abs(a.Top - b.Top) < .5;
}
