using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopPet;

internal sealed record PetVoiceDesignSample(string SkinId, string Instruction, string Text, string Language, byte[] Wave);
internal sealed record PetSavedVoiceInfo(string ReferenceId, string SkinId, string Instruction, string Text, string Language,
    string SourceModel, string PlaybackModel, string Sha256, DateTimeOffset CreatedUtc, double DurationSeconds, string Name = "");

// User-owned samples are separate from the installed application's bundled
// voices. Each successful save publishes a new immutable directory; settings
// may select it only after this method returns, so a failure preserves the old voice.
internal sealed class PetVoiceLibrary
{
    internal const int MaximumWaveBytes = 10 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "voices");
    internal string RootPath { get; }

    internal PetVoiceLibrary(string? root = null)
    {
        RootPath = Path.GetFullPath(root ?? DefaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(Path.GetDirectoryName(RootPath))) throw new ArgumentException("音色保存目录无效。", nameof(root));
    }

    internal PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null)
    {
        ValidateSample(sample);
        DateTimeOffset created = DateTimeOffset.UtcNow;
        string displayName = name == null ? AutomaticName(sample.SkinId, created) : ValidateName(name);
        // Never retain the caller's mutable playback buffer while publishing.
        byte[] audio = (byte[])sample.Wave.Clone();
        TimeSpan duration = ValidateWave(audio);
        string id = Guid.NewGuid().ToString("N");
        string target = Child(id), pending = Child(".pending-" + id);
        EnsureNoReparsePoints(RootPath);
        Directory.CreateDirectory(RootPath);
        EnsureNoReparsePoints(RootPath);
        if (Directory.Exists(target) || File.Exists(target) || Directory.Exists(pending) || File.Exists(pending))
            throw new IOException("无法创建新的固定音色，请重试。");
        Directory.CreateDirectory(pending);
        try
        {
            EnsureNoReparsePoints(pending);
            var info = new PetSavedVoiceInfo(id, sample.SkinId.ToLowerInvariant(), sample.Instruction.Trim(), sample.Text.Trim(), sample.Language,
                "Qwen3-TTS-12Hz-1.7B-VoiceDesign", "Qwen3-TTS-12Hz-1.7B-Base",
                Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant(), created, duration.TotalSeconds, displayName);
            WriteNew(pending, "reference.wav", audio);
            WriteNew(pending, "reference.txt", Utf8.GetBytes(info.Text + "\n"));
            WriteNew(pending, "metadata.json", Utf8.GetBytes(JsonSerializer.Serialize(info, JsonOptions) + "\n"));
            EnsureNoReparsePoints(pending);
            EnsureNoReparsePoints(RootPath);
            Directory.Move(pending, target);
            return new PetVoiceProfile("saved-voice", "Vivian", info.Language, "", id);
        }
        finally { CleanupPending(pending, id); }
    }

    internal bool TryReadMetadata(string? referenceId, out PetSavedVoiceInfo? info)
    {
        info = null;
        if (!PetVoiceProfiles.IsReferenceId(referenceId)) return false;
        try
        {
            string directory = Child(referenceId!);
            EnsureNoReparsePoints(directory);
            string path = Path.Combine(directory, "metadata.json");
            EnsureNoReparsePoints(path);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 16384) return false;
            var read = JsonSerializer.Deserialize<PetSavedVoiceInfo>(File.ReadAllText(path, Utf8), JsonOptions);
            if (read == null || read.ReferenceId != referenceId || !PetVoiceProfiles.KnownSkin(read.SkinId) ||
                !ValidText(read.Instruction, 600) || !ValidText(read.Text, 100) ||
                !PetVoiceProfiles.Languages.Contains(read.Language, StringComparer.Ordinal) ||
                read.SourceModel != "Qwen3-TTS-12Hz-1.7B-VoiceDesign" || read.PlaybackModel != "Qwen3-TTS-12Hz-1.7B-Base" ||
                read.Sha256 is not { Length: 64 } || read.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                !double.IsFinite(read.DurationSeconds) || read.DurationSeconds is < 1 or > 30)
                return false;
            // Existing libraries predate names. A read adds a readable label in
            // memory without rewriting or touching the user's audio files.
            string name = string.IsNullOrEmpty(read.Name) ? AutomaticName(read.SkinId, read.CreatedUtc) : ValidateName(read.Name);
            info = read with { Name = name };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    internal IReadOnlyList<PetSavedVoiceInfo> ListForSkin(string skinId)
    {
        if (!PetVoiceProfiles.KnownSkin(skinId)) return Array.Empty<PetSavedVoiceInfo>();
        try
        {
            EnsureNoReparsePoints(RootPath);
            if (!Directory.Exists(RootPath)) return Array.Empty<PetSavedVoiceInfo>();
            // Read-only and bounded even if this user directory contains many
            // unrelated entries. Pending saves never participate in selection.
            var candidates = Directory.EnumerateDirectories(RootPath)
                .Where(path => PetVoiceProfiles.IsReferenceId(Path.GetFileName(path))).Take(1000)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => PetVoiceProfiles.IsReferenceId(directory.Name) &&
                    (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(directory => directory.LastWriteTimeUtc);
            var result = new List<PetSavedVoiceInfo>();
            foreach (var directory in candidates)
            {
                if (!TryReadMetadata(directory.Name, out var info) || info == null ||
                    !string.Equals(info.SkinId, skinId, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(info);
            }
            return result.OrderByDescending(info => info.CreatedUtc).ThenByDescending(info => info.ReferenceId, StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Array.Empty<PetSavedVoiceInfo>(); }
    }

    internal PetSavedVoiceInfo? FindLatestForSkin(string skinId) => ListForSkin(skinId).FirstOrDefault();

    internal PetSavedVoiceInfo Rename(string referenceId, string name)
    {
        RequireCustomReference(referenceId);
        string displayName = ValidateName(name);
        if (!TryReadMetadata(referenceId, out var previous) || previous == null)
            throw new IOException("找不到此自定义音色，或音色记录已损坏。");
        var renamed = previous with { Name = displayName };
        string directory = Child(referenceId), metadata = Path.Combine(directory, "metadata.json");
        string pendingName = ".metadata-" + Guid.NewGuid().ToString("N") + ".tmp";
        string pending = Path.Combine(directory, pendingName);
        EnsureNoReparsePoints(metadata);
        try
        {
            WriteNew(directory, pendingName, Utf8.GetBytes(JsonSerializer.Serialize(renamed, JsonOptions) + "\n"));
            EnsureNoReparsePoints(directory);
            EnsureNoReparsePoints(metadata);
            EnsureNoReparsePoints(pending);
            // Replacement is atomic. The previous metadata remains intact if
            // publishing fails; audio and reference text are never rewritten.
            File.Replace(pending, metadata, null);
            return renamed;
        }
        finally
        {
            try { EnsureNoReparsePoints(pending); if (File.Exists(pending)) File.Delete(pending); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal void Delete(string referenceId)
    {
        RequireCustomReference(referenceId);
        if (!TryReadMetadata(referenceId, out _))
            throw new IOException("找不到此自定义音色，或音色记录已损坏。");
        string source = Child(referenceId), removed = Child(".trash-" + referenceId);
        EnsureNoReparsePoints(source);
        EnsureNoReparsePoints(Path.Combine(source, "reference.wav"));
        EnsureNoReparsePoints(Path.Combine(source, "reference.txt"));
        EnsureNoReparsePoints(Path.Combine(source, "metadata.json"));
        EnsureNoReparsePoints(removed);
        if (Directory.Exists(removed) || File.Exists(removed)) throw new IOException("此音色已有恢复副本，请先检查本机音色库。");
        // This is a reversible removal, not recursive deletion. The published
        // ID disappears atomically; all bytes remain in the private trash folder.
        // A failure leaves the original version and every other voice untouched.
        Directory.Move(source, removed);
    }

    private static void RequireCustomReference(string? referenceId)
    {
        if (!PetVoiceProfiles.IsReferenceId(referenceId))
            throw new ArgumentException("只能修改自定义音色；默认音色受到保护。");
    }

    private static string AutomaticName(string skinId, DateTimeOffset created) =>
        PetVoiceProfiles.CharacterName(skinId) + " · " + created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    internal static string ValidateName(string? name)
    {
        // Names are metadata only. In particular punctuation in a display name
        // never becomes part of a directory, filename, or reference identifier.
        if (name == null || !ValidText(name, 120) || name.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new ArgumentException("音色名称须为 1–60 个字符的单行文字。");
        string trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed is "." or ".." || trimmed.EnumerateRunes().Count() > 60)
            throw new ArgumentException("音色名称须为 1–60 个字符的单行文字。");
        return trimmed;
    }

    internal static void ValidateSample(PetVoiceDesignSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!PetVoiceProfiles.KnownSkin(sample.SkinId)) throw new ArgumentException("请选择已知的桌宠角色。");
        if (!ValidText(sample.Instruction, 600)) throw new ArgumentException("请输入 1–600 字的音色描述。");
        if (!ValidText(sample.Text, 100)) throw new ArgumentException("试听台词应为 1–100 字，请使用普通文本。");
        if (!PetVoiceProfiles.Languages.Contains(sample.Language, StringComparer.Ordinal)) throw new ArgumentException("不支持该语音语言。");
        ValidateWave(sample.Wave);
    }

    internal static TimeSpan ValidateWave(byte[]? wave)
    {
        if (wave == null || wave.Length < 44 || wave.Length > MaximumWaveBytes) throw InvalidWave();
        // Shared parsing checks RIFF length, duplicated/truncated chunks, PCM16
        // encoding, block alignment and data size before deriving the duration.
        TimeSpan duration = PetSpeechWave.Duration(wave);
        if (duration.TotalSeconds is < 1 or > 30) throw new InvalidOperationException("固定音色的参考音频需为 1–30 秒，请调整试听台词后重新生成。");
        for (int offset = 12; offset < wave.Length;)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(offset + 4, 4));
            if (wave.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                ReadOnlySpan<byte> format = wave.AsSpan(offset + 8, 16);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(format[4..]) != 24000) throw InvalidWave();
            }
            offset = checked(offset + 8 + (int)size + ((int)size & 1));
        }
        return duration;
    }

    private static bool ValidText(string? text, int maximum)
    {
        if (text == null || text.Length > maximum || string.IsNullOrWhiteSpace(text)) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) && c is not ('\n' or '\t')) return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }
    private static InvalidOperationException InvalidWave() => new("试听音频必须是 24 kHz、单声道、16 位 PCM WAV，大小不超过 10 MiB。");

    private string Child(string name)
    {
        string full = Path.GetFullPath(Path.Combine(RootPath, name));
        if (!full.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(full), RootPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("音色路径无效。");
        return full;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("固定音色目录不能使用链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void WriteNew(string directory, string name, byte[] data)
    {
        EnsureNoReparsePoints(directory);
        using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(true);
    }

    private void CleanupPending(string pending, string id)
    {
        // Only the exact temporary version created by this save is eligible for
        // cleanup. No recursive deletion and no deletion of published voices.
        if (!string.Equals(pending, Child(".pending-" + id), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!Directory.Exists(pending)) return;
            EnsureNoReparsePoints(pending);
            foreach (string name in new[] { "reference.wav", "reference.txt", "metadata.json" })
            {
                string path = Path.Combine(pending, name);
                EnsureNoReparsePoints(path);
                if (File.Exists(path)) File.Delete(path);
            }
            Directory.Delete(pending, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
