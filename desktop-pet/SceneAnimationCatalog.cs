using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet;

internal sealed class SceneAnimationClip
{
    public string Type { get; set; } = "frames";
    public string[] Frames { get; set; } = Array.Empty<string>();
    public string? File { get; set; }
    public double Fps { get; set; } = 12;
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public int FrameCount { get; set; }
    public int[]? DurationsMs { get; set; }
}

internal sealed class SceneAnimationAction
{
    public SceneAnimationClip? Start { get; set; }
    public SceneAnimationClip Loop { get; set; } = new();
    public SceneAnimationClip? End { get; set; }
    public bool Repeat { get; set; } = true;
    public SceneAnimationAudio? Audio { get; set; }
    public SceneAnimationThoughts? Thoughts { get; set; }
}

internal sealed class SceneAnimationAudio
{
    public string File { get; set; } = "";
}

internal sealed class SceneAnimationThoughts
{
    public string Title { get; set; } = "";
    public string[] Lines { get; set; } = Array.Empty<string>();
}

internal sealed record SceneAnimationPack(string Id, string Name, string SkinId, string RootDirectory,
    string ManifestPath, IReadOnlyDictionary<string, SceneAnimationAction> Actions, IReadOnlyList<string> Files,
    double AnchorX = 0.5, double AnchorY = 1);

