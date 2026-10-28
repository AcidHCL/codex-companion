using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// In-memory fake transport; no account calls or user settings writes.
internal static class CodexPrivacyChecks
{
    internal static async Task<object> RunAsync()
    {
        int assertions = 0;
        void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException("Privacy check: " + reason); }
        var settings = JsonSerializer.Deserialize<DesktopSettings>("{}")!;
        Check(!settings.CodexMessagesEnabled, "Existing settings without the field must default to no message access.");
        var fake = new FakeConnection();
        using var monitor = new CodexMonitor(settings, fake, persistSettings: false);
        int notices = 0;
        monitor.VerifiedTransition += _ => notices++;
        await monitor.RefreshAsync(true);
        Check(monitor.Snapshot.Connected && monitor.Snapshot.Tasks.Count == 1, "Status listing still works.");
        Check(fake.Reads == 0, "Active tasks must not cause hidden read_thread calls.");
        var task = monitor.Snapshot.Tasks.Single();
        await monitor.SelectTaskAsync(task);
        Check(fake.Reads == 0 && monitor.Conversation == null, "Selecting without consent cannot read messages.");
        fake.Running = false;
        await monitor.RefreshAsync();
        Check(fake.Reads == 0 && notices == 0, "An idle transition cannot read details or invent a success notice.");
        monitor.SetMessageAccess(true);
        await monitor.SelectTaskAsync(task);
        Check(fake.Reads == 1 && monitor.Conversation?.Messages.Count == 1, "Explicit opt-in permits displaying a message.");
        monitor.SetMessageAccess(false);
        Check(monitor.Conversation == null && monitor.SelectedTask == null && !monitor.MessagesEnabled, "Opt-out immediately clears visible messages.");

        monitor.SetMessageAccess(true);
        fake.PendingRead = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pendingRead = monitor.SelectTaskAsync(task);
        Check(!pendingRead.IsCompleted, "A real asynchronous read is in flight.");
        monitor.SetMessageAccess(false);
        fake.PendingRead.SetResult(FakeConnection.History());
        await pendingRead;
        fake.PendingRead = null;
        Check(monitor.Conversation == null && !monitor.IsReading, "A late response after opt-out cannot restore messages.");

        fake.PendingList = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pendingRefresh = monitor.RefreshAsync(true);
        Check(!pendingRefresh.IsCompleted, "A refresh is in flight before revocation.");
        await monitor.ForgetConnectionAsync();
        fake.PendingList.SetResult(FakeConnection.Tasks(false));
        await pendingRefresh;
        fake.PendingList = null;
        Check(!monitor.ConnectionEnabled && !monitor.Snapshot.Connected && monitor.Snapshot.Tasks.Count == 0 && monitor.Snapshot.Buckets.Count == 0, "Forget clears data and rejects late refresh results.");
        int before = fake.Calls;
        await monitor.RefreshAsync(true);
        Check(fake.Calls == before, "F5/refresh cannot reconnect after forget.");
        bool refused = false;
        try { await monitor.ResumeConnectionAsync(); } catch (InvalidOperationException) { refused = true; }
        Check(refused && !settings.CodexEnabled, "Resume requires a newly saved pairing.");
        fake.IsPairingRevoked = false;
        await monitor.ResumeConnectionAsync();
        Check(monitor.ConnectionEnabled && monitor.Snapshot.Connected && !monitor.MessagesEnabled, "Explicit resume after external pairing stays message-private.");

        using var guarded = new CodexConnection(store: new CodexConnectionStore(Path.Combine(Path.GetTempPath(), "aipet-no-rpc-" + Guid.NewGuid().ToString("N"))));
        foreach (string tool in new[] { "read_thread", "send_message_to_thread" })
        {
            bool blocked = false;
            try { await guarded.CallAsync(tool, new { }); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "The transport must reject unauthorized message/write calls before any pipe discovery.");
        }
        Check(!Program.DiagnosticError(new IOException("PRIVATE_FIXTURE_CONTENT")).Contains("PRIVATE_FIXTURE_CONTENT"), "Diagnostic output cannot include raw error content.");
        return new { passed = true, assertions, checks = new[] { "default_no_message_reads", "explicit_opt_in", "late_read_discarded", "late_refresh_discarded", "revocation_blocks_refresh", "re_pair_requires_explicit_resume", "transport_read_write_guards", "redacted_diagnostics" } };
    }

    private sealed class FakeConnection : ICodexConnection
    {
        public string ConnectionDescription => "fixture";
        public string? CallerThreadId => "fixture-task";
        public bool IsPairingRevoked { get; set; }
        public bool HasSavedPairing => !IsPairingRevoked;
        public bool Running { get; set; } = true;
        public int Reads { get; private set; }
        public int Calls { get; private set; }
        private bool messageAccess;
        public TaskCompletionSource<JsonElement>? PendingRead { get; set; }
        public TaskCompletionSource<JsonElement>? PendingList { get; set; }
        public void SetMessageAccess(bool enabled) => messageAccess = enabled;
        public Task EnsureConnectedAsync(CancellationToken token = default) { Calls++; token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<JsonElement> CallAsync(string tool, object args, CancellationToken token = default)
        {
            Calls++;
            if (tool == "list_threads") return PendingList?.Task ?? Task.FromResult(Tasks(Running));
            if (tool == "get_usage_limits") return Task.FromResult(Parse("{\"rateLimits\":{\"credits\":{\"balance\":\"123\"}}}"));
            if (tool != "read_thread" || !messageAccess) throw new InvalidOperationException("Denied fixture request.");
            Reads++; return PendingRead?.Task ?? Task.FromResult(History());
        }
        public static JsonElement Tasks(bool running) => JsonSerializer.SerializeToElement(new { threads = new[] { new { id = "fixture-task", title = "Synthetic task", kind = "codex", status = running ? "running" : "idle", updatedAt = 1 } } });
        public static JsonElement History() => Parse("{\"turns\":[{\"id\":\"fixture-turn\",\"status\":\"completed\",\"items\":[{\"id\":\"message\",\"type\":\"agentMessage\",\"text\":\"Synthetic private message\"}]}],\"page\":{\"hasMore\":false}}");
        private static JsonElement Parse(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        public bool ResumeSavedPairing() => !IsPairingRevoked;
        public void Disconnect() { }
        public Task ForgetAsync() { IsPairingRevoked = true; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
