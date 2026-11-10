using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.WorkCompanion;

// Independent low-frequency sensor; never share the pet movement/landing loop.
// Native calls are read-only and execute off the UI thread. No input hooks,
// window enumeration, accessibility tree, screen capture, or process arguments.
internal sealed class WindowsWorkObserver : IDesktopObserver
{
    private const int MaximumTitleLength = 256;
    private static readonly HashSet<string> AlwaysExcluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "1password", "bitwarden", "keepass", "keepassxc", "lastpass", "dashlane",
        "protonpass", "enpass", "nordpass", "keeperpasswordmanager", "credentialuibroker", "logonui", "consent"
    };

    public Task<WorkObservation> ReadAsync(bool includeTitles, IReadOnlyList<string> excludedApps,
        CancellationToken cancellationToken)
    {
        // Capture configuration before crossing the thread boundary.
        var exclusions = new HashSet<string>(AlwaysExcluded, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < Math.Min(excludedApps.Count, 64); index++)
        {
            string name = NormalizeAppName(excludedApps[index]);
            if (name.Length > 0) exclusions.Add(name);
        }
        return Task.Run(() => Read(includeTitles, exclusions, cancellationToken), cancellationToken);
    }

    private static WorkObservation Read(bool includeTitles, HashSet<string> excludedApps,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        try
        {
            if (!OperatingSystem.IsWindows()) return WorkObservation.Unavailable(at, "unavailable");
            // OpenInputDesktop alone is insufficient: on a disconnected RDP
            // session it returns the desktop that would activate on reconnect.
            if (!Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint sessionId) || !IsActiveSession(sessionId))
                return WorkObservation.Unavailable(at, "session_inactive");
            if (!IsDefaultInputDesktop()) return WorkObservation.Unavailable(at, "secure_desktop");

            IntPtr window = Native.GetForegroundWindow();
            if (window == IntPtr.Zero || !Native.IsWindowVisible(window) || Native.IsIconic(window) ||
                Native.GetWindowThreadProcessId(window, out uint processId) == 0 || processId == 0 ||
                !Native.ProcessIdToSessionId(processId, out uint foregroundSession) || foregroundSession != sessionId)
                return WorkObservation.Unavailable(at, "foreground_unavailable");

            if (processId == (uint)Environment.ProcessId)
                return WorkObservation.Unavailable(at, "own_app") with { IsOwnApp = true };
            string app;
            using (var process = Process.GetProcessById(checked((int)processId))) app = process.ProcessName;
            if (string.IsNullOrWhiteSpace(app)) return WorkObservation.Unavailable(at, "foreground_unavailable");
            if (StringComparer.OrdinalIgnoreCase.Equals(app, "DesktopPet"))
                return WorkObservation.Unavailable(at, "own_app") with { IsOwnApp = true };
            // Exclusions precede GetWindowText, so even opt-in titles never read
            // a password manager's caption. Excluded app names are not returned.
            if (excludedApps.Contains(NormalizeAppName(app))) return WorkObservation.Unavailable(at, "excluded");

            var className = new StringBuilder(128);
            if (Native.GetClassName(window, className, className.Capacity) == 0)
                return WorkObservation.Unavailable(at, "foreground_unavailable");
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return WorkObservation.Unavailable(at, "desktop_shell");

            var input = new Native.LastInputInfo { Size = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
            if (!Native.GetLastInputInfo(ref input)) return WorkObservation.Unavailable(at, "unavailable");
            // DWORD arithmetic handles the normal GetTickCount rollover. Implausible
            // future input timestamps fail closed rather than resembling activity.
            uint idleMilliseconds = unchecked((uint)Environment.TickCount - input.Time);
            if (idleMilliseconds > int.MaxValue) return WorkObservation.Unavailable(at, "unavailable");
            if (!TryGetFullscreen(window, out bool fullscreen)) return WorkObservation.Unavailable(at, "unavailable");

            string? title = null;
            if (includeTitles && !fullscreen)
            {
                var buffer = new StringBuilder(MaximumTitleLength + 1);
                // Only the top-level foreground window, never controls/WM_GETTEXT.
                Native.GetWindowText(window, buffer, buffer.Capacity);
                title = SanitizeTitle(buffer.ToString());
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Do not publish a mixed observation across a focus/lock/RDP transition.
            if (Native.GetForegroundWindow() != window || !IsActiveSession(sessionId) || !IsDefaultInputDesktop())
                return WorkObservation.Unavailable(DateTimeOffset.UtcNow, "foreground_unavailable");
            return new WorkObservation(at, app, title,
                processId.ToString(CultureInfo.InvariantCulture) + ":" + window.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                TimeSpan.FromMilliseconds(idleMilliseconds), true, fullscreen, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or
            ArgumentException or System.Security.SecurityException or UnauthorizedAccessException or
            DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            // No raw exception, title, executable path, or other personal values
            // escape through diagnostics when a process disappears mid-read.
            return WorkObservation.Unavailable(at, "unavailable");
        }
    }

    internal static string NormalizeAppName(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.Length > 80 || name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0) return "";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    internal static bool IsAlwaysExcluded(string app) => AlwaysExcluded.Contains(NormalizeAppName(app));

    internal static string? SanitizeTitle(string value)
    {
        var clean = new StringBuilder(Math.Min(value.Length, MaximumTitleLength));
        foreach (char character in value)
        {
            if (char.IsControl(character)) continue;
            clean.Append(character);
            if (clean.Length >= MaximumTitleLength) break;
        }
        if (clean.Length > 0 && char.IsHighSurrogate(clean[^1])) clean.Length--;
        string result = clean.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private static bool IsActiveSession(uint sessionId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            return Native.WTSQuerySessionInformation(IntPtr.Zero, sessionId, 8, out buffer, out uint bytes) &&
                buffer != IntPtr.Zero && bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == 0; // WTSActive
        }
        finally { if (buffer != IntPtr.Zero) Native.WTSFreeMemory(buffer); }
    }

    private static bool IsDefaultInputDesktop()
    {
        IntPtr desktop = Native.OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS only
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return Native.GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) &&
                StringComparer.OrdinalIgnoreCase.Equals(name.ToString(), "Default");
        }
        finally { Native.CloseDesktop(desktop); }
    }

    private static bool TryGetFullscreen(IntPtr window, out bool fullscreen)
    {
        fullscreen = false;
        if (!Native.GetWindowRect(window, out var bounds)) return false;
        IntPtr monitor = Native.MonitorFromWindow(window, 2);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info) ||
            bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        // Compare the entire monitor, not its work area (ordinary maximized
        // windows leave room for the taskbar). Borderless/fullscreen is quiet.
        fullscreen = bounds.Left <= info.Monitor.Left + 2 && bounds.Top <= info.Monitor.Top + 2 &&
            bounds.Right >= info.Monitor.Right - 2 && bounds.Bottom >= info.Monitor.Bottom - 2;
        return true;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct LastInputInfo { public uint Size; public uint Time; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public Rect Monitor; public Rect Work; public uint Flags; }
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder value, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder value, int count);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int bytes, out int needed);
        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr info, out uint bytes);
        [DllImport("wtsapi32.dll")] internal static extern void WTSFreeMemory(IntPtr buffer);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr window, out Rect bounds);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
