using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed partial class OllamaChatWindow
{
    // Scheduled output borrows the selected client and the background lane, not
    // the conversation. A user message or provider change cancels this work.
    internal async Task<ScheduledTaskAnnouncementResult> DescribeScheduledOutputAsync(string? output,
        string expectedModel, Uri expectedEndpoint, CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        ct.ThrowIfCancellationRequested();
        if (ScheduledTaskAnnouncement.ShortResult(output) is { } shortResult) return shortResult;
        if (_shutdown || _model != expectedModel || _client.Endpoint != expectedEndpoint ||
            _generating || _userSendPending || _switching || _configuring || _sessionSwitching ||
            _preparingImage || VoiceInputBusy || ModelMemoryBusy || _behaviorTask is { IsCompleted: false } ||
            IsVisible && (_input.IsKeyboardFocusWithin || _imeComposing || DateTimeOffset.UtcNow - _lastInputActivity < TimeSpan.FromSeconds(3)))
            return ScheduledTaskAnnouncementResult.Fallback;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(35));
        _behaviorCancellation = cancellation;
        var client = _client;
        string persona = _settings.SkinId, language = _settings.Language;
        long epoch = Interlocked.Read(ref _behaviorEpoch);
        var task = RunScheduledOutputAsync(output, expectedModel, client, persona, language, epoch, cancellation);
        _behaviorTask = ScheduledOutputTextAsync(task);
        return await task;
    }

    private static async Task<string?> ScheduledOutputTextAsync(Task<ScheduledTaskAnnouncementResult> task)
    {
        // StopBehaviorReactionAsync drains this task; cancellation is a normal
        // transition for the caller, not a failure of model switching.
        try { return (await task).Output; } catch (OperationCanceledException) { return null; }
    }

    private async Task<ScheduledTaskAnnouncementResult> RunScheduledOutputAsync(string? output, string model,
        IOllamaClient client, string persona, string language, long epoch, CancellationTokenSource cancellation)
    {
        bool entered = false;
        bool Current() => !_shutdown && ReferenceEquals(client, _client) && model == _model &&
            epoch == Interlocked.Read(ref _behaviorEpoch) && persona == _settings.SkinId && language == _settings.Language;
        try
        {
            await _modelCallGate.WaitAsync(cancellation.Token); entered = true;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current()) return ScheduledTaskAnnouncementResult.Fallback;
            var result = await ScheduledTaskAnnouncement.CreateWithClientAsync(output, model, client, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return Current() ? result : ScheduledTaskAnnouncementResult.Fallback;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { return ScheduledTaskAnnouncementResult.Fallback; }
        finally
        {
            if (entered) _modelCallGate.Release();
            if (ReferenceEquals(_behaviorCancellation, cancellation)) _behaviorCancellation = null;
            cancellation.Dispose();
        }
    }
}
