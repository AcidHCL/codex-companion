using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed record LocalGpuInfo(string Name, long TotalBytes, long UsedBytes, int? UtilizationPercent);

// Driver-wide local usage: it includes other apps and is not a per-model total.
// Querying this never loads a model or initializes a CUDA context in the pet.
internal static class LocalGpuTelemetry
{
    internal static async Task<IReadOnlyList<LocalGpuInfo>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string executable = FindExecutable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("--query-gpu=name,memory.total,memory.used,utilization.gpu");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动本机显存查询。");
            Task<string> output = ReadBoundedAsync(process.StandardOutput, 64 * 1024, deadline.Token);
            Task<string> error = ReadBoundedAsync(process.StandardError, 8 * 1024, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("NVIDIA 驱动暂时无法提供本机显存信息。");
            return Parse(output.Result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("本机显存查询超时，请稍后刷新。");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("无法读取本机显存，请检查 NVIDIA 驱动。", ex);
        }
        finally
        {
            // Kill only this short-lived helper if timeout/cancellation interrupted it.
            try { if (process.Id > 0 && !process.HasExited) process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    private static string FindExecutable()
    {
        // Avoid shell resolution and current-directory/PATH executable hijacking.
        foreach (string file in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        })
            if (Path.IsPathFullyQualified(file) && File.Exists(file)) return file;
        throw new NotSupportedException("未找到 NVIDIA 显存查询工具；当前设备暂不提供本机总显存数据。");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        char[] buffer = new char[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            if (result.Length + count > limit) throw new InvalidOperationException("本机显存查询返回了过长的数据。");
            result.Append(buffer, 0, count);
        }
    }

    internal static IReadOnlyList<LocalGpuInfo> Parse(string output)
    {
        var results = new List<LocalGpuInfo>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The last three columns are numeric; preserve commas inside device names.
            int utilizationStart = line.LastIndexOf(',');
            int usedStart = utilizationStart > 0 ? line.LastIndexOf(',', utilizationStart - 1) : -1;
            int totalStart = usedStart > 0 ? line.LastIndexOf(',', usedStart - 1) : -1;
            if (totalStart <= 0) throw InvalidData();
            string name = line[..totalStart].Trim().Trim('"').Replace("\"\"", "\"");
            if (name.Length == 0 || name.Length > 512) throw InvalidData();
            long total = ParseBytes(line[(totalStart + 1)..usedStart]);
            long used = ParseBytes(line[(usedStart + 1)..utilizationStart]);
            if (total == 0 || used > total) throw InvalidData();
            int? utilization = int.TryParse(line[(utilizationStart + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int percent)
                && percent is >= 0 and <= 100 ? percent : null;
            results.Add(new LocalGpuInfo(name, total, used, utilization));
            if (results.Count > 64) throw InvalidData();
        }
        if (results.Count == 0) throw new InvalidOperationException("本机未检测到可查询的 NVIDIA 显卡。");
        return results;
    }

    private static long ParseBytes(string value)
    {
        if (!decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal mib)
            || mib < 0 || mib > long.MaxValue / (1024m * 1024m)) throw InvalidData();
        return (long)(mib * 1024m * 1024m);
    }

    private static InvalidOperationException InvalidData() => new("NVIDIA 驱动未提供有效的本机显存数据。");
}
