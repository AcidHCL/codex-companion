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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

// Exercise the real WPF controls with synthetic transport/data only. This entry
// point does not use DesktopSettings.Load/Save, real Codex pipes, or the pet mutex.
internal static class CodexPrivacyUiChecks
{
    private const string FixtureTitle = "隐私验证 · 合成任务";
    private const string FixtureMessage = "这是一条仅用于界面验证的合成消息。";

    internal static int Run()
    {
        string reportPath = Path.Combine(AppContext.BaseDirectory, "privacy-ui-validation.json");
        string screenshotPath = Path.Combine(AppContext.BaseDirectory, "privacy-dashboard-preview.png");
        int assertions = 0, result = 1;
        bool finished = false;
        var groups = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        CodexDashboard? window = null;
        CodexMonitor? monitor = null;
        var fake = new SyntheticConnection();

        void Check(bool condition, string reason)
        {
            assertions++;
            if (!condition) throw new UiCheckException(reason);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                {
                    passed, assertions, groups, error, syntheticTransport = true, realAccountRequests = 0,
                    userSettingsWrites = 0, fakeRequests = fake.Calls, fakeMessageReads = fake.Reads,
                    fakeForgetCalls = fake.Forgets, fakeResumeCalls = fake.Resumes,
                    blockedPairingAttempts = fake.BlockedAttempts
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                monitor?.Dispose(); window?.Close(); result = passed ? 0 : 1; app.Shutdown(result);
            }
        }
        timeout.Tick += (_, _) => Finish(false, "UI_TEST_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Finish(false, "UI_DISPATCHER_ERROR_" + e.Exception.GetType().Name);
        };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var settings = new DesktopSettings { RefreshSeconds = 120, NotificationsEnabled = false };
                monitor = new CodexMonitor(settings, fake, persistSettings: false);
                window = new CodexDashboard(monitor, settings, () => { }) { ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                await UntilAsync(() => monitor.Snapshot.Connected && !monitor.Snapshot.Refreshing);
                await SettleAsync(window);
                var toggle = Descendants<CheckBox>(window).Single(x => Equals(x.Content, "读取对话内容"));
                Check(toggle.IsChecked != true && !monitor.MessagesEnabled, "Message checkbox must start unchecked.");
                Check(VisibleText(window).Contains("消息读取默认关闭"), "The default privacy explanation must be visible.");
                Check(fake.Reads == 0, "Opening the dashboard must not read messages.");
                Click(TaskButton(window));
                await SettleAsync(window);
                Check(fake.Reads == 0 && monitor.SelectedTask == null, "Task clicks cannot bypass the unchecked message permission.");
                groups.Add("default_privacy_and_task_click_guard");

                toggle.IsChecked = true;
                Click(TaskButton(window));
                await UntilAsync(() => monitor.Conversation?.Messages.Count == 1);
                await SettleAsync(window);
                Check(monitor.MessagesEnabled && fake.Reads == 1, "Checking the box and choosing a task must permit one read.");
                Check(VisibleText(window).Contains(FixtureMessage), "The opted-in message must appear in the transcript.");
                toggle.IsChecked = false;
                await SettleAsync(window);
                Check(!monitor.MessagesEnabled && monitor.Conversation == null && monitor.SelectedTask == null, "Unchecking must clear in-memory conversation and selection.");
                Check(!VisibleText(window).Contains(FixtureMessage), "Unchecking must remove the rendered message.");
                groups.Add("checkbox_opt_in_and_immediate_message_removal");

                // Populate the transcript again so clearing covers all three panels.
                toggle.IsChecked = true;
                Click(TaskButton(window));
                await UntilAsync(() => monitor.Conversation?.Messages.Count == 1);
                Click(Descendants<Button>(window).Single(x => Equals(x.Content, "断开并清除连接")));
                await UntilAsync(() => fake.Forgets == 1);
                await SettleAsync(window);
                Check(!monitor.ConnectionEnabled && !monitor.Snapshot.Connected, "Clear must disable the connection.");
                Check(monitor.Snapshot.Tasks.Count == 0 && monitor.Snapshot.Buckets.Count == 0 && monitor.Conversation == null, "Clear must discard tasks, usage, and messages.");
                Check(!VisibleText(window).Contains(FixtureTitle) && !VisibleText(window).Contains(FixtureMessage), "Cleared content must disappear from the real visual tree.");
                Check(toggle.IsChecked != true && !toggle.IsEnabled, "The message permission must be disabled after clear.");
                int afterClearCalls = fake.Calls;
                var source = PresentationSource.FromVisual(window) ?? throw new UiCheckException("The test window has no presentation source.");
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F5) { RoutedEvent = Keyboard.KeyDownEvent });
                await monitor.RefreshAsync(true);
                await SettleAsync(window);
                Check(fake.Calls == afterClearCalls && !monitor.ConnectionEnabled, "F5 and refresh cannot revive a cleared connection.");
                groups.Add("clear_removes_all_panels_and_f5_does_not_reconnect");

