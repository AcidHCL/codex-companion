using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.WorkCompanion;

// Optional second-layer seam. Raw window text and pixels never enter layer-one
// observations, trigger policy, chat history, or this contract.
internal interface IWorkContextExtension
{
    event Action? Invalidated;
    TimeSpan ResponseTimeout { get; }
    void Configure(WorkCompanionOptions options, bool paused);
    Task<WorkActivity?> RecallActivityAsync(WorkObservation observation, CancellationToken ct);
    string? RequestContext(WorkObservation observation);
    bool IsExplicitRequest(WorkReactionCandidate candidate);
    Task<WorkReactionReply?> GenerateAsync(IOllamaClient client, string model, string persona,
        WorkReactionCandidate candidate, Func<Task<WorkReactionReply?>> fallback, CancellationToken ct);
    void FinishCandidate(WorkReactionCandidate candidate, bool accepted);
    void ClearTransientState();
}
