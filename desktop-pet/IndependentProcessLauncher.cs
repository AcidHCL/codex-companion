using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet;

// Desktop Bridge can redirect a descendant's files even when it reports no
// package identity. Ask Windows to use this session's existing Explorer as the
// parent before opening any persistent state. No elevation or scheduled task.
internal static class IndependentProcessLauncher
{
    private const string Marker = "--independent-desktop-child";
    private const uint QueryProcess = 0x1000, CreateProcessRight = 0x80, Synchronize = 0x100000;
    private static readonly string[] ContextKeys = { "CODEX_THREAD_ID", "CODEX_TURN_ID", "CODEX_APP_TOOLS_PIPE_PATH" };

    internal static int? RelaunchIfNeeded(string[] args)
    {
        if (IsIndependentContext()) return null;
        if (args.Contains(Marker, StringComparer.Ordinal))
            throw new InvalidOperationException(L.T("桌宠未能进入独立的 Windows 桌面环境，请从解压目录直接启动 DesktopPet.exe。", "AI Pet could not enter an independent Windows desktop session. Launch DesktopPet.exe directly from the extracted folder."));
        bool pairing = args.Contains("--pair-codex", StringComparer.Ordinal);
        bool command = pairing || args.Contains("--connection-test", StringComparer.Ordinal) ||
            args.Contains("--desktop-environment-test", StringComparer.Ordinal) || args.Contains("--integration-test", StringComparer.Ordinal);
        using var child = LaunchCurrent(args.Append(Marker).ToArray(), pairing);
        if (command)
        {
            if (!child.WaitForExit(85000))
            {
                // Only the exact diagnostic/pairing process created here may be
                // stopped. Never terminate another pet or the Codex application.
                child.Kill();
                child.WaitForExit(5000);
                throw new TimeoutException(L.T("桌宠独立启动验证超时。", "Independent startup verification timed out."));
            }
            return child.ExitCode;
        }
        return child.WaitForExit(1500) ? child.ExitCode : 0;
    }