// Data-only local packages. Nothing in a manifest is evaluated as code or used as a remote URL.
internal sealed class SceneAnimationCatalog
{
    private const string MetadataName = ".aipet-scene-import.json";
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long MaxFileBytes = 96L * 1024 * 1024;
    private const long MaxDecodedPixels = 64L * 1024 * 1024;
    // PNG sequences are streamed through a bounded bitmap cache. This is an
    // on-disk package budget, separate from each eagerly decoded image/APNG.
    private const long MaxPackagePixels = 512L * 1024 * 1024;
    private const int MaxSequenceFrames = 720;
    private const int MaxFrames = 300;
    private const int MaxFiles = 2048;
    private static readonly string[] ActionNames = { "working", "attention", "complete", "walk", "walk-left", "walk-right", "drag", "fall", "land", "jump", "idle", "sing",
        "happy", "angry", "startled", "bashful", "jump-left", "jump-right", "climb-left", "climb-right", "climb-top-left", "climb-top-right",
        "token-come", "bowl-tap", "bowl-tap-eager", "dance", "dance-2", "dance-3", "fireworks", "bangyou-dance" };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    public static string DefaultRootDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "Scenes");
    public string RootDirectory { get; }

    public SceneAnimationCatalog(string? rootDirectory = null) => RootDirectory = SceneFileAccess.LocalFullPath(rootDirectory ?? DefaultRootDirectory);

    public IReadOnlyList<SceneAnimationPack> Load(out IReadOnlyList<string> issues)
    {
        var result = new List<SceneAnimationPack>();
        var errors = new List<string>();
        issues = errors.AsReadOnly();
        try
        {
            SceneFileAccess.CheckNoReparsePoints(RootDirectory);
            if (!Directory.Exists(RootDirectory)) return result.AsReadOnly();
            int count = 0;
            foreach (string directory in Directory.EnumerateDirectories(RootDirectory))
            {
                string id = Path.GetFileName(directory);
                if (!IsImportedId(id)) continue;
                if (++count > 1024) { errors.Add("动画包数量超过 1024，请先减少已导入动画包。"); break; }
                try
                {
                    using var stream = SceneFileAccess.OpenChecked(Path.Combine(directory, MetadataName));
                    using var metadata = ReadJson(stream, 16384);
                    var root = metadata.RootElement;
                    RejectUnknown(root, "version", "id", "name", "manifestFile", "importedAtUtc");
                    if (Get(root, "version").GetInt32() != 1 || RequiredString(root, "id") != id)
                        throw Invalid("动画包导入记录不兼容。");
                    string file = NormalizeReference(RequiredString(root, "manifestFile"), ".petanim.json");
                    if (file.Contains('/')) throw Invalid("动画包清单必须位于包目录根部。");
                    result.Add(Validate(Path.Combine(directory, file), id, RequiredString(root, "name")));
                }
                catch (Exception ex) when (Displayable(ex)) { errors.Add(id + "：" + DisplayError(ex)); }
            }
        }
        catch (Exception ex) when (Displayable(ex)) { errors.Add(DisplayError(ex)); }
        return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public SceneAnimationPack Validate(string manifestPath, string id, string? nameOverride = null)
    {
        ValidateId(id);
        using var plan = Inspect(manifestPath, CancellationToken.None);
        return plan.Info(id, nameOverride);
    }

    public IReadOnlyList<string> GetValidatedFiles(SceneAnimationPack pack)
    {
        var checkedPack = Validate(pack.ManifestPath, pack.Id, pack.Name);
        if (!string.Equals(checkedPack.RootDirectory, SceneFileAccess.LocalFullPath(pack.RootDirectory), StringComparison.OrdinalIgnoreCase))
            throw Invalid("动画包根目录与清单不一致。");
        return checkedPack.Files;
    }

    public Task<SceneAnimationPack> ImportAsync(string manifestPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportCore(manifestPath, cancellationToken), cancellationToken);

    private SceneAnimationPack ImportCore(string manifestPath, CancellationToken token)
    {
        string? staging = null;
        bool created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using var plan = Inspect(manifestPath, token);
            string id = "scene-" + Guid.NewGuid().ToString("N");
            string destination = SceneFileAccess.ChildPath(RootDirectory, id);
            SceneFileAccess.EnsureDirectory(RootDirectory);
            staging = SceneFileAccess.ChildPath(RootDirectory, ".import-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(staging) || File.Exists(staging)) throw Invalid("动画导入临时目录已存在，请重试。");
            Directory.CreateDirectory(staging);
            created = true;
            SceneFileAccess.CheckNoReparsePoints(staging);
            foreach (var entry in plan.Entries)
            {
                token.ThrowIfCancellationRequested();
                string output = SceneFileAccess.ChildPath(staging, entry.Relative);
                SceneFileAccess.EnsureDirectory(Path.GetDirectoryName(output)!);
                SceneFileAccess.CheckNoReparsePoints(output);
                entry.Stream.Position = 0;
                using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = entry.Stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    copied += read;
                    if (copied > entry.Stream.Length) throw Invalid("动画资源在复制期间发生变化。");
                    target.Write(buffer, 0, read);
                }
                if (copied != entry.Stream.Length) throw Invalid("动画资源未能完整复制。");
                target.Flush(true);
            }
            string manifestFile = Path.GetFileName(plan.ManifestPath);
            byte[] record = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, id, name = plan.Name, manifestFile, importedAtUtc = DateTimeOffset.UtcNow });
            using (var stream = new FileStream(Path.Combine(staging, MetadataName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(record); stream.Flush(true); }
            var verified = Validate(Path.Combine(staging, manifestFile), id);
            token.ThrowIfCancellationRequested();
            SceneFileAccess.CheckNoReparsePoints(RootDirectory);
            SceneFileAccess.CheckNoReparsePoints(staging);
            if (Directory.Exists(destination) || File.Exists(destination)) throw Invalid("动画包 ID 已存在，未覆盖旧包。");
            Directory.Move(staging, destination);
            created = false;
            return verified with { RootDirectory = destination, ManifestPath = Path.Combine(destination, manifestFile) };
        }
        catch (Exception ex)
        {
            if (created && staging != null)
            {
                try { Live2DModelCatalog.CleanupOwnedDirectory(staging, RootDirectory, ".import-"); }
                catch (Exception cleanup) { throw new IOException("动画包导入未完成，临时目录清理失败；原有动画包未被覆盖。", new AggregateException(ex, cleanup)); }
            }
            if (ex is OperationCanceledException) throw;
            if (Displayable(ex)) throw new InvalidDataException(DisplayError(ex), ex);
            throw;
        }
    }

    private static Plan Inspect(string manifestPath, CancellationToken token)
    {
        string full = SceneFileAccess.LocalFullPath(manifestPath);
        string fileName = NormalizeReference(Path.GetFileName(full), ".petanim.json");
        var plan = new Plan(full);
        try
        {
            var entry = plan.Add(fileName, ".petanim.json", token);
            using var doc = ReadJson(entry.Stream, 1024 * 1024);
            var root = doc.RootElement;
            RejectUnknown(root, "version", "name", "skinId", "anchorX", "anchorY", "actions");
            if (Get(root, "version").ValueKind != JsonValueKind.Number || !Get(root, "version").TryGetInt32(out int version) || version != 1)
                throw Invalid("动画包只支持 version: 1。");
            plan.Name = ValidName(RequiredString(root, "name"));
            plan.SkinId = RequiredString(root, "skinId");
            ValidateId(plan.SkinId);
            plan.AnchorX = Number(root, "anchorX", 0.5);
            plan.AnchorY = Number(root, "anchorY", 1);
            if (plan.AnchorX < 0 || plan.AnchorX > 1 || plan.AnchorY < 0 || plan.AnchorY > 1) throw Invalid("anchorX / anchorY 必须在 0 到 1 之间。");
            var actions = Get(root, "actions");
            RequireObject(actions, "actions");
            foreach (var item in actions.EnumerateObject())
            {
                string actionId = item.Name.ToLowerInvariant();
                if (!ActionNames.Contains(actionId, StringComparer.Ordinal)) throw Invalid("不支持的场景：" + item.Name);
                RejectUnknown(item.Value, "start", "loop", "end", "repeat", "audio", "thoughts");
                var action = new SceneAnimationAction { Loop = ReadClip(Get(item.Value, "loop"), plan, token) };
                if (TryGet(item.Value, "start", out var start) && start.ValueKind != JsonValueKind.Null) action.Start = ReadClip(start, plan, token);
                if (TryGet(item.Value, "end", out var end) && end.ValueKind != JsonValueKind.Null) action.End = ReadClip(end, plan, token);
                if (TryGet(item.Value, "repeat", out var repeat))
                {
                    if (repeat.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("repeat 必须是 true 或 false。");
                    action.Repeat = repeat.GetBoolean();
                }
                if (TryGet(item.Value, "audio", out var audio) && audio.ValueKind != JsonValueKind.Null)
                {
                    RejectUnknown(audio, "file");
                    var resource = plan.Add(RequiredString(audio, "file"), ".mp3|.wav|.ogg", token);
                    ValidateAudio(resource);
                    action.Audio = new SceneAnimationAudio { File = resource.Relative };
                }
                if (TryGet(item.Value, "thoughts", out var thoughts) && thoughts.ValueKind != JsonValueKind.Null)
                {
                    RejectUnknown(thoughts, "title", "lines");
                    string title = RequiredString(thoughts, "title");
                    var linesJson = Get(thoughts, "lines");
                    if (title.Length > 60 || title.Any(char.IsControl) || linesJson.ValueKind != JsonValueKind.Array || linesJson.GetArrayLength() is < 1 or > 12)
                        throw Invalid("思考面板标题最多 60 字，内容必须包含 1–12 行。");
                    var lines = new List<string>();
                    foreach (var line in linesJson.EnumerateArray())
                    {
                        if (line.ValueKind != JsonValueKind.String || line.GetString() is not string text || text.Length is < 1 or > 120 || text.Any(char.IsControl))
                            throw Invalid("思考面板每行须为 1–120 字纯文本，不能含控制字符。");
                        lines.Add(text);
                    }
                    action.Thoughts = new SceneAnimationThoughts { Title = title, Lines = lines.ToArray() };
                }
                plan.Actions.Add(actionId, action);
            }
            if (plan.Actions.Count == 0) throw Invalid("动画包至少需要一个场景动作。");
            return plan;
        }
        catch { plan.Dispose(); throw; }
    }

    private static SceneAnimationClip ReadClip(JsonElement json, Plan plan, CancellationToken token)
    {
        RequireObject(json, "动作片段");
        RejectUnknown(json, "type", "frames", "file", "fps", "frameWidth", "frameHeight", "frameCount", "durationsMs");
        SceneAnimationClip clip;
        try { clip = JsonSerializer.Deserialize<SceneAnimationClip>(json.GetRawText(), ReadOptions) ?? throw Invalid("动作片段为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("动作片段字段类型无效。", ex); }
        clip.Type = (clip.Type ?? "").ToLowerInvariant();
        if (clip.Type is not ("frames" or "apng" or "spritesheet" or "video")) throw Invalid("type 只支持 frames、apng、spritesheet、video。");
        if (!double.IsFinite(clip.Fps) || clip.Fps < 1 || clip.Fps > 60) throw Invalid("fps 必须在 1 到 60 之间。");
        if (clip.Type == "frames")
        {
            if (clip.Frames == null || clip.Frames.Length is < 1 or > MaxSequenceFrames) throw Invalid("PNG 序列必须包含 1–720 帧。");
            if (clip.File != null || clip.FrameWidth != 0 || clip.FrameHeight != 0 || clip.FrameCount != 0) throw Invalid("frames 片段只能引用 frames，不使用 file 或精灵图参数。");
            ImageInfo? first = null;
            for (int i = 0; i < clip.Frames.Length; i++)
            {
                var entry = plan.Add(clip.Frames[i], ".png", token);
                var image = plan.Image(entry, animated: false);
                if (first != null && (first.Width != image.Width || first.Height != image.Height)) throw Invalid("同一 PNG 序列的每一帧必须具有相同画布尺寸。");
                first ??= image;
                clip.Frames[i] = entry.Relative;
            }
            ValidateDurations(clip, clip.Frames.Length);
        }
        else
        {
            if (clip.Frames is { Length: > 0 }) throw Invalid("此片段使用 file，不使用 frames。");
            clip.Frames = Array.Empty<string>();
            var entry = plan.Add(clip.File ?? "", clip.Type == "video" ? ".webm|.mp4" : ".png|.apng", token);
            clip.File = entry.Relative;
            if (clip.Type == "video")
            {
                if (clip.DurationsMs != null || clip.FrameWidth != 0 || clip.FrameHeight != 0 || clip.FrameCount != 0) throw Invalid("视频使用原生尺寸和时序，不使用帧参数。");
                ValidateVideo(entry);
            }
            else
            {
                var image = plan.Image(entry, animated: clip.Type == "apng");
                if (clip.Type == "apng")
                {
                    if (clip.DurationsMs != null || clip.FrameWidth != 0 || clip.FrameHeight != 0 || clip.FrameCount != 0) throw Invalid("APNG 使用文件内的尺寸和时序，不使用帧参数。");
                }
                else
                {
                    if (clip.FrameWidth < 1 || clip.FrameHeight < 1 || clip.FrameWidth > image.Width || clip.FrameHeight > image.Height ||
                        image.Width % clip.FrameWidth != 0 || image.Height % clip.FrameHeight != 0)
                        throw Invalid("精灵图宽高必须是 frameWidth / frameHeight 的整数倍，且不能带间隔。");
                    if (clip.FrameCount is < 1 or > MaxFrames || clip.FrameCount > (image.Width / clip.FrameWidth) * (image.Height / clip.FrameHeight))
                        throw Invalid("精灵图 frameCount 必须在 1–300 之间且不能超过网格容量。");
                    ValidateDurations(clip, clip.FrameCount);
                }
            }
        }
        return clip;
    }

    private static void ValidateDurations(SceneAnimationClip clip, int count)
    {
        if (clip.DurationsMs != null && (clip.DurationsMs.Length != count || clip.DurationsMs.Any(ms => ms < 17 || ms > 60000)))
            throw Invalid("durationsMs 必须与帧数相同，每帧为 17–60000 毫秒。");
        double duration = clip.DurationsMs?.Sum(ms => (long)ms) / 1000.0 ?? count / clip.Fps;
        if (duration > 120) throw Invalid("单个动作片段最多 120 秒。");
    }

    private sealed class Plan : IDisposable
    {
        public string ManifestPath { get; }
        public string Root { get; }
        public string Name = "";
        public string SkinId = "";
        public double AnchorX = 0.5, AnchorY = 1;
        public Dictionary<string, SceneAnimationAction> Actions = new(StringComparer.OrdinalIgnoreCase);
        public List<Entry> Entries { get; } = new();
        private readonly Dictionary<string, Entry> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ImageInfo> images = new(StringComparer.OrdinalIgnoreCase);
        private long totalBytes, totalPixels;
        public Plan(string path) { ManifestPath = path; Root = Path.GetDirectoryName(path)!; }
        public Entry Add(string relative, string extensions, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string normalized = NormalizeReference(relative, extensions);
            if (files.TryGetValue(normalized, out var existing)) return existing;
            if (Entries.Count >= MaxFiles) throw Invalid("一个动画包最多引用 2048 个文件。");
            var stream = SceneFileAccess.OpenChecked(SceneFileAccess.ChildPath(Root, normalized));
            try
            {
                if (stream.Length <= 0 || stream.Length > MaxFileBytes) throw Invalid("动画资源为空或超过单文件 96 MiB 上限：" + normalized);
                if (totalBytes + stream.Length > MaxPackageBytes) throw Invalid("动画包资源总大小超过 512 MiB 上限。");
                var entry = new Entry(normalized, stream);
                files.Add(normalized, entry); Entries.Add(entry); totalBytes += stream.Length;
                return entry;
            }
            catch { stream.Dispose(); throw; }
        }
        public ImageInfo Image(Entry entry, bool animated)
        {
            if (!images.TryGetValue(entry.Relative, out var info))
            {
                info = ReadPng(entry.Stream, entry.Relative);
                long pixels = (long)info.Width * info.Height * Math.Max(1, info.Frames);
                if (pixels > MaxDecodedPixels)
                    throw Invalid("单个 PNG、精灵图或 APNG 展开后超过 64 Mi 像素上限，请降低分辨率或帧数。");
                if (totalPixels + pixels > MaxPackagePixels)
                    throw Invalid("动画包资源累计超过 512 Mi 像素上限，请拆分动画包或降低分辨率。");
                images.Add(entry.Relative, info); totalPixels += pixels;
            }
            if (animated != (info.Frames > 0)) throw Invalid(animated ? "APNG 片段必须使用包含动画帧的 APNG 文件。" : "PNG 序列和精灵图必须使用静态 PNG。");
            return info;
        }
        public SceneAnimationPack Info(string id, string? nameOverride) => new(id, nameOverride == null ? Name : ValidName(nameOverride), SkinId, Root, ManifestPath,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, SceneAnimationAction>(Actions), Entries.Select(e => e.Relative).ToArray(), AnchorX, AnchorY);
        public void Dispose() { foreach (var entry in Entries) entry.Stream.Dispose(); }
    }
    private sealed record Entry(string Relative, FileStream Stream);
    private sealed record ImageInfo(int Width, int Height, int Frames);

    private static ImageInfo ReadPng(FileStream stream, string label)
    {
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Invalid("不是有效 PNG/APNG：" + label);
        int width = 0, height = 0, frames = 0, controls = 0, chunks = 0;
        bool hasData = false, ended = false;
        double totalDuration = 0;
        uint nextSequence = 0;
        byte[] head = new byte[8];
        while (stream.Position < stream.Length)
        {
            if (++chunks > 10000 || stream.Length - stream.Position < 12) throw Invalid("PNG 数据块数量或长度无效：" + label);
            stream.ReadExactly(head);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4));
            string kind = Encoding.ASCII.GetString(head, 4, 4);
            if (length > MaxFileBytes || length + 4L > stream.Length - stream.Position) throw Invalid("PNG 数据块越界：" + label);
            long end = stream.Position + length + 4;
            if (chunks == 1 && kind != "IHDR") throw Invalid("PNG 缺少 IHDR：" + label);
            if (kind == "IHDR")
            {
                if (chunks != 1 || length != 13) throw Invalid("PNG 头格式无效：" + label);
                byte[] data = new byte[13]; stream.ReadExactly(data);
                uint w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)), h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
                if (w == 0 || h == 0 || w > 4096 || h > 4096) throw Invalid("动画图片单边必须为 1–4096 像素。");
                width = (int)w; height = (int)h;
                if (data[10] != 0 || data[11] != 0 || data[12] > 1) throw Invalid("PNG 编码参数无效。");
            }
            else if (kind == "acTL")
            {
                if (length != 8 || hasData || frames != 0) throw Invalid("APNG acTL 数据无效。");
                byte[] data = new byte[8]; stream.ReadExactly(data);
                uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
                if (count is < 1 or > MaxFrames) throw Invalid("APNG 必须包含 1–300 帧。");
                frames = (int)count;
            }
            else if (kind == "fcTL")
            {
                if (length != 26 || frames == 0 || ++controls > frames) throw Invalid("APNG 帧控制无效。");
                byte[] data = new byte[26]; stream.ReadExactly(data);
                if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) != nextSequence++) throw Invalid("APNG 帧序号不连续。");
                uint w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)), h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
                uint x = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4)), y = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
                if (w == 0 || h == 0 || (ulong)x + w > (uint)width || (ulong)y + h > (uint)height || data[24] > 2 || data[25] > 1) throw Invalid("APNG 帧超出画布或混合参数无效。");
                int numerator = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20, 2)), denominator = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22, 2));
                double seconds = numerator / (double)(denominator == 0 ? 100 : denominator);
                if (seconds < 0.01 || seconds > 60 || (totalDuration += seconds) > 120) throw Invalid("APNG 帧时长须为 10–60000 毫秒，单片段最多 120 秒。");
            }
            else if (kind == "fdAT")
            {
                if (length < 5 || frames == 0 || controls == 0) throw Invalid("APNG 帧数据无效。");
                byte[] sequence = new byte[4]; stream.ReadExactly(sequence);
                if (BinaryPrimitives.ReadUInt32BigEndian(sequence) != nextSequence++) throw Invalid("APNG 帧序号不连续。");
            }
            else if (kind == "IDAT") hasData = true;
            else if (kind == "IEND") { if (length != 0) throw Invalid("PNG 结束块无效。"); ended = true; }
            stream.Position = end;
            if (ended) break;
        }
        stream.Position = 0;
        if (!ended || !hasData || (frames > 0 && controls != frames)) throw Invalid("PNG/APNG 数据不完整：" + label);
        return new ImageInfo(width, height, frames);
    }

    private static void ValidateAudio(Entry entry)
    {
        var stream = entry.Stream;
        if (stream.Length > 32L * 1024 * 1024) throw Invalid("音频文件不能超过 32 MiB。");
        stream.Position = 0;
        byte[] data = new byte[(int)Math.Min(16, stream.Length)]; stream.ReadExactly(data); stream.Position = 0;
        bool valid = Path.GetExtension(entry.Relative).ToLowerInvariant() switch
        {
            ".mp3" => data.Length >= 3 && (data.AsSpan(0, 3).SequenceEqual("ID3"u8) || data[0] == 0xff && (data[1] & 0xe0) == 0xe0 && (data[1] & 0x06) != 0 && (data[2] & 0xf0) is not (0 or 0xf0)),
            ".wav" => data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WAVE"u8),
            ".ogg" => data.Length >= 5 && data.AsSpan(0, 4).SequenceEqual("OggS"u8) && data[4] == 0,
            _ => false
        };
        if (!valid) throw Invalid("音频文件头与 MP3 / WAV / OGG 类型不一致：" + entry.Relative);
        // The isolated decoder verifies actual duration (<= 10 minutes) before playback.
    }

    private static void ValidateVideo(Entry entry)
    {
        var stream = entry.Stream;
        stream.Position = 0;
        byte[] data = new byte[(int)Math.Min(16, stream.Length)]; stream.ReadExactly(data); stream.Position = 0;
        bool webm = entry.Relative.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        if (webm && (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) ||
            !webm && (data.Length < 12 || !data.AsSpan(4, 4).SequenceEqual("ftyp"u8)))
            throw Invalid("视频文件头与 WebM / MP4 类型不一致：" + entry.Relative);
        // The isolated player also verifies decoded dimensions, duration and codec support before showing the scene.
    }

    private static JsonDocument ReadJson(FileStream stream, int maximum)
    {
        if (stream.Length < 2 || stream.Length > maximum) throw Invalid("动画 JSON 清单为空或过大。");
        stream.Position = 0;
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); stream.Position = 0;
        int offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
        var doc = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 16 });
        try { RequireObject(doc.RootElement, "清单"); RejectDuplicates(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!keys.Add(property.Name)) throw Invalid("清单存在重复字段：" + property.Name); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }
    private static void RejectUnknown(JsonElement element, params string[] names)
    {
        RequireObject(element, "清单对象");
        foreach (var property in element.EnumerateObject()) if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) throw Invalid("暂不支持的动画字段：" + property.Name);
    }
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
    private static JsonElement Get(JsonElement element, string name) => TryGet(element, name, out var value) ? value : throw Invalid("动画清单缺少字段：" + name);
    private static string RequiredString(JsonElement element, string name) => Get(element, name) is var value && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw Invalid(name + " 必须为非空字符串。");
    private static double Number(JsonElement element, string name, double fallback)
    {
        if (!TryGet(element, name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number)) throw Invalid(name + " 必须为有限数字。");
        return number;
    }
    private static void RequireObject(JsonElement element, string name) { if (element.ValueKind != JsonValueKind.Object) throw Invalid(name + " 必须为 JSON 对象。"); }
    private static string ValidName(string name) => name.Trim().Length is >= 1 and <= 80 && !name.Any(char.IsControl) ? name.Trim() : throw Invalid("动画名称须为 1–80 个不含控制字符的字符。");
    private static void ValidateId(string id) { if (id.Length is < 1 or > 80 || id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))) throw Invalid("动画或皮肤 ID 只能包含字母、数字、横线和下划线。"); }
    private static bool IsImportedId(string id) => id.StartsWith("scene-", StringComparison.Ordinal) && id.Length == 38 && Guid.TryParseExact(id[6..], "N", out _);
    private static InvalidDataException Invalid(string message) => new(message);
    private static bool Displayable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or Win32Exception or InvalidOperationException or FormatException or OverflowException;
    private static string DisplayError(Exception ex) => ex switch { InvalidDataException => ex.Message, UnauthorizedAccessException => "无法访问动画资源，请检查文件夹权限。", Win32Exception => "无法核验动画资源位置，请使用本机普通文件夹。", _ => "动画文件读取或复制失败：" + ex.Message };
    private static string NormalizeReference(string reference, string extensions)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 240 || Path.IsPathRooted(reference) || reference.StartsWith('/') || reference.StartsWith('\\') || reference.Any(ch => ch < 32 || ":?#%<>|\"*".Contains(ch))) throw Invalid("动画资源须使用本机包内相对路径，不能是 URL、绝对路径或特殊路径。");
        string[] segments = reference.Replace('\\', '/').Split('/');
        if (segments.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || IsDeviceName(part))) throw Invalid("动画资源路径含有不允许的目录段或设备名。");
        string result = string.Join('/', segments);
        if (!extensions.Split('|').Any(ext => result.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) || result.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) throw Invalid("动画资源类型不受支持；需要 " + extensions + "。");
        return result;
    }
    private static bool IsDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && (name[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }
}

