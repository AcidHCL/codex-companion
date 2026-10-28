using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace DesktopPet;

// Explicit CLI regression only: injected lifecycle signals, one isolated local
// renderer, no monitor/session changes, account requests or saved preferences.
internal static class DisplayRecoveryIntegrationChecks
{
    internal static int? HandleProbe(string[] args)
    {
        int parentOption = Array.IndexOf(args, "--graphics-recovery-parent-probe");
        int childOption = Array.IndexOf(args, "--graphics-recovery-child-probe");
        if (parentOption < 0 && childOption < 0) return null;
        int option = parentOption >= 0 ? parentOption : childOption;
        if (option + 1 >= args.Length || !Guid.TryParseExact(args[option + 1], "N", out _)) return 2;
        string probe = args[option + 1];
        try
        {
            if (parentOption >= 0)
            {
                if (parentOption + 2 >= args.Length || !int.TryParse(args[parentOption + 2], out int count) || count is < 1 or > 2) return 2;
                // This disposable mutex belongs only to the probe. It has no
                // relationship to the running user's desktop-pet singleton.
                using var guard = new Mutex(true, @"Local\AIPet.DisplayRecoveryProbe." + probe, out bool ownsProbe);
                if (!ownsProbe) return 2;
                var start = GraphicsRecovery.CreateRestartInfo(Environment.ProcessPath!, new GraphicsRecoveryAttempt(DateTimeOffset.UtcNow, count), Environment.ProcessId);
                start.ArgumentList.Add("--graphics-recovery-child-probe");
                start.ArgumentList.Add(probe);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Probe child did not start.");
                WriteProbe("graphics-recovery-probe-parent.json", new { probe, passed = true, childStarted = true });
                guard.ReleaseMutex();
                return 0;
            }

            int recoveryOption = Array.IndexOf(args, "--graphics-recovery");
            int parentId = int.Parse(args[recoveryOption + 3]);
            int countExpected = int.Parse(args[recoveryOption + 1]);
            bool parentExited;
            try { using var parent = Process.GetProcessById(parentId); parentExited = parent.HasExited; }
            catch (ArgumentException) { parentExited = true; }
            bool inheritedCodexContext = new[] { "CODEX_THREAD_ID", "CODEX_TURN_ID", "CODEX_APP_TOOLS_PIPE_PATH" }
                .Any(key => Environment.GetEnvironmentVariable(key) != null);
            bool probeMutexReleased;
            using (var guard = new Mutex(false, @"Local\AIPet.DisplayRecoveryProbe." + probe))
            {
                try { probeMutexReleased = guard.WaitOne(0); }
                catch (AbandonedMutexException) { probeMutexReleased = true; }
                if (probeMutexReleased) guard.ReleaseMutex();
            }
            bool passed = GraphicsRecovery.IsRecoveryLaunch && GraphicsRecovery.CompatibilityMode == (countExpected == 2)
                && parentExited && probeMutexReleased && !inheritedCodexContext;
            WriteProbe("graphics-recovery-probe-child.json", new
            {
                probe, passed, parentExited, probeMutexReleased, inheritedCodexContext,
                recoveryLaunch = GraphicsRecovery.IsRecoveryLaunch, compatibilityMode = GraphicsRecovery.CompatibilityMode
            });
            return passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteProbe(parentOption >= 0 ? "graphics-recovery-probe-parent.json" : "graphics-recovery-probe-child.json",
                new { probe, passed = false, error = ex.GetType().Name + ": " + ex.Message });
            return 1;
        }
    }

