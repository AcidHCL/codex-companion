using System;
using System.IO;
using System.Text.Json;

namespace DesktopPet.ScreenUnderstanding;

internal sealed class ScreenUnderstandingOptionsStore
{
    private readonly string _path;
    internal ScreenUnderstandingOptionsStore(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "screen-understanding-settings.json"));

    internal ScreenUnderstandingOptions Load() =>
        (ScreenSettingsJson.Read<ScreenUnderstandingOptions>(_path, 8192) ?? new()).Normalize();
    internal void Save(ScreenUnderstandingOptions options) => ScreenSettingsJson.Write(_path, options.Normalize(), 8192);
}

internal static class ScreenSettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 8 };

    internal static T? Read<T>(string path, int maxBytes) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 2 || file.Length > maxBytes) return null;
            using var data = new MemoryStream();
            byte[] buffer = new byte[4096]; int count;
            while ((count = file.Read(buffer)) > 0)
            {
                if (data.Length + count > maxBytes) return null;
                data.Write(buffer, 0, count);
            }
            return JsonSerializer.Deserialize<T>(data.ToArray(), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return null; }
    }

    internal static void Write<T>(string path, T value, int maxBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > maxBytes) throw new InvalidOperationException("Screen understanding data exceeds its size limit.");
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".screen-understanding-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
