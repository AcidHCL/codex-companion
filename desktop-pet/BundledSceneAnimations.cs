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

    public static bool IsBuiltIn(string? id) => id is DeepSeekId or DeepSeekRedrawnId;

    public static SceneAnimationPack? FindForSkin(string skinId, string? packId = null)
    {
        if (!string.Equals(skinId, "deepseek", StringComparison.OrdinalIgnoreCase)) return null;
        packId ??= DeepSeekId;
        if (!IsBuiltIn(packId)) return null;
        string folder = packId == DeepSeekRedrawnId ? "deepseek-small-redrawn-24fps" : "deepseek-small";
        string manifest = Path.Combine(AppContext.BaseDirectory, "assets", "scenes", folder, "deepseek-small.petanim.json");
        if (!File.Exists(manifest)) return null;
        var pack = new SceneAnimationCatalog().Validate(manifest, packId,
            packId == DeepSeekRedrawnId ? "DeepSeek 小版 · 24 FPS 重绘版" : "DeepSeek 小版 · 旧版动画（可回档）");
        string[] states = { "idle", "working", "walk-left", "walk-right", "fall", "land", "drag", "sing",
            "happy", "angry", "startled", "bashful", "jump-left", "jump-right", "climb-left", "climb-right", "climb-top-left", "climb-top-right",
            "token-come", "bowl-tap", "bowl-tap-eager", "dance", "dance-2", "dance-3", "fireworks" };
        if (!string.Equals(pack.SkinId, "deepseek", StringComparison.OrdinalIgnoreCase) ||
            pack.Actions.Count != states.Length || states.Any(state => !pack.Actions.ContainsKey(state)))
            throw new InvalidDataException("DeepSeek 内置动画包的角色或状态不完整。");
        return pack;
    }
}
