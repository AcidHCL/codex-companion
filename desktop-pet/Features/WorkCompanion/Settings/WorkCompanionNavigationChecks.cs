using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet.WorkCompanion;

// Exercises the real settings shell through its generic page contribution.
// No module, sensor, Ollama client, voice worker, or user store is constructed.
internal static class WorkCompanionNavigationChecks
{
    internal static int Run()
    {
        string languageBefore = L.Language;
        var checks = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        AppSettingsWindow? window = null; PetWindow? pet = null;
        int result = 1, modelActions = 0, viewCreations = 0; bool finished = false;
        string evidence = Path.Combine(AppContext.BaseDirectory, "work-companion-navigation-evidence");
        Directory.CreateDirectory(evidence);
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            try { window?.Close(); pet?.Close(); }
            catch (Exception ex) { passed = false; error = "cleanup_" + ex.GetType().Name; }
            L.Initialize(languageBefore);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "work-companion-navigation-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, error, viewCreations,
                realAccountRequests = 0, realModelOperations = 0, userSettingsWrites = 0,
                realGpuQueries = 0, modelActions, audioOutput = false, observedDesktop = false,
                productionDatabaseOpened = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "work_companion_navigation_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                foreach (string language in new[] { L.Chinese, L.English })
                {
                    L.Initialize(language);
                    var settings = new DesktopSettings
                    {
                        Language = language, SkinId = "gemini", CodexEnabled = false,
                        Topmost = false, MotionEnabled = false, MovementEnabled = false,
                        SpeechEnabled = false, SharedMemoryEnabled = false, WebSearchEnabled = false,
                        OllamaAddress = OllamaEndpoint.DefaultAddress, OllamaModel = "fixture:latest"
                    };
                    pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false, subscribeToSystemEvents: false);
                    var services = new DashboardModelServices(
                        (_, _) => Task.FromResult<IReadOnlyList<OllamaRunningModel>>(Array.Empty<OllamaRunningModel>()),
                        _ => Task.FromResult<PetVoiceHealth?>(null),
                        _ => Task.FromResult<IReadOnlyList<LocalGpuInfo>>(Array.Empty<LocalGpuInfo>()),
                        _ => { modelActions++; return Task.FromResult("unexpected model operation"); },
                        ReadInstalledModels: (_, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "fixture:latest" }),
                        SelectLlmModel: _ => { modelActions++; return Task.FromResult("unexpected model switch"); },
                        ReadOllamaActivity: (_, _) => Task.FromResult(new OllamaGpuActivity(false,
                            L.T("测试环境：未读取实际 GPU。", "Test fixture: no real GPU query."), null,
                            Array.Empty<OllamaGpuProcess>(), DateTimeOffset.UtcNow)),
                        ReadOwnActivity: _ => new OllamaRequestSnapshot(0, 0, null, null));
                    var host = new FakeHost();
                    WorkCompanionSettingsView? featureView = null;
                    int creationsBefore = viewCreations;
                    window = new AppSettingsWindow(pet, modelServices: services, voiceStudio: new EmptyStudio(),
                        extraPages: new[] { new PetSettingsPage("WorkCompanion", L.T("主动陪伴", "Proactive companion"), () =>
                        { viewCreations++; return featureView = new WorkCompanionSettingsView(host); }) })
                    { ShowActivated = false, ShowInTaskbar = false };
                    window.Show(); await Settle(window);
                    Check(featureView != null && featureView.Visibility == Visibility.Collapsed && host.SubscriberCount == 1,
                        "optional_page_is_mounted_once_and_starts_hidden_" + language);
                    Check(Named<GeneralSettingsView>(window, "GeneralSettingsView").IsVisible,
                        "general_settings_remains_default_page_" + language);
                    var nav = Named<Button>(window, "SettingsNavWorkCompanion");
                    Check(nav.Content?.ToString() == L.T("主动陪伴", "Proactive companion") &&
                        AutomationProperties.GetName(nav) == nav.Content?.ToString(), "feature_navigation_is_localized_and_accessible_" + language);
                    Click(window, "SettingsNavWorkCompanion"); await Settle(window);
                    Check(featureView!.IsVisible && !Named<GeneralSettingsView>(window, "GeneralSettingsView").IsVisible &&
                        !Walk(window).OfType<ModelSettingsView>().Single().IsVisible,
                        "feature_navigation_selects_only_the_feature_page_" + language);
                    Check(AutomationProperties.GetItemStatus(nav) == L.T("当前页面", "Current page") &&
                        AutomationProperties.GetItemStatus(Named<Button>(window, "SettingsNavGeneral")) == "",
                        "selected_navigation_marker_moves_to_feature_" + language);

