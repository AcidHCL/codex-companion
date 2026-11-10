using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.WorkCompanion;

namespace DesktopPet;

// All feature-specific chat integration lives in this removable partial. It
// borrows the existing priority/cancellation lane, never the chat transcript.
internal sealed partial class OllamaChatWindow
{
    private CancellationTokenSource? _workReactionCancellation;

    // Deliberately independent of LlmBehaviorEnabled and of _connected: a
    // hidden host has not refreshed its UI model list yet. The stateless
    // provider transport validates exactly the selected model itself.
    // Excluding the background task here lets the module revalidate context
    // while its own generation is running, without canceling itself.
    internal bool IsWorkContextAvailable => !_shutdown && (_client is OllamaClient or OpenAiCompatibleClient) &&
        (_client is OpenAiCompatibleClient api ? api.CanAttemptBackgroundRequest && ChatApiModel.IsValid(_model) : OllamaModels.IsSupported(_model)) && _capabilities.SupportsChat != false &&
        !_generating && !_userSendPending && !_switching && !_configuring &&
        !_sessionSwitching && !_preparingImage && !VoiceInputBusy && !ModelMemoryBusy &&
        !(IsVisible && (_input.IsKeyboardFocusWithin || _imeComposing ||
            DateTimeOffset.UtcNow - _lastInputActivity < TimeSpan.FromSeconds(3)));

    internal bool CanReactToWork => IsWorkContextAvailable &&
        (_behaviorTask == null || _behaviorTask.IsCompleted);

    internal Uri WorkCompanionEndpoint => _client.Endpoint;
    internal bool WorkCompanionUsesApi => _client is OpenAiCompatibleClient;
    internal string? WorkCompanionModelBlockReason => _client is OpenAiCompatibleClient api &&
        (!ChatApiModel.IsValid(_model) || !api.CanAttemptBackgroundRequest)
        ? L.T("当前 API 配置尚未就绪，请在模型与语音中检查模型名和密钥。", "The API configuration is not ready. Check the model ID and key under Models & Speech.") : null;

    internal void CancelWorkReaction()
    {
        var cancellation = _workReactionCancellation;
        if (cancellation == null) return;
        if (ReferenceEquals(cancellation, _behaviorCancellation)) CancelBehaviorReaction();
        else try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal async Task<WorkReactionReply?> ReactToWorkAsync(WorkReactionCandidate candidate,
        CancellationToken cancellationToken, IWorkContextExtension? contextExtension = null)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ReactToWorkAsync(candidate, cancellationToken, contextExtension)).Task.Unwrap();
        if (!CanReactToWork || cancellationToken.IsCancellationRequested) return null;
        var client = _client;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        cancellation.CancelAfter((contextExtension?.ResponseTimeout ?? OllamaWorkReactionPlanner.Timeout) + TimeSpan.FromSeconds(2));
        _workReactionCancellation = _behaviorCancellation = cancellation;
        long epoch = Interlocked.Read(ref _behaviorEpoch);
        var task = RunWorkReactionAsync(client, _model, _settings.SkinId, _conversation?.Id,
            L.IsEnglish, epoch, candidate, cancellation, contextExtension);
        // Existing typing, sending, recording, model switching and shutdown
        // paths cancel _behaviorCancellation and drain _behaviorTask. Mapping
        // the structured reply keeps that established ordering intact.
        _behaviorTask = WorkReplyTextAsync(task);
        return await task;
    }

    private static async Task<string?> WorkReplyTextAsync(Task<WorkReactionReply?> task)
        => (await task)?.Text;

    private async Task<WorkReactionReply?> RunWorkReactionAsync(IOllamaClient client, string model,
        string persona, string? conversationId, bool english, long epoch,
        WorkReactionCandidate candidate, CancellationTokenSource cancellation, IWorkContextExtension? contextExtension)
    {
        bool entered = false;
        bool Current() => IsWorkContextAvailable && epoch == Interlocked.Read(ref _behaviorEpoch) &&
            ReferenceEquals(client, _client) && model == _model && conversationId == _conversation?.Id &&
            string.Equals(persona, _settings.SkinId, StringComparison.OrdinalIgnoreCase) && english == L.IsEnglish;
        try
        {
            await _modelCallGate.WaitAsync(cancellation.Token); entered = true;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current()) return null;
            if (client is OllamaClient)
            {
                var activity = OllamaRequestActivity.Snapshot(client.Endpoint.AbsoluteUri);
                if (activity.ActiveInference > 0 || activity.ActiveMaintenance > 0 ||
                    activity.GraceUntil is { } grace && grace > DateTimeOffset.UtcNow) return null;
            }
            else CompanionDiagnostics.Record("model", "model_request");
            Task<WorkReactionReply?> Fallback() => OllamaWorkReactionPlanner.GenerateAsync(client, model, persona, candidate, cancellation.Token);
            var reply = contextExtension == null ? await Fallback() :
                await contextExtension.GenerateAsync(client, model, persona, candidate, Fallback, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return Current() ? reply : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        finally
        {
            if (entered) _modelCallGate.Release();
            if (ReferenceEquals(_behaviorCancellation, cancellation)) _behaviorCancellation = null;
            if (ReferenceEquals(_workReactionCancellation, cancellation)) _workReactionCancellation = null;
            cancellation.Dispose();
        }
    }
}
