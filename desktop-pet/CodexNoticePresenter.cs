using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed record CodexNoticePresentation(string Text, string Body, bool Expanded);

// Notification summaries never use a chat session or a memory store. Keep the
// asynchronous request separate from polling, and discard revoked/stale replies.
internal sealed class CodexNoticePresenter : IDisposable
{
    private readonly Func<string?, string, Uri, CancellationToken, Task<ScheduledTaskAnnouncementResult>> _describe;
    private readonly Action<PetNotice, CodexNoticePresentation> _present;
    private CancellationTokenSource? _pending;
    private Func<bool>? _eligible;
    private bool _disposed;

    public CodexNoticePresenter(Action<PetNotice, CodexNoticePresentation> present, Func<int>? keepAliveMinutes = null)
        : this(new ScheduledTaskAnnouncement(endpoint => new OllamaClient(endpoint, keepAliveMinutes)).CreateAsync, present) { }

    internal CodexNoticePresenter(
        Func<string?, string, Uri, CancellationToken, Task<ScheduledTaskAnnouncementResult>> describe,
        Action<PetNotice, CodexNoticePresentation> present)
    { _describe = describe; _present = present; }

    internal async Task ShowAsync(PetNotice notice, string model, string address, Func<bool> eligible)
    {
        CancelPending();
        if (_disposed || !eligible()) return;
        using var cancellation = new CancellationTokenSource();
        _pending = cancellation; _eligible = eligible;
        try
        {
            string? output = null;
            if (notice.IsScheduled)
            {
                try
                {
                    var result = await _describe(notice.ScheduledOutput, model, OllamaEndpoint.Parse(address), cancellation.Token);
                    output = result.Output;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
                { /* An unavailable summary must not suppress the completion notice. */ }
            }
            if (_disposed || cancellation.IsCancellationRequested || !eligible()) return;
            string body = string.IsNullOrWhiteSpace(output) ? notice.Message : output;
            string text = notice.Title + (L.IsEnglish ? ": " : "：") + body;
            _present(notice, new(text, body, notice.IsScheduled && !string.IsNullOrWhiteSpace(output)));
        }
        finally
        {
            if (ReferenceEquals(_pending, cancellation)) { _pending = null; _eligible = null; }
        }
    }

    internal void Revalidate()
    {
        if (_eligible != null && !_eligible()) CancelPending();
    }

    private void CancelPending()
    {
        _pending?.Cancel();
        _pending = null; _eligible = null;
    }

    public void Dispose() { _disposed = true; CancelPending(); }
}
