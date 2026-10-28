using System;
using System.Collections.Generic;

namespace DesktopPet;

// The built-in skin always has an animated baseline, independently of custom scene settings.
internal static class DeepSeekAnimationState
{
    public static string Select(bool running, string? movement, int direction, bool landing = false) =>
        movement == "drag" ? "drag" :
        movement == "fall" ? "fall" :
        movement is "jump" or "jump-prep" ? direction < 0 ? "jump-left" : "jump-right" :
        movement == "climb" ? direction < 0 ? "climb-left" : "climb-right" :
        movement == "climb-top" ? direction < 0 ? "climb-top-left" : "climb-top-right" :
        landing ? "land" :
        movement == "walk" ? direction < 0 ? "walk-left" : "walk-right" : running ? "working" : "idle";

    public static IReadOnlyList<string> RunChecks()
    {
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        Check(Select(false, "idle", 1) == "idle", "没有任务时循环待机");
        Check(Select(true, "idle", 1) == "working", "执行任务时循环工作");
        Check(Select(true, "walk", -1) == "walk-left", "向左实际行走优先于工作画面");
        Check(Select(false, "walk", 1) == "walk-right", "向右行走使用独立右向动画");
        Check(Select(false, "walk", -1) != Select(false, "walk", 1), "行走中转向切换动画状态");
        Check(Select(true, "fall", -1) == "fall" && Select(false, "fall", -1) == "fall", "真实下落始终优先于工作和待机");
        Check(Select(true, "idle", 1, landing: true) == "land" && Select(false, "walk", -1, landing: true) == "land", "落地动作完成前不会被工作或行走覆盖");
        Check(Select(true, "fall", 1, landing: true) == "fall", "再次失去支撑立即打断落地并转为下落");
        Check(Select(false, "drag", 1) == "drag" && Select(true, "drag", -1) == "drag", "拎起和挣扎优先于待机及工作状态");
        Check(Select(true, "drag", 1, landing: true) == "drag", "重新拎起会立即打断落地动作");
        Check(Select(false, "jump", 1, landing: true) == "jump-right" && Select(true, "jump", -1, landing: true) == "jump-left", "真实跳跃按方向选择独立动画并清除旧落地姿势");
        Check(Select(true, "jump-prep", -1) == "jump-left", "起跳准备使用同方向跳跃动画");
        Check(Select(true, "climb", 1, landing: true) == "climb-right" && Select(false, "climb", -1) == "climb-left", "沿窗口侧边攀爬优先于工作和旧落地姿势");
        Check(Select(false, "climb-top", 1) == "climb-top-right" && Select(true, "climb-top", -1) == "climb-top-left", "翻上窗口使用独立左右方向动画");
        var landing = new LandingTransitionPolicy();
        landing.ObserveMovement("idle", true);
        Check(!landing.IsPlaying, "普通停止和暂停产生的 idle 不触发落地");
        landing.Begin(); landing.ObserveMovement("idle", true);
        Check(landing.IsPlaying, "物理落地事件保持单次落地动画");
        landing.ObserveMovement("fall", true);
        Check(!landing.IsPlaying, "再次下落清除尚未完成的落地");
        landing.Begin(); landing.ObserveMovement("drag", true);
        Check(!landing.IsPlaying, "用户拖拽取消落地");
        landing.Begin(); landing.ObserveMovement("idle", false);
        Check(!landing.IsPlaying, "关闭移动取消落地并返回默认动画");
        landing.Begin(); landing.Complete();
        Check(!landing.IsPlaying, "单次动作播放完成后解除行走限制");
        return checks;
    }
}

// Only a physical landing event may start this state. An ordinary idle update
// also happens on pause, menu open, disable, and slow drag placement.
internal sealed class LandingTransitionPolicy
{
    public bool IsPlaying { get; private set; }
    public void Begin() => IsPlaying = true;
    public void Complete() => IsPlaying = false;
    public void ObserveMovement(string? state, bool movementEnabled)
    {
        if (!movementEnabled || state is "drag" or "jump" or "jump-prep" or "fall" or "climb" or "climb-top") IsPlaying = false;
    }
}