                    var draft = Named<TextBox>(window, "WorkCompanionExcludedApps"); draft.Text = "unsaved-browser";
                    Click(window, "SettingsNavModels"); await Settle(window);
                    Check(Walk(window).OfType<ModelSettingsView>().Single().IsVisible && !featureView.IsVisible,
                        "existing_models_page_is_still_reachable_" + language);
                    Check(host.SubscriberCount == 1, "hiding_feature_does_not_dispose_its_page_" + language);
                    Click(window, "SettingsNavGeneral"); await Settle(window);
                    Check(Named<GeneralSettingsView>(window, "GeneralSettingsView").IsVisible && !featureView.IsVisible,
                        "existing_general_page_is_still_reachable_" + language);
                    Click(window, "SettingsNavWorkCompanion"); await Settle(window);
                    Check(viewCreations == creationsBefore + 1 && draft.Text == "unsaved-browser" && host.Updates == 0,
                        "navigation_preserves_feature_draft_without_saving_or_recreating_" + language);
                    Check(modelActions == 0 && !pet.IsSpeaking, "navigation_never_loads_switches_generates_or_speaks_" + language);

                    Named<CheckBox>(window, "WorkCompanionEnabled").IsChecked = true;
                    Check(host.Options.Enabled && !settings.CodexEnabled && !settings.SpeechEnabled && !settings.MovementEnabled &&
                        settings.OllamaModel == "fixture:latest", "feature_controls_update_only_the_injected_host_" + language);
                    host.SetStatus(new WorkCompanionStatus(L.T("等待已加载的聊天模型", "Waiting for an already loaded chat model"),
                        L.T("模型未加载时保持安静，不会自动加载。", "Stays quiet while the chat model is unloaded; never loads one automatically.")));
                    await Settle(window);
                    string suffix = language == L.Chinese ? "zh" : "en";
                    window.SavePreview(Path.Combine(evidence, "work-companion-navigation-" + suffix + ".png"));
                    window.Width = window.MinWidth; window.Height = window.MinHeight; await Settle(window);
                    Check(FitsWidth(window, nav) && FitsWidth(window, featureView) &&
                        FitsWidth(window, Named<CheckBox>(window, "WorkCompanionEnabled")),
                        "feature_navigation_and_content_fit_minimum_settings_width_" + language);
                    var scroll = (ScrollViewer)featureView.Content;
                    Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                        "minimum_width_needs_no_horizontal_scrolling_" + language);
                    var contextControl = Named<CheckBox>(window, "WorkCompanionWorkEnabled");
                    var contextCard = Walk(window).OfType<Border>().Single(border => border.Child is Panel panel && panel.Children.Contains(contextControl));
                    contextCard.BringIntoView(); await Settle(window);
                    Check(new[] { "WorkCompanionWorkEnabled", "WorkCompanionGameEnabled", "WorkCompanionMediaEnabled", "WorkCompanionDailyEnabled", "WorkCompanionQuietFullscreen" }
                        .All(name => Fits(window, Named<CheckBox>(window, name))),
                        "four_contexts_and_fullscreen_control_are_reachable_at_minimum_size_" + language);
                    window.SavePreview(Path.Combine(evidence, "companion-contexts-minimum-" + suffix + ".png"));

                    host.UpdateOptions(host.Options with { AppActivities = new Dictionary<string, string>
                    { ["mygame"] = "game", ["chrome"] = "daily", ["vlc"] = "media", ["code"] = "work" } });
                    var mappingList = Named<ListBox>(window, "WorkCompanionActivityList");
                    mappingList.SelectedItem = mappingList.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, "mygame"));
                    var mappingCard = Walk(window).OfType<Border>().Single(border => border.Child is Panel panel && panel.Children.Contains(mappingList));
                    mappingCard.BringIntoView(); await Settle(window);
                    Check(Fits(window, Named<TextBox>(window, "WorkCompanionActivityApp")) && Fits(window, Named<ComboBox>(window, "WorkCompanionActivityChoice")) &&
                        Fits(window, mappingList) && Fits(window, Named<Button>(window, "WorkCompanionSaveActivity")) && Fits(window, Named<Button>(window, "WorkCompanionRemoveActivity")),
                        "mapping_editor_and_saved_contexts_fit_minimum_settings_size_" + language);
                    Check(Named<TextBox>(window, "WorkCompanionActivityApp").Text == "mygame" &&
                        Named<ComboBox>(window, "WorkCompanionActivityChoice").SelectedValue as string == "game",
                        "selected_mapping_populates_editable_app_and_context_" + language);
                    window.SavePreview(Path.Combine(evidence, "companion-app-contexts-minimum-" + suffix + ".png"));
                    Named<Button>(window, "WorkCompanionSaveExclusions").BringIntoView(); await Settle(window);
                    Check(Fits(window, draft) && Fits(window, Named<Button>(window, "WorkCompanionSaveExclusions")),
                        "bottom_controls_remain_reachable_at_minimum_window_size_" + language);
                    window.SavePreview(Path.Combine(evidence, "work-companion-navigation-minimum-" + suffix + ".png"));

                    var summary = Named<TextBlock>(window, "WorkCompanionStatus"); string lastSummary = summary.Text;
                    window.Close(); window = null;
                    Check(host.SubscriberCount == 0, "closing_settings_disposes_optional_page_and_unsubscribes_host_" + language);
                    host.SetStatus(new WorkCompanionStatus("after close"));
                    Check(summary.Text == lastSummary, "closed_feature_page_receives_no_late_updates_" + language);
                    featureView.Dispose(); Check(host.SubscriberCount == 0, "feature_disposal_is_idempotent_" + language);
                    pet.Close(); pet = null;
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
        public WorkCompanionStatus Status { get; private set; } = new(L.T("已关闭", "Disabled"),
            L.T("开启后才会开始轻量观察。", "Lightweight observation starts only after you enable it."));
        public event Action? Changed;
        internal int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;
        internal int Updates;
        public void UpdateOptions(WorkCompanionOptions options) { Updates++; Options = options.Normalize(); Changed?.Invoke(); }
        public void SetPaused(bool paused) { Status = Status with { Paused = paused }; Changed?.Invoke(); }
        public void ClearTransientState() { Status = Status with { LastReaction = null }; Changed?.Invoke(); }
        internal void SetStatus(WorkCompanionStatus status) { Status = status; Changed?.Invoke(); }
    }

    private sealed class EmptyStudio : IPetVoiceStudio
    {
        private static InvalidOperationException Unexpected() => new("No model, voice, or storage operations in navigation fixture");
        public Task<PetVoiceDesignSample> GenerateAsync(string skinId, string instruction, string text, string language, CancellationToken ct) => throw Unexpected();
        public Task PlayAsync(PetVoiceDesignSample sample, double volume, CancellationToken ct) => throw Unexpected();
        public void StopPlayback() { }
        public PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null) => throw Unexpected();
        public PetSavedVoiceInfo? ReadSavedVoice(string skinId) => null;
        public IReadOnlyList<PetSavedVoiceInfo> ListSavedVoices(string skinId) => Array.Empty<PetSavedVoiceInfo>();
        public PetSavedVoiceInfo RenameSavedVoice(string referenceId, string name) => throw Unexpected();
        public void DeleteSavedVoice(string referenceId) => throw Unexpected();
        public void Dispose() { }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Named<T>(Window root, string name) where T : FrameworkElement => Walk(root).OfType<T>().First(x => x.Name == name);
    private static void Click(Window root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle(Window window)
    { await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render); await Task.Delay(60); window.UpdateLayout(); }
    private static bool FitsWidth(Window window, FrameworkElement element)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point());
        return element.IsVisible && element.ActualWidth > 0 && origin.X >= 0 && origin.X + element.ActualWidth <= window.ActualWidth;
    }
    private static bool Fits(Window window, FrameworkElement element)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point());
        return FitsWidth(window, element) && origin.Y >= 0 && origin.Y + element.ActualHeight <= window.ActualHeight;
    }
}
