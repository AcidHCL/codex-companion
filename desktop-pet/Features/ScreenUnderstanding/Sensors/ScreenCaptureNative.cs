using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPet.ScreenUnderstanding;

internal sealed record ScreenWindowSnapshot(uint ProcessId, IntPtr Window, string Identity, long ProcessStarted,
    Rectangle Client, Rectangle VisibleClient, bool Fullscreen);

internal static class ScreenCaptureNative
{
    private static readonly HashSet<string> ProtectedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktoppet", "1password", "bitwarden", "keepass", "keepassxc", "lastpass", "dashlane",
        "protonpass", "enpass", "nordpass", "keeperpasswordmanager", "credentialuibroker", "logonui", "consent"
    };

    internal static bool TrySnapshot(string app, string windowKey, bool allowFullscreen,
        IReadOnlyList<string> excluded, out ScreenWindowSnapshot? snapshot, out string status, bool fixture = false)
    {
        snapshot = null; status = "foreground_unavailable";
        try
        {
            if (!OperatingSystem.IsWindows() || !ScreenCapturePolicy.ValidApp(app) ||
                !ScreenCapturePolicy.TryWindowKey(windowKey, out uint expectedPid, out IntPtr window)) return false;
            if (!Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint session) || !ActiveSession(session) || !DefaultInputDesktop())
            { status = "session_unavailable"; return false; }
            if (Native.GetForegroundWindow() != window || !Native.IsWindowVisible(window) || Native.IsIconic(window) ||
                Native.GetWindowThreadProcessId(window, out uint pid) == 0 || pid != expectedPid ||
                !Native.ProcessIdToSessionId(pid, out uint targetSession) || targetSession != session) return false;
            // Fixtures are allowed only for our own PID and only through the
            // in-process test call. The serialized worker protocol has no bypass.
            bool ownFixture = fixture && pid == (uint)Environment.ProcessId;
            using var process = Process.GetProcessById(checked((int)pid));
            string name = ScreenCapturePolicy.NormalizeApp(process.ProcessName);
            if (!string.Equals(name, ScreenCapturePolicy.NormalizeApp(app), StringComparison.OrdinalIgnoreCase)) return false;
            if ((!ownFixture && (pid == (uint)Environment.ProcessId || ProtectedApps.Contains(name))) ||
                excluded.Any(value => string.Equals(ScreenCapturePolicy.NormalizeApp(value), name, StringComparison.OrdinalIgnoreCase)))
            { status = "excluded"; return false; }
            var className = new StringBuilder(128);
            if (Native.GetClassName(window, className, className.Capacity) == 0) return false;
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            { status = "desktop_shell"; return false; }
            if (Native.GetWindowDisplayAffinity(window, out uint affinity) && affinity != 0)
            { status = "protected_window"; return false; }
            if (Native.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            if (!Native.GetClientRect(window, out var client) || !Native.ClientToScreen(window, ref client.TopLeft) ||
                !Native.ClientToScreen(window, ref client.BottomRight)) return false;
            Rectangle clientBounds = client.Rectangle;
            if (clientBounds.Width < 16 || clientBounds.Height < 16 || (long)clientBounds.Width * clientBounds.Height > ScreenCapturePolicy.MaximumCapturePixels)
            { status = "window_size_unavailable"; return false; }
            Rectangle desktopBounds = new(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
            Rectangle visible = Rectangle.Intersect(clientBounds, desktopBounds);
            if (visible.Width < 16 || visible.Height < 16) return false;
            var monitor = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor)) return false;
            Rectangle full = monitor.Monitor.Rectangle;
            bool fullscreen = clientBounds.Left <= full.Left + 2 && clientBounds.Top <= full.Top + 2 &&
                clientBounds.Right >= full.Right - 2 && clientBounds.Bottom >= full.Bottom - 2;
            if (fullscreen && !allowFullscreen) { status = "fullscreen_quiet"; return false; }
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) { status = "identity_unavailable"; return false; }
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant() + "\0" + name)));
            long started = process.StartTime.ToUniversalTime().Ticks;
            if (Native.GetForegroundWindow() != window) return false;
            snapshot = new(pid, window, identity, started, clientBounds, visible, fullscreen);
            status = "available"; return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or
            System.Security.SecurityException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or OverflowException)
        { return false; }
    }

    internal static ScreenCaptureResult Capture(ScreenCaptureRequest request, bool fixture = false)
    {
        if (!ScreenCapturePolicy.ValidRequest(request)) return ScreenCaptureResult.Unavailable("invalid_request");
        // All native coordinates, UIA bounds and pixels use physical pixels.
        IntPtr previousDpi = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (!TrySnapshot(request.App, request.WindowKey, request.AllowFullscreen, request.ExcludedApps, out var before, out string status, fixture))
                return ScreenCaptureResult.Unavailable(status);
            var watch = Stopwatch.StartNew();
            var accessibility = ScreenWindowText.Read(before!, request.IncludeText, watch);
            if (!accessibility.Safe) return ScreenCaptureResult.Unavailable(accessibility.Status);
            string text = request.IncludeText ? accessibility.Text : "";
            string? image = null;
            if (request.IncludeImage)
            {
                using var pixels = CapturePixels(before!);
                if (pixels != null) image = Encode(pixels);
            }
            if (!TrySnapshot(request.App, request.WindowKey, request.AllowFullscreen, request.ExcludedApps, out var after, out _, fixture) ||
                before != after) return ScreenCaptureResult.Unavailable("foreground_changed");
            // Check provider protection again: a password dialog/field may have
            // appeared without changing the root HWND during image capture.
            if (!ScreenWindowText.Read(before!, false, watch).Safe)
                return ScreenCaptureResult.Unavailable("protected_content");
            if (text.Length == 0 && image == null) return ScreenCaptureResult.Unavailable("content_unavailable");
            return new(true, image == null && request.IncludeImage ? "text_only" : "captured", text, image,
                request.WindowKey, before!.Identity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ScreenCaptureResult.Unavailable("capture_unavailable"); }
        finally { if (previousDpi != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previousDpi); }
    }

    private static Bitmap? CapturePixels(ScreenWindowSnapshot snapshot)
    {
        using var print = new Bitmap(snapshot.Client.Width, snapshot.Client.Height, PixelFormat.Format24bppRgb);
        bool printed;
        using (var graphics = Graphics.FromImage(print))
        {
            graphics.Clear(Color.Black);
            IntPtr dc = graphics.GetHdc();
            try { printed = Native.PrintWindow(snapshot.Window, dc, 1); } // PW_CLIENTONLY
            finally { graphics.ReleaseHdc(dc); }
        }
        var source = new Rectangle(snapshot.VisibleClient.X - snapshot.Client.X, snapshot.VisibleClient.Y - snapshot.Client.Y,
            snapshot.VisibleClient.Width, snapshot.VisibleClient.Height);
        if (printed)
        {
            var crop = print.Clone(source, PixelFormat.Format24bppRgb);
            if (!Blank(crop)) return crop;
            crop.Dispose();
        }

        // GPU-backed apps sometimes cannot paint into PrintWindow. A fallback
        // copies only the target's visible client rectangle, never the desktop.
        // Transparent windows could expose unrelated apps behind them: decline.
        if ((Native.GetWindowLongPtr(snapshot.Window, -20).ToInt64() & 0x00080000) != 0) return null;
        if (!HigherWindows(snapshot.Window, snapshot.VisibleClient, out var before)) return null;
        var visible = new Bitmap(snapshot.VisibleClient.Width, snapshot.VisibleClient.Height, PixelFormat.Format24bppRgb);
        try
        {
            using (var graphics = Graphics.FromImage(visible))
            {
                graphics.CopyFromScreen(snapshot.VisibleClient.Location, Point.Empty, snapshot.VisibleClient.Size, CopyPixelOperation.SourceCopy);
                if (!HigherWindows(snapshot.Window, snapshot.VisibleClient, out var after)) { visible.Dispose(); return null; }
                // Mask both snapshots so moved overlays cannot leave old pixels.
                foreach (Rectangle overlay in before.Concat(after))
                    graphics.FillRectangle(Brushes.Black, overlay.X - snapshot.VisibleClient.X,
                        overlay.Y - snapshot.VisibleClient.Y, overlay.Width, overlay.Height);
            }
            if (Blank(visible)) { visible.Dispose(); return null; }
            return visible;
        }
        catch { visible.Dispose(); throw; }
    }

    private static bool HigherWindows(IntPtr target, Rectangle region, out List<Rectangle> overlaps)
    {
        overlaps = new();
        IntPtr window = Native.GetTopWindow(IntPtr.Zero);
        var seen = new HashSet<IntPtr>();
        for (int count = 0; window != IntPtr.Zero && count < 256 && seen.Add(window); count++, window = Native.GetWindow(window, 2))
        {
            if (window == target) return true;
            if (!Native.IsWindowVisible(window) || Native.IsIconic(window)) continue;
            if (Native.DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!Native.GetWindowRect(window, out var rect)) return false;
            Rectangle intersection = Rectangle.Intersect(region, rect.Rectangle);
            if (!intersection.IsEmpty) overlaps.Add(intersection);
        }
        return false;
    }

    internal static bool Blank(Bitmap image)
    {
        int minimum = 255, maximum = 0;
        for (int y = 0; y < image.Height; y += Math.Max(1, image.Height / 24))
            for (int x = 0; x < image.Width; x += Math.Max(1, image.Width / 24))
            {
                Color pixel = image.GetPixel(x, y);
                int brightness = (pixel.R + pixel.G + pixel.B) / 3;
                minimum = Math.Min(minimum, brightness); maximum = Math.Max(maximum, brightness);
            }
        return maximum - minimum < 3;
    }

    internal static string? Encode(Bitmap source)
    {
        double scale = Math.Min(1, ScreenCapturePolicy.MaximumImageEdge / (double)Math.Max(source.Width, source.Height));
        using var small = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, small.Width, small.Height));
        }
        var encoder = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
        foreach (long quality in new long[] { 85, 65, 45 })
        {
            using var bytes = new MemoryStream();
            using var options = new EncoderParameters(1);
            options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            small.Save(bytes, encoder, options);
            if (bytes.Length <= ScreenCapturePolicy.MaximumImageBytes) return Convert.ToBase64String(bytes.ToArray());
        }
        return null;
    }

    private static bool ActiveSession(uint session)
    {
        IntPtr buffer = IntPtr.Zero;
        try { return Native.WTSQuerySessionInformation(IntPtr.Zero, session, 8, out buffer, out uint size) && buffer != IntPtr.Zero && size >= 4 && Marshal.ReadInt32(buffer) == 0; }
        finally { if (buffer != IntPtr.Zero) Native.WTSFreeMemory(buffer); }
    }
    private static bool DefaultInputDesktop()
    {
        IntPtr desktop = Native.OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return Native.GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { Native.CloseDesktop(desktop); }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public Point TopLeft, BottomRight;
            public Rectangle Rectangle => System.Drawing.Rectangle.FromLTRB(TopLeft.X, TopLeft.Y, BottomRight.X, BottomRight.Y);
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int result, int length);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] internal static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, int bytes, out int required);
        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] internal static extern void WTSFreeMemory(IntPtr buffer);
    }
}
