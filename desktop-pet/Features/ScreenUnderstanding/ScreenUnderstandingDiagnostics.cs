using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPet.ScreenUnderstanding;

internal static class ScreenUnderstandingDiagnostics
{
    internal static int Run(bool captureFixture)
    {
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var reports = new Dictionary<string, object>();
        int result = 1; bool finished = false;
        string reportName = captureFixture ? "screen-capture-validation.json" : "screen-understanding-validation.json";
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(55) };
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, reportName), JsonSerializer.Serialize(new
            { passed, version = AppVersion.Number, reports, error, realModelRequests = 0,
                userSettingsWrites = 0, personalWindowCaptured = false, captureFixture }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "screen_understanding_checks_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                if (captureFixture) reports["capture"] = await ScreenCaptureChecks.RunAsync();
                else
                {
                    reports["settingsMemory"] = ScreenUnderstandingSettingsChecks.Run();
                    reports["model"] = await ScreenUnderstandingModelChecks.RunAsync();
                    reports["apiModel"] = await ScreenUnderstandingApiChecks.RunAsync();
                    reports["coordinator"] = await ScreenUnderstandingModuleChecks.RunAsync();
                }
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run(); return result;
    }
}
