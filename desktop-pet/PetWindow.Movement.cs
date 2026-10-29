using System;
using System.ComponentModel;
using System.Windows;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    private DesktopMovementController? _movement;
    private bool _menuOpen;
    private bool? _movementActivityPaused;
    internal DesktopMovementController? MovementController => _movement;

    private void InitializeMovement()
    {
        _movement = new DesktopMovementController(this, () => VisibleFootPixels,
            platformProvider: _movementPlatformProvider, visibleBoundsPixels: () => CharacterVisibleBoundsPixels,
            motionContactPixels: MotionContactPixels,
            motionProfile: () => UsesBuiltInScenes ? _builtInMotionProfile : DeepSeekMotionTimeline.Legacy,
            motionCanvasScale: () => Math.Min(CharacterRectDip.Width, CharacterRectDip.Height)
                * NativeMethods.GetDpiScale(_handle) / _builtInMotionProfile.CanvasSize,
            motionSceneReady: action => !UsesBuiltInScenes ||
                (_sceneActive && _sceneAction == action && _sceneTransitionCommand == 0));
        _movement.StateChanged += OnMovementSceneChanged;
        _movement.MotionFrameChanged += SyncMovementSceneFrame;
        _movement.Landed += OnMovementLanded;
        _movement.Settled += QueueSave;
        _movement.SetEnabled(Settings.MovementEnabled);
        if (_subscribeToSystemEvents) SystemParameters.StaticPropertyChanged += OnSystemMotionChanged;
        UpdateActivityPause();
    }

    private void OnSystemMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(ApplyMotion);
    }

    internal void RefreshMotionPreferences() => ApplyMotion();

    public void SetMovement(bool enabled)
    {
        InvalidateLlmBehavior();
        Settings.MovementEnabled = enabled;
        if (!enabled) _landingScene.Complete();
        _bounce.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        _bounce.Y = 0;
        UpdateBadgePlacement();
        _movement?.SetEnabled(enabled);
        UpdateActivityPause();
        RefreshAutomaticScene();
        Changed();
    }

    public void JumpToNearbyWindow()
        => JumpToWindow(0);

    public void JumpToWindow(int direction)
    {
        InvalidateLlmBehavior();
        if (PhysicalBehaviorBusy || _manualScene) { Say("等我落稳或播放结束后，再试跳跃。"); return; }
        if (DisplayRenderingPaused) { Say("显示恢复后可继续桌面移动。"); return; }
        if (!Settings.MovementEnabled)
        {
            SetMovement(true);
            Say("已开启桌面移动。等我落稳后，再点击跳到附近窗口。");
            return;
        }
        if (!_motionPreferences().AllowsAnimation) { Say("请先允许系统显示动态效果。"); return; }
        // A menu command runs before ContextMenu.Closed. Release its temporary pause here.
        _movement?.SetPaused(!IsVisible);
        bool jumped = _movement?.TryJumpToWindow(direction) == true;
        if (!jumped) Say("附近没有可落脚且能跳到的窗口顶边。可先把我拖近窗口。");
    }

    public void ClimbNearbyWindow(int direction)
    {
        InvalidateLlmBehavior();
        if (!UsesBuiltInScenes) { Say("当前只有 DeepSeek 的默认动画支持攀爬。"); return; }
        if (DisplayRenderingPaused || !IsVisible) return;
        if (!Settings.MovementEnabled) { SetMovement(true); Say("已开启桌面移动。把我放在窗口侧边附近，落稳后再试攀爬。"); return; }
        if (!_motionPreferences().AllowsAnimation) { Say("请先允许系统显示动态效果。"); return; }
        if (PhysicalBehaviorBusy || _manualScene) { Say("等我落稳或播放结束后，再试攀爬。"); return; }
        _movement?.SetPaused(false);
        if (_movement?.TryClimbToWindow(direction) != true)
            Say("这一侧没有足够近且未被遮挡的窗沿。请把我拖到窗口侧边附近，落稳后再试。");
    }

    private void SyncMovementSceneFrame()
    {
        string? action = UsesBuiltInScenes ? _movement?.MotionAction : null;
        _scene?.SetMotionFrame(action, _movement?.MotionFrame ?? 0);
        if (action == null)
        {
            // The controller clears its frame lease immediately before it
            // publishes the final state/landing. Let that atomic transition
            // finish, instead of decoding a transient fall from the old state.
            Dispatcher.BeginInvoke(new Action(RefreshAutomaticScene));
            return;
        }
        if (action != null && _manualScene)
        {
            _scene?.SetAudioSettings(false, Settings.SoundVolume);
            _manualScene = _llmPerformance = _clickPerformance = false; _manualSceneAction = null;
        }
        RefreshAutomaticScene();
    }

    private void UpdateActivityPause()
    {
        bool pause = DisplayRenderingPaused || !IsVisible || !_motionPreferences().AllowsAnimation;
        _scene?.SetPaused(pause);
        _live2D?.SetPaused(pause || _sceneActive);
        bool movementPause = pause || _menuOpen;
        if (_movement != null && _movementActivityPaused != movementPause)
        {
            // Apply only a changed host pause reason. A clip switch also calls
            // ApplyMotion; it must not undo an explicit controller pause (or a
            // jump command's temporary unpause before ContextMenu.Closed).
            // Store first because SetPaused may synchronously publish idle.
            _movementActivityPaused = movementPause;
            _movement.SetPaused(movementPause);
        }
        UpdateMovementAutonomy();
    }

    private void UpdateMovementAutonomy()
    {
        // Work/attention animations stay at their current support; gravity and dragging still work.
        bool busy = IsCompanionOpen || IsChatBusy || (UsesBuiltInScenes ? _scenePolicy.HasRunningTasks || _landingScene.IsPlaying : _sceneActive && _sceneAction is "working" or "attention" or "complete");
        _movement?.SetAutonomous(!IsLlmBehaviorActive && !_manualScene && !_pointerPressed && !busy);
    }
}
