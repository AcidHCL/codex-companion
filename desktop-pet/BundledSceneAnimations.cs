using System;
using System.IO;
using System.Linq;

namespace DesktopPet;

// Built-in skin defaults live with the application and never need to be imported
// into the user's library. Keep the mapping explicit so other skins stay unchanged.
internal static class BundledSceneAnimations
{
    public const string DeepSeekId = "builtin-deepseek-small";
    public const string DeepSeekRedrawnId = "builtin-deepseek-small-redrawn-24fps";
    public const string GptId = "builtin-gpt-small";

    public static bool IsDeepSeekBuiltIn(string? id) => id is DeepSeekId or DeepSeekRedrawnId;
    public static bool IsBuiltIn(string? id) => IsDeepSeekBuiltIn(id) || id == GptId;
    public static bool SupportsSkin(string? skinId) => string.Equals(skinId, "deepseek", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(skinId, "gpt", StringComparison.OrdinalIgnoreCase);

    public static SceneAnimationPack? FindForSkin(string skinId, string? packId = null)
    {
        bool isGpt = string.Equals(skinId, "gpt", StringComparison.OrdinalIgnoreCase);
        bool isDeepSeek = string.Equals(skinId, "deepseek", StringComparison.OrdinalIgnoreCase);
        if (!isGpt && !isDeepSeek) return null;
        packId ??= isGpt ? GptId : DeepSeekId;
        if (isGpt ? packId != GptId : !IsDeepSeekBuiltIn(packId)) return null;
        string folder = isGpt ? "gpt-small" : packId == DeepSeekRedrawnId ? "deepseek-small-redrawn-24fps" : "deepseek-small";
        string manifest = Path.Combine(AppContext.BaseDirectory, "assets", "scenes", folder,
            isGpt ? "gpt-small.petanim.json" : "deepseek-small.petanim.json");
        if (!File.Exists(manifest)) return null;
        var pack = new SceneAnimationCatalog().Validate(manifest, packId,
            isGpt ? "GPT 小龙娘 · 经典状态动画" : packId == DeepSeekRedrawnId ? "DeepSeek 小版 · 24 FPS 重绘版" : "DeepSeek 小版 · 旧版动画（可回档）");
        string[] states = { "idle", "working", "walk-left", "walk-right", "fall", "land", "drag",
            "happy", "angry", "startled", "bashful", "jump-left", "jump-right", "climb-left", "climb-right", "climb-top-left", "climb-top-right" };
        if (isDeepSeek) states = states.Concat(new[] { "sing", "token-come", "bowl-tap", "bowl-tap-eager", "dance", "dance-2", "dance-3", "fireworks", "bangyou-dance" }).ToArray();
        if (!string.Equals(pack.SkinId, skinId, StringComparison.OrdinalIgnoreCase) ||
            pack.Actions.Count != states.Length || states.Any(state => !pack.Actions.ContainsKey(state)))
            throw new InvalidDataException((isGpt ? "GPT" : "DeepSeek") + " 内置动画包的角色或状态不完整。");
        return pack;
    }
}
