using System;
using System.Collections.Generic;
using System.Globalization;
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

// Entirely synthetic UI workflow: no production settings, model loading,
// account requests, speech playback, or voice-library filesystem writes.
internal static class LocalizationUiChecks
{
    internal static int Run()
    {
        L.Initialize(L.Chinese);
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string evidence = Path.Combine(AppContext.BaseDirectory, "localization-ui-previews");
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        PetWindow? pet = null; AppSettingsWindow? settingsWindow = null; SettingsWindow? wardrobe = null;
        bool finished = false; int result = 1, modelOperations = 0, restartRequests = 0;
        void RestartFixture()
        {
            restartRequests++;
            if (restartRequests == 1) throw new IOException("Fixture save failure");
        }
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            settingsWindow?.Close(); wardrobe?.Close(); pet?.Close();
            L.Initialize(L.Chinese);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "localization-ui-validation.json"), JsonSerializer.Serialize(new
            { passed, assertions = checks.Count, checks, error, modelOperations, userSettingsWrites = 0, audioOutput = false }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "LOCALIZATION_UI_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var settings = new DesktopSettings { Language = L.Chinese, SkinId = "gemini", CodexEnabled = false,
                    SpeechEnabled = false, Topmost = false, MotionEnabled = false, MovementEnabled = false,
                    WebSearchEnabled = false, SharedMemoryEnabled = false };
                settings.SetVoiceProfile("gpt", new("gpt-fixed", "Vivian", "Japanese", ""));
                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                pet = new PetWindow(skins, settings, persist: false, subscribeToSystemEvents: false);
                var services = new DashboardModelServices(
                    (_, _) => Task.FromResult<IReadOnlyList<OllamaRunningModel>>(Array.Empty<OllamaRunningModel>()),
                    _ => Task.FromResult<PetVoiceHealth?>(null),
                    _ => Task.FromResult<IReadOnlyList<LocalGpuInfo>>(new[] { new LocalGpuInfo("Fixture GPU", 32L << 30, 4L << 30, 12) }),
                    SetLlmLoaded: _ => { modelOperations++; throw new InvalidOperationException("No inference in localization fixture"); },
                    ReadInstalledModels: (_, _) => Task.FromResult<IReadOnlyList<string>>(new[] { settings.OllamaModel }));
                settingsWindow = new AppSettingsWindow(pet, modelServices: services, voiceStudio: new StudioFixture(), restart: RestartFixture) { ShowActivated = false, ShowInTaskbar = false };
                settingsWindow.Show(); await Settle();
                Click(settingsWindow, "SettingsNavModels"); await Settle();
                Named<ComboBox>(settingsWindow, "DashboardVoiceCharacter").SelectedValue = "gpt";
                Named<TextBox>(settingsWindow, "VoiceStudioName").Text = "我的 voice draft";
                Named<TextBox>(settingsWindow, "VoiceStudioInstruction").Text = "Keep this custom prompt exactly.";
                Click(settingsWindow, "SettingsNavGeneral"); await Settle();
                Named<ComboBox>(settingsWindow, "AppLanguage").SelectedIndex = 1;
                Require(settings.Language == L.English && !L.IsEnglish, "language_choice_saves_preference_without_interrupting_current_process");
                Require(Named<TextBlock>(settingsWindow, "AppLanguageStatus").Text.Contains("Restart AI Pet"), "language_change_explains_restart_requirement");
                Click(settingsWindow, "SettingsNavModels"); await Settle();
                Require(Named<TextBox>(settingsWindow, "VoiceStudioName").Text == "我的 voice draft" &&
                    Named<TextBox>(settingsWindow, "VoiceStudioInstruction").Text == "Keep this custom prompt exactly.", "language_choice_keeps_unsaved_voice_draft_and_page_state");
                var serialized = JsonSerializer.Serialize(settings);
                Require(JsonSerializer.Deserialize<DesktopSettings>(serialized)?.Language == L.English, "language_preference_survives_settings_serialization");
                Click(settingsWindow, "SettingsNavGeneral"); await Settle();
                Require(Named<Button>(settingsWindow, "RestartPet").Content as string == "重启桌宠", "chinese_restart_button_label");
                Click(settingsWindow, "RestartPet");
                Require(restartRequests == 1 && Named<Button>(settingsWindow, "RestartPet").IsEnabled &&
                    Named<ComboBox>(settingsWindow, "AppLanguage").IsEnabled &&
                    Named<TextBlock>(settingsWindow, "AppLanguageStatus").Text.Contains("重启失败"), "failed_restart_leaves_language_controls_usable");
                Click(settingsWindow, "RestartPet"); Click(settingsWindow, "RestartPet");
                Require(restartRequests == 2 && !Named<Button>(settingsWindow, "RestartPet").IsEnabled &&
                    !Named<ComboBox>(settingsWindow, "AppLanguage").IsEnabled, "restart_dispatches_once_and_prevents_duplicate_clicks");
                settingsWindow.Close(); settingsWindow = null;

