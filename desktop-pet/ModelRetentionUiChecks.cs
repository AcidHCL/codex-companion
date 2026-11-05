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

// Tests preference round trips and real controls with synthetic telemetry only.
// Never creates an Ollama client, voice worker, chat store, or settings file.
internal static class ModelRetentionUiChecks
{
    internal static int Run()
    {
        string originalLanguage = L.Language;
        var checks = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        AppSettingsWindow? window = null; PetWindow? pet = null;
        int result = 1, loads = 0, unloads = 0;
        bool finished = false;
        string evidence = Path.Combine(AppContext.BaseDirectory, "model-retention-ui-evidence");
        Directory.CreateDirectory(evidence);
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            try { window?.Close(); pet?.Close(); }
            catch (Exception ex) { passed = false; error = "cleanup_" + ex.GetType().Name; }
            L.Initialize(originalLanguage);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "model-retention-ui-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, error,
                realAccountRequests = 0, realModelOperations = 0, userSettingsWrites = 0,
                audioOutput = false, productionDatabaseOpened = false, loads, unloads
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "model_retention_ui_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var legacy = JsonSerializer.Deserialize<DesktopSettings>("{}")!;
                legacy.Normalize();
                Check(legacy.OllamaKeepAliveMinutes == 5 && legacy.SpeechIdleUnloadMinutes == 0,
                    "legacy_settings_keep_five_minute_llm_and_chat_session_speech_defaults");
                foreach (int minutes in new[] { -1, 1, 5, 10, 30, 60 })
                {
                    var saved = new DesktopSettings { OllamaKeepAliveMinutes = minutes, SpeechIdleUnloadMinutes = minutes };
                    var restored = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(saved))!;
                    restored.Normalize();
                    Check(restored.OllamaKeepAliveMinutes == minutes && restored.SpeechIdleUnloadMinutes == minutes,
                        "both_retention_preferences_roundtrip_" + minutes);
                }
                legacy.OllamaKeepAliveMinutes = -99; legacy.SpeechIdleUnloadMinutes = int.MaxValue; legacy.Normalize();
                Check(legacy.OllamaKeepAliveMinutes == 5 && legacy.SpeechIdleUnloadMinutes == 0,
                    "invalid_persisted_durations_restore_safe_legacy_defaults");