// Uses the same parent-handle checks as the Live2D importer; legitimate MSIX redirection is supported.
internal static class SceneFileAccess
{
    internal static string LocalFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
            throw Invalid("请选择本机磁盘中的动画目录或文件，不支持网络和设备路径。");
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("动画文件路径无效。", ex); }
    }
    internal static string ChildPath(string root, string relative)
    {
        string fullRoot = LocalFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Invalid("动画资源路径超出允许目录。");
        return path;
    }
    internal static void EnsureDirectory(string path)
    {
        CheckNoReparsePoints(path);
        Directory.CreateDirectory(path);
        CheckNoReparsePoints(path);
    }
    internal static void CheckNoReparsePoints(string path)
    {
        string? part = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(part))
        {
            try
            {
                var attrs = File.GetAttributes(part);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw Invalid("动画路径不能经过符号链接或目录联接点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            part = Path.GetDirectoryName(part);
        }
    }
    internal static FileStream OpenChecked(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("安全动画导入仅支持 Windows。");
        CheckNoReparsePoints(path);
        // MSIX can legitimately redirect an AppData directory into its package
        // cache. Pin every lexical ancestor without following a reparse point,
        // then compare the opened file with that parent's actual location.
        // Denying delete sharing prevents an ancestor being replaced mid-check.
        using var parents = PinParentDirectories(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException ex) { throw new InvalidDataException("缺少动画引用资源：" + Path.GetFileName(path), ex); }
        catch (DirectoryNotFoundException ex) { throw new InvalidDataException("缺少动画资源目录：" + Path.GetFileName(path), ex); }
        try
        {
            string actual = FinalHandlePath(stream.SafeFileHandle);
            string actualParent = FinalHandlePath(parents.Handles[^1]);
            string expected = Path.Combine(actualParent, Path.GetFileName(path));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw Invalid("动画文件解析到其它位置，可能包含符号链接。");
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
                    throw Invalid("动画路径不能经过符号链接或目录联接点。");
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

    private static InvalidDataException Invalid(string message) => new(message);
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

