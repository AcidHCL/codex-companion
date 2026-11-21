using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace DesktopPet.WorkCompanion;

// Read-only support report from the same unredirected desktop context as the
// installed app. No window titles, text, pixels, prompts or model generation.
internal static class CompanionRuntimeProbe
{
    internal static int Run()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "companion-runtime-validation.json");
        try
        {
            var settings = DesktopSettings.Load();
            var options = new WorkCompanionOptionsStore().Load();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var client = ChatModelConnection.CreateClient(settings);
            string model = ChatModelConnection.ActiveModel(settings);
            var running = client is OllamaClient ? client.ListRunningModelsAsync(deadline.Token).GetAwaiter().GetResult() : Array.Empty<OllamaRunningModel>();
            var selected = running.FirstOrDefault(item => item.Name == model);
            var observation = new WindowsWorkObserver().ReadAsync(false, options.ExcludedApps, deadline.Token).GetAwaiter().GetResult();
            CompanionDiagnostics.Initialize();
            object? screen = null;
#if SCREEN_UNDERSTANDING
            var screenOptions = new ScreenUnderstanding.ScreenUnderstandingOptionsStore().Load();
            var readiness = ScreenUnderstanding.ScreenModelConnection.ReadinessAsync(client, model, deadline.Token).GetAwaiter().GetResult();
            screen = new { screenOptions.Enabled, screenOptions.ReadWindowText, screenOptions.UseScreenshots,
                screenOptions.AllowRemoteEndpoint, screenOptions.AutomaticIntervalMinutes,
                readiness.Available, readiness.SupportsText, readiness.SupportsImages, readiness.Status };
#endif
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                passed = true, version = AppVersion.Number, at = DateTimeOffset.UtcNow,
                independentContext = IndependentProcessLauncher.IsIndependentContext(),
                selectedModel = model, provider = settings.UseChatApi ? "api" : "ollama", localEndpoint = client.Endpoint.IsLoopback,
                settings.LlmBehaviorEnabled, settings.SpeechEnabled,
                companion = new { options.Enabled, options.ObserveApplications, options.CooldownMinutes,
                    options.WorkEnabled, options.GameEnabled, options.MediaEnabled, options.DailyEnabled, options.QuietFullscreen },
                resident = selected != null, contextLength = selected?.ContextLength, screen,
                observer = new { observation.IsAvailable, observation.Status, observation.IsFullscreen,
                    observation.IsOwnApp, idleSeconds = (int)observation.IdleFor.TotalSeconds },
                recent = CompanionDiagnostics.ReadRecent(24).Select(entry => new { entry.Timestamp, entry.Layer, entry.Code }),
                realModelRequests = 0, userSettingsWrites = 0, capturedWindowContents = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = ex.GetType().Name }));
            return 1;
        }
    }
}
