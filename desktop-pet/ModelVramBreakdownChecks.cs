using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPet;

internal static class ModelVramBreakdownChecks
{
    internal static void Run(Action<bool, string> require)
    {
        const long gib = 1024L * 1024 * 1024;
        var gpus = new[] { new LocalGpuInfo("fixture GPU", 32 * gib, 16 * gib, 10) };
        var models = new[] { new OllamaRunningModel("test:9b", 8 * gib, 8 * gib, 8192, null), new OllamaRunningModel("test:3b", 2 * gib, 2 * gib, 8192, null) };
        var voice = new PetVoiceHealth("ready", "faster", true, true, false, "Qwen3-TTS", "cuda:0", 2 * gib, 3 * gib);
        var known = ModelVramBreakdown.Analyze(gpus, null, models, null, voice, null, true);
        require(known.KnownModelBytes == 13 * gib && known.OtherBytes == 3 * gib && known.FreeBytes == 16 * gib && known.AttributionFits,
            "vram_partition_uses_reserved_voice_once_and_balances_driver_total");
        require(known.LocalModels.Select(m => m.Color).Distinct().Count() == 3 && known.LocalModels.Single(m => m.Key == "voice").Bytes == 3 * gib,
            "vram_models_and_voice_have_distinct_colors_without_double_counting_allocated_memory");
        string firstColor = ModelVramBreakdown.ColorForModel("test:9b");
        var reverse = ModelVramBreakdown.Analyze(gpus, null, models.Reverse().ToArray(), null, voice, null, true);
        ModelVramBreakdown.ColorForModel("new-model:latest");
        require(reverse.LocalModels.Single(m => m.Key == "ollama:test:9b").Color == firstColor && ModelVramBreakdown.ColorForModel("test:9b") == firstColor,
            "vram_color_stays_stable_across_refresh_order_and_new_models");
        var remote = ModelVramBreakdown.Analyze(gpus, null, models, null, voice, null, false);
        require(remote.KnownModelBytes == 3 * gib && remote.RemoteModels.Count == 2 && remote.OtherBytes == 13 * gib,
            "vram_remote_ollama_models_never_enter_local_gpu_partition");
        var unknown = ModelVramBreakdown.Analyze(gpus, null, new[] { models[0] with { VramBytes = null } }, null,
            voice with { ReservedBytes = null }, null, true);
        require(unknown.HasUnknownModels && unknown.KnownModelBytes == 0 && unknown.LocalModels.All(m => m.Bytes == null) && unknown.OtherBytes == 16 * gib,
            "vram_missing_metrics_stay_unknown_without_substituting_voice_allocations");
        var over = ModelVramBreakdown.Analyze(new[] { gpus[0] with { UsedBytes = 2 * gib } }, null, models, null, voice, null, true);
        require(!over.AttributionFits && over.OtherBytes == null && over.KnownModelBytes == 13 * gib && over.Notes.Any(n => n.Contains("暂不合并")),
            "vram_out_of_sync_measurements_do_not_invent_negative_other_usage");
        var unavailable = ModelVramBreakdown.Analyze(null, "driver unavailable", models, null, voice, null, true);
        require(unavailable.TotalBytes == null && unavailable.UsedBytes == null && unavailable.LocalModels.Count == 3 && unavailable.KnownModelBytes == 13 * gib,
            "vram_driver_failure_keeps_per_model_values_without_fake_capacity");
        var multi = ModelVramBreakdown.Analyze(new[] { gpus[0], gpus[0] with { Name = "second GPU" } }, null, models, null, voice, null, true);
        require(multi.TotalBytes == 64 * gib && multi.UsedBytes == 32 * gib && multi.KnownModelBytes == 13 * gib && multi.Notes.Any(n => n.Contains("每张显卡")),
            "vram_multigpu_aggregates_capacity_without_duplicating_each_model");
        var unloaded = ModelVramBreakdown.Analyze(gpus, null, Array.Empty<OllamaRunningModel>(), null, null, null, true);
        require(unloaded.KnownModelBytes == 0 && unloaded.LocalModels.Single().Bytes == 0 && !unloaded.HasUnknownModels,
            "vram_unloaded_voice_process_is_distinct_from_unknown_metrics");
        var idleUnknown = ModelVramBreakdown.Analyze(gpus, null, Array.Empty<OllamaRunningModel>(), null,
            new PetVoiceHealth("idle", "faster", false, false, false), null, true);
        require(idleUnknown.HasUnknownModels && idleUnknown.LocalModels.Single().Bytes == null,
            "vram_idle_live_worker_missing_allocator_metrics_remain_unknown");
        var failed = ModelVramBreakdown.Analyze(gpus, null, null, "offline", null, "health timeout", true);
        require(failed.HasUnknownModels && failed.LocalModels.Count == 2 && failed.LocalModels.All(m => m.Bytes == null),
            "vram_health_failures_are_unknown_not_reported_as_zero");
        var view = ModelVramBreakdown.Build(gpus, null, models, null, voice, null, true);
        require(view is StackPanel && ((StackPanel)view).Children.OfType<FrameworkElement>().Any(e => e.Name == "GpuCapacitySegments") &&
            ((StackPanel)view).Children.OfType<FrameworkElement>().Any(e => e.Name == "LocalModelLegend"),
            "vram_view_exposes_capacity_segments_and_per_model_legend");
        var mismatchView = (StackPanel)ModelVramBreakdown.Build(new[] { gpus[0] with { UsedBytes = 2 * gib } }, null, models, null, voice, null, true);
        require(mismatchView.Children.OfType<FrameworkElement>().Any(e => e.Name == "GpuModelRelativeSegments") && mismatchView.Children.OfType<FrameworkElement>().Any(e => e.Name == "GpuAttributionMismatch"),
            "vram_mismatch_visual_keeps_driver_and_model_scales_separate");
    }
}
