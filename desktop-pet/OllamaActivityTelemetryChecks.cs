using System;
using System.Linq;

namespace DesktopPet;

internal static class OllamaActivityTelemetryChecks
{
    internal static void Run(Action<bool, string> require)
    {
        var tree = new[]
        {
            new OllamaActivityTelemetry.ProcessEntry(10, 1, "ollama.exe"),
            new OllamaActivityTelemetry.ProcessEntry(11, 10, "llama-server.exe"),
            new OllamaActivityTelemetry.ProcessEntry(12, 10, "python.exe"),
            new OllamaActivityTelemetry.ProcessEntry(20, 1, "ollama.exe"),
            new OllamaActivityTelemetry.ProcessEntry(21, 20, "llama-server.exe"),
            new OllamaActivityTelemetry.ProcessEntry(30, 31, "ollama.exe"),
            new OllamaActivityTelemetry.ProcessEntry(31, 30, "llama-server.exe")
        };
        var selected = OllamaActivityTelemetry.SelectDescendants(tree, 10);
        require(selected.Select(p => p.ProcessId).SequenceEqual(new[] { 10, 11 }),
            "activity_only_endpoint_server_tree_excludes_other_server_and_voice");
        require(OllamaActivityTelemetry.IsWithinInstall(@"C:\Ollama\lib\ollama\llama-server.exe", @"C:\Ollama") &&
            !OllamaActivityTelemetry.IsWithinInstall(@"C:\Ollama-other\llama-server.exe", @"C:\Ollama"),
            "activity_runner_install_path_boundary");
        const string engine = "luid_0x00000000_0x0001910B_phys_0_eng_0_engtype_3D";
        require(OllamaActivityTelemetry.TryEngine("pid_11_" + engine, 86.5, 0, out var sample) &&
            sample!.ProcessId == 11 && sample.Percent == 86.5,
            "activity_3d_engine_is_not_discarded_for_cuda_runner");
        require(!OllamaActivityTelemetry.TryEngine("pid_11_" + engine, 0, 0xC0000BB8, out _) &&
            !OllamaActivityTelemetry.TryEngine("pid_11_" + engine, double.NaN, 0, out _) &&
            !OllamaActivityTelemetry.TryEngine("pid_11_" + engine, -1, 0, out _) &&
            !OllamaActivityTelemetry.TryEngine("pid_bad_" + engine, 5, 0, out _),
            "activity_invalid_or_missing_rate_is_not_idle");
        var now = DateTimeOffset.UtcNow;
        var busy = OllamaActivityTelemetry.Aggregate(selected, new[]
        {
            new OllamaActivityTelemetry.EngineValue(11, engine, 60),
            new OllamaActivityTelemetry.EngineValue(11, engine, 60),
            new OllamaActivityTelemetry.EngineValue(11, engine + "_other", 40),
            new OllamaActivityTelemetry.EngineValue(21, engine, 99),
            new OllamaActivityTelemetry.EngineValue(12, engine, 80)
        }, 10, now);
        require(busy.Available && busy.UtilizationPercent == 60 && busy.Processes.Count == 1 &&
            busy.Processes[0].ProcessId == 11 && busy.ServerProcessId == 10 && busy.SampledAt == now,
            "activity_excludes_unrelated_gpu_load_and_does_not_sum_engines_or_duplicates");
        var missing = OllamaActivityTelemetry.Aggregate(selected, Array.Empty<OllamaActivityTelemetry.EngineValue>(), 10, now);
        require(!missing.Available && missing.UtilizationPercent == null,
            "activity_missing_runner_counters_are_unknown_not_zero");
        var hostOnly = OllamaActivityTelemetry.Aggregate(selected,
            new[] { new OllamaActivityTelemetry.EngineValue(10, engine, 0) }, 10, now);
        require(!hostOnly.Available, "activity_service_host_zero_does_not_mask_missing_runner");
        var idle = OllamaActivityTelemetry.Aggregate(selected,
            new[] { new OllamaActivityTelemetry.EngineValue(11, engine, 0) }, 10, now);
        require(idle.Available && idle.UtilizationPercent == 0, "activity_valid_idle_zero_is_preserved");
        var twoRunners = selected.Concat(new[] { new OllamaActivityTelemetry.ProcessEntry(13, 10, "llama-server.exe") }).ToArray();
        var sharedEngine = OllamaActivityTelemetry.Aggregate(twoRunners, new[]
        {
            new OllamaActivityTelemetry.EngineValue(11, engine, 45),
            new OllamaActivityTelemetry.EngineValue(13, engine, 30),
            new OllamaActivityTelemetry.EngineValue(13, engine + "_other", 10)
        }, 10, now);
        require(sharedEngine.Available && sharedEngine.UtilizationPercent == 75 && sharedEngine.Processes.Count == 2,
            "activity_combines_same_engine_runner_activity_without_cross_engine_addition");
        var partiallyMissing = OllamaActivityTelemetry.Aggregate(twoRunners,
            new[] { new OllamaActivityTelemetry.EngineValue(11, engine, 0) }, 10, now);
        require(!partiallyMissing.Available, "activity_one_missing_runner_keeps_service_activity_unknown");
    }
}