    internal static bool IsIndependentContext()
    {
        using var self = Process.GetCurrentProcess();
        if (!IsProcessInJob(self.Handle, IntPtr.Zero, out bool inJob)) throw NativeFailure();
        if (inJob || HasPackage(self.Handle)) return false;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet");
        foreach (string name in new[] { "settings.json", "connection.dat", "connection.revoked", "connection.migration-v1", "lifecycle.jsonl" })
        {
            string path = Path.Combine(root, name);
            // Opening for metadata only also catches files visible through the
            // Desktop Bridge merged view. Directory handles alone do not.
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) continue;
                throw new Win32Exception(error);
            }
            if (!string.Equals(FinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    internal static Process LaunchCurrent(string[] args, bool preservePairingContext = false)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException(L.T("找不到桌宠程序路径。", "Could not find the AI Pet executable path."));
        executable = Path.GetFullPath(executable);
        if (!string.Equals(Path.GetFileName(executable), "DesktopPet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("独立启动仅支持当前 DesktopPet.exe。", "Independent startup supports only the current DesktopPet.exe."));
        IntPtr window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint shellPid) == 0 || shellPid == 0)
            throw new InvalidOperationException(L.T("当前 Windows 桌面尚未就绪，请稍后再启动桌宠。", "The Windows desktop is not ready. Try starting the pet again shortly."));
        using var shell = OpenProcess(CreateProcessRight | QueryProcess | Synchronize, false, shellPid);
        if (shell.IsInvalid) throw NativeFailure();
        ValidateDesktopParent(shell);

        IntPtr bytes = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        if (bytes == IntPtr.Zero) throw NativeFailure();
        IntPtr list = Marshal.AllocHGlobal(bytes), parentValue = IntPtr.Zero, environment = IntPtr.Zero;
        bool initialized = false;
        ProcessInformation created = default;
        int environmentBytes = 0;
        Process? managedChild = null;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref bytes)) throw NativeFailure();
            initialized = true;
            parentValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parentValue, shell.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x20000, parentValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw NativeFailure();
            string block = BuildEnvironment(preservePairingContext);
            environment = Marshal.StringToHGlobalUni(block);
            environmentBytes = checked((block.Length + 1) * sizeof(char));
            var startup = new StartupInfoEx { Attributes = list };
            startup.Startup.Size = (uint)Marshal.SizeOf<StartupInfoEx>();
            startup.Startup.Flags = 1; // STARTF_USESHOWWINDOW
            startup.Startup.ShowWindow = 0; // No console/helper flash.
            string command = Quote(executable) + (args.Length == 0 ? "" : " " + string.Join(" ", args.Select(Quote)));
            // Suspended until the actual child has been checked. Inherited
            // handles are disabled; only its explicit environment carries the
            // pairing context, never the command line or a temporary file.
            const uint flags = 0x80000 | 0x400 | 0x08000000 | 0x4;
            if (!CreateProcessW(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                    flags, environment, AppContext.BaseDirectory, ref startup, out created)) throw NativeFailure();
            if (!IsProcessInJob(created.Process, IntPtr.Zero, out bool inJob)) throw NativeFailure();
            if (inJob || HasPackage(created.Process)) throw new InvalidOperationException(L.T("Windows 未能启动独立的桌宠进程。", "Windows could not start the independent pet process."));
            ValidateSameUserAndSession(created.Process, requireUnelevated: true);
            // Verify the parent is still the same desktop before starting code.
            if (WaitForSingleObject(shell, 0) != 258 || GetShellWindow() != window) throw new InvalidOperationException(L.T("Windows 桌面正在重启，请稍后重试。", "The Windows desktop is restarting. Try again shortly."));
            managedChild = Process.GetProcessById((int)created.ProcessId);
            _ = managedChild.Handle; // Hold the exact object before it can exit.
            if (ResumeThread(created.Thread) == uint.MaxValue) throw NativeFailure();
            return managedChild;
        }
        catch
        {
            if (created.Process != IntPtr.Zero) TerminateProcess(created.Process, 1);
            managedChild?.Dispose();
            throw;
        }
        finally
        {
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            if (environment != IntPtr.Zero)
            {
                // Environment blocks contain embedded NULs, so clear their full
                // allocation rather than only the first C-style string.
                for (int index = 0; index < environmentBytes; index++) Marshal.WriteByte(environment, index, 0);
                Marshal.FreeHGlobal(environment);
            }
            if (parentValue != IntPtr.Zero) Marshal.FreeHGlobal(parentValue);
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }

    private static void ValidateDesktopParent(SafeProcessHandle parent)
    {
        if (WaitForSingleObject(parent, 0) != 258) throw new InvalidOperationException(L.T("Windows 桌面进程已经退出。", "The Windows desktop process has exited."));
        var buffer = new StringBuilder(32768);
        uint size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(parent, 0, buffer, ref size)) throw NativeFailure();
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!string.Equals(Path.GetFullPath(buffer.ToString()), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("当前桌面宿主不是 Windows Explorer。", "The desktop host is not Windows Explorer."));
        using var file = CreateFile(expected, 0, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (file.IsInvalid || !string.Equals(FinalPath(file), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("Windows Explorer 的路径验证失败。", "Windows Explorer path verification failed."));
        ValidateSameUserAndSession(parent.DangerousGetHandle(), requireUnelevated: true);
        if (!IsProcessInJob(parent.DangerousGetHandle(), IntPtr.Zero, out bool inJob)) throw NativeFailure();
        if (inJob || HasPackage(parent.DangerousGetHandle())) throw new InvalidOperationException(L.T("Windows 桌面未处于独立运行环境。", "The Windows desktop is not running independently."));
    }

    private static void ValidateSameUserAndSession(IntPtr process, bool requireUnelevated)
    {
        if (!OpenProcessToken(process, 8, out var token)) throw NativeFailure();
        using (token)
        using (var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (current.User == null || identity.User != current.User)
                throw new InvalidOperationException(L.T("桌面进程不属于当前 Windows 用户。", "The desktop process does not belong to the current Windows user."));
            using var self = Process.GetCurrentProcess();
            if (ReadTokenInt(token, 12) != self.SessionId)
                throw new InvalidOperationException(L.T("桌面进程不属于当前 Windows 登录会话。", "The desktop process does not belong to the current Windows session."));
            if (requireUnelevated && ReadTokenInt(token, 20) != 0)
                throw new InvalidOperationException(L.T("请以普通用户方式启动桌宠，无需管理员权限。", "Start AI Pet as a standard user; administrator rights are not needed."));
        }
    }

    private static int ReadTokenInt(SafeAccessTokenHandle token, int kind)
    {
        IntPtr value = Marshal.AllocHGlobal(4);
        try { if (!GetTokenInformation(token, kind, value, 4, out _)) throw NativeFailure(); return Marshal.ReadInt32(value); }
        finally { Marshal.FreeHGlobal(value); }
    }

    private static bool HasPackage(IntPtr process)
    {
        uint size = 0;
        int result = GetPackageFullName(process, ref size, IntPtr.Zero);
        if (result == 15700) return false; // APPMODEL_ERROR_NO_PACKAGE
        if (result is 0 or 122) return true;
        throw new Win32Exception(result);
    }

    private static string BuildEnvironment(bool preservePairingContext)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            string key = (string)item.Key, value = (string?)item.Value ?? "";
            if (!preservePairingContext && ContextKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            if (key.Contains('\0') || value.Contains('\0')) throw new InvalidOperationException(L.T("进程环境变量无效。", "Invalid process environment variable."));
            entries[key] = value;
        }
        return string.Join("\0", entries.Select(entry => entry.Key + "=" + entry.Value)) + "\0\0";
    }

    internal static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException(L.T("命令行参数包含无效字符。", "A command-line argument contains invalid characters."));
        var output = new StringBuilder("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') output.Append('\\', slashes * 2 + 1).Append('"');
            else output.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint size = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (size == 0 || size >= buffer.Capacity) throw NativeFailure();
        string result = buffer.ToString();
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
    }

    private static Win32Exception NativeFailure() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public uint Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr Reserved2Pointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFullName(IntPtr process, ref uint size, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr value, uint length, out uint returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