                // Simulate the next startup without restarting or touching the installed pet.
                L.Initialize(settings.Language);
                var studio = new StudioFixture();
                settingsWindow = new AppSettingsWindow(pet, modelServices: services, voiceStudio: studio, restart: RestartFixture)
                    { Width = 1060, Height = 720, ShowActivated = false, ShowInTaskbar = false };
                settingsWindow.Show(); await Settle();
                Require(L.IsEnglish && settingsWindow.Title.StartsWith("AI Pet", StringComparison.Ordinal) && settingsWindow.Title.Contains("Settings"), "next_startup_uses_english_settings_window");
                Require(Named<ComboBox>(settingsWindow, "AppLanguage").SelectedIndex == 1 &&
                    !Named<TextBlock>(settingsWindow, "AppLanguageStatus").Text.Contains("Language saved."), "restored_language_selection_has_no_stale_restart_notice");
                Require(Fits(settingsWindow, Named<ComboBox>(settingsWindow, "AppLanguage")), "language_picker_fits_minimum_settings_window");
                Require(Named<Button>(settingsWindow, "RestartPet").Content as string == "Restart AI Pet" &&
                    Fits(settingsWindow, Named<Button>(settingsWindow, "RestartPet")), "english_restart_button_fits_beside_language_picker");
                Capture(settingsWindow, Path.Combine(evidence, "english-general-minimum.png"));
                Click(settingsWindow, "SettingsNavModels"); await Settle();
                Named<ComboBox>(settingsWindow, "DashboardVoiceCharacter").SelectedValue = "gpt"; await Settle();
                Require(Named<ComboBox>(settingsWindow, "DashboardVoiceLanguage").SelectedValue as string == "Japanese",
                    "legacy_explicit_builtin_language_is_shown_without_auto_migration");
                Click(settingsWindow, "VoiceLibraryApply");
                Require(settings.GetVoiceProfile("gpt").Language == "Japanese" && !PetVoiceProfiles.FollowsAppLanguage(settings.GetVoiceProfile("gpt")),
                    "reapplying_same_builtin_voice_preserves_explicit_language");
                Named<ComboBox>(settingsWindow, "DashboardVoiceCharacter").SelectedValue = "deepseek"; await Settle();
                var list = Named<ListBox>(settingsWindow, "VoiceLibraryList");
                Require(Named<ComboBox>(settingsWindow, "DashboardVoiceLanguage").SelectedValue as string == "auto", "builtin_voice_defaults_follow_application_language");
                Require(Named<ComboBox>(settingsWindow, "VoiceStudioLanguage").SelectedItem as string == "English" &&
                    !ContainsHan(Named<TextBox>(settingsWindow, "VoiceStudioText").Text), "new_voice_studio_draft_uses_english_language_and_audition_text");
                foreach (string builtin in new[] { "builtin:base", "builtin:gpt-fixed" })
                {
                    list.SelectedValue = builtin;
                    Require(!Named<Button>(settingsWindow, "VoiceLibraryDelete").IsEnabled && !Named<Button>(settingsWindow, "VoiceLibraryRename").IsEnabled,
                        "english_library_protects_" + builtin);
                }
                list.SelectedValue = StudioFixture.Reference;
                Require(Named<TextBox>(settingsWindow, "VoiceLibraryName").Text == StudioFixture.CustomName, "english_ui_keeps_user_voice_name_unchanged");
                Click(settingsWindow, "VoiceLibraryApply");
                var custom = settings.GetVoiceProfile("deepseek");
                Require(custom.ReferenceId == StudioFixture.Reference && custom.Language == "Japanese" && custom.FollowAppLanguage == false,
                    "selecting_custom_voice_preserves_explicit_saved_language");
                Named<ComboBox>(settingsWindow, "DashboardVoiceLanguage").SelectedValue = "auto";
                Click(settingsWindow, "DashboardVoiceProfileSave");
                Require(settings.GetVoiceProfile("deepseek").FollowAppLanguage == true &&
                    PetVoiceProfiles.ResolveForPlayback(settings.GetVoiceProfile("deepseek")).Language == "English",
                    "explicit_auto_language_changes_playback_without_changing_reference_identity");
                Named<ComboBox>(settingsWindow, "DashboardVoiceLanguage").SelectedValue = "Japanese";
                Click(settingsWindow, "DashboardVoiceProfileSave");
                Require(settings.GetVoiceProfile("deepseek").FollowAppLanguage == false &&
                    PetVoiceProfiles.ResolveForPlayback(settings.GetVoiceProfile("deepseek")).Language == "Japanese",
                    "explicit_speech_language_overrides_english_interface");
                Named<Button>(settingsWindow, "DashboardVoiceProfileSave").BringIntoView(); await Settle();
                Require(Fits(settingsWindow, Named<ComboBox>(settingsWindow, "DashboardVoiceLanguage")), "speech_language_choice_fits_minimum_settings_width");
                Require(LabelFits(Named<Button>(settingsWindow, "DashboardVoiceProfileSave")), "english_apply_button_text_fits");
                Capture(settingsWindow, Path.Combine(evidence, "english-voice-profile-minimum.png"));
                Named<Button>(settingsWindow, "VoiceStudioSave").BringIntoView(); await Settle();
                foreach (string name in new[] { "VoiceStudioGenerate", "VoiceStudioPlay", "VoiceStudioStop", "VoiceStudioCancel", "VoiceStudioSave" })
                    Require(LabelFits(Named<Button>(settingsWindow, name)), "english_studio_button_fits_" + name);
                Capture(settingsWindow, Path.Combine(evidence, "english-voice-studio-minimum.png"));
                Named<Button>(settingsWindow, "VoiceLibraryApply").BringIntoView(); await Settle();
                Named<TextBox>(settingsWindow, "VoiceLibraryName").Text = "Renamed 声音";
                Click(settingsWindow, "VoiceLibraryRename");
                Require(studio.Voice?.Name == "Renamed 声音" && settings.GetVoiceProfile("deepseek").ReferenceId == StudioFixture.Reference,
                    "rename_keeps_custom_reference_and_accepts_mixed_language_names");
                var models = Walk(settingsWindow).OfType<ModelSettingsView>().Single();
                models.DeleteSelectedSavedVoice(confirmed: true);
                Require(studio.Deletes == 1 && settings.GetVoiceProfile("deepseek").ModelId == "base" &&
                    PetVoiceProfiles.ResolveForPlayback(settings.GetVoiceProfile("deepseek")).Language == "English",
                    "deleting_custom_voice_restores_protected_english_default");
                Require(modelOperations == 0 && !pet.IsSpeaking, "localization_and_voice_selection_never_load_models_or_play_audio");

