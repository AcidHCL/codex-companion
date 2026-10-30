using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Runs on an existing WPF dispatcher, with hidden windows and fake clients only.
internal static class OllamaModelMemoryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        int saved = 0, voiceSessions = 0;
        var settings = new DesktopSettings { CodexEnabled = false, SharedMemoryEnabled = false, WebSearchEnabled = false };
        var client = new FixtureClient();
        var chat = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => saved++, client: client,
            voiceChatSessionActive: _ => voiceSessions++);
        try
        {
            string selected = settings.OllamaModel;
            check(!chat.IsVisible && chat.DashboardModelControlBlockedReason == null, "hidden_chat_model_control_is_available_without_opening_chat");
            Task<string> load = chat.SetDashboardModelLoadedAsync(true);
            await Until(() => client.Preloads == 1);
            check(chat.ModelMemoryOperationText?.Contains("加载") == true && chat.DashboardModelControlBlockedReason != null,
                "dashboard_load_exposes_operation_and_blocks_parallel_controls");
            string parallel = await chat.SetDashboardModelLoadedAsync(false);
            check(client.Unloads == 0 && parallel.Contains("正在进行"), "dashboard_double_click_cannot_unload_while_loading");
            await chat.SetModelLoadedAsync(false);
            check(client.Unloads == 0, "chat_control_shares_dashboard_operation_gate");
            client.LoadRelease.TrySetResult();
            string loaded = await load;
            check(loaded.Contains("已预加载") && chat.ModelMemoryOperationText == null && chat.DashboardModelControlBlockedReason == null,
                "dashboard_load_reports_acknowledgement_and_clears_busy_state");
            check(!chat.IsVisible && voiceSessions == 0 && saved == 0 && settings.OllamaModel == selected && client.ChatCalls == 0,
                "dashboard_model_control_keeps_chat_hidden_and_does_not_change_settings_or_send_messages");
            string unloaded = await chat.SetDashboardModelLoadedAsync(false);
            check(unloaded.Contains("已卸载") && client.Unloads == 1 && client.Models.Distinct().Single() == selected,
                "dashboard_unloads_only_current_selected_model");

            client.Installed = new[] { "other:latest" };
            string missing = await chat.SetDashboardModelLoadedAsync(true);
            check(missing.Contains("未安装") && client.Preloads == 1 && settings.OllamaModel == selected && saved == 0,
                "dashboard_missing_model_never_auto_selects_or_loads_fallback");
            client.Installed = new[] { selected }; client.ThrowOnLoad = true;
            string error = await chat.SetDashboardModelLoadedAsync(true);
            check(error.Contains("测试加载失败") && !error.Contains("已预加载") && chat.ModelMemoryOperationText == null,
                "dashboard_load_failure_is_reported_without_false_success_or_stuck_busy");
        }
        finally { chat.Shutdown(); }
        await OllamaModelSelectionChecks.RunAsync(check);
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FixtureClient : IOllamaClient
    {
        internal string[] Installed = { OllamaModels.FastModel };
        internal readonly TaskCompletionSource LoadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Models = new();
        internal int Preloads, Unloads, ChatCalls;
        internal bool ThrowOnLoad;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Installed);
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct)
            => Task.FromResult(new OllamaModelCapabilities(true, false, false, false));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages,
            [EnumeratorCancellation] CancellationToken ct)
        { ChatCalls++; await Task.CompletedTask; yield return new("不应执行", true); }
        public async Task PreloadAsync(string model, CancellationToken ct)
        {
            Models.Add(model); Preloads++;
            if (ThrowOnLoad) throw new InvalidOperationException("测试加载失败");
            await LoadRelease.Task.WaitAsync(ct);
        }
        public Task UnloadAsync(string model, CancellationToken ct)
        { Models.Add(model); Unloads++; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
