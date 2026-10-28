using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet;

// Authenticate the OS-reported server before sending any RPC data. A matching pipe
// name/catalog is not an identity check. No trust bypass or network lookup exists.
internal static class CodexPipeTrust
{
    private const string Publisher = "OpenAI OpCo, LLC";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const uint WaitTimeout = 0x00000102;

    internal static void Validate(NamedPipeClientStream pipe)
    {
        try { ValidateCore(pipe); }
        catch (Exception ex)
        {
            // A caller must never accidentally reuse an unauthenticated stream.
            pipe.Dispose();
            if (ex is UnauthorizedAccessException) throw;
            throw new UnauthorizedAccessException("无法验证 Codex 本机接口身份；已拒绝连接。", ex);
        }
    }

    private static void ValidateCore(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows() || !pipe.IsConnected)
            throw Denied("管道尚未连接");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid == 0)
            throw NativeFailure();
        // Holding the process object avoids silently inspecting a reused PID.
        using var process = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, serverPid);
        if (process.IsInvalid) throw NativeFailure();
        EnsureRunning(process);
        if (!OpenProcessToken(process, TokenQuery, out var token)) throw NativeFailure();
        using (token)
        {
            using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            string owner = ReadUserSid(token);
            if (current.User == null || !string.Equals(owner, current.User.Value, StringComparison.Ordinal))
                throw Denied("接口不属于当前 Windows 用户");
            uint session = ReadSessionId(token);
            using var self = Process.GetCurrentProcess();
            if (session != (uint)self.SessionId) throw Denied("接口不属于当前 Windows 登录会话");
        }
        string imagePath = QueryImagePath(process);
        if (!IsAllowedInstallPath(imagePath)) throw Denied("接口程序不在受支持的 Codex 安装位置");
        if (IsWithin(imagePath, StoreRoot)) ValidateStorePackage(process);
        // Deny write/delete sharing while inspecting the exact file handle supplied
        // to WinVerifyTrust, rather than separately reopening the signer by path.
        using var file = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string finalPath = FinalPath(file.SafeFileHandle);
        if (!string.Equals(imagePath, finalPath, StringComparison.OrdinalIgnoreCase) || !IsAllowedInstallPath(finalPath))
            throw Denied("接口程序路径发生重定向");
        ValidateAuthenticode(file, finalPath);
        EnsureRunning(process);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint finalPid) || finalPid != serverPid ||
            !string.Equals(QueryImagePath(process), imagePath, StringComparison.OrdinalIgnoreCase))
            throw Denied("验证期间接口进程已变化");
    }

    internal static bool IsAllowedInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        string full = Path.GetFullPath(path);
        string localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex");
        if (IsWithin(full, localRoot)) return string.Equals(Path.GetFileName(full), "Codex.exe", StringComparison.OrdinalIgnoreCase);
        // Store installations must be inside the specifically named package, and
        // still pass the same publisher/signature check. Other WindowsApps are denied.
        if (!IsWithin(full, StoreRoot)) return false;
        string relative = Path.GetRelativePath(StoreRoot, full);
        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        if (separator <= 0) return false;
        string packageDirectory = relative.Substring(0, separator);
        string executable = relative.Substring(separator + 1);
        return packageDirectory.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
            packageDirectory.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(executable, @"app\ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(executable, @"app\Codex.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static string StoreRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    private static void ValidateStorePackage(SafeProcessHandle process)
    {
        // The actual packaged desktop app currently hosts AppTools in ChatGPT.exe.
        // Validate its OS package identity, not just a matching directory name.
        uint size = 0;
        if (GetPackageFamilyName(process, ref size, null) != 122 || size == 0 || size > 1024)
            throw Denied("无法确认 Codex 商店应用包身份");
        var family = new StringBuilder((int)size);
        if (GetPackageFamilyName(process, ref size, family) != 0 ||
            !string.Equals(family.ToString(), "OpenAI.Codex_2p2nqsd0c76g0", StringComparison.Ordinal))
            throw Denied("接口程序不是受信任的 Codex 商店应用包");
    }

    private static bool IsWithin(string path, string directory) =>
        path.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void EnsureRunning(SafeProcessHandle process)
    {
        if (WaitForSingleObject(process, 0) != WaitTimeout) throw Denied("接口进程已经退出");
    }

    private static string QueryImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(32768);
        uint capacity = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref capacity)) throw NativeFailure();
        return Path.GetFullPath(buffer.ToString());
    }

    private static string FinalPath(SafeFileHandle file)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw NativeFailure();
        string result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw Denied("接口程序不是本地文件");
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result.Substring(4);
        return Path.GetFullPath(result);
    }

    private static string ReadUserSid(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, 1, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 65536) throw NativeFailure();
        IntPtr buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!GetTokenInformation(token, 1, buffer, required, out _)) throw NativeFailure();
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint ReadSessionId(SafeAccessTokenHandle token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!GetTokenInformation(token, 12, buffer, sizeof(uint), out uint read) || read != sizeof(uint)) throw NativeFailure();
            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Internal only so the security checks can exercise unsigned/tampered files
    // independently of installation-path rejection. Production always uses Validate.
    internal static void ValidateAuthenticode(FileStream file, string path)
    {
        var fileInfo = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), Path = Marshal.StringToCoTaskMemUni(path), File = file.SafeFileHandle.DangerousGetHandle() };
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, filePointer, false);
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = filePointer, StateAction = 1,
            // Offline Authenticode policy, with cached whole-chain revocation
            // (excluding the root) and obsolete digest rejection. A missing or
            // stale revocation cache is a failure, never an implicit success.
            ProviderFlags = 0x1000 | 0x0080 | 0x2000
        };
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            // WinVerifyTrust returns a LONG, not an HRESULT. Only zero is success.
            if (status != 0) throw Denied("接口程序的 Windows 代码签名或离线证书链验证失败（0x" + unchecked((uint)status).ToString("X8") + "）");
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            IntPtr certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero || Marshal.ReadInt32(certificate) < Marshal.SizeOf<ProviderCertificateHeader>())
                throw Denied("无法取得已验证签名的发布者");
            // Read the signer from the successful WinVerifyTrust state, not from
            // an unrelated embedded certificate / second path-based parse.
            var cert = Marshal.PtrToStructure<ProviderCertificateHeader>(certificate);
            if (cert.Certificate == IntPtr.Zero ||
                !string.Equals(CertificateAttribute(cert.Certificate, "2.5.4.10"), Publisher, StringComparison.Ordinal) ||
                !string.Equals(CertificateAttribute(cert.Certificate, "2.5.4.3"), Publisher, StringComparison.Ordinal))
                throw Denied("接口程序的已验证签名不属于 OpenAI 官方发布者");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(fileInfo.Path);
            GC.KeepAlive(file);
        }
    }

    private static string CertificateAttribute(IntPtr certificate, string oid)
    {
        IntPtr oidPointer = Marshal.StringToCoTaskMemAnsi(oid);
        try
        {
            uint size = CertGetNameString(certificate, 3, 0, oidPointer, null, 0);
            if (size <= 1 || size > 1024) return "";
            var text = new StringBuilder((int)size);
            return CertGetNameString(certificate, 3, 0, oidPointer, text, size) == size ? text.ToString() : "";
        }
        finally { Marshal.FreeCoTaskMem(oidPointer); }
    }

    private static UnauthorizedAccessException Denied(string reason) => new("Codex 安全校验拒绝连接：" + reason + "。");
    private static Win32Exception NativeFailure() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo { public uint Size; public IntPtr Path; public IntPtr File; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size; public IntPtr PolicyCallback; public IntPtr SipClient; public uint UiChoice; public uint RevocationChecks;
        public uint UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr Url;
        public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificateHeader { public uint Size; public IntPtr Certificate; }

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);
    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint size, StringBuilder? family);
    [DllImport("advapi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, uint length, out uint returnLength);
    [DllImport("wintrust.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
    [DllImport("crypt32.dll", EntryPoint = "CertGetNameStringW", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CertGetNameString(IntPtr certificate, uint type, uint flags, IntPtr typeParameter, StringBuilder? name, uint size);
}
