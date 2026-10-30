using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    public event Action? OpenScenesRequested;
    public SceneAnimationPack? CurrentScenePack { get; private set; }
    public string SceneStatus { get; private set; } = "导入动作包，为工作和移动选择动画";
    public bool IsSceneLoading { get; private set; }
    public bool IsScenePlaying => _sceneActive;
    public bool UsesBuiltInScenes => BundledSceneAnimations.IsBuiltIn(CurrentScenePack?.Id);
    public bool CanPlayPerformance(string action) => !_closed && IsVisible && !DisplayRenderingPaused && !IsSceneLoading && _scene != null &&
        !PhysicalBehaviorBusy && PetPerformanceCatalog.Installed(CurrentScenePack).Contains(action);
    public bool CanSing => CanPlayPerformance("sing");
    public bool IsSinging => _manualScene && _manualSceneAction == "sing";
    private bool IsManualSoundScene => _manualScene && _manualSceneAction is { } action && CurrentScenePack is { } pack &&
        pack.Actions.TryGetValue(action, out var definition) && definition.Audio != null;
    internal string? CurrentSceneAction => _sceneAction;
    internal SceneAnimationView? SceneRenderer => _scene;
    private SceneAnimationView? _scene;
    private readonly SceneTriggerPolicy _scenePolicy = new();
    private readonly LandingTransitionPolicy _landingScene = new();
    private int _sceneGeneration, _sceneCommand, _sceneTransitionCommand;
    private bool _sceneActive, _manualScene, _celebrating;
    private string? _sceneAction, _desiredSceneKey, _manualSceneAction;
    private readonly SemaphoreSlim _sceneGate = new(1);
    public void OpenSceneLibrary() => OpenScenesRequested?.Invoke();

    private async Task RestoreRenderersAsync()
    {
        if (DisplayRenderingPaused && !_displayRestoreInProgress) return;
        if (!string.IsNullOrWhiteSpace(Settings.Live2DModelId)) await RestoreLive2DAsync();
        if (_closed) return;
        if (!string.IsNullOrWhiteSpace(Settings.ScenePackId))
        {
            var packs = SceneAnimationLibrary.Load(out var issues);
            var pack = packs.FirstOrDefault(p => p.Id == Settings.ScenePackId && p.SkinId == CurrentSkin.Id);
            if (pack != null && !(CurrentLive2DModel != null && BundledSceneAnimations.IsBuiltIn(pack.Id)))
            {
                if (await SelectScenePackAsync(pack)) return;
            }
            else { Settings.ScenePackId = null; SceneStatus = issues.FirstOrDefault() ?? "原动作包不可用，请重新导入。"; Changed(); }
        }
        await RestoreDefaultSceneAsync();
    }

    private async Task RestoreDefaultSceneAsync()
    {
        if (_closed || DisplayRenderingPaused || IsLive2DLoading || CurrentLive2DModel != null || CurrentScenePack != null || IsSceneLoading) return;
        try
        {
            var pack = BundledSceneAnimations.FindForSkin(CurrentSkin.Id,
                CurrentSkin.Id == "deepseek" ? Settings.DeepSeekAnimationPackId : null);
            if (pack != null) await SelectScenePackAsync(pack);
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { SceneStatus = "默认动画不可用：" + ex.Message; Program.Log(ex); SettingsChanged?.Invoke(); }
    }

    public async Task<bool> SelectScenePackAsync(SceneAnimationPack pack)
    {
        if (_closed) return false;
        InvalidateLlmBehavior();
        if (!_displayRestoreInProgress) DisplayLifecycle?.ResetRendererFailureBudget();
        if (!string.Equals(pack.SkinId, CurrentSkin.Id, StringComparison.OrdinalIgnoreCase))
        {
            SceneStatus = "这个动作包属于 " + pack.SkinId + "，请先选择对应的 PNG 角色。";
            SettingsChanged?.Invoke(); return false;
        }
        if (DeferSceneSelection(pack)) return true;
        ReleaseScene();
        int generation = _sceneGeneration;
        IsSceneLoading = true;
        SceneStatus = "正在载入 " + pack.Name + "…";
        SettingsChanged?.Invoke();
        try
        {
            // Revalidate selection; files may have changed since the library was opened.
            var validated = new SceneAnimationCatalog().Validate(pack.ManifestPath, pack.Id, pack.Name);
            if (!string.Equals(validated.SkinId, CurrentSkin.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("动作包的适配角色已改变，请重新选择。");
            if (BundledSceneAnimations.IsBuiltIn(validated.Id))
            {
                _modelGeneration++;
                ReleaseLive2D(); CurrentLive2DModel = null; IsLive2DLoading = false;
                Settings.Live2DModelId = null; _image.Visibility = Visibility.Visible;
                LoadBuiltInContactGeometry(validated);
            }
            var renderer = new SceneAnimationView(validated, RendererProfileDirectory) { Opacity = 0 };
            renderer.SetAudioSettings(false, Settings.SoundVolume);
            _scene = renderer;
            _character.Children.Add(renderer);
            ApplySceneThoughtLayout(false);
            UpdateLayout();
            await renderer.LoadAsync();
            if (_closed || generation != _sceneGeneration) return false;
            if (!string.Equals(validated.SkinId, CurrentSkin.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("当前角色已改变，请重新选择动作包。");
            CurrentScenePack = validated;
            Settings.ScenePackId = validated.Id;
            if (BundledSceneAnimations.IsDeepSeekBuiltIn(validated.Id)) Settings.DeepSeekAnimationPackId = validated.Id;
            IsSceneLoading = false;
            SceneStatus = "已选择 · " + validated.Name;
            renderer.Completed += () =>
            {
                if (_closed || generation != _sceneGeneration) return;
                if (_sceneTransitionCommand == 0) SceneCompleted();
            };
            renderer.Failed += error =>
            {
                if (_closed || generation != _sceneGeneration) return;
                Program.Log(new InvalidOperationException(error));
                QueueRendererRecovery("场景动画暂不可用");
            };
            UpdateActivityPause(); Changed(); RefreshAutomaticScene();
            return true;
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            if (_closed || generation != _sceneGeneration) return false;
            ReleaseScene();
            SceneStatus = "动作包加载失败：" + ex.Message;
            Program.Log(ex); Changed();
            if (!_displayRestoreInProgress) QueueRendererRecovery("动作载入暂时失败");
            return false;
        }
    }

    public async Task PreviewSceneAsync(string action)
    {
        if (_closed || !IsVisible || DisplayRenderingPaused || _scene == null || CurrentScenePack?.Actions.ContainsKey(action) != true) return;
        if (PhysicalBehaviorBusy) { Say("等我落稳后再播放动画。"); return; }
        InvalidateLlmBehavior();
        await PreviewSceneCoreAsync(action, llmOwned: false);
    }

    private async Task PreviewSceneCoreAsync(string action, bool llmOwned, bool clickOwned = false)
    {
        _scene?.SetMotionFrame(null, 0);
        if (!llmOwned) _landingScene.Complete();
        _manualScene = true; _celebrating = false;
        _llmPerformance = llmOwned;
        _clickPerformance = clickOwned;
        _manualSceneAction = action;
        RefreshSpeechSuspension();
        _desiredSceneKey = "preview:" + Guid.NewGuid().ToString("N");
        UpdateMovementAutonomy();
        SettingsChanged?.Invoke();
        await ChangeSceneAsync(action, action == "sing", ++_sceneCommand);
        BehaviorStateChanged?.Invoke();
    }

    public Task SingAsync() => PreviewSceneAsync("sing");

    public void StopSinging()
    {
        if (IsSinging) StopScenePreview();
    }

    public void SetSoundEnabled(bool enabled)
    {
        Settings.SoundEnabled = enabled;
        _scene?.SetAudioSettings(enabled && _manualScene, Settings.SoundVolume);
        RefreshSpeechSuspension();
        Changed();
    }

    public void SetSoundVolume(double volume)
    {
        Settings.SoundVolume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : .35;
        _scene?.SetAudioSettings(Settings.SoundEnabled && _manualScene, Settings.SoundVolume);
        RefreshSpeechSuspension();
        Changed();
    }

    public void StopScenePreview()
    {
        InvalidateLlmBehavior();
        StopScenePreviewCore();
    }

    private void StopScenePreviewCore()
    {
        bool restoreAutomatic = UsesBuiltInScenes || IsSinging || IsManualSoundScene;
        _scene?.SetAudioSettings(false, Settings.SoundVolume);
        _manualScene = _celebrating = _clickPerformance = false;
        _manualSceneAction = null;
        _llmPerformance = false;
        RefreshSpeechSuspension();
        if (restoreAutomatic)
        {
            _desiredSceneKey = "resume-default";
            UpdateActivityPause(); RefreshAutomaticScene();
            return;
        }
        _desiredSceneKey = null;
        ObserveRendererOperation(ChangeSceneAsync(null, false, ++_sceneCommand));
        // Stop is explicit: stay at idle until a subsequent state change/preview request.
        _desiredSceneKey = AutomaticAction();
    }

    public void SetSceneAuto(bool enabled)
    {
        InvalidateLlmBehavior();
        if (UsesBuiltInScenes) { StopScenePreview(); return; }
        Settings.SceneAutoEnabled = enabled;
        _scene?.SetAudioSettings(false, Settings.SoundVolume);
        _manualScene = _celebrating = _clickPerformance = false;
        _manualSceneAction = null;
        _desiredSceneKey = "settings-changed";
        RefreshSpeechSuspension();
        RefreshAutomaticScene(); Changed();
    }

    public void ClearScenePack()
    {
        InvalidateLlmBehavior();
        ReleaseScene(); Settings.ScenePackId = null;
        SceneStatus = "已恢复角色默认状态"; Changed();
        ObserveRendererOperation(RestoreDefaultSceneAsync());
    }

    private void StopSceneForBaseChange(string skinId)
    {
        _movement?.CancelScriptedMotion();
        if (IsSceneLoading || (CurrentScenePack != null && !string.Equals(CurrentScenePack.SkinId, skinId, StringComparison.OrdinalIgnoreCase)))
        { ReleaseScene(); Settings.ScenePackId = null; }
        else StopScenePreview();
    }

    private void UpdateSceneFromCodex(CodexSnapshot snapshot)
    {
        _scenePolicy.Observe(snapshot);
        if (!snapshot.Connected) _celebrating = false;
        UpdateActivityPause();
        RefreshAutomaticScene();
    }

    internal void OnVerifiedSceneNotice(PetNotice notice)
    {
        if (notice.Kind == "complete" && Settings.SceneAutoEnabled && !_manualScene && CurrentScenePack?.Actions.ContainsKey("complete") == true)
        {
            _celebrating = true;
            RefreshAutomaticScene();
        }
    }

    private string? AutomaticAction()
    {
        if (UsesBuiltInScenes && _movement?.MotionAction is string motionAction && CurrentScenePack!.Actions.ContainsKey(motionAction)) return motionAction;
        // An upward mouse throw is still a passive airborne state. The complete
        // jump sequence (especially its landing pose) belongs only to the
        // controller's frame-driven jump, never a timed preview in mid-air.
        if (UsesBuiltInScenes && _movement?.IsEnabled == true && _movement.State == "jump") return "fall";
        if (UsesBuiltInScenes)
            return DeepSeekAnimationState.Select(_scenePolicy.HasRunningTasks || IsChatBusy,
                _movement?.IsDragging == true ? "drag" : _movement?.IsEnabled == true ? _movement.State : "idle",
                _movement?.FacingDirection ?? 1, _landingScene.IsPlaying);
        if (!Settings.SceneAutoEnabled || CurrentScenePack == null) return null;
        bool Has(string? action) => action != null && CurrentScenePack.Actions.ContainsKey(action);
        string? movement = _movement?.State;
        if (movement is "drag" or "fall" or "jump" && Has(movement)) return movement;
        if (_scenePolicy.PersistentAction == "attention" && Has("attention")) return "attention";
        if (_celebrating && Has("complete")) return "complete";
        if (movement == "walk")
        {
            string directional = _movement?.FacingDirection < 0 ? "walk-left" : "walk-right";
            if (Has(directional)) return directional;
            if (Has(movement)) return movement;
        }
        if (IsChatBusy && Has("working")) return "working";
        return Has(_scenePolicy.PersistentAction) ? _scenePolicy.PersistentAction : null;
    }

    private void OnMovementSceneChanged(string state)
    {
        OnBehaviorMovementChanged(state);
        _landingScene.ObserveMovement(state, _movement?.IsEnabled == true);
        // Grabbing is a direct interaction and must win over a library preview,
        // including while autonomous desktop movement is switched off.
        // Fall/jump retain the existing interruption rule for a landing preview.
        if (state is "drag" or "fall" or "jump" or "climb" or "climb-top")
        { _scene?.SetAudioSettings(false, Settings.SoundVolume); _manualScene = _llmPerformance = _clickPerformance = false; _manualSceneAction = null; RefreshSpeechSuspension(); }
        UpdateMovementAutonomy();
        RefreshAutomaticScene();
    }

    private void OnMovementLanded()
    {
        InvalidateLlmBehavior();
        if (UsesBuiltInScenes && _movement?.IsEnabled == true && !_movement.IsDragging && !_manualScene &&
            CurrentScenePack?.Actions.ContainsKey("land") == true)
        {
            _landingScene.Begin();
            _desiredSceneKey = "physical-landing";
            UpdateMovementAutonomy();
            RefreshAutomaticScene();
        }
        EmitBehaviorInteraction("landed");
        BehaviorStateChanged?.Invoke();
    }

    private void RefreshAutomaticScene()
    {
        if (_closed || DisplayRenderingPaused || _manualScene || IsSceneLoading || _scene == null) return;
        string? action = AutomaticAction();
        if (_desiredSceneKey == action) return;
        _desiredSceneKey = action;
        bool repeat = action != null && action != "complete" && CurrentScenePack!.Actions[action].Repeat;
        ObserveRendererOperation(ChangeSceneAsync(action, repeat, ++_sceneCommand, graceful: !UsesBuiltInScenes));
    }

    private void SceneCompleted()
    {
        if (_movement?.HasScriptedMotion == true && !_manualScene) return;
        _llmPerformance = _clickPerformance = false;
        _manualSceneAction = null;
        RefreshSpeechSuspension();
        if (UsesBuiltInScenes)
        {
            _landingScene.Complete();
            _manualScene = _celebrating = false;
            _desiredSceneKey = "resume-default";
            UpdateActivityPause();
            RefreshAutomaticScene();
            BehaviorStateChanged?.Invoke();
            return;
        }
        _sceneActive = false;
        bool resume = _manualScene || _celebrating;
        _manualScene = _celebrating = false;
        _sceneAction = null;
        ApplySceneThoughtLayout(false);
        ApplyMotion();
        SceneStatus = "待机 · " + CurrentScenePack?.Name;
        SettingsChanged?.Invoke();
        if (resume) { _desiredSceneKey = null; RefreshAutomaticScene(); }
        BehaviorStateChanged?.Invoke();
    }

    private async Task ChangeSceneAsync(string? action, bool repeat, int command, bool graceful = false)
    {
        var renderer = _scene;
        int generation = _sceneGeneration;
        if (renderer == null) return;
        if (!_manualScene) renderer.SetAudioSettings(false, Settings.SoundVolume);
        // A replaced pending decode/transition must not hold up the new state.
        bool replacingPending = _sceneTransitionCommand != 0;
        _sceneTransitionCommand = command;
        bool continuous = UsesBuiltInScenes && action != null;
        if (renderer.IsPlaying && (replacingPending || !graceful) && !continuous) ObserveRendererOperation(StopRendererQuietly(renderer));
        await _sceneGate.WaitAsync();
        try
        {
            if (_closed || command != _sceneCommand || generation != _sceneGeneration) return;
            // Background task/movement changes never start audible media.
            // Only an explicit preview or singing command may use the saved volume.
            renderer.SetAudioSettings(Settings.SoundEnabled && _manualScene, Settings.SoundVolume);
            ApplySceneThoughtLayout(action != null && CurrentScenePack!.Actions[action].Thoughts != null);
            if (continuous)
            {
                // The renderer retains the previous animation frame until the next is decoded.
                await renderer.SwitchAsync(action!, repeat);
            }
            else
            {
                if (graceful && renderer.IsPlaying)
                {
                    try { await renderer.StopAsync(false).WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { await renderer.StopAsync(true); }
                }
                else await renderer.StopAsync(true);
                if (_closed || command != _sceneCommand || generation != _sceneGeneration) return;
                _sceneActive = false;
                ApplyMotion();
                if (action == null) { _sceneAction = null; SceneStatus = "待机 · " + CurrentScenePack?.Name; SettingsChanged?.Invoke(); return; }
                await renderer.PlayAsync(action, repeat);
            }
            if (_closed || command != _sceneCommand || generation != _sceneGeneration) return;
            _sceneAction = action;
            _sceneTransitionCommand = 0;
            if (!renderer.IsPlaying) { SceneCompleted(); return; }
            _sceneActive = true;
            if (UsesBuiltInScenes) _movement?.ReconfigureGeometry();
            if (!UsesBuiltInScenes) _sceneGeometry = null;
            QueueFootGeometryRefresh();
            SceneStatus = (_manualScene ? "预览 · " : "状态 · ") + (action switch
            { "idle" => "待机", "working" => "工作", "walk-left" => "向左走", "walk-right" => "向右走", "drag" => "拎起与挣扎", "fall" => "下落", "land" => "落地",
              "jump-left" => "向左跳跃", "jump-right" => "向右跳跃", "climb-left" => "向左攀爬", "climb-right" => "向右攀爬",
              "climb-top-left" => "向左翻上窗沿", "climb-top-right" => "向右翻上窗沿", _ => PetPerformanceCatalog.Label(action ?? "") });
            ApplyMotion(); SettingsChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            if (!_closed && generation == _sceneGeneration && command == _sceneCommand)
            {
                _sceneActive = _manualScene = _celebrating = _clickPerformance = false;
                _llmPerformance = false;
                _manualSceneAction = null;
                RefreshSpeechSuspension();
                _landingScene.Complete();
                ApplySceneThoughtLayout(false);
                SceneStatus = "播放失败，已恢复待机：" + ex.Message;
                ApplyMotion(); SettingsChanged?.Invoke(); Program.Log(ex);
            }
        }
        finally { if (_sceneTransitionCommand == command) _sceneTransitionCommand = 0; _sceneGate.Release(); }
    }

    private static async Task StopRendererQuietly(SceneAnimationView renderer)
    {
        try { await renderer.StopAsync(true); } catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { /* Replaced/disposed renderers are no longer presented. */ }
    }

    private void UpdateSceneVisibility()
    {
        if (_scene != null) _scene.Opacity = _sceneActive ? 1 : 0;
        if (_live2D != null) _live2D.Opacity = _sceneActive ? 0 : 1;
        _image.Opacity = _sceneActive ? 0 : 1;
    }

    private void ReleaseScene()
    {
        InvalidateLlmBehavior();
        _movement?.CancelScriptedMotion();
        _sceneGeneration++; _sceneCommand++;
        _sceneTransitionCommand = 0;
        var previous = _scene; _scene = null;
        if (previous != null) { _character.Children.Remove(previous); previous.Dispose(); }
        // A display suspend reaches this path after the graphics resources may
        // already be unavailable. Detach the browser before changing layout.
        // Keep its contact geometry until the temporary headroom is removed.
        if (!_closed) ApplySceneThoughtLayout(false);
        _sceneGeometry = null;
        _builtInContactGeometry = null;
        CurrentScenePack = null;
        IsSceneLoading = false;
        _sceneActive = _manualScene = _celebrating = _clickPerformance = false;
        _manualSceneAction = null;
        RefreshSpeechSuspension();
        _landingScene.Complete();
        _sceneAction = _desiredSceneKey = null;
        ApplyMotion();
    }
}
