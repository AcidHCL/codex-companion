using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopPet;

internal sealed partial class ModelSettingsView
{
    private readonly OllamaActivityClassifier _activityClassifier = new();
    private readonly TextBlock _ollamaActivityState = ModelText(L.T("等待查询", "Waiting for status"), "DashboardOllamaActivityState", 15, true);
    private readonly TextBlock _ollamaActivityDetail = ModelText("", "DashboardOllamaActivityDetail");
    private readonly TextBlock _ollamaActivityOwn = ModelText("", "DashboardOllamaActivityOwn");
    private readonly TextBlock _ollamaActivityGpu = ModelText("", "DashboardOllamaActivityGpu");
    private readonly TextBlock _ollamaActivityProcesses = ModelText("", "DashboardOllamaActivityProcesses");
    private readonly TextBlock _ollamaActivityEndpoint = ModelText("", "DashboardOllamaActivityEndpoint");

    private void BuildOllamaActivityCard(StackPanel body)
    {
        var contents = new StackPanel();
        contents.Children.Add(ModelText(L.T("Ollama 运行活动", "Ollama activity"), "DashboardOllamaActivityTitle", 16, true));
        contents.Children.Add(_ollamaActivityEndpoint);
        AutomationProperties.SetLiveSetting(_ollamaActivityState, AutomationLiveSetting.Polite);
        contents.Children.Add(_ollamaActivityState); contents.Children.Add(_ollamaActivityDetail);
        contents.Children.Add(_ollamaActivityOwn); contents.Children.Add(_ollamaActivityGpu);
        contents.Children.Add(ModelText(L.T(
            "仅估计整个 Ollama 服务的活动，无法确定具体模型或任务。",
            "Estimates activity for the entire Ollama service, not a specific model or task."), "DashboardOllamaActivityScope"));
        var details = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        details.Children.Add(_ollamaActivityProcesses);
        details.Children.Add(ModelText(L.T(
            "每 5 秒观察一次；连续约 10 秒有 GPU 活动且桌宠没有请求，才提示“疑似外部推理”。加载、预热、CPU 推理和短请求可能影响判断。本提示不会自动加载或卸载模型。",
            "Samples every 5 seconds. Sustained GPU activity for about 10 seconds without pet requests suggests external inference. Loading, warm-up, CPU inference and short requests can affect this estimate. This indicator never loads or unloads models."), "DashboardOllamaActivityHint"));
        contents.Children.Add(new Expander { Name = "DashboardOllamaActivityDetails", Header = L.T("进程与判断依据", "Processes & detection details"), Content = details });
        var card = Card(contents); card.Name = "DashboardOllamaActivityCard";
        card.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(card);
    }

    private void ResetOllamaActivity()
    {
        _activityClassifier.Reset();
        _ollamaActivityState.Text = L.T("等待查询", "Waiting for status");
        _ollamaActivityState.Foreground = Ui.Brush("#71826D");
        _ollamaActivityDetail.Text = _ollamaActivityOwn.Text = _ollamaActivityGpu.Text = _ollamaActivityProcesses.Text = "";
    }

    private void RenderOllamaActivity(string endpoint, IReadOnlyList<OllamaRunningModel>? models, string? modelError,
        OllamaGpuActivity? gpu, string? gpuError, OllamaRequestSnapshot? own)
    {
        _ollamaActivityEndpoint.Text = endpoint;
        _ollamaActivityEndpoint.ToolTip = _settings.UseChatApi ? L.T("独立于当前 API 连接。", "Separate from the active API connection.") : null;
        var assessment = _activityClassifier.Evaluate(endpoint, modelError == null ? models?.Count : null, gpu, own, DateTimeOffset.UtcNow);
        var (title, detail, color) = assessment.State switch
        {
            OllamaActivityState.OwnRequest => (L.T("桌宠正在请求", "Pet request in progress"),
                L.T("请求可能正在排队，也可能与其他软件共用。", "Requests may be queued or shared with other apps."), "#316CB0"),
            OllamaActivityState.Maintenance => (L.T("桌宠正在加载或卸载", "Pet is loading or unloading"),
                L.T("暂不判断外部推理。", "External inference is not estimated yet."), "#316CB0"),
            OllamaActivityState.Settling => (L.T("请求刚结束 · 等待稳定", "Request ended · Settling"),
                L.T("短暂活动可能仍属于刚结束的请求。", "Brief activity may belong to the previous request."), "#8B6B28"),
            OllamaActivityState.Unloaded => (L.T("服务暂无已加载模型", "No resident models reported"),
                L.T("正在加载的模型可能尚未显示。", "Models still loading may not appear yet."), "#71826D"),
            OllamaActivityState.Quiet => (L.T("已加载 · 暂无明显 GPU 活动", "Loaded · No notable GPU activity"),
                L.T("本次采样低于 5%；这不排除 CPU 推理或采样间隙中的短请求。", "This sample is below 5%. CPU inference or short requests between samples are still possible."), "#3F7C54"),
            OllamaActivityState.Observing => (L.T("检测到 GPU 活动 · 继续观察", "GPU activity detected · Observing"),
                L.T("桌宠无请求，等待持续活动确认。", "No pet requests; checking for sustained activity."), "#8B6B28"),
            OllamaActivityState.SuspectedExternal => (L.T("疑似外部推理", "Possible external inference"),
                L.T("桌宠无请求，但 Ollama 持续使用 GPU；也可能是加载或预热。", "Ollama keeps using the GPU without pet requests; it may also be loading or warming up."), "#B06A24"),
            _ => (L.T("无法判断", "Unable to determine"),
                modelError != null ? L.T("Ollama 模型状态暂不可用。", "Ollama model status is unavailable.") :
                own == null ? L.T("桌宠请求状态暂不可用。", "The pet's request status is unavailable.") :
                gpuError ?? gpu?.UnavailableReason ?? L.T("尚未取得有效的 Ollama 进程 GPU 数据。", "Valid GPU data for Ollama processes is not available yet."), "#71826D")
        };
        _ollamaActivityState.Text = title; _ollamaActivityDetail.Text = detail; _ollamaActivityState.Foreground = Ui.Brush(color);
        _ollamaActivityOwn.Text = own == null ? L.T("桌宠请求 · 未提供", "Pet requests · Unavailable") :
            L.T($"桌宠进行中的请求 · {own.ActiveInference}    加载 / 卸载 · {own.ActiveMaintenance}",
                $"Active pet requests · {own.ActiveInference}    Load / unload · {own.ActiveMaintenance}");
        bool validGpu = gpu is { Available: true, UtilizationPercent: not null } && gpu.Processes.Count > 0 &&
            double.IsFinite(gpu.UtilizationPercent.Value) && gpu.UtilizationPercent.Value is >= 0 and <= 100 &&
            DateTimeOffset.UtcNow - gpu.SampledAt <= TimeSpan.FromSeconds(15);
        _ollamaActivityGpu.Text = validGpu
            ? L.T("Ollama 进程 GPU 活动 · ", "Ollama process GPU activity · ") + gpu!.UtilizationPercent!.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%" +
                L.T("    采样 ", "    Sampled ") + gpu.SampledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : L.T("Ollama 进程 GPU 活动 · 未提供", "Ollama process GPU activity · Unavailable");
        _ollamaActivityProcesses.Text = validGpu ? string.Join("\n", gpu!.Processes.Take(8).Select(p =>
            p.Name + " · PID " + p.ProcessId + " · " + p.UtilizationPercent.ToString("0.#", CultureInfo.CurrentCulture) + "%")) : "";
    }
}
