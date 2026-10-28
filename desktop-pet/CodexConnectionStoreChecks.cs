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
using Microsoft.Win32.SafeHandles;

namespace DesktopPet;

// All records are fabricated and every filesystem operation is under a unique
// test directory. This never reads or changes the user's AIPet connection.
internal static class CodexConnectionStoreChecks
{
    public static object Run(string temporaryParent)
    {
        string directory = Path.Combine(Path.GetFullPath(temporaryParent), "connection-store-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool success, string label) { if (!success) throw new InvalidOperationException("Connection store check failed: " + label); checks.Add(label); }
        CodexConnectionStore Store(string name) => new(Path.Combine(directory, name));
        var hint = new CodexConnectionHint(@"\\.\pipe\codex-browser-use-" + Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
        var first = Store("roundtrip");
        Check(first.Load() == null && !first.IsRevoked, "fresh store has no record and is not revoked");
        Check(first.SaveVerified(hint), "verified record saves using current-user DPAPI");
        Check(first.Load() == hint, "encrypted record round-trips");
        string disk = Encoding.UTF8.GetString(File.ReadAllBytes(first.ProtectedPath));
        Check(!disk.Contains(hint.Endpoint, StringComparison.Ordinal) && !disk.Contains(hint.CallerThreadId, StringComparison.Ordinal) && !disk.Contains(hint.CallerTurnId, StringComparison.Ordinal), "disk bytes contain none of the connection identifiers");
        var acl = new FileInfo(first.ProtectedPath).GetAccessControl();
        string currentSid = WindowsIdentity.GetCurrent().User!.Value;
        bool onlyPrivate = acl.AreAccessRulesProtected;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value != currentSid && rule.IdentityReference.Value != "S-1-5-18") onlyPrivate = false;
        Check(onlyPrivate, "encrypted file DACL allows only current user and SYSTEM");
        Check(new CodexConnectionStore(Path.GetDirectoryName(first.ProtectedPath)!).Load() == hint, "another instance reads the same verified record");
        Check(first.SaveVerified(hint with { SavedAtUtc = DateTimeOffset.UtcNow }), "existing encrypted record is atomically replaced");

        var updatedEndpoint = hint with { Endpoint = @"\\.\pipe\codex-app-tools-" + Guid.NewGuid().ToString("D") };
        Check(first.SaveVerified(updatedEndpoint) && first.Load() == updatedEndpoint, "legacy pairing can retain caller context while saving a verified new AppTools endpoint");
        Check(new CodexConnectionStore(Path.GetDirectoryName(first.ProtectedPath)!).Load() == updatedEndpoint, "new AppTools endpoint survives reopening the encrypted store");
        Check(!first.SaveVerified(updatedEndpoint with { Endpoint = @"\\server\pipe\codex-app-tools-" + Guid.NewGuid().ToString("D") }) && first.Load() == updatedEndpoint, "remote endpoint is rejected without overwriting the verified record");
        Check(!first.SaveVerified(updatedEndpoint with { Endpoint = updatedEndpoint.Endpoint + "-other" }) && first.Load() == updatedEndpoint, "malformed AppTools endpoint is rejected without losing pairing");

        byte[] encrypted = CodexConnectionStore.ProtectForCurrentUser(Encoding.UTF8.GetBytes("test-only-context"));
        bool wrongEntropyRejected = false;
        try { _ = CodexConnectionStore.UnprotectForCurrentUser(encrypted, Encoding.UTF8.GetBytes("another-application")); }
        catch (CryptographicException) { wrongEntropyRejected = true; }
        Check(wrongEntropyRejected, "wrong application entropy cannot decrypt the record");
        byte[] damaged = File.ReadAllBytes(first.ProtectedPath); damaged[damaged.Length - 1] ^= 0x40; File.WriteAllBytes(first.ProtectedPath, damaged);
        Check(first.Load() == null && first.LastError != null, "tampered ciphertext fails closed");
        WriteLegacy(first, hint);
        Check(first.Load() == null, "damaged v2 does not downgrade to a valid plaintext v1 record");

        var migration = Store("migration"); WriteLegacy(migration, hint);
        Check(migration.Load() == hint && File.Exists(migration.LegacyPath) && !File.Exists(migration.ProtectedPath), "legacy load is read-only and does not migrate before verification");
        Check(!migration.SaveVerified(hint with { CallerTurnId = "invalid" }) && File.Exists(migration.LegacyPath), "failed verification input retains the legacy record");
        Check(migration.SaveVerified(hint) && !File.Exists(migration.LegacyPath) && migration.Load() == hint, "successful verified migration encrypts and then removes plaintext");
        var lockedMigration = Store("locked-migration"); WriteLegacy(lockedMigration, hint);
        using (var held = new FileStream(lockedMigration.LegacyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(!lockedMigration.SaveVerified(hint) && File.Exists(lockedMigration.LegacyPath), "blocked migration preserves the plaintext source instead of losing it");

        var revoked = Store("revocation"); Check(revoked.SaveVerified(hint), "prepare revocation fixture");
        revoked.Forget();
        Check(revoked.IsRevoked && revoked.Load() == null && !File.Exists(revoked.ProtectedPath) && !File.Exists(revoked.LegacyPath), "forget persists revocation and deletes connection files");
        var otherInstance = new CodexConnectionStore(Path.GetDirectoryName(revoked.ProtectedPath)!);
        Check(!otherInstance.SaveVerified(hint) && otherInstance.IsRevoked, "late saves from another instance cannot undo revocation");
        WriteLegacy(revoked, hint);
        Check(otherInstance.Load() == null, "restoring an old plaintext record does not undo revocation");
        Check(otherInstance.SaveVerified(hint, allowReactivation: true) && !otherInstance.IsRevoked && otherInstance.Load() == hint, "explicit verified pairing can reactivate the connection");
        Check(!File.Exists(otherInstance.LegacyPath), "explicit pairing also removes restored plaintext");
        var expired = Store("expired"); WriteLegacy(expired, hint with { SavedAtUtc = DateTimeOffset.UtcNow.AddDays(-91) });
        Check(expired.Load() == null && expired.LastError != null, "records older than 90 days require re-pairing");
        Check(!expired.SaveVerified(hint with { SavedAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) }), "future timestamps are rejected");

        var broadAcl = Store("unsafe-acl"); Check(broadAcl.SaveVerified(hint), "prepare ACL fixture");
        var broad = new FileInfo(broadAcl.ProtectedPath).GetAccessControl();
        broad.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(broadAcl.ProtectedPath).SetAccessControl(broad);
        Check(broadAcl.Load() == null && broadAcl.LastError != null, "a record readable by Everyone is rejected");
        Check(!broadAcl.SaveVerified(hint), "saving does not trust or overwrite an unsafe existing file");

        var oversized = Store("oversized"); Directory.CreateDirectory(Path.GetDirectoryName(oversized.LegacyPath)!);
        File.WriteAllBytes(oversized.LegacyPath, new byte[16385]);
        Check(oversized.Load() == null && oversized.LastError != null, "oversized records are rejected before deserialization");
        var malformed = Store("malformed"); Directory.CreateDirectory(Path.GetDirectoryName(malformed.LegacyPath)!);
        File.WriteAllText(malformed.LegacyPath, "{bad json");
        Check(malformed.Load() == null && malformed.LastError != null, "malformed JSON fails closed");

        var linked = Store("hardlink"); Check(linked.SaveVerified(hint), "prepare hard-link fixture");
        string hardLink = Path.Combine(directory, "alias.dat");
        if (!CreateHardLink(hardLink, linked.ProtectedPath, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Check(linked.Load() == null && linked.LastError != null, "real NTFS hard-linked records are rejected");
            Check(!linked.SaveVerified(hint), "real NTFS hard-linked targets cannot be replaced by saving");
            bool refusedDelete = false;
            try { linked.Forget(); } catch (IOException) { refusedDelete = true; }
            Check(refusedDelete && linked.IsRevoked && File.Exists(hardLink), "forget retains revocation even when unsafe old-file deletion is refused");
        }
        finally { File.Delete(hardLink); }

        string target = Path.Combine(directory, "junction-target"); Directory.CreateDirectory(target);
        var targetStore = new CodexConnectionStore(target); Check(targetStore.SaveVerified(hint), "prepare reparse fixture");
        string junction = Path.Combine(directory, "junction"); CreateJunction(junction, target);
        try
        {
            var redirected = new CodexConnectionStore(junction);
            Check(redirected.Load() == null && redirected.LastError != null, "real NTFS directory junctions are rejected before reading a record");
            Check(!redirected.SaveVerified(hint), "real NTFS directory junctions are rejected before writing");
            Check(targetStore.Load() == hint, "rejected redirected operations leave the target record intact");
        }
        finally { Directory.Delete(junction); }
        return new { passed = true, assertions = checks.Count, checks, directory };
    }

    private static void WriteLegacy(CodexConnectionStore store, CodexConnectionHint hint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyPath)!);
        File.WriteAllText(store.LegacyPath, JsonSerializer.Serialize(new { version = 1, endpoint = hint.Endpoint, callerThreadId = hint.CallerThreadId, callerTurnId = hint.CallerTurnId, savedAtUtc = hint.SavedAtUtc }));
    }

    // A junction uses the mount-point reparse tag and does not require the
    // administrator-only symbolic-link privilege on normal NTFS volumes.
    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        string substitute = @"\??\" + Path.GetFullPath(target);
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute), printBytes = Encoding.Unicode.GetBytes(target);
        byte[] buffer = new byte[16 + substituteBytes.Length + 2 + printBytes.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(buffer.Length - 8))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substituteBytes.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substituteBytes.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)printBytes.Length)).CopyTo(buffer, 14);
        substituteBytes.CopyTo(buffer, 16); printBytes.CopyTo(buffer, 18 + substituteBytes.Length);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
