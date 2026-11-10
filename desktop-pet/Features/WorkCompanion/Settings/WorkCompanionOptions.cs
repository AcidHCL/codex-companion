using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopPet.WorkCompanion;

// Feature-owned settings only. Observations, titles and generated replies are
// deliberately absent from this contract and its persisted representation.
internal sealed record WorkCompanionOptions
{
    public bool Enabled { get; init; }
    public bool ObserveApplications { get; init; } = true;
    public bool IncludeWindowTitles { get; init; }
    public bool ReactToAppChanges { get; init; } = true;
    public bool BreakReminders { get; init; } = true;
    public int BreakAfterMinutes { get; init; } = 45;
    public bool UseCodexEvents { get; init; } = true;
    public bool SpeakReactions { get; init; }
    public int CooldownMinutes { get; init; } = 5;
    public bool WorkEnabled { get; init; } = true;
    public bool GameEnabled { get; init; } = true;
    public bool MediaEnabled { get; init; } = true;
    public bool DailyEnabled { get; init; } = true;
    public bool QuietFullscreen { get; init; } = true;
    public int GameBreakAfterMinutes { get; init; } = 60;
    public int MediaBreakAfterMinutes { get; init; } = 60;
    public int DailyBreakAfterMinutes { get; init; } = 45;
    public Dictionary<string, string> AppActivities { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] ExcludedApps { get; init; } = DefaultExcludedApps();

    internal static string[] DefaultExcludedApps() => new[]
    {
        "1password", "bitwarden", "keepass", "keepassxc", "dashlane", "enpass",
        "lastpass", "protonpass", "nordpass", "keeperpasswordmanager", "credentialuibroker", "consent", "logonui"
    };

    public WorkCompanionOptions Normalize() => this with
    {
        BreakAfterMinutes = BreakAfterMinutes is 30 or 45 or 60 or 90 ? BreakAfterMinutes : 45,
        GameBreakAfterMinutes = GameBreakAfterMinutes is 30 or 45 or 60 or 90 ? GameBreakAfterMinutes : 60,
        MediaBreakAfterMinutes = MediaBreakAfterMinutes is 30 or 45 or 60 or 90 ? MediaBreakAfterMinutes : 60,
        DailyBreakAfterMinutes = DailyBreakAfterMinutes is 30 or 45 or 60 or 90 ? DailyBreakAfterMinutes : 45,
        CooldownMinutes = CooldownMinutes is 3 or 5 or 10 ? CooldownMinutes : 5,
        AppActivities = NormalizeActivities(AppActivities),
        ExcludedApps = (ExcludedApps ?? DefaultExcludedApps()).Select(NormalizeAppName)
            .Where(value => value != null).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray()
    };

    internal static string? NormalizeActivity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "work" => "work", "game" => "game", "media" => "media", "daily" => "daily", _ => null
    };

    private static Dictionary<string, string> NormalizeActivities(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Conflicting case/extension aliases are ambiguous; ignore the group
        // instead of guessing which saved entry should win.
        foreach (var group in (source ?? new()).Select(pair => new
            { Name = NormalizeAppName(pair.Key), Activity = NormalizeActivity(pair.Value) })
            .Where(pair => pair.Name != null).GroupBy(pair => pair.Name!, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count == 64) break;
            if (group.Count() == 1 && group.First().Activity is { } activity) result.Add(group.Key, activity);
        }
        return result;
    }

    internal static string? NormalizeAppName(string? value)
    {
        string name = value?.Trim() ?? "";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        if (name.Length is < 1 or > 80 || name is "." or ".." ||
            name.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ' ' or '!'))) return null;
        return name.ToLowerInvariant();
    }

    internal static bool TryParseExcludedApps(string? text, out string[] applications)
    {
        var values = (text ?? "").Split(new[] { '\r', '\n', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            string? name = NormalizeAppName(value);
            if (name == null) { applications = Array.Empty<string>(); return false; }
            unique.Add(name);
            if (unique.Count > 64) { applications = Array.Empty<string>(); return false; }
        }
        applications = unique.ToArray(); return true;
    }
}

internal sealed record WorkCompanionStatus(string Summary, string? Detail = null,
    string? LastReaction = null, bool Paused = false);

// The view speaks only to this feature interface. It cannot start an observer,
// access model clients, or modify the existing pet/chat settings on its own.
internal interface IWorkCompanionSettingsHost
{
    WorkCompanionOptions Options { get; }
    WorkCompanionStatus Status { get; }
    event Action? Changed;
    void UpdateOptions(WorkCompanionOptions options);
    void SetPaused(bool paused);
    void ClearTransientState();
}
