using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal enum DisplayLifecycleSignal
{
    DisplayOff, DisplayOn, SystemSuspend, SystemResume,
    SessionLocked, SessionUnlocked, SessionDisconnected, SessionConnected, DisplayChanged
}

// One UI-thread owner for monitor/session transitions. It never changes power
// settings, locks a session, shows a window, or changes saved preferences.
internal sealed class DisplayLifecycleController : IDisposable
{
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private readonly Dispatcher _dispatcher;
    private readonly Action _suspend, _resumed;
    private readonly Func<CancellationToken, Task<bool>> _restore;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _sessionRefreshTimer;
    private readonly Func<WindowsSessionSnapshot>? _querySession;
    private readonly IntPtr _window;
    private CancellationTokenSource _cycle = new();
    private IntPtr _powerRegistration;
    private bool _sessionRegistered, _displayOff, _systemSuspended, _locked, _disconnected, _hostVisible;
    private bool _lockKnown, _connectionKnown;
    private readonly bool _subscribeToSystemEvents;
    private int _generation;
    private readonly Queue<long> _rendererFailures = new();
    internal bool IsSuspended { get; private set; }
    internal bool IsRestoring { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal int RestoreAttempts { get; private set; }
    internal int CompletedRestores { get; private set; }
    internal bool RetryExhausted => IsSuspended && RestoreAttempts >= 3 && !IsRestoring;
    internal bool PowerNotificationsRegistered => _powerRegistration != IntPtr.Zero;
    internal bool RecoveryScheduled => _timer.IsEnabled;
    internal bool RendererRetryExhausted { get; private set; }
    internal int SessionId { get; }
    internal bool IsRemoteSession { get; private set; }
    internal bool IsActiveRemoteSession => IsRemoteSession && _connectionKnown && !_disconnected && _lockKnown && !_locked;
    internal bool IsSessionLocked => _locked;
    internal bool IsSessionDisconnected => _disconnected;
    internal bool SessionNotificationsRegistered => _sessionRegistered;
    // An RDP terminal renders to the remote display, independently of whether
    // the host's physical monitor is off. Its lock/disconnect still blocks.
    private bool Blocked => (_displayOff && !IsActiveRemoteSession) || _systemSuspended || _locked || _disconnected || !_hostVisible;
    // Ordinary WPF controls remain usable while a scene/WebView renderer retries.
    internal bool AllowsCompanionInteraction => !Blocked && !IsDisposed;

    internal DisplayLifecycleController(Dispatcher dispatcher, IntPtr window, Action suspend,
        Func<CancellationToken, Task<bool>> restore, Action resumed, bool subscribeToSystemEvents = true,
        Func<WindowsSessionSnapshot>? querySession = null)
    {
        _dispatcher = dispatcher; _suspend = suspend; _restore = restore; _resumed = resumed;
        _window = window; _subscribeToSystemEvents = subscribeToSystemEvents;
        _querySession = querySession;
        if (_querySession == null && subscribeToSystemEvents) _querySession = WindowsSessionState.QueryCurrent;
        var initialSession = _querySession?.Invoke();
        SessionId = initialSession?.SessionId ?? WindowsSessionState.CurrentSessionId;
        if (initialSession.HasValue) ApplySessionSnapshot(initialSession.Value);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(900) };
        _timer.Tick += TimerTick;
        _sessionRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _sessionRefreshTimer.Tick += SessionRefreshTick;
        if (!subscribeToSystemEvents) return;
        if (window != IntPtr.Zero)
        {
            // Microsoft recommends session display status for interactive
            // apps; console state is only a compatibility fallback.
            var setting = SessionDisplayStatus;
            _powerRegistration = RegisterPowerSettingNotification(window, ref setting, 0);
            if (_powerRegistration == IntPtr.Zero)
            {
                setting = ConsoleDisplayState;
                _powerRegistration = RegisterPowerSettingNotification(window, ref setting, 0);
            }
            _sessionRegistered = WTSRegisterSessionNotification(window, 0); // NOTIFY_FOR_THIS_SESSION
        }
        // WTS may not be ready during sign-in. Retry registration and query
        // our own session without interpreting global SessionSwitch events.
        if (!_sessionRegistered) _sessionRefreshTimer.Start();
    }

    internal void SetHostVisible(bool visible)
    {
        if (IsDisposed || _hostVisible == visible) return;
        _hostVisible = visible;
        if (!visible) { _timer.Stop(); if (IsRestoring) StartFreshCycle(schedule: false); }
        else if (Blocked && !IsSuspended) StartFreshCycle(schedule: false);
        else if (IsSuspended && !Blocked) StartFreshCycle();
    }

