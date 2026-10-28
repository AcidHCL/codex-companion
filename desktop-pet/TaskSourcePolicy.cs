using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DesktopPet;

internal enum TaskSource { Unknown, Codex, Dot }
internal enum DashboardTaskFilter { Codex, Dot, All }

// Task ownership is independent of the RPC backend (Kind) and task identity (Key).
// The current list_threads API does not expose Dot ownership. Do not infer it
// from a title, status, project name, pinned state, or merely a remote host.
internal static class TaskSourcePolicy
{
    internal const int MaximumOverrides = 2000;

    internal static TaskSource ClassifyHost(string kind, string? hostId) =>
        kind == "codex" && (hostId == null || hostId == "local") ? TaskSource.Codex : TaskSource.Unknown;

    internal static TaskSource Classify(JsonElement task)
    {
        if (CodexParser.Text(task, "kind") != "codex") return TaskSource.Unknown;
        string? host = CodexParser.Text(task, "hostId");
        // Forward-compatible adapter metadata, not extra RPCs or body inspection:
        // native Dot ownership requires ALL of durable host, aeon thread source,
        // and agent profile aeon_kind=orbit. An aeon or durable task alone is not
        // Dot. Current list_threads omits this profile, so such rows stay unknown
        // until an explicit user override or this complete evidence is supplied.
        if (host == "durable" && CodexParser.Text(task, "threadSource") == "aeon" &&
            CodexParser.Text(CodexParser.Get(task, "agentProfile"), "aeon_kind") == "orbit")
            return TaskSource.Dot;
        var threadSource = CodexParser.Get(task, "threadSource");
        if (threadSource.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
            CodexParser.Text(task, "threadSource") != "user") return TaskSource.Unknown;
        return ClassifyHost("codex", host);
    }

    internal static CodexTaskInfo ApplyOverride(CodexTaskInfo task, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(task.Key, out string? value) && TryParseOverride(value, out var source))
            return task with { Source = source, SourceIsManual = true };
        return task with { Source = task.DetectedSource, SourceIsManual = false };
    }

    internal static bool TryParseOverride(string? value, out TaskSource source)
    {
        source = value switch { "codex" => TaskSource.Codex, "dot" => TaskSource.Dot, _ => TaskSource.Unknown };
        return source != TaskSource.Unknown;
    }

    internal static Dictionary<string, string> NormalizeOverrides(Dictionary<string, string>? overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overrides == null) return result;
        foreach (var entry in overrides)
        {
            if (result.Count >= MaximumOverrides) break;
            if (entry.Key.Length > 1024 || !entry.Key.StartsWith("codex:", StringComparison.Ordinal) ||
                entry.Key.Any(char.IsControl) || !TryParseOverride(entry.Value, out _)) continue;
            result[entry.Key] = entry.Value;
        }
        return result;
    }

    internal static bool Matches(CodexTaskInfo task, DashboardTaskFilter filter) => filter switch
    {
        DashboardTaskFilter.Codex => task.Source == TaskSource.Codex,
        DashboardTaskFilter.Dot => task.Source == TaskSource.Dot,
        DashboardTaskFilter.All => true,
        _ => false
    };

    internal static string SourceLabel(CodexTaskInfo task) =>
        (task.Source switch { TaskSource.Codex => "Codex", TaskSource.Dot => "Dot", _ => "来源待确认" }) +
        (task.SourceIsManual ? " · 手动" : "");

    internal static CodexSnapshot Project(CodexSnapshot snapshot, TaskSource source) =>
        snapshot with { Tasks = snapshot.Tasks.Where(task => task.Source == source).ToArray() };
}
