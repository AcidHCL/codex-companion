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
