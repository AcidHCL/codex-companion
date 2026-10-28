using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace DesktopPet;

// Positions in this module are physical desktop pixels, including negative monitor
// coordinates. No title text, process name, or window content is collected.
internal sealed record DesktopWindowSurface(IntPtr Handle, Rect Bounds, bool CanLand = true);
internal sealed record DesktopPlatform(IntPtr Handle, double Left, double Right, double Top, Rect OwnerBounds)
{
    public bool Contains(double x, double margin = 3) => x >= Left + margin && x <= Right - margin;
}
// Side is -1 for a window's left border and +1 for its right border. The pet
// faces the opposite direction while gripping that exposed side.
internal sealed record DesktopClimbEdge(IntPtr Handle, int Side, double X, double Top, double Bottom, Rect OwnerBounds);
internal sealed record DesktopPlatformSnapshot(IReadOnlyList<Rect> WorkAreas, IReadOnlyList<DesktopPlatform> Platforms,
    IReadOnlyList<DesktopClimbEdge>? ClimbEdges = null);

internal static class DesktopWindowPlatforms
{
    // Only the current carrier needs a fresh rectangle every display frame. The
    // more expensive front-to-back occlusion scan runs separately at 10 Hz.
    public static DesktopPlatformSnapshot RefreshSupport(DesktopPlatformSnapshot snapshot, IntPtr handle)
    {
        if (handle == IntPtr.Zero) return snapshot;
        Rect oldBounds = snapshot.Platforms.FirstOrDefault(p => p.Handle == handle)?.OwnerBounds
            ?? snapshot.ClimbEdges?.FirstOrDefault(p => p.Handle == handle)?.OwnerBounds ?? Rect.Empty;
        if (oldBounds.IsEmpty) return snapshot;
        if (!TryGetSurfaceBounds(handle, out var bounds))
            return new DesktopPlatformSnapshot(snapshot.WorkAreas, snapshot.Platforms.Where(p => p.Handle != handle).ToArray(),
                snapshot.ClimbEdges?.Where(p => p.Handle != handle).ToArray());
        double ratio = bounds.Width / Math.Max(1, oldBounds.Width);
        var platforms = snapshot.Platforms.Select(p => p.Handle != handle ? p : new DesktopPlatform(handle,
            bounds.Left + (p.Left - oldBounds.Left) * ratio,
            bounds.Left + (p.Right - oldBounds.Left) * ratio, bounds.Top, bounds)).ToArray();
        double heightRatio = bounds.Height / Math.Max(1, oldBounds.Height);
        var sides = snapshot.ClimbEdges?.Select(p => p.Handle != handle ? p : new DesktopClimbEdge(handle, p.Side,
            p.Side < 0 ? bounds.Left : bounds.Right,
            bounds.Top + (p.Top - oldBounds.Top) * heightRatio,
            bounds.Top + (p.Bottom - oldBounds.Top) * heightRatio, bounds)).ToArray();
        return new DesktopPlatformSnapshot(snapshot.WorkAreas, platforms, sides);
    }

