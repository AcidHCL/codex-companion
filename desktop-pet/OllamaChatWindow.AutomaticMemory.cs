using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed partial class OllamaChatWindow
{
    private sealed record MemoryReview(IOllamaClient Client, string Model, string Scope, string ConversationId,
        long Sequence, bool English, bool Companion, string Context, string Reply, bool Retried = false);

    private readonly Queue<MemoryReview> _pendingMemoryReviews = new();
    private CancellationTokenSource? _automaticMemoryReview;
    private Task _memoryWorker = Task.CompletedTask;
    internal Task PendingAutomaticMemoryForTesting => _memoryWorker;
    private long _memoryEpoch;
    private int _foregroundModelWaiters;
    private bool AutomaticMemoryOn => !_shutdown && _settings.SharedMemoryEnabled && _settings.AutomaticMemoryEnabled;

    // Only the two successful transcript commit paths call this. Never scan old chats on startup.
    private void QueueAutomaticMemory(string conversationId, long sequence, string context, string reply, bool companion)
    {
        if (!AutomaticMemoryOn || _client is not (OllamaClient or OpenAiCompatibleClient)) return;
        // Limit transient text retained while a long foreground request owns the model.
        if (_pendingMemoryReviews.Count >= 32) _pendingMemoryReviews.Dequeue();
        _pendingMemoryReviews.Enqueue(new(_client, _model, EndpointScope(_client.Endpoint), conversationId,
            sequence, L.IsEnglish, companion, context, reply));
        if (_memoryWorker.IsCompleted) _memoryWorker = ReviewPendingMemoriesAsync(_lifetime.Token);
    }

    private void DiscardAutomaticMemoryQueue()
    {
        ++_memoryEpoch;
        _pendingMemoryReviews.Clear();
        _automaticMemoryReview?.Cancel();
    }

    private async Task EnterForegroundModelLaneAsync(CancellationToken ct)
    {
        ++_foregroundModelWaiters;
        try
        {
            _automaticMemoryReview?.Cancel();
            await _modelCallGate.WaitAsync(ct);
        }
        finally { --_foregroundModelWaiters; }
    }

    private bool CanReview(MemoryReview item, long epoch) => AutomaticMemoryOn && epoch == _memoryEpoch &&
        ReferenceEquals(item.Client, _client) && item.Model == _model && item.Scope == EndpointScope(_client.Endpoint);

    private async Task ReviewPendingMemoriesAsync(CancellationToken lifetime)
    {
        // Yield to the UI after the bubble/answer is shown, including when a test transport completes synchronously.
        await Task.Yield();
        while (_pendingMemoryReviews.TryDequeue(out var item) && !lifetime.IsCancellationRequested)
        {
            long epoch = _memoryEpoch;
            if (!CanReview(item, epoch)) continue;
            using var review = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            bool entered = false;
            try
            {
                await Task.Delay(350, review.Token);
                if (!CanReview(item, epoch)) continue;
                while (true)
                {
                    await _modelCallGate.WaitAsync(review.Token);
                    if (_foregroundModelWaiters == 0) { entered = true; break; }
                    _modelCallGate.Release();
                    await Task.Delay(100, review.Token);
                }
                if (!CanReview(item, epoch) ||
                    !_chatStore.CanReviewTurnForAutomaticMemory(item.Scope, item.ConversationId, item.Sequence)) continue;
                _automaticMemoryReview = review;
                var note = await AutomaticMemoryJudge.EvaluateAsync(item.Client, item.Model, item.English,
                    item.Companion, item.Context, item.Reply, review.Token);
                review.Token.ThrowIfCancellationRequested();
                if (note != null && CanReview(item, epoch) &&
                    _chatStore.TryAddAutomaticMemory(item.Scope, item.ConversationId, item.Sequence, note.Title, note.Content))
                    _memoryWindow?.RefreshList();
            }
            catch (OperationCanceledException) when (review.IsCancellationRequested)
            {
                if (!item.Retried && CanReview(item, epoch) && _pendingMemoryReviews.Count < 32)
                    _pendingMemoryReviews.Enqueue(item with { Retried = true });
            }
            catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
            {
                // An unavailable model or optional memory-store failure must not undo a committed chat turn.
                if (ex is ChatMemoryStoreException) ReportStorageFailure(ex);
            }
            finally
            {
                _automaticMemoryReview = null;
                if (entered) _modelCallGate.Release();
            }
        }
    }
}
