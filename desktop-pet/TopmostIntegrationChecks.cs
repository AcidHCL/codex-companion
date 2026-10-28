using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

// Standalone native-window regression. All state changes target the two windows
// created here; no user window, settings file, Codex transport or input is used.
internal static class TopmostIntegrationChecks
{
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
    private const long WsExTopmost = 0x00000008L;
    private const long ProtectedExStyles = 0x08000000L | 0x00000020L | 0x00080000L | 0x00000080L;
    private static readonly IntPtr HwndTop = IntPtr.Zero, HwndTopmost = new(-1), HwndNotTopmost = new(-2);

    internal static int Run()
    {
        string report = Path.Combine(AppContext.BaseDirectory, "topmost-validation.json");
        int assertions = 0, result = 1;
        bool finished = false, petClosed = false;
        string foregroundMode = "not_initialized";
        string activePhase = "initializing";
        var elapsed = Stopwatch.StartNew();
        PetWindow? pet = null;
        Window? fixture = null;
        var checks = new List<string>();
        var samples = new List<object>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(55) };
        var foregroundWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };

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
            foregroundWatch.Stop();
            try
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    passed, version = AppVersion.Number, assertions, checks, samples, error, foregroundMode,
                    lastPhase = activePhase, elapsedMilliseconds = elapsed.ElapsedMilliseconds,
                    inspectedActualNativeZOrder = true,
                    changedExistingUserWindows = false, syntheticMouseOrKeyboardInput = false,
                    realAccountRequests = 0, userSettingsWrites = 0,
                    fixtures = "One non-persisting static pet and one ordinary WPF window with an editable control"
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                if (!petClosed) pet?.Close();
                fixture?.Close();
                result = passed ? 0 : 1;
                app.Shutdown(result);
            }
        }

        timeout.Tick += (_, _) => Finish(false, "TOPMOST_CHECK_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Finish(false, "TOPMOST_DISPATCHER_" + e.Exception.GetType().Name + ": " + e.Exception.Message);
        };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                // Capture identity only. Never activate, inspect content from,
                // or otherwise modify this existing foreground window.
                IntPtr originalForeground = GetForegroundWindow();
                IntPtr expectedForeground = originalForeground;
                bool fixtureTransition = true;
                IntPtr testPetHandle = IntPtr.Zero;
                var input = new TextBox { Text = "置顶回归检查：仅操作本测试窗口", Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
                fixture = new Window
                {
                    Title = "AI Pet native topmost regression fixture", Width = 520, Height = 340,
                    Left = 40, Top = 40, Content = input, Background = Brushes.LightGray,
                    ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Topmost = false
                };
                fixture.Show();
                IntPtr fixtureHandle = new WindowInteropHelper(fixture).Handle;
                void RecordForeground(string cause, int? activationKind = null)
                {
                    IntPtr current = GetForegroundWindow();
                    samples.Add(new
                    {
                        name = "foreground-diagnostic", cause, phase = activePhase,
                        elapsedMilliseconds = elapsed.ElapsedMilliseconds, activationKind, fixtureTransition,
                        expectedForegroundPreserved = current == expectedForeground,
                        originalForegroundPreserved = current == originalForeground,
                        foregroundIsFixture = current == fixtureHandle,
                        foregroundIsPet = testPetHandle != IntPtr.Zero && current == testPetHandle,
                        foregroundIsOther = current != IntPtr.Zero && current != expectedForeground &&
                            current != fixtureHandle && current != testPetHandle,
                        foregroundAbsent = current == IntPtr.Zero,
                        fixtureWpfActive = fixture.IsActive,
                        fixtureNativeVisible = IsWindowVisible(fixtureHandle),
                        fixtureNativeMinimized = IsIconic(fixtureHandle),
                        ownThreadFocusIsFixture = GetFocus() == fixtureHandle,
                        fixtureKeyboardFocus = ReferenceEquals(Keyboard.FocusedElement, input),
                        petRepairCount = pet?.TopmostGuard?.ApplyCount
                    });
                }
                // Diagnose only messages delivered to our own fixture. A
                // click-activation or mouse-down here identifies interference
                // without reading input, titles, or handles from other apps.
                HwndSource.FromHwnd(fixtureHandle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    string? cause = message switch
                    {
                        0x0006 => "fixture-WM_ACTIVATE", 0x0007 => "fixture-WM_SETFOCUS",
                        0x0008 => "fixture-WM_KILLFOCUS", 0x0021 => "fixture-WM_MOUSEACTIVATE",
                        0x00A1 => "fixture-WM_NCLBUTTONDOWN", 0x0201 => "fixture-WM_LBUTTONDOWN",
                        _ => null
                    };
                    if (cause != null) RecordForeground(cause, message == 0x0006 ? (int)(wParam.ToInt64() & 0xffff) : null);
                    return IntPtr.Zero;
                });
                await ActivateFixtureAsync(fixture, input, fixtureHandle, samples, "initial-show", requestForeground: false);
                foregroundMode = "preserve_post_fixture_transition_foreground";
                expectedForeground = GetForegroundWindow();
                Check(expectedForeground == originalForeground || expectedForeground == fixtureHandle,
                    "Fixture setup ends with either the original window or our fixture in the foreground");
                fixtureTransition = false;
                // Our own minimize/restore and TextBox.Focus can legally change
                // foreground. Each completed fixture operation establishes the
                // baseline that subsequent pet repairs must leave unchanged.
                async Task FocusFixtureAsync(string phase)
                {
                    activePhase = phase;
                    await ActivateFixtureAsync(fixture, input, fixtureHandle, samples, phase, requestForeground: false);
                    IntPtr current = GetForegroundWindow();
                    Check(current == originalForeground || current == fixtureHandle,
                        phase + ": fixture transition ends with an allowed foreground identity");
                    expectedForeground = current;
                    fixtureTransition = false;
                }

                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                var settings = new DesktopSettings
                {
                    SkinId = skins.First(s => s.Id != "deepseek").Id,
                    Size = 160, Left = 90, Top = 80, Topmost = true,
                    MotionEnabled = false, MovementEnabled = false, SceneAutoEnabled = false, ClickThrough = true,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false
                };
                pet = new PetWindow(skins, settings, persist: false);
                pet.Closed += (_, _) => petClosed = true;
                pet.Show();
                IntPtr petHandle = new WindowInteropHelper(pet).Handle;
                testPetHandle = petHandle;
                var initialBounds = NativeMethods.GetWindowBoundsPixels(petHandle);
                long initialProtectedStyles = GetWindowLongPtr(petHandle, -20).ToInt64() & ProtectedExStyles;
                Check(initialProtectedStyles == ProtectedExStyles,
                    "Test pet starts with NOACTIVATE, TRANSPARENT, LAYERED and TOOLWINDOW styles enabled");
                bool foregroundStayed = true;
                IntPtr previousForeground = GetForegroundWindow();
                foregroundWatch.Tick += (_, _) =>
                {
                    IntPtr current = GetForegroundWindow();
                    if (current != previousForeground)
                    {
                        RecordForeground("foreground-identity-changed");
                        previousForeground = current;
                    }
                    foregroundStayed &= current != petHandle && (fixtureTransition || current == expectedForeground);
                };
                foregroundWatch.Start();

                bool FocusPreserved() => GetForegroundWindow() == expectedForeground && GetForegroundWindow() != petHandle &&
                    GetFocus() == fixtureHandle && ReferenceEquals(Keyboard.FocusedElement, input);

                void Record(string name)
                {
                    var order = NativeOrder(petHandle, fixtureHandle);
                    samples.Add(new
                    {
                        name, phase = activePhase, elapsedMilliseconds = elapsed.ElapsedMilliseconds,
                        petRank = order.Pet, fixtureRank = order.Fixture,
                        petNativeTopmost = IsNativeTopmost(petHandle), settings.Topmost,
                        petVisible = IsWindowVisible(petHandle), fixtureForeground = GetForegroundWindow() == fixtureHandle,
                        fixtureHasOwner = GetWindow(fixtureHandle, 4) != IntPtr.Zero,
                        fixtureOwnerVisible = IsWindowVisible(GetWindow(fixtureHandle, 4)),
                        expectedForegroundPreserved = GetForegroundWindow() == expectedForeground,
                        petOwnsForeground = GetForegroundWindow() == petHandle,
                        fixtureKeyboardFocus = GetFocus() == fixtureHandle && ReferenceEquals(Keyboard.FocusedElement, input)
                    });
                }

                async Task AssertAboveAsync(string name)
                {
                    activePhase = name;
                    bool focusStayed = true;
                    bool recovered = await WaitUntilAsync(() =>
                    {
                        focusStayed &= FocusPreserved();
                        var order = NativeOrder(petHandle, fixtureHandle);
                        return IsNativeTopmost(petHandle) && order.Pet >= 0 && order.Fixture >= 0 && order.Pet < order.Fixture;
                    });
                    Record(name);
                    Check(recovered, name + ": pet is in the native topmost band and actually above the fixture");
                    Check(focusStayed && FocusPreserved(), name + ": recovery preserves the selected foreground and fixture keyboard focus");
                    Check(NativeMethods.GetWindowBoundsPixels(petHandle) == initialBounds,
                        name + ": z-order repair does not move or resize the pet");
                    Check((GetWindowLongPtr(petHandle, -20).ToInt64() & ProtectedExStyles) == initialProtectedStyles,
                        name + ": repair preserves NOACTIVATE, TRANSPARENT, LAYERED and TOOLWINDOW styles");
                }

                await AssertAboveAsync("initial-show");
                Check(!settings.MotionEnabled && !settings.MovementEnabled,
                    "Topmost protection runs while breathing and desktop movement are both disabled");
                var guard = pet.TopmostGuard ?? throw new InvalidOperationException("Pet topmost guard was not initialized");
                Check(guard.IsMonitoring && !guard.IsDisposed, "Visible topmost pet installs monitoring");

                for (int cycle = 1; cycle <= 6; cycle++)
                {
                    activePhase = "maximize-fixture-" + cycle;
                    fixtureTransition = true;
                    fixture.WindowState = WindowState.Maximized;
                    await FocusFixtureAsync("maximize-" + cycle);
                    await AssertAboveAsync("maximize-" + cycle);
                    activePhase = "minimize-fixture-" + cycle;
                    fixtureTransition = true;
                    fixture.WindowState = WindowState.Minimized;
                    await Task.Delay(90);
                    // Minimized windows cannot own focus. Restore only our own
                    // fixture before asserting non-activation by the pet guard.
                    fixture.WindowState = WindowState.Normal;
                    await FocusFixtureAsync("restore-" + cycle);
                    await AssertAboveAsync("restore-" + cycle);
                }

                for (int cycle = 1; cycle <= 3; cycle++)
                {
                    activePhase = "native-demotion-injection-" + cycle;
                    Check(settings.Topmost, "Native demotion starts with the user's topmost setting still enabled");
                    Check(SetWindowPos(petHandle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate),
                        "Inject a native NOTOPMOST demotion into only the test pet");
                    Check(SetWindowPos(fixtureHandle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate),
                        "Place the ordinary fixture over the temporarily demoted pet");
                    Record("native-demotion-injected-" + cycle);
                    await AssertAboveAsync("native-demotion-recovered-" + cycle);
                }

                // A second topmost window can cover a pet whose TOPMOST bit is
                // still intact. This exercises actual relative order, rather
                // than merely checking or restoring that style bit.
                activePhase = "peer-topmost-injection";
                fixture.Topmost = true;
                Check(SetWindowPos(fixtureHandle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate),
                    "Place our topmost fixture ahead of the still-topmost pet");
                var coveredOrder = NativeOrder(petHandle, fixtureHandle);
                Check(IsNativeTopmost(petHandle) && coveredOrder.Fixture >= 0 && coveredOrder.Fixture < coveredOrder.Pet,
                    "Reproduce actual occlusion while the pet native topmost flag remains set");
                Record("topmost-peer-covers-pet");
                activePhase = "peer-minimize-fixture";
                fixtureTransition = true;
                fixture.WindowState = WindowState.Minimized;
                await Task.Delay(90);
                fixture.WindowState = WindowState.Normal;
                await FocusFixtureAsync("topmost-peer-restore");
                await AssertAboveAsync("topmost-peer-restoration");
                // Mimic the animation tail reordering the restored HWND once
                // more after the first repair. No activation, movement, or
                // explicit restore request is made: the already queued settled
                // check from this restoration must still correct the order.
                activePhase = "peer-animation-tail-injection";
                await Task.Delay(100);
                Check(SetWindowPos(fixtureHandle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate),
                    "Re-cover the pet at the restoration animation tail without another activation or restore event");
                var tailOrder = NativeOrder(petHandle, fixtureHandle);
                Check(IsNativeTopmost(petHandle) && tailOrder.Fixture >= 0 && tailOrder.Fixture < tailOrder.Pet,
                    "Animation-tail injection genuinely covers the still-topmost pet");
                Record("topmost-peer-animation-tail-covers");
                await AssertAboveAsync("topmost-peer-animation-tail-settled");
                activePhase = "peer-topmost-disabled-settling";
                fixture.Topmost = false;
                await Task.Delay(900); // Drain the bounded, delayed event repair.
                int quietApplyCount = guard.ApplyCount;
                activePhase = "quiet-no-events-observation";
                bool quietFocusStayed = true;
                await ObserveAsync(2600, () => quietFocusStayed &= FocusPreserved());
                Check(guard.ApplyCount == quietApplyCount,
                    "Idle topmost protection does not repeatedly rewrite native z-order");
                Check(quietFocusStayed, "Idle watchdog preserves the selected foreground and fixture keyboard focus");

                RecordForeground("quiet-observation-finished");
                activePhase = "tray-menu-mark-open";
                RecordForeground("before-SetTrayMenuOpen-true");
                pet.SetTrayMenuOpen(true);
                RecordForeground("after-SetTrayMenuOpen-true");
                int menuApplyCount = guard.ApplyCount;
                activePhase = "tray-menu-native-demotion";
                RecordForeground("before-menu-native-demotion");
                Check(SetWindowPos(petHandle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate),
                    "Demote only the test pet while its tray-menu state is open");
                RecordForeground("after-menu-native-demotion");
                activePhase = "tray-menu-pause-observation";
                bool menuStayedPaused = true, menuFocusStayed = true;
                await ObserveAsync(2600, () =>
                {
                    menuStayedPaused &= !IsNativeTopmost(petHandle) && guard.ApplyCount == menuApplyCount;
                    menuFocusStayed &= FocusPreserved();
                });
                Record("tray-menu-pauses-repair");
                Check(menuStayedPaused, "Tray-menu state pauses native repairs across a watchdog interval");
                Check(menuFocusStayed, "Tray-menu pause preserves the selected foreground and fixture keyboard focus");
                activePhase = "tray-menu-mark-closed";
                pet.SetTrayMenuOpen(false);
                await AssertAboveAsync("tray-menu-close-recovers");

                activePhase = "topmost-disabled-observation";
                pet.SetTopmost(false);
                Check(!settings.Topmost && !pet.Topmost && !IsNativeTopmost(petHandle),
                    "Disabling topmost updates settings, WPF property and native band immediately");
                Check(!guard.IsMonitoring, "Disabling topmost unhooks native monitoring and stops the watchdog");
                int disabledApplyCount = guard.ApplyCount;
                Check(SetWindowPos(fixtureHandle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate),
                    "Place fixture above the pet while topmost is disabled");
                bool disabledStayedBehind = true, disabledFocusStayed = true;
                await ObserveAsync(2600, () =>
                {
                    var order = NativeOrder(petHandle, fixtureHandle);
                    disabledStayedBehind &= !IsNativeTopmost(petHandle) && order.Pet > order.Fixture && order.Fixture >= 0;
                    disabledFocusStayed &= FocusPreserved();
                });
                Record("disabled-remains-behind");
                Check(disabledStayedBehind && guard.ApplyCount == disabledApplyCount,
                    "Disabled topmost never promotes the pet across a watchdog interval");
                Check(disabledFocusStayed, "Disabled topmost leaves fixture focus unchanged");

                pet.SetTopmost(true);
                await AssertAboveAsync("re-enabled");
                activePhase = "pet-hidden-observation";
                pet.Hide();
                Check(!guard.IsMonitoring, "Hiding the pet unhooks native monitoring and stops the watchdog");
                int hiddenApplyCount = guard.ApplyCount;
                Check(SetWindowPos(petHandle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate),
                    "Demote the hidden test pet without showing it");
                bool hiddenStayed = true, hiddenFocusStayed = true;
                await ObserveAsync(2600, () =>
                {
                    hiddenStayed &= !pet.IsVisible && !IsWindowVisible(petHandle) && !IsNativeTopmost(petHandle);
                    hiddenFocusStayed &= FocusPreserved();
                });
                Record("hidden-remains-hidden-and-demoted");
                Check(settings.Topmost && hiddenStayed && guard.ApplyCount == hiddenApplyCount,
                    "Hidden pet is neither shown nor promoted even though topmost remains enabled in settings");
                Check(hiddenFocusStayed, "Hidden pet guard leaves fixture focus unchanged");
                pet.Show();
                await AssertAboveAsync("show-recovers-without-activation");

                activePhase = "pet-close";
                pet.Close();
                Check(petClosed && !IsWindow(petHandle), "Close destroys the test pet HWND");
                Check(guard.IsDisposed && !guard.IsMonitoring, "Close disposes the guard and removes native hooks and timers");
                int closedApplyCount = guard.ApplyCount;
                fixtureTransition = true;
                fixture.WindowState = WindowState.Maximized;
                await FocusFixtureAsync("after-pet-close");
                await ObserveAsync(2600, () =>
                {
                    if (IsWindow(petHandle)) throw new InvalidOperationException("Closed pet HWND was unexpectedly recreated");
                });
                Check(FocusPreserved() && guard.ApplyCount == closedApplyCount,
                    "Window events after pet close cause no native repair, focus change or dispatcher exception");
                Check(foregroundStayed,
                    "Continuous foreground sampling found no pet activation or unexpected change of the preserved foreground");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run();
        return result;
    }

    private static async Task<bool> ActivateFixtureAsync(Window fixture, TextBox input, IntPtr handle,
        List<object> samples, string phase, bool requestForeground = true)
    {
        void Record(string step, bool? activationAccepted = null)
        {
            samples.Add(new
            {
                name = "fixture-activation-" + phase + "-" + step,
                fixtureWpfVisible = fixture.IsVisible,
                fixtureLoaded = fixture.IsLoaded,
                fixtureNativeVisible = IsWindowVisible(handle),
                fixtureNativeMinimized = IsIconic(handle),
                fixtureWpfActive = fixture.IsActive,
                fixtureForeground = GetForegroundWindow() == handle,
                fixtureNativeFocus = GetFocus() == handle,
                fixtureNativeFocusAbsent = GetFocus() == IntPtr.Zero,
                fixtureKeyboardFocus = ReferenceEquals(Keyboard.FocusedElement, input),
                activationAccepted, requestForeground
            });
        }

        Record("before");
        // The test process is intentionally launched with WindowStyle Hidden.
        // STARTUPINFO can override the first ShowWindow call even when WPF's
        // IsVisible says true. Explicitly show only our known fixture HWND;
        // do not manipulate any existing foreground window or inject input.
        if (!IsWindowVisible(handle)) ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
        fixture.UpdateLayout();
        await fixture.Dispatcher.InvokeAsync(() => fixture.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        bool? accepted = requestForeground ? fixture.Activate() : null;
        input.Focus();
        Keyboard.Focus(input);
        await Task.Delay(100);
        Record("after", accepted);
        if (GetFocus() != handle || !ReferenceEquals(Keyboard.FocusedElement, input))
            throw new InvalidOperationException("TEST_FIXTURE_MISSING_OWN_THREAD_FOCUS: our fixture must retain native and keyboard focus within this test thread; see fixture-activation samples");
        return GetForegroundWindow() == handle;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 4000)
        {
            if (condition()) return true;
            await Task.Delay(40);
        }
        return condition();
    }

    private static async Task ObserveAsync(int milliseconds, Action observe)
    {
        var clock = Stopwatch.StartNew();
        do { observe(); await Task.Delay(40); } while (clock.ElapsedMilliseconds < milliseconds);
        observe();
    }

    private static bool IsNativeTopmost(IntPtr handle) => (GetWindowLongPtr(handle, -20).ToInt64() & WsExTopmost) != 0;

    // EnumWindows is enumerated in desktop z-order. Ignore all window content;
    // only the ranks of the two known fixture handles are retained.
    private static (int Pet, int Fixture) NativeOrder(IntPtr pet, IntPtr fixture)
    {
        int rank = 0, petRank = -1, fixtureRank = -1;
        EnumWindows((handle, _) =>
        {
            if (handle == pet) petRank = rank;
            if (handle == fixture) fixtureRank = rank;
            rank++;
            return petRank < 0 || fixtureRank < 0;
        }, IntPtr.Zero);
        return (petRank, fixtureRank);
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
}
