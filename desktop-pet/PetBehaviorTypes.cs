using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal enum PetBehaviorOrigin { Conversation, PassiveReaction }
internal sealed record PetBehaviorCommand(string Action, string? Name = null, string? Direction = null,
    double Seconds = 3, PetBehaviorOrigin Origin = PetBehaviorOrigin.Conversation);
internal sealed record PetBehaviorResult(bool Accepted, string Message, string Action);
internal sealed record PetBehaviorSnapshot(string SkinId, string State, long Revision, bool Enabled,
    bool Visible, bool CanWalk, IReadOnlyList<string> Performances, bool UserPerformance = false, bool SoundEnabled = true,
    bool CanJump = false, bool CanClimb = false);
internal sealed record PetInteractionEvent(string Kind, DateTimeOffset OccurredAt, long Revision, IReadOnlyList<string>? RecentKinds = null);
internal sealed record PetBehaviorPlanResult(string Instruction, IReadOnlyList<PetBehaviorResult> Results);

// Implementations marshal to their UI thread. These capabilities control only
// this pet, never arbitrary files, programs, user input, or other application windows.
internal interface IPetBehaviorHost
{
    Task<PetBehaviorSnapshot> GetSnapshotAsync(CancellationToken ct);
    Task<PetBehaviorResult> ExecuteAsync(PetBehaviorCommand command, long expectedRevision, CancellationToken ct);
}
