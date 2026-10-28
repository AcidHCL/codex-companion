using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet;

internal sealed record CodexConnectionHint(string Endpoint, string CallerThreadId, string CallerTurnId, DateTimeOffset SavedAtUtc);

// DPAPI is protection against accidental disclosure and other Windows users, not
// a boundary against malicious code already running as this Windows user.
internal sealed class CodexConnectionStore
{
    private const int MaximumFileBytes = 16384;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("AIPETC02");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopPet/CodexConnection/v2");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string root;
    private readonly string mutexName;
    private readonly bool sealLegacyMigration;
    private static readonly byte[] MigrationSeal = Encoding.ASCII.GetBytes("AIPet canonical pairing store v1\n");
    internal string RootDirectory => root;
    internal string MigrationSealPath => Path.Combine(root, "connection.migration-v1");
    public string ProtectedPath => Path.Combine(root, "connection.dat");
    public string LegacyPath => Path.Combine(root, "connection.json");
    public string RevocationPath => Path.Combine(root, "connection.revoked");
    public string? LastError { get; private set; }
    public CodexPairingState LastState { get; private set; } = CodexPairingState.Unknown;

    public CodexConnectionStore(string? rootDirectory = null)
    {
        sealLegacyMigration = rootDirectory == null;
        root = Path.GetFullPath(rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet"));
        mutexName = @"Local\AIPetConnectionStore-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Substring(0, 32);
    }

    // Retain the fail-closed boolean for callers that only need a safety gate.
    // Diagnostic/polling callers must not interpret an unreadable store as revocation.
    public bool IsRevoked => CheckRevocation() != CodexPairingState.Ready;
    public CodexPairingState CheckRevocation()
    {
        LastError = null;
        try
        {
            using var gate = Enter();
            using var parents = PinDirectories(false);
            if (EntryExists(RevocationPath))
            {
                ValidateExisting(RevocationPath, true);
                return LastState = CodexPairingState.Revoked;
            }
            return LastState = CodexPairingState.Ready;
        }
        catch (Exception ex) when (IsStorageError(ex)) { RecordFailure(ex); return LastState; }
    }

    public CodexConnectionHint? Read() => Load();
    public CodexConnectionHint? Load()
    {
        LastError = null;
        try
        {
            using var gate = Enter();
            using var parents = PinDirectories(false);
            if (EntryExists(RevocationPath)) { ValidateExisting(RevocationPath, true); LastState = CodexPairingState.Revoked; return null; }
            if (EntryExists(ProtectedPath))
            {
                byte[] protectedBytes = ReadBounded(ProtectedPath, requirePrivateAcl: true);
                if (protectedBytes.Length <= Header.Length || !protectedBytes.AsSpan(0, Header.Length).SequenceEqual(Header)) throw new InvalidDataException();
                byte[] clear = UnprotectForCurrentUser(protectedBytes.AsSpan(Header.Length).ToArray());
                try
                {
                    var hint = JsonSerializer.Deserialize<CodexConnectionHint>(clear, JsonOptions);
                    return AcceptLoadedHint(hint);
                }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            // A legacy record is only a candidate for a separately verified connection.
            // Reading never migrates it and a damaged v2 file never falls back to v1.
            if (!EntryExists(LegacyPath)) { LastState = CodexPairingState.Missing; return null; }
            byte[] legacy = ReadBounded(LegacyPath, requirePrivateAcl: false);
            try
            {
                ReadOnlySpan<byte> json = legacy.AsSpan();
                if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json.Slice(3);
                using var document = JsonDocument.Parse(json.ToArray());
                var candidate = JsonSerializer.Deserialize<LegacyHint>(document.RootElement.GetRawText(), JsonOptions);
                var hint = candidate == null ? null : new CodexConnectionHint(candidate.Endpoint, candidate.CallerThreadId, candidate.CallerTurnId, candidate.SavedAtUtc);
                if (candidate?.Version != 1) throw new InvalidDataException();
                return AcceptLoadedHint(hint);
            }
            finally { CryptographicOperations.ZeroMemory(legacy); }
        }
        catch (Exception ex) when (IsStorageError(ex)) { RecordFailure(ex); return null; }
    }

    // Call only after server identity and read-only protocol verification succeed.
    // Explicit user pairing is the only operation permitted to clear revocation.
    public bool SaveVerified(CodexConnectionHint hint, bool allowReactivation = false)
    {
        LastError = null;
        try
        {
            if (!Valid(hint)) throw new InvalidDataException();
            using var gate = Enter();
            using var parents = PinDirectories(true);
            if (EntryExists(RevocationPath) && !allowReactivation)
            { LastState = CodexPairingState.Revoked; LastError = "连接已清除，请重新主动配对。"; return false; }
            ValidateExisting(ProtectedPath, true);
            ValidateExisting(LegacyPath, false);
            ValidateExisting(RevocationPath, true);
            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(hint, JsonOptions);
            byte[] encrypted;
            try { encrypted = ProtectForCurrentUser(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            byte[] bytes = new byte[Header.Length + encrypted.Length];
            Header.CopyTo(bytes, 0); encrypted.CopyTo(bytes, Header.Length);
            // Once this canonical store has been paired, losing a record must not
            // silently restore an obsolete record from a former packaged scope.
            if (sealLegacyMigration) WriteMigrationSealCore();
            WriteAtomic(ProtectedPath, bytes);
            // Keep the revoke marker until all persistent state is successfully updated.
            DeleteValidated(LegacyPath, false);
            if (allowReactivation) DeleteValidated(RevocationPath, true);
            LastState = CodexPairingState.Ready; return true;
        }
        catch (Exception ex) when (IsStorageError(ex)) { RecordFailure(ex); return false; }
    }

    public void Forget()
    {
        LastError = null;
        try
        {
            using var gate = Enter();
            using var parents = PinDirectories(true);
            ValidateExisting(RevocationPath, true);
            // Persist this first so failed deletion or an outstanding old request
            // cannot silently re-enable the stored context on the next launch.
            WriteAtomic(RevocationPath, Encoding.UTF8.GetBytes("AIPet connection revoked\n"));
            LastState = CodexPairingState.Revoked;
            if (sealLegacyMigration) WriteMigrationSealCore();
            DeleteValidated(ProtectedPath, true);
            DeleteValidated(LegacyPath, false);
        }
        catch (Exception ex) when (IsStorageError(ex))
        { LastError = SafeStorageError; throw new IOException("无法安全清除连接记录；已停止连接，请检查本机文件权限。", ex); }
    }

    // Migration coordination uses the same mutex as every normal read/save/revoke.
    // It never grants access to an unvalidated file or to decrypted bytes.
    internal IDisposable LockForMigration() => Enter();
    internal bool ProtectedRecordExistsForMigration() => EntryExists(ProtectedPath);
    internal void RecordMigrationFailure(Exception error) => RecordFailure(error);
    internal bool TryReadMigrationSeal(out bool isSealed)
    {
        isSealed = false;
        try
        {
            using var gate = Enter();
            using var parents = PinDirectories(false);
            if (!EntryExists(MigrationSealPath)) return true;
            if (!ReadBounded(MigrationSealPath, true).AsSpan().SequenceEqual(MigrationSeal)) throw new InvalidDataException();
            isSealed = true;
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex)) { RecordFailure(ex); return false; }
    }
    internal bool TrySealLegacyMigration()
    {
        try
        {
            using var gate = Enter();
            using var parents = PinDirectories(true);
            WriteMigrationSealCore();
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex)) { RecordFailure(ex); return false; }
    }
    private void WriteMigrationSealCore()
    {
        if (EntryExists(MigrationSealPath))
        {
            if (!ReadBounded(MigrationSealPath, true).AsSpan().SequenceEqual(MigrationSeal)) throw new InvalidDataException();
            return;
        }
        WriteAtomic(MigrationSealPath, MigrationSeal);
    }

    private CodexConnectionHint? AcceptLoadedHint(CodexConnectionHint? hint)
    {
        if (!ValidStructure(hint)) throw new InvalidDataException();
        if (hint!.SavedAtUtc < DateTimeOffset.UtcNow.AddDays(-90))
        {
            LastState = CodexPairingState.Expired;
            LastError = "连接记录已过期，请在 Codex 中主动重新配对。";
            return null;
        }
        LastState = CodexPairingState.Ready; return hint;
    }
    private static bool Valid(CodexConnectionHint? hint) => ValidStructure(hint) && hint!.SavedAtUtc >= DateTimeOffset.UtcNow.AddDays(-90);
    private static bool ValidStructure(CodexConnectionHint? hint) => hint != null &&
        CodexEndpointPolicy.TryNormalize(hint.Endpoint, out _) &&
        Guid.TryParseExact(hint.CallerThreadId, "D", out _) && Guid.TryParseExact(hint.CallerTurnId, "D", out _) &&
        hint.SavedAtUtc != default && hint.SavedAtUtc <= DateTimeOffset.UtcNow.AddMinutes(10);

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static byte[] ReadBounded(string path, bool requirePrivateAcl)
    {
        using var stream = OpenChecked(path, requirePrivateAcl);
        if (stream.Length == 0 || stream.Length > MaximumFileBytes) throw new InvalidDataException();
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static FileStream OpenChecked(string path, bool requirePrivateAcl)
    {
        // OPEN_REPARSE_POINT opens the link itself, so validation occurs before any
        // bytes are read from a possible redirected target. Deny delete sharing.
        var handle = CreateFile(path, 0x80020000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            CheckHandle(handle, directory: false);
            var stream = new FileStream(handle, FileAccess.Read);
            try
            {
                if (requirePrivateAcl) ValidatePrivateAcl(stream.GetAccessControl());
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateExisting(string path, bool requirePrivateAcl)
    { if (EntryExists(path)) { using var stream = OpenChecked(path, requirePrivateAcl); if (stream.Length > MaximumFileBytes) throw new InvalidDataException(); } }

    private static void DeleteValidated(string path, bool requirePrivateAcl)
    { if (EntryExists(path)) { ValidateExisting(path, requirePrivateAcl); File.Delete(path); } }

    private void WriteAtomic(string target, byte[] bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException();
        string temporary = Path.Combine(root, ".connection-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, PrivateAcl()))
            {
                CheckHandle(file.SafeFileHandle, false);
                ValidatePrivateAcl(file.GetAccessControl());
                file.Write(bytes); file.Flush(true);
            }
            ValidateExisting(target, true);
            File.Move(temporary, target, overwrite: true);
            ValidateExisting(target, true);
        }
        finally { if (EntryExists(temporary)) File.Delete(temporary); }
    }

    private static FileSecurity PrivateAcl()
    {
        SecurityIdentifier current = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
        var security = new FileSecurity();
        security.SetOwner(current);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static void ValidatePrivateAcl(FileSecurity security)
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException();
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier))?.Value != user) throw new UnauthorizedAccessException();
        bool userAccess = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            string sid = rule.IdentityReference.Value;
            if (sid != user && sid != "S-1-5-18") throw new UnauthorizedAccessException();
            if (sid == user && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) userAccess = true;
        }
        if (!userAccess) throw new UnauthorizedAccessException();
    }

    private PinnedDirectories PinDirectories(bool create)
    {
        var pinned = new PinnedDirectories();
        try
        {
            string? drive = Path.GetPathRoot(root);
            if (drive == null || drive.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException();
            string current = drive;
            foreach (string segment in root.Substring(drive.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!EntryExists(current))
                {
                    if (!create) break;
                    Directory.CreateDirectory(current);
                }
                var handle = CreateFile(current, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                pinned.Handles.Add(handle);
                CheckHandle(handle, directory: true);
            }
            return pinned;
        }
        catch { pinned.Dispose(); throw; }
    }

    private static void CheckHandle(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.NumberOfLinks != 1))
            throw new InvalidDataException("连接记录路径不是独立的普通文件或目录。");
    }

    private IDisposable Enter()
    {
        var mutex = new Mutex(false, mutexName);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("连接记录正在使用中。"); }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    internal static byte[] ProtectForCurrentUser(byte[] bytes, byte[]? entropyOverride = null) => Crypt(bytes, entropyOverride ?? Entropy, true);
    internal static byte[] UnprotectForCurrentUser(byte[] bytes, byte[]? entropyOverride = null) => Crypt(bytes, entropyOverride ?? Entropy, false);
    private static byte[] Crypt(byte[] input, byte[] entropy, bool protect)
    {
        if (input.Length == 0 || input.Length > MaximumFileBytes) throw new InvalidDataException();
        var inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var inputBlob = new DataBlob { Length = input.Length, Data = inputPin.AddrOfPinnedObject() };
        var entropyBlob = new DataBlob { Length = entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
        DataBlob output = default;
        try
        {
            // CRYPTPROTECT_UI_FORBIDDEN only: omitting LOCAL_MACHINE binds the
            // protected data to the current Windows logon user.
            bool success = protect ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Length <= 0 || output.Length > MaximumFileBytes) throw new InvalidDataException();
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
            entropyPin.Free(); inputPin.Free();
        }
    }

    private const string SafeStorageError = "连接记录未通过完整性、权限或当前用户解密检查；已停止访问。";
    private void RecordFailure(Exception ex)
    {
        // Sharing/lock/device races may clear on the next poll. All other failures
        // remain blocked as invalid; neither category is an actual revoke marker.
        bool temporary = ex is IOException ||
            ex is Win32Exception native && native.NativeErrorCode is 2 or 3 or 21 or 32 or 33 or 170 or 995 or 1117 or 1237;
        LastState = temporary ? CodexPairingState.Unavailable : CodexPairingState.Invalid;
        LastError = temporary ? "连接记录暂时无法读取；稍后会自动重试，无需重新配对。" : SafeStorageError;
    }
    private static bool IsStorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException or Win32Exception or ArgumentException or System.Security.SecurityException;
    private sealed class LegacyHint
    {
        public int Version { get; set; }
        public string Endpoint { get; set; } = "";
        public string CallerThreadId { get; set; } = "";
        public string CallerTurnId { get; set; } = "";
        public DateTimeOffset SavedAtUtc { get; set; }
    }
    private sealed class PinnedDirectories : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = new();
        public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }
    private sealed class MutexLease : IDisposable
    {
        private readonly Mutex mutex;
        public MutexLease(Mutex mutex) => this.mutex = mutex;
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
