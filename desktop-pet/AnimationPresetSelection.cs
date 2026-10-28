using System;
using System.Linq;

namespace DesktopPet;

internal static class AnimationPresetSelection
{
    internal static void Apply(DesktopSettings settings, string[] args)
    {
        bool redrawn = args.Contains("--redrawn-animations", StringComparer.Ordinal);
        bool classic = args.Contains("--classic-animations", StringComparer.Ordinal);
        if (redrawn && classic) throw new ArgumentException("一次只能选择一个动画版本。");
        if (!redrawn && !classic) return;
        settings.SkinId = "deepseek";
        settings.Live2DModelId = null;
        settings.ScenePackId = settings.DeepSeekAnimationPackId = redrawn
            ? BundledSceneAnimations.DeepSeekRedrawnId : BundledSceneAnimations.DeepSeekId;
    }
}