    private static bool TryGetSurfaceBounds(IntPtr handle, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!IsWindowVisible(handle) || IsIconic(handle)) return false;
        if (DwmGetCloaked(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (DwmGetFrameBounds(handle, 9, out var rect, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(handle, out rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
        bounds = rect.ToRect();
        return true;
    }

    // EnumWindows supplies front-to-back ordering. A front window can cover part
    // of a rear top edge even when that front window is not a landing candidate.
    public static IReadOnlyList<DesktopPlatform> VisibleEdges(IReadOnlyList<DesktopWindowSurface> frontToBack)
    {
        var result = new List<DesktopPlatform>();
        for (int i = 0; i < frontToBack.Count; i++)
        {
            var window = frontToBack[i];
            if (!window.CanLand || window.Bounds.Width < 40 || window.Bounds.Height < 24) continue;
            var spans = new List<(double Left, double Right)> { (window.Bounds.Left, window.Bounds.Right) };
            for (int j = 0; j < i && spans.Count > 0; j++)
            {
                var cover = frontToBack[j].Bounds;
                // Probe just inside the top border, not an invisible resize shadow.
                if (cover.Top > window.Bounds.Top + 1 || cover.Bottom <= window.Bounds.Top + 1) continue;
                var next = new List<(double Left, double Right)>();
                foreach (var span in spans)
                {
                    if (cover.Right <= span.Left || cover.Left >= span.Right) { next.Add(span); continue; }
                    if (cover.Left > span.Left) next.Add((span.Left, Math.Min(span.Right, cover.Left)));
                    if (cover.Right < span.Right) next.Add((Math.Max(span.Left, cover.Right), span.Right));
                }
                spans = next;
            }
            foreach (var span in spans.Where(s => s.Right - s.Left >= 24))
                result.Add(new DesktopPlatform(window.Handle, span.Left, span.Right, window.Bounds.Top, window.Bounds));
        }
        return result;
    }

    public static IReadOnlyList<DesktopClimbEdge> VisibleSides(IReadOnlyList<DesktopWindowSurface> frontToBack)
    {
        var result = new List<DesktopClimbEdge>();
        for (int i = 0; i < frontToBack.Count; i++)
        {
            var window = frontToBack[i];
            if (!window.CanLand || window.Bounds.Width < 40 || window.Bounds.Height < 24) continue;
            foreach (int side in new[] { -1, 1 })
            {
                double x = side < 0 ? window.Bounds.Left : window.Bounds.Right;
                double probe = x - side; // one physical pixel inside the border
                var spans = new List<(double Top, double Bottom)> { (window.Bounds.Top, window.Bounds.Bottom) };
                for (int j = 0; j < i && spans.Count > 0; j++)
                {
                    var cover = frontToBack[j].Bounds;
                    if (probe < cover.Left || probe >= cover.Right) continue;
                    var next = new List<(double Top, double Bottom)>();
                    foreach (var span in spans)
                    {
                        if (cover.Bottom <= span.Top || cover.Top >= span.Bottom) { next.Add(span); continue; }
                        if (cover.Top > span.Top) next.Add((span.Top, Math.Min(span.Bottom, cover.Top)));
                        if (cover.Bottom < span.Bottom) next.Add((Math.Max(span.Top, cover.Bottom), span.Bottom));
                    }
                    spans = next;
                }
                foreach (var span in spans.Where(s => s.Bottom - s.Top >= 24))
                    result.Add(new DesktopClimbEdge(window.Handle, side, x, span.Top, span.Bottom, window.Bounds));
            }
        }
        return result;
    }

    public static DesktopPlatformSnapshot Capture(IntPtr petHandle)
    {
        var monitors = new List<Rect>();
        MonitorEnum monitorCallback = (IntPtr monitor, IntPtr dc, ref NativeRect unused, IntPtr state) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && info.Work.Right > info.Work.Left && info.Work.Bottom > info.Work.Top)
                monitors.Add(info.Work.ToRect());
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorCallback, IntPtr.Zero);
        var windows = new List<DesktopWindowSurface>();
        uint ownPid = (uint)Environment.ProcessId;
        WindowEnum callback = (handle, _) =>
        {
            if (handle == petHandle || !IsWindowVisible(handle) || IsIconic(handle)) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            if (pid == ownPid) return true;
            if (DwmGetCloaked(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var className = new StringBuilder(128);
            GetClassName(handle, className, className.Capacity);
            string name = className.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (DwmGetFrameBounds(handle, 9, out var rect, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(handle, out rect)) return true;
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            var bounds = rect.ToRect();
            if (!monitors.Any(m => m.IntersectsWith(bounds))) return true;
            // Menus/tooltips may hide a platform but are not platforms themselves.
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            bool canLand = (style & 0x80L) == 0 && name != "#32768" && name != "tooltips_class32";
            windows.Add(new DesktopWindowSurface(handle, bounds, canLand));
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        return new DesktopPlatformSnapshot(monitors, VisibleEdges(windows), VisibleSides(windows));
    }

    public static Rect NearestWorkArea(Rect bounds, IReadOnlyList<Rect> workAreas)
    {
        if (workAreas.Count == 0) return bounds;
        Point center = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        return workAreas.OrderByDescending(w => IntersectionArea(bounds, w))
            .ThenBy(w => DistanceSquared(center, w)).First();
    }

    public static Rect ClampToWorkAreas(Rect bounds, IReadOnlyList<Rect> workAreas)
    {
        var work = NearestWorkArea(bounds, workAreas);
        return new Rect(Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
            Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)), bounds.Width, bounds.Height);
    }

    public static Rect ClampBodyToWorkAreas(Rect window, Rect localBody, IReadOnlyList<Rect> workAreas)
    {
        if (workAreas.Count == 0) return window;
        var body = new Rect(window.Left + localBody.Left, window.Top + localBody.Top, localBody.Width, localBody.Height);
        var work = NearestWorkArea(body, workAreas);
        double minX = work.Left - localBody.Left, minY = work.Top - localBody.Top;
        double maxX = Math.Max(minX, work.Right - localBody.Right), maxY = Math.Max(minY, work.Bottom - localBody.Bottom);
        return new Rect(Math.Clamp(window.Left, minX, maxX), Math.Clamp(window.Top, minY, maxY), window.Width, window.Height);
    }

    private static double IntersectionArea(Rect a, Rect b)
    {
        var intersection = Rect.Intersect(a, b);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }
    private static double DistanceSquared(Point p, Rect r)
    {
        double dx = p.X - Math.Clamp(p.X, r.Left, r.Right), dy = p.Y - Math.Clamp(p.Y, r.Top, r.Bottom);
        return dx * dx + dy * dy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool WindowEnum(IntPtr hwnd, IntPtr state);
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowEnum callback, IntPtr state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr state);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetCloaked(IntPtr hwnd, int attribute, out int value, int bytes);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetFrameBounds(IntPtr hwnd, int attribute, out NativeRect value, int bytes);
}
