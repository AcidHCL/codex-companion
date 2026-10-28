using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DesktopPet;

internal readonly record struct GraphicsRecoveryAttempt(DateTimeOffset WindowStart, int Count)
{
    internal GraphicsRecoveryAttempt? Next(DateTimeOffset now)
    {
        // Moving the wall clock backwards must not replenish the retry budget.
        int count = now >= WindowStart && now - WindowStart >= TimeSpan.FromMinutes(10) ? 0 : Count;
        if (count >= 2) return null;
        return new GraphicsRecoveryAttempt(count == 0 || now < WindowStart ? now : WindowStart, count + 1);
    }
}

internal static class GraphicsRecovery
{
    private static GraphicsRecoveryAttempt _attempt;
    private static int _started;
    internal static bool IsRecoveryLaunch { get; private set; }
    internal static bool CompatibilityMode { get; private set; }

    // Runs before constructing any WPF Application/window. The child waits for
    // the failed parent to release its mutex; it never terminates another PID.
    internal static bool Initialize(string[] args)
    {
        int option = Array.IndexOf(args, "--graphics-recovery");
        if (option < 0) return true;
        if (option + 3 >= args.Length || !int.TryParse(args[option + 1], out int count) || count is < 1 or > 2 ||
            !long.TryParse(args[option + 2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ||
            !int.TryParse(args[option + 3], out int parentId) || parentId <= 0 || parentId == Environment.ProcessId) return false;
        DateTimeOffset start;
        try { start = new DateTimeOffset(ticks, TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (start > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1)) return false;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
            if (!parent.WaitForExit(15000)) return false;
        }
        catch (ArgumentException) { /* Parent already exited, as intended. */ }
        catch (InvalidOperationException) { /* Process exited while being read. */ }
        catch (System.ComponentModel.Win32Exception) { return false; }
        Thread.Sleep(1500); // Allow the desktop/device transition to settle.
        _attempt = new GraphicsRecoveryAttempt(start, count);
        IsRecoveryLaunch = true;
        CompatibilityMode = count >= 2;
        return true;
    }

    internal static ProcessStartInfo CreateRestartInfo(string executable, GraphicsRecoveryAttempt attempt, int parentId)
    {
        if (!string.Equals(Path.GetFullPath(executable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(executable), "DesktopPet.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Graphics recovery can only restart the current desktop pet executable.", nameof(executable));
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add("--graphics-recovery");
        start.ArgumentList.Add(attempt.Count.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(attempt.WindowStart.UtcTicks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parentId.ToString(CultureInfo.InvariantCulture));
        // A restarted desktop program discovers its saved, protected pairing.
        // Never inherit a transient Codex task's connection context.
        foreach (string key in new[] { "CODEX_THREAD_ID", "CODEX_TURN_ID", "CODEX_APP_TOOLS_PIPE_PATH" }) start.Environment.Remove(key);
        return start;
    }

    internal static bool TryBeginRecovery() => Interlocked.Exchange(ref _started, 1) == 0;

    internal static void ExitAndRecover(DesktopSettings? settings)
    {
        if (!TryBeginRecovery()) return;
        LifecycleLog.Record(LifecycleEvent.GraphicsRecoveryStarted);
        try
        {
            // No geometry reads, WPF Shutdown/Close, COM disposal, credential
            // copies or renderer calls after the render partition has failed.
            try { settings?.Save(); } catch (Exception ex) { Program.Log(ex); }
            var next = _attempt.Next(DateTimeOffset.UtcNow);
            string? executable = Environment.ProcessPath;
            if (next.HasValue && executable != null &&
                string.Equals(Path.GetFileName(executable), "DesktopPet.exe", StringComparison.OrdinalIgnoreCase))
            {
                using var child = Process.Start(CreateRestartInfo(executable, next.Value, Environment.ProcessId));
                LifecycleLog.Record(child != null ? LifecycleEvent.GraphicsRecoveryChildStarted : LifecycleEvent.GraphicsRecoveryUnavailable, child?.Id);
            }
            else LifecycleLog.Record(LifecycleEvent.GraphicsRecoveryUnavailable);
        }
        catch (Exception ex) { LifecycleLog.RecordException(LifecycleEvent.GraphicsRecoveryFailed, ex); Program.Log(ex); }
        finally
        {
            // This is only our current failed process. Waiting for WPF teardown
            // here can deadlock in SyncFlush and generate more modal errors.
            LifecycleLog.Record(LifecycleEvent.GraphicsRecoveryExit, 70);
            Environment.Exit(70);
        }
    }
}
