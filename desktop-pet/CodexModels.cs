using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopPet;

internal sealed record UsageWindowInfo(int DurationMinutes, double? UsedPercent, DateTimeOffset? ResetsAt)
{
    public double? RemainingPercent => UsedPercent.HasValue ? Math.Clamp(100 - UsedPercent.Value, 0, 100) : null;
    public string PeriodLabel => DurationMinutes switch { 10080 => "每周", 300 => "5 小时", _ => DurationMinutes > 0 ? DurationMinutes + " 分钟" : "未知周期" };
}
internal sealed record UsageBucketInfo(string Id, string Name, string? Plan, string? CreditBalance, bool? HasCredits, bool Unlimited, IReadOnlyList<UsageWindowInfo> Windows);
internal sealed record CodexTaskInfo(string Id, string Title, string Status, string Kind, string? HostId, string? ProjectName, double UpdatedAt, bool Pinned)
{
    public TaskSource Source { get; init; } = TaskSourcePolicy.ClassifyHost(Kind, HostId);
    public bool SourceIsManual { get; init; }
    internal TaskSource DetectedSource { get; init; } = TaskSourcePolicy.ClassifyHost(Kind, HostId);
    public string Key => Kind + ":" + (HostId ?? "local") + ":" + Id;
    public bool IsRunning => Status is "active" or "inProgress" or "running";
    public bool NeedsAttention => Status is "needsAttention" or "waitingForInput" or "waitingForApproval" or "interrupted" or "error" or "systemError";
    public string StatusLabel => Status switch { "waitingForApproval" => "等待审批", "waitingForInput" => "等待输入", "systemError" or "error" => "任务异常", "notLoaded" => "未加载", _ => IsRunning ? "正在执行" : NeedsAttention ? "需要关注" : Status is "idle" or "completed" ? "空闲" : "状态未知" };
}
internal sealed record PublicMessage(string Id, string Role, string Text, string? Phase);
internal sealed record ConversationPage(string TaskKey, IReadOnlyList<PublicMessage> Messages, string? NextCursor, string? LatestTurnStatus, bool HasMore);
internal sealed record CodexSnapshot(bool Connected, bool Refreshing, string StatusText, DateTimeOffset? LastUpdated, DateTimeOffset? UsageUpdatedAt, IReadOnlyList<UsageBucketInfo> Buckets, IReadOnlyList<CodexTaskInfo> Tasks, string? Error, int? ResetCreditsAvailable)
{
    public static CodexSnapshot Empty => new(false, false, "等待连接 Codex", null, null, Array.Empty<UsageBucketInfo>(), Array.Empty<CodexTaskInfo>(), null, null);
    public IReadOnlyList<CodexTaskInfo> CodexTasks => Tasks.Where(task => task.Source == TaskSource.Codex).ToArray();
    public IReadOnlyList<CodexTaskInfo> DotTasks => Tasks.Where(task => task.Source == TaskSource.Dot).ToArray();
    public IReadOnlyList<CodexTaskInfo> UnknownTasks => Tasks.Where(task => task.Source == TaskSource.Unknown).ToArray();
}
internal sealed record PetNotice(string Title, string Message, string Kind);

internal static class CodexParser
{
    public static (IReadOnlyList<UsageBucketInfo> Buckets, int? ResetCredits, string? AccountId) ParseUsage(JsonElement data)
    {
        RequireObject(data);
        var buckets = new List<UsageBucketInfo>();
        var map = Get(data, "rateLimitsByLimitId");
        if (map.ValueKind == JsonValueKind.Object)
            foreach (var pair in map.EnumerateObject())
                if (pair.Value.ValueKind == JsonValueKind.Object) buckets.Add(ParseBucket(pair.Name, pair.Value));
        if (buckets.Count == 0 && Get(data, "rateLimits").ValueKind == JsonValueKind.Object)
            buckets.Add(ParseBucket(Text(Get(data, "rateLimits"), "limitId") ?? "codex", Get(data, "rateLimits")));
        if (!data.TryGetProperty("rateLimits", out _) && !data.TryGetProperty("rateLimitsByLimitId", out _))
            throw new InvalidDataException("Codex 用量接口格式发生变化。");
        int? resets = Number(Get(data, "rateLimitResetCredits"), "availableCount") is double count && count >= 0 ? (int)count : null;
        return (buckets, resets, Text(data, "accountId"));
    }

    private static UsageBucketInfo ParseBucket(string id, JsonElement data)
    {
        var windows = new List<UsageWindowInfo>();
        foreach (string slot in new[] { "primary", "secondary" })
        {
            var window = Get(data, slot);
            if (window.ValueKind != JsonValueKind.Object) continue;
            double? percent = Number(window, "usedPercent");
            if (percent.HasValue) percent = Math.Clamp(percent.Value, 0, 100);
            windows.Add(new UsageWindowInfo((int)(Number(window, "windowDurationMins") ?? 0), percent, Timestamp(Number(window, "resetsAt"))));
        }
        var credits = Get(data, "credits");
        var balance = Get(credits, "balance");
        string? raw = balance.ValueKind == JsonValueKind.String ? balance.GetString() : balance.ValueKind == JsonValueKind.Number ? balance.GetRawText() : null;
        return new UsageBucketInfo(id, Text(data, "limitName") ?? (id == "codex" ? "Codex" : id), Text(data, "planType"), raw, Bool(credits, "hasCredits"), Bool(credits, "unlimited") == true, windows);
    }

