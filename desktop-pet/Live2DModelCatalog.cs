using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet;

internal sealed record Live2DModelInfo(
    string Id, string Name, string ModelPath, string RootDirectory, IReadOnlyList<string> Files);

// Local data importer only. No scripts, remote URLs, downloads, or Cubism execution.
// Reference schema: https://github.com/Live2D/CubismSpecs/blob/master/FileFormats/model3.json.md
internal sealed class Live2DModelCatalog
{
    private const string MetadataName = ".aipet-import.json";
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const long MaxJsonBytes = 16L * 1024 * 1024;
    private const long MaxTexturePixels = 64L * 1024 * 1024;
    private const int MaxFiles = 1024;
    private const int MaxTextures = 64;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "Live2D");
    public string RootDirectory { get; }

    public Live2DModelCatalog(string? rootDirectory = null)
    {
        RootDirectory = LocalFullPath(rootDirectory ?? DefaultRootDirectory);
    }

    public IReadOnlyList<Live2DModelInfo> Load() => Load(out _);

    public IReadOnlyList<Live2DModelInfo> Load(out IReadOnlyList<string> issues)
    {
        var models = new List<Live2DModelInfo>();
        var errors = new List<string>();
        issues = errors.AsReadOnly();
        try
        {
            CheckNoReparsePoints(RootDirectory);
            if (!Directory.Exists(RootDirectory)) return models.AsReadOnly();
            int count = 0;
            foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
            {
                string id = Path.GetFileName(directory);
                if (!IsImportedId(id)) continue; // Never treat staging/unrelated directories as models.
                if (++count > 2048) { errors.Add(L.T("模型目录数量超过上限，请减少已导入模型后重试。", "Too many model directories. Remove some imported models and retry.")); break; }
                try
                {
                    CheckNoReparsePoints(directory);
                    using var stream = OpenChecked(Path.Combine(directory, MetadataName));
                    using var metadata = ParseJson(stream, 16 * 1024, L.T("导入记录", "Import record"));
                    var data = metadata.RootElement;
                    if (!data.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1 ||
                        ReadString(data, "id") != id)
                        throw Invalid(L.T("模型导入记录格式不兼容。", "The model import record format is incompatible."));
                    string modelFile = NormalizeReference(ReadString(data, "modelFile") ?? "", ".model3.json");
                    if (modelFile.Contains('/')) throw Invalid(L.T("模型入口必须位于导入目录根部。", "The model entry must be at the root of the imported directory."));
                    models.Add(Validate(Path.Combine(directory, modelFile), id, ReadString(data, "name")));
                }
                catch (Exception ex) when (IsDisplayableFailure(ex))
                {
                    errors.Add(id + "：" + DisplayError(ex));
                }
            }
        }
        catch (Exception ex) when (IsDisplayableFailure(ex)) { errors.Add(DisplayError(ex)); }
        return models.OrderBy(model => model.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    // Structural validation does not execute Cubism Core or guarantee the model's visual quality.
    // Validate supports official samples directly without importing or writing any files.
    public Live2DModelInfo Validate(string modelPath, string? id = null, string? displayName = null)
    {
        using var plan = Inspect(modelPath, CancellationToken.None);
        string stableId = id ?? "preview-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(plan.ModelPath.ToUpperInvariant())))[..24].ToLowerInvariant();
        ValidateId(stableId);
        return plan.Info(stableId, ModelName(plan.ModelPath, displayName), plan.Root);
    }

    // Files are normalized relative paths with '/' separators, including the model3.json entry.
    // The web host must match this allowlist (OrdinalIgnoreCase on Windows), never serve the whole root.
    public IReadOnlyList<string> GetValidatedFiles(Live2DModelInfo model)
    {
        var checkedModel = Validate(model.ModelPath, model.Id, model.Name);
        if (!string.Equals(checkedModel.RootDirectory, LocalFullPath(model.RootDirectory), StringComparison.OrdinalIgnoreCase))
            throw Invalid(L.T("模型根目录与入口文件不一致。", "The model root directory does not match its entry file."));
        return checkedModel.Files;
    }

    public Task<Live2DModelInfo> ImportAsync(string modelJsonPath, string? displayName = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportCore(modelJsonPath, displayName, cancellationToken), cancellationToken);

    private Live2DModelInfo ImportCore(string modelJsonPath, string? displayName, CancellationToken cancellationToken)
    {
        string? staging = null;
        bool createdStaging = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var plan = Inspect(modelJsonPath, cancellationToken);
            string name = ModelName(plan.ModelPath, displayName);
            string id = "l2d-" + Guid.NewGuid().ToString("N");
            string destination = ChildPath(RootDirectory, id);
            EnsureDirectory(RootDirectory);
            staging = ChildPath(RootDirectory, ".import-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(staging) || File.Exists(staging)) throw Invalid(L.T("导入临时目录已存在，请重试。", "The temporary import directory already exists. Retry."));
            Directory.CreateDirectory(staging);
            createdStaging = true;
            CheckNoReparsePoints(staging);
            foreach (var entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string output = ChildPath(staging, entry.RelativePath);
                EnsureDirectory(Path.GetDirectoryName(output)!);
                CheckNoReparsePoints(output);
                // Source handles stay open without write/delete sharing throughout the import.
                entry.Stream.Position = 0;
                using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = entry.Stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    copied += read;
                    if (copied > entry.Length) throw Invalid(L.T("模型资源在导入期间发生变化，请重新选择模型。", "Model assets changed during import. Select the model again."));
                    target.Write(buffer, 0, read);
                }
                if (copied != entry.Length) throw Invalid(L.T("模型资源未能完整复制，请重试。", "Model assets were not fully copied. Retry."));
                target.Flush(true);
            }

            string modelFile = Path.GetFileName(plan.ModelPath);
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, id, name, modelFile, importedAtUtc = DateTimeOffset.UtcNow
            });
            using (var record = new FileStream(Path.Combine(staging, MetadataName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                record.Write(metadata);
                record.Flush(true);
            }
            // Re-open the completed staged package before atomically publishing it.
            var validated = Validate(Path.Combine(staging, modelFile), id, name);
            cancellationToken.ThrowIfCancellationRequested();
            CheckNoReparsePoints(RootDirectory);
            CheckNoReparsePoints(staging);
            if (Directory.Exists(destination) || File.Exists(destination)) throw Invalid(L.T("模型 ID 已存在，未覆盖现有模型。", "This model ID already exists. No existing model was overwritten."));
            Directory.Move(staging, destination); // Same parent/volume; existing directories are never replaced.
            createdStaging = false;
            return validated with { RootDirectory = destination, ModelPath = Path.Combine(destination, modelFile) };
        }
        catch (Exception ex)
        {
            Exception? cleanupFailure = null;
            if (createdStaging && staging != null)
            {
                try { CleanupOwnedDirectory(staging, RootDirectory, ".import-"); }
                catch (Exception cleanup) { cleanupFailure = cleanup; }
            }
            if (cleanupFailure != null)
                throw new IOException(L.T("模型导入未完成，且临时目录清理失败；现有模型未被覆盖。请检查模型目录权限。", "Model import and temporary directory cleanup failed. Existing models were not overwritten. Check directory permissions."), ex);
            if (ex is OperationCanceledException) throw;
            if (IsDisplayableFailure(ex)) throw new InvalidDataException(DisplayError(ex), ex);
            throw;
        }
    }

    private static Plan Inspect(string modelPath, CancellationToken token)
    {
        string full = LocalFullPath(modelPath);
        if (!full.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
            throw Invalid(L.T("请选择以 .model3.json 结尾的 Live2D 模型入口。", "Select a Live2D model entry ending in .model3.json."));
        string root = Path.GetDirectoryName(full) ?? throw Invalid(L.T("模型入口目录无效。", "Invalid model entry directory."));
        string fileName = NormalizeReference(Path.GetFileName(full), ".model3.json");
        var plan = new Plan(full, root);
        try
        {
            var model = plan.Add(fileName, ".model3.json", token);
            using var document = ParseJson(model.Stream, MaxJsonBytes, fileName);
            var json = document.RootElement;
            RejectUnknown(json, "Version", "FileReferences", "Groups", "HitAreas", "Layout");
            if (!json.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value) || value != 3)
                throw Invalid(L.T("目前只支持 Version=3 的 Cubism .model3.json。", "Only Cubism .model3.json files with Version=3 are supported."));
            if (!json.TryGetProperty("FileReferences", out var refs) || refs.ValueKind != JsonValueKind.Object)
                throw Invalid(L.T("模型入口缺少有效的 FileReferences。", "The model entry is missing valid FileReferences."));
            var allowedRefs = new HashSet<string>(StringComparer.Ordinal)
            {
                "Moc", "Textures", "Physics", "Pose", "UserData", "DisplayInfo", "MotionSync", "Motions", "Expressions"
            };
            foreach (var property in refs.EnumerateObject())
                if (!allowedRefs.Contains(property.Name))
                    throw Invalid(L.T("模型引用了暂不支持的资源字段：", "The model references an unsupported resource field: ") + property.Name + L.T("。不会复制未知资源或脚本。", ". Unknown resources and scripts will not be copied."));

            plan.Add(RequiredString(refs, "Moc"), ".moc3", token);
            if (!refs.TryGetProperty("Textures", out var textures) || textures.ValueKind != JsonValueKind.Array ||
                textures.GetArrayLength() < 1 || textures.GetArrayLength() > MaxTextures)
                throw Invalid(L.T("Textures 必须包含 1–64 张 PNG 纹理。", "Textures must contain 1–64 PNG textures."));
            foreach (var texture in textures.EnumerateArray())
                plan.Add(ElementString(texture, L.T("纹理路径", "Texture path")), ".png", token);

            foreach (var optional in new[]
            {
                ("Physics", ".physics3.json"), ("Pose", ".pose3.json"), ("UserData", ".userdata3.json"),
                ("DisplayInfo", ".cdi3.json"), ("MotionSync", ".motionsync3.json")
            })
                if (refs.TryGetProperty(optional.Item1, out var path) && path.ValueKind != JsonValueKind.Null)
                    plan.Add(ElementString(path, optional.Item1), optional.Item2, token);

            if (refs.TryGetProperty("Expressions", out var expressions) && expressions.ValueKind != JsonValueKind.Null)
            {
                RequireArray(expressions, "Expressions");
                foreach (var expression in expressions.EnumerateArray())
                {
                    RequireObject(expression, L.T("表情条目", "Expression entry"));
                    RequiredString(expression, "Name");
                    plan.Add(RequiredString(expression, "File"), ".exp3.json", token);
                    RejectUnknown(expression, "Name", "File", "FadeInTime", "FadeOutTime");
                }
            }
            if (refs.TryGetProperty("Motions", out var motions) && motions.ValueKind != JsonValueKind.Null)
            {
                RequireObject(motions, "Motions");
                foreach (var group in motions.EnumerateObject())
                {
                    RequireArray(group.Value, L.T("动作组 ", "Motion group ") + group.Name);
                    foreach (var motion in group.Value.EnumerateArray())
                    {
                        RequireObject(motion, L.T("动作条目", "Motion entry"));
                        plan.Add(RequiredString(motion, "File"), ".motion3.json", token);
                        if (motion.TryGetProperty("Sound", out var sound) && sound.ValueKind != JsonValueKind.Null)
                            plan.Add(ElementString(sound, L.T("动作音频", "Motion audio")), ".wav|.ogg|.mp3", token);
                        RejectUnknown(motion, "File", "Sound", "MotionSync", "FadeInTime", "FadeOutTime");
                    }
                }
            }
            return plan;
        }
        catch { plan.Dispose(); throw; }
    }

    private sealed class Plan : IDisposable
    {
        public string ModelPath { get; }
        public string Root { get; }
        public List<Entry> Entries { get; } = new();
        private readonly Dictionary<string, Entry> files = new(StringComparer.OrdinalIgnoreCase);
        private long totalBytes;
        private long totalTexturePixels;
        public Plan(string modelPath, string root) { ModelPath = modelPath; Root = root; }
        public Entry Add(string path, string extension, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string relative = NormalizeReference(path, extension);
            if (files.TryGetValue(relative, out var existing)) return existing;
            if (Entries.Count >= MaxFiles) throw Invalid(L.T("模型引用文件过多，最多支持 1024 个文件。", "Too many model references. At most 1,024 files are supported."));
            string full = ChildPath(Root, relative);
            var stream = OpenChecked(full);
            try
            {
                long length = stream.Length;
                if (length <= 0 || length > MaxFileBytes) throw Invalid(L.T("资源为空或超过单文件 128 MiB 上限：", "The asset is empty or exceeds the 128 MiB per-file limit: ") + relative);
                if (totalBytes + length > MaxPackageBytes) throw Invalid(L.T("模型资源总大小超过 512 MiB 上限。", "Model assets exceed the 512 MiB package limit."));
                ValidateResource(stream, relative, ref totalTexturePixels);
                var entry = new Entry(relative, stream, length);
                files.Add(relative, entry); Entries.Add(entry); totalBytes += length;
                return entry;
            }
            catch { stream.Dispose(); throw; }
        }
        public Live2DModelInfo Info(string id, string name, string root) => new(id, name,
            Path.Combine(root, Path.GetFileName(ModelPath)), root, Entries.Select(entry => entry.RelativePath).ToArray());
        public void Dispose() { foreach (var entry in Entries) entry.Stream.Dispose(); }
    }
    private sealed record Entry(string RelativePath, FileStream Stream, long Length);

    private static void ValidateResource(FileStream stream, string relative, ref long texturePixels)
    {
        stream.Position = 0;
        byte[] header = new byte[Math.Min(32, (int)Math.Min(stream.Length, 32))];
        stream.ReadExactly(header);
        stream.Position = 0;
        if (relative.EndsWith(".moc3", StringComparison.OrdinalIgnoreCase))
        {
            if (stream.Length < 64 || !header.AsSpan(0, 4).SequenceEqual("MOC3"u8))
                throw Invalid(L.T("MOC3 文件头无效：", "Invalid MOC3 header: ") + relative);
        }
        else if (relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (header.Length < 24 || !header.AsSpan(0, 8).SequenceEqual(signature) ||
                !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw Invalid(L.T("纹理不是有效的 PNG 文件：", "The texture is not a valid PNG: ") + relative);
            uint width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > 8192 || height > 8192 ||
                texturePixels + (long)width * height > MaxTexturePixels)
                throw Invalid(L.T("纹理尺寸过大，单边最多 8192 像素，总纹理最多 6400 万像素。", "Textures are too large. Each edge is limited to 8,192 pixels and all textures to 64 million pixels."));
            texturePixels += (long)width * height;
        }
        else if (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = ParseJson(stream, MaxJsonBytes, relative);
        }
        else if (relative.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            if (header.Length < 12 || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw Invalid(L.T("动作音频不是有效的 WAV：", "Motion audio is not a valid WAV: ") + relative);
        }
        else if (relative.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            if (header.Length < 4 || !header.AsSpan(0, 4).SequenceEqual("OggS"u8)) throw Invalid(L.T("动作音频不是有效的 OGG：", "Motion audio is not a valid OGG: ") + relative);
        }
        else if (relative.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (header.Length < 3 || !(header.AsSpan(0, 3).SequenceEqual("ID3"u8) || header[0] == 255 && (header[1] & 224) == 224))
                throw Invalid(L.T("动作音频不是有效的 MP3：", "Motion audio is not a valid MP3: ") + relative);
        }
        stream.Position = 0;
    }

    private static JsonDocument ParseJson(FileStream stream, long maximum, string label)
    {
        if (stream.Length <= 0 || stream.Length > maximum) throw Invalid(label + L.T(" 大小无效或超过 JSON 上限。", " has an invalid size or exceeds the JSON size limit."));
        stream.Position = 0;
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        // Cubism exports can include a UTF-8 BOM.
        int offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.AsMemory(offset), JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException(label + L.T(" 不是有效的 JSON。", " is not valid JSON."), ex); }
        try { RequireObject(document.RootElement, label); RejectDuplicateKeys(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
        finally { stream.Position = 0; }
    }
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid(L.T("JSON 中存在重复字段：", "Duplicate JSON field: ") + property.Name);
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
    private static void RejectUnknown(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal)) throw Invalid(L.T("暂不支持的模型资源字段：", "Unsupported model resource field: ") + property.Name);
    }

    private static string NormalizeReference(string reference, string extension)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 240 || Path.IsPathRooted(reference) ||
            reference.StartsWith('/') || reference.StartsWith('\\') || reference.Any(ch => ch < 32 || ":?#%<>|\"*".Contains(ch)))
            throw Invalid(L.T("模型资源必须使用不含 URL、绝对路径或特殊字符的相对路径。", "Model assets must use relative paths without URLs, absolute paths, or special characters."));
        var segments = reference.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') || IsDeviceName(segment)))
            throw Invalid(L.T("模型资源路径不能包含空段、点路径、设备名或结尾空格。", "Model paths cannot contain empty segments, dot segments, device names, or trailing spaces."));
        string normalized = string.Join("/", segments);
        if (normalized.Equals(MetadataName, StringComparison.OrdinalIgnoreCase) ||
            !extension.Split('|').Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            throw Invalid(L.T("资源类型不受支持或疑似脚本：", "Unsupported resource type or suspected script: ") + normalized + L.T("；需要 ", "; required: ") + extension + "。");
        return normalized;
    }
    private static bool IsDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            (name[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }
    private static string LocalFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
            throw Invalid(L.T("请选择本机磁盘中的模型目录或文件，不支持网络和设备路径。", "Select a model directory or file on a local disk. Network and device paths are not supported."));
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException(L.T("模型文件路径无效。", "Invalid model file path."), ex); }
    }
    private static string ChildPath(string root, string relative)
    {
        string fullRoot = LocalFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Invalid(L.T("模型资源路径超出允许目录。", "The model asset path is outside the permitted directory."));
        return path;
    }
    private static void EnsureDirectory(string path)
    {
        CheckNoReparsePoints(path);
        Directory.CreateDirectory(path);
        CheckNoReparsePoints(path);
    }
    private static void CheckNoReparsePoints(string path)
    {
        string? part = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(part))
        {
            try
            {
                var attrs = File.GetAttributes(part);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw Invalid(L.T("模型路径不能经过符号链接或目录联接点。", "Model paths cannot pass through symbolic links or junctions."));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            part = Path.GetDirectoryName(part);
        }
    }
    private static FileStream OpenChecked(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("安全模型导入仅支持 Windows。", "Secure model import is supported only on Windows."));
        CheckNoReparsePoints(path);
        // MSIX can legitimately redirect an AppData directory into its package
        // cache. Pin every lexical ancestor without following a reparse point,
        // then compare the opened file with that parent's actual location.
        // Denying delete sharing prevents an ancestor being replaced mid-check.
        using var parents = PinParentDirectories(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException ex) { throw new InvalidDataException(L.T("缺少模型引用资源：", "Missing referenced model asset: ") + Path.GetFileName(path), ex); }
        catch (DirectoryNotFoundException ex) { throw new InvalidDataException(L.T("缺少模型资源目录：", "Missing model asset directory: ") + Path.GetFileName(path), ex); }
        try
        {
            string actual = FinalHandlePath(stream.SafeFileHandle);
            string actualParent = FinalHandlePath(parents.Handles[^1]);
            string expected = Path.Combine(actualParent, Path.GetFileName(path));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw Invalid(L.T("模型文件解析到其它位置，可能包含符号链接。", "The model file resolves to another location and may involve a symbolic link."));
            CheckNoReparsePoints(path);
            CheckNoReparsePoints(actual);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static PinnedDirectories PinParentDirectories(string filePath)
    {
        var ancestors = new Stack<string>();
        for (string? parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
            !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent)) ancestors.Push(parent);
        var pinned = new PinnedDirectories();
        try
        {
            foreach (string directory in ancestors)
            {
                // FILE_READ_ATTRIBUTES, FILE_SHARE_READ|WRITE, OPEN_EXISTING,
                // FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT.
                var handle = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                pinned.Handles.Add(handle);
                if (!GetFileInformationByHandleEx(handle, 9, out var info, 8))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                    (info.FileAttributes & (uint)FileAttributes.Directory) == 0)
                    throw Invalid(L.T("模型路径不能经过符号链接或目录联接点。", "Model paths cannot pass through symbolic links or junctions."));
            }
            return pinned;
        }
        catch { pinned.Dispose(); throw; }
    }

    private sealed class PinnedDirectories : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = new();
        public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }

    private static string FinalHandlePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        string actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        return LocalFullPath(actual);
    }

    // Never use recursive deletion on an unchecked path. Only the caller-created unique
    // staging/test directory is eligible; links are rejected and traversal is one level at a time.
    internal static void CleanupOwnedDirectory(string directory, string expectedParent, string requiredPrefix)
    {
        string full = LocalFullPath(directory);
        string parent = LocalFullPath(expectedParent);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(requiredPrefix, StringComparison.Ordinal) || full == parent)
            throw Invalid(L.T("拒绝清理非本次创建的临时目录。", "Refused to clean up a temporary directory not created by this import."));
        if (!Directory.Exists(full)) return;
        CheckNoReparsePoints(full);
        DeleteCheckedTree(full);
    }
    private static void DeleteCheckedTree(string directory)
    {
        CheckNoReparsePoints(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckNoReparsePoints(entry);
            var attrs = File.GetAttributes(entry);
            if ((attrs & FileAttributes.Directory) != 0) DeleteCheckedTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory, false);
    }
    private static string ModelName(string modelPath, string? requested)
    {
        string name = requested?.Trim() ?? Path.GetFileName(modelPath)[..^".model3.json".Length];
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw Invalid(L.T("模型名称需为 1–80 个不含控制字符的字符。", "Model names must contain 1–80 characters without control characters."));
        return name;
    }
    private static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 80 || id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw Invalid(L.T("模型 ID 只能包含字母、数字、横线和下划线。", "Model IDs may contain only letters, numbers, hyphens, and underscores."));
    }
    private static bool IsImportedId(string id) => id.StartsWith("l2d-", StringComparison.Ordinal) &&
        id.Length == 36 && Guid.TryParseExact(id[4..], "N", out _);
    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredString(JsonElement element, string name) =>
        ReadString(element, name) is string value && value.Length > 0 ? value : throw Invalid(L.T("模型缺少有效字段：", "The model is missing a valid field: ") + name);
    private static string ElementString(JsonElement element, string label) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is string value && value.Length > 0 ? value : throw Invalid(label + L.T(" 必须为非空字符串。", " must be a nonempty string."));
    private static void RequireObject(JsonElement element, string label)
    { if (element.ValueKind != JsonValueKind.Object) throw Invalid(label + L.T(" 必须是 JSON 对象。", " must be a JSON object.")); }
    private static void RequireArray(JsonElement element, string label)
    { if (element.ValueKind != JsonValueKind.Array) throw Invalid(label + L.T(" 必须是 JSON 数组。", " must be a JSON array.")); }
    private static InvalidDataException Invalid(string message) => new(message);
    private static bool IsDisplayableFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or Win32Exception;
    private static string DisplayError(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        UnauthorizedAccessException => L.T("无法访问模型资源，请检查文件和目录权限。", "Could not access model assets. Check file and directory permissions."),
        Win32Exception => L.T("无法安全核验模型文件位置，请确认模型位于本机普通文件夹。", "Could not safely verify model file locations. Use a regular local folder."),
        _ => L.T("模型文件读取或复制失败：", "Could not read or copy model files: ") + ex.Message
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);
}

