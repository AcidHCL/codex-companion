using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Synthetic transport and UI callbacks only: no pipes, account data, or settings writes.
internal static class CodexMonitorRecoveryChecks
{
    internal static async Task<object> RunAsync()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Monitor recovery check failed: " + name);
        }
        static DesktopSettings Settings() => new() { CodexEnabled = true, CodexMessagesEnabled = false, RefreshSeconds = 5 };
        static COMException RenderFailure() => new("Synthetic render failure", unchecked((int)0x88980406));

        // A failure before the first await used to bypass the refresh gate's finally.
        var startTransport = new FakeConnection();
        using (var monitor = new CodexMonitor(Settings(), startTransport, persistSettings: false))
        {
            bool throwOnce = true;
            int observed = 0;
            monitor.Changed += () => { if (throwOnce) { throwOnce = false; throw RenderFailure(); } };
            monitor.Changed += () => observed++;
            Exception? failure = null;
            try { await monitor.RefreshAsync(true); } catch (Exception ex) { failure = ex; }
            Check(failure == null, "initial_subscriber_failure_does_not_escape_refresh");
            Check(monitor.Snapshot.Connected && !monitor.Snapshot.Refreshing, "initial_subscriber_failure_does_not_block_connection");
            Check(observed >= 2, "initial_subscriber_failure_does_not_skip_other_subscribers");
            int firstLists = startTransport.ListCalls;
            await monitor.RefreshAsync(true);
            Check(startTransport.ListCalls == firstLists + 1, "initial_subscriber_failure_releases_gate_for_next_refresh");
            Check(startTransport.DisconnectCalls == 0, "initial_subscriber_failure_does_not_disconnect_transport");
            Check(startTransport.MessageCalls == 0 && !monitor.MessagesEnabled, "initial_failure_recovery_keeps_messages_disabled");
        }

        // Throw only after a successful network result; this used to be reported as disconnection.
        var completedTransport = new FakeConnection();
        using (var monitor = new CodexMonitor(Settings(), completedTransport, persistSettings: false))
        {
            int successfulObservers = 0;
            monitor.Changed += () => { if (monitor.Snapshot.Connected && !monitor.Snapshot.Refreshing) throw RenderFailure(); };
            monitor.Changed += () => { if (monitor.Snapshot.Connected && !monitor.Snapshot.Refreshing) successfulObservers++; };
            await monitor.RefreshAsync(true);
            Check(monitor.Snapshot.Connected && !monitor.Snapshot.Refreshing, "completed_subscriber_failure_preserves_successful_snapshot");
            Check(completedTransport.DisconnectCalls == 0, "completed_subscriber_failure_is_not_a_connection_failure");
            Check(successfulObservers == 1, "completed_subscriber_failure_does_not_skip_other_subscribers");
            await monitor.RefreshAsync(true);
            Check(completedTransport.ListCalls == 2 && successfulObservers == 2, "repeated_completed_subscriber_failure_does_not_lock_gate");
            Check(completedTransport.MessageCalls == 0 && !monitor.MessagesEnabled, "completed_failure_recovery_keeps_messages_disabled");
        }

        // An actual temporary connection failure should disconnect once, then recover normally.
        var reconnectTransport = new FakeConnection { FailedConnectsRemaining = 1 };
        using (var monitor = new CodexMonitor(Settings(), reconnectTransport, persistSettings: false))
        {
            await monitor.RefreshAsync(true);
            Check(!monitor.Snapshot.Connected && !monitor.Snapshot.Refreshing, "real_connection_failure_clears_refreshing");
            Check(reconnectTransport.DisconnectCalls == 1 && reconnectTransport.ListCalls == 0, "real_connection_failure_discards_only_failed_session");
            await monitor.RefreshAsync(true);
            Check(monitor.Snapshot.Connected && monitor.Snapshot.Tasks.Count == 1, "next_refresh_recovers_after_codex_becomes_available");
            Check(reconnectTransport.EnsureCalls == 2 && reconnectTransport.ListCalls == 1, "recovery_retries_connection_and_retrieves_tasks");
            Check(reconnectTransport.UsageCalls == 1 && reconnectTransport.MessageCalls == 0 && !monitor.MessagesEnabled, "recovery_retrieves_usage_without_message_reads");
        }

        return new
        {
            passed = true,
            assertions = checks.Count,
            checks,
            syntheticOnly = true,
            userSettingsWritten = false,
            rpcRequestsSent = 0,
            messageReads = startTransport.MessageCalls + completedTransport.MessageCalls + reconnectTransport.MessageCalls,
            transports = new[] { startTransport.Counts("initial_notification"), completedTransport.Counts("completed_notification"), reconnectTransport.Counts("reconnect") }
        };
    }

    private sealed class FakeConnection : ICodexConnection
    {
        public string ConnectionDescription => "Synthetic recovery transport";
        public string? CallerThreadId => "synthetic-recovery-task";
        public bool IsPairingRevoked => false;
        public bool HasSavedPairing => true;
        public int FailedConnectsRemaining { get; set; }
        public int EnsureCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int UsageCalls { get; private set; }
        public int MessageCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public void SetMessageAccess(bool enabled) { }
        public bool ResumeSavedPairing() => true;
        public Task EnsureConnectedAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            EnsureCalls++;
            if (FailedConnectsRemaining > 0)
            {
                FailedConnectsRemaining--;
                throw new IOException("Synthetic unavailable transport");
            }
            return Task.CompletedTask;
        }
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (tool == "list_threads")
            {
                ListCalls++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { threads = new[] { new { id = "synthetic-recovery-task", title = "Synthetic recovery task", kind = "codex", status = "running", updatedAt = 1 } } }));
            }
            if (tool == "get_usage_limits")
            {
                UsageCalls++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { rateLimits = new { credits = new { balance = "123" } } }));
            }
            if (tool == "read_thread") MessageCalls++;
            throw new InvalidOperationException("Unexpected synthetic tool request");
        }
        public void Disconnect() => DisconnectCalls++;
        public Task ForgetAsync() => Task.CompletedTask;
        public void Dispose() { }
        public object Counts(string name) => new { name, EnsureCalls, ListCalls, UsageCalls, MessageCalls, DisconnectCalls };
    }
}
