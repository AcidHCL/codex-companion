using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopPet;

internal sealed record ModelVramItem(string Key, string Label, string Color, long? Bytes, bool Remote = false);
internal sealed record ModelVramSnapshot(IReadOnlyList<ModelVramItem> LocalModels, IReadOnlyList<ModelVramItem> RemoteModels,
    long? TotalBytes, long? UsedBytes, long KnownModelBytes, bool AttributionFits, long? OtherBytes, long? FreeBytes,
    bool HasUnknownModels, IReadOnlyList<string> Notes);

// Independent snapshots use different accounting scopes. Model attribution is
// shown as a partition only when it fits the contemporaneous driver total.
internal static class ModelVramBreakdown
{
    internal const string VoiceColor = "#8B6BB5";
    internal const string OtherColor = "#9CA8AE";
    internal const string FreeColor = "#E6ECE7";
    private static readonly object ColorLock = new();
    private static readonly Dictionary<string, string> ModelColors = new(StringComparer.Ordinal);
    private static readonly string[] Palette = { "#4388C7", "#D99843", "#3D9B91", "#CB6E82", "#6F91C4", "#9C9151", "#BB7953", "#66966D", "#BF76A1", "#597FAC", "#5AA9B3", "#A38462" };

    internal static string ColorForModel(string name)
    {
        lock (ColorLock)
        {
            if (ModelColors.TryGetValue(name, out var color)) return color;
            uint hash = StableHash(name);
            var used = ModelColors.Values.ToHashSet(StringComparer.Ordinal);
            for (int offset = 0; offset < Palette.Length; offset++)
            {
                color = Palette[(int)((hash + (uint)offset) % (uint)Palette.Length)];
                if (!used.Contains(color)) break;
            }
            if (used.Contains(color!))
            {
                // A bounded process-lifetime cache preserves colors when models
                // appear/disappear; additional colors avoid collisions in view.
                uint seed = hash;
                do { color = HashColor(seed); seed = unchecked(seed + 2654435761u); }
                while (used.Contains(color) || color == VoiceColor || color == OtherColor);
            }
            if (ModelColors.Count < 512) ModelColors.Add(name, color!);
            return color!;
        }
    }

    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value) hash = unchecked((hash ^ c) * 16777619);
        return hash;
    }

    private static string HashColor(uint value) => "#" + (60 + (value & 127)).ToString("X2") +
        (65 + ((value >> 8) & 127)).ToString("X2") + (65 + ((value >> 16) & 127)).ToString("X2");

    internal static ModelVramSnapshot Analyze(IReadOnlyList<LocalGpuInfo>? gpus, string? gpuError,
        IReadOnlyList<OllamaRunningModel>? models, string? modelError, PetVoiceHealth? voice, string? voiceError, bool ollamaLocal)
    {
        var local = new List<ModelVramItem>(); var remote = new List<ModelVramItem>(); var notes = new List<string>();
        if (modelError != null || models == null)
        {
            (ollamaLocal ? local : remote).Add(new("ollama:unknown", L.T("Ollama 模型（状态不可用）", "Ollama models (status unavailable)"), ColorForModel("ollama:unknown"), null, !ollamaLocal));
            notes.Add((ollamaLocal ? L.T("本机", "Local") : L.T("远程", "Remote")) + L.T(" Ollama 暂未提供模型占用。", " Ollama has not provided model memory usage."));
        }
        else
        {
            foreach (var model in models.GroupBy(m => m.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(m => m.Name, StringComparer.Ordinal))
                (ollamaLocal ? local : remote).Add(new("ollama:" + model.Name, model.Name, ColorForModel(model.Name),
                    model.VramBytes is >= 0 ? model.VramBytes : null, !ollamaLocal));
        }
        if (voiceError != null)
            local.Add(new("voice", L.T("语音模型（状态不可用）", "Speech model (status unavailable)"), VoiceColor, null));
        else if (voice == null)
            local.Add(new("voice", L.T("语音模型（未加载）", "Speech model (not loaded)"), VoiceColor, 0));
        else
        {
            bool hasMemory = voice.Loaded || voice.Retiring || voice.ReservedBytes is > 0 || voice.AllocatedBytes is > 0;
            string name = string.IsNullOrWhiteSpace(voice.Model) ? L.T("语音模型", "Speech model") : L.T("语音 · ", "Speech · ") + voice.Model;
            // Reserved already includes allocated. Do not sum or substitute the
            // smaller allocation metric as if it represented all voice memory.
            local.Add(new("voice", name + (hasMemory ? L.T("（含缓存）", " (including cache)") : L.T("（未加载）", " (not loaded)")), VoiceColor,
                voice.ReservedBytes is >= 0 ? voice.ReservedBytes : null));
        }
        long? total = null, usedBytes = null;
        if (gpuError == null && gpus is { Count: > 0 } && gpus.All(g => g.TotalBytes > 0 && g.UsedBytes >= 0 && g.UsedBytes <= g.TotalBytes))
        {
            try { total = gpus.Aggregate(0L, (sum, gpu) => checked(sum + gpu.TotalBytes)); usedBytes = gpus.Aggregate(0L, (sum, gpu) => checked(sum + gpu.UsedBytes)); }
            catch (OverflowException) { total = usedBytes = null; }
        }
        if (total == null) notes.Add(L.T("本机显存总量不可用", "Local GPU memory total unavailable") + (string.IsNullOrWhiteSpace(gpuError) ? L.T("。", ".") : L.T("：", ": ") + Limit(gpuError)));
        bool unknown = local.Any(m => m.Bytes == null);
        decimal known = local.Sum(m => (decimal)(m.Bytes ?? 0));
        long knownBytes = known <= long.MaxValue ? (long)known : long.MaxValue;
        bool fits = usedBytes != null && known <= usedBytes;
        if (usedBytes != null && !fits) notes.Add(L.T("模型上报合计高于本次驱动实测：采样时间或统计口径不同，暂不合并为整卡分区。", "Reported model usage exceeds the driver's measurement. Sampling times or accounting differ, so these figures are not merged into the GPU partition chart."));
        if (unknown) notes.Add(L.T("部分模型未提供显存；未归属区域可能包含这些模型，不能视为零占用。", "Some models do not report GPU memory. Unattributed usage may include them; unavailable figures do not mean zero usage."));
        if (gpus is { Count: > 1 }) notes.Add(L.T("多张显卡按总量汇总；接口未提供各模型在每张显卡上的分配。", "Multiple GPUs are aggregated. The interface does not report each model's allocation on individual cards."));
        return new(local, remote, total, usedBytes, knownBytes, fits,
            fits ? usedBytes - knownBytes : null, total != null ? total - usedBytes : null, unknown, notes);
    }

    internal static FrameworkElement Build(IReadOnlyList<LocalGpuInfo>? gpus, string? gpuError,
        IReadOnlyList<OllamaRunningModel>? models, string? modelError, PetVoiceHealth? voice, string? voiceError, bool ollamaLocal)
    {
        var snapshot = Analyze(gpus, gpuError, models, modelError, voice, voiceError, ollamaLocal);
        var view = new StackPanel { Name = "ModelVramBreakdown" };
        if (gpus != null && gpuError == null)
            for (int i = 0; i < gpus.Count; i++)
            {
                var gpu = gpus[i];
                view.Children.Add(Text(gpu.Name + " · " + Bytes(gpu.UsedBytes) + " / " + Bytes(gpu.TotalBytes) +
                    L.T(" · GPU 使用率 ", " · GPU utilization ") + (gpu.UtilizationPercent is { } p ? p + "%" : L.T("未提供", "Not provided")), "GpuInfo" + i, true));
            }
        if (snapshot.TotalBytes is { } total && snapshot.UsedBytes is { } used)
        {
            if (gpus!.Count > 1) view.Children.Add(Text(L.T("全部显卡 · 已用 ", "All GPUs · Used ") + Bytes(used) + " / " + Bytes(total), "GpuAggregateUsage", true));
            var parts = snapshot.AttributionFits
                ? snapshot.LocalModels.Where(m => m.Bytes is > 0).ToList()
                : new List<ModelVramItem>();
            long other = snapshot.AttributionFits ? snapshot.OtherBytes!.Value : used;
            parts.Add(new("other", snapshot.AttributionFits ? L.T("其他 / 未归属", "Other / unattributed") : L.T("驱动实测已用", "Driver-reported usage"), OtherColor, other));
            parts.Add(new("free", L.T("可用显存", "Free GPU memory"), FreeColor, snapshot.FreeBytes));
            view.Children.Add(Bar(parts, total, "GpuCapacitySegments"));
            if (!snapshot.AttributionFits && snapshot.KnownModelBytes > 0)
            {
                view.Children.Add(Text(L.T("模型上报相对占比（单独比例，不代表整卡分区）", "Relative reported model usage (separate proportions, not GPU partitions)"), "GpuAttributionMismatch"));
                view.Children.Add(Bar(snapshot.LocalModels, snapshot.KnownModelBytes, "GpuModelRelativeSegments"));
            }
        }
        view.Children.Add(Legend(snapshot.LocalModels, "LocalModelLegend"));
        if (snapshot.UsedBytes is { } && snapshot.TotalBytes is { })
        {
            var accounting = new List<ModelVramItem>();
            if (snapshot.OtherBytes is { } other) accounting.Add(new("other", L.T("其他 / 未归属", "Other / unattributed"), OtherColor, other));
            else accounting.Add(new("driver", L.T("驱动实测已用", "Driver-reported usage"), OtherColor, snapshot.UsedBytes));
            accounting.Add(new("free", L.T("可用显存", "Free GPU memory"), FreeColor, snapshot.FreeBytes));
            view.Children.Add(Legend(accounting, "GpuAccountingLegend"));
        }
        if (snapshot.RemoteModels.Count > 0)
        {
            view.Children.Add(Text(L.T("远程 Ollama 模型 · 不计入本机显存条", "Remote Ollama models · Excluded from local GPU memory"), "RemoteModelLegendHeading", true));
            view.Children.Add(Legend(snapshot.RemoteModels, "RemoteModelLegend"));
        }
        foreach (string note in snapshot.Notes) view.Children.Add(Text(note, "", false));
        view.Children.Add(Text(L.T("语音按 PyTorch 保留显存计入（已包含分配量）；颜色随模型保持，未归属包含桌面、其他程序及未报告的开销。", "Speech uses PyTorch reserved memory, including allocated memory. Colors stay with each model. Unattributed usage includes the desktop, other applications, and unreported overhead."), "ModelVramScopeHint"));
        return view;
    }

    private static FrameworkElement Bar(IEnumerable<ModelVramItem> items, long total, string name)
    {
        var bar = new Grid { Name = name, Height = 13, Margin = new Thickness(0, 2, 0, 12), ClipToBounds = true };
        int index = 0;
        foreach (var item in items.Where(m => m.Bytes is > 0))
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((double)item.Bytes!.Value / Math.Max(1, total), GridUnitType.Star) });
            var segment = new Border { Background = Ui.Brush(item.Color), ToolTip = item.Label + " · " + Bytes(item.Bytes) };
            AutomationProperties.SetName(segment, item.Label + " · " + Bytes(item.Bytes));
            Grid.SetColumn(segment, index++); bar.Children.Add(segment);
        }
        return bar;
    }

    private static FrameworkElement Legend(IEnumerable<ModelVramItem> items, string name)
    {
        var legend = new WrapPanel { Name = name, Margin = new Thickness(0, 0, 0, 3) };
        int index = 0;
        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 20, 7), MaxWidth = 480 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(3), Background = Ui.Brush(item.Color),
                BorderThickness = new Thickness(item.Key == "free" ? 1 : 0), BorderBrush = Ui.Brush("#CCD5CE"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) });
            var label = Text(item.Label + " · " + Bytes(item.Bytes), name + "Item" + index++);
            label.Margin = new Thickness(0); Grid.SetColumn(label, 1); row.Children.Add(label); legend.Children.Add(row);
        }
        return legend;
    }

    private static TextBlock Text(string value, string name, bool bold = false)
    {
        var text = Ui.Text(value, 11, bold ? "#375B43" : "#71826D", bold);
        if (name.Length > 0) text.Name = name;
        text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 0, 0, 7); return text;
    }
    private static string Bytes(long? bytes) => bytes == null ? L.T("未提供", "Not provided") : bytes >= 1024L * 1024 * 1024
        ? (bytes.Value / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.CurrentCulture) + " GiB"
        : (bytes.Value / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MiB";
    private static string Limit(string text) => text.Length <= 240 ? text : text[..240] + "…";
}
