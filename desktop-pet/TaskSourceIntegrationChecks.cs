using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

// Synthetic sources and in-memory settings only. The test does not discover a
// Codex pipe, read user configuration, change consent, or send real RPC requests.
internal static class TaskSourceIntegrationChecks
{
    internal static int Run()
    {
        string report = Path.Combine(AppContext.BaseDirectory, "task-source-validation.json");
        string evidence = Path.Combine(AppContext.BaseDirectory, "task-source-test-evidence");
        var checks = new List<string>();
        var diagnostics = new Dictionary<string, object?>();
        var transports = new List<FakeConnection>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        CodexDashboard? dashboard = null;
        CodexMonitor? dashboardMonitor = null;
        int result = 1;
        bool finished = false;
        string phase = "starting";
        void Check(bool passed, string name)
        {
            if (!passed)
            {
                if (dashboard?.IsVisible == true)
                {
                    try
                    {
                        diagnostics["failedDashboardSourceFilter"] = dashboardMonitor?.DashboardFilter.ToString();
                        diagnostics["failedDashboardVisibleText"] = VisibleText(dashboard);
                        diagnostics["failedDashboardTaskRows"] = TaskRows(dashboard);
                        Directory.CreateDirectory(evidence);
                        dashboard.SavePreview(Path.Combine(evidence, "failure-preview.png"));
                    }
                    catch (Exception ex) { diagnostics["failurePreviewErrorType"] = ex.GetType().Name; }
                }
                throw new CheckFailure(name);
            }
            checks.Add(name);
        }
        FakeConnection Transport()
        {
            var fake = new FakeConnection(); transports.Add(fake); return fake;
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    passed, assertions = checks.Count, checks, error, phase, diagnostics,
                    syntheticTransport = true, realAccountRequests = 0, userSettingsWrites = 0,
                    fakeMessageReads = transports.Sum(t => t.ReadIds.Count),
                    fakeUsageReads = transports.Sum(t => t.UsageCalls)
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                dashboard?.Close(); dashboardMonitor?.Dispose();
                result = passed ? 0 : 1; app.Shutdown(result);
            }
        }
        timeout.Tick += (_, _) => Finish(false, "task_source_test_timeout");
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true; Finish(false, "dispatcher_" + e.Exception.GetType().Name);
        };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                phase = "classification";
                CheckClassification(Check);
                phase = "scope_and_persistence";
                await CheckScopesAndPersistence(Check, Transport);
                phase = "runtime_router_and_pet";
                await CheckRuntimeIsolation(Check, Transport, diagnostics);
                phase = "late_reads";
                await CheckLateReads(Check, Transport);

                phase = "dashboard";
                var settings = Settings();
                var uiFake = Transport();
                dashboardMonitor = new CodexMonitor(settings, uiFake, persistSettings: false);
                await dashboardMonitor.RefreshAsync(true);
                dashboard = new CodexDashboard(dashboardMonitor, settings, () => { })
                {
                    ShowActivated = false, ShowInTaskbar = false
                };
                dashboard.Show();
                await SettleAsync(dashboard);
                var filter = Descendants<ComboBox>(dashboard).Single(x => x.Name == "TaskSourceFilter");
                var originalUsage = dashboardMonitor.Snapshot.Buckets;
                // Locate the credit value by its raw-value tooltip, then compare
                // the rendered value across filters without assuming formatting.
                var balanceView = Descendants<TextBlock>(dashboard).Single(t => t.IsVisible &&
                    Equals(t.ToolTip, originalUsage.Single().CreditBalance));
                string originalBalanceText = balanceView.Text;
                diagnostics["initialCreditsDisplay"] = originalBalanceText;
                Check(filter.Items.Count == 3 && Equals(filter.SelectedValue, DashboardTaskFilter.Codex), "dashboard_defaults_to_codex_with_three_sources");
                Check(TaskRows(dashboard).SequenceEqual(new[] { FakeConnection.CodexTitle }), "dashboard_default_rows_exclude_dot_and_unknown");
                Check(uiFake.ReadIds.Count == 0, "opening_filtered_dashboard_keeps_messages_private");
                Directory.CreateDirectory(evidence);
                dashboard.SavePreview(Path.Combine(evidence, "codex-preview.png"));

                filter.SelectedValue = DashboardTaskFilter.Dot;
                await SettleAsync(dashboard);
                Check(dashboardMonitor.DashboardFilter == DashboardTaskFilter.Dot && TaskRows(dashboard).SequenceEqual(new[] { FakeConnection.DotTitle }), "dashboard_dot_filter_uses_monitor_scope");
                diagnostics["dotCreditsDisplay"] = balanceView.Text;
                Check(balanceView.IsVisible && balanceView.Text == originalBalanceText &&
                    ReferenceEquals(originalUsage, dashboardMonitor.Snapshot.Buckets), "dot_filter_preserves_account_wide_credits");
                Click(TaskRow(dashboard, FakeConnection.DotTitle));
                await SettleAsync(dashboard);
                Check(uiFake.ReadIds.Count == 0 && dashboardMonitor.Conversation == null, "dot_row_click_cannot_bypass_message_consent");

                filter.SelectedValue = DashboardTaskFilter.All;
                await SettleAsync(dashboard);
                Check(TaskRows(dashboard).Count == 3, "all_filter_shows_codex_dot_and_unknown");
                Check(Descendants<TextBlock>(dashboard).Any(t => t.Name == "TaskSourceHint" && t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)), "all_filter_explains_unknown_sources");
                Check(TaskRow(dashboard, FakeConnection.UnknownTitle).ContextMenu?.Items.Count >= 3, "unknown_row_offers_manual_source_assignment");
                var manualDot = TaskRow(dashboard, FakeConnection.UnknownTitle).ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "归入 Dot"));
                manualDot.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await SettleAsync(dashboard);
                Check(dashboardMonitor.Snapshot.Tasks.Single(t => t.Id == "unknown") is { Source: TaskSource.Dot, SourceIsManual: true } && uiFake.ReadIds.Count == 0, "real_assignment_menu_classifies_without_message_access");
                filter.SelectedValue = DashboardTaskFilter.Dot;
                await SettleAsync(dashboard);
                Check(TaskRows(dashboard).Count == 2, "manually_assigned_unknown_appears_in_dot_filter");
                var automatic = TaskRow(dashboard, FakeConnection.UnknownTitle).ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "自动识别来源"));
                automatic.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await SettleAsync(dashboard);
                Check(TaskRows(dashboard).Count == 1 && dashboardMonitor.Snapshot.UnknownTasks.Count == 1, "real_auto_assignment_menu_restores_unknown_and_removes_dot_row");
                filter.SelectedValue = DashboardTaskFilter.All;
                await SettleAsync(dashboard);
                dashboard.SavePreview(Path.Combine(evidence, "all-preview.png"));

                filter.SelectedValue = DashboardTaskFilter.Dot;
                await SettleAsync(dashboard);
                var consent = Descendants<CheckBox>(dashboard).Single(x => Equals(x.Content, "读取对话内容"));
                consent.IsChecked = true;
                Click(TaskRow(dashboard, FakeConnection.DotTitle));
                await UntilAsync(() => dashboardMonitor.Conversation?.Messages.Count == 1);
                await SettleAsync(dashboard);
                Check(uiFake.ReadIds.SequenceEqual(new[] { "dot" }) && VisibleText(dashboard).Contains(FakeConnection.Message("dot")), "explicit_dot_selection_renders_opted_in_conversation");
                filter.SelectedValue = DashboardTaskFilter.Codex;
                await SettleAsync(dashboard);
                Check(dashboardMonitor.SelectedTask == null && dashboardMonitor.Conversation == null && !VisibleText(dashboard).Contains(FakeConnection.Message("dot")), "dashboard_source_change_immediately_removes_prior_transcript");
                diagnostics["finalCreditsDisplay"] = balanceView.Text;
                Check(balanceView.IsVisible && balanceView.Text == originalBalanceText &&
                    ReferenceEquals(originalUsage, dashboardMonitor.Snapshot.Buckets), "source_changes_do_not_reset_account_usage");
                Check(app.Windows.OfType<Window>().Count(w => w.IsVisible) == 1, "source_filters_do_not_create_a_visible_dot_companion");

                phase = "minimum_dashboard_layout";
                dashboard.Width = 1000; dashboard.Height = 720;
                await SettleAsync(dashboard);
                var tasksScroll = Descendants<ScrollViewer>(dashboard).Single(s => s.Content is StackPanel panel &&
                    Descendants<Button>(panel).Any(b => b.ToolTip is string title && FakeConnection.Titles.Contains(title)));
                var firstTask = TaskRow(dashboard, FakeConnection.CodexTitle);
                diagnostics["minimumDashboardLayout"] = new
                {
                    dashboard.ActualWidth, dashboard.ActualHeight, tasksScroll.ViewportHeight,
                    taskViewportActualHeight = tasksScroll.ActualHeight, firstTaskHeight = firstTask.ActualHeight,
                    firstTask.DesiredSize
                };
                Check(tasksScroll.ViewportHeight >= firstTask.ActualHeight && firstTask.ActualHeight >= 45,
                    "minimum_dashboard_size_keeps_one_complete_task_row_visible");
                dashboard.SavePreview(Path.Combine(evidence, "minimum-preview.png"));
                Finish(true);
            }
            catch (Exception ex)
            {
                Finish(false, ex is CheckFailure ? ex.Message : "test_" + ex.GetType().Name);
            }
        };
        app.Run();
        return result;
    }

    private static DesktopSettings Settings(bool messages = false) => new()
    {
        CodexEnabled = true, CodexMessagesEnabled = messages, NotificationsEnabled = true,
        RefreshSeconds = 120, SceneAutoEnabled = true, ScenePackId = "synthetic-pack"
    };

    private static void CheckClassification(Action<bool, string> check)
    {
        CodexTaskInfo Parse(Dictionary<string, object?> row) => CodexParser.ParseTasks(JsonSerializer.SerializeToElement(new { threads = new[] { row } })).Single();
        var local = FakeConnection.Row("a", "Dot orbit agent named task", "idle", null);
        var durable = FakeConnection.Row("b", "Codex normal named task", "idle", "durable");
        check(Parse(local).Source == TaskSource.Codex, "local_source_is_not_inferred_from_title");
        check(Parse(durable).Source == TaskSource.Unknown, "durable_host_alone_is_unknown_not_dot");
        durable["threadSource"] = "aeon";
        check(Parse(durable).Source == TaskSource.Unknown, "aeon_without_orbit_evidence_remains_unknown");
        durable["agentProfile"] = new { aeon_kind = "orbit" };
        check(Parse(durable).Source == TaskSource.Dot, "strict_durable_aeon_orbit_metadata_identifies_dot");
        durable["hostId"] = "unrecognized-remote";
        check(Parse(durable).Source == TaskSource.Unknown, "unknown_remote_host_is_not_promoted_by_partial_metadata");
        var fabricated = FakeConnection.Row("c", "Dot", "idle", "durable");
        fabricated["threadSource"] = "dot";
        check(Parse(fabricated).Source == TaskSource.Unknown, "unsupported_dot_source_string_does_not_invent_native_metadata");
        local["threadSource"] = "aeon";
        check(Parse(local).Source == TaskSource.Unknown, "local_non_user_agent_source_is_not_promoted_to_codex");
        local["threadSource"] = "subagent";
        check(Parse(local).Source == TaskSource.Unknown, "local_subagent_source_does_not_mix_with_user_codex_tasks");
        check(new CodexTaskInfo("legacy", "legacy", "idle", "codex", null, null, 0, false).Source == TaskSource.Codex, "legacy_local_fixture_constructor_remains_codex");
    }

    private static async Task CheckScopesAndPersistence(Action<bool, string> check, Func<FakeConnection> transport)
    {
        var settings = Settings();
        var fake = transport();
        using var monitor = new CodexMonitor(settings, fake, persistSettings: false);
        await monitor.RefreshAsync(true);
        check(monitor.Snapshot.Tasks.Count == 3 && monitor.Snapshot.Connected, "raw_snapshot_retains_all_three_task_sources");
        check(monitor.DashboardFilter == DashboardTaskFilter.Codex && monitor.DashboardTasks.Count == 1 && monitor.DashboardTasks.Single().Id == "codex", "monitor_default_focus_is_codex");
        check(monitor.CodexPetSnapshot.Tasks.Single().Id == "codex" && monitor.DotTaskSnapshot.Tasks.Single().Id == "dot", "monitor_pet_and_dot_projections_are_disjoint");
        check(monitor.Snapshot.UnknownTasks.Single().Id == "unknown", "unknown_source_is_retained_for_all_scope");
        var originalBuckets = monitor.Snapshot.Buckets;
        var unknown = monitor.Snapshot.Tasks.Single(t => t.Id == "unknown");
        monitor.SetDashboardFilter(DashboardTaskFilter.Dot);
        await monitor.SelectTaskAsync(monitor.DashboardTasks.Single());
        monitor.SetDashboardFilter(DashboardTaskFilter.All);
        monitor.SetTaskSourceOverride(unknown, TaskSource.Dot);
        check(monitor.Snapshot.Tasks.Single(t => t.Id == "unknown") is { Source: TaskSource.Dot, SourceIsManual: true }, "manual_dot_assignment_updates_current_snapshot");
        await monitor.RefreshAsync();
        check(monitor.Snapshot.Tasks.Single(t => t.Id == "unknown") is { Source: TaskSource.Dot, SourceIsManual: true }, "manual_source_survives_refresh");
        var roundtrip = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings))!;
        roundtrip.Normalize();
        using (var restored = new CodexMonitor(roundtrip, transport(), persistSettings: false))
        {
            await restored.RefreshAsync(true);
            check(restored.Snapshot.Tasks.Single(t => t.Id == "unknown") is { Source: TaskSource.Dot, SourceIsManual: true }, "manual_source_survives_settings_serialization_and_new_monitor");
            check(restored.DashboardFilter == DashboardTaskFilter.Codex, "new_monitor_restarts_with_codex_focus");
        }
        monitor.SetTaskSourceOverride(unknown, null);
        check(monitor.Snapshot.Tasks.Single(t => t.Id == "unknown") is { Source: TaskSource.Unknown, SourceIsManual: false }, "removing_manual_source_restores_detected_unknown");
        fake.PendingList = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refresh = monitor.RefreshAsync();
        check(!refresh.IsCompleted, "raw_task_refresh_is_in_flight_before_manual_assignment");
        monitor.SetTaskSourceOverride(unknown, TaskSource.Dot);
        fake.PendingList.SetResult(fake.Tasks()); await refresh; fake.PendingList = null;
        check(monitor.Snapshot.Tasks.Single(t => t.Id == "unknown") is { Source: TaskSource.Dot, SourceIsManual: true }, "late_task_list_cannot_overwrite_new_manual_assignment");
        check(fake.ReadIds.Count == 0, "filtering_classification_and_refresh_make_no_message_reads_without_consent");
        check(ReferenceEquals(originalBuckets, monitor.Snapshot.Buckets) && monitor.CodexPetSnapshot.Buckets.Single().CreditBalance == "1200" && monitor.DotTaskSnapshot.Buckets.Single().CreditBalance == "1200", "source_projections_share_account_usage_without_reclassification");
        check(fake.UsageCalls == 1, "source_switching_does_not_trigger_extra_usage_requests");
    }

    private static async Task CheckRuntimeIsolation(Action<bool, string> check, Func<FakeConnection> transport, IDictionary<string, object?> diagnostics)
    {
        var fake = transport();
        fake.Statuses["codex"] = "running"; fake.Statuses["dot"] = "running"; fake.Statuses["unknown"] = "running";
        using var monitor = new CodexMonitor(Settings(true), fake, persistSettings: false);
        var notices = new List<PetNotice>(); var transitions = new List<PetNotice>();
        monitor.Notice += notices.Add; monitor.VerifiedTransition += transitions.Add;
        await monitor.RefreshAsync(true);
        check(fake.ReadIds.SequenceEqual(new[] { "codex" }), "automatic_runtime_status_read_only_targets_codex");
        var router = new CompanionTaskRouter();
        router.Observe(monitor.Snapshot);
        check(router.MainPet.Activity == CompanionActivity.Working && router.Dot.Activity == CompanionActivity.Working, "router_keeps_separate_codex_and_dot_work_states");
        check(!router.DotCompanionEnabled && !router.DotCompanionVisible, "dot_companion_framework_is_disabled_and_hidden");
        fake.ReadIds.Clear(); fake.Statuses["dot"] = "waitingForInput"; fake.Statuses["unknown"] = "waitingForInput";
        await monitor.RefreshAsync();
        router.Observe(monitor.Snapshot);
        check(notices.Count == 0 && transitions.Count == 0 && fake.ReadIds.All(id => id == "codex"), "dot_or_unknown_attention_never_emits_main_pet_notices_or_reads");
        check(router.MainPet.Activity == CompanionActivity.Working && router.Dot.Activity == CompanionActivity.Attention, "dot_attention_does_not_override_main_pet_work_state");
        fake.Statuses["codex"] = "idle"; fake.ReadIds.Clear();
        await monitor.RefreshAsync();
        router.Observe(monitor.Snapshot);
        check(notices.Count == 1 && transitions.Count == 1 && notices[0].Message == FakeConnection.CodexTitle, "only_codex_completion_produces_verified_main_pet_notice");
        check(fake.ReadIds.SequenceEqual(new[] { "codex" }), "completion_verification_does_not_inspect_dot_or_unknown");
        check(router.MainPet.Activity == CompanionActivity.Idle && router.Dot.Activity == CompanionActivity.Attention, "codex_idle_and_dot_attention_remain_independent");
        var scene = new SceneTriggerPolicy();
        scene.Observe(monitor.Snapshot);
        check(scene.PersistentAction == null && !scene.HasRunningTasks, "raw_mixed_snapshot_cannot_trigger_dot_main_pet_scene");
        var skins = SkinCatalog.Load(AppContext.BaseDirectory);
        var petSettings = new DesktopSettings { SkinId = skins.First(s => s.Id != "deepseek").Id, CodexEnabled = false, MotionEnabled = false, MovementEnabled = false, Topmost = false };
        var pet = new PetWindow(skins, petSettings, persist: false, subscribeToSystemEvents: false);
        try
        {
            // Realize this isolated static-skin window so VisualTreeHelper sees
            // the actual badge; it never activates or starts a Codex monitor.
            pet.Show();
            await SettleAsync(pet);
            pet.SetCodexStatus(monitor.Snapshot);
            await SettleAsync(pet);
            string state = string.Join("\n", Descendants<TextBlock>(pet).Select(t => t.Text));
            diagnostics["mainPetBadgeText"] = state;
            check(state.Contains("Codex 待命") && !state.Contains("项待处理"), "real_main_pet_badge_ignores_dot_and_unknown_attention");
            pet.SetChatBusy(true);
            pet.SetCodexStatus(monitor.Snapshot);
            string duringChat = string.Join("\n", Descendants<TextBlock>(pet).Select(t => t.Text));
            check(pet.IsChatBusy && duringChat.Contains("Codex 待命") && !duringChat.Contains("项执行中"), "local_chat_busy_does_not_manufacture_a_codex_task_or_badge_count");
            pet.SetChatBusy(false);
        }
        finally { pet.Close(); }
        monitor.SetDashboardFilter(DashboardTaskFilter.Dot);
        router.Observe(monitor.Snapshot);
        check(router.MainPet.Activity == CompanionActivity.Idle && !router.DotCompanionVisible, "dashboard_dot_filter_cannot_redirect_or_show_companions");

        // A completion verifier already sent before reclassification may finish;
        // its result must not announce the now-Dot task through the main pet.
        fake.Statuses["codex"] = "running";
        await monitor.RefreshAsync();
        fake.Statuses["codex"] = "idle";
        fake.PendingRead = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pendingCompletion = monitor.RefreshAsync();
        check(!pendingCompletion.IsCompleted, "completion_verification_is_in_flight_before_source_change");
        monitor.SetTaskSourceOverride(monitor.Snapshot.Tasks.Single(t => t.Id == "codex"), TaskSource.Dot);
        fake.PendingRead.SetResult(fake.History("codex")); await pendingCompletion; fake.PendingRead = null;
        check(notices.Count == 1 && transitions.Count == 1, "late_completion_of_reclassified_dot_task_cannot_emit_main_pet_notice");
    }

    private static async Task CheckLateReads(Action<bool, string> check, Func<FakeConnection> transport)
    {
        var fake = transport();
        using var monitor = new CodexMonitor(Settings(true), fake, persistSettings: false);
        await monitor.RefreshAsync(true);
        var dot = monitor.Snapshot.Tasks.Single(t => t.Id == "dot");
        await monitor.SelectTaskAsync(dot);
        check(fake.ReadIds.Count == 0 && monitor.SelectedTask == null, "hidden_source_cannot_be_selected_through_monitor_api");
        monitor.SetDashboardFilter(DashboardTaskFilter.Dot);
        await monitor.SelectTaskAsync(dot);
        check(monitor.Conversation?.Messages.Single().Text == FakeConnection.Message("dot"), "explicit_dot_scope_selection_can_read_after_consent");

        fake.PendingRead = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = monitor.SelectTaskAsync(dot);
        check(!pending.IsCompleted && monitor.IsReading, "dot_read_is_genuinely_in_flight");
        monitor.SetDashboardFilter(DashboardTaskFilter.Codex);
        check(fake.LastReadToken.IsCancellationRequested && monitor.SelectedTask == null && monitor.Conversation == null && !monitor.IsReading, "source_change_cancels_read_and_clears_transcript_immediately");
        fake.PendingRead.SetResult(fake.History("dot")); await pending; fake.PendingRead = null;
        check(monitor.Conversation == null && monitor.SelectedTask == null && !monitor.IsReading, "late_dot_response_cannot_repopulate_codex_scope");

        var codex = monitor.Snapshot.Tasks.Single(t => t.Id == "codex");
        fake.PendingRead = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = monitor.SelectTaskAsync(codex);
        monitor.SetTaskSourceOverride(codex, TaskSource.Dot);
        check(fake.LastReadToken.IsCancellationRequested && monitor.SelectedTask == null, "reclassification_cancels_in_flight_selection_that_leaves_scope");
        fake.PendingRead.SetResult(fake.History("codex")); await pending; fake.PendingRead = null;
        check(monitor.Conversation == null && !monitor.IsReading, "late_reclassified_response_is_discarded");
        monitor.SetTaskSourceOverride(codex, null);
        monitor.SetDashboardFilter(DashboardTaskFilter.Dot);
        await monitor.SelectTaskAsync(dot);
        fake.PendingRead = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = monitor.RefreshAsync();
        check(!pending.IsCompleted && monitor.IsReading, "selected_dot_transcript_refresh_is_in_flight");
        monitor.SetDashboardFilter(DashboardTaskFilter.Codex);
        fake.PendingRead.SetResult(fake.History("dot")); await pending; fake.PendingRead = null;
        check(monitor.Snapshot.Connected && monitor.Snapshot.Tasks.Count == 3 && monitor.Conversation == null, "filter_change_preserves_raw_refresh_but_discards_stale_selected_read");

        monitor.SetDashboardFilter(DashboardTaskFilter.Dot);
        fake.HasMore = true;
        await monitor.SelectTaskAsync(dot);
        fake.PendingRead = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = monitor.LoadOlderAsync();
        check(!pending.IsCompleted && monitor.IsReading, "older_dot_page_read_is_in_flight");
        monitor.SetDashboardFilter(DashboardTaskFilter.Codex);
        check(fake.LastReadToken.IsCancellationRequested, "source_change_cancels_older_page_read");
        fake.PendingRead.SetResult(fake.History("dot")); await pending; fake.PendingRead = null;
        check(monitor.SelectedTask == null && monitor.Conversation == null && !monitor.IsReading, "late_older_dot_page_cannot_restore_hidden_transcript");
    }

    private static Button TaskRow(DependencyObject root, string title) => Descendants<Button>(root).Single(b => Equals(b.ToolTip, title));
    private static IReadOnlyList<string> TaskRows(DependencyObject root) => Descendants<Button>(root)
        .Where(b => b.ToolTip is string s && FakeConnection.Titles.Contains(s)).Select(b => (string)b.ToolTip).OrderBy(t => t, StringComparer.Ordinal).ToArray();
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string VisibleText(DependencyObject root) => string.Join("\n",
        Descendants<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text)
            .Concat(Descendants<TextBox>(root).Where(t => t.IsVisible).Select(t => t.Text)));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }
    private static async Task UntilAsync(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new CheckFailure("synthetic_ui_operation_timeout");
            await Task.Delay(15);
        }
    }
    private sealed class CheckFailure(string message) : Exception(message);

    private sealed class FakeConnection : ICodexConnection
    {
        internal const string CodexTitle = "Codex · 本地合成任务";
        internal const string DotTitle = "Dot · 合成代理任务";
        internal const string UnknownTitle = "来源待确认 · 合成远程任务";
        internal static readonly string[] Titles = { CodexTitle, DotTitle, UnknownTitle };
        internal readonly Dictionary<string, string> Statuses = new() { ["codex"] = "idle", ["dot"] = "idle", ["unknown"] = "idle" };
        internal readonly List<string> ReadIds = new();
        internal int UsageCalls;
        internal CancellationToken LastReadToken;
        internal TaskCompletionSource<JsonElement>? PendingRead;
        internal TaskCompletionSource<JsonElement>? PendingList;
        internal bool HasMore;
        private bool _messages;
        public string ConnectionDescription => "Synthetic source test";
        public string? CallerThreadId => "codex";
        public bool IsPairingRevoked => false;
        public bool HasSavedPairing => true;
        public void SetMessageAccess(bool enabled) => _messages = enabled;
        public Task EnsureConnectedAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<JsonElement> CallAsync(string tool, object args, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (tool == "list_threads") return PendingList?.Task ?? Task.FromResult(Tasks());
            if (tool == "get_usage_limits")
            {
                UsageCalls++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { accountId = "synthetic-account", rateLimits = new { credits = new { balance = "1200" }, primary = new { windowDurationMins = 300, usedPercent = 20 }, secondary = new { windowDurationMins = 10080, usedPercent = 35 } } }));
            }
            if (tool != "read_thread" || !_messages) throw new CheckFailure("synthetic_transport_message_consent_bypass");
            var arguments = JsonSerializer.SerializeToElement(args);
            string id = arguments.GetProperty("threadId").GetString()!;
            ReadIds.Add(id); LastReadToken = token;
            // Intentionally ignore cancellation to simulate an already delivered
            // response. The monitor's read generation must reject its late data.
            return PendingRead?.Task ?? Task.FromResult(History(id));
        }
        internal JsonElement Tasks()
        {
            var dot = Row("dot", DotTitle, Statuses["dot"], "durable");
            dot["threadSource"] = "aeon"; dot["agentProfile"] = new { aeon_kind = "orbit" };
            return JsonSerializer.SerializeToElement(new { threads = new[] { Row("codex", CodexTitle, Statuses["codex"], null), dot, Row("unknown", UnknownTitle, Statuses["unknown"], "durable") } });
        }
        internal static Dictionary<string, object?> Row(string id, string title, string status, string? host) => new()
        {
            ["id"] = id, ["title"] = title, ["status"] = status, ["kind"] = "codex", ["hostId"] = host, ["updatedAt"] = 1
        };
        internal static string Message(string id) => "仅用于来源筛选验证的合成消息：" + id;
        internal JsonElement History(string id) => JsonSerializer.SerializeToElement(new
        {
            thread = new { status = Statuses[id] },
            turns = new[] { new { id = "fixture-turn-" + id, status = Statuses[id] == "idle" ? "completed" : "running", items = new[] { new { id = "message-" + id, type = "agentMessage", text = Message(id) } } } },
            page = new { hasMore = HasMore, nextCursor = HasMore ? "synthetic-older-cursor" : null }
        });
        public bool ResumeSavedPairing() => true;
        public void Disconnect() { }
        public Task ForgetAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
