using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.WorkCompanion;

internal enum WorkActivity { Work, Game, Media, Daily }

// Deliberately contains no screen pixels, UI tree, document text, or key values.
// Values live only in this module's short-lived observation state.
internal sealed record WorkObservation(DateTimeOffset At, string App, string? WindowTitle,
    string WindowKey, TimeSpan IdleFor, bool IsAvailable, bool IsFullscreen, bool IsOwnApp)
{
    public string Status { get; init; } = "available";
    public WorkActivity Activity { get; init; } = WorkActivity.Work;

    public static WorkObservation Unavailable(DateTimeOffset at, string status) =>
        new(at, "", null, "", TimeSpan.Zero, false, false, false) { Status = status };
}

internal interface IDesktopObserver
{
    Task<WorkObservation> ReadAsync(bool includeTitles, IReadOnlyList<string> excludedApps,
        CancellationToken cancellationToken);
}

internal sealed record WorkReactionCandidate(string Id, string Kind, DateTimeOffset At,
    DateTimeOffset ExpiresAt, string App, string? Title, string? PreviousApp, int ActiveMinutes)
{
    public WorkActivity Activity { get; init; } = WorkActivity.Work;
    public WorkActivity? PreviousActivity { get; init; }
    // Target identity remains internal; it is never part of an LLM prompt.
    public string? WindowKey { get; init; }
    public string? ContextRequestKey { get; init; }
}
