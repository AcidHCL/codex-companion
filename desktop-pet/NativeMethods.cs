using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace DesktopPet;

internal static class NativeMethods
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint MonitorDefaultToNearest = 2;

    public static void SetNoActivate(IntPtr hwnd, bool enabled = true) => ChangeStyle(hwnd, WsExNoActivate, enabled);

    // WPF AllowsTransparency supplies WS_EX_LAYERED; preserve that style when toggling input.
    public static void SetClickThrough(IntPtr hwnd, bool enabled) => ChangeStyle(hwnd, WsExTransparent, enabled);

    public static void SetToolWindow(IntPtr hwnd, bool enabled = true)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style = enabled ? (style | WsExToolWindow) & ~WsExAppWindow : style & ~WsExToolWindow;
        ApplyStyle(hwnd, style);
    }

    public static double GetDpiScale(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(hwnd);
        return (dpi == 0 ? 96u : dpi) / 96.0;
    }

    public static string GetMonitorName(IntPtr hwnd) => GetMonitorInfoForWindow(hwnd).Device ?? string.Empty;

    public static Rect GetWindowWorkAreaPixels(IntPtr hwnd)
    {
        var work = GetMonitorInfoForWindow(hwnd).Work;
        return new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    // For local UI calculations only. Persist placement with the *Pixels APIs, which
    // preserve the virtual desktop coordinate system across mixed-DPI monitors.
    public static Rect GetWindowWorkArea(IntPtr hwnd)
    {
        var pixels = GetWindowWorkAreaPixels(hwnd);
        var scale = GetDpiScale(hwnd);
        return new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale);
    }

    public static Rect GetWindowBoundsPixels(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    public static Rect ClampToWorkArea(Rect bounds, IntPtr hwnd)
    {
        var work = GetWindowWorkArea(hwnd);
        return Clamp(bounds, work);
    }

    public static void SetWindowPositionPixels(IntPtr hwnd, int left, int top)
    {
        if (!SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void ClampWindowToWorkArea(IntPtr hwnd)
    {
        var bounds = GetWindowBoundsPixels(hwnd);
        var target = Clamp(bounds, GetWindowWorkAreaPixels(hwnd));
        if (Math.Abs(bounds.X - target.X) >= 1 || Math.Abs(bounds.Y - target.Y) >= 1)
            SetWindowPositionPixels(hwnd, (int)Math.Round(target.X), (int)Math.Round(target.Y));
    }

    private static Rect Clamp(Rect bounds, Rect work)
    {
        var maxLeft = Math.Max(work.Left, work.Right - bounds.Width);
        var maxTop = Math.Max(work.Top, work.Bottom - bounds.Height);
        return new Rect(Math.Clamp(bounds.Left, work.Left, maxLeft), Math.Clamp(bounds.Top, work.Top, maxTop), bounds.Width, bounds.Height);
    }

    private static MonitorInfo GetMonitorInfoForWindow(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    private static void ChangeStyle(IntPtr hwnd, long flag, bool enabled)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        ApplyStyle(hwnd, enabled ? style | flag : style & ~flag);
    }

    private static void ApplyStyle(IntPtr hwnd, long style)
    {
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
