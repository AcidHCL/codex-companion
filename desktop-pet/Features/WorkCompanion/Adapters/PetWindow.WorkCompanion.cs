using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DesktopPet.WorkCompanion;

namespace DesktopPet;

// A small, feature-owned presentation adapter. No physical movement or musical
// performance commands are exposed to background work observations.
internal sealed partial class PetWindow
{
    private long? _workExpressionCommand;
    private long? _workBubbleRevision;
    private long? _workSpeechRevision;
    internal long WorkCompanionBehaviorRevision => _behaviorRevision;
    internal bool CanObserveWorkDesktop => CompanionSurfacesAvailable && !DisplayRenderingPaused;
    internal string WorkReactionBlockCode => !CanObserveWorkDesktop ? "observer_unavailable" :
        IsCompanionOpen || _menuOpen ? "pet_ui_open" :
        _voice?.HasPendingSpeech == true ? "speech_busy" :
        IsChatBusy || _voiceInputActive || _voiceStudioActive ? "chat_busy" :
        _pointerPressed || _nativePointerDrag || PhysicalBehaviorBusy || _manualScene ? "interaction_busy" : "host_busy";
    internal bool CanPresentWorkReaction => CompanionSurfacesAvailable && !DisplayRenderingPaused &&
        !_pointerPressed && !_nativePointerDrag && !PhysicalBehaviorBusy && !_manualScene &&
        !_menuOpen && !IsCompanionOpen && !IsChatBusy && !_voiceInputActive && !_voiceStudioActive &&
        _voice?.HasPendingSpeech != true && _bubble.Visibility != Visibility.Visible;

    internal async Task<bool> PresentWorkReactionAsync(WorkReactionReply reply, bool speak,
        long expectedBubble, CancellationToken ct)
    {
        Dispatcher.VerifyAccess(); ct.ThrowIfCancellationRequested();
        if (!CanPresentWorkReaction || SpeechBubbleRevision != expectedBubble) return false;
        ShowSpeechBubble(reply.Text);
        _workBubbleRevision = SpeechBubbleRevision;
        if (speak)
        {
            long? before = _voice?.SpeechRevision;
            SpeakText(reply.Text, PetSpeechSource.Preset);
            _workSpeechRevision = _voice?.SpeechRevision != before ? _voice?.SpeechRevision : null;
        }
        string? expression = reply.Expression switch { "happy" => "happy", "surprised" => "startled", _ => null };
        if (expression == null || !CanPlayPerformance(expression)) return true;
        var transition = PreviewSceneCoreAsync(expression, llmOwned: true);
        long command = _sceneCommand;
        _workExpressionCommand = command;
        try { await transition.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (_workExpressionCommand == command) CancelWorkExpression();
            ObserveRendererOperation(transition);
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { if (_workExpressionCommand == command) CancelWorkExpression(); }
        return true; // The text was already shown even if an optional animation failed.
    }

    internal void CancelWorkExpression()
    {
        long? owned = _workExpressionCommand;
        _workExpressionCommand = null;
        if (owned is { } command && _sceneCommand == command && _llmPerformance &&
            _manualSceneAction is "happy" or "startled")
        { _llmPerformance = false; StopScenePreviewCore(); }
    }

    internal void CancelWorkPresentation()
    {
        long? ownedBubble = _workBubbleRevision;
        long? ownedSpeech = _workSpeechRevision;
        _workBubbleRevision = null;
        _workSpeechRevision = null;
        CancelWorkExpression();
        // A voice audition can supersede speech without replacing the bubble.
        // Cancel only the actual utterance started by this feature.
        if (ownedSpeech.HasValue && ownedSpeech == _voice?.SpeechRevision) StopSpeech();
        if (ownedBubble == SpeechBubbleRevision)
        {
            // A newer user/notice bubble revokes this ownership automatically.
            _speechTimer.Stop();
            _bubble.Visibility = Visibility.Collapsed; UpdateBadgePlacement();
        }
    }
}
