using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopPet;

// These are task-driven animation intents. A future companion renderer can map
// them onto its own assets without depending on PetWindow or the Codex transport.
internal enum CompanionActivity { Disconnected, Idle, Working, Attention }

internal sealed record CompanionState(TaskSource Source, bool Connected, CompanionActivity Activity, IReadOnlyList<CodexTaskInfo> Tasks)
{
    internal static CompanionState Create(CodexSnapshot snapshot, TaskSource source)
    {
        // Own a read-only copy: a later poll or caller mutation must not change
        // the state already observed by a companion renderer.
        var tasks = Array.AsReadOnly(snapshot.Tasks.Where(t => t.Source == source).ToArray());
        var activity = !snapshot.Connected ? CompanionActivity.Disconnected
            : tasks.Any(t => t.NeedsAttention) ? CompanionActivity.Attention
            : tasks.Any(t => t.IsRunning) ? CompanionActivity.Working
            : CompanionActivity.Idle;
        return new CompanionState(source, snapshot.Connected, activity, tasks);
    }

    internal bool HasSameContent(CompanionState other) =>
        Source == other.Source && Connected == other.Connected && Activity == other.Activity && Tasks.SequenceEqual(other.Tasks);
}

// A single metadata feed yields two independent character states. This class
// has no window, renderer, timer, network access, or persistence responsibility.
internal sealed class CompanionTaskRouter
{
    public CompanionState MainPet { get; private set; } = CompanionState.Create(CodexSnapshot.Empty, TaskSource.Codex);
    public CompanionState Dot { get; private set; } = CompanionState.Create(CodexSnapshot.Empty, TaskSource.Dot);
    public CodexSnapshot MainPetSnapshot { get; private set; } = CodexSnapshot.Empty;

    // Deliberately not a user setting yet: no Dot surface is created or shown
    // in this release, even while its independent task state is kept current.
    public bool DotCompanionEnabled => false;
    public bool DotCompanionVisible => false;

    public event Action? Changed;

    public void Observe(CodexSnapshot snapshot)
    {
        var main = CompanionState.Create(snapshot, TaskSource.Codex);
        var dot = CompanionState.Create(snapshot, TaskSource.Dot);
        bool changed = !MainPet.HasSameContent(main) || !Dot.HasSameContent(dot);
        MainPet = main;
        Dot = dot;
        MainPetSnapshot = TaskSourcePolicy.Project(snapshot, TaskSource.Codex) with { Tasks = main.Tasks };
        // Repeated identical polls must not restart a future companion's action.
        if (changed) Changed?.Invoke();
    }
}
