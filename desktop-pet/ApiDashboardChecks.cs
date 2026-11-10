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

namespace DesktopPet;

// The dashboard fixture never reads a saved key or calls an external service.
internal static class ApiDashboardChecks
{
    internal static int Run()
    {
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        int result = 1;
        app.Startup += async (_, _) =>
        {
            string? error = null;
            try
            {
                await RunAsync((condition, name) =>
                {
                    if (!condition) throw new InvalidOperationException(name);
                    checks.Add(name);
                });
                result = 0;
            }
            catch (Exception ex) { error = ex.ToString(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "api-dashboard-validation.json"),
                JsonSerializer.Serialize(new { passed = result == 0, checks, error, realApiRequests = 0, userSettingsWrites = 0 },
                    new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(result);
        };
        app.Run();
        return result;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        L.Initialize(L.Chinese);
        var settings = new DesktopSettings { UseChatApi = true,
            ApiProfile = new ChatApiProfile { BaseUrl = "https://api.example.com/v1", Model = "fixture-model" },
            OllamaAddress = "http://127.0.0.1:11434", OllamaModel = OllamaModels.QualityModel, OllamaKeepAliveMinutes = 30 };
        int ollamaReads = 0, installedReads = 0, metadataReads = 0, maintenance = 0, applies = 0;
        string[] cached = { "fixture-model", "other-model" };
        var services = new DashboardModelServices(
            (_, _) => { ollamaReads++; return Task.FromResult<IReadOnlyList<OllamaRunningModel>>(new[] {
                new OllamaRunningModel(settings.OllamaModel, 4L << 30, 3L << 30, 8192, null) }); },
            _ => Task.FromResult<PetVoiceHealth?>(null),
            _ => Task.FromResult<IReadOnlyList<LocalGpuInfo>>(new[] { new LocalGpuInfo("Fixture GPU", 16L << 30, 3L << 30, 0) }),
            _ => { maintenance++; return Task.FromResult("unexpected"); },
            ReadInstalledModels: (_, _) => { installedReads++; return Task.FromResult<IReadOnlyList<string>>(new[] { settings.OllamaModel }); },
            SelectLlmModel: selected => { settings.ApiProfile = settings.ApiProfile with { Model = selected }; return Task.FromResult("selected"); },
            ApplyChatApiConnection: (update, _) => { applies++; settings.UseChatApi = update.UseApi; settings.ApiProfile = update.Profile; return Task.CompletedTask; },
            HasChatApiKey: _ => true,
            ReadChatApiModels: () => cached,
            ListChatApiModels: (_, _) => { metadataReads++; return Task.FromResult<IReadOnlyList<string>>(cached); });
        using var view = new ModelSettingsView(settings, modelServices: services);
        var window = new Window { Content = view, Width = 1000, Height = 850, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            check(ollamaReads == 0 && metadataReads == 0, "constructing_api_dashboard_has_no_network_or_inference");
            window.Show(); await Settle();
            check(Named<TextBlock>(view, "DashboardLlmState").Text.Contains("API 模式"), "api_mode_does_not_claim_local_model_is_loaded");
            check(Named<TextBlock>(view, "DashboardLlmMemory").Text.Contains("远程模型不占本机"), "api_model_does_not_inherit_ollama_vram");
            check(Named<TextBlock>(view, "DashboardLlmOthers").Text.Contains(settings.OllamaModel), "remaining_local_models_are_shown_separately");
            check(!Named<Button>(view, "DashboardLlmLoad").IsEnabled && !Named<Button>(view, "DashboardLlmUnload").IsEnabled &&
                !Named<ComboBox>(view, "DashboardLlmIdleUnload").IsEnabled, "api_residency_controls_are_disabled");
            Named<Button>(view, "DashboardLlmLoad").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Named<Button>(view, "DashboardLlmUnload").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(maintenance == 0, "routed_api_load_unload_events_do_not_touch_ollama");
            Named<Button>(view, "ModelRefresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            check(metadataReads == 0 && installedReads == 0 && ollamaReads > 0, "api_dashboard_refresh_only_reads_local_telemetry_and_cached_models");
            var selector = Named<ComboBox>(view, "DashboardLlmSelector");
            check(selector.IsEnabled && selector.Items.Count == 2, "api_selector_uses_shared_chat_model_cache");
            selector.SelectedValue = "other-model"; await Settle();
            check(settings.ApiProfile.Model == "other-model" && settings.OllamaModel == OllamaModels.QualityModel &&
                settings.OllamaKeepAliveMinutes == 30, "api_model_selection_preserves_ollama_selection_and_retention");
            settings.ApiProfile = settings.ApiProfile with { Model = "fixture-model" };
            view.ModelSelectionChanged(); await Settle();
            check((string?)selector.SelectedValue == "fixture-model", "external_chat_selection_synchronizes_dashboard");
            Capture(window, "api-dashboard-models.png");
            Named<Expander>(view, "ChatApiConnectionExpander").IsExpanded = true; await Settle();
            Named<ComboBox>(view, "ChatApiProvider").SelectedValue = false;
            Named<Button>(view, "ChatApiApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            check(applies == 1 && !settings.UseChatApi && settings.OllamaModel == OllamaModels.QualityModel && metadataReads == 0,
                "explicit_switch_back_restores_ollama_without_cloud_request");
            check(Named<Button>(view, "DashboardLlmLoad").IsEnabled && installedReads > 0,
                "switching_back_restores_local_model_controls");
        }
        finally { window.Close(); }
    }

    private static async Task Settle()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Named<T>(VisualTreeHelper.GetChild(root, i), name); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("UI element unavailable: " + name);
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, name)); png.Save(output);
    }
}