                foreach (string language in new[] { L.Chinese, L.English })
                {
                    L.Initialize(language);
                    var settings = new DesktopSettings
                    {
                        Language = language, SkinId = "gemini", CodexEnabled = false,
                        Topmost = false, MotionEnabled = false, MovementEnabled = false,
                        SpeechEnabled = false, SharedMemoryEnabled = false, WebSearchEnabled = false
                    };
                    pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false, subscribeToSystemEvents: false);
                    var services = new DashboardModelServices(
                        (_, _) => Task.FromResult<IReadOnlyList<OllamaRunningModel>>(Array.Empty<OllamaRunningModel>()),
                        _ => Task.FromResult<PetVoiceHealth?>(null),
                        _ => Task.FromResult<IReadOnlyList<LocalGpuInfo>>(Array.Empty<LocalGpuInfo>()),
                        load => { if (load) loads++; else unloads++; return Task.FromResult("unexpected operation"); });
                    window = new AppSettingsWindow(pet, modelServices: services, voiceStudio: new EmptyStudio())
                    { ShowActivated = false, ShowInTaskbar = false };
                    window.Show();
                    Named<Button>(window, "SettingsNavModels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Settle(window);
                    var voice = Named<ComboBox>(window, "DashboardVoiceIdleUnload");
                    var llm = Named<ComboBox>(window, "DashboardLlmIdleUnload");
                    Check((int)voice.SelectedValue == 0 && (int)llm.SelectedValue == 5,
                        "controls_show_saved_default_policy_" + language);
                    Check(voice.Items.Count == 7 && llm.Items.Count == 6 && voice.IsEnabled && llm.IsEnabled,
                        "independent_duration_and_never_choices_are_available_even_before_models_load_" + language);
                    foreach (int minutes in new[] { -1, 1, 5, 10, 30, 60 })
                    {
                        llm.SelectedValue = minutes;
                        voice.SelectedValue = minutes;
                        Check(settings.OllamaKeepAliveMinutes == minutes && settings.SpeechIdleUnloadMinutes == minutes,
                            "selecting_durations_updates_shared_preferences_" + language + "_" + minutes);
                    }
                    voice.SelectedValue = 0;
                    Check(settings.SpeechIdleUnloadMinutes == 0 && settings.OllamaKeepAliveMinutes == 60,
                        "legacy_speech_mode_does_not_change_llm_policy_" + language);
                    pet.SetOllamaKeepAliveMinutes(10); pet.SetSpeechIdleUnloadMinutes(-1);
                    Check((int)llm.SelectedValue == 10 && (int)voice.SelectedValue == -1,
                        "external_preference_change_syncs_open_selectors_" + language);
                    Check(loads == 0 && unloads == 0 && !pet.IsSpeaking,
                        "saving_retention_does_not_load_unload_generate_or_speak_" + language);
                    string llmHint = Named<TextBlock>(window, "DashboardLlmIdleHint").Text;
                    string voiceHint = Named<TextBlock>(window, "DashboardVoiceIdleHint").Text;
                    Check(llmHint.Contains(L.T("下一次", "next model request")) &&
                        llmHint.Contains(L.T("不修改服务器全局设置", "without changing Ollama's global settings")) &&
                        voiceHint.Contains(L.T("手动卸载", "Manual unload")),
                        "policy_scope_and_manual_unload_are_explained_" + language);
                    Check(Named<TextBlock>(window, "DashboardVoiceIdleLabel").Text == L.T("空闲后自动卸载", "Unload after inactivity"),
                        "retention_labels_follow_app_language_" + language);
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    llm.BringIntoView(); await Settle(window);
                    Check(Fits(window, llm) && Fits(window, voice), "both_retention_selectors_fit_minimum_settings_window_" + language);
                    Capture(window, Path.Combine(evidence, "retention-settings-" + language + ".png"));
                    window.Close(); window = null; pet.Close(); pet = null;
                }
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run(); return result;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Named<T>(Window root, string name) where T : FrameworkElement => Walk(root).OfType<T>().First(x => x.Name == name);
    private static async Task Settle(Window window)
    { await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render); await Task.Delay(50); window.UpdateLayout(); }
    private static bool Fits(Window window, FrameworkElement element)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point());
        return element.IsVisible && origin.X >= 0 && origin.Y >= 0 &&
            origin.X + element.ActualWidth <= window.ActualWidth && origin.Y + element.ActualHeight <= window.ActualHeight;
    }
    private static void Capture(Window window, string file)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }
    private sealed class EmptyStudio : IPetVoiceStudio
    {
        public Task<PetVoiceDesignSample> GenerateAsync(string skinId, string instruction, string text, string language, CancellationToken ct) => throw new InvalidOperationException("No model operations in retention fixture");
        public Task PlayAsync(PetVoiceDesignSample sample, double volume, CancellationToken ct) => throw new InvalidOperationException("No audio in retention fixture");
        public void StopPlayback() { }
        public PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null) => throw new InvalidOperationException("No storage in retention fixture");
        public PetSavedVoiceInfo? ReadSavedVoice(string skinId) => null;
        public IReadOnlyList<PetSavedVoiceInfo> ListSavedVoices(string skinId) => Array.Empty<PetSavedVoiceInfo>();
        public PetSavedVoiceInfo RenameSavedVoice(string referenceId, string name) => throw new InvalidOperationException("No storage in retention fixture");
        public void DeleteSavedVoice(string referenceId) => throw new InvalidOperationException("No storage in retention fixture");
        public void Dispose() { }
    }
}
