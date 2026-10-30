using System;

namespace DesktopPet;

internal static class LocalGpuTelemetryChecks
{
    internal static void Run(Action<bool, string> require)
    {
        var single = LocalGpuTelemetry.Parse("NVIDIA GeForce RTX 4090, 24564, 3072, 15\r\n");
        require(single.Count == 1 && single[0].Name == "NVIDIA GeForce RTX 4090" &&
            single[0].TotalBytes == 24564L * 1024 * 1024 && single[0].UsedBytes == 3072L * 1024 * 1024 &&
            single[0].UtilizationPercent == 15, "gpu_metrics_convert_driver_mib_to_bytes");
        var multiple = LocalGpuTelemetry.Parse("\"GPU, one\", 8192, 0, [N/A]\nGPU two, 16384, 0.5, 100\n");
        require(multiple.Count == 2 && multiple[0].Name == "GPU, one" && multiple[0].UsedBytes == 0 &&
            multiple[0].UtilizationPercent == null && multiple[1].UsedBytes == 512 * 1024,
            "gpu_multiple_devices_zero_usage_and_unavailable_utilization_are_distinct");
        require(LocalGpuTelemetry.Parse("GPU, 8192, 1, -1")[0].UtilizationPercent == null &&
            LocalGpuTelemetry.Parse("GPU, 8192, 1, 101")[0].UtilizationPercent == null,
            "gpu_invalid_utilization_not_reported_as_zero");
        string[] invalidValues = { "", "No devices were found", "GPU, N/A, 1, 2", "GPU, 8192, -1, 1",
            "GPU, 0, 0, 0", "GPU, 8192, 9000, 0", "GPU, 99999999999999999999, 0, 0" };
        for (int index = 0; index < invalidValues.Length; index++)
        {
            bool rejected = false;
            try { LocalGpuTelemetry.Parse(invalidValues[index]); } catch (InvalidOperationException) { rejected = true; }
            require(rejected, "gpu_invalid_or_missing_memory_is_unavailable_" + index);
        }
    }
}