                // Represents a successful external explicit pairing. No actual
                // store or environment is changed by this in-memory operation.
                fake.IsPairingRevoked = false;
                Click(Descendants<Button>(window).Single(x => Equals(x.Content, "重新连接")));
                await UntilAsync(() => monitor.Snapshot.Connected && !monitor.Snapshot.Refreshing);
                await SettleAsync(window);
                Check(monitor.ConnectionEnabled && fake.Resumes == 1, "The reconnect control must use the newly saved pairing.");
                Check(!monitor.MessagesEnabled && toggle.IsChecked != true, "Re-pair/reconnect must not re-enable message access.");
                Check(monitor.Snapshot.Tasks.Count == 1 && monitor.Snapshot.Buckets.Count == 1, "Usage and tasks must resume after explicit reconnect.");
                Check(VisibleText(window).Contains("消息读取默认关闭") && !VisibleText(window).Contains(FixtureMessage), "The restored dashboard must stay message-private.");
                groups.Add("explicit_reconnect_after_pairing_keeps_messages_off");
                window.SavePreview(screenshotPath);
                Check(File.Exists(screenshotPath) && new FileInfo(screenshotPath).Length > 1000, "A synthetic-data dashboard preview must be saved.");

                async Task ShowPairingStateAsync(CodexPairingState state)
                {
                    window.Close(); monitor.Dispose();
                    fake.PairingState = state;
                    settings = new DesktopSettings { CodexEnabled = true, RefreshSeconds = 120, NotificationsEnabled = false };
                    monitor = new CodexMonitor(settings, fake, persistSettings: false);
                    window = new CodexDashboard(monitor, settings, () => { }) { ShowActivated = false, ShowInTaskbar = false };
                    window.Show();
                    await SettleAsync(window);
                    await UntilAsync(() => !monitor.Snapshot.Refreshing);
                }

                // An unreadable local record is recoverable and must not be
                // presented as a missing record or a request to pair again.
                int beforeUnavailableCalls = fake.Calls, beforeUnavailableReads = fake.Reads;
                await ShowPairingStateAsync(CodexPairingState.Unavailable);
                string notice = NoticeText(window);
                Check(notice.Contains("暂时无法读取") && notice.Contains("自动重试") && notice.Contains("无需重新配对"),
                    "Unavailable storage must explain automatic recovery without requiring pairing.");
                Check(!RequestsPairing(notice), "Unavailable storage must not display first-pairing instructions.");
                Check(!monitor.Snapshot.Connected && fake.Calls == beforeUnavailableCalls && fake.Reads == beforeUnavailableReads,
                    "Unavailable pairing must make no synthetic account or message requests.");
                Check(!monitor.MessagesEnabled && monitor.Conversation == null,
                    "An unavailable startup must remain message-private.");
                fake.PairingState = CodexPairingState.Ready;
                await monitor.RefreshAsync(true);
                await SettleAsync(window);
                Check(monitor.Snapshot.Connected && monitor.Snapshot.Tasks.Count == 1 && monitor.Snapshot.Buckets.Count == 1,
                    "Readable pairing must recover tasks and usage on refresh without re-pairing.");
                Check(NoticeText(window).Length == 0 && !monitor.MessagesEnabled && fake.Reads == beforeUnavailableReads,
                    "Recovery must clear the storage notice without enabling message reads.");
                groups.Add("temporary_pairing_failure_and_recovery_without_repairing");

