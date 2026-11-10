using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet.WorkCompanion;

internal static class WorkCompanionSettingsUiChecks
{
    internal static int Run()
    {
        var checks = new List<string>();
        string languageBefore = L.Language;
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        Window? window = null; WorkCompanionSettingsView? view = null;
        bool finished = false; int result = 1;
        string evidence = Path.Combine(AppContext.BaseDirectory, "work-companion-ui-evidence"); Directory.CreateDirectory(evidence);
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            view?.Dispose(); window?.Close(); L.Initialize(languageBefore);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "work-companion-ui-validation.json"), JsonSerializer.Serialize(new
            { passed, version = AppVersion.Number, assertions = checks.Count, checks, error,
                realAccountRequests = 0, realModelOperations = 0, userSettingsWrites = 0, audioOutput = false, observedDesktop = false }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "work_companion_ui_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                foreach (string language in new[] { L.Chinese, L.English })
                {
                    L.Initialize(language);
                    var host = new FakeHost();
                    int diagnosticReads = 0; bool diagnosticFails = false;
                    string diagnosticRecords = L.T("12:00 等待：聊天模型尚未加载。", "12:00 Waiting: the chat model is not loaded.");
                    view = new WorkCompanionSettingsView(host, () =>
                    {
                        diagnosticReads++;
                        if (diagnosticFails) throw new IOException("Synthetic diagnostic read failure");
                        return diagnosticRecords;
                    });
                    window = new Window { Title = "Proactive companion settings fixture", Width = 800, Height = 680,
                        Content = view, Background = Ui.Brush("#F4F6F0"), ShowActivated = false, ShowInTaskbar = false };
                    window.Show(); await Settle(window);
                    Check(host.Updates == 0 && host.SubscriberCount == 1, "opening_settings_is_read_only_" + language);
                    var diagnostics = Named<Expander>(window, "WorkCompanionDiagnostics");
                    Check(!diagnostics.IsExpanded && diagnosticReads == 0,
                        "recent_records_start_collapsed_without_reading_any_history_" + language);
                    diagnostics.IsExpanded = true; await Settle(window);
                    Check(diagnosticReads == 1 && Named<TextBlock>(window, "WorkCompanionDiagnosticRecords").Text == diagnosticRecords &&
                        host.Updates == 0 && host.Clears == 0,
                        "expanding_records_only_reads_the_injected_diagnostic_snapshot_" + language);
                    diagnosticRecords = L.T("12:01 等待：输入暂停后再回应。", "12:01 Waiting: replies wait for an input pause.");
                    Click(window, "WorkCompanionRefreshDiagnostics");
                    Check(diagnosticReads == 2 && Named<TextBlock>(window, "WorkCompanionDiagnosticRecords").Text == diagnosticRecords &&
                        host.Updates == 0 && host.Clears == 0,
                        "refreshing_records_does_not_save_settings_or_start_observation_" + language);
                    host.SetStatus(host.Status with { Detail = "Synthetic status refresh" });
                    Check(diagnosticReads == 2, "ordinary_status_refresh_does_not_poll_diagnostic_history_" + language);
                    diagnosticFails = true; Click(window, "WorkCompanionRefreshDiagnostics");
                    Check(!Named<TextBlock>(window, "WorkCompanionDiagnosticRecords").Text.Contains("Synthetic diagnostic", StringComparison.Ordinal) &&
                        host.Updates == 0, "record_read_failure_has_a_safe_inline_message_without_settings_side_effects_" + language);
                    diagnosticFails = false; diagnosticRecords = ""; Click(window, "WorkCompanionRefreshDiagnostics");
                    Check(Named<TextBlock>(window, "WorkCompanionDiagnosticRecords").Text == L.T("暂时没有陪伴记录。", "No companion records yet."),
                        "empty_diagnostic_history_has_a_clear_placeholder_" + language);
                    diagnostics.IsExpanded = false; await Settle(window);
                    Capture(window, Path.Combine(evidence, "work-companion-top-" + language + ".png"));
                    Check(Named<CheckBox>(window, "WorkCompanionEnabled").IsChecked == false && !Named<Button>(window, "WorkCompanionPause").IsEnabled,
                        "disabled_default_is_visible_and_pause_unavailable_" + language);
                    Named<CheckBox>(window, "WorkCompanionEnabled").IsChecked = true;
                    Named<CheckBox>(window, "WorkCompanionIncludeTitles").IsChecked = true;
                    Named<ComboBox>(window, "WorkCompanionBreakAfter").SelectedValue = 60;
                    Named<ComboBox>(window, "WorkCompanionCooldown").SelectedValue = 10;
                    Check(host.Options.Enabled && host.Options.IncludeWindowTitles && host.Options.BreakAfterMinutes == 60 && host.Options.CooldownMinutes == 10,
                        "visible_controls_save_feature_options_" + language);
                    Named<CheckBox>(window, "WorkCompanionGameEnabled").IsChecked = false;
                    Check(!host.Options.GameEnabled && !Named<ComboBox>(window, "WorkCompanionGameBreakAfter").IsEnabled && host.Options.WorkEnabled && host.Options.MediaEnabled && host.Options.DailyEnabled,
                        "gaming_toggle_is_independent_and_controls_its_reminder_" + language);
                    Named<CheckBox>(window, "WorkCompanionGameEnabled").IsChecked = true;
                    Named<ComboBox>(window, "WorkCompanionGameBreakAfter").SelectedValue = 90;
                    Named<ComboBox>(window, "WorkCompanionMediaBreakAfter").SelectedValue = 30;
                    Named<ComboBox>(window, "WorkCompanionDailyBreakAfter").SelectedValue = 60;
                    Named<CheckBox>(window, "WorkCompanionQuietFullscreen").IsChecked = false;
                    Check(host.Options.GameBreakAfterMinutes == 90 && host.Options.MediaBreakAfterMinutes == 30 && host.Options.DailyBreakAfterMinutes == 60 && !host.Options.QuietFullscreen,
                        "context_reminder_times_and_fullscreen_preference_save_independently_" + language);
                    Named<CheckBox>(window, "WorkCompanionQuietFullscreen").IsChecked = true;
                    Named<ComboBox>(window, "WorkCompanionDailyBreakAfter").BringIntoView(); await Settle(window);
                    Capture(window, Path.Combine(evidence, "companion-contexts-" + language + ".png"));
                    Named<ComboBox>(window, "WorkCompanionCooldown").BringIntoView(); await Settle(window);
                    Capture(window, Path.Combine(evidence, "companion-reactions-" + language + ".png"));
                    var appName = Named<TextBox>(window, "WorkCompanionActivityApp");
                    var category = Named<ComboBox>(window, "WorkCompanionActivityChoice");
                    int mappingsBefore = host.Updates;
                    appName.Text = @"C:\private\game.exe"; category.SelectedValue = "game"; Click(window, "WorkCompanionSaveActivity");
                    Check(host.Updates == mappingsBefore && host.Options.AppActivities.Count == 0,
                        "invalid_app_mapping_path_is_not_saved_" + language);
                    appName.Text = " MyGame.EXE "; Click(window, "WorkCompanionSaveActivity");
                    Check(host.Options.AppActivities.Count == 1 && host.Options.AppActivities["mygame"] == "game",
                        "custom_game_process_mapping_is_saved_and_normalized_" + language);
                    category.SelectedValue = "media"; Click(window, "WorkCompanionSaveActivity");
                    Check(host.Options.AppActivities.Count == 1 && host.Options.AppActivities["mygame"] == "media",
                        "saving_existing_mapping_updates_instead_of_duplicating_" + language);
                    var mappingList = Named<ListBox>(window, "WorkCompanionActivityList"); mappingList.SelectedIndex = 0;
                    appName.Text = "unsaved-game";
                    host.SetStatus(new WorkCompanionStatus("changed"));
                    Check(mappingList.SelectedIndex == 0 && appName.Text == "unsaved-game",
                        "status_refresh_preserves_mapping_selection_and_input_draft_" + language);
                    Named<Button>(window, "WorkCompanionRemoveActivity").BringIntoView(); await Settle(window);
                    Check(Fits(window, appName) && Fits(window, category) && Fits(window, mappingList), "mapping_controls_fit_page_width_" + language);
                    Capture(window, Path.Combine(evidence, "companion-mappings-" + language + ".png"));
                    Click(window, "WorkCompanionRemoveActivity");
                    Check(host.Options.AppActivities.Count == 0 && !Named<Button>(window, "WorkCompanionRemoveActivity").IsEnabled,
                        "removing_mapping_restores_automatic_classification_" + language);
                    Named<CheckBox>(window, "WorkCompanionObserveApplications").IsChecked = false;
                    Check(!Named<CheckBox>(window, "WorkCompanionIncludeTitles").IsEnabled && !Named<ComboBox>(window, "WorkCompanionBreakAfter").IsEnabled && Named<CheckBox>(window, "WorkCompanionUseCodexEvents").IsEnabled,
                        "app_observation_dependencies_do_not_disable_codex_events_" + language);
                    Named<CheckBox>(window, "WorkCompanionObserveApplications").IsChecked = true;
                    var exclusions = Named<TextBox>(window, "WorkCompanionExcludedApps");
                    exclusions.Text = "chrome.exe\ncode";
                    host.SetStatus(new WorkCompanionStatus(L.T("等待合适时机", "Waiting for a suitable moment"), L.T("聊天模型未加载", "Chat model is not loaded")));
                    Check(exclusions.Text == "chrome.exe\ncode", "status_refresh_preserves_unsaved_exclusion_draft_" + language);
                    int before = host.Updates; exclusions.Text = @"C:\private\browser.exe";
                    Click(window, "WorkCompanionSaveExclusions");
                    Check(host.Updates == before && Named<TextBlock>(window, "WorkCompanionSettingsFeedback").Text.Length > 0, "invalid_exclusion_draft_is_not_saved_" + language);
                    exclusions.Text = "chrome.exe\ncode"; Click(window, "WorkCompanionSaveExclusions");
                    Check(host.Options.ExcludedApps.SequenceEqual(new[] { "chrome", "code" }) && !Named<Button>(window, "WorkCompanionSaveExclusions").IsEnabled,
                        "valid_exclusion_list_saved_once_and_normalized_" + language);
                    Click(window, "WorkCompanionPause");
                    Check(host.Status.Paused && Named<Button>(window, "WorkCompanionPause").Content?.ToString() == L.T("恢复陪伴", "Resume companion"), "pause_action_updates_status_and_button_" + language);
                    Click(window, "WorkCompanionPause"); Check(!host.Status.Paused, "resume_action_restores_companion_" + language);
                    host.SetStatus(new WorkCompanionStatus(L.T("等待合适时机", "Waiting for a suitable moment"), L.T("用户正在聊天", "You are chatting"), L.T("辛苦啦，休息一下吧。", "You've been working hard. Take a short break.")));
                    Check(Named<TextBlock>(window, "WorkCompanionLastReaction").Text.Contains(host.Status.LastReaction!), "last_reaction_is_visible_without_history_store_" + language);
                    Capture(window, Path.Combine(evidence, "work-companion-status-" + language + ".png"));
                    Named<Button>(window, "WorkCompanionSaveExclusions").BringIntoView(); await Settle(window);
                    Check(Fits(window, exclusions) && Fits(window, Named<Button>(window, "WorkCompanionSaveExclusions")), "exclusions_are_reachable_at_minimum_page_width_" + language);
                    Capture(window, Path.Combine(evidence, "work-companion-options-" + language + ".png"));
                    Click(window, "WorkCompanionClear"); Check(host.Clears == 1 && host.Status.LastReaction == null, "clear_observations_routes_to_feature_host_" + language);
                    view.Dispose(); Check(host.SubscriberCount == 0, "disposed_page_releases_host_subscription_" + language);
                    host.SetStatus(new WorkCompanionStatus("after disposal"));
                    Check(Named<TextBlock>(window, "WorkCompanionStatus").Text != "after disposal", "disposed_page_ignores_later_status_updates_" + language);
                    window.Close(); window = null; view = null;
                }
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run(); return result;
    }
    private sealed class FakeHost : IWorkCompanionSettingsHost
    {
        public WorkCompanionOptions Options { get; private set; } = new();
        public WorkCompanionStatus Status { get; private set; } = new(L.T("已关闭", "Disabled"));
        public event Action? Changed;
        internal int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;
        internal int Updates, Clears;
        public void UpdateOptions(WorkCompanionOptions options) { Updates++; Options = options.Normalize(); Changed?.Invoke(); }
        public void SetPaused(bool paused) { Status = Status with { Paused = paused }; Changed?.Invoke(); }
        public void ClearTransientState() { Clears++; Status = Status with { LastReaction = null }; Changed?.Invoke(); }
        internal void SetStatus(WorkCompanionStatus status) { Status = status; Changed?.Invoke(); }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Named<T>(Window root, string name) where T : FrameworkElement => Walk(root).OfType<T>().First(x => x.Name == name);
    private static void Click(Window root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle(Window window)
    { await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render); await Task.Delay(50); window.UpdateLayout(); }
    private static bool Fits(Window window, FrameworkElement element)
    { Point origin = element.TransformToAncestor(window).Transform(new Point()); return element.IsVisible && origin.X >= 0 && origin.Y >= 0 && origin.X + element.ActualWidth <= window.ActualWidth && origin.Y + element.ActualHeight <= window.ActualHeight; }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
