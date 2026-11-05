using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Synthetic fixtures only: no automation files, account RPCs, models, or settings writes.
internal static class CodexScheduledTaskChecks
{
    internal static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException("Scheduled task check failed: " + name);
            checks.Add(name);
        }
        var metadata = CodexScheduledTasks.ParseMetadata("version = 1\nid = \"sample\\u002djob\" # comment\nkind='heartbeat'\nprompt = '''\nkind = \"wrong\"\n<html>private text</html>\n'''\ntarget_thread_id = \"thread-a\"\n");
        Check(metadata == new CodexScheduledTask("sample-job", "thread-a"), "literal_multiline_prompt_cannot_spoof_metadata");
        metadata = CodexScheduledTasks.ParseMetadata("id=\"sample-job\"\nkind=\"heartbeat\"\nprompt=\"\"\"a \\\"quote\\\" and \\\n   wrapped line\"\"\"\ntarget_thread_id='thread-a'\n");
        Check(metadata?.ThreadId == "thread-a", "basic_multiline_escapes_and_continuation_are_skipped_correctly");
        Check(CodexScheduledTasks.ParseMetadata("id='sample-job'\nkind='heartbeat'\nkind='other'\ntarget_thread_id='thread-a'") == null, "duplicate_metadata_is_rejected");
        Check(CodexScheduledTasks.ParseMetadata("id='sample-job'\nkind='cron'\ntarget_thread_id='thread-a'") == null, "non_heartbeat_automation_is_not_bound");
        Check(CodexScheduledTasks.ParseMetadata("id='sample-job'\nkind='heartbeat'\n[section]\ntarget_thread_id='thread-a'") == null, "table_fields_do_not_become_root_metadata");

        var schedules = new[] { new CodexScheduledTask("sample-job", "thread-a") };
        var fake = new FakeConnection();
        var settings = Settings();
        using (var monitor = new CodexMonitor(settings, fake, false, () => schedules))
        {
            var notices = new List<PetNotice>(); var transitions = new List<PetNotice>();
            monitor.Notice += notices.Add; monitor.VerifiedTransition += transitions.Add;
            await monitor.RefreshAsync(true);
            Check(notices.Count == 0 && fake.Reads == 1, "startup_baseline_does_not_replay_existing_completed_run");
            fake.Complete("scheduled-2", "第二次检查：结果正常。");
            await monitor.RefreshAsync();
            Check(notices.Count == 1 && notices[0].IsScheduled && notices[0].Kind == "complete", "fast_scheduled_completion_between_idle_polls_is_detected");
            Check(notices[0].ScheduledOutput == "第二次检查：结果正常。" && notices[0].TurnId == "scheduled-2" && notices[0].TaskKey == "codex:local:thread-a", "only_current_final_answer_is_exposed_with_task_and_turn_identity");
            fake.UpdatedAt++;
            await monitor.RefreshAsync();
            fake.Status = "running"; await monitor.RefreshAsync();
            fake.Status = "idle"; await monitor.RefreshAsync();
            Check(notices.Count == 1, "same_completed_turn_deduplicates_timestamp_changes_and_status_flaps");

            fake.Begin("manual-3", "请手动检查一下。"); await monitor.RefreshAsync();
            fake.Complete("manual-3", "手动检查已完成。", scheduled: false); await monitor.RefreshAsync();
            Check(notices.Count == 2 && !notices[1].IsScheduled && notices[1].ScheduledOutput == null, "manual_turn_in_bound_chat_keeps_ordinary_completion");
            fake.Begin("scheduled-4"); await monitor.RefreshAsync();
            fake.Complete("scheduled-4", "按计划完成。"); await monitor.RefreshAsync();
            Check(notices.Count == 3 && notices[2].IsScheduled, "observed_running_scheduled_turn_emits_special_completion");
            Check(transitions.Count == notices.Count, "scheduled_notice_preserves_verified_scene_transition");

            settings.NotificationsEnabled = false;
            fake.Complete("scheduled-5", "只允许场景。"); await monitor.RefreshAsync();
            Check(notices.Count == 3 && transitions.Count == 4, "notifications_disabled_keeps_verified_scene_without_bubble_notice");
            monitor.SetMessageAccess(false);
            int reads = fake.Reads;
            fake.Complete("scheduled-6", "不允许读取。"); await monitor.RefreshAsync();
            Check(fake.Reads == reads && transitions.Count == 4, "message_access_off_blocks_schedule_reads_and_notices");
            settings.NotificationsEnabled = true;
            monitor.SetMessageAccess(true); await monitor.RefreshAsync();
            Check(notices.Count == 3 && transitions.Count == 4, "reopening_message_access_baselines_without_backlog");
            fake.Complete("scheduled-7", "恢复后新结果。"); await monitor.RefreshAsync();
            Check(notices.Count == 4, "new_run_after_access_reopens_is_announced");

            monitor.SetTaskSourceOverride(monitor.Snapshot.Tasks.Single(), TaskSource.Dot);
            reads = fake.Reads;
            fake.Complete("scheduled-8", "Dot 不应显示。"); await monitor.RefreshAsync();
            Check(fake.Reads == reads && notices.Count == 4, "manual_dot_classification_blocks_automatic_schedule_reads");
            monitor.SetTaskSourceOverride(monitor.Snapshot.Tasks.Single(), TaskSource.Codex);
            await monitor.RefreshAsync();
            Check(notices.Count == 4, "source_reclassification_baselines_without_replaying_dot_result");

            fake.FailConnection = true; await monitor.RefreshAsync();
            fake.FailConnection = false; fake.Complete("scheduled-9", "断线期间结果。"); await monitor.RefreshAsync();
            Check(notices.Count == 4 && monitor.Snapshot.Connected, "reconnect_baselines_without_replaying_offline_results");
            fake.Complete("scheduled-10", "重连后结果。"); await monitor.RefreshAsync();
            Check(notices.Count == 5, "new_run_after_reconnect_is_announced");

            fake.Begin("scheduled-late"); await monitor.RefreshAsync();
            fake.Complete("scheduled-late", "晚到的结果。");
            fake.Pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task late = monitor.RefreshAsync();
            Check(!late.IsCompleted, "schedule_result_can_be_in_flight");
            monitor.SetTaskSourceOverride(monitor.Snapshot.Tasks.Single(), TaskSource.Dot);
            fake.Pending.SetResult(fake.History()); await late; fake.Pending = null;
            Check(notices.Count == 5, "late_result_after_source_change_is_discarded");
        }

        fake = new FakeConnection();
        using (var monitor = new CodexMonitor(Settings(), fake, false, () => schedules))
        {
            var notices = new List<PetNotice>(); monitor.Notice += notices.Add;
            await monitor.RefreshAsync();
            fake.Begin("wrong-id", FakeConnection.Heartbeat("other-job")); await monitor.RefreshAsync();
            fake.Complete("wrong-id", "错误绑定。", scheduled: false); await monitor.RefreshAsync();
            Check(notices.Count == 1 && !notices[0].IsScheduled, "heartbeat_id_must_match_the_thread_binding");
            fake.Begin("quoted", "请看这段文本：\n" + FakeConnection.Heartbeat()); await monitor.RefreshAsync();
            fake.Complete("quoted", "仅为引用。", scheduled: false); await monitor.RefreshAsync();
            Check(notices.Count == 2 && !notices[1].IsScheduled, "quoted_heartbeat_is_not_native_scheduled_trigger");
        }
        fake = new FakeConnection { Host = "durable" };
        settings = Settings(); settings.TaskSourceOverrides["codex:durable:thread-a"] = "codex";
        using (var monitor = new CodexMonitor(settings, fake, false, () => schedules))
        {
            await monitor.RefreshAsync();
            Check(fake.Reads == 0, "local_schedule_binding_does_not_bind_remote_thread_with_same_id");
        }

        var sample = new FakeConnection();
        var parsed = CodexScheduledTasks.LatestTurn(sample.History())!;
        Check(parsed.AutomationId == "sample-job" && parsed.Output == "原有结果。", "actual_heartbeat_shape_and_final_answer_phase_are_supported");
        sample.FinalPhase = "commentary";
        Check(CodexScheduledTasks.LatestTurn(sample.History())!.Output.Length == 0, "missing_final_answer_never_reads_commentary_or_tool_output");
        return new { passed = true, assertions = checks.Count, checks, syntheticOnly = true, userSettingsWritten = false, rpcRequestsSent = 0 };
    }

    private static DesktopSettings Settings() => new()
    {
        CodexEnabled = true, CodexMessagesEnabled = true, NotificationsEnabled = true,
        SceneAutoEnabled = true, ScenePackId = "synthetic-pack", RefreshSeconds = 120
    };

    private sealed class FakeConnection : ICodexConnection
    {
        public string ConnectionDescription => "Synthetic scheduled transport";
        public string? CallerThreadId => "test";
        public bool IsPairingRevoked => false;
        public bool HasSavedPairing => true;
        internal string Status = "idle", TurnId = "scheduled-1", TurnStatus = "completed", Output = "原有结果。", UserText = Heartbeat(), FinalPhase = "final_answer";
        internal string? Host;
        internal int UpdatedAt = 1, Reads;
        internal bool FailConnection;
        internal TaskCompletionSource<JsonElement>? Pending;
        internal static string Heartbeat(string id = "sample-job") => "<heartbeat>\r\n  <automation_id>" + id + "</automation_id>\r\n  <current_time_iso>2026-11-04T12:00:00.000Z</current_time_iso>\r\n  <instructions>\r\n合成测试指令。\r\n  </instructions>\r\n</heartbeat>\r\n";
        internal void Begin(string id, string? user = null) { TurnId = id; Status = "running"; TurnStatus = "inProgress"; UserText = user ?? Heartbeat(); UpdatedAt++; }
        internal void Complete(string id, string output, bool scheduled = true) { TurnId = id; Status = "idle"; TurnStatus = "completed"; Output = output; if (scheduled) UserText = Heartbeat(); UpdatedAt++; }
        public void SetMessageAccess(bool enabled) { }
        public bool ResumeSavedPairing() => true;
        public Task EnsureConnectedAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (FailConnection) throw new System.IO.IOException("Synthetic offline");
            return Task.CompletedTask;
        }
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (tool == "list_threads") return Task.FromResult(JsonSerializer.SerializeToElement(new { threads = new[] { new { id = "thread-a", title = "测试对话", kind = "codex", hostId = Host, status = Status, updatedAt = UpdatedAt } } }));
            if (tool == "get_usage_limits") return Task.FromResult(JsonSerializer.SerializeToElement(new { rateLimits = new { credits = new { balance = "10" } } }));
            if (tool == "read_thread") { Reads++; return Pending?.Task ?? Task.FromResult(History()); }
            throw new InvalidOperationException("Unexpected synthetic RPC");
        }
        internal JsonElement History() => JsonSerializer.SerializeToElement(new
        {
            thread = new { status = Status }, turns = new[] { new
            {
                id = TurnId, status = TurnStatus, items = new object[]
                {
                    new { type = "userMessage", content = new[] { new { type = "text", text = UserText } } },
                    new { type = "agentMessage", phase = "commentary", text = "不要播报的过程说明。" },
                    new { type = "commandExecution", output = "不要播报的工具结果。" },
                    new { type = "agentMessage", phase = FinalPhase, text = Output }
                }
            } }
        });
        public void Disconnect() { }
        public Task ForgetAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
