using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.WorkCompanion;

// Observation and model generation remain separate from committing a visible chat remark.
internal sealed class PetWorkCompanionHost : IWorkCompanionHost, IDisposable
{
    private readonly PetWindow _pet;
    private readonly Func<OllamaChatWindow?> _getChat, _ensureChat;
    private readonly IWorkContextExtension? _contextExtension;
    private readonly Func<string, CancellationToken, Task<OllamaGpuActivity>> _readActivity;
    private bool _disposed;

    internal PetWorkCompanionHost(PetWindow pet, Func<OllamaChatWindow?> getChat,
        Func<OllamaChatWindow?> ensureChat, IWorkContextExtension? contextExtension = null,
        Func<string, CancellationToken, Task<OllamaGpuActivity>>? readActivity = null)
    { _pet = pet; _getChat = getChat; _ensureChat = ensureChat; _contextExtension = contextExtension;
        _readActivity = readActivity ?? OllamaActivityTelemetry.ReadAsync; }

    public WorkHostState Capture()
    {
        var settings = _pet.Settings;
        var chat = _getChat();
        string context = string.Join("\0", ChatModelConnection.Scope(ChatModelConnection.ActiveEndpoint(settings), settings.UseChatApi),
            ChatModelConnection.ActiveModel(settings),
            settings.UseChatApi ? settings.ApiProfile.SupportsImages.ToString() : "",
            settings.UseChatApi ? settings.ApiProfile.SupportsTools.ToString() : "",
            settings.Language, _pet.CurrentSkin.Id, _pet.WorkCompanionBehaviorRevision,
            chat?.ModelConnectionRevision ?? 0);
        string? petBlocked = _pet.WorkReactionBlockCode(chat);
        bool available = !_disposed && petBlocked == null && chat?.IsWorkContextAvailable != false;
        string? modelBlocked = available ? null : chat?.WorkCompanionModelBlockReason;
        if (!available && !_disposed) CompanionDiagnostics.Record("presentation",
            petBlocked ?? (modelBlocked != null ? "model_not_ready" : "chat_busy"));
        return new WorkHostState(available, context, _pet.SpeechBubbleRevision,
            available ? null : modelBlocked ?? L.T("聊天、语音、操作桌宠或其他提示优先。", "Chat, speech, pet interaction and other notices take priority."),
            CanObserve: !_disposed && _pet.CanObserveWorkDesktop);
    }

    public async Task<WorkReactionReply?> GenerateAsync(WorkReactionCandidate candidate, CancellationToken ct)
    {
        if (!Capture().Available) { CompanionDiagnostics.Record("model", "host_busy"); return null; }
        var session = _getChat() ?? _ensureChat();
        if (session == null || !session.CanReactToWork) { CompanionDiagnostics.Record("model", "model_busy"); return null; }
        var expected = Capture();
        string? conversationId = session.CurrentConversationId;
        if (!session.WorkCompanionUsesApi)
        {
            var endpoint = session.WorkCompanionEndpoint.AbsoluteUri;
            var own = OllamaRequestActivity.Snapshot(endpoint);
            if (own.ActiveInference > 0 || own.ActiveMaintenance > 0 ||
                own.GraceUntil is { } grace && grace > DateTimeOffset.UtcNow)
            { CompanionDiagnostics.Record("model", "model_busy"); return null; }
            // A single bounded, read-only native sample at most once per candidate.
            // A local Ollama workload cannot block an unrelated API service.
            var activity = await _readActivity(endpoint, ct);
            if (activity.Available && activity.UtilizationPercent >= 20)
            { CompanionDiagnostics.Record("model", "model_busy"); return null; }
        }
        ct.ThrowIfCancellationRequested();
        if (!WorkCompanionController.SameContext(expected, Capture()) || !ReferenceEquals(session, _getChat()) ||
            session.CurrentConversationId != conversationId) return null;
        var reply = await session.ReactToWorkAsync(candidate, ct, _contextExtension);
        return reply == null ? null : reply with { TargetConversationId = conversationId };
    }

    public void FinishCandidate(WorkReactionCandidate candidate, bool accepted) =>
        _contextExtension?.FinishCandidate(candidate, accepted);

    public Task<WorkPresentation> PresentAsync(WorkReactionReply reply, WorkHostState expected, bool speak, CancellationToken ct)
    {
        var session = _getChat();
        if (_disposed || reply.ContextOnly || session == null || session.CurrentConversationId != reply.TargetConversationId ||
            !WorkCompanionController.SameContext(expected, Capture()))
            return Task.FromResult(WorkPresentation.Skipped);
        return _pet.PresentWorkReactionAsync(reply, speak, expected.Revision, ct, session);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _getChat()?.CancelWorkReaction(); CancelPresentation();
    }

    internal void CancelPresentation() => _pet.CancelWorkPresentation();
}
