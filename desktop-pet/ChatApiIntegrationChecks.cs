using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

internal static class ChatApiIntegrationChecks
{
    internal static int Run()
    {
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        var checks = new List<string>();
        app.Startup += async (_, _) =>
        {
            OllamaChatWindow? window = null;
            string? error = null;
            object? transport = null, storage = null;
            try
            {
                void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
                transport = await OpenAiCompatibleClientChecks.RunAsync();
                storage = ChatApiSettingsChecks.Run(AppContext.BaseDirectory);
                await AutomaticMemoryJudgeChecks.RunAsync();
                await CheckAutomaticMemoryAsync(Check);
                await CheckHiddenApiReactionsAsync(Check);
                string fixture = Path.Combine(AppContext.BaseDirectory, "api-integration-fixture-" + Guid.NewGuid().ToString("N"));
                var keys = new ApiKeyStore(fixture);
                var settings = new DesktopSettings { OllamaKeepAliveMinutes = 30, OllamaModel = "local-model", WebSearchEnabled = false, SpeechEnabled = false };
                var local = new FakeClient(OllamaEndpoint.Parse(settings.OllamaAddress), "local-model");
                var apis = new List<FakeClient>();
                var locals = new List<FakeClient> { local };
                var store = new ChatMemoryStore(":memory:");
                bool failSave = false;
                window = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { },
                    () => { if (failSave) throw new IOException("fixture save failure"); }, local,
                    uri => { var c = new FakeClient(uri, "local-model"); locals.Add(c); return c; }, chatStore: store, apiKeys: keys,
                    apiClientFactory: profile => { var c = new FakeClient(profile.Endpoint, profile.Model, "other-model"); apis.Add(c); return c; });
                // No visible fixture window, microphone, model or network access.
                window.Measure(new Size(460, 550)); window.Arrange(new Rect(0, 0, 460, 550));
                await window.CheckConnectionAsync();
                var input = Find<TextBox>((DependencyObject)window.Content, "OllamaChatInput");
                input.Text = "local private conversation";
                await window.SendAsync();
                string localId = window.CurrentConversationId!;
                Check(store.ReadTurns(localId).Count == 1, "local_chat_fixture_persisted");
                var profile = new ChatApiProfile { BaseUrl = "https://api.example.test/v1", Model = "api-model", SupportsImages = true, SupportsTools = true };
                await window.ApplyChatApiConnectionAsync(new(true, profile, ApiKeyChange.Replace, "fixture-api-key"), CancellationToken.None);
                string apiId = window.CurrentConversationId!;
                Check(settings.UseChatApi && window.SelectedModel == "api-model", "api_selection_commits_to_shared_settings");
                Check(settings.OllamaModel == "local-model" && settings.OllamaKeepAliveMinutes == 30, "original_ollama_model_and_retention_preserved");
                Check(local.Unloads == 0 && apis[0].Calls == 0 && apis[0].Lists == 0, "provider_apply_does_not_infer_unload_or_query");
                Check(apiId != localId && store.ReadTurns(apiId).Count == 0, "provider_switch_starts_isolated_conversation");
                Check(keys.Read(profile.Endpoint) == "fixture-api-key", "new_key_saved_to_matching_endpoint");
                input.Text = "api hello"; await window.SendAsync();
                Check(apis[0].Calls == 1 && !apis[0].LastMessages.Any(m => m.Content.Contains("local private")), "old_local_history_never_sent_to_api");
                Check(apis[0].LastMessages[0].Content.Contains("API"), "persona_truthfully_identifies_api_backend");
                Check(store.ReadTurns(apiId).Count == 1, "api_reply_saved_in_local_history");
                await window.CheckConnectionWhenIdleAsync();
                Check(apis[0].Lists == 0, "automatic_health_check_does_not_poll_api");
                apis[0].FailList = true;
                await window.CheckConnectionAsync();
                input.Text = "manual model after metadata denied"; await window.SendAsync();
                Check(apis[0].Calls == 2, "manual_model_can_chat_when_metadata_permission_denied");
                apis[0].FailList = false;
                await window.SetDashboardModelLoadedAsync(true); await window.SetDashboardModelLoadedAsync(false);
                Check(apis[0].Loads == 0 && apis[0].Unloads == 0, "remote_model_cannot_be_loaded_or_unloaded");
                await window.SelectDashboardModelAsync("other-model");
                Check(window.SelectedModel == "other-model" && settings.ApiProfile.Model == "other-model" && window.CurrentConversationId == apiId, "dashboard_api_model_switch_preserves_chat_and_syncs");
                Check(apis[0].Unloads == 0, "api_model_switch_never_unloads");
                var alternative = profile with { BaseUrl = "https://api.example.test/other/v1", Model = "api-model" };
                await window.ApplyChatApiConnectionAsync(new(true, alternative, ApiKeyChange.Keep, null), CancellationToken.None);
                Check(window.CurrentConversationId != apiId && keys.Read(alternative.Endpoint) == null, "same_host_different_base_path_has_isolated_key_and_history");
                await window.ApplyChatApiConnectionAsync(new(true, profile, ApiKeyChange.Keep, null), CancellationToken.None);
                Check(window.CurrentConversationId == apiId && store.ReadTurns(apiId).Count == 2, "returning_api_restores_saved_conversation");
                failSave = true;
                bool failed = false;
                try { await window.ApplyChatApiConnectionAsync(new(true, alternative, ApiKeyChange.Replace, "fixture-other-key"), CancellationToken.None); }
                catch (IOException) { failed = true; }
                failSave = false;
                Check(failed && settings.ApiProfile.BaseUrl == profile.BaseUrl && keys.Read(alternative.Endpoint) == null && window.CurrentConversationId == apiId, "failed_settings_save_rolls_back_profile_key_and_active_chat");
                await window.ApplyChatApiConnectionAsync(new(false, profile, ApiKeyChange.Keep, null), CancellationToken.None);
                Check(!settings.UseChatApi && window.CurrentConversationId == localId && store.ReadTurns(localId).Count == 1, "switching_back_restores_original_local_chat");
                Check(locals.All(x => x.Loads == 0 && x.Unloads == 0), "provider_switch_preserves_local_model_residency");
                Check(!JsonSerializer.Serialize(settings).Contains("fixture-api-key"), "settings_json_contains_no_api_secret");
                Check(ChatModelConnection.Scope(new Uri("https://same.test/"), true) != ChatModelConnection.Scope(new Uri("https://same.test/"), false), "provider_scope_separated_even_at_same_root_url");
                window.Shutdown(); window = null;
                result = 0;
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally { window?.Shutdown(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chat-api-validation.json"), JsonSerializer.Serialize(new
                { passed = result == 0, version = AppVersion.Number, checks, error, transport, storage, realApiRequests = 0, userSettingsWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(result);
        };
        app.Run(); return result;
    }

    private static T Find<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T value && value.Name == name) return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { try { return Find<T>(VisualTreeHelper.GetChild(parent, i), name); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException(name);
    }

    private static async Task CheckAutomaticMemoryAsync(Action<bool, string> check)
    {
        const string milestone = "我们的第一个双人解谜游戏终于上线了！";
        const string spoken = "今天得盖个鲸尾章庆祝。";
        const string mouseReply = "再点一下，这枚鲸尾邮票可就盖好了。";
        string mouseSummary = new PetInteractionEvent("clicked", default, 0, ClickCount: 3).DescribeForChat(false);
        var profile = new ChatApiProfile { BaseUrl = "https://memory.example.test/v1", Model = "memory-fixture" };
        var settings = new DesktopSettings { UseChatApi = true, ApiProfile = profile, SharedMemoryEnabled = true,
            AutomaticMemoryEnabled = true, WebSearchEnabled = false, LlmBehaviorEnabled = false, SpeechEnabled = false };
        int chatCalls = 0, reviewCalls = 0;
        string latestSystem = "";
        var mouseEvidence = new List<(string Summary, string Reply, bool NoUserText)>();
        bool holdReview = false;
        var began = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ApiHandler(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = payload.RootElement.GetProperty("messages");
            string input = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
            string reply = "听起来不错。";
            if (input.StartsWith("{", StringComparison.Ordinal))
            {
                using var data = JsonDocument.Parse(input);
                var source = data.RootElement;
                ++reviewCalls;
                if (holdReview) { began.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); }
                if (source.GetProperty("source").GetString() == "displayed_companion_event")
                {
                    string summary = source.GetProperty("event_summary").GetString()!;
                    string petReply = source.GetProperty("pet_reply").GetString()!;
                    if (summary == mouseSummary)
                    {
                        mouseEvidence.Add((summary, petReply, source.GetProperty("user_text").ValueKind == JsonValueKind.Null));
                        reply = petReply == mouseReply
                            ? JsonSerializer.Serialize(new {kind="moment",title="鲸尾邮票",content="桌宠把被连点打趣成盖鲸尾邮票。",evidence="鲸尾邮票"})
                            : "null";
                    }
                    else reply = JsonSerializer.Serialize(new {kind="moment",title="鲸尾章",content="桌宠用鲸尾章的玩笑庆祝游戏首发。",evidence="鲸尾章"});
                }
                else reply = source.GetProperty("user_text").GetString() == milestone
                        ? JsonSerializer.Serialize(new {kind="user",title="游戏上线",content="用户的第一个双人解谜游戏已经上线。",evidence="双人解谜游戏终于上线"})
                        : "null";
            }
            else
            {
                ++chatCalls;
                latestSystem = messages[0].GetProperty("content").GetString()!;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = reply } } } }),
                System.Text.Encoding.UTF8, "application/json") };
        });
        var store = new ChatMemoryStore(":memory:");
        var client = new OpenAiCompatibleClient(profile.Endpoint, () => null, profile.Model, false, false, handler);
        var chat = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => { }, client, chatStore: store);
        try
        {
            chat.Measure(new Size(460, 550)); chat.Arrange(new Rect(0, 0, 460, 550));
            var input = Find<TextBox>((DependencyObject)chat.Content, "OllamaChatInput");
            string scope = ChatModelConnection.Scope(profile.Endpoint, true);
            input.Text = milestone;
            await chat.SendAsync();
            await chat.PendingAutomaticMemoryForTesting.WaitAsync(TimeSpan.FromSeconds(8));
            var first = store.ListMemories(scope).Single();
            check(first.IsAutomatic && first.Content.Contains("双人解谜游戏"), "completed_chat_produces_a_labeled_automatic_memory");
            await chat.NewConversationAsync();
            string id = chat.CurrentConversationId!;
            input.Text = "我们的解谜游戏进展怎么样了？";
            await chat.SendAsync();
            await chat.PendingAutomaticMemoryForTesting.WaitAsync(TimeSpan.FromSeconds(8));
            check(latestSystem.Contains("双人解谜游戏") && store.ListMemories(scope).Count == 1 && chatCalls == 2,
                "new_chat_recalls_learned_milestone_and_null_decision_writes_nothing");
            check(chat.TrySaveCompanionReaction(id, spoken, "用户和桌宠庆祝游戏首发。"), "accepted_companion_remark_enters_chat");
            await chat.PendingAutomaticMemoryForTesting.WaitAsync(TimeSpan.FromSeconds(8));
            check(store.ListMemories(scope).Any(n => n.IsAutomatic && n.Content.Contains("鲸尾章")) && reviewCalls == 3 &&
                store.ReadTurns(id).Count == 2, "companion_moment_crosses_chats_without_extra_chat_turn");
            const string ordinary = "嗯，怎么啦？";
            check(chat.TrySaveCompanionReaction(id, ordinary, mouseSummary) &&
                chat.TrySaveCompanionReaction(id, mouseReply, mouseSummary), "mouse_remarks_enter_chat_before_memory_review");
            await chat.PendingAutomaticMemoryForTesting.WaitAsync(TimeSpan.FromSeconds(8));
            check(mouseEvidence.SequenceEqual(new[] { (mouseSummary, ordinary, true), (mouseSummary, mouseReply, true) }) &&
                store.ListMemories(scope).Count == 3 && store.ListMemories(scope).Any(n => n.IsAutomatic && n.Title == "鲸尾邮票") &&
                store.ReadTurns(id).Count == 4, "judge_receives_only_factual_mouse_summary_and_spoken_reply_and_can_decline_or_keep_it");

            // A disabled setting must win over an in-flight decision, even if the provider returns later.
            holdReview = true;
            chat.TrySaveCompanionReaction(id, spoken, "桌宠再次提起游戏首发。");
            await began.Task.WaitAsync(TimeSpan.FromSeconds(8));
            settings.AutomaticMemoryEnabled = false;
            input.Text = "新问题优先。";
            await chat.SendAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await chat.PendingAutomaticMemoryForTesting.WaitAsync(TimeSpan.FromSeconds(4));
            check(chatCalls == 3 && store.ListMemories(scope).Count == 3 && reviewCalls == 6,
                "foreground_preempts_review_and_disable_prevents_a_late_automatic_write");
            input.Text = "记住：以后称呼我小鲸。"; await chat.SendAsync();
            check(store.ListMemories(scope).Any(n => !n.IsAutomatic && n.Content.Contains("小鲸")) && reviewCalls == 6,
                "explicit_save_still_works_with_learning_off");
        }
        finally { chat.Shutdown(); }
    }

    private static async Task CheckHiddenApiReactionsAsync(Action<bool, string> check)
    {
        int requests = 0;
        var settings = new DesktopSettings { UseChatApi = true, LlmBehaviorEnabled = true,
            ApiProfile = new() { BaseUrl = "https://api.openai.com/v1", Model = "fixture-chat" },
            WebSearchEnabled = false, SpeechEnabled = false };
        var handler = new ApiHandler((request, _) =>
        {
            requests++;
            check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "hidden_api_reaction_uses_inference_only_on_actual_event_" + requests);
            if (requests == 1) throw new HttpRequestException("fixture transient failure");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"你好呀。\"}}]}",
                System.Text.Encoding.UTF8, "application/json") });
        });
        var client = new OpenAiCompatibleClient(settings.ApiProfile.Endpoint, () => "fixture-only-key", settings.ApiProfile.Model, false, false, handler);
        var hidden = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => { }, client,
            behaviorHost: new SimpleBehaviorHost());
        try
        {
            check(!hidden.IsVisible && hidden.CanReactToInteraction && requests == 0, "api_reactions_ready_after_restart_without_opening_chat_or_network_probe");
            var reaction = new PetInteractionEvent("clicked", DateTimeOffset.UtcNow, 1);
            check(await hidden.ReactToInteractionAsync(reaction, CancellationToken.None) == null && !hidden.CanReactToInteraction,
                "api_reaction_failure_is_silent_and_temporarily_unready");
            await hidden.CheckConnectionWhenIdleAsync();
            check(hidden.CanReactToInteraction && requests == 1, "api_reaction_rearms_after_failure_without_paid_or_metadata_probe");
            check(await hidden.ReactToInteractionAsync(reaction, CancellationToken.None) == "你好呀。" && requests == 2,
                "next_click_uses_current_api_after_recovery");
        }
        finally { hidden.Shutdown(); }
    }

    private sealed class ApiHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class SimpleBehaviorHost : IPetBehaviorHost
    {
        public Task<PetBehaviorSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(new PetBehaviorSnapshot("deepseek", "idle", 1, true, true, false, Array.Empty<string>()));
        public Task<PetBehaviorResult> ExecuteAsync(PetBehaviorCommand command, long expectedRevision, CancellationToken ct) => throw new InvalidOperationException("This fixture exposes no tools.");
    }

    private sealed class FakeClient(Uri endpoint, params string[] models) : IOllamaClient
    {
        public Uri Endpoint => endpoint;
        internal int Calls, Lists, Loads, Unloads;
        internal bool FailList;
        internal IReadOnlyList<OllamaMessage> LastMessages = Array.Empty<OllamaMessage>();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) { Lists++; if (FailList) throw new OllamaException("Fixture metadata denied"); return Task.FromResult<IReadOnlyList<string>>(models); }
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(new OllamaModelCapabilities(true, true, false, true));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        { Calls++; LastMessages = messages.ToArray(); await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new("Hello", false); yield return new("", true); }
        public Task PreloadAsync(string model, CancellationToken ct) { Loads++; return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { Unloads++; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