    private static void WriteProbe(string name, object value)
    {
        string target = Path.Combine(AppContext.BaseDirectory, name);
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, target, overwrite: true);
    }

    internal static int Run()
    {
        string report = Path.Combine(AppContext.BaseDirectory, "display-recovery-validation.json");
        string profile = Path.Combine(AppContext.BaseDirectory, "display-recovery-test-evidence", Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        var elapsed = Stopwatch.StartNew();
        int assertions = 0, result = 1;
        bool finished = false;
        string phase = "initializing";
        Window? fixture = null;
        PetWindow? pet = null;
        SceneAnimationView? scene = null;
        Live2DView? live = null;
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(150) };

        void Check(bool condition, string description)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }

        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try { pet?.Close(); scene?.Dispose(); live?.Dispose(); fixture?.Close(); }
            catch (Exception ex) { passed = false; error = "Cleanup: " + ex.GetType().Name + ": " + ex.Message; }
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions, checks, error, lastPhase = phase,
                elapsedMilliseconds = elapsed.ElapsedMilliseconds,
                actualLocalSceneRenderer = true, injectedProcessFailureClassification = true,
                realMonitorsTurnedOff = false, changedExistingUserWindows = false,
                productionProcessRestarts = 0, isolatedProbeProcesses = 4, realAccountRequests = 0, userSettingsWrites = 0,
                browserProfileIsolated = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }

        timeout.Tick += (_, _) => Finish(false, "DISPLAY_RECOVERY_CHECK_TIMEOUT");
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
                phase = "failure-classification-and-restart-budget";
                var fatal = new COMException("Injected render partition failure", GraphicsFailure.RenderThreadFailure);
                var ordinary = new COMException("Injected browser disconnection", unchecked((int)0x80010108));
                Check(GraphicsFailure.IsFatal(fatal), "WPF render-thread HRESULT is fatal");
                Check(GraphicsFailure.IsFatal(new InvalidOperationException("Wrapped", fatal)), "Wrapped WPF fatal is detected");
                Check(GraphicsFailure.IsFatal(new AggregateException(ordinary, fatal)), "Aggregate WPF fatal is detected");
                Check(!GraphicsFailure.IsFatal(ordinary), "Ordinary browser COM errors are not WPF fatal");
                Check(!GraphicsFailure.IsFatal(new InvalidOperationException("Ordinary")), "Ordinary invalid operation is not WPF fatal");
                Check(!WebViewFailurePolicy.IsUnavailable(fatal), "Renderer catch policy propagates WPF fatal");
                Check(WebViewFailurePolicy.IsUnavailable(ordinary), "Renderer catch policy handles disconnected browser COM");
                foreach (var kind in new[] { CoreWebView2ProcessFailedKind.GpuProcessExited, CoreWebView2ProcessFailedKind.UtilityProcessExited, CoreWebView2ProcessFailedKind.RenderProcessUnresponsive })
                    Check(!WebViewFailurePolicy.RequiresRecreation(kind), "Auto-recoverable or transient WebView event: " + kind);
                foreach (var kind in new[] { CoreWebView2ProcessFailedKind.BrowserProcessExited, CoreWebView2ProcessFailedKind.RenderProcessExited, CoreWebView2ProcessFailedKind.FrameRenderProcessExited })
                    Check(WebViewFailurePolicy.RequiresRecreation(kind), "Terminal WebView exit: " + kind);

                var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
                var first = new GraphicsRecoveryAttempt(now, 0).Next(now);
                Check(first.HasValue && first.Value.Count == 1, "First recovery attempt is allowed");
                var second = first!.Value.Next(now.AddSeconds(1));
                Check(second.HasValue && second.Value.Count == 2, "Second recovery attempt is allowed");
                Check(second!.Value.Next(now.AddMinutes(9)) == null, "Third recovery is blocked within ten minutes");
                var cooled = second.Value.Next(now.AddMinutes(10));
                Check(cooled.HasValue && cooled.Value.Count == 1, "Ten-minute cooldown starts one fresh recovery budget");
                var rewound = first.Value.Next(now.AddHours(-1));
                Check(rewound.HasValue && rewound.Value.Count == 2, "Clock rollback consumes the existing budget");
                Check(rewound!.Value.Next(now.AddHours(-2)) == null, "Repeated clock rollback cannot replenish recovery budget");

                var keys = new[] { "CODEX_THREAD_ID", "CODEX_TURN_ID", "CODEX_APP_TOOLS_PIPE_PATH" };
                var previous = new Dictionary<string, string?>();
                try
                {
                    foreach (string key in keys) { previous[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, "display-test-placeholder"); }
                    var restart = GraphicsRecovery.CreateRestartInfo(Environment.ProcessPath!, first.Value, Environment.ProcessId);
                    Check(string.Equals(restart.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase), "Restart target is the current desktop pet executable");
                    Check(!restart.UseShellExecute && restart.CreateNoWindow && restart.WindowStyle == ProcessWindowStyle.Hidden, "Recovery child launch is hidden and does not use a shell");
                    Check(restart.ArgumentList.Count == 4 && restart.ArgumentList[0] == "--graphics-recovery", "Recovery arguments carry only bounded attempt metadata");
                    foreach (string key in keys) Check(!restart.Environment.ContainsKey(key), "Recovery does not inherit transient context: " + key);
                    bool rejected = false;
                    try { GraphicsRecovery.CreateRestartInfo(Path.Combine(AppContext.BaseDirectory, "Other.exe"), first.Value, Environment.ProcessId); }
                    catch (ArgumentException) { rejected = true; }
                    Check(rejected, "Recovery refuses a different executable");
                }
                finally { foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value); }
                Check(GraphicsRecovery.TryBeginRecovery(), "First fatal handler owns process recovery");
                Check(!GraphicsRecovery.TryBeginRecovery(), "Duplicate fatal handler cannot launch another recovery");

                phase = "actual-process-handoff";
                await CheckHandoffAsync(1, Check);
                await CheckHandoffAsync(2, Check);

                phase = "display-lifecycle";
                await CheckLifecycleAsync(app.Dispatcher, Check);

                phase = "real-scene-renderer";
                Directory.CreateDirectory(profile);
                var pack = new SceneAnimationCatalog().Validate(Path.Combine(AppContext.BaseDirectory, "assets", "scenes", "deepseek-small", "deepseek-small.petanim.json"), "display-recovery-scene");
                scene = new SceneAnimationView(pack, profile);
                fixture = new Window
                {
                    Title = "AI Pet display recovery regression fixture", Width = 180, Height = 220,
                    ShowInTaskbar = false, ShowActivated = false, Content = scene,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual
                };
                fixture.Show();
                await scene.LoadAsync();
                await scene.PlayAsync("working", true);
                Check(scene.IsPlaying && !scene.IsFailed, "Actual bundled animation loads in an isolated WebView profile");
                foreach (var kind in new[] { CoreWebView2ProcessFailedKind.GpuProcessExited, CoreWebView2ProcessFailedKind.UtilityProcessExited, CoreWebView2ProcessFailedKind.RenderProcessUnresponsive })
                {
                    scene.HandleProcessFailure(kind);
                    Check(!scene.IsFailed && scene.IsPlaying, "Scene survives transient process event: " + kind);
                }
                scene.SetPaused(true);
                await WaitForAsync(async () => (await SceneStateAsync(scene)).GetProperty("paused").GetBoolean());
                long draws = (await SceneStateAsync(scene)).GetProperty("draws").GetInt64();
                await Task.Delay(180);
                Check((await SceneStateAsync(scene)).GetProperty("draws").GetInt64() == draws, "Paused actual scene stops drawing");
                scene.SetPaused(false);
                await WaitForAsync(async () => (await SceneStateAsync(scene)).GetProperty("draws").GetInt64() > draws);
                Check(true, "Actual scene resumes after pause");

                int sceneNotices = 0;
                scene.Failed += _ => sceneNotices++;
                Task pending = scene.SwitchAsync("walk-right", true);
                Check(!pending.IsCompleted, "Failure injection covers a pending first-frame wait");
                scene.HandleProcessFailure(CoreWebView2ProcessFailedKind.RenderProcessExited);
                Check(scene.IsFailed && !scene.IsPlaying, "Scene fatal exit immediately disables playing state");
                Check(sceneNotices == 0, "Scene failure notification is deferred outside native event stack");
                Check(await FailsAsync(pending), "Scene fatal exit completes the pending first-frame wait");
                scene.SetPaused(true); scene.SetPaused(false);
                scene.HandleProcessFailure(CoreWebView2ProcessFailedKind.BrowserProcessExited);
                await Task.Delay(80);
                Check(sceneNotices == 1, "Repeated scene faults and pause changes notify the host exactly once");
                Check(await FailsAsync(scene.GetDiagnosticsAsync()), "Failed scene rejects diagnostics before touching a dead Core");
                scene.Dispose(); scene.Dispose();
                Check(scene.Children.Count == 0, "Scene disposal is idempotent and releases the child renderer");
                fixture.Content = null;

                phase = "live2d-terminal-state";
                var model = new Live2DModelCatalog().Validate(Path.Combine(AppContext.BaseDirectory, "assets", "live2d-samples", "Hiyori", "Hiyori.model3.json"), "display-recovery-live2d");
                live = new Live2DView(model, profile);
                int liveNotices = 0;
                live.Failed += _ => liveNotices++;
                live.HandleProcessFailure(CoreWebView2ProcessFailedKind.GpuProcessExited);
                live.HandleProcessFailure(CoreWebView2ProcessFailedKind.UtilityProcessExited);
                Check(!live.IsFailed, "Live2D does not terminate for GPU or utility self-recovery");
                live.HandleProcessFailure(CoreWebView2ProcessFailedKind.FrameRenderProcessExited);
                Check(live.IsFailed && !live.ModelLoaded && liveNotices == 0, "Live2D terminal state is immediate but notification is deferred");
                Check(CompletionState(live, "_ready") && CompletionState(live, "_loaded"), "Live2D terminal exit completes both initialization waiters");
                live.SetPaused(true); live.SetMotionEnabled(false); live.SetPaused(false);
                live.HandleProcessFailure(CoreWebView2ProcessFailedKind.BrowserProcessExited);
                await Task.Delay(80);
                Check(liveNotices == 1, "Live2D repeated terminal events notify exactly once");
                Check(await FailsAsync(live.LoadAsync(model)), "Failed Live2D rejects reload without initializing a dead control");
                live.Dispose(); live.Dispose();
                Check(live.Children.Count == 0, "Live2D disposal is idempotent");

                phase = "real-pet-host-recovery";
                var settings = new DesktopSettings
                {
                    SkinId = "deepseek", Size = 160, Left = 240, Top = 60,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false,
                    Topmost = false, ClickThrough = true, MovementEnabled = false,
                    MotionEnabled = true, SceneAutoEnabled = true
                };
                // This suite specifically exercises local monitor-off behavior;
                // remote display behavior has its own host-level regression.
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false,
                    motionPreferences: () => new MotionPreferences(true, false),
                    querySession: () => new WindowsSessionSnapshot(WindowsSessionState.CurrentSessionId, false, true, false),
                    subscribeToSystemEvents: false)
                {
                    RendererProfileDirectory = profile
                };
                pet.Show();
                await WaitForAsync(() => Task.FromResult(pet.IsLoaded && pet.DisplayLifecycle != null));
                var hostLifecycle = pet.DisplayLifecycle!;
                if (hostLifecycle.IsSuspended) await hostLifecycle.RestorePendingAsync();
                await WaitForAsync(() => Task.FromResult(pet.SceneRenderer?.IsPlaying == true && pet.CurrentSceneAction == "idle"), 45000);
                Check(pet.UsesBuiltInScenes && pet.CurrentScenePack?.SkinId == "deepseek", "Actual pet restores its bundled DeepSeek idle animation on startup");
                string? chosenPack = settings.ScenePackId;
                var original = pet.SceneRenderer!;
                hostLifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
                Check(pet.SceneRenderer == null && original.Children.Count == 0 && hostLifecycle.IsSuspended, "Actual screen-off path detaches and disposes the pet composition renderer");
                Check(settings.ScenePackId == chosenPack && settings.MotionEnabled && settings.SceneAutoEnabled && !settings.MovementEnabled,
                    "Screen-off preserves scene selection and motion preferences");
                hostLifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
                Check(await hostLifecycle.RestorePendingAsync(), "Actual pet rebuild completes after simulated screen-on");
                await WaitForAsync(() => Task.FromResult(pet.SceneRenderer?.IsPlaying == true && pet.CurrentSceneAction == "idle"), 45000);
                Check(!ReferenceEquals(original, pet.SceneRenderer) && pet.CurrentScenePack?.Id == chosenPack,
                    "Pet screen-on creates a fresh renderer for the same animation pack");
                Check(settings.SkinId == "deepseek" && settings.MotionEnabled && settings.SceneAutoEnabled && !settings.MovementEnabled,
                    "Pet screen-on retains the selected character and activity preferences");

                hostLifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
                pet.SelectSkin("gemini");
                Check(pet.CurrentSkin.Id == "gemini" && pet.SceneRenderer == null, "Changing character while displays are off remains detached");
                hostLifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
                Check(await hostLifecycle.RestorePendingAsync(), "Pet can recover a character changed during display suspension");
                Check(pet.CurrentSkin.Id == "gemini" && pet.CurrentScenePack == null && pet.SceneRenderer == null && settings.ScenePackId == null,
                    "A changed character does not inherit the previous DeepSeek animation pack");
                hostLifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayChanged);
                Check(hostLifecycle.RecoveryScheduled, "Host close test starts with a queued display recovery");
                pet.Close(); pet = null;
                Check(hostLifecycle.IsDisposed && !hostLifecycle.RecoveryScheduled && !await hostLifecycle.RestorePendingAsync(),
                    "Closing the real pet cancels its queued recovery and prevents renderer resurrection");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run();
        return result;
    }

    private static async Task CheckLifecycleAsync(Dispatcher dispatcher, Action<bool, string> check)
    {
        int suspends = 0, restores = 0, resumed = 0;
        using var lifecycle = new DisplayLifecycleController(dispatcher, IntPtr.Zero, () => suspends++,
            _ => { restores++; return Task.FromResult(true); }, () => resumed++, subscribeToSystemEvents: false);
        lifecycle.SetHostVisible(true);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
        check(lifecycle.IsSuspended && !lifecycle.RecoveryScheduled && suspends == 1, "Repeated screen-off signals suspend once without restoring");
        check(!await lifecycle.RestorePendingAsync() && restores == 0, "Screen-off blocks renderer rebuilding");
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
        check(lifecycle.RecoveryScheduled, "Screen-on schedules a settled recovery");
        check(await lifecycle.RestorePendingAsync() && !lifecycle.IsSuspended && resumed == 1, "Screen-on restores once and resumes activity");

        lifecycle.SimulateSignal(DisplayLifecycleSignal.SessionLocked);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.SystemSuspend);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.SystemResume);
        check(!await lifecycle.RestorePendingAsync() && restores == 1, "Resume and screen-on cannot bypass the still-locked session");
        lifecycle.SimulateSignal(DisplayLifecycleSignal.SessionUnlocked);
        check(await lifecycle.RestorePendingAsync() && resumed == 2, "Unlock releases the remaining recovery blocker");
        lifecycle.SimulateSignal(DisplayLifecycleSignal.SessionDisconnected);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayChanged);
        check(!await lifecycle.RestorePendingAsync(), "Display topology changes cannot rebuild a disconnected session");
        lifecycle.SimulateSignal(DisplayLifecycleSignal.SessionConnected);
        check(await lifecycle.RestorePendingAsync(), "Reconnection permits a pending recovery");

        int before = restores;
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayChanged);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayChanged);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayChanged);
        check(await lifecycle.RestorePendingAsync() && restores == before + 1, "Burst display changes coalesce into one renderer rebuild");
        check(!await lifecycle.RestorePendingAsync() && restores == before + 1, "A settled display cycle is not rebuilt again");
        lifecycle.SetHostVisible(false);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
        lifecycle.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
        check(!await lifecycle.RestorePendingAsync(), "User-hidden host does not rebuild or reveal itself on wake");
        lifecycle.SetHostVisible(true);
        check(await lifecycle.RestorePendingAsync(), "Showing the host resumes its pending display recovery");

        int failedCalls = 0;
        DisplayLifecycleController? failed = null;
        failed = new DisplayLifecycleController(dispatcher, IntPtr.Zero, () => { }, _ =>
        {
            failedCalls++; failed!.RequestRecovery(); return Task.FromResult(false);
        }, () => throw new InvalidOperationException("Failed restore must not resume"), false);
        using (failed)
        {
            failed.SetHostVisible(true); failed.RequestRecovery();
            for (int i = 0; i < 3; i++) check(!await failed.RestorePendingAsync(), "Failed rebuild remains paused: attempt " + (i + 1));
            check(failedCalls == 3 && failed.RetryExhausted && !failed.RecoveryScheduled, "Renderer recovery stops after three failed attempts");
            failed.RequestRecovery(); failed.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
            check(!await failed.RestorePendingAsync() && failedCalls == 3, "Failure callbacks and redundant wake signals cannot reset exhausted retries");
        }

        int shortLivedRebuilds = 0;
        using var rolling = new DisplayLifecycleController(dispatcher, IntPtr.Zero, () => { },
            _ => { shortLivedRebuilds++; return Task.FromResult(true); }, () => { }, false);
        rolling.SetHostVisible(true);
        for (int i = 0; i < 3; i++)
        {
            rolling.RequestRecovery(rendererFailure: true);
            check(await rolling.RestorePendingAsync(), "Short-lived renderer replacement may recover: cycle " + (i + 1));
        }
        rolling.RequestRecovery(rendererFailure: true);
        check(rolling.RendererRetryExhausted && rolling.RetryExhausted && !rolling.RecoveryScheduled,
            "Repeated post-load renderer crashes exhaust one rolling recovery budget");
        rolling.RequestRecovery(rendererFailure: true);
        rolling.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
        check(!await rolling.RestorePendingAsync() && shortLivedRebuilds == 3,
            "Further failure callbacks or redundant display-on cannot bypass the rolling limit");
        rolling.RequestRecovery(userInitiated: true);
        check(await rolling.RestorePendingAsync() && !rolling.RendererRetryExhausted,
            "Explicit user retry can restart an exhausted renderer cycle");
        rolling.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
        rolling.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
        check(await rolling.RestorePendingAsync() && !rolling.RendererRetryExhausted,
            "A genuine new display transition can recover after an earlier crash streak");

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        int generations = 0, staleResumes = 0;
        using var interrupted = new DisplayLifecycleController(dispatcher, IntPtr.Zero, () => { }, token =>
        {
            observed = token; return ++generations == 1 ? gate.Task : Task.FromResult(true);
        }, () => staleResumes++, false);
        interrupted.SetHostVisible(true); interrupted.RequestRecovery();
        Task<bool> inFlight = interrupted.RestorePendingAsync();
        check(interrupted.IsRestoring, "In-flight recovery is tracked");
        interrupted.SimulateSignal(DisplayLifecycleSignal.DisplayOff);
        check(observed.IsCancellationRequested, "A new screen-off cancels the old renderer generation");
        gate.SetResult(true);
        check(!await inFlight && interrupted.IsSuspended && staleResumes == 0, "Late successful rebuild cannot resume an obsolete screen generation");
        interrupted.SimulateSignal(DisplayLifecycleSignal.DisplayOn);
        check(await interrupted.RestorePendingAsync() && staleResumes == 1, "Newest screen generation can recover normally");

        var disposalGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposedResumes = 0;
        CancellationToken disposalToken = default;
        var disposed = new DisplayLifecycleController(dispatcher, IntPtr.Zero, () => { }, token =>
        {
            disposalToken = token; return disposalGate.Task;
        }, () => disposedResumes++, false);
        disposed.SetHostVisible(true); disposed.RequestRecovery();
        Task<bool> disposing = disposed.RestorePendingAsync();
        disposed.Dispose(); disposed.Dispose();
        check(disposalToken.IsCancellationRequested && disposed.IsDisposed, "Disposing lifecycle cancels the in-flight recovery and is idempotent");
        disposalGate.SetResult(true);
        check(!await disposing && disposedResumes == 0 && !disposed.RecoveryScheduled, "Disposed lifecycle never revives or schedules a renderer");
        disposed.SimulateSignal(DisplayLifecycleSignal.DisplayOn); disposed.RequestRecovery();
        check(!await disposed.RestorePendingAsync(), "Post-disposal signals cannot restart lifecycle work");

        using var fatal = new DisplayLifecycleController(dispatcher, IntPtr.Zero, () => { },
            _ => Task.FromException<bool>(new COMException("Injected fatal", GraphicsFailure.RenderThreadFailure)), () => { }, false);
        fatal.SetHostVisible(true); fatal.RequestRecovery();
        bool propagated = false;
        try { await fatal.RestorePendingAsync(); }
        catch (Exception ex) when (GraphicsFailure.IsFatal(ex)) { propagated = true; }
        check(propagated && fatal.RetryExhausted && !fatal.RecoveryScheduled, "Fatal WPF errors propagate and stop local renderer retries");
    }

    private static async Task CheckHandoffAsync(int count, Action<bool, string> check)
    {
        string probe = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--graphics-recovery-parent-probe");
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string key in new[] { "CODEX_THREAD_ID", "CODEX_TURN_ID", "CODEX_APP_TOOLS_PIPE_PATH" })
            start.Environment[key] = "display-test-placeholder";
        using var parent = Process.Start(start) ?? throw new InvalidOperationException("Probe parent did not start.");
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
        check(parent.ExitCode == 0, "Actual recovery probe parent launches a child then exits: attempt " + count);
        string report = Path.Combine(AppContext.BaseDirectory, "graphics-recovery-probe-child.json");
        JsonElement data = default;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 12000)
        {
            if (File.Exists(report))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(report));
                if (doc.RootElement.TryGetProperty("probe", out var id) && id.GetString() == probe)
                {
                    data = doc.RootElement.Clone();
                    break;
                }
            }
            await Task.Delay(60);
        }
        check(data.ValueKind == JsonValueKind.Object && data.GetProperty("passed").GetBoolean(), "Actual child recovery initialization and handoff succeed: attempt " + count);
        check(data.GetProperty("parentExited").GetBoolean() && data.GetProperty("probeMutexReleased").GetBoolean(), "Actual recovery child starts after parent exit and releases probe ownership: attempt " + count);
        check(!data.GetProperty("inheritedCodexContext").GetBoolean(), "Actual recovery child receives no transient Codex context: attempt " + count);
        check(data.GetProperty("compatibilityMode").GetBoolean() == (count == 2), "Compatibility rendering is enabled only for the second recovery launch: attempt " + count);
    }

    private static async Task<JsonElement> SceneStateAsync(SceneAnimationView scene)
    {
        using var outer = JsonDocument.Parse(await scene.GetDiagnosticsAsync());
        using var inner = JsonDocument.Parse(outer.RootElement.GetString()!);
        return inner.RootElement.Clone();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMilliseconds = 6000)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (await condition()) return;
            await Task.Delay(40);
        }
        throw new TimeoutException("The isolated renderer did not reach its expected state.");
    }

    private static async Task<bool> FailsAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static bool CompletionState(object view, string field)
    {
        var completion = (TaskCompletionSource<bool>)view.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        // Observe the expected terminal error so this test does not create an
        // unrelated unobserved-task notification after its assertions finish.
        _ = completion.Task.Exception;
        return completion.Task.IsCompleted;
    }
}
