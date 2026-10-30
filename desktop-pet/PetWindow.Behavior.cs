using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPet;

internal sealed partial class PetWindow : IPetBehaviorHost
{
    private long _behaviorRevision;
    private bool _llmPerformance;
    private bool _llmLocomotion;
    private bool _explicitBehaviorCommandsActive;
    private bool _observingBehaviorDrag, _behaviorDragStarted;
    private Point _behaviorDragOrigin;
    internal bool IsLlmBehaviorActive { get; private set; }
    internal bool CanLlmReact => _explicitBehaviorCommandsActive;
    internal event Action<PetInteractionEvent>? InteractionOccurred;
    internal event Action? BehaviorStateChanged;
    internal event Action? BehaviorInvalidated;

    // Explicit song commands can work with a text-only model. They do not
    // transfer autonomous movement or passive reactions away from default mode.
    internal void SetExplicitBehaviorCommandsActive(bool active)
    {
        Dispatcher.VerifyAccess();
        if (_explicitBehaviorCommandsActive == active) return;
        _explicitBehaviorCommandsActive = active;
        InvalidateLlmBehavior();
    }

    internal void SetLlmBehaviorActive(bool active)
    {
        Dispatcher.VerifyAccess();
        if (IsLlmBehaviorActive == active) return;
        IsLlmBehaviorActive = active;
        InvalidateLlmBehavior();
        UpdateMovementAutonomy();
    }

    // Invalidates pending plans and revokes only resources owned by the LLM.
    // User previews, passive animations, and physics are not cancelled here.
    internal void InvalidateLlmBehavior()
    {
        Dispatcher.VerifyAccess();
        _behaviorRevision++;
        _movement?.StopDirectedWalk();
        if (_llmLocomotion)
        {
            _llmLocomotion = false;
            _movement?.CancelScriptedMotion();
        }
        if (_llmPerformance)
        {
            _llmPerformance = false;
            StopScenePreviewCore();
        }
        BehaviorInvalidated?.Invoke();
        BehaviorStateChanged?.Invoke();
    }

