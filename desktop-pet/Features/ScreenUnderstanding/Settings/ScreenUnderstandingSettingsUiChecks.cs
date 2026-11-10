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

namespace DesktopPet.ScreenUnderstanding;

internal static class ScreenUnderstandingSettingsUiChecks
{
    // Run separately from any other check that creates a WPF Application.
    internal static int Run()
    {
        string languageBefore = L.Language;
        var checks = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        Window? window = null; ScreenUnderstandingSettingsView? view = null;
        string evidence = Path.Combine(AppContext.BaseDirectory, "screen-understanding-ui-evidence"); Directory.CreateDirectory(evidence);
        int result = 1; bool finished = false;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            view?.Dispose(); window?.Close(); L.Initialize(languageBefore);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "screen-understanding-ui-validation.json"), JsonSerializer.Serialize(new
            { passed, version = AppVersion.Number, assertions = checks.Count, checks, error,
                realModelRequests = 0, userSettingsWrites = 0, userWindowsRead = 0, userScreenshots = 0, audioOutput = false },
                new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "screen_understanding_ui_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                foreach (string language in new[] { L.Chinese, L.English })
                {
                    L.Initialize(language);
                    var host = new FakeHost(); view = new ScreenUnderstandingSettingsView(host);
                    window = new Window { Title = "Screen understanding settings fixture", Width = 800, Height = 700,
                        Content = view, ShowActivated = false, ShowInTaskbar = false, Background = Ui.Brush("#F4F6F0") };
                    window.Show(); await Settle(window);
                    Check(host.Updates == 0 && host.Requests == 0 && host.Subscribers == 1,
                        "opening_settings_is_read_only_and_subscribes_once_" + language);
                    Check(Named<CheckBox>(window, "ScreenUnderstandingEnabled").IsChecked == false &&
                        Named<CheckBox>(window, "ScreenUnderstandingRemote").IsChecked == false &&
                        !Named<Button>(window, "ScreenUnderstandingRequest").IsEnabled,
                        "screen_and_remote_opt_ins_start_off_" + language);
                    Named<CheckBox>(window, "ScreenUnderstandingEnabled").IsChecked = true;
                    Named<CheckBox>(window, "ScreenUnderstandingReadText").IsChecked = false;
                    Named<CheckBox>(window, "ScreenUnderstandingScreenshots").IsChecked = false;
                    Check(!Named<Button>(window, "ScreenUnderstandingRequest").IsEnabled && host.Options.Enabled,
                        "request_requires_at_least_one_enabled_input_source_" + language);
                    Named<CheckBox>(window, "ScreenUnderstandingScreenshots").IsChecked = true;
                    Named<CheckBox>(window, "ScreenUnderstandingRemote").IsChecked = true;
                    Named<CheckBox>(window, "ScreenUnderstandingLearn").IsChecked = false;
                    Named<ComboBox>(window, "ScreenUnderstandingInterval").SelectedValue = 10;
                    Check(host.Options is { Enabled: true, ReadWindowText: false, UseScreenshots: true, AllowRemoteEndpoint: true,
                        LearnApplications: false, AutomaticIntervalMinutes: 10 }, "independent_settings_controls_save_correct_preferences_" + language);
                    Click(window, "ScreenUnderstandingRequest");
                    Check(host.Requests == 1, "manual_button_only_queues_host_request_without_reading_the_settings_window_" + language);
                    Named<CheckBox>(window, "ScreenUnderstandingRemote").IsChecked = false;
                    Named<CheckBox>(window, "ScreenUnderstandingReadText").IsChecked = true;
                    Named<CheckBox>(window, "ScreenUnderstandingLearn").IsChecked = true;
                    host.Status = new ScreenUnderstandingStatus(L.T("等待目标窗口", "Waiting for the target window"),
                        L.T("切回应用并暂停输入后读取。", "Return to the app and pause input before analysis."),
                        L.T("这是一个游戏窗口，画面中有菜单。", "This appears to be a game window with a menu visible."));
                    host.Notify();
                    Check(Named<TextBlock>(window, "ScreenUnderstandingLastSummary").Text.Contains(host.Status.LastSummary!),
                        "transient_understanding_is_visible_without_persisting_it_" + language);
                    Named<CheckBox>(window, "ScreenUnderstandingEnabled").BringIntoView(); await Settle(window);
                    Capture(window, Path.Combine(evidence, "screen-understanding-top-" + language + ".png"));
                    Named<ComboBox>(window, "ScreenUnderstandingInterval").BringIntoView(); await Settle(window);
                    Check(Fits(window, Named<CheckBox>(window, "ScreenUnderstandingRemote")) &&
                        Fits(window, Named<ComboBox>(window, "ScreenUnderstandingInterval")), "read_options_fit_small_settings_page_" + language);
                    Capture(window, Path.Combine(evidence, "screen-understanding-options-" + language + ".png"));

                    var memories = Named<ListBox>(window, "ScreenUnderstandingMemories"); memories.SelectedIndex = 0;
                    var selected = (LearnedApplication)((ListBoxItem)memories.SelectedItem).Tag;
                    Named<ComboBox>(window, "ScreenUnderstandingMemoryCategory").SelectedValue = "media";
                    host.Status = host.Status with { Detail = "status refresh fixture" }; host.Notify();
                    Check(memories.SelectedIndex == 0 && Named<ComboBox>(window, "ScreenUnderstandingMemoryCategory").SelectedValue as string == "media",
                        "status_refresh_preserves_memory_selection_and_correction_draft_" + language);
                    Click(window, "ScreenUnderstandingCorrectMemory");
                    Check(host.GetMemories().First(item => item.Id == selected.Id) is { Activity: "media", UserCorrected: true } && host.Corrections == 1,
                        "selected_application_category_can_be_corrected_" + language);
                    Named<Button>(window, "ScreenUnderstandingClearMemories").BringIntoView(); await Settle(window);
                    Check(Fits(window, memories) && Fits(window, Named<ComboBox>(window, "ScreenUnderstandingMemoryCategory")) &&
                        Fits(window, Named<Button>(window, "ScreenUnderstandingDeleteMemory")), "memory_management_is_reachable_without_horizontal_overflow_" + language);
                    Capture(window, Path.Combine(evidence, "screen-understanding-memory-" + language + ".png"));
                    Click(window, "ScreenUnderstandingDeleteMemory");
                    Check(host.GetMemories().Count == 1 && !host.GetMemories().Any(item => item.Id == selected.Id) && host.Deletes == 1,
                        "delete_removes_only_selected_memory_" + language);
                    Click(window, "ScreenUnderstandingClearMemories");
                    Check(host.GetMemories().Count == 0 && host.Clears == 1 && !Named<Button>(window, "ScreenUnderstandingClearMemories").IsEnabled &&
                        !Named<Button>(window, "ScreenUnderstandingCorrectMemory").IsEnabled,
                        "clear_memory_disables_empty_selection_actions_" + language);
                    host.FailSave = true;
                    Named<CheckBox>(window, "ScreenUnderstandingEnabled").IsChecked = false;
                    Check(host.Options.Enabled && Named<CheckBox>(window, "ScreenUnderstandingEnabled").IsChecked == true &&
                        Named<TextBlock>(window, "ScreenUnderstandingFeedback").Text.Length > 0,
                        "failed_save_keeps_existing_options_and_restores_control_state_" + language);
                    view.Dispose(); Check(host.Subscribers == 0, "disposing_settings_unsubscribes_from_host_" + language);
                    string oldStatus = Named<TextBlock>(window, "ScreenUnderstandingStatus").Text;
                    host.Status = new("after disposal"); host.Notify();
                    Check(Named<TextBlock>(window, "ScreenUnderstandingStatus").Text == oldStatus,
                        "disposed_view_ignores_late_host_notifications_" + language);
                    window.Close(); window = null; view = null;
                }
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run(); return result;
    }

