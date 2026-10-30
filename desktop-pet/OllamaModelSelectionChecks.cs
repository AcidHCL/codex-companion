using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet;

// Dispatcher fixture: selectors, persistence and model calls use the real chat
// controller with an in-memory database and fake Ollama. No model is loaded.
internal static class OllamaModelSelectionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var settings = new DesktopSettings { CodexEnabled = false, SharedMemoryEnabled = false, WebSearchEnabled = false };
        var client = new FixtureClient();
        int saves = 0, voiceSessions = 0; bool failSave = false;
        var store = new ChatMemoryStore(":memory:");
        var conversation = store.CreateConversation(OllamaEndpoint.DefaultAddress, "deepseek", settings.OllamaModel);
        store.AppendTurn(conversation.Id, new ChatStoredTurn(0, "原来的问题", "原来的回答", settings.OllamaModel,
            Array.Empty<ChatStoredImage>(), Array.Empty<PetWebSource>(), null, DateTimeOffset.UtcNow));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4)));
        using var buffer = new MemoryStream(); encoder.Save(buffer);
        var image = new ChatStoredImage("fixture.png", Convert.ToBase64String(buffer.ToArray()));
        store.SaveDraft(conversation.Id, "尚未发送的草稿", new[] { image }); store.SetActiveConversation(conversation.Id);
        var chat = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { },
            () => { if (failSave) throw new IOException("fixture save failure"); saves++; },
            client: client, chatStore: store, voiceChatSessionActive: _ => voiceSessions++);
        var selector = Field<ComboBox>(chat, "_models");
        var input = Field<TextBox>(chat, "_input");
        var originalImage = Field<List<OllamaChatImage>>(chat, "_pendingImages").Single();
        var notices = new List<(string Settings, string Chat, string? Selector, string? Blocked)>();
        chat.ModelSelectionChanged += () => notices.Add((settings.OllamaModel, chat.SelectedModel, selector.SelectedValue as string,
            chat.DashboardModelControlBlockedReason));
        try
        {
            string chosen = OllamaModels.QualityModel;
            string result = await chat.SelectDashboardModelAsync(chosen);
            check(result.Contains("已切换") && settings.OllamaModel == chosen && chat.SelectedModel == chosen && (string?)selector.SelectedValue == chosen,
                "dashboard_selection_commits_same_model_to_chat_selector_and_settings");
            check(notices.Count == 1 && notices[0] == (chosen, chosen, chosen, null),
                "selection_notification_contains_committed_state_after_busy_gate_released");
            check(!chat.IsVisible && voiceSessions == 0 && client.Preloads == 0 && client.ChatCalls == 0 && client.Unloads.Count == 0,
                "hidden_dashboard_selection_does_not_open_chat_load_models_or_begin_voice");
            check(chat.CurrentConversationId == conversation.Id && input.Text == "尚未发送的草稿" &&
                ReferenceEquals(Field<List<OllamaChatImage>>(chat, "_pendingImages").Single(), originalImage) &&
                store.ReadTurns(conversation.Id).Single().Assistant == "原来的回答",
                "model_selection_preserves_conversation_history_draft_and_images");

            selector.SelectedValue = OllamaModels.FastModel;
            await Until(() => chat.SelectedModel == OllamaModels.FastModel && chat.DashboardModelControlBlockedReason == null);
            check(settings.OllamaModel == OllamaModels.FastModel && notices.Count == 2 && notices[^1].Settings == OllamaModels.FastModel && notices[^1].Blocked == null,
                "chat_selector_uses_shared_switch_path_and_notifies_dashboard_immediately");

            int beforeFailure = notices.Count, beforeSaves = saves;
            result = await chat.SelectDashboardModelAsync("missing:latest");
            check(result.Contains("未安装") && settings.OllamaModel == OllamaModels.FastModel && (string?)selector.SelectedValue == OllamaModels.FastModel && notices.Count == beforeFailure && saves == beforeSaves,
                "missing_dashboard_selection_preserves_both_selectors_without_fallback");
            client.FailCapabilities = true;
            result = await chat.SelectDashboardModelAsync(chosen);
            client.FailCapabilities = false;
            check(result.Contains("保留原模型") && chat.SelectedModel == OllamaModels.FastModel && notices.Count == beforeFailure,
                "capability_read_failure_keeps_original_selection_and_notification_state");
            failSave = true; result = await chat.SelectDashboardModelAsync(chosen); failSave = false;
            check(result.Contains("保留原模型") && settings.OllamaModel == OllamaModels.FastModel && chat.SelectedModel == OllamaModels.FastModel && (string?)selector.SelectedValue == OllamaModels.FastModel && notices.Count == beforeFailure,
                "persistence_failure_rolls_back_shared_model_choice");

            int beforeLists = client.ListCalls;
            result = await chat.SelectDashboardModelAsync("invalid model with spaces");
            check(client.ListCalls == beforeLists && result.Contains("请选择"), "invalid_selection_is_rejected_before_network_or_persistence");

            var load = chat.SetDashboardModelLoadedAsync(true);
            await Until(() => client.Preloads == 1);
            result = await chat.SelectDashboardModelAsync(chosen);
            check(result.Contains("正在进行") && settings.OllamaModel == OllamaModels.FastModel && client.Unloads.Count == 0,
                "dashboard_selector_cannot_race_manual_model_loading");
            client.PreloadRelease.TrySetResult(); await load;
            result = await chat.SelectDashboardModelAsync(chosen);
            check(result.Contains("已切换") && client.Unloads.SequenceEqual(new[] { OllamaModels.FastModel }) && client.Preloads == 1,
                "selection_releases_only_previously_used_model_without_preloading_new_model");

            int beforeRefresh = notices.Count;
            await chat.CheckConnectionAsync();
            check(notices.Count == beforeRefresh + 1 && notices[^1] == (chosen, chosen, chosen, null),
                "connection_model_list_refresh_notifies_only_after_committed_state");
        }
        finally { chat.Shutdown(); }
    }

    private static T Field<T>(OllamaChatWindow chat, string field) => (T)typeof(OllamaChatWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chat)!;
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FixtureClient : IOllamaClient
    {
        internal readonly List<string> Unloads = new();
        internal readonly TaskCompletionSource PreloadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ListCalls, Preloads, ChatCalls;
        internal bool FailCapabilities;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        { ListCalls++; return Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel, OllamaModels.QualityModel }); }
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct)
            => FailCapabilities ? Task.FromException<OllamaModelCapabilities>(new IOException("fixture read failure")) : Task.FromResult(new OllamaModelCapabilities(true, true, false, false));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        { ChatCalls++; await Task.CompletedTask; yield return new("不应执行", true); }
        public async Task PreloadAsync(string model, CancellationToken ct)
        { Preloads++; await PreloadRelease.Task.WaitAsync(ct); }
        public Task UnloadAsync(string model, CancellationToken ct) { Unloads.Add(model); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