                int beforeMissingCalls = fake.Calls;
                await ShowPairingStateAsync(CodexPairingState.Missing);
                notice = NoticeText(window);
                Check(notice.Contains("未找到配对记录") && notice.Contains("首次配对"),
                    "A genuinely missing pairing must explain the initial pairing step.");
                Check(!monitor.Snapshot.Connected && fake.Calls == beforeMissingCalls && !monitor.MessagesEnabled,
                    "Missing pairing must not request account data or enable messages.");
                groups.Add("missing_pairing_notice");

                int beforeRevokedCalls = fake.Calls;
                await ShowPairingStateAsync(CodexPairingState.Revoked);
                notice = NoticeText(window);
                Check(notice.Contains("连接已清除") && notice.Contains("请在 Codex 中重新配对"),
                    "Explicit revocation must explain re-pairing, not silently restore access.");
                source = PresentationSource.FromVisual(window) ?? throw new UiCheckException("The revoked test window has no presentation source.");
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F5) { RoutedEvent = Keyboard.KeyDownEvent });
                await monitor.RefreshAsync(true);
                await SettleAsync(window);
                Check(!monitor.ConnectionEnabled && !monitor.Snapshot.Connected && fake.Calls == beforeRevokedCalls,
                    "A revoked startup must remain disconnected after F5 and direct refresh.");
                Check(!monitor.MessagesEnabled && !Descendants<CheckBox>(window).Single(x => Equals(x.Content, "读取对话内容")).IsEnabled,
                    "Revocation must keep message permission disabled.");
                groups.Add("revoked_startup_notice_and_refresh_guard");

                int beforeInvalidCalls = fake.Calls;
                await ShowPairingStateAsync(CodexPairingState.Invalid);
                notice = NoticeText(window);
                Check(notice.Contains("未通过安全校验") && notice.Contains("已暂停请求"),
                    "Invalid pairing must explain the security validation failure.");
                Check(!RequestsPairing(notice), "Invalid pairing must not be mislabeled as a first-pairing requirement.");
                Check(!monitor.Snapshot.Connected && fake.Calls == beforeInvalidCalls && !monitor.MessagesEnabled,
                    "Invalid pairing must fail closed without synthetic account or message requests.");
                groups.Add("invalid_pairing_notice_and_request_guard");

                int beforeExpiredCalls = fake.Calls, beforeExpiredReads = fake.Reads;
                await ShowPairingStateAsync(CodexPairingState.Expired);
                notice = NoticeText(window);
                Check(notice.Contains("连接记录已过期") && notice.Contains("主动重新配对"),
                    "An expired pairing must explain expiration and deliberate re-pairing.");
                Check(!notice.Contains("首次配对") && !notice.Contains("未通过安全校验") && !notice.Contains("暂时无法读取"),
                    "An expired pairing must not be confused with missing, unsafe, or temporarily unreadable records.");
                await monitor.RefreshAsync(true);
                await SettleAsync(window);
                Check(!monitor.Snapshot.Connected && fake.Calls == beforeExpiredCalls && fake.Reads == beforeExpiredReads && !monitor.MessagesEnabled,
                    "Expired pairing must remain closed without account or message requests after refresh.");
                groups.Add("expired_pairing_notice_and_request_guard");
                Finish(true);
            }
            catch (Exception ex)
            {
                Finish(false, ex is UiCheckException ? ex.Message : "UI_TEST_ERROR_" + ex.GetType().Name);
            }
        };
        app.Run();
        return result;
    }

    private static Button TaskButton(DependencyObject root) => Descendants<Button>(root).Single(x => Equals(x.ToolTip, FixtureTitle));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string NoticeText(DependencyObject root)
    {
        var notice = Descendants<TextBlock>(root).Single(x => x.Name == "ConnectionNotice");
        return notice.IsVisible ? notice.Text : "";
    }
    private static bool RequestsPairing(string notice) => notice.Contains("首次配对") || notice.Contains("请在 Codex 中重新配对") || notice.Contains("请重新配对");
    private static string VisibleText(DependencyObject root) => string.Join("\n",
        Descendants<TextBlock>(root).Where(x => x.IsVisible).Select(x => x.Text)
            .Concat(Descendants<TextBox>(root).Where(x => x.IsVisible).Select(x => x.Text)));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }
    private static async Task UntilAsync(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new UiCheckException("A synthetic UI operation did not complete.");
            await Task.Delay(15);
        }
    }
    private sealed class UiCheckException : Exception { public UiCheckException(string reason) : base(reason) { } }

    private sealed class SyntheticConnection : ICodexConnection
    {
        private bool messages;
        public string ConnectionDescription => "合成验证连接";
        public string? CallerThreadId => "privacy-ui-fixture";
        public CodexPairingState PairingState { get; set; } = CodexPairingState.Ready;
        public bool IsPairingRevoked
        {
            get => PairingState == CodexPairingState.Revoked;
            set => PairingState = value ? CodexPairingState.Revoked : CodexPairingState.Ready;
        }
        public bool HasSavedPairing => PairingState == CodexPairingState.Ready;
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public int Forgets { get; private set; }
        public int Resumes { get; private set; }
        public int BlockedAttempts { get; private set; }
        public void SetMessageAccess(bool enabled) => messages = enabled;
        public Task EnsureConnectedAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (IsPairingRevoked) throw new UiCheckException("Synthetic connection was used after revocation.");
            if (PairingState != CodexPairingState.Ready)
            {
                BlockedAttempts++;
                if (PairingState == CodexPairingState.Unavailable) throw new IOException("Synthetic pairing record temporarily unavailable.");
                throw new InvalidOperationException("Synthetic pairing record is not ready.");
            }
            Calls++; return Task.CompletedTask;
        }
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (PairingState != CodexPairingState.Ready) throw new UiCheckException("A request bypassed synthetic pairing validation.");
            Calls++;
            if (tool == "list_threads") return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                threads = new[] { new { id = "privacy-ui-fixture", title = FixtureTitle, kind = "codex", status = "idle", updatedAt = 1 } }
            }));
            if (tool == "get_usage_limits") return Task.FromResult(Parse("{\"rateLimits\":{\"credits\":{\"balance\":\"1200\"},\"primary\":{\"windowDurationMins\":300,\"usedPercent\":20},\"secondary\":{\"windowDurationMins\":10080,\"usedPercent\":35}}}"));
            if (tool != "read_thread" || !messages) throw new UiCheckException("Synthetic message permission was bypassed.");
            Reads++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                turns = new[] { new { id = "privacy-ui-turn", status = "completed", items = new[] { new { id = "privacy-ui-message", type = "agentMessage", text = FixtureMessage } } } },
                page = new { hasMore = false }
            }));
        }
        private static JsonElement Parse(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        public bool ResumeSavedPairing() { Resumes++; return HasSavedPairing; }
        public Task ForgetAsync() { Forgets++; IsPairingRevoked = true; return Task.CompletedTask; }
        public void Disconnect() { }
        public void Dispose() { }
    }
}