    public static IReadOnlyList<CodexTaskInfo> ParseTasks(JsonElement data)
    {
        RequireObject(data);
        if (Get(data, "threads").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Codex 任务接口格式发生变化。");
        var rows = new Dictionary<string, CodexTaskInfo>(StringComparer.Ordinal);
        foreach (string slot in new[] { "pinnedThreads", "threads" })
        {
            var array = Get(data, slot);
            if (array.ValueKind != JsonValueKind.Array) continue;
            foreach (var task in array.EnumerateArray())
            {
                // The ChatGPT read endpoint may download attachments. This adapter
                // reads the Codex backend only; ownership is classified separately.
                if (Text(task, "kind") != "codex") continue;
                string? id = Text(task, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var row = new CodexTaskInfo(id, Text(task, "title") ?? "未命名任务", Status(task), "codex", Text(task, "hostId"), Text(task, "projectName") ?? Text(task, "cwd"), Number(task, "updatedAt") ?? 0, slot == "pinnedThreads");
                var source = TaskSourcePolicy.Classify(task);
                row = row with { Source = source, DetectedSource = source };
                if (!rows.ContainsKey(row.Key)) rows[row.Key] = row;
            }
        }
        return rows.Values.OrderByDescending(t => t.IsRunning).ThenByDescending(t => t.NeedsAttention).ThenByDescending(t => t.Pinned).ThenByDescending(t => t.UpdatedAt).ToArray();
    }

    public static ConversationPage ParseConversation(JsonElement data, string taskKey)
    {
        RequireObject(data);
        var turns = Get(data, "turns");
        if (turns.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Codex 对话接口格式发生变化。");
        var page = Get(data, "page");
        var messages = new List<PublicMessage>();
        var entries = turns.EnumerateArray().ToArray();
        string? latest = entries.Length > 0 ? Text(entries[0], "status") : null;
        // Codex returns turns newest first, but items within each turn are chronological.
        foreach (var turn in entries.Reverse())
        {
            var items = Get(turn, "items");
            if (items.ValueKind != JsonValueKind.Array) continue;
            int index = 0;
            foreach (var item in items.EnumerateArray())
            {
                index++;
                string? type = Text(item, "type");
                if (type != "userMessage" && type != "agentMessage") continue;
                string body;
                if (type == "agentMessage") body = Text(item, "text") ?? "";
                else
                {
                    var content = Get(item, "content");
                    body = content.ValueKind != JsonValueKind.Array ? "" : string.Join("\n", content.EnumerateArray().Select(c => Text(c, "type") switch
                    {
                        "text" => Text(c, "text") ?? "",
                        "image" or "localImage" => "[图片附件]",
                        _ => ""
                    }).Where(s => s.Length > 0));
                }
                if (string.IsNullOrWhiteSpace(body)) continue;
                string id = (Text(turn, "id") ?? "turn") + ":" + (Text(item, "id") ?? index.ToString(CultureInfo.InvariantCulture));
                messages.Add(new PublicMessage(id, type == "userMessage" ? "user" : "assistant", body, Text(item, "phase")));
            }
        }
        string? cursor = Text(page, "nextCursor");
        return new ConversationPage(taskKey, messages, cursor, latest, Bool(page, "hasMore") == true && !string.IsNullOrEmpty(cursor));
    }

    public static CodexTaskInfo WithRuntimeStatus(CodexTaskInfo task, JsonElement history)
    {
        var thread = Get(history, "thread");
        string state = Status(thread);
        if (state == "unknown") return task;
        var flags = Get(Get(thread, "status"), "activeFlags");
        if (flags.ValueKind == JsonValueKind.Array)
        {
            var names = flags.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()).ToArray();
            if (names.Contains("waitingOnApproval")) state = "waitingForApproval";
            else if (names.Contains("waitingOnUserInput")) state = "waitingForInput";
        }
        return task with { Status = state };
    }
    internal static string Status(JsonElement data)
    {
        var state = Get(data, "status");
        return state.ValueKind == JsonValueKind.String ? state.GetString() ?? "unknown" : Text(state, "type") ?? "unknown";
    }
    internal static JsonElement Get(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) ? value : default;
    internal static string? Text(JsonElement data, string name) => Get(data, name).ValueKind == JsonValueKind.String ? Get(data, name).GetString() : null;
    internal static double? Number(JsonElement data, string name) => Get(data, name).ValueKind == JsonValueKind.Number && Get(data, name).TryGetDouble(out double value) && double.IsFinite(value) ? value : null;
    internal static bool? Bool(JsonElement data, string name) => Get(data, name).ValueKind is JsonValueKind.True ? true : Get(data, name).ValueKind is JsonValueKind.False ? false : null;
    private static void RequireObject(JsonElement data) { if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Codex 返回了无法识别的数据。"); }
    private static DateTimeOffset? Timestamp(double? seconds)
    {
        if (!seconds.HasValue) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds((long)seconds.Value); } catch (ArgumentOutOfRangeException) { return null; }
    }
}