    internal void HandleWindowMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (IsDisposed) return;
        if (message == 0x02B1) // WM_WTSSESSION_CHANGE carries the affected session ID.
        {
            if (SessionId >= 0 && lParam.ToInt64() == SessionId) HandleSessionChange(unchecked((int)wParam.ToInt64()));
            return;
        }
        if (message == 0x007E) // WM_DISPLAYCHANGE may accompany switching between console and RDP.
        {
            RefreshSessionState();
            SimulateSignal(DisplayLifecycleSignal.DisplayChanged);
            return;
        }
        if (message != 0x0218) return; // WM_POWERBROADCAST
        switch (wParam.ToInt64())
        {
            case 0x0004: SimulateSignal(DisplayLifecycleSignal.SystemSuspend); break; // PBT_APMSUSPEND
            case 0x0006: // PBT_APMRESUMECRITICAL
            case 0x0007: // PBT_APMRESUMESUSPEND
            case 0x0012: // PBT_APMRESUMEAUTOMATIC
                RefreshSessionState();
                SimulateSignal(DisplayLifecycleSignal.SystemResume);
                break;
            case 0x8013 when lParam != IntPtr.Zero: // PBT_POWERSETTINGCHANGE
                var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                if ((setting.Setting == SessionDisplayStatus || setting.Setting == ConsoleDisplayState) && setting.DataLength == 4)
                {
                    int state = Marshal.ReadInt32(lParam, Marshal.SizeOf<PowerBroadcastSetting>());
                    if (state is 0 or 1 or 2) SimulateSignal(state == 0 ? DisplayLifecycleSignal.DisplayOff : DisplayLifecycleSignal.DisplayOn);
                }
                break;
        }
    }

    private void HandleSessionChange(int reason)
    {
        _dispatcher.VerifyAccess();
        bool wasBlocked = Blocked, wasRemote = IsRemoteSession;
        var snapshot = _querySession?.Invoke();
        if (snapshot.HasValue && snapshot.Value.SessionId != SessionId) snapshot = null;
        ApplySessionSnapshot(snapshot);
        // Explicit lock/disconnect messages are authoritative even if the WTS
        // query still returns its pre-transition value. Connection is never
        // permission to clear a lock: only a query or unlock event can do so.
        switch (reason)
        {
            case 1: // ConsoleConnect may also be RemoteFX; an affirmative query selects that exception.
                IsRemoteSession = snapshot?.IsRemote == true;
                _disconnected = false; _connectionKnown = true;
                break;
            case 3: IsRemoteSession = true; _disconnected = false; _connectionKnown = true; break;
            case 2: case 4: case 6: _disconnected = true; _connectionKnown = true; break;
            case 5: _disconnected = false; _connectionKnown = true; break;
            case 7: _locked = true; _lockKnown = true; break;
            case 8: _locked = false; _lockKnown = true; break;
        }
        ReconcileState(wasBlocked, topology: wasRemote != IsRemoteSession || reason is 1 or 3 or 15);
        if (_subscribeToSystemEvents)
        {
            _sessionRefreshTimer.Stop(); _sessionRefreshTimer.Interval = TimeSpan.FromSeconds(2); _sessionRefreshTimer.Start();
        }
    }

    internal void RefreshSessionState()
    {
        _dispatcher.VerifyAccess();
        if (IsDisposed || _querySession == null) return;
        bool wasBlocked = Blocked, wasRemote = IsRemoteSession;
        ApplySessionSnapshot(_querySession());
        ReconcileState(wasBlocked, topology: wasRemote != IsRemoteSession);
    }

    private void ApplySessionSnapshot(WindowsSessionSnapshot? snapshot)
    {
        if (!snapshot.HasValue || snapshot.Value.SessionId != SessionId) return;
        var state = snapshot.Value;
        if (state.IsRemote.HasValue) IsRemoteSession = state.IsRemote.Value;
        if (state.IsConnected.HasValue) { _disconnected = !state.IsConnected.Value; _connectionKnown = true; }
        if (state.IsLocked.HasValue) { _locked = state.IsLocked.Value; _lockKnown = true; }
    }

    private void SessionRefreshTick(object? sender, EventArgs e)
    {
        _sessionRefreshTimer.Stop();
        if (IsDisposed) return;
        if (!_sessionRegistered && _window != IntPtr.Zero)
            _sessionRegistered = WTSRegisterSessionNotification(_window, 0);
        RefreshSessionState();
        if (!_sessionRegistered) { _sessionRefreshTimer.Interval = TimeSpan.FromSeconds(5); _sessionRefreshTimer.Start(); }
    }

    // Tests use the same transition path without sending power messages or
    // changing the real monitor/session. Must be invoked on the UI thread.
    internal void SimulateSignal(DisplayLifecycleSignal signal)
    {
        _dispatcher.VerifyAccess();
        if (IsDisposed) return;
        bool wasBlocked = Blocked;
        switch (signal)
        {
            case DisplayLifecycleSignal.DisplayOff: _displayOff = true; break;
            case DisplayLifecycleSignal.DisplayOn: _displayOff = false; break;
            case DisplayLifecycleSignal.SystemSuspend: _systemSuspended = true; break;
            case DisplayLifecycleSignal.SystemResume: _systemSuspended = false; break;
            case DisplayLifecycleSignal.SessionLocked: _locked = true; _lockKnown = true; break;
            case DisplayLifecycleSignal.SessionUnlocked: _locked = false; _lockKnown = true; break;
            case DisplayLifecycleSignal.SessionDisconnected: _disconnected = true; _connectionKnown = true; break;
            case DisplayLifecycleSignal.SessionConnected: _disconnected = false; _connectionKnown = true; break;
        }
        ReconcileState(wasBlocked, topology: signal == DisplayLifecycleSignal.DisplayChanged);
    }

    private void ReconcileState(bool wasBlocked, bool topology = false)
    {
        if (wasBlocked != Blocked || topology) ResetRendererFailureBudget();
        if (Blocked)
        {
            if (!wasBlocked || topology || !IsSuspended) StartFreshCycle(schedule: false);
            return;
        }
        if (wasBlocked || topology) StartFreshCycle();
    }

    internal void RequestRecovery(bool rendererFailure = false, bool userInitiated = false)
    {
        _dispatcher.VerifyAccess();
        if (IsDisposed) return;
        if (userInitiated) ResetRendererFailureBudget();
        // A failure from the current restore attempt must not create an
        // unbounded new retry budget. The outer attempt owns its next retry.
        if (IsRestoring || !userInitiated && IsSuspended && (_timer.IsEnabled || RetryExhausted || RendererRetryExhausted)) return;
        if (rendererFailure)
        {
            long now = Environment.TickCount64;
            while (_rendererFailures.Count > 0 && now - _rendererFailures.Peek() >= 600000) _rendererFailures.Dequeue();
            if (_rendererFailures.Count >= 3)
            {
                StartFreshCycle(schedule: false);
                RestoreAttempts = 3;
                RendererRetryExhausted = true;
                return;
            }
            _rendererFailures.Enqueue(now);
        }
        StartFreshCycle();
    }

    internal void ResetRendererFailureBudget()
    {
        _dispatcher.VerifyAccess();
        _rendererFailures.Clear(); RendererRetryExhausted = false;
    }

    private void StartFreshCycle(bool schedule = true)
    {
        _timer.Stop();
        _generation++;
        _cycle.Cancel(); _cycle.Dispose(); _cycle = new CancellationTokenSource();
        RestoreAttempts = 0;
        IsSuspended = true;
        // Also invalidates an in-flight renderer when another display change
        // arrives, so its late continuation cannot revive a stale instance.
        _suspend();
        if (schedule && !Blocked) Schedule(TimeSpan.FromMilliseconds(900));
    }

    private void Schedule(TimeSpan delay)
    {
        if (IsDisposed || Blocked || RestoreAttempts >= 3) return;
        _timer.Stop(); _timer.Interval = delay; _timer.Start();
    }

    private async void TimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        // Fatal WPF errors intentionally leave this async-void event through
        // DispatcherUnhandledException for the process-level recovery owner.
        await RestorePendingAsync();
    }

    internal async Task<bool> RestorePendingAsync()
    {
        _dispatcher.VerifyAccess();
        _timer.Stop();
        if (IsDisposed || Blocked || !IsSuspended || RestoreAttempts >= 3) return false;
        if (IsRestoring) { Schedule(TimeSpan.FromMilliseconds(150)); return false; }
        int generation = _generation;
        var token = _cycle.Token;
        bool fatal = false;
        IsRestoring = true; RestoreAttempts++;
        try
        {
            bool restored = await _restore(token);
            if (IsDisposed || token.IsCancellationRequested || generation != _generation || Blocked) return false;
            if (restored)
            {
                IsSuspended = false;
                CompletedRestores++;
                _resumed();
                return true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (GraphicsFailure.IsFatal(ex))
        {
            fatal = true;
            _cycle.Cancel(); RestoreAttempts = 3;
            throw;
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
        finally
        {
            IsRestoring = false;
            if (!IsDisposed && IsSuspended && !Blocked)
            {
                if (!fatal && generation == _generation) _suspend(); // Discard a half-loaded attempt before retrying.
                Schedule(TimeSpan.FromMilliseconds(generation != _generation ? 900 : RestoreAttempts == 1 ? 1800 : 4000));
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _generation++;
        _timer.Stop(); _timer.Tick -= TimerTick;
        _cycle.Cancel(); _cycle.Dispose();
        _sessionRefreshTimer.Stop(); _sessionRefreshTimer.Tick -= SessionRefreshTick;
        if (_sessionRegistered) { WTSUnRegisterSessionNotification(_window); _sessionRegistered = false; }
        if (_powerRegistration != IntPtr.Zero) { UnregisterPowerSettingNotification(_powerRegistration); _powerRegistration = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PowerBroadcastSetting { public Guid Setting; public uint DataLength; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("wtsapi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
