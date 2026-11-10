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

// Injected in-memory settings and model-list callbacks only. Does not read a
// real key, call an API, run a model or change saved application preferences.
internal static class ChatApiSettingsUiChecks
{
    internal static int Run()
    {
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string previousLanguage = L.Language;
        string evidence = Path.Combine(AppContext.BaseDirectory, "api-settings-ui-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var checks = new List<string>(); Window? window = null; ChatApiConnectionView? view = null;
        int result = 1, applies = 0, lists = 0; bool finished = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop(); view?.Dispose(); window?.Close(); L.Initialize(previousLanguage);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "api-settings-ui-validation.json"), JsonSerializer.Serialize(new
            { passed, assertions = checks.Count, checks, error, evidence, fakeApplies = applies, fakeModelListRequests = lists,
                realApiRequests = 0, userSettingsWrites = 0, realCredentialsAccessed = false, inputEventsInjected = false },
                new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "API_SETTINGS_UI_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                foreach (var language in new[] { L.Chinese, L.English })
                {
                    L.Initialize(language);
                    var profile = new ChatApiProfile { BaseUrl = "https://api.example.test/v1", Model = "fixture-model" };
                    ChatApiConnectionUpdate? lastApply = null, lastList = null;
                    bool failApply = false, saved = true;
                    view = new ChatApiConnectionView(profile, true, (update, token) =>
                    {
                        token.ThrowIfCancellationRequested(); applies++; lastApply = update;
                        if (failApply) throw new InvalidOperationException("fixture-credential-must-not-be-displayed");
                        if (update.KeyChange == ApiKeyChange.Delete) saved = false;
                        if (update.KeyChange == ApiKeyChange.Replace) saved = true;
                        return Task.CompletedTask;
                    }, endpoint => saved && endpoint == profile.Endpoint, (update, token) =>
                    {
                        token.ThrowIfCancellationRequested(); lists++; lastList = update;
                        return Task.FromResult<IReadOnlyList<string>>(new[] { "fixture-second", "fixture-model", "fixture-model", "invalid model" });
                    });
                    window = new Window { Width = 820, Height = 900, Content = new ScrollViewer { Content = view,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, ShowActivated = false, ShowInTaskbar = false,
                        Title = "API settings fixture", Background = Ui.Brush("#F7F9F2") };
                    window.Show(); await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                    var key = Named<PasswordBox>(view, "ChatApiKeyInput");
                    var address = Named<TextBox>(view, "ChatApiBaseUrl"); var model = Named<TextBox>(view, "ChatApiModelId");
                    var provider = Named<ComboBox>(view, "ChatApiProvider"); var modelList = Named<ComboBox>(view, "ChatApiAvailableModels");
                    int beforeApplies = applies, beforeLists = lists;
                    Check(key.Password == "" && !Named<CheckBox>(view, "ChatApiSupportsImages").IsChecked!.Value, language + "_no_key_echo_images_opt_in");
                    var help = Named<Expander>(view, "ChatApiHelp");
                    Check(!help.IsExpanded && Named<TextBlock>(view, "ChatApiDataHint").IsVisible,
                        language + "_help_optional_service_notice_visible");
                    Capture(view, Path.Combine(evidence, language + ".png"));
                    window.Width = 440; window.UpdateLayout();
                    Capture(view, Path.Combine(evidence, language + "-narrow.png"));
                    window.Width = 820; window.UpdateLayout();
                    help.IsExpanded = true; window.UpdateLayout();
                    Check(Named<TextBlock>(view, "ChatApiBaseUrlHint").Text.Contains("https://api.openai.com/v1", StringComparison.Ordinal), language + "_official_openai_guidance");
                    Capture(view, Path.Combine(evidence, language + "-help.png"));
                    help.IsExpanded = false; window.UpdateLayout();
                    Check(applies == beforeApplies && lists == beforeLists, language + "_opening_has_no_requests_or_changes");
                    model.Text = "";
                    await view.ReadModelsAsync();
                    Check(lists == beforeLists + 1 && applies == beforeApplies && lastList?.Profile.Model == "" && lastList.KeyChange == ApiKeyChange.Keep,
                        language + "_model_list_reads_unsaved_draft_without_apply");
                    Check(modelList.Items.Count == 2, language + "_model_list_filters_invalid_and_duplicate_ids");
                    modelList.SelectedItem = "fixture-second";
                    Check(model.Text == "fixture-second", language + "_model_list_selection_fills_manual_id");
                    Capture(view, Path.Combine(evidence, language + "-models.png"));
                    await view.ApplyAsync();
                    Check(lastApply?.Profile.Model == "fixture-second" && lastApply.KeyChange == ApiKeyChange.Keep && lastApply.NewApiKey == null,
                        language + "_save_keeps_endpoint_bound_key");
                    key.Password = "fixture-replacement";
                    address.Text = "https://other.example.test/v1";
                    Check(key.Password == "" && modelList.Items.Count == 0, language + "_address_change_clears_typed_key_and_model_list");
                    key.Password = "fixture-replacement";
                    await view.ApplyAsync();
                    Check(lastApply?.KeyChange == ApiKeyChange.Replace && lastApply.NewApiKey == "fixture-replacement" && key.Password == "",
                        language + "_replacement_applied_and_password_cleared");
                    int beforeClear = applies;
                    Named<Button>(view, "ChatApiClearKey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(applies == beforeClear, language + "_clear_key_requires_explicit_apply");
                    await view.ApplyAsync();
                    Check(lastApply?.KeyChange == ApiKeyChange.Delete && lastApply.NewApiKey == null, language + "_clear_sends_endpoint_scoped_command");
                    failApply = true; await view.ApplyAsync();
                    Check(!Named<TextBlock>(view, "ChatApiConnectionStatus").Text.Contains("fixture-credential", StringComparison.Ordinal),
                        language + "_callback_error_does_not_echo_secret");
                    failApply = false; model.Text = ""; provider.SelectedValue = false;
                    await view.ApplyAsync();
                    Check(lastApply?.UseApi == false && lastApply.Profile.Model == "fixture-second", language + "_ollama_switch_ignores_incomplete_api_draft");
                    view.Dispose(); window.Close(); window = null; view = null;
                }
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex is InvalidOperationException ? ex.Message : ex.GetType().Name); }
        };
        app.Run(); return result;
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).Single(x => x.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
