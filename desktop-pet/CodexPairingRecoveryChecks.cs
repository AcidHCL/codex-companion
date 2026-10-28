using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

// Isolated synthetic stores and a private STA dispatcher. No real account/pipes,
// settings changes, or user session/window actions are used by this regression.
internal static class CodexPairingRecoveryChecks
{
    internal static Task<object> RunAsync()
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { completion.SetResult(await RunCoreAsync()); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Pairing recovery regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private static async Task<object> RunCoreAsync()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Pairing recovery check failed: " + name);
        }
        string fixture = Path.Combine(AppContext.BaseDirectory, "pairing-recovery-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var store = new CodexConnectionStore(Path.Combine(fixture, "store"));
            var hint = new CodexConnectionHint(@"\\.\pipe\codex-browser-use-" + Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
            Check(store.SaveVerified(hint), "synthetic_private_store_created");
            byte[] before = SHA256.HashData(File.ReadAllBytes(store.ProtectedPath));
            using (var held = new FileStream(store.ProtectedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check(store.Load() == null && store.LastState == CodexPairingState.Unavailable,
                    "real_exclusive_file_lock_is_temporary_not_missing_or_revoked");
                Check(!File.Exists(store.RevocationPath), "temporary_lock_does_not_create_revocation_marker");
                using var guarded = new CodexConnection(store: store);
                Exception? failure = null;
                try { await guarded.EnsureConnectedAsync(); } catch (Exception ex) { failure = ex; }
                Check(failure is IOException && guarded.PairingState == CodexPairingState.Unavailable,
                    "locked_store_reports_retryable_connection_failure");
                Check(!guarded.IsPairingRevoked && guarded.CandidateAttempts == 0,
                    "locked_store_never_discovers_or_calls_a_pipe");
                Check(failure != null && !failure.Message.Contains("首次配对"),
                    "temporary_load_failure_never_requests_first_pairing");
            }
            Check(store.Load() == hint && store.LastState == CodexPairingState.Ready,
                "original_record_recovers_after_file_lock_is_released");
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(store.ProtectedPath))),
                "temporary_failure_preserves_exact_encrypted_record");