    private sealed class FakeHost : IScreenUnderstandingSettingsHost
    {
        public ScreenUnderstandingOptions Options { get; private set; } = new();
        public ScreenUnderstandingStatus Status { get; set; } = new(L.T("已关闭", "Disabled"));
        public event Action? Changed;
        public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        public int Updates, Requests, Corrections, Deletes, Clears;
        public bool FailSave;
        private readonly List<LearnedApplication> _memories = new()
        {
            new("fixture-game", "mygame", new string('a',64), "game", .95, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90), false),
            new("fixture-editor", "myeditor", new string('b',64), "work", .91, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90), false)
        };
        public void UpdateOptions(ScreenUnderstandingOptions options)
        { if (FailSave) throw new IOException("fixture"); Updates++; Options = options.Normalize(); Notify(); }
        public void RequestNextWindowAnalysis() { Requests++; Notify(); }
        public IReadOnlyList<LearnedApplication> GetMemories() => _memories.ToArray();
        public void UpdateMemoryCategory(string id, string category)
        { int index = _memories.FindIndex(item => item.Id == id); if (index >= 0) _memories[index] = _memories[index] with { Activity = category, UserCorrected = true }; Corrections++; Notify(); }
        public void DeleteMemory(string id) { _memories.RemoveAll(item => item.Id == id); Deletes++; Notify(); }
        public void ClearMemories() { _memories.Clear(); Clears++; Notify(); }
        internal void Notify() => Changed?.Invoke();
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Named<T>(Window window, string name) where T : FrameworkElement => Walk(window).OfType<T>().First(item => item.Name == name);
    private static void Click(Window window, string name) => Named<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle(Window window)
    { await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render); await Task.Delay(50); window.UpdateLayout(); }
    private static bool Fits(Window window, FrameworkElement element)
    { Point point = element.TransformToAncestor(window).Transform(new Point()); return element.IsVisible && point.X >= 0 && point.Y >= 0 && point.X + element.ActualWidth <= window.ActualWidth && point.Y + element.ActualHeight <= window.ActualHeight; }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
