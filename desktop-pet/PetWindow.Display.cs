using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    internal DisplayLifecycleController? DisplayLifecycle { get; private set; }
    internal string? RendererProfileDirectory { get; set; }
    private bool _displayRenderingPaused, _displayRestoreInProgress;
    private Live2DModelInfo? _displayResumeModel;
    private SceneAnimationPack? _displayResumePack;
    private string? _displayManualAction, _displayManualPack;
    private bool DisplayRenderingPaused => _displayRenderingPaused || GraphicsRecovery.CompatibilityMode;

    private void InitializeDisplayLifecycle()
    {
        DisplayLifecycle = new DisplayLifecycleController(Dispatcher, _handle,
            SuspendDisplayRendering, RestoreDisplayRenderingAsync, ResumeDisplayRendering,
            subscribeToSystemEvents: _subscribeToSystemEvents, querySession: _querySession);
        DisplayLifecycle.SetHostVisible(IsVisible);
    }

    private void SuspendDisplayRendering()
    {
        if (_closed) return;
        if (!_displayRenderingPaused)
        {
            // A song is an explicit user action. Never start it again after
            // locking, disconnecting, or rebuilding a failed renderer.
            _displayManualAction = _manualScene && !IsSinging && !IsManualSoundScene ? _sceneAction : null;
            _displayManualPack = _manualScene && !IsSinging && !IsManualSoundScene ? CurrentScenePack?.Id : null;
        }
        _displayResumeModel = CurrentLive2DModel ?? _displayResumeModel;
        _displayResumePack = CurrentScenePack ?? _displayResumePack;
        _displayRenderingPaused = true; // Must precede callbacks from SetPaused/ReleaseScene.
        RefreshSpeechSuspension();
        if (DisplayLifecycle?.AllowsCompanionInteraction == false) CompanionSurfacesUnavailable?.Invoke();
        TopmostGuard?.Refresh();
        _modelGeneration++;
        _movementActivityPaused = true;
        _movement?.SetPaused(true);
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, null); _breath.ScaleY = 1;
        _bounce.BeginAnimation(TranslateTransform.YProperty, null); _bounce.Y = 0;
        _footGeometryTimer?.Stop();
        _speechTimer.Stop();
        // A hidden/locked pet must not revive an expired speech bubble with
        // its timer permanently stopped or retain its extra singing headroom.
        _bubble.Visibility = Visibility.Collapsed;
        // Remove composition controls while display resources are unavailable.
        // Release methods leave model/pack IDs and every saved preference alone.
        try { ReleaseScene(); } catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
        try { ReleaseLive2D(); } catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
        IsLive2DLoading = IsSceneLoading = false;
        _sceneActive = false;
        _image.Visibility = Visibility.Visible; _image.Opacity = 1;
        SceneStatus = GraphicsRecovery.CompatibilityMode ? "图形兼容模式 · 静态待机" : "显示暂停 · 保留原动作设置";
        if (!string.IsNullOrEmpty(Settings.Live2DModelId)) Live2DStatus = "显示恢复后重新载入原模型";
    }

    private async Task<bool> RestoreDisplayRenderingAsync(CancellationToken token)
    {
        if (_closed || token.IsCancellationRequested) return false;
        if (GraphicsRecovery.CompatibilityMode) return true;
        _displayRestoreInProgress = true;
        try
        {
            // Resolve by the current saved selection, so a deliberate user
            // change made during a pause wins over the previous renderer.
            string? modelId = Settings.Live2DModelId;
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var model = _displayResumeModel?.Id == modelId ? _displayResumeModel :
                    Live2DLibrary.Load(out _).FirstOrDefault(item => item.Id == modelId);
                if (model == null) { Live2DStatus = "原模型暂不可用，已保留选择"; return false; }
                if (!await SelectLive2DAsync(model)) return false;
            }
            if (_closed || token.IsCancellationRequested) return false;
            string? packId = Settings.ScenePackId;
            if (!string.IsNullOrWhiteSpace(packId))
            {
                var pack = _displayResumePack?.Id == packId && _displayResumePack.SkinId == CurrentSkin.Id ? _displayResumePack :
                    SceneAnimationLibrary.Load(out _).FirstOrDefault(item => item.Id == packId && item.SkinId == CurrentSkin.Id);
                if (pack == null) { SceneStatus = "原动作包暂不可用，已保留选择"; return false; }
                if (!(CurrentLive2DModel != null && BundledSceneAnimations.IsBuiltIn(pack.Id)) && !await SelectScenePackAsync(pack)) return false;
            }
            else if (string.IsNullOrWhiteSpace(modelId))
            {
                var pack = BundledSceneAnimations.FindForSkin(CurrentSkin.Id, Settings.DeepSeekAnimationPackId);
                if (pack != null && !await SelectScenePackAsync(pack)) return false;
            }
            if (_closed || token.IsCancellationRequested) return false;
            ClampPosition();
            return true;
        }
        finally { _displayRestoreInProgress = false; }
    }

    private void ResumeDisplayRendering()
    {
        if (_closed) return;
        _displayRenderingPaused = false;
        RefreshSpeechSuspension();
        _movementActivityPaused = null;
        ApplyMotion();
        TopmostGuard?.Refresh();
        if (GraphicsRecovery.CompatibilityMode) return;
        if (_scene == null && CurrentScenePack == null) SceneStatus = "显示已恢复 · 角色待机";
        QueueFootGeometryRefresh();
        if (_displayManualAction is string action && CurrentScenePack is { } selectedPack && _displayManualPack == selectedPack.Id && selectedPack.Actions.ContainsKey(action))
            ObserveRendererOperation(PreviewSceneAsync(action));
        else { _desiredSceneKey = null; RefreshAutomaticScene(); }
        _displayManualAction = _displayManualPack = null;
        SettingsChanged?.Invoke();
    }

    private void QueueRendererRecovery(string message)
    {
        if (_closed) return;
        SceneStatus = message + " · 正在恢复，保留原设置";
        Live2DStatus = message;
        DisplayLifecycle?.RequestRecovery(rendererFailure: true);
        if (DisplayLifecycle?.RendererRetryExhausted == true)
        {
            SceneStatus = "动画连续恢复失败 · 保留原设置，等待显示恢复或重新选择";
            Live2DStatus = "模型连续恢复失败 · 保留原选择";
        }
        SettingsChanged?.Invoke();
    }

    // Fire-and-forget renderer entry points still deliver fatal WPF failures to
    // the application's DispatcherUnhandledException process-recovery handler.
    private async void ObserveRendererOperation(Task operation)
    {
        try { await operation; }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
    }

    private bool DeferSceneSelection(SceneAnimationPack pack)
    {
        if (!DisplayRenderingPaused || _displayRestoreInProgress && !GraphicsRecovery.CompatibilityMode) return false;
        _displayResumePack = pack;
        CurrentScenePack = pack;
        Settings.ScenePackId = pack.Id;
        if (BundledSceneAnimations.IsBuiltIn(pack.Id)) Settings.DeepSeekAnimationPackId = pack.Id;
        if (BundledSceneAnimations.IsBuiltIn(pack.Id)) { Settings.Live2DModelId = null; CurrentLive2DModel = null; _displayResumeModel = null; }
        SceneStatus = GraphicsRecovery.CompatibilityMode ? "图形兼容模式 · 已保留动作选择" : "显示恢复后载入所选动作";
        Changed();
        if (!GraphicsRecovery.CompatibilityMode) DisplayLifecycle?.RequestRecovery(userInitiated: true);
        return true;
    }

    private bool DeferModelSelection(Live2DModelInfo model)
    {
        if (!DisplayRenderingPaused || _displayRestoreInProgress && !GraphicsRecovery.CompatibilityMode) return false;
        _displayResumeModel = model;
        CurrentLive2DModel = model;
        Settings.Live2DModelId = model.Id;
        if (BundledSceneAnimations.IsBuiltIn(Settings.ScenePackId)) { Settings.ScenePackId = null; CurrentScenePack = null; _displayResumePack = null; }
        Live2DStatus = GraphicsRecovery.CompatibilityMode ? "图形兼容模式 · 已保留模型选择" : "显示恢复后载入所选模型";
        Changed();
        if (!GraphicsRecovery.CompatibilityMode) DisplayLifecycle?.RequestRecovery(userInitiated: true);
        return true;
    }

    private void ForgetDisplaySelectionsForSkinChange()
    {
        // A suspended renderer has already left CurrentScenePack, so selection
        // cleanup must also cover its saved ID and detached descriptors.
        Settings.ScenePackId = null;
        _displayResumePack = null;
        _displayResumeModel = null;
        _displayManualAction = _displayManualPack = null;
    }
}
