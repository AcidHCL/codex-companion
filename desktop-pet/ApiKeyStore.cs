using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPet;

// Endpoint-bound DPAPI storage. A different host or base path never reuses a
// saved credential. Ordinary preferences, diagnostic reports and API profile
// serialization contain no key. DPAPI protects other Windows users, not malware
// already running as this Windows user.
internal sealed class ApiKeyStore
{
    private const int MaximumFileBytes = 16_384;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("AIPETAPI1");
    private readonly string _root;
    private readonly object _sync = new();

    internal ApiKeyStore(string? rootDirectory = null) => _root = Path.GetFullPath(rootDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "api-keys"));

    internal bool HasKey(Uri endpoint)
    {
        // Read/decrypt to distinguish a usable credential from a damaged or
        // another-user blob, but never return it to a status label.
        try { return Read(endpoint) != null; }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }

    internal string? Read(Uri endpoint)
    {
        var canonical = ChatApiEndpoint.Parse(endpoint.AbsoluteUri);
        lock (_sync)
        {
            string file = FileFor(canonical);
            if (!File.Exists(file)) return null;
            ValidatePath(file, createDirectory: false);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= Header.Length || stream.Length > MaximumFileBytes) throw StorageError();
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (!bytes.AsSpan(0, Header.Length).SequenceEqual(Header)) throw StorageError();
            byte[] clear;
            try { clear = Crypt(bytes.AsSpan(Header.Length).ToArray(), Entropy(canonical), protect: false); }
            catch (Exception ex) when (StorageFailure(ex)) { throw StorageError(); }
            try
            {
                string key = new UTF8Encoding(false, true).GetString(clear);
                ValidateKey(key); return key;
            }
            catch (Exception ex) when (StorageFailure(ex) || ex is ArgumentException) { throw StorageError(); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
    }

    internal void Save(Uri endpoint, string key)
    {
        var canonical = ChatApiEndpoint.Parse(endpoint.AbsoluteUri);
        key = ValidateKey(key);
        lock (_sync)
        {
            string file = FileFor(canonical);
            ValidatePath(file, createDirectory: true);
            byte[] clear = Encoding.UTF8.GetBytes(key);
            byte[] encrypted;
            try { encrypted = Crypt(clear, Entropy(canonical), protect: true); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            byte[] bytes = new byte[Header.Length + encrypted.Length];
            Header.CopyTo(bytes, 0); encrypted.CopyTo(bytes, Header.Length);
            if (bytes.Length > MaximumFileBytes) throw StorageError();
            string temporary = Path.Combine(_root, ".api-key-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                ValidatePath(file, createDirectory: false);
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception ex) when (StorageFailure(ex)) { throw StorageError(); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (StorageFailure(ex)) { } }
        }
    }

    internal void Delete(Uri endpoint)
    {
        var canonical = ChatApiEndpoint.Parse(endpoint.AbsoluteUri);
        lock (_sync)
        {
            string file = FileFor(canonical);
            if (!File.Exists(file)) return;
            ValidatePath(file, createDirectory: false);
            try { File.Delete(file); } catch (Exception ex) when (StorageFailure(ex)) { throw StorageError(); }
        }
    }

    internal static string ValidateKey(string? key)
    {
        string clean = (key ?? "").Trim();
        if (clean.Length is < 1 or > 8192 || clean.Any(c => c <= ' ' || c > '~'))
            throw new ArgumentException(L.T("API Key 无效，请粘贴完整密钥，不要包含空格或换行。", "The API key is invalid. Paste the complete key without spaces or line breaks."));
        return clean;
    }

    private string FileFor(Uri endpoint) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri))) + ".key");
    private static byte[] Entropy(Uri endpoint) => SHA256.HashData(Encoding.UTF8.GetBytes("AIPet/API-key/v1\0" + endpoint.AbsoluteUri));

    private void ValidatePath(string file, bool createDirectory)
    {
        if (!file.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw StorageError();
        if (createDirectory) Directory.CreateDirectory(_root);
        for (var dir = new DirectoryInfo(_root); dir != null && dir.Exists; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw StorageError();
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw StorageError();
    }

    private static IOException StorageError() => new(L.T("无法安全读取或保存 API 密钥，请重新输入密钥或检查本地存储权限。",
        "The API key could not be read or saved safely. Re-enter it or check local storage permissions."));
    private static bool StorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        CryptographicException or Win32Exception or System.Security.SecurityException or DecoderFallbackException;

    private static byte[] Crypt(byte[] input, byte[] entropy, bool protect)
    {
        var dataPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var data = new Blob { Length = input.Length, Data = dataPin.AddrOfPinnedObject() };
        var extra = new Blob { Length = entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
        Blob output = default;
        try
        {
            bool succeeded = protect ? CryptProtectData(ref data, null, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref data, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded || output.Data == IntPtr.Zero || output.Length is < 1 or > MaximumFileBytes) throw StorageError();
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!protect) for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            dataPin.Free(); entropyPin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
