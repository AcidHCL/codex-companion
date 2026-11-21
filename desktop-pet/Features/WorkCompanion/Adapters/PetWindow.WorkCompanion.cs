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
    internal string? WorkReactionBlockCode(OllamaChatWindow? chat)
    {
        if (!CanObserveWorkDesktop) return "observer_unavailable";
        // An attached chat can stay open while the user works in another app.
        // The action rail, an active chat, and an in-progress turn still take priority.
        bool backgroundChat = chat is { IsVisible: true, IsActive: false, IsKeyboardFocusWithin: false } &&
            ReferenceEquals(chat.Owner, this);
        if (_menuOpen || IsCompanionOpen && !backgroundChat) return "pet_ui_open";
        if (_voice?.HasPendingSpeech == true) return "speech_busy";
        if (IsChatBusy || _voiceInputActive || _voiceStudioActive) return "chat_busy";
        if (_pointerPressed || _nativePointerDrag || PhysicalBehaviorBusy || _manualScene) return "interaction_busy";
        return _bubble.Visibility == Visibility.Visible ? "host_busy" : null;
    }

    internal async Task<WorkPresentation> PresentWorkReactionAsync(WorkReactionReply reply, bool speak,
        long expectedBubble, CancellationToken ct, OllamaChatWindow session)
    {
        Dispatcher.VerifyAccess(); ct.ThrowIfCancellationRequested();
        if (WorkReactionBlockCode(session) != null || SpeechBubbleRevision != expectedBubble) return WorkPresentation.Skipped;
        ShowSpeechBubble(reply.Text);
        _workBubbleRevision = SpeechBubbleRevision;
        // Commit before any asynchronous animation or user follow-up can switch the chat.
        var result = session.TrySaveCompanionReaction(reply.TargetConversationId, reply.Text, reply.EventSummary)
            ? WorkPresentation.SavedToChat : WorkPresentation.Shown;
        if (result == WorkPresentation.Shown) CompanionDiagnostics.Record("presentation", "chat_save_failed");
        if (speak)
        {
            long? before = _voice?.SpeechRevision;
            SpeakText(reply.Text, PetSpeechSource.Preset);
            _workSpeechRevision = _voice?.SpeechRevision != before ? _voice?.SpeechRevision : null;
        }
        string? expression = reply.Expression switch { "happy" => "happy", "surprised" => "startled", _ => null };
        if (expression == null || !CanPlayPerformance(expression)) return result;
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
        return result;
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