internal static class Live2DModelCatalogChecks
{
    // Uses only a unique caller-owned scratch directory; never opens the real model library.
    public static object Run(string? temporaryParent = null)
    {
        string parent = Path.GetFullPath(temporaryParent ?? Path.GetTempPath());
        string scratch = Path.Combine(parent, "AIPet.Live2DChecks-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(scratch) || File.Exists(scratch)) throw new IOException("Self-check directory already exists.");
        Directory.CreateDirectory(scratch);
        string? link = null;
        int assertions = 0;
        bool reparseTestAvailable = false;
        void Check(bool success, string label)
        {
            assertions++;
            if (!success) throw new InvalidOperationException("Live2D import check: " + label);
        }
        try
        {
            string source = Path.Combine(scratch, "source");
            string library = Path.Combine(scratch, "library");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source, "textures"));
            Directory.CreateDirectory(Path.Combine(source, "motions"));
            Directory.CreateDirectory(Path.Combine(source, "expressions"));
            byte[] moc = new byte[64];
            Encoding.ASCII.GetBytes("MOC3").CopyTo(moc, 0);
            File.WriteAllBytes(Path.Combine(source, "fixture.moc3"), moc);
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j1ZkAAAAASUVORK5CYII=");
            File.WriteAllBytes(Path.Combine(source, "textures", "texture.png"), png);
            File.WriteAllText(Path.Combine(source, "fixture.physics3.json"), "{\"Version\":3}");
            File.WriteAllText(Path.Combine(source, "fixture.pose3.json"), "{\"Version\":3}");
            File.WriteAllText(Path.Combine(source, "motions", "idle.motion3.json"), "{\"Version\":3,\"Curves\":[]}");
            File.WriteAllText(Path.Combine(source, "expressions", "happy.exp3.json"), "{\"Type\":\"Live2D Expression\",\"Parameters\":[]}");
            File.WriteAllText(Path.Combine(source, "unreferenced.txt"), "not a model resource");
            File.WriteAllText(Path.Combine(source, "unreferenced.js"), "must never be copied or executed");
            string entry = Path.Combine(source, "fixture.model3.json");
            string goodJson = """
                {"Version":3,"FileReferences":{"Moc":"fixture.moc3","Textures":["textures/texture.png"],
                "Physics":"fixture.physics3.json","Pose":"fixture.pose3.json",
                "Motions":{"Idle":[{"File":"motions/idle.motion3.json"}]},
                "Expressions":[{"Name":"happy","File":"expressions/happy.exp3.json"}]}}
                """;
            File.WriteAllText(entry, goodJson);
            var catalog = new Live2DModelCatalog(library);
            var preview = catalog.Validate(entry, "official-fixture", "Fixture");
            Check(!Directory.Exists(library) && preview.Files.Count == 7, "Validate must not create a library, and must include only the entry plus referenced resources.");
            Check(preview.Id == "official-fixture" && preview.Name == "Fixture" &&
                preview.Files.Contains("motions/idle.motion3.json") && preview.Files.Contains("expressions/happy.exp3.json"),
                "Optional resources and explicit preview identity must be preserved.");

