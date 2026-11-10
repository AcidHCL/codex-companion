using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// Synthetic request and GPU data only. No inference, model loading, or user storage.
internal static class OllamaActivityUiChecks
{
    internal static int Run()
    {
        string originalLanguage = L.Language;
        var checks = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(55) };
        AppSettingsWindow? window = null; PetWindow? pet = null;
        int result = 1, loads = 0, unloads = 0; bool finished = false;
        string evidence = Path.Combine(AppContext.BaseDirectory, "ollama-activity-ui-evidence");
        Directory.CreateDirectory(evidence);
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            try { window?.Close(); pet?.Close(); }
            catch (Exception ex) { passed = false; error = "cleanup_" + ex.GetType().Name; }
            L.Initialize(originalLanguage);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ollama-activity-ui-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, error,
                realAccountRequests = 0, realModelOperations = 0, userSettingsWrites = 0,
                realGpuQueries = 0, audioOutput = false, productionDatabaseOpened = false, loads, unloads
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "ollama_activity_ui_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                CheckClassifier(Check);
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
                    int samples = 0; bool unavailable = false, modelError = false;
                    DateTimeOffset start = DateTimeOffset.UtcNow.AddSeconds(-10);
                    OllamaRequestSnapshot own = new(0, 0, null, null);
                    var services = new DashboardModelServices(
                        (_, _) => modelError ? throw new InvalidOperationException("fixture status unavailable") :
                            Task.FromResult<IReadOnlyList<OllamaRunningModel>>(new[] { new OllamaRunningModel(settings.OllamaModel, 18L << 30, 18L << 30, 8192, DateTimeOffset.Now.AddMinutes(5)) }),
                        _ => Task.FromResult<PetVoiceHealth?>(null),
                        _ => Task.FromResult<IReadOnlyList<LocalGpuInfo>>(new[] { new LocalGpuInfo("Fixture GPU", 32L << 30, 22L << 30, 98) }),
                        load => { if (load) loads++; else unloads++; return Task.FromResult("unexpected operation"); },
                        ReadOllamaActivity: (_, _) =>
                        {
                            DateTimeOffset at = samples < 3 ? start.AddSeconds(samples * 5) : DateTimeOffset.UtcNow;
                            samples++;
                            return Task.FromResult(unavailable
                                ? new OllamaGpuActivity(false, L.T("进程 GPU 计数器不可用。", "Process GPU counters are unavailable."), null, Array.Empty<OllamaGpuProcess>(), at)
                                : Sample(at, 90));
                        },
                        ReadOwnActivity: _ => own);
                    window = new AppSettingsWindow(pet, modelServices: services, voiceStudio: new EmptyStudio())
                    { ShowActivated = false, ShowInTaskbar = false };
                    window.Show(); await Settle(window);
                    Check(samples == 0, "general_settings_does_not_sample_gpu_" + language);
                    Click(window, "SettingsNavModels"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("检测到 GPU 活动 · 继续观察", "GPU activity detected · Observing"), "first_busy_sample_is_observation_only_" + language);
                    Click(window, "ModelRefresh"); await Settle(window);
                    Click(window, "ModelRefresh"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("疑似外部推理", "Possible external inference"), "sustained_gpu_without_own_requests_shows_suspected_external_" + language);
                    var details = Named<Expander>(window, "DashboardOllamaActivityDetails");
                    Check(!details.IsExpanded, "technical_detection_details_are_collapsed_by_default_" + language);
                    details.IsExpanded = true; await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityGpu").Contains("90%") && Text(window, "DashboardOllamaActivityProcesses").Contains("PID 1234"),
                        "activity_card_reports_process_percent_and_pid_" + language);
                    Check(Text(window, "DashboardOllamaActivityScope").Contains(L.T("整个 Ollama 服务", "entire Ollama service")) &&
                        Text(window, "DashboardOllamaActivityHint").Contains(L.T("不会自动加载或卸载", "never loads or unloads")),
                        "scope_and_no_automatic_model_actions_are_explained_" + language);
                    var activityCard = Named<Border>(window, "DashboardOllamaActivityCard");
                    activityCard.BringIntoView(); await Settle(window);
                    Capture(window, Path.Combine(evidence, "ollama-activity-" + language + ".png"));
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    activityCard.BringIntoView(); await Settle(window);
                    Check(FitsWidth(window, activityCard) && FitsWidth(window, Named<TextBlock>(window, "DashboardOllamaActivityHint")),
                        "activity_card_wraps_at_minimum_settings_width_" + language);
                    Capture(window, Path.Combine(evidence, "ollama-activity-minimum-" + language + ".png"));

                    own = new(2, 0, null, null); Click(window, "ModelRefresh"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("桌宠正在请求", "Pet request in progress") &&
                        Text(window, "DashboardOllamaActivityOwn").Contains("2"), "own_inflight_requests_are_counted_without_claiming_exclusive_use_" + language);
                    own = new(0, 1, null, null); Click(window, "ModelRefresh"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("桌宠正在加载或卸载", "Pet is loading or unloading"), "maintenance_is_not_labeled_external_" + language);
                    own = new(0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30)); Click(window, "ModelRefresh"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("请求刚结束 · 等待稳定", "Request ended · Settling"), "post_request_grace_is_visible_" + language);
                    own = new(0, 0, null, null); unavailable = true; Click(window, "ModelRefresh"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("无法判断", "Unable to determine") &&
                        !Text(window, "DashboardOllamaActivityGpu").Contains("90%") && Text(window, "DashboardOllamaActivityProcesses") == "",
                        "missing_counters_clear_stale_percent_and_processes_" + language);
                    unavailable = false; modelError = true; Click(window, "ModelRefresh"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("无法判断", "Unable to determine"), "ollama_connection_failure_cannot_label_external_" + language);
                    modelError = false; settings.OllamaAddress = "http://192.0.2.10:11434";
                    int beforeRemote = samples; Click(window, "ModelRefresh"); await Settle(window);
                    Check(samples == beforeRemote && Text(window, "DashboardOllamaActivityState") == L.T("无法判断", "Unable to determine") &&
                        Text(window, "DashboardOllamaActivityDetail").Contains(L.T("远程", "remote")), "remote_server_never_uses_local_gpu_as_evidence_" + language);
                    settings.OllamaAddress = OllamaEndpoint.DefaultAddress; Click(window, "ModelRefresh"); await Settle(window);
                    Check(Text(window, "DashboardOllamaActivityState") == L.T("检测到 GPU 活动 · 继续观察", "GPU activity detected · Observing"), "endpoint_change_resets_observation_" + language);
                    int beforeHidden = samples; window.Hide(); await Task.Delay(5200);
                    Check(samples == beforeHidden, "hidden_settings_do_not_poll_process_gpu_" + language);
                    window.Show(); await Settle(window);
                    Check(samples > beforeHidden && Text(window, "DashboardOllamaActivityState") == L.T("检测到 GPU 活动 · 继续观察", "GPU activity detected · Observing"), "showing_settings_starts_fresh_observation_" + language);
                    Check(loads == 0 && unloads == 0 && !pet.IsSpeaking, "activity_display_never_loads_unloads_or_speaks_" + language);
                    window.Close(); window = null; pet.Close(); pet = null;
                }
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }

    private static OllamaGpuActivity Sample(DateTimeOffset at, double value, int server = 20) =>
        new(true, null, value, new[] { new OllamaGpuProcess(1234, "llama-server.exe", value) }, at) { ServerProcessId = server };

    private static void CheckClassifier(Action<bool, string> check)
    {
        var classifier = new OllamaActivityClassifier();
        var own = new OllamaRequestSnapshot(0, 0, null, null);
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        OllamaActivityState At(int second, double value = 90, OllamaRequestSnapshot? request = null, int server = 20, string endpoint = "local")
        { var at = now.AddSeconds(second); return classifier.Evaluate(endpoint, 1, Sample(at, value, server), request ?? own, at).State; }
        check(At(0) == OllamaActivityState.Observing && At(5) == OllamaActivityState.Observing && At(10) == OllamaActivityState.SuspectedExternal,
            "classifier_requires_three_spaced_busy_observations");
        check(At(15, 0) == OllamaActivityState.Quiet && At(20) == OllamaActivityState.Observing, "quiet_sample_resets_external_sequence");
        classifier.Reset();
        check(At(0) == OllamaActivityState.Observing && At(1) == OllamaActivityState.Observing && At(2) == OllamaActivityState.Observing,
            "rapid_manual_refresh_does_not_fake_sustained_load");
        classifier.Reset(); At(0); At(5);
        check(At(10, request: new(1, 0, null, null)) == OllamaActivityState.OwnRequest && At(15) == OllamaActivityState.Observing,
            "own_request_interrupts_external_evidence");
        check(At(20, request: new(0, 1, null, null)) == OllamaActivityState.Maintenance, "preload_and_unload_are_maintenance");
        check(At(25, request: new(0, 0, now.AddSeconds(24), now.AddSeconds(54))) == OllamaActivityState.Settling,
            "canceled_request_grace_prevents_external_label");
        classifier.Reset(); At(0); At(5);
        check(At(10, server: 21) == OllamaActivityState.Observing, "server_restart_resets_sequence");
        classifier.Reset(); At(0); At(5);
        check(At(10, endpoint: "another") == OllamaActivityState.Observing, "endpoint_change_resets_sequence");
        classifier.Reset(); At(0); At(5);
        check(At(30) == OllamaActivityState.Observing, "long_sampling_gap_resets_sequence");
        check(classifier.Evaluate("local", 1, Sample(now, 90), own, now.AddSeconds(20)).State == OllamaActivityState.Unknown,
            "stale_gpu_sample_is_unknown");
        check(classifier.Evaluate("local", 1, new(false, "missing", null, Array.Empty<OllamaGpuProcess>(), now), own, now).State == OllamaActivityState.Unknown,
            "missing_counter_is_not_zero_or_external");
        check(classifier.Evaluate("local", 1, new(true, null, 90, Array.Empty<OllamaGpuProcess>(), now), own, now).State == OllamaActivityState.Unknown,
            "whole_gpu_without_matched_process_cannot_claim_external");
        check(classifier.Evaluate("local", 1, Sample(now, double.NaN), own, now).State == OllamaActivityState.Unknown,
            "invalid_counter_value_is_unknown");
        check(classifier.Evaluate("local", null, Sample(now, 90), own, now).State == OllamaActivityState.Unknown,
            "unavailable_model_list_is_unknown");
        check(classifier.Evaluate("local", 0, Sample(now, 0), own, now).State == OllamaActivityState.Unloaded,
            "no_resident_models_is_distinct_from_quiet");
        check(classifier.Evaluate("local", 1, Sample(now, 90), null, now).State == OllamaActivityState.Unknown,
            "missing_own_request_tracking_cannot_claim_external");
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Named<T>(Window root, string name) where T : FrameworkElement => Walk(root).OfType<T>().First(x => x.Name == name);
    private static string Text(Window root, string name) => Named<TextBlock>(root, name).Text;
    private static void Click(Window root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle(Window window)
    { await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render); await Task.Delay(80); window.UpdateLayout(); }
    private static bool FitsWidth(Window window, FrameworkElement element)
    { Point origin = element.TransformToAncestor(window).Transform(new Point()); return element.IsVisible && origin.X >= 0 && origin.X + element.ActualWidth <= window.ActualWidth; }
    private static void Capture(Window window, string file)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }
    private sealed class EmptyStudio : IPetVoiceStudio
    {
        public Task<PetVoiceDesignSample> GenerateAsync(string skinId, string instruction, string text, string language, CancellationToken ct) => throw new InvalidOperationException("No model operations in activity fixture");
        public Task PlayAsync(PetVoiceDesignSample sample, double volume, CancellationToken ct) => throw new InvalidOperationException("No audio in activity fixture");
        public void StopPlayback() { }
        public PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null) => throw new InvalidOperationException("No storage in activity fixture");
        public PetSavedVoiceInfo? ReadSavedVoice(string skinId) => null;
        public IReadOnlyList<PetSavedVoiceInfo> ListSavedVoices(string skinId) => Array.Empty<PetSavedVoiceInfo>();
        public PetSavedVoiceInfo RenameSavedVoice(string referenceId, string name) => throw new InvalidOperationException("No storage in activity fixture");
        public void DeleteSavedVoice(string referenceId) => throw new InvalidOperationException("No storage in activity fixture");
        public void Dispose() { }
    }
}
