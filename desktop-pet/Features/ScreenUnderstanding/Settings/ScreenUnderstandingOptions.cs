using System;
using System.Collections.Generic;

namespace DesktopPet.ScreenUnderstanding;

internal sealed record ScreenUnderstandingOptions
{
    public bool Enabled { get; init; }
    public bool ReadWindowText { get; init; } = true;
    public bool UseScreenshots { get; init; } = true;
    public bool LearnApplications { get; init; } = true;
    public bool AllowRemoteEndpoint { get; init; }
    public int AutomaticIntervalMinutes { get; init; } = 5;

    public ScreenUnderstandingOptions Normalize() => this with
    { AutomaticIntervalMinutes = AutomaticIntervalMinutes is 3 or 5 or 10 ? AutomaticIntervalMinutes : 5 };
}

// LastSummary is transient status only. It must never enter either settings or
// learned-application storage, chat history, or the shared memory database.
internal sealed record ScreenUnderstandingStatus(string Summary, string? Detail = null, string? LastSummary = null);

internal interface IScreenUnderstandingSettingsHost
{
    ScreenUnderstandingOptions Options { get; }
    ScreenUnderstandingStatus Status { get; }
    event Action? Changed;
    void UpdateOptions(ScreenUnderstandingOptions options);
    void RequestNextWindowAnalysis();
    IReadOnlyList<LearnedApplication> GetMemories();
    void UpdateMemoryCategory(string id, string category);
    void DeleteMemory(string id);
    void ClearMemories();
}
