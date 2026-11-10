using System;
using System.IO;
using System.Text.Json;

namespace DesktopPet.WorkCompanion;

internal sealed class WorkCompanionOptionsStore
{
    private const int MaxBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 8 };
    private readonly string _path;

    internal WorkCompanionOptionsStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIPet", "work-companion-settings.json"));
    }

    internal WorkCompanionOptions Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 2 or > MaxBytes) return new();
            // Read a bounded stream as well as checking its length, so an
            // unexpectedly growing or replaced file cannot consume unbounded RAM.
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            int count;
            while ((count = file.Read(buffer)) != 0)
            {
                if (bytes.Length + count > MaxBytes) return new();
                bytes.Write(buffer, 0, count);
            }
            return (JsonSerializer.Deserialize<WorkCompanionOptions>(bytes.ToArray(), JsonOptions) ?? new()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return new(); }
    }

    internal void Save(WorkCompanionOptions options)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(options.Normalize(), JsonOptions);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Work companion settings exceed the size limit.");
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".work-companion-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes); file.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
        finally
        {
            // Only our exact temporary file may be removed; no user data is
            // removed when an atomic replacement fails.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
