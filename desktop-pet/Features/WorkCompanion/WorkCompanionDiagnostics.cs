using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPet.WorkCompanion;

internal static class WorkCompanionDiagnostics
{
    internal static int RunObserverProbe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        try
        {
            var observation = new WindowsWorkObserver().ReadAsync(false,
                WorkCompanionOptions.DefaultExcludedApps(), timeout.Token).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "work-companion-probe-validation.json"), JsonSerializer.Serialize(new
            {
                passed = true, version = AppVersion.Number, elapsedMilliseconds = clock.ElapsedMilliseconds,
                observation.IsAvailable, observation.Status, observation.IsFullscreen, observation.IsOwnApp,
                titleAbsent = observation.WindowTitle == null, rawObservationRecorded = false,
                realModelRequests = 0, userSettingsWrites = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "work-companion-probe-validation.json"),
                JsonSerializer.Serialize(new { passed = false, errorType = ex.GetType().Name }));
            return 1;
        }
    }

    // Synthetic fixtures only. The real feature composition root is not created.
    internal static int Run()
    {
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var reports = new Dictionary<string, object>();
        int result = 1; bool finished = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "work-companion-validation.json"), JsonSerializer.Serialize(new
            { passed, version = AppVersion.Number, reports, error, realModelRequests = 0,
                observedDesktop = false, userSettingsWrites = 0, audioPlayback = false }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "work_companion_checks_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                reports["observationPolicy"] = WorkCompanionCoreChecks.Run();
                reports["diagnosticJournal"] = CompanionDiagnosticsChecks.Run();
                reports["activityPolicy"] = CompanionActivityChecks.Run();
                reports["controller"] = await WorkCompanionControllerChecks.RunAsync();
                reports["planner"] = await OllamaWorkReactionChecks.RunAsync();
                reports["chatIsolation"] = await OllamaWorkReactionChatChecks.RunAsync();
                reports["apiChatIsolation"] = await ApiWorkReactionChatChecks.RunAsync();
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run(); return result;
    }
}
