using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopPet;

// Saved per skin. This is an immutable selection, never a model path or a URL.
internal sealed record PetVoiceProfile(string ModelId, string Speaker, string Language, string Instruct, string? ReferenceId = null);
internal sealed record PetVoiceChoice(string Id, string Label);

internal static class PetVoiceProfiles
{
    internal static readonly PetVoiceChoice[] Characters = {
        new("deepseek", "DeepSeek"), new("gemini", "Gemini"), new("claude", "Claude"),
        new("gpt", "GPT 小龙娘"), new("grok", "Grok"), new("glm", "GLM"),
        new("kimi", "Kimi"), new("minimax", "MiniMax"), new("qwen", "Qwen")
    };
    internal static readonly PetVoiceChoice[] Models = {
        new("disabled", "不启用此角色语音"), new("base", "Base 1.7B · Let Me Go 参考音色"),
        new("custom-voice", "CustomVoice 1.7B · 预置音色"), new("voice-design", "VoiceDesign 1.7B · 文字设计音色"),
        new("gpt-fixed", "Base 1.7B · GPT 龙娘固定音色"),
        new("saved-voice", "Base 1.7B · 我的固定音色")
    };
    internal static readonly PetVoiceChoice[] Speakers = {
        new("Vivian", "Vivian · 明亮女声 / 中文"), new("Serena", "Serena · 温柔女声 / 中文"),
        new("Uncle_Fu", "Uncle Fu · 浑厚男声 / 中文"), new("Dylan", "Dylan · 北京男声"),
        new("Eric", "Eric · 四川男声"), new("Ryan", "Ryan · 男声 / 英语"),
        new("Aiden", "Aiden · 清朗男声 / 英语"), new("Ono_Anna", "Ono Anna · 轻快女声 / 日语"),
        new("Sohee", "Sohee · 温暖女声 / 韩语")
    };
    internal static readonly string[] Languages = { "Chinese", "English", "Japanese", "Korean", "German", "French", "Russian", "Portuguese", "Spanish", "Italian" };
    internal static bool KnownSkin(string? skinId) => Characters.Any(x => string.Equals(x.Id, skinId, StringComparison.OrdinalIgnoreCase));
    internal static string CharacterName(string skinId) => Characters.FirstOrDefault(x => string.Equals(x.Id, skinId, StringComparison.OrdinalIgnoreCase))?.Label ?? skinId;
    internal static string ModelDisplayName(string? modelId) => Models.FirstOrDefault(x => x.Id == modelId)?.Label ?? Models[0].Label;
    internal static bool IsEnabled(PetVoiceProfile? profile) => profile?.ModelId is "base" or "custom-voice" or "voice-design" or "gpt-fixed" ||
        (profile?.ModelId == "saved-voice" && IsReferenceId(profile.ReferenceId));
    internal static bool IsReferenceId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static PetVoiceProfile DefaultForSkin(string skinId) => new(
        skinId?.ToLowerInvariant() switch { "deepseek" => "base", "gpt" => "gpt-fixed", _ => "disabled" }, "Vivian", "Chinese", "");
    internal static string DefaultInstruction(string skinId) => string.Equals(skinId, "gpt", StringComparison.OrdinalIgnoreCase)
        ? "清亮但不尖锐的年轻成年女声，普通话清晰，带一点优雅、自信和小骄傲。平时轻快俏皮，认真时温柔可靠，避免过度幼态和夸张夹音。"
        : "自然清晰的中文女声，亲切、轻快，语速适中，适合日常陪伴对话。";

    internal static PetVoiceProfile Normalize(PetVoiceProfile? profile, string skinId)
    {
        var fallback = DefaultForSkin(skinId);
        if (profile == null) return fallback;
        string model = (profile.ModelId ?? "").Trim().ToLowerInvariant();
        if (!Models.Any(x => x.Id == model)) model = fallback.ModelId;
        string speaker = Speakers.FirstOrDefault(x => string.Equals(x.Id, profile.Speaker, StringComparison.OrdinalIgnoreCase))?.Id ?? "Vivian";
        string language = Languages.FirstOrDefault(x => string.Equals(x, profile.Language, StringComparison.OrdinalIgnoreCase)) ?? "Chinese";
        string instruct = new string((profile.Instruct ?? "").Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(600).ToArray()).Trim();
        if (model == "voice-design" && instruct.Length == 0) instruct = DefaultInstruction(skinId);
        // The generated reference defines this preset; descriptive prompts are
        // not carried into subsequent speech or persisted as an active setting.
        if (model is "gpt-fixed" or "saved-voice") instruct = "";
        string? referenceId = model == "saved-voice" && IsReferenceId(profile.ReferenceId) ? profile.ReferenceId : null;
        // A broken saved reference must never silently become the DeepSeek voice.
        if (model == "saved-voice" && referenceId == null) model = "disabled";
        return new(model, speaker, language, instruct, referenceId);
    }

    internal static Dictionary<string, PetVoiceProfile> NormalizeProfiles(Dictionary<string, PetVoiceProfile>? profiles)
    {
        var result = new Dictionary<string, PetVoiceProfile>(StringComparer.OrdinalIgnoreCase);
        if (profiles != null)
            foreach (var pair in profiles)
                if (KnownSkin(pair.Key)) result[pair.Key.ToLowerInvariant()] = Normalize(pair.Value, pair.Key);
        return result;
    }

    internal static string? ModelFolderName(string modelId) => modelId switch
    {
        "base" or "gpt-fixed" or "saved-voice" => "Qwen3-TTS-12Hz-1.7B-Base", "custom-voice" => "Qwen3-TTS-12Hz-1.7B-CustomVoice",
        "voice-design" => "Qwen3-TTS-12Hz-1.7B-VoiceDesign", _ => null
    };

    // Read-only discovery, matching the worker's conventional root and sibling weights.
    // Opening the dashboard must not start Python, import torch or allocate VRAM.
    internal static bool IsModelInstalled(string modelId, string? start = null)
    {
        string? name = ModelFolderName(modelId);
        if (name == null) return false;
        try
        {
            for (var directory = new DirectoryInfo(start ?? AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, ".tools", "deepseek-voice-models", name, "model.safetensors"))) return true;
                foreach (string service in new[] { Path.Combine(directory.FullName, "voice-service"), Path.Combine(directory.FullName, "desktop-pet", "voice-service") })
                {
                    string config = Path.Combine(service, "config.json");
                    if (!File.Exists(config)) continue;
                    using var data = JsonDocument.Parse(File.ReadAllText(config));
                    if (!data.RootElement.TryGetProperty("model_path", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) continue;
                    string basePath = Path.GetFullPath(value.GetString()!, service);
                    string path = modelId is "base" or "gpt-fixed" or "saved-voice" ? basePath : Path.Combine(Path.GetDirectoryName(basePath)!, name);
                    if (File.Exists(Path.Combine(path, "model.safetensors"))) return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return false;
    }
}