                wardrobe = new SettingsWindow(pet, skins, openSettings: () => { })
                    { Width = 930, Height = 760, ShowActivated = false, ShowInTaskbar = false };
                wardrobe.Show(); await Settle();
                Require(wardrobe.Title.Contains("Character wardrobe") && LabelFits(Named<Button>(wardrobe, "OpenSettings")), "english_wardrobe_opens_and_settings_control_fits");
                Capture(wardrobe, Path.Combine(evidence, "english-wardrobe-minimum.png"));
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }

    private static bool ContainsHan(string text) => text.Any(c => c is >= '\u4e00' and <= '\u9fff');
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Named<T>(Window root, string name) where T : FrameworkElement => Walk(root).OfType<T>().First(x => x.Name == name);
    private static void Click(Window root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle() { await Task.Delay(80); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static bool Fits(Window window, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        return element.IsVisible && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1;
    }
    private static bool LabelFits(Button button)
    {
        var text = new FormattedText(button.Content?.ToString() ?? "", CultureInfo.CurrentUICulture, button.FlowDirection,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, button.Foreground,
            VisualTreeHelper.GetDpi(button).PixelsPerDip);
        return button.ActualWidth + 1 >= text.WidthIncludingTrailingWhitespace + button.Padding.Left + button.Padding.Right;
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }

    private sealed class StudioFixture : IPetVoiceStudio
    {
        internal const string Reference = "0123456789abcdef0123456789abcdef";
        internal const string CustomName = "我的声音 [keep]";
        internal PetSavedVoiceInfo? Voice = new(Reference, "deepseek", "Keep custom prompt", "Keep custom text", "Japanese",
            "fixture-design", "fixture-base", "fixture", DateTimeOffset.UtcNow, 5, CustomName);
        internal int Deletes;
        public Task<PetVoiceDesignSample> GenerateAsync(string skinId, string instruction, string text, string language, CancellationToken ct) => throw new InvalidOperationException("No generation in fixture");
        public Task PlayAsync(PetVoiceDesignSample sample, double volume, CancellationToken ct) => throw new InvalidOperationException("No audio in fixture");
        public void StopPlayback() { }
        public PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null) => throw new InvalidOperationException("No persistence in fixture");
        public PetSavedVoiceInfo? ReadSavedVoice(string skinId) => null;
        public IReadOnlyList<PetSavedVoiceInfo> ListSavedVoices(string skinId) => Voice is { } voice && voice.SkinId == skinId ? new[] { voice } : Array.Empty<PetSavedVoiceInfo>();
        public PetSavedVoiceInfo RenameSavedVoice(string referenceId, string name)
        {
            if (Voice is not { } voice || voice.ReferenceId != referenceId) throw new InvalidOperationException("Unknown reference");
            return Voice = voice with { Name = name };
        }
        public void DeleteSavedVoice(string referenceId)
        {
            if (Voice?.ReferenceId != referenceId) throw new InvalidOperationException("Unknown reference");
            Voice = null; Deletes++;
        }
        public void Dispose() { }
    }
}