    public Task<PetBehaviorSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Dispatcher.CheckAccess()) return Task.FromResult(BehaviorSnapshot());
        return Dispatcher.InvokeAsync(BehaviorSnapshot, DispatcherPriority.Normal, ct).Task;
    }

    public Task<PetBehaviorResult> ExecuteAsync(PetBehaviorCommand command, long expectedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Dispatcher.CheckAccess()) return ExecuteBehaviorCoreAsync(command, expectedRevision, ct);
        return Dispatcher.InvokeAsync(() => ExecuteBehaviorCoreAsync(command, expectedRevision, ct), DispatcherPriority.Normal, ct).Task.Unwrap();
    }

    private bool BehaviorAvailable => (IsLlmBehaviorActive || _explicitBehaviorCommandsActive) && !_closed && IsVisible && !DisplayRenderingPaused;
    private bool PhysicalBehaviorBusy => _movement?.IsDragging == true ||
        _movement?.HasScriptedMotion == true ||
        _movement?.IsEnabled == true && _movement.State is "fall" or "jump" or "climb" or "climb-top" || _landingScene.IsPlaying;

    private PetBehaviorSnapshot BehaviorSnapshot()
    {
        string state = _movement?.IsDragging == true ? "drag" :
            _movement?.IsEnabled == true && _movement.State is "fall" or "jump" or "climb" or "climb-top" ? _movement.State :
            _landingScene.IsPlaying ? "land" : _manualScene ? _manualSceneAction ?? "performance" :
            _movement?.IsEnabled == true && _movement.State == "walk" ? "walk" :
            _scenePolicy.HasRunningTasks || IsChatBusy ? "working" : "idle";
        // This is an explicit performance registry. Never infer permission to
        // play a physical state from the presence of its animation in a pack.
        IReadOnlyList<string> performances = !IsSceneLoading && _scene != null
            ? PetPerformanceCatalog.Installed(CurrentScenePack) : Array.Empty<string>();
        bool canWalk = BehaviorAvailable && !_pointerPressed && Settings.MovementEnabled && _movement is { IsEnabled: true, Grounded: true, IsDragging: false } &&
            !PhysicalBehaviorBusy && !_manualScene && !_menuOpen && _motionPreferences().AllowsAnimation;
        return new PetBehaviorSnapshot(CurrentSkin.Id, state, _behaviorRevision, IsLlmBehaviorActive || _explicitBehaviorCommandsActive,
            !_closed && IsVisible && !DisplayRenderingPaused, canWalk, performances, _manualScene && !_llmPerformance && !_clickPerformance, Settings.SoundEnabled,
            CanJump: canWalk && UsesBuiltInScenes, CanClimb: canWalk && UsesBuiltInScenes);
    }

    private async Task<PetBehaviorResult> ExecuteBehaviorCoreAsync(PetBehaviorCommand command, long expectedRevision, CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        ct.ThrowIfCancellationRequested();
        PetBehaviorResult Reject(string message) => new(false, message, command.Action);
        if (expectedRevision != _behaviorRevision) return Reject("状态已改变，这条动作请求已失效。");
        if (!BehaviorAvailable) return Reject("模型行为控制当前未启用，或桌宠暂不可见。");
        if (!Enum.IsDefined(command.Origin)) return Reject("不支持的动作来源。");
        if (!IsLlmBehaviorActive && !(command.Origin == PetBehaviorOrigin.Conversation &&
            (command.Action == "play_performance" && PetPerformanceCatalog.IsMusicPerformance(command.Name) ||
             command.Action == "stop_performance" && PetPerformanceCatalog.IsMusicPerformance(_manualSceneAction))))
            return Reject("当前模型只支持明确播放或停止音乐表演的请求，其他动作控制尚未启用。");
        switch (command.Action)
        {
            case "play_performance":
                if (command.Name == null || !PetPerformanceCatalog.Installed(CurrentScenePack).Contains(command.Name) || command.Direction != null)
                    return Reject("只能播放已安装的表演与情绪动画，不能控制被动动画。");
                if (_manualScene && !_llmPerformance && !_clickPerformance) return Reject("用户正在手动播放动画，模型不能替换它。");
                if (PhysicalBehaviorBusy || _menuOpen) return Reject("请等移动、拖拽或落地结束后再表演。");
                if (!CanPlayPerformance(command.Name)) return Reject("当前角色的这段动画暂不可用。");
                if (_llmPerformance && _manualSceneAction == command.Name) return new(true, "正在播放：" + PetPerformanceCatalog.Label(command.Name), command.Action);
                _movement?.StopDirectedWalk();
                var performance = PreviewSceneCoreAsync(command.Name, llmOwned: true);
                try
                {
                    await performance.WaitAsync(ct);
                    ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    if (expectedRevision == _behaviorRevision && _llmPerformance)
                    { _llmPerformance = false; StopScenePreviewCore(); }
                    // The renderer may finish decoding after cancellation. Its
                    // command generation was revoked above; still observe any
                    // late renderer failure instead of leaving an orphan task.
                    ObserveRendererOperation(performance);
                    throw;
                }
                if (expectedRevision != _behaviorRevision || !_llmPerformance || _manualSceneAction != command.Name)
                    return Reject("状态已改变，动画请求没有继续执行。");
                if (!_sceneActive)
                {
                    _llmPerformance = false;
                    return Reject("动画没有成功开始，请稍后再试。");
                }
                BehaviorStateChanged?.Invoke();
                return new(true, command.Name == "sing"
                    ? Settings.SoundEnabled ? "已开始唱歌。" : "已播放唱歌动画；声音当前关闭。"
                    : "已开始播放：" + PetPerformanceCatalog.Label(command.Name) +
                      (PetPerformanceCatalog.IsMusicPerformance(command.Name) && !Settings.SoundEnabled ? "；声音当前关闭。" : "。"), command.Action);
            case "stop_performance":
                if (command.Name != null || command.Direction != null) return Reject("停止演出不接受其他参数。");
                if (_manualScene && !_llmPerformance && !_clickPerformance) return Reject("这段演出由用户手动启动，模型不能停止它。");
                if (_llmPerformance || _clickPerformance) { _llmPerformance = false; StopScenePreviewCore(); }
                BehaviorStateChanged?.Invoke();
                return new(true, "模型演出已停止。", command.Action);
            case "walk":
                if (command.Origin != PetBehaviorOrigin.Conversation) return Reject("被动互动的反应不能控制行走。");
                if (command.Name != null || command.Direction is not ("left" or "right") || !double.IsFinite(command.Seconds) || command.Seconds is < 1 or > 8)
                    return Reject("行走需要 left 或 right，持续时间为 1 到 8 秒。");
                if (!Settings.MovementEnabled) return Reject("桌面行走当前关闭，请先在设置中开启桌面行走。");
                if (!BehaviorSnapshot().CanWalk) return Reject("当前无法行走，请等待落稳或演出结束。");
                if (_movement?.TryDirectedWalk(command.Direction == "left" ? -1 : 1, command.Seconds) != true)
                    return Reject("暂时无法开始行走，请等桌宠落稳。");
                BehaviorStateChanged?.Invoke();
                return new(true, command.Direction == "left" ? "已开始向左走。" : "已开始向右走。", command.Action);
            case "stop_walk":
                if (command.Origin != PetBehaviorOrigin.Conversation) return Reject("被动互动的反应不能控制行走。");
                if (command.Name != null || command.Direction != null) return Reject("停止行走不接受其他参数。");
                _movement?.StopDirectedWalk();
                BehaviorStateChanged?.Invoke();
                return new(true, "模型行走已停止。", command.Action);
            case "jump":
            case "climb":
                if (command.Origin != PetBehaviorOrigin.Conversation) return Reject("被动互动的反应不能控制跳跃或攀爬。");
                if (command.Name != null || command.Direction is not ("left" or "right")) return Reject("跳跃和攀爬需要 left 或 right 方向。");
                if (!Settings.MovementEnabled) return Reject("桌面行走当前关闭，请先在设置中开启桌面行走。");
                var capability = BehaviorSnapshot();
                if (!(command.Action == "jump" ? capability.CanJump : capability.CanClimb))
                    return Reject("当前无法移动，请等待落稳或演出结束，并使用支持该动作的角色。");
                _llmLocomotion = true;
                bool started = command.Action == "jump"
                    ? _movement?.TryJumpToWindow(command.Direction == "left" ? -1 : 1) == true
                    : _movement?.TryClimbToWindow(command.Direction == "left" ? -1 : 1) == true;
                if (!started)
                {
                    _llmLocomotion = false;
                    return Reject(command.Action == "jump" ? "该方向没有可落脚且能跳到的窗口顶边。" : "该方向没有足够近、未被遮挡且可攀爬的窗口侧边。");
                }
                BehaviorStateChanged?.Invoke();
                return new(true, command.Action == "jump" ? "已开始向目标窗口跳跃，尚未落地。" : "已开始攀爬目标窗沿，尚未翻上顶边。", command.Action);
            default:
                return Reject("没有这个动作能力；拖拽、下落和落地由物理状态控制。");
        }
    }

    internal void BeginBehaviorPointerInteraction()
    {
        _clickGeneration++;
        _clickSequence.Reset();
        InvalidateLlmBehavior();
        var bounds = NativeMethods.GetWindowBoundsPixels(_handle);
        _behaviorDragOrigin = new Point(bounds.Left + VisibleFootPixels.X, bounds.Top + VisibleFootPixels.Y);
        _observingBehaviorDrag = true; _behaviorDragStarted = false;
    }

    private void ObserveBehaviorDrag()
    {
        if (!_observingBehaviorDrag || _behaviorDragStarted || _handle == IntPtr.Zero) return;
        var bounds = NativeMethods.GetWindowBoundsPixels(_handle);
        var point = new Point(bounds.Left + VisibleFootPixels.X, bounds.Top + VisibleFootPixels.Y);
        if ((point - _behaviorDragOrigin).Length < 4 * NativeMethods.GetDpiScale(_handle)) return;
        _behaviorDragStarted = true;
        EmitBehaviorInteraction("drag_started");
    }

    internal void CompleteBehaviorPointerInteraction(bool forceDrag = false)
    {
        if (!_observingBehaviorDrag) return;
        ObserveBehaviorDrag();
        _observingBehaviorDrag = false;
        if (forceDrag && !_behaviorDragStarted) EmitBehaviorInteraction("drag_started");
        EmitBehaviorInteraction(_behaviorDragStarted || forceDrag ? "released" : "clicked");
    }

    private void OnBehaviorMovementChanged(string state)
    {
        // Scripted motion owns its phase changes. Starting its own jump or
        // switching to the top-out must not revoke the command that started it.
        if (_movement?.HasScriptedMotion != true && state is "drag" or "fall" or "jump") InvalidateLlmBehavior();
        if (_movement?.HasScriptedMotion != true && state is "idle" or "walk") _llmLocomotion = false;
        if (state == "fall") EmitBehaviorInteraction("fall_started");
        BehaviorStateChanged?.Invoke();
    }

    private void EmitBehaviorInteraction(string kind, int clickCount = 0, string? clickReaction = null, DateTimeOffset? occurredAt = null)
    {
        if (!Settings.LlmBehaviorEnabled || _closed || !IsVisible || DisplayRenderingPaused) return;
        InteractionOccurred?.Invoke(new PetInteractionEvent(kind, occurredAt ?? DateTimeOffset.UtcNow, _behaviorRevision,
            ClickCount: clickCount, ClickReaction: clickReaction));
    }
}
