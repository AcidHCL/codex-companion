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
internal sealed record PetInteractionEvent(string Kind, DateTimeOffset OccurredAt, long Revision,
    IReadOnlyList<string>? RecentKinds = null, int ClickCount = 0, string? ClickReaction = null)
{
    internal const int MaximumClickCount = 100;
    internal static int BoundedClickCount(int count) => Math.Clamp(count, 0, MaximumClickCount);
    internal static string? KnownClickReaction(string? name) => name is "happy" or "startled" or "angry" ? name : null;

    internal string DescribeForChat(bool english)
    {
        if (Kind is not ("clicked" or "drag_started" or "released" or "fall_started" or "landed")) return "";
        var trail = RecentKinds == null ? new List<string>() : new List<string>(RecentKinds);
        if (trail.Count == 0 || trail[^1] != Kind) trail.Add(Kind);
        // Ending a drag can report gravity synchronously, before the pointer-release callback.
        for (int i = 1; i < trail.Count; i++)
        {
            if (trail[i - 1] == "fall_started" && trail[i] == "released")
                (trail[i - 1], trail[i]) = (trail[i], trail[i - 1]);
        }
        var kinds = new List<string>();
        foreach (string kind in trail)
        {
            kinds.Remove(kind);
            kinds.Add(kind);
        }
        // The queue can retain the latest click count after the click itself has left its short trail.
        if (ClickCount > 0 && !kinds.Contains("clicked")) kinds.Insert(0, "clicked");

        int count = BoundedClickCount(ClickCount);
        var actions = new List<string>();
        foreach (string kind in kinds)
        {
            string action = kind switch
            {
                "clicked" when count >= MaximumClickCount => english ? "was clicked repeatedly" : "被连续点击了多次",
                "clicked" when count > 1 => english ? $"was clicked {count} times in quick succession" : $"被连点了 {count} 下",
                "clicked" => english ? "was clicked once" : "被点了一下",
                "drag_started" => english ? "was picked up" : "被拎起",
                "released" => english ? "was released" : "被松开",
                "fall_started" => english ? "started falling" : "开始下落",
                "landed" => english ? "landed" : "落地了",
                _ => ""
            };
            if (action.Length > 0) actions.Add(action);
        }
        if (!english) return "桌宠刚刚" + string.Join("，", actions) + "。";
        string last = actions[^1];
        actions.RemoveAt(actions.Count - 1);
        string lead = actions.Count == 0 ? "" : string.Join(", ", actions) + (actions.Count == 1 ? " and " : ", and ");
        return "A moment ago, the pet " + lead + last + ".";
    }
}
internal sealed record PetBehaviorPlanResult(string Instruction, IReadOnlyList<PetBehaviorResult> Results);

// Implementations marshal to their UI thread. These capabilities control only
// this pet, never arbitrary files, programs, user input, or other application windows.
internal interface IPetBehaviorHost
{
    Task<PetBehaviorSnapshot> GetSnapshotAsync(CancellationToken ct);
    Task<PetBehaviorResult> ExecuteAsync(PetBehaviorCommand command, long expectedRevision, CancellationToken ct);
}