            var first = catalog.ImportAsync(entry, "Imported fixture").GetAwaiter().GetResult();
            Check(File.Exists(first.ModelPath) && File.ReadAllText(first.ModelPath) == goodJson, "Import must preserve the model JSON and publish the completed package.");
            Check(!File.Exists(Path.Combine(first.RootDirectory, "unreferenced.txt")) &&
                !File.Exists(Path.Combine(first.RootDirectory, "unreferenced.js")), "Unreferenced files and scripts must never be copied.");
            var loaded = catalog.Load(out var issues);
            Check(issues.Count == 0 && loaded.Count == 1 && loaded[0].Id == first.Id && loaded[0].Name == first.Name,
                "Imported IDs and names must remain stable when the catalog is reloaded.");
            Check(catalog.GetValidatedFiles(first).SequenceEqual(preview.Files), "The host file allowlist must match the validated copied resource graph.");
            var second = catalog.ImportAsync(entry).GetAwaiter().GetResult();
            Check(first.Id != second.Id && File.Exists(first.ModelPath) && catalog.Load().Count == 2,
                "A new import must not replace an existing model.");

            void RejectModel(string badJson, string label)
            {
                File.WriteAllText(entry, badJson);
                bool rejected = false;
                try { catalog.ImportAsync(entry).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, label);
                Check(catalog.Load().Count == 2 && !Directory.EnumerateDirectories(library, ".import-*").Any(),
                    "Rejected imports must leave existing models intact without a partial published package.");
            }
            foreach (string forbidden in new[] { "../outside.moc3", @"..\outside.moc3", "C:/outside.moc3", "https://example.invalid/model.moc3", "%2e%2e/outside.moc3", "payload.js" })
            {
                RejectModel(JsonSerializer.Serialize(new
                {
                    Version = 3,
                    FileReferences = new { Moc = forbidden, Textures = new[] { "textures/texture.png" } }
                }), "Unsafe model reference must be rejected: " + forbidden);
            }
            RejectModel(goodJson.Replace("textures/texture.png", "textures/missing.png", StringComparison.Ordinal),
                "A missing texture must prevent publication.");
            RejectModel("""{"Version":3,"FileReferences":{"Moc":"fixture.moc3","Moc":"fixture.moc3","Textures":["textures/texture.png"]}}""",
                "Ambiguous duplicate JSON fields must be rejected.");
            File.WriteAllText(Path.Combine(source, "fixture.physics3.json"), "<script>not JSON</script>");
            RejectModel(goodJson, "A script disguised as optional JSON must be rejected.");
            File.WriteAllText(Path.Combine(source, "fixture.physics3.json"), "{\"Version\":3}");
            File.WriteAllText(entry, goodJson);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool cancelled = false;
                try { catalog.ImportAsync(entry, cancellationToken: cancellation.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && catalog.Load().Count == 2, "Cancellation must not publish or modify any model.");
            }

            // An actual symlink test is optional when Windows does not grant creation rights.
            string external = Path.Combine(scratch, "external");
            Directory.CreateDirectory(external);
            File.WriteAllBytes(Path.Combine(external, "texture.png"), png);
            link = Path.Combine(source, "linked");
            try { Directory.CreateSymbolicLink(link, external); reparseTestAvailable = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException) { link = null; }
            if (reparseTestAvailable)
                RejectModel(goodJson.Replace("textures/texture.png", "linked/texture.png", StringComparison.Ordinal),
                    "A resource reached through a directory symlink must be rejected.");
            Check(File.ReadAllText(first.ModelPath) == goodJson && File.ReadAllText(second.ModelPath) == goodJson,
                "All failed imports must preserve previously imported model bytes.");
            return new { passed = true, offline = true, assertions, reparseTestAvailable, realUserLibraryTouched = false };
        }
        finally
        {
            // This is only the explicitly created test symlink; never recurse through it.
            if (link != null && Directory.Exists(link)) Directory.Delete(link, false);
            Live2DModelCatalog.CleanupOwnedDirectory(scratch, parent, "AIPet.Live2DChecks-");
        }
    }
}
