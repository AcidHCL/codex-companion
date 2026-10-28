using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopPet;

// Synthetic fixtures only: no pipe, network, account data, or conversation files.
internal static class CodexParserChecks
{
    public static object Run()
    {
        int assertions = 0;
        var groups = new List<string>();
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Codex parser check: " + message);
        }
        void Reject(string json, Action<JsonElement> parse, string message)
        {
            bool rejected = false;
            using var document = JsonDocument.Parse(json);
            try { parse(document.RootElement); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }

        var usage = Parse("""
            {
              "accountId":"fixture-account",
              "rateLimits":{"limitId":"legacy","credits":{"balance":"999"}},
              "rateLimitsByLimitId":{
                "codex":{
                  "limitName":"Codex","planType":"fixture",
                  "credits":{"hasCredits":true,"unlimited":false,"balance":"62500"},
                  "primary":{"windowDurationMins":300,"usedPercent":12.5,"resetsAt":1700000000},
                  "secondary":{"windowDurationMins":10080,"usedPercent":67,"resetsAt":1700100000}
                },
                "model-extra":{"credits":{"balance":12.25},"primary":null,"secondary":null},
                "future-null":null,
                "future-scalar":"ignore"
              },
              "rateLimitResetCredits":{"availableCount":2},
              "futureField":{"ignored":true}
            }
            """, CodexParser.ParseUsage);
        Check(usage.Buckets.Count == 2, "A valid bucket map must supersede legacy data and ignore non-object entries.");
        var core = usage.Buckets.Single(b => b.Id == "codex");
        Check(core.CreditBalance == "62500" && core.HasCredits == true && !core.Unlimited, "Credit raw units or flags changed.");
        Check(usage.Buckets.Single(b => b.Id == "model-extra").CreditBalance == "12.25", "Numeric credit balances must retain their decimal value.");
        Check(core.Windows.Count == 2 && core.Windows[0].RemainingPercent == 87.5 && core.Windows[1].RemainingPercent == 33, "Used percentage was mistaken for remaining percentage.");
        Check(core.Windows[0].PeriodLabel == "5 小时" && core.Windows[1].PeriodLabel == "每周", "Usage periods must be identified by duration.");
        Check(core.Windows[0].ResetsAt?.ToUnixTimeSeconds() == 1700000000, "Reset time must use Unix seconds.");
        Check(usage.ResetCredits == 2 && usage.AccountId == "fixture-account", "Optional usage metadata was lost.");
        groups.Add("multi_bucket_usage_and_raw_credits");

        var sparse = Parse("""
            {
              "rateLimitsByLimitId":{
                "nulls":{"credits":null,"primary":null,"secondary":null},
                "unknown":{"credits":{"balance":"not-a-number","hasCredits":"true","unlimited":true},
                  "primary":{"windowDurationMins":42,"usedPercent":null,"resetsAt":null},
                  "secondary":{"windowDurationMins":"300","usedPercent":"12","resetsAt":1e300}},
                "clamped":{"primary":{"windowDurationMins":300,"usedPercent":-5},
                  "secondary":{"windowDurationMins":10080,"usedPercent":150}}
              },
              "rateLimitResetCredits":{"availableCount":null}
            }
            """, CodexParser.ParseUsage);
        var nulls = sparse.Buckets.Single(b => b.Id == "nulls");
        Check(nulls.CreditBalance == null && nulls.HasCredits == null && nulls.Windows.Count == 0, "Missing values must remain unavailable, not become zero.");
        var unknown = sparse.Buckets.Single(b => b.Id == "unknown");
        Check(unknown.CreditBalance == "not-a-number" && unknown.HasCredits == null && unknown.Unlimited, "Parser must preserve raw balance text while handling strict boolean types.");
        Check(unknown.Windows[0].RemainingPercent == null && unknown.Windows[0].ResetsAt == null, "An unknown window must not imply unused quota or a reset date.");
        Check(unknown.Windows[0].PeriodLabel == "42 分钟" && unknown.Windows[1].PeriodLabel == "未知周期", "Unknown period labels are unsafe.");
        Check(unknown.Windows[1].RemainingPercent == null && unknown.Windows[1].ResetsAt == null, "Wrong numeric types or out-of-range dates must remain unavailable.");
        var clamped = sparse.Buckets.Single(b => b.Id == "clamped");
        Check(clamped.Windows[0].RemainingPercent == 100 && clamped.Windows[1].RemainingPercent == 0, "Out-of-range percentage values must be clamped.");
        Check(sparse.ResetCredits == null, "Missing reset credits must not become zero.");
        var legacy = Parse("""{"rateLimitsByLimitId":{},"rateLimits":{"limitId":"legacy","credits":{"balance":"-1.5"},"primary":null}}""", CodexParser.ParseUsage);
        Check(legacy.Buckets.Count == 1 && legacy.Buckets[0].Id == "legacy" && legacy.Buckets[0].CreditBalance == "-1.5", "An empty map must fall back to legacy data without changing raw credit values.");
        Check(Parse("""{"rateLimits":null}""", CodexParser.ParseUsage).Buckets.Count == 0, "Explicit unavailable usage should be an empty result.");
        Reject("{}", e => { CodexParser.ParseUsage(e); }, "An unrecognized usage envelope must fail visibly.");
        Reject("[]", e => { CodexParser.ParseUsage(e); }, "A non-object usage envelope must fail visibly.");
        groups.Add("unknown_null_legacy_and_changed_usage_shapes");

        var tasks = Parse("""
            {
              "pinnedThreads":[
                {"id":"same","kind":"codex","title":"Pinned original title","status":"idle","updatedAt":10},
                {"id":"chat","kind":"chatgpt","title":"Excluded chat","status":"active"}
              ],
              "threads":[
                {"id":"same","kind":"codex","hostId":"local","title":"Duplicate later title","status":"idle","updatedAt":999},
                {"id":"same","kind":"codex","hostId":"remote-host","title":"Remote same ID","status":"idle","updatedAt":20},
                {"id":"run","kind":"codex","title":"Running original title","status":{"type":"active","activeFlags":[]},"updatedAt":1},
                {"id":"attention","kind":"codex","title":"Attention","status":"systemError","updatedAt":2},
                {"id":"idle-new","kind":"codex","title":"Recent idle","status":"idle","updatedAt":1000},
                {"id":"idle-old","kind":"codex","status":{"type":"futureState"},"updatedAt":0},
                {"id":"chat","kind":"chatgpt","title":"Excluded chat","status":"active"},
                {"id":"future","kind":"future","title":"Excluded future kind","status":"active"},
                {"kind":"codex","title":"Missing ID"},
                null
              ]
            }
            """, CodexParser.ParseTasks);
        Check(tasks.Count == 6 && tasks.All(t => t.Kind == "codex"), "Only Codex tasks with usable IDs may be listed.");
        Check(tasks.Select(t => t.Key).Distinct().Count() == tasks.Count, "Tasks must be deduplicated using kind, host, and ID.");
        var pinned = tasks.Single(t => t.Key == "codex:local:same");
        Check(pinned.Pinned && pinned.Title == "Pinned original title", "The first pinned entry and its source title must win deduplication.");
        Check(tasks.Count(t => t.Id == "same") == 2, "Identical task IDs on different hosts must remain distinct.");
        Check(tasks.Select(t => t.Id).SequenceEqual(new[] { "run", "attention", "same", "idle-new", "same", "idle-old" }), "Task priority must be running, attention, pinned, then recency.");
        Check(tasks[0].IsRunning && tasks[1].NeedsAttention, "Both object and string task statuses must be understood.");
        Check(tasks[^1].Title == "未命名任务" && tasks[^1].StatusLabel == "状态未知", "Missing titles and future statuses need safe fallbacks.");
        Reject("""{"pinnedThreads":[]}""", e => { CodexParser.ParseTasks(e); }, "A missing task-list array must be reported as an incompatible response.");
        groups.Add("codex_only_task_identity_deduplication_and_priority");


        var approval = Parse("""{"thread":{"status":{"type":"active","activeFlags":["waitingOnApproval"]}}}""", e => CodexParser.WithRuntimeStatus(tasks[0], e));
        Check(approval.Status == "waitingForApproval" && approval.NeedsAttention && !approval.IsRunning && approval.Key == tasks[0].Key, "Runtime approval flags must become an attention state without changing task identity.");
        var input = Parse("""{"thread":{"status":{"type":"active","activeFlags":["waitingOnUserInput"]}}}""", e => CodexParser.WithRuntimeStatus(tasks[0], e));
        Check(input.Status == "waitingForInput" && input.NeedsAttention && !input.IsRunning, "Runtime user-input flags must become an attention state.");
        var resumed = Parse("""{"thread":{"status":{"type":"active","activeFlags":[]}}}""", e => CodexParser.WithRuntimeStatus(approval, e));
        Check(resumed.Status == "active" && resumed.IsRunning && !resumed.NeedsAttention, "Ordinary active state must clear a previous waiting state.");
        groups.Add("runtime_approval_user_input_and_resumed_execution");

        const string cursor = "{\"turnId\":\"older:turn\",\"includeAnchor\":false}";
        var history = Parse("""
            {
              "schemaVersion":1,
              "thread":{"id":"task","kind":"codex"},
              "page":{"order":"newest_first","limit":3,"nextCursor":"{\"turnId\":\"older:turn\",\"includeAnchor\":false}","hasMore":true},
              "turns":[
                {"id":"latest","status":"inProgress"},
                {"id":"new","status":"completed","items":[
                  {"type":"userMessage","id":"u2","content":[{"type":"text","text":"new user"}]},
                  {"type":"reasoning","id":"r","summary":["PRIVATE_REASONING"],"content":["PRIVATE_CONTENT"]},
                  {"type":"plan","id":"p","text":"PRIVATE_PLAN"},
                  {"type":"commandExecution","id":"c","command":"PRIVATE_COMMAND","output":{"text":"PRIVATE_OUTPUT"}},
                  {"type":"fileChange","id":"f","changes":[{"path":"PRIVATE_PATH","diff":"PRIVATE_DIFF"}]},
                  {"type":"mcpToolCall","id":"m","arguments":{"key":"PRIVATE_ARGUMENT"}},
                  {"type":"dynamicToolCall","id":"d","arguments":"PRIVATE_DYNAMIC"},
                  {"type":"functionCallOutput","id":"o","output":{"text":"PRIVATE_FUNCTION_OUTPUT"}},
                  {"type":"collabAgentToolCall","id":"co","prompt":"PRIVATE_DELEGATION"},
                  {"type":"webSearch","id":"w","query":"PRIVATE_QUERY"},
                  {"type":"future","id":"future","text":"PRIVATE_UNKNOWN"},
                  {"type":"agentMessage","id":"a2","text":"new final","phase":"final_answer"},
                  {"type":"agentMessage","id":"empty","text":"   "},
                  {"type":"userMessage","id":"bad-content","content":"PRIVATE_INVALID"},
                  null
                ]},
                {"id":"old","status":"completed","items":[
                  {"type":"userMessage","id":"u1","content":[
                    {"type":"text","text":"old user"},
                    {"type":"image","url":"PRIVATE_REMOTE_URL"},
                    {"type":"localImage","path":"PRIVATE_LOCAL_PATH"},
                    {"type":"skill","name":"PRIVATE_SKILL","path":"PRIVATE_SKILL_PATH"},
                    {"type":"text","text":"second line"}]},
                  {"type":"agentMessage","id":"a1","text":"old reply","phase":null}
                ]}
              ]
            }
            """, e => CodexParser.ParseConversation(e, "codex:local:task"));
        Check(history.TaskKey == "codex:local:task" && history.LatestTurnStatus == "inProgress", "Task identity and latest status must survive empty latest items.");
        Check(history.Messages.Count == 4, "Only nonempty public user and assistant messages may pass the allowlist.");
        Check(history.Messages.Select(m => m.Id).SequenceEqual(new[] { "old:u1", "old:a1", "new:u2", "new:a2" }), "Newest-first turns must display chronologically without reversing items within a turn.");
        Check(history.Messages.Select(m => m.Role).SequenceEqual(new[] { "user", "assistant", "user", "assistant" }), "Public message roles are incorrect.");
        Check(history.Messages[0].Text == "old user\n[图片附件]\n[图片附件]\nsecond line", "Public image placeholders must not expose attachment URLs or local paths.");
        Check(history.Messages.All(m => !m.Text.Contains("PRIVATE_", StringComparison.Ordinal)), "Private reasoning, tool content, or attachment locations leaked into public text.");
        Check(history.Messages[1].Phase == null && history.Messages[3].Phase == "final_answer", "Nullable assistant phase must be preserved.");
        Check(history.NextCursor == cursor && history.HasMore, "Opaque pagination cursors must be preserved verbatim.");
        groups.Add("public_message_allowlist_order_media_privacy_and_cursor");

        var empty = Parse("""{"turns":[],"page":{"hasMore":true,"nextCursor":null}}""", e => CodexParser.ParseConversation(e, "fixture"));
        Check(empty.Messages.Count == 0 && empty.LatestTurnStatus == null && !empty.HasMore, "An empty page or missing cursor must not enable more-history loading.");
        var terminal = Parse("""{"turns":[{"id":"t","status":"completed","items":[{"type":"agentMessage","text":"one"}]}],"page":{"nextCursor":"opaque","hasMore":false}}""", e => CodexParser.ParseConversation(e, "fixture"));
        Check(terminal.Messages.Count == 1 && terminal.Messages[0].Id.Length > 0 && !terminal.HasMore, "Missing item IDs need a stable fallback; a terminal page must remain terminal.");
        Reject("""{"page":{"hasMore":false}}""", e => { CodexParser.ParseConversation(e, "fixture"); }, "Missing turns must raise a compatibility error.");
        Reject("""{"turns":{}}""", e => { CodexParser.ParseConversation(e, "fixture"); }, "Invalid turns must raise a compatibility error.");
        Reject("null", e => { CodexParser.ParseConversation(e, "fixture"); }, "Null conversations must fail visibly.");
        groups.Add("empty_pages_and_explicit_compatibility_failures");


        ConversationPage HistoryPage(string? nextCursor, params string[] ids) => new(
            "codex:local:history-fixture",
            ids.Select(id => new PublicMessage(id + ":a", "assistant", id, null)).ToArray(),
            nextCursor, "inProgress", nextCursor != null);
        var cachedHistory = HistoryPage("before-t1", "t1", "t2", "t3");
        var freshHistory = HistoryPage("before-t5", "t5", "t6", "t7");
        var resetHistory = ConversationHistory.MergeLatest(cachedHistory, freshHistory, true);
        Check(resetHistory.Page.Messages.Select(m => m.Text).SequenceEqual(new[] { "t5", "t6", "t7" }) && !resetHistory.OlderLoaded,
            "A nonoverlapping latest page must drop the disconnected old chain and reset loaded-history state.");
        Check(resetHistory.Page.NextCursor == "before-t5" && resetHistory.Page.HasMore,
            "A reset chain must use the latest page cursor so the missing turns remain recoverable.");
        var recoveredHistory = ConversationHistory.MergeOlder(resetHistory.Page, HistoryPage("before-t2", "t2", "t3", "t4"), "before-t5");
        Check(recoveredHistory.Messages.Select(m => m.Text).SequenceEqual(new[] { "t2", "t3", "t4", "t5", "t6", "t7" }),
            "Loading history after a latest-page gap must produce chronological turns, not t4 before t1.");
        var oldestHistory = ConversationHistory.MergeOlder(recoveredHistory, HistoryPage(null, "t1", "t2"), "before-t2");
        Check(oldestHistory.Messages.Select(m => m.Text).SequenceEqual(new[] { "t1", "t2", "t3", "t4", "t5", "t6", "t7" }) && !oldestHistory.HasMore,
            "An overlapping older page must deduplicate and finish the contiguous chronological history.");

        var growingLatest = HistoryPage("before-t2", "t2", "t3", "t4") with
        {
            Messages = new[]
            {
                new PublicMessage("t2:a", "assistant", "t2", null),
                new PublicMessage("t3:a", "assistant", "t3 has grown while streaming", "commentary"),
                new PublicMessage("t4:a", "assistant", "t4", "final_answer")
            },
            LatestTurnStatus = "completed"
        };
        var grownHistory = ConversationHistory.MergeLatest(cachedHistory, growingLatest, false);
        Check(grownHistory.Page.Messages.Select(m => m.Id).SequenceEqual(new[] { "t1:a", "t2:a", "t3:a", "t4:a" }) &&
            grownHistory.Page.Messages.Single(m => m.Id == "t3:a").Text == "t3 has grown while streaming",
            "A growing message with the same ID must replace cached text once, preserving chronological order.");
        Check(grownHistory.Page.LatestTurnStatus == "completed" && grownHistory.Page.NextCursor == "before-t2" && !grownHistory.OlderLoaded,
            "A latest refresh must retain fresh terminal status and pagination when older history was not loaded.");

        var loadedHistory = ConversationHistory.MergeLatest(cachedHistory, growingLatest, true);
        Check(loadedHistory.OlderLoaded && loadedHistory.Page.NextCursor == "before-t1" && loadedHistory.Page.HasMore,
            "Refreshing overlapping messages must preserve the earliest loaded-history cursor.");
        var exhaustedHistory = ConversationHistory.MergeLatest(cachedHistory with { NextCursor = null, HasMore = false }, growingLatest, true);
        Check(exhaustedHistory.OlderLoaded && exhaustedHistory.Page.NextCursor == null && !exhaustedHistory.Page.HasMore,
            "An exhausted old-history chain must not reopen because the latest limited page has more.");
        var repeatedCursor = ConversationHistory.MergeOlder(resetHistory.Page, HistoryPage("before-t5", "t4"), "before-t5");
        Check(!repeatedCursor.HasMore, "A repeated older-page cursor must stop pagination.");
        var otherTask = growingLatest with { TaskKey = "codex:local:other-task" };
        var switchedHistory = ConversationHistory.MergeLatest(cachedHistory, otherTask, true);
        Check(switchedHistory.Page.TaskKey == otherTask.TaskKey && switchedHistory.Page.Messages.Count == otherTask.Messages.Count && !switchedHistory.OlderLoaded,
            "Coincident message IDs from a different task must not preserve the previous task's history.");
        groups.Add("conversation_history_gap_streaming_and_pagination_regressions");

        return new { passed = true, offline = true, fixtureGroups = groups, assertionCount = assertions };
    }

    private static T Parse<T>(string json, Func<JsonElement, T> parser)
    {
        using var document = JsonDocument.Parse(json);
        return parser(document.RootElement);
    }
}
