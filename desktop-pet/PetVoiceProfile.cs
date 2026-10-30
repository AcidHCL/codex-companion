using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopPet;

// Saved per skin. This is an immutable selection, never a model path or a URL.
internal sealed record PetVoiceProfile(string ModelId, string Speaker, string Language, string Instruct, string? ReferenceId = null, bool? FollowAppLanguage = null);
internal sealed record PetVoiceChoice(string Id, string Label);

internal static class PetVoiceProfiles
{
    internal static PetVoiceChoice[] Characters => new PetVoiceChoice[] {
        new("deepseek", "DeepSeek"), new("gemini", "Gemini"), new("claude", "Claude"),
        new("gpt", L.T("GPT 小龙娘", "GPT Little Dragon")), new("grok", "Grok"), new("glm", "GLM"),
        new("kimi", "Kimi"), new("minimax", "MiniMax"), new("qwen", "Qwen")
    };
    internal static PetVoiceChoice[] Models => new PetVoiceChoice[] {
        new("disabled", L.T("不启用此角色语音", "Disable speech for this character")), new("base", L.T("Base 1.7B · Let Me Go 参考音色", "Base 1.7B · Let Me Go reference voice")),
        new("custom-voice", L.T("CustomVoice 1.7B · 预置音色", "CustomVoice 1.7B · Preset speakers")), new("voice-design", L.T("VoiceDesign 1.7B · 文字设计音色", "VoiceDesign 1.7B · Voice from a description")),
        new("gpt-fixed", L.T("Base 1.7B · GPT 龙娘固定音色", "Base 1.7B · GPT Dragon saved voice")),
        new("saved-voice", L.T("Base 1.7B · 我的固定音色", "Base 1.7B · My saved voice"))
    };
    internal static PetVoiceChoice[] Speakers => new PetVoiceChoice[] {
        new("Vivian", L.T("Vivian · 明亮女声 / 中文", "Vivian · Bright female / Chinese")), new("Serena", L.T("Serena · 温柔女声 / 中文", "Serena · Gentle female / Chinese")),
        new("Uncle_Fu", L.T("Uncle Fu · 浑厚男声 / 中文", "Uncle Fu · Rich male / Chinese")), new("Dylan", L.T("Dylan · 北京男声", "Dylan · Male / Beijing Mandarin")),
        new("Eric", L.T("Eric · 四川男声", "Eric · Male / Sichuan dialect")), new("Ryan", L.T("Ryan · 男声 / 英语", "Ryan · Male / English")),
        new("Aiden", L.T("Aiden · 清朗男声 / 英语", "Aiden · Clear male / English")), new("Ono_Anna", L.T("Ono Anna · 轻快女声 / 日语", "Ono Anna · Lively female / Japanese")),
        new("Sohee", L.T("Sohee · 温暖女声 / 韩语", "Sohee · Warm female / Korean"))
    };
    internal static readonly string[] Languages = { "Chinese", "English", "Japanese", "Korean", "German", "French", "Russian", "Portuguese", "Spanish", "Italian" };
    internal static bool KnownSkin(string? skinId) => Characters.Any(x => string.Equals(x.Id, skinId, StringComparison.OrdinalIgnoreCase));
    internal static string CharacterName(string skinId) => Characters.FirstOrDefault(x => string.Equals(x.Id, skinId, StringComparison.OrdinalIgnoreCase))?.Label ?? skinId;
    internal static string ModelDisplayName(string? modelId) => Models.FirstOrDefault(x => x.Id == modelId)?.Label ?? Models[0].Label;
    internal static bool IsEnabled(PetVoiceProfile? profile) => profile?.ModelId is "base" or "custom-voice" or "voice-design" or "gpt-fixed" ||
        (profile?.ModelId == "saved-voice" && IsReferenceId(profile.ReferenceId));
    internal static bool IsReferenceId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static PetVoiceProfile DefaultForSkin(string skinId) => new(
        skinId?.ToLowerInvariant() switch { "deepseek" => "base", "gpt" => "gpt-fixed", _ => "disabled" }, "Vivian", "Chinese", "", FollowAppLanguage: true);
    internal static string DefaultInstruction(string skinId) => DefaultInstructionForLanguage(skinId, L.IsEnglish);

    private static string DefaultInstructionForLanguage(string skinId, bool english) => string.Equals(skinId, "gpt", StringComparison.OrdinalIgnoreCase)
        ? english ? "A clear, warm young adult female voice, elegant, confident and slightly proud. Speak natural, clearly articulated English, playful in casual conversation and gentle when serious. Avoid shrill, childish or exaggerated delivery."
            : "清亮但不尖锐的年轻成年女声，普通话清晰，带一点优雅、自信和小骄傲。平时轻快俏皮，认真时温柔可靠，避免过度幼态和夸张夹音。"
        : english ? "A natural, clear female voice, warm and lively, speaking conversational English at a moderate pace for everyday companionship."
            : "自然清晰的中文女声，亲切、轻快，语速适中，适合日常陪伴对话。";

    // Legacy built-in references follow the application language. Explicit
    // custom language settings and all saved reference audio remain untouched.
    internal static bool FollowsAppLanguage(PetVoiceProfile profile) =>
        profile.FollowAppLanguage ?? ((profile.ModelId is "base" or "gpt-fixed") &&
            string.Equals(profile.Language, "Chinese", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(profile.Instruct));

    internal static PetVoiceProfile ResolveForPlayback(PetVoiceProfile profile)
    {
        if (!FollowsAppLanguage(profile)) return profile;
        string instruction = profile.Instruct;
        foreach (string role in new[] { "gpt", "deepseek" })
            if (instruction == DefaultInstructionForLanguage(role, false) || instruction == DefaultInstructionForLanguage(role, true))
            { instruction = DefaultInstructionForLanguage(role, L.IsEnglish); break; }
        return profile with { Language = L.IsEnglish ? "English" : "Chinese", Instruct = instruction };
    }

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
        return new(model, speaker, language, instruct, referenceId, profile.FollowAppLanguage);
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
