using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    private readonly PetClickSequence _clickSequence = new();
    private bool _pointerPressed, _nativePointerDrag, _clickPerformance;
    private Point _pointerOrigin;
    private long _clickGeneration;

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_closed || _handle == IntPtr.Zero || DisplayRenderingPaused || Settings.ClickThrough || _nativePointerDrag) return;
        _pointerOrigin = _character.PointToScreen(e.GetPosition(_character));
        _pointerPressed = _character.CaptureMouse();
        if (_pointerPressed) _movement?.StopDirectedWalk();
        UpdateMovementAutonomy();
        // Every click belongs to the interaction burst, including a double
        // click. The action rail remains the explicit way to open chat.
        e.Handled = true;
    }

    private void OnLeftMove(object sender, MouseEventArgs e)
    {
        if (!_pointerPressed || _nativePointerDrag) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelPointerPress(); return; }
        Point cursor = _character.PointToScreen(e.GetPosition(_character));
        double scale = NativeMethods.GetDpiScale(_handle);
        if (Math.Abs(cursor.X - _pointerOrigin.X) < Math.Max(4, SystemParameters.MinimumHorizontalDragDistance) * scale &&
            Math.Abs(cursor.Y - _pointerOrigin.Y) < Math.Max(4, SystemParameters.MinimumVerticalDragDistance) * scale) return;

        _nativePointerDrag = true;
        _pointerPressed = false;
        _character.ReleaseMouseCapture();
        _clickGeneration++;
        _clickSequence.Reset();
        BeginBehaviorPointerInteraction();
        // Start passive drag only after actual pointer movement. A tap must
        // never release the current support or briefly show a lifted pose.
        _movement?.BeginDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally
        {
            if (!_closed)
            {
                ClampPetToVisibleWorkArea();
                _movement?.EndDrag();
                CompleteBehaviorPointerInteraction(forceDrag: true);
                QueueSave();
            }
            _nativePointerDrag = false;
            UpdateMovementAutonomy();
        }
        e.Handled = true;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerPressed || _nativePointerDrag) return;
        _pointerPressed = false;
        _character.ReleaseMouseCapture();
        ObserveRendererOperation(ReactToClickAsync());
        UpdateMovementAutonomy();
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, MouseEventArgs e)
    {
        if (!_pointerPressed || _nativePointerDrag) return;
        CancelPointerPress(); // Losing capture is not a completed click.
    }

    private void CancelPointerPress()
    {
        _pointerPressed = false;
        if (_character.IsMouseCaptured) _character.ReleaseMouseCapture();
        UpdateMovementAutonomy();
    }

    private void ResetClickInteraction()
    {
        _clickGeneration++;
        _clickSequence.Reset();
        CancelPointerPress();
        if (_clickPerformance) StopScenePreviewCore();
    }

    // Shared by the actual mouse-up path and isolated integration checks.
    internal async Task ReactToClickAsync(DateTimeOffset? occurredAt = null)
    {
        Dispatcher.VerifyAccess();
        if (_closed || !IsVisible || DisplayRenderingPaused || Settings.ClickThrough) return;
        DateTimeOffset now = occurredAt ?? DateTimeOffset.UtcNow;
        int count = _clickSequence.Register(now);
        string expression = PetClickSequence.Expression(count);
        long generation = ++_clickGeneration;
        // A poke must not cancel an in-flight jump/climb or a passive fall.
        // The coordinator still cancels its older reaction upon the new event.
        if (!PhysicalBehaviorBusy) InvalidateLlmBehavior();
        long revision = _behaviorRevision;
        string? played = null;
        if (!PhysicalBehaviorBusy && !_menuOpen && (!_manualScene || _clickPerformance) && CanPlayPerformance(expression))
        {
            if (!(_clickPerformance && _manualSceneAction == expression && _sceneAction == expression && _sceneActive && _sceneTransitionCommand == 0))
                await PreviewSceneCoreAsync(expression, llmOwned: false, clickOwned: true);
            if (generation != _clickGeneration || revision != _behaviorRevision || _closed || !IsVisible || DisplayRenderingPaused) return;
            if (_sceneActive && _clickPerformance && _manualSceneAction == expression && _sceneAction == expression && _sceneTransitionCommand == 0) played = expression;
        }
        else if (!PhysicalBehaviorBusy && !_manualScene) Bounce();

        if (generation != _clickGeneration || _closed || !IsVisible || DisplayRenderingPaused) return;
        // Immediate visual acknowledgement also works without an LLM. Keep
        // this tiny bubble silent so rapid pokes do not queue TTS utterances;
        // the eventual model reaction uses the normal speech output path.
        if (!Settings.LlmBehaviorEnabled || !_explicitBehaviorCommandsActive)
            ShowSpeechBubble(CurrentSkin.Id == "deepseek"
                ? count >= 3 ? "再戳本鲸就要生气了！赔一碗白米饭！" : count == 2 ? "哎呀，怎么又戳本鲸！" : "嗯？找本鲸有什么事？"
                : count >= 3 ? "别一直戳我啦！" : count == 2 ? "又戳我？" : "我在呢～");
        EmitBehaviorInteraction("clicked", count, played, now);
    }
}
