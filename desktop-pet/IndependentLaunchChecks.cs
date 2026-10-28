using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace DesktopPet;

// Real process-boundary checks. The kill-on-close job contains only an isolated
// probe host; neither the interactive pet nor Codex is ever stopped by this test.
internal static class IndependentLaunchChecks
{
    private static readonly string[] Keys = { "CODEX_THREAD_ID", "CODEX_TURN_ID", "CODEX_APP_TOOLS_PIPE_PATH" };
    private const string FixtureValue = "independent-launch-fixture-context";

    internal static int? HandleProbe(string[] args)
    {
        if (args.Contains("--desktop-environment-test", StringComparer.Ordinal)) return WriteEnvironmentReport();
        if (args.Contains("--independent-launch-test", StringComparer.Ordinal)) return Run();
        int host = Array.IndexOf(args, "--independent-launch-host");
        if (host >= 0) return Host(ProbeDirectory(args, host));
        int child = Array.IndexOf(args, "--independent-launch-child");
        if (child >= 0)
        {
            string root = ProbeDirectory(args, child);
            if (child + 2 >= args.Length || args[child + 2] is not ("stripped" or "preserved")) return 3;
            return Child(root, args[child + 2]);
        }
        return null;
    }

    private static string ProbeDirectory(string[] args, int index)
    {
        if (index + 1 >= args.Length) throw new ArgumentException("Missing fixture directory.");
        string path = Path.GetFullPath(args[index + 1]).TrimEnd(Path.DirectorySeparatorChar);
        string parent = Path.GetDirectoryName(path) ?? "";
        if (!string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("independent-launch-test-", StringComparison.Ordinal) ||
            !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Invalid fixture directory.");
        return path;
    }

    private static int WriteEnvironmentReport()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "desktop-environment-validation.json");
        try
        {
            using var self = Process.GetCurrentProcess();
            bool independent = IndependentProcessLauncher.IsIndependentContext();
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet");
            Write(output, new { passed = independent, version = AppVersion.Number, pid = self.Id, sessionId = self.SessionId,
                independentContext = independent, protectedRecordExists = File.Exists(Path.Combine(root, "connection.dat")),
                revocationExists = File.Exists(Path.Combine(root, "connection.revoked")),
                inheritedCodexContextExcluded = Keys.All(key => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) });
            return independent ? 0 : 1;
        }
        catch (Exception ex) { Write(output, new { passed = false, errorType = ex.GetType().Name, errorHResult = ex.HResult }); return 1; }
    }

    private static int Host(string root)
    {
        if (!WaitFile(Path.Combine(root, "host-in-job"), 15000)) return 3;
        foreach (string key in Keys) Environment.SetEnvironmentVariable(key, FixtureValue);
        using var stripped = IndependentProcessLauncher.LaunchCurrent(new[] { "--independent-launch-child", root, "stripped" });
        using var preserved = IndependentProcessLauncher.LaunchCurrent(new[] { "--independent-launch-child", root, "preserved" }, preservePairingContext: true);
        Write(Path.Combine(root, "host-ready.json"), new { stripped = stripped.Id, preserved = preserved.Id });
        // The test owns this process and terminates its job. A timeout prevents a
        // forgotten probe remaining resident if the controller is interrupted.
        Thread.Sleep(30000);
        return 4;
    }

    private static int Child(string root, string mode)
    {
        using var self = Process.GetCurrentProcess();
        bool independent = IndependentProcessLauncher.IsIndependentContext();
        bool contextCorrect = Keys.All(key => mode == "preserved" ? Environment.GetEnvironmentVariable(key) == FixtureValue : string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)));
        Write(Path.Combine(root, mode + "-ready.json"), new { pid = self.Id, independent, contextCorrect });
        if (!WaitFile(Path.Combine(root, "verify-after-host-exit"), 25000)) return 4;
        Write(Path.Combine(root, mode + "-survived.json"), new { pid = self.Id, independent = IndependentProcessLauncher.IsIndependentContext(), contextCorrect, survivedHostJobClose = true });
        return independent && contextCorrect ? 0 : 1;
    }

    internal static int Run()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "independent-launch-validation.json");
        string root = Path.Combine(AppContext.BaseDirectory, "independent-launch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IntPtr job = IntPtr.Zero;
        Process? host = null;
        var children = new List<Process>();
        var checks = new List<string>();
        try
        {
            Check(!StartupModePolicy.IsIsolatedDiagnostic(new[] { "--unknown-test" }), "Unknown test-like argument cannot bypass independent launch", checks);
            Check(!StartupModePolicy.IsIsolatedDiagnostic(System.Array.Empty<string>()), "Normal launch must normalize its process environment", checks);
            Check(StartupModePolicy.IsIsolatedDiagnostic(new[] { "--self-test" }), "Known synthetic test retains isolated diagnostic routing", checks);
            Check(StartupModePolicy.UsesRealConnection(new[] { "--integration-test" }) &&
                !StartupModePolicy.IsIsolatedDiagnostic(new[] { "--integration-test" }), "Real integration test uses canonical pairing storage", checks);
            Check(!StartupModePolicy.IsIsolatedDiagnostic(new[] { "--pair-codex", "--self-test" }), "Real pairing requirement wins over a synthetic test flag", checks);
            Check(!StartupModePolicy.IsIsolatedDiagnostic(new[] { "--self-test", "--connection-test" }), "Real connection requirement wins regardless of flag order", checks);
            Check(IndependentProcessLauncher.Quote("") == "\"\"", "Empty arguments remain empty", checks);
            Check(IndependentProcessLauncher.Quote("a b") == "\"a b\"", "Paths with spaces stay one argument", checks);
            Check(IndependentProcessLauncher.Quote("a\\\"b") == "\"a\\\\\\\"b\"", "Embedded quote escaping is preserved", checks);
            Check(IndependentProcessLauncher.Quote("a\\") == "\"a\\\\\"", "Trailing path separator is preserved", checks);
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var limits = new ExtendedLimit(); limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<ExtendedLimit>(); IntPtr memory = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(limits, memory, false); if (!SetInformationJobObject(job, 9, memory, (uint)size)) throw new System.ComponentModel.Win32Exception(); }
            finally { Marshal.FreeHGlobal(memory); }
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--independent-launch-host"); start.ArgumentList.Add(root);
            host = Process.Start(start) ?? throw new InvalidOperationException("Probe host failed to start.");
            if (!AssignProcessToJobObject(job, host.Handle)) throw new System.ComponentModel.Win32Exception();
            File.WriteAllText(Path.Combine(root, "host-in-job"), "ready");
            Check(WaitFile(Path.Combine(root, "host-ready.json"), 12000), "Probe host launched both independent children", checks);
            foreach (string mode in new[] { "stripped", "preserved" })
            {
                string ready = Path.Combine(root, mode + "-ready.json");
                Check(WaitFile(ready, 10000), mode + " child started", checks);
                using var report = JsonDocument.Parse(File.ReadAllText(ready));
                Check(report.RootElement.GetProperty("independent").GetBoolean(), mode + " child has no inherited job or redirected files", checks);
                Check(report.RootElement.GetProperty("contextCorrect").GetBoolean(), mode + " child environment policy is correct", checks);
                var child = Process.GetProcessById(report.RootElement.GetProperty("pid").GetInt32());
                try
                {
                    // Keep the exact process object open before it exits; a
                    // PID-only Process cannot retrieve its exit code later.
                    _ = child.Handle;
                    children.Add(child);
                }
                catch { child.Dispose(); throw; }
            }
            CloseHandle(job); job = IntPtr.Zero;
            Check(host.WaitForExit(5000), "Closing the fixture job terminates only its host", checks);
            Check(children.All(child => !child.HasExited), "Both independent children survive the host job closing", checks);
            File.WriteAllText(Path.Combine(root, "verify-after-host-exit"), "verify");
            foreach (string mode in new[] { "stripped", "preserved" })
            {
                string reportPath = Path.Combine(root, mode + "-survived.json");
                Check(WaitFile(reportPath, 5000), mode + " child continues executing after host termination", checks);
                using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
                Check(report.RootElement.GetProperty("independent").GetBoolean() && report.RootElement.GetProperty("survivedHostJobClose").GetBoolean(), mode + " child remains in the normal user environment", checks);
            }
            Check(children.All(child => child.WaitForExit(5000) && child.ExitCode == 0), "Own child probes exit normally", checks);
            Write(output, new { passed = true, version = AppVersion.Number, assertions = checks.Count, checks, userPetOrCodexStopped = false, credentialsRead = false });
            return 0;
        }
        catch (Exception ex) { Write(output, new { passed = false, assertions = checks.Count, checks, errorType = ex.GetType().Name, error = ex.Message }); return 1; }
        finally
        {
            if (job != IntPtr.Zero) CloseHandle(job);
            File.WriteAllText(Path.Combine(root, "verify-after-host-exit"), "finish");
            foreach (var child in children) child.Dispose();
            if (host != null) { if (!host.HasExited) host.Kill(); host.Dispose(); }
        }
    }

    private static bool WaitFile(string path, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.ElapsedMilliseconds < milliseconds) Thread.Sleep(50);
        return File.Exists(path);
    }
    private static void Write(string path, object value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    private static void Check(bool condition, string description, List<string> checks) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
