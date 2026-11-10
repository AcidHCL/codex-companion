using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPet.WorkCompanion;

// Entirely in-memory HTTP, history and settings. No real desktop, API key,
// model, graphics telemetry or user settings are accessed by these checks.
internal static class ApiWorkReactionChatChecks
{
    internal static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("API companion: " + name); checks.Add(name); }
        using (var fixture = new Fixture((_, _) => Task.FromResult(Completion("{\"text\":\"我在旁边陪你。\",\"expression\":\"happy\"}"))))
        {
            Check(fixture.Window.WorkCompanionUsesApi && fixture.Window.CanReactToWork && !fixture.Window.IsVisible &&
                fixture.ApiBodies.Count == 0 && fixture.OllamaPaths.Count == 0, "hidden_api_host_uses_current_model_without_poll_or_residency");
            fixture.Input.Text = "SECRET_UNSENT_DRAFT"; fixture.Window.SaveDraftForTesting();
            string snapshot = fixture.Snapshot();
            using var otherLocalWork = OllamaRequestActivity.Begin(OllamaEndpoint.Parse(fixture.Settings.OllamaAddress), maintenance: true);
            var reply = await fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None);
            Check(reply == new WorkReactionReply("我在旁边陪你。", "happy"), "selected_api_reacts_even_while_ollama_has_maintenance_work");
            Check(fixture.ApiBodies.Count == 1 && fixture.OllamaPaths.Count == 0 &&
                fixture.ApiPaths.All(p => p == "/v1/chat/completions"), "api_companion_never_queries_ollama_or_api_model_residency");
            using var payload = JsonDocument.Parse(fixture.ApiBodies[0]);
            var messages = payload.RootElement.GetProperty("messages");
            Check(payload.RootElement.GetProperty("model").GetString() == Fixture.Model &&
                messages.GetArrayLength() == 2 && !payload.RootElement.TryGetProperty("tools", out _) &&
                !payload.RootElement.TryGetProperty("keep_alive", out _) && !payload.RootElement.TryGetProperty("options", out _),
                "api_companion_request_has_only_current_model_and_stateless_messages");
            Check(!fixture.ApiBodies[0].Contains("SECRET_", StringComparison.Ordinal) && snapshot == fixture.Snapshot() &&
                fixture.SayCalls == 0 && fixture.BusyCalls == 0 && fixture.SaveCalls == 0,
                "api_companion_does_not_send_or_write_chat_memory_drafts_or_present_output");
            Check(fixture.Settings.OllamaModel == Fixture.LocalModel && fixture.Settings.OllamaKeepAliveMinutes == -1,
                "api_companion_preserves_original_ollama_settings");
            var pet = HiddenPet(fixture.Settings);
            using var host = new PetWorkCompanionHost(pet, () => fixture.Window, () => fixture.Window);
            try
            {
                string original = host.Capture().ContextKey;
                fixture.Settings.OllamaModel = "irrelevant-local-model";
                Check(host.Capture().ContextKey == original, "api_context_ignores_inactive_ollama_model");
                fixture.Settings.ApiProfile = fixture.Settings.ApiProfile with { SupportsImages = true };
                Check(host.Capture().ContextKey != original, "api_capability_change_invalidates_work_context");
                original = host.Capture().ContextKey;
                fixture.Settings.ApiProfile = fixture.Settings.ApiProfile with { BaseUrl = fixture.Settings.ApiProfile.BaseUrl.Replace("/v1", "/other/v1", StringComparison.Ordinal) };
                Check(host.Capture().ContextKey != original, "api_full_base_path_change_invalidates_work_context");
            }
            finally { host.Dispose(); pet.Close(); }
        }

        foreach (bool toOllama in new[] { false, true })
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken transportToken = default;
            using var fixture = new Fixture(async (_, ct) =>
            {
                transportToken = ct; started.TrySetResult(true); await released.Task;
                return Completion("{\"text\":\"LATE_REPLY\",\"expression\":\"happy\"}");
            });
            var work = fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None);
            Task? change = null;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var profile = fixture.Settings.ApiProfile with { Model = "other-api-model" };
                change = fixture.Window.ApplyChatApiConnectionAsync(new(!toOllama, profile, ApiKeyChange.Keep, null), CancellationToken.None);
                Check(transportToken.IsCancellationRequested && !change.IsCompleted && !work.IsCompleted,
                    "global_switch_cancels_and_drains_active_api_reaction_" + toOllama);
                released.TrySetResult(true);
                Check(await work.WaitAsync(TimeSpan.FromSeconds(5)) == null,
                    "late_api_reaction_discarded_after_global_switch_" + toOllama);
                await change.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Window.SelectedModel == (toOllama ? Fixture.LocalModel : "other-api-model") &&
                    fixture.Window.WorkCompanionUsesApi == !toOllama && fixture.Settings.UseChatApi == !toOllama,
                    "companion_follows_new_global_provider_and_model_" + toOllama);
                Check(fixture.OllamaPaths.All(p => p is "/api/tags" or "/api/show") && fixture.SayCalls == 0,
                    "switch_never_loads_unloads_or_presents_old_reaction_" + toOllama);
            }
            finally { released.TrySetResult(true); fixture.Window.CancelWorkReaction(); await work; if (change != null) await change; }
        }

        string scheduledSource = "EXTERNAL_SCHEDULE_RESULT: " + new string('x', 500);
        using (var fixture = new Fixture((_, _) => Task.FromResult(Completion("Processed 42 records successfully."))))
        {
            fixture.Input.Text = "SECRET_UNSENT_DRAFT"; fixture.Window.SaveDraftForTesting();
            string before = fixture.Snapshot();
            var endpoint = fixture.Settings.ApiProfile.Endpoint;
            var shortResult = await fixture.Window.DescribeScheduledOutputAsync("Short result", Fixture.Model, endpoint, CancellationToken.None);
            Check(shortResult.Output == "Short result" && !shortResult.IsSummary && fixture.ApiBodies.Count == 0,
                "short_scheduled_output_does_not_query_any_model");
            var result = await fixture.Window.DescribeScheduledOutputAsync(scheduledSource, Fixture.Model, endpoint, CancellationToken.None);
            Check(result is { Output: "Processed 42 records successfully.", IsSummary: true } &&
                fixture.ApiBodies.Count == 1 && fixture.OllamaPaths.Count == 0,
                "long_scheduled_output_uses_selected_api_without_ollama_residency_query");
            using var payload = JsonDocument.Parse(fixture.ApiBodies[0]);
            var messages = payload.RootElement.GetProperty("messages");
            Check(payload.RootElement.GetProperty("model").GetString() == Fixture.Model &&
                messages.GetArrayLength() == 2 && messages[1].GetProperty("content").GetString() == scheduledSource &&
                !payload.RootElement.TryGetProperty("tools", out _) && !fixture.ApiBodies[0].Contains("SECRET_", StringComparison.Ordinal),
                "scheduled_summary_has_only_current_model_instruction_and_source");
            Check(before == fixture.Snapshot() && fixture.SayCalls == 0 && fixture.SaveCalls == 0 && fixture.BusyCalls == 0,
                "api_summary_does_not_change_history_memory_draft_settings_or_present_itself");
            Check(await fixture.Window.DescribeScheduledOutputAsync(scheduledSource, "stale-model", endpoint, CancellationToken.None) ==
                ScheduledTaskAnnouncementResult.Fallback && fixture.ApiBodies.Count == 1,
                "stale_scheduled_model_selection_cannot_issue_request");
        }

        foreach (bool toOllama in new[] { false, true })
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken transportToken = default;
            using var fixture = new Fixture(async (_, ct) =>
            {
                transportToken = ct; started.TrySetResult(true); await released.Task;
                return Completion("LATE_SUMMARY");
            });
            var summary = fixture.Window.DescribeScheduledOutputAsync(scheduledSource, Fixture.Model,
                fixture.Settings.ApiProfile.Endpoint, CancellationToken.None);
            Task? change = null;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var profile = fixture.Settings.ApiProfile with { Model = "other-api-model" };
                change = fixture.Window.ApplyChatApiConnectionAsync(new(!toOllama, profile, ApiKeyChange.Keep, null), CancellationToken.None);
                Check(transportToken.IsCancellationRequested && !summary.IsCompleted && !change.IsCompleted,
                    "global_switch_cancels_and_drains_active_summary_" + toOllama);
                released.TrySetResult(true);
                bool canceled = false;
                try { await summary.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "late_scheduled_summary_cannot_survive_global_switch_" + toOllama);
                await change.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Window.SelectedModel == (toOllama ? Fixture.LocalModel : "other-api-model") &&
                    fixture.Settings.UseChatApi == !toOllama && fixture.SayCalls == 0 &&
                    fixture.OllamaPaths.All(p => p is "/api/tags" or "/api/show"),
                    "summary_cancellation_does_not_break_provider_switch_or_load_models_" + toOllama);
            }
            finally
            {
                released.TrySetResult(true);
                try { await summary; } catch (OperationCanceledException) { }
                if (change != null) await change;
            }
        }

        using (var fixture = new Fixture((_, _) => throw new HttpRequestException("fixture private upstream failure")))
        {
            string before = fixture.Snapshot();
            Check(await fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None) == null &&
                fixture.Window.CanReactToWork && before == fixture.Snapshot() && fixture.OllamaPaths.Count == 0,
                "failed_api_background_request_stays_silent_without_ollama_fallback_or_history_write");
        }
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new Fixture((_, _) => throw new InvalidOperationException("A stale preflight must not infer."), skinId: "gemini");
            fixture.Settings.OllamaAddress = "http://preflight-" + Guid.NewGuid().ToString("N") + ".invalid:11434";
            fixture.Settings.Topmost = false; fixture.Settings.MotionEnabled = false; fixture.Settings.SoundEnabled = false;
            string apiConversation = fixture.Window.CurrentConversationId!;
            var apiProfile = fixture.Settings.ApiProfile;
            await fixture.Window.ApplyChatApiConnectionAsync(new(false, apiProfile, ApiKeyChange.Keep, null), CancellationToken.None);
            string localConversation = fixture.Window.CurrentConversationId!;
            string StoredContents() => JsonSerializer.Serialize(new
            {
                api = fixture.Store.ReadTurns(apiConversation), local = fixture.Store.ReadTurns(localConversation),
                memory = fixture.Store.ListMemories(fixture.Scope)
            });
            string before = StoredContents();
            var pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), fixture.Settings, persist: false,
                motionPreferences: () => new MotionPreferences(false, false),
                querySession: () => new WindowsSessionSnapshot(42, false, true, false), subscribeToSystemEvents: false,
                movementPlatformProvider: _ => new DesktopPlatformSnapshot(new[] { new Rect(0, 0, 1920, 1080) }, Array.Empty<DesktopPlatform>()))
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
            using var host = new PetWorkCompanionHost(pet, () => fixture.Window, () => fixture.Window,
                readActivity: async (_, ct) =>
                {
                    entered.TrySetResult(true); await released.Task.WaitAsync(ct);
                    return new OllamaGpuActivity(true, null, 0, Array.Empty<OllamaGpuProcess>(), DateTimeOffset.UtcNow);
                });
            Task<WorkReactionReply?>? preflight = null;
            try
            {
                pet.Show();
                var until = DateTimeOffset.UtcNow.AddSeconds(5);
                while (!host.Capture().Available && DateTimeOffset.UtcNow < until) await Task.Delay(20);
                Check(host.Capture().Available, "preflight_fixture_is_available_without_visible_pet_or_desktop_observer");
                preflight = host.GenerateAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                long revision = fixture.Window.ModelConnectionRevision;
                await fixture.Window.ApplyChatApiConnectionAsync(new(true, apiProfile, ApiKeyChange.Keep, null), CancellationToken.None);
                Check(fixture.Window.ModelConnectionRevision != revision && fixture.Window.WorkCompanionUsesApi,
                    "global_provider_commit_changes_revision_while_preflight_waits");
                released.TrySetResult(true);
                Check(await preflight.WaitAsync(TimeSpan.FromSeconds(5)) == null && fixture.ApiBodies.Count == 0 &&
                    fixture.OllamaPaths.All(path => path is "/api/tags" or "/api/show"),
                    "provider_changed_during_gpu_preflight_never_submits_old_candidate_to_new_api");
                Check(before == StoredContents() && fixture.SayCalls == 0,
                    "stale_preflight_does_not_write_history_memory_or_present_reply");
            }
            finally { released.TrySetResult(true); if (preflight != null) await preflight; host.Dispose(); pet.Close(); }
        }
        using (var fixture = new Fixture((_, _) => throw new InvalidOperationException("Unconfigured API must not infer."), missingOfficialKey: true))
        {
            Check(!fixture.Window.CanReactToWork && fixture.Window.WorkCompanionModelBlockReason != null &&
                await fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None) == null &&
                fixture.ApiPaths.Count == 0 && fixture.OllamaPaths.Count == 0,
                "missing_official_api_key_skips_background_before_any_request_and_explains_status");
        }
        return new { passed = true, checks, realModelRequests = 0, observedDesktop = false, userSettingsWrites = 0 };
    }

    private static HttpResponseMessage Completion(string content) => OllamaWorkReactionChecks.Json(JsonSerializer.Serialize(new
    { choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } } }));

    private static PetWindow HiddenPet(DesktopSettings settings) => new(SkinCatalog.Load(AppContext.BaseDirectory), settings,
        persist: false, motionPreferences: () => new MotionPreferences(false, false),
        querySession: () => new WindowsSessionSnapshot(42, false, true, false), subscribeToSystemEvents: false);

    private sealed class Fixture : IDisposable
    {
        internal const string Model = "provider/companion-model", LocalModel = "local-companion:latest";
        internal readonly DesktopSettings Settings;
        internal readonly ChatMemoryStore Store = new(":memory:");
        internal readonly OllamaChatWindow Window;
        internal readonly TextBox Input;
        internal readonly string Scope;
        internal readonly List<string> ApiPaths = new(), ApiBodies = new(), OllamaPaths = new();
        internal int SayCalls, BusyCalls, SaveCalls;

        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> chat, bool missingOfficialKey = false, string skinId = "deepseek")
        {
            var endpoint = missingOfficialKey ? new Uri("https://api.openai.com/v1/") :
                new Uri("https://companion-" + Guid.NewGuid().ToString("N") + ".example.test/v1/");
            Scope = ChatModelConnection.Scope(endpoint, true);
            Settings = new DesktopSettings { UseChatApi = true, ApiProfile = new() { BaseUrl = endpoint.AbsoluteUri, Model = Model },
                SkinId = skinId, OllamaModel = LocalModel, OllamaKeepAliveMinutes = -1, LlmBehaviorEnabled = false,
                WebSearchEnabled = false, SpeechEnabled = false, CodexEnabled = false, NotificationsEnabled = false };
            OpenAiCompatibleClient Api(ChatApiProfile profile) => new(profile.Endpoint, () => missingOfficialKey ? null : "fixture-only-key", profile.Model, false, false,
                new OllamaWorkReactionChecks.Handler(async (request, ct) =>
                {
                    ApiPaths.Add(request.RequestUri!.AbsolutePath);
                    if (request.RequestUri.AbsolutePath != "/v1/chat/completions") throw new InvalidOperationException("Unexpected API route.");
                    ApiBodies.Add(await request.Content!.ReadAsStringAsync(ct)); return await chat(request, ct);
                }));
            Store.UpsertMemory(new(Guid.NewGuid().ToString("N"), Scope, "SECRET_MEMORY", "SECRET_MEMORY_CONTENT", true, DateTimeOffset.UtcNow));
            Window = new OllamaChatWindow(Settings, () => "DeepSeek", _ => BusyCalls++, _ => SayCalls++, () => SaveCalls++,
                client: Api(Settings.ApiProfile),
                clientFactory: uri => new OllamaClient(uri, new OllamaWorkReactionChecks.Handler((request, _) =>
                {
                    string path = request.RequestUri!.AbsolutePath; OllamaPaths.Add(path);
                    return Task.FromResult(path switch
                    {
                        "/api/tags" => OllamaWorkReactionChecks.Json(OllamaWorkReactionChecks.Models(LocalModel)),
                        "/api/show" => OllamaWorkReactionChecks.Json("{\"capabilities\":[\"completion\"]}"),
                        _ => throw new InvalidOperationException("Unexpected Ollama inference or residency route.")
                    });
                }), () => Settings.OllamaKeepAliveMinutes),
                chatStore: Store, apiClientFactory: Api)
            { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
            Store.AppendTurn(Window.CurrentConversationId!, new(0, "SECRET_USER_HISTORY", "SECRET_ASSISTANT_HISTORY", Model,
                Array.Empty<ChatStoredImage>(), Array.Empty<PetWebSource>(), null, DateTimeOffset.UtcNow));
            Input = FindInput(Window) ?? throw new InvalidOperationException("Missing chat input.");
        }
        internal string Snapshot() => JsonSerializer.Serialize(new { conversation = Store.GetConversation(Window.CurrentConversationId!),
            turns = Store.ReadTurns(Window.CurrentConversationId!), memories = Store.ListMemories(Scope) });
        public void Dispose() { Window.Shutdown(); Store.Dispose(); }
    }

    private static TextBox? FindInput(DependencyObject root)
    {
        if (root is TextBox input && input.Name == "OllamaChatInput") return input;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject nested && FindInput(nested) is { } found) return found;
        return null;
    }
}
