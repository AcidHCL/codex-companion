using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopPet;

// WPF's Topmost property expresses intent, but an unchanged dependency property
// does not repair a native demotion or a changed order within the topmost band.
// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos
// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwineventhook
internal sealed class TopmostController : IDisposable
{
    private const long WsExTopmost = 0x8;
    private const uint NoMoveSizeActivateOwner = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private readonly IntPtr _window;
    private readonly Func<bool> _enabled, _visible, _menuOpen;
    private readonly DispatcherTimer _pending, _settled, _watchdog;
    private readonly WinEventCallback _callback;
    private readonly List<IntPtr> _hooks = new();
    private bool _applying;
    private bool _postPromotionCheckAvailable;
    private IntPtr _eventWindow;
    private long _lastPromotion;
    internal bool IsMonitoring { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal int ApplyCount { get; private set; }

    internal TopmostController(Dispatcher dispatcher, IntPtr window, Func<bool> enabled, Func<bool> visible, Func<bool> menuOpen)
    {
        _window = window; _enabled = enabled; _visible = visible; _menuOpen = menuOpen;
        _callback = OnWinEvent; // The native hooks require a rooted delegate.
        _pending = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        // Leave more than the 450 ms promotion throttle after the first repair;
        // an animation-tail reorder must not swallow the final settling check.
        _settled = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _pending.Tick += (_, _) => { _pending.Stop(); RestoreIfNeeded(checkOrder: true); };
        _settled.Tick += (_, _) => { _settled.Stop(); RestoreIfNeeded(checkOrder: true); };
        // The fallback repairs the flag only. It never continuously competes with
        // another program which deliberately keeps raising its own topmost window.
        _watchdog.Tick += (_, _) => RestoreIfNeeded(checkOrder: false);
    }

    internal void Refresh()
    {
        if (IsDisposed) return;
        if (!_enabled() || !_visible())
        {
            StopMonitoring();
            if (!_enabled() && IsWindow(_window) && NativeTopmost()) SetNativeTopmost(false);
            return;
        }
        if (!IsMonitoring)
        {
            IsMonitoring = true;
            // Foreground, move/size end, Alt+Tab end, minimize end, window geometry.
            // Out-of-context hooks deliver on our UI thread; no input is injected.
            foreach (uint eventId in new uint[] { 0x0003, 0x000B, 0x0015, 0x0017, 0x800B })
            {
                var hook = SetWinEventHook(eventId, eventId, IntPtr.Zero, _callback, 0, 0, 0);
                if (hook != IntPtr.Zero) _hooks.Add(hook);
            }
            _watchdog.Start(); // Also works if hook registration is unavailable.
        }
        RequestRestore();
    }

    internal void RequestRestore(IntPtr relatedWindow = default)
    {
        if (IsDisposed || !IsMonitoring || _applying || !_enabled() || !_visible()) return;
        if (relatedWindow != IntPtr.Zero && relatedWindow != _window) _eventWindow = relatedWindow;
        if (_menuOpen()) { _pending.Stop(); _settled.Stop(); return; }
        _postPromotionCheckAvailable = true;
        if (!_pending.IsEnabled) _pending.Start();
        _settled.Stop(); _settled.Start();
    }

    internal void OnWindowMessage(int message, IntPtr lParam)
    {
        if (message != 0x0047 || lParam == IntPtr.Zero || _applying) return; // WM_WINDOWPOSCHANGED
        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
        // Walking/dragging uses SWP_NOZORDER. Do not schedule work every frame.
        if ((position.Flags & 0x0004) == 0) RequestRestore();
    }

    private void OnWinEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (IsDisposed || !IsMonitoring || window == IntPtr.Zero || window == _window) return;
        if (eventId == 0x800B && (objectId != 0 || childId != 0 || window != GetForegroundWindow())) return;
        if (GetAncestor(window, 2) != window) return; // GA_ROOT, not a child/control.
        RequestRestore(window);
    }

    private void RestoreIfNeeded(bool checkOrder)
    {
        if (IsDisposed || !IsMonitoring || !_enabled() || !_visible() || _menuOpen() ||
            !IsWindowVisible(_window) || IsIconic(_window)) return;
        var foreground = GetForegroundWindow();
        if (ForegroundHasMenu() || IsShellSurface(foreground)) return;
        bool missingFlag = !NativeTopmost();
        if (!missingFlag)
        {
            // A restored window may remain in the background, so inspect the
            // event's root window as well as the current foreground window.
            if (!checkOrder || (!CanCoverPet(foreground) && !CanCoverPet(_eventWindow))) return;
            if (Environment.TickCount64 - _lastPromotion < 450) return;
        }
        if (SetNativeTopmost(true) && checkOrder && _postPromotionCheckAvailable)
        {
            // A preceding repair may have deferred the first raise until the
            // settling timer. Allow one last check after that successful raise,
            // never a self-sustaining loop competing with another topmost app.
            _postPromotionCheckAvailable = false;
            _settled.Stop(); _settled.Start();
        }
    }

    private bool SetNativeTopmost(bool value)
    {
        try
        {
            _applying = true;
            // No activation, movement, sizing, visibility, or owner-order changes.
            // Only this pet's HWND is ever mutated by this controller.
            if (!SetWindowPos(_window, new IntPtr(value ? -1 : -2), 0, 0, 0, 0, NoMoveSizeActivateOwner))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            ApplyCount++;
            if (value) _lastPromotion = Environment.TickCount64;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Program.Log(ex); return false; }
        finally { _applying = false; }
    }

    private bool NativeTopmost() => (GetWindowLongPtr(_window, -20).ToInt64() & WsExTopmost) != 0;
    private bool CanCoverPet(IntPtr window) => window != IntPtr.Zero && window != _window &&
        IsWindowVisible(window) && !IsIconic(window) && !IsShellSurface(window) &&
        !HasVisibleOwner(window) && Overlaps(window) && IsAbove(window, _window);
    private static bool HasVisibleOwner(IntPtr window)
    {
        // WPF and other frameworks also use an invisible owner to omit ordinary
        // windows from the taskbar. Only a visible owner identifies the temporary
        // dialog/popup relationship we must avoid raising over.
        var owner = GetWindow(window, 4); // GW_OWNER
        return owner != IntPtr.Zero && IsWindowVisible(owner) && !IsIconic(owner);
    }
    private bool Overlaps(IntPtr other) => GetWindowRect(_window, out var a) && GetWindowRect(other, out var b) &&
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    private static bool IsAbove(IntPtr other, IntPtr window)
    {
        var visited = new HashSet<IntPtr>();
        for (var previous = GetWindow(window, 3); previous != IntPtr.Zero && visited.Count < 512 && visited.Add(previous); previous = GetWindow(previous, 3))
            if (previous == other) return true; // GW_HWNDPREV: higher in the Z order.
        return false;
    }
    private static bool ForegroundHasMenu()
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(0, ref info) && (info.Flags & 0x1C) != 0;
    }
    private static bool IsShellSurface(IntPtr window)
    {
        var name = new StringBuilder(256);
        if (window == IntPtr.Zero || GetClassName(window, name, name.Capacity) == 0) return false;
        return name.ToString() is "#32768" or "tooltips_class32" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
            "NotifyIconOverflowWindow" or "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow" or "MultitaskingViewFrame";
    }
    private void StopMonitoring()
    {
        IsMonitoring = false;
        _eventWindow = IntPtr.Zero;
        _postPromotionCheckAvailable = false;
        _pending.Stop(); _settled.Stop(); _watchdog.Stop();
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        StopMonitoring();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public uint Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretBounds; }
    private delegate void WinEventCallback(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
}
