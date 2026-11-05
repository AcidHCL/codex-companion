using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// The live mode reads only local Windows counters. It never opens a chat,
// connects to a model endpoint, loads a model, or reads production settings.
internal static class OllamaActivityValidation
{
    internal static async Task<int> RunAsync(bool live)
    {
        string output = Path.Combine(AppContext.BaseDirectory,
            live ? "ollama-activity-probe-validation.json" : "ollama-activity-validation.json");
        try
        {
            object checks;
            if (live)
            {
                var readings = new List<object>();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                for (int i = 0; i < 3; i++)
                {
                    var watch = Stopwatch.StartNew();
                    var activity = await OllamaActivityTelemetry.ReadAsync(OllamaEndpoint.DefaultAddress, deadline.Token);
                    readings.Add(new { activity, elapsedMilliseconds = watch.ElapsedMilliseconds });
                    if (i < 2) await Task.Delay(1000, deadline.Token);
                }
                checks = new { readings, readOnly = true };
            }
            else
            {
                var telemetryChecks = new List<string>();
                OllamaActivityTelemetryChecks.Run((condition, name) =>
                {
                    if (!condition) throw new InvalidOperationException(name);
                    telemetryChecks.Add(name);
                });
                checks = new { telemetry = telemetryChecks, requests = await OllamaRequestActivityChecks.RunAsync() };
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                passed = true, version = AppVersion.Number, checks,
                realModelOperations = 0, userSettingsWrites = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new
            { passed = false, version = AppVersion.Number, error = ex.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