            // Exercise the actual mutex timeout from the old startup-gating defect.
            string mutexName = @"Local\AIPetConnectionStore-" + Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(Path.GetDirectoryName(store.ProtectedPath)!).ToUpperInvariant()))).Substring(0, 32);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var holder = new Thread(() =>
                {
                    using var mutex = new Mutex(false, mutexName);
                    mutex.WaitOne(); entered.Set();
                    try { release.Wait(); } finally { mutex.ReleaseMutex(); }
                }) { IsBackground = true };
                holder.Start();
                Check(entered.Wait(TimeSpan.FromSeconds(3)), "synthetic_store_mutex_is_held_by_another_thread");
                try
                {
                    var state = await Task.Run(store.CheckRevocation);
                    Check(state == CodexPairingState.Unavailable && !File.Exists(store.RevocationPath),
                        "mutex_timeout_is_unavailable_not_actual_revocation");
                }
                finally { release.Set(); holder.Join(3000); }
            }
            Check(store.CheckRevocation() == CodexPairingState.Ready && store.Load() == hint,
                "mutex_timeout_recovers_without_repairing_or_replacing_record");

            // Change only a synthetic record's age, keeping its protected file ACL
            // and valid DPAPI/record structure so expiry is distinct from damage.
            var expiredHint = hint with { SavedAtUtc = DateTimeOffset.UtcNow.AddDays(-91) };
            byte[] expiredBytes = System.Text.Encoding.ASCII.GetBytes("AIPETC02")
                .Concat(CodexConnectionStore.ProtectForCurrentUser(JsonSerializer.SerializeToUtf8Bytes(expiredHint))).ToArray();
            File.WriteAllBytes(store.ProtectedPath, expiredBytes);
            Check(store.Load() == null && store.LastState == CodexPairingState.Expired,
                "safe_expired_record_is_distinct_from_corruption_and_revocation");
            using (var expired = new CodexConnection(store: store))
            {
                bool denied = false;
                try { await expired.EnsureConnectedAsync(); } catch (InvalidOperationException) { denied = true; }
                Check(denied && expired.PairingState == CodexPairingState.Expired && expired.CandidateAttempts == 0,
                    "ordinary_connection_rejects_expired_record_before_pipe_discovery");
            }
            Check(store.SaveVerified(hint, allowReactivation: true) && store.Load() == hint && store.LastState == CodexPairingState.Ready,
                "explicit_verified_pairing_can_replace_safe_expired_record");

            File.WriteAllBytes(store.ProtectedPath, new byte[] { 1, 2, 3 });
            using (var invalid = new CodexConnection(store: store))
            {
                bool denied = false;
                try { await invalid.EnsureConnectedAsync(); } catch (UnauthorizedAccessException) { denied = true; }
                Check(denied && invalid.PairingState == CodexPairingState.Invalid && invalid.CandidateAttempts == 0,
                    "invalid_ciphertext_remains_blocked_before_pipe_discovery");
            }
            store.Forget();
            using (var revoked = new CodexConnection(store: store))
            {
                bool denied = false;
                try { await revoked.EnsureConnectedAsync(); } catch (InvalidOperationException) { denied = true; }
                Check(denied && revoked.PairingState == CodexPairingState.Revoked && revoked.CandidateAttempts == 0,
                    "real_revocation_remains_blocked_before_pipe_discovery");
                Check(!revoked.ResumeSavedPairing() && store.IsRevoked, "ordinary_resume_cannot_undo_real_revocation");
            }

            var settings = new DesktopSettings { CodexEnabled = true, CodexMessagesEnabled = true, RefreshSeconds = 5 };
            var transport = new FakeConnection { PairingState = CodexPairingState.Unavailable };
            using var monitor = new CodexMonitor(settings, transport, persistSettings: false);
            monitor.Start();
            Check(!monitor.Snapshot.Connected && monitor.AutomaticRetryScheduled && monitor.ConnectionRequested,
                "startup_storage_failure_keeps_automatic_timer_running");
            Check(transport.Calls == 0 && monitor.PairingState == CodexPairingState.Unavailable,
                "startup_storage_failure_never_attempts_tool_reads");
            Check(monitor.Snapshot.Error?.Contains("无需重新配对") == true,
                "startup_storage_failure_has_retry_guidance");
            transport.PairingState = CodexPairingState.Ready;
            var until = DateTimeOffset.UtcNow.AddSeconds(8);
            while (!monitor.Snapshot.Connected && DateTimeOffset.UtcNow < until) await Task.Delay(100);
            Check(monitor.Snapshot.Connected && transport.Calls == 2 && transport.Ensures == 2,
                "real_dispatcher_timer_reconnects_without_manual_refresh_or_repairing");
            Check(settings.CodexEnabled && settings.CodexMessagesEnabled && monitor.MessagesEnabled,
                "automatic_recovery_preserves_connection_and_message_preferences");
            await monitor.ForgetConnectionAsync();
            int calls = transport.Calls, ensures = transport.Ensures;
            transport.PairingState = CodexPairingState.Ready; // external new record still requires explicit resume
            await monitor.RefreshAsync(true);
            Check(!monitor.AutomaticRetryScheduled && !settings.CodexEnabled && !monitor.ConnectionRequested,
                "explicit_forget_stops_polling_and_persists_user_intent_in_memory");
            Check(transport.Calls == calls && transport.Ensures == ensures,
                "refresh_never_revives_a_connection_after_explicit_forget");

            return new { passed = true, assertions = checks.Count, checks, userSettingsWritten = false,
                realAccountRequests = 0, realPipeDiscoveryAttempts = 0, syntheticStoreOnly = true, actualTimerRecovery = true };
        }
        finally
        {
            // This exact unique directory was created above beneath the executable;
            // it contains only our synthetic fixtures and never any user records.
            Directory.Delete(fixture, recursive: true);
        }
    }

    private sealed class FakeConnection : ICodexConnection
    {
        public CodexPairingState PairingState { get; set; }
        public bool IsPairingRevoked => PairingState == CodexPairingState.Revoked;
        public bool HasSavedPairing => PairingState == CodexPairingState.Ready;
        public string ConnectionDescription => "Synthetic pairing recovery transport";
        public string? CallerThreadId => null;
        public int Ensures { get; private set; }
        public int Calls { get; private set; }
        public bool ResumeSavedPairing() => HasSavedPairing;
        public void SetMessageAccess(bool enabled) { }
        public Task EnsureConnectedAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Ensures++;
            if (PairingState != CodexPairingState.Ready) throw new IOException("Synthetic storage unavailable");
            return Task.CompletedTask;
        }
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            return tool switch
            {
                "list_threads" => Task.FromResult(JsonSerializer.SerializeToElement(new { threads = Array.Empty<object>() })),
                "get_usage_limits" => Task.FromResult(JsonSerializer.SerializeToElement(new { rateLimits = new { credits = new { balance = "123" } } })),
                _ => throw new InvalidOperationException("Unexpected synthetic request")
            };
        }
        public void Disconnect() { }
        public Task ForgetAsync() { PairingState = CodexPairingState.Revoked; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
