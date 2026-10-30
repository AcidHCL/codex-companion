using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopPet;

internal sealed record SkinInfo(string Id, string Name, string Accent, string File)
{
    public string ImagePath => File;
}

internal static class SkinCatalog
{
    public static IReadOnlyList<SkinInfo> Load(string baseDir)
    {
        var directory = Path.GetFullPath(Path.Combine(baseDir, "assets", "skins"));
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!System.IO.File.Exists(manifestPath)) throw new FileNotFoundException(L.T("未找到皮肤清单，请重新编译应用：", "The skin manifest was not found. Rebuild the application: ") + manifestPath, manifestPath);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(manifestPath));
        var entries = doc.RootElement;
        if (entries.ValueKind == JsonValueKind.Object && entries.TryGetProperty("skins", out var skins)) entries = skins;
        if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.T("皮肤清单必须是数组或包含 skins 数组的对象。", "The skin manifest must be an array or an object containing a skins array."));
        var result = new List<SkinInfo>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowedRoot = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in entries.EnumerateArray())
        {
            var id = ReadString(entry, "id");
            var name = ReadString(entry, "name");
            var accent = ReadString(entry, "accent");
            var relativeFile = ReadString(entry, "file");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(relativeFile)) continue;
            if (Path.IsPathRooted(relativeFile)) throw new InvalidDataException(L.T("皮肤文件不能使用绝对路径：", "Skin files cannot use absolute paths: ") + id);
            var file = Path.GetFullPath(Path.Combine(directory, relativeFile));
            if (!file.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("皮肤文件必须位于 assets/skins 中：", "Skin files must be located in assets/skins: ") + id);
            if (!ids.Add(id)) throw new InvalidDataException(L.T("皮肤 ID 重复：", "Duplicate skin ID: ") + id);
            if (!System.IO.File.Exists(file)) continue;
            result.Add(new SkinInfo(id, string.IsNullOrWhiteSpace(name) ? id : name, string.IsNullOrWhiteSpace(accent) ? "#75A5FF" : accent, file));
        }
        if (result.Count == 0) throw new InvalidDataException(L.T("没有可用的皮肤图片，请确认 assets/skins 中的文件齐全。", "No skin images available. Check that assets/skins contains all required files."));
        return result.AsReadOnly();
    }

    private static string ReadString(JsonElement entry, string key) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
