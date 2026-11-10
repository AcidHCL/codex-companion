using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopPet.WorkCompanion;

// Invoke on the existing test Application's dispatcher. Windows remain hidden;
// all models, web access and storage use in-memory fixtures.
internal static class OllamaWorkReactionChatChecks
{
    internal static async Task<object> RunAsync()
    {
        if (Application.Current == null || !Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException("Run work companion chat checks on the WPF test dispatcher.");
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Work companion chat: " + label);
            checks.Add(label);
        }
        using (var fixture = new Fixture((_, _) => Task.FromResult(OllamaWorkReactionChecks.Reply(
            "{\"text\":\"需要帮忙就叫我。\",\"expression\":\"happy\"}"))))
        {
            Check(fixture.Handler.Paths.Count == 0, "constructing_hidden_chat_does_not_query_or_operate_models");
            fixture.Input.Text = "SECRET_UNSENT_DRAFT";
            fixture.Window.SaveDraftForTesting();
            var before = fixture.Snapshot();
            Check(!fixture.Window.IsVisible && !fixture.Settings.LlmBehaviorEnabled && fixture.Window.CanReactToWork,
                "hidden_work_host_does_not_require_behavior_mode_or_UI_connection_refresh");
            var reply = await fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None);
            Check(reply == new WorkReactionReply("需要帮忙就叫我。", "happy"), "structured_work_reply_returned_to_host_only");
            Check(fixture.Snapshot() == before && fixture.Input.Text == "SECRET_UNSENT_DRAFT",
                "work_observation_and_reply_never_change_history_shared_memory_or_draft");
            Check(fixture.SayCalls == 0 && fixture.BusyCalls == 0 && fixture.SaveSettingsCalls == 0 &&
                fixture.Settings.OllamaModel == Fixture.Model && fixture.Settings.OllamaKeepAliveMinutes == -1,
                "work_generation_does_not_present_change_settings_or_mark_chat_busy");
            Check(fixture.ChatBodies.Count == 1 && !fixture.ChatBodies[0].Contains("SECRET_", StringComparison.Ordinal),
                "background_payload_contains_no_chat_memory_or_unsent_draft");
            Check(!fixture.Handler.Paths.Contains("/api/generate", StringComparer.Ordinal) &&
                fixture.Handler.Paths.Count(path => path == "/api/chat") == 1,
                "background_never_loads_unloads_or_sends_ordinary_chat");
            int metadataBefore = fixture.Handler.Paths.Count;
            Check(await fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None) == null &&
                fixture.Handler.Paths.Count == metadataBefore, "post_request_grace_prevents_new_background_requests");
        }

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        using (var fixture = new Fixture(async (_, ct) =>
        {
            requestToken = ct; started.TrySetResult(true);
            // Intentionally ignore cancellation until released. The UI must
            // still drain this transport before letting the shared lane go.
            await released.Task;
            return OllamaWorkReactionChecks.Reply("{\"text\":\"LATE_REPLY\",\"expression\":\"surprised\"}");
        }))
        {
            var pet = HiddenPet(fixture.Settings);
            using var coordinator = new PetBehaviorCoordinator(pet, () => fixture.Window,
                () => throw new InvalidOperationException("The existing host must be reused."));
            pet.SettingsChanged += coordinator.RefreshMode;
            var work = fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Window.IsWorkContextAvailable && !fixture.Window.CanReactToWork,
                    "running_work_keeps_context_available_without_allowing_parallel_reaction");
                coordinator.RefreshMode();
                pet.SetSoundVolume(.4); // The same status-refresh route used by ordinary animation changes.
                Check(!requestToken.IsCancellationRequested && !work.IsCompleted,
                    "disabled_behavior_mode_refresh_does_not_cancel_independent_work");
                int readsBeforeHealth = fixture.Handler.Paths.Count;
                await fixture.Window.CheckConnectionWhenIdleAsync();
                await fixture.Window.CheckConnectionWhenIdleAsync();
                Check(!requestToken.IsCancellationRequested && !work.IsCompleted && fixture.Window.IsWorkContextAvailable &&
                    fixture.Handler.Paths.Count == readsBeforeHealth,
                    "periodic_health_checks_skip_active_shared_lane_without_metadata_or_cancellation");
                fixture.Input.Text = "User takes priority now";
                Check(requestToken.IsCancellationRequested, "typing_uses_existing_behavior_cancellation_for_work");
                Task drained = fixture.Window.StopBehaviorReactionAsync();
                Check(!drained.IsCompleted && !work.IsCompleted, "priority_path_waits_for_canceled_transport_to_drain");
                released.TrySetResult(true);
                Check(await work.WaitAsync(TimeSpan.FromSeconds(5)) == null, "late_background_reply_is_discarded_after_user_input");
                await drained.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Window.CanReactToWork && fixture.Handler.Paths.Count(path => path == "/api/chat") == 1,
                    "cancellation_releases_shared_lane_without_extra_generation");
                Check(fixture.Store.ReadTurns(fixture.Window.CurrentConversationId!).Count == 1 &&
                    fixture.Store.ListMemories(fixture.Scope).Count == 1 && fixture.SayCalls == 0,
                    "canceled_work_leaves_existing_chat_memory_and_presentation_untouched");
            }
            finally
            {
                released.TrySetResult(true); fixture.Window.CancelWorkReaction(); await work;
                pet.SettingsChanged -= coordinator.RefreshMode; coordinator.Dispose(); pet.Close();
            }
        }

        using (var fixture = new Fixture((_, _) => throw new InvalidOperationException("An idle health check must not infer.")))
        {
            await fixture.Window.CheckConnectionWhenIdleAsync();
            Check(fixture.Handler.Paths.Contains("/api/tags", StringComparer.Ordinal) &&
                !fixture.Handler.Paths.Contains("/api/chat", StringComparer.Ordinal) &&
                !fixture.Handler.Paths.Contains("/api/generate", StringComparer.Ordinal) && fixture.Window.CanReactToWork,
                "idle_periodic_health_check_still_refreshes_metadata_without_inference");
        }

        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken transportToken = default;
            using var fixture = new Fixture(async (_, ct) =>
            {
                transportToken = ct; entered.TrySetResult(true); await release.Task;
                return OllamaWorkReactionChecks.Reply("{\"text\":\"Stale after refresh\",\"expression\":\"none\"}");
            });
            var work = fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None);
            Task? refresh = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                int readsBeforeRefresh = fixture.Handler.Paths.Count;
                refresh = fixture.Window.CheckConnectionAsync();
                Check(transportToken.IsCancellationRequested && !refresh.IsCompleted && !work.IsCompleted &&
                    fixture.Handler.Paths.Count == readsBeforeRefresh,
                    "explicit_connection_refresh_still_cancels_and_drains_shared_reaction_before_metadata");
                await fixture.Window.CheckConnectionWhenIdleAsync();
                Check(!refresh.IsCompleted && fixture.Handler.Paths.Count == readsBeforeRefresh,
                    "periodic_check_also_skips_a_canceled_reaction_until_its_transport_has_drained");
                release.TrySetResult(true);
                Check(await work.WaitAsync(TimeSpan.FromSeconds(5)) == null,
                    "explicit_refresh_discards_the_canceled_reactions_late_reply");
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Handler.Paths.Count(path => path == "/api/tags") >= 2 && fixture.Window.CanReactToWork &&
                    !fixture.Handler.Paths.Contains("/api/generate", StringComparer.Ordinal) &&
                    fixture.Settings.OllamaModel == Fixture.Model && fixture.Settings.OllamaKeepAliveMinutes == -1,
                    "explicit_refresh_completes_after_drain_without_changing_model_or_retention");
            }
            finally
            {
                release.TrySetResult(true); fixture.Window.CancelWorkReaction(); await work;
                if (refresh != null) await refresh;
            }
        }

        foreach (bool disableMode in new[] { false, true })
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken transportToken = default;
            using var fixture = new Fixture(async (_, ct) =>
            {
                transportToken = ct; entered.TrySetResult(true); await release.Task;
                return OllamaWorkReactionChecks.Reply("{\"text\":\"Late\",\"expression\":\"none\"}");
            });
            if (disableMode) { fixture.Settings.LlmBehaviorEnabled = true; fixture.Window.RefreshBehaviorConfiguration(); }
            var pet = HiddenPet(fixture.Settings);
            using var coordinator = new PetBehaviorCoordinator(pet, () => fixture.Window,
                () => throw new InvalidOperationException("The existing host must be reused."));
            var work = fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (disableMode)
                {
                    fixture.Settings.LlmBehaviorEnabled = false;
                    coordinator.RefreshMode();
                }
                else
                {
                    // Raise the actual pet right-click handler without moving
                    // the system pointer, showing a window, or injecting OS input.
                    var character = ((Grid)pet.Content).Children.OfType<Grid>().First();
                    character.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                }
                Check(transportToken.IsCancellationRequested,
                    disableMode ? "real_behavior_disable_still_cancels_work" : "actual_pet_click_handler_still_cancels_work");
                release.TrySetResult(true);
                Check(await work.WaitAsync(TimeSpan.FromSeconds(5)) == null,
                    disableMode ? "disabled_mode_drops_late_work_result" : "pet_click_drops_late_work_result");
            }
            finally { release.TrySetResult(true); fixture.Window.CancelWorkReaction(); await work; coordinator.Dispose(); pet.Close(); }
        }

        using (var fixture = new Fixture((_, _) => throw new InvalidOperationException("Busy path must not infer.")))
        {
            using (var active = OllamaRequestActivity.Begin(fixture.Window.WorkCompanionEndpoint, maintenance: true))
                Check(await fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None) == null &&
                    fixture.Handler.Paths.Count == 0, "maintenance_blocks_work_before_even_metadata_reads");
            Check(await fixture.Window.ReactToWorkAsync(OllamaWorkReactionChecks.Candidate(), CancellationToken.None) == null &&
                fixture.Handler.Paths.Count == 0, "unconfirmed_request_grace_blocks_background_inference");
        }
        return new { passed = true, checks, realModelRequests = 0, productionDatabaseAccess = false, shownWindows = 0 };
    }

    private static PetWindow HiddenPet(DesktopSettings settings) => new(SkinCatalog.Load(AppContext.BaseDirectory), settings,
        persist: false, motionPreferences: () => new MotionPreferences(false, false),
        querySession: () => new WindowsSessionSnapshot(42, false, true, false), subscribeToSystemEvents: false);

    private sealed class Fixture : IDisposable
    {
        internal const string Model = "work-fixture:latest";
        internal DesktopSettings Settings { get; }
        internal ChatMemoryStore Store { get; }
        internal OllamaChatWindow Window { get; }
        internal TextBox Input { get; }
        internal string Scope { get; }
        internal OllamaWorkReactionChecks.Handler Handler { get; }
        internal List<string> ChatBodies { get; } = new();
        internal int SayCalls, BusyCalls, SaveSettingsCalls;

        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> chat)
        {
            var endpoint = new Uri("http://work-fixture-" + Guid.NewGuid().ToString("N") + ".invalid:11434/");
            Scope = endpoint.GetLeftPart(UriPartial.Authority);
            Settings = new DesktopSettings
            {
                SkinId = "deepseek", OllamaModel = Model, OllamaAddress = endpoint.AbsoluteUri,
                OllamaKeepAliveMinutes = -1, LlmBehaviorEnabled = false, WebSearchEnabled = false,
                SharedMemoryEnabled = true, CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false
            };
            Handler = new OllamaWorkReactionChecks.Handler(async (request, ct) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path is "/api/tags" or "/api/ps") return OllamaWorkReactionChecks.Json(OllamaWorkReactionChecks.Models(Model));
                if (path == "/api/show") return OllamaWorkReactionChecks.Json("{\"capabilities\":[\"completion\"]}");
                if (path != "/api/chat") throw new InvalidOperationException("Unexpected model operation.");
                ChatBodies.Add(await request.Content!.ReadAsStringAsync(ct));
                return await chat(request, ct);
            });
            Store = new ChatMemoryStore(":memory:");
            Store.UpsertMemory(new(Guid.NewGuid().ToString("N"), Scope, "SECRET_MEMORY", "SECRET_MEMORY_CONTENT", true, DateTimeOffset.UtcNow));
            Window = new OllamaChatWindow(Settings, () => "DeepSeek", _ => BusyCalls++, _ => SayCalls++, () => SaveSettingsCalls++,
                client: new OllamaClient(endpoint, Handler, () => Settings.OllamaKeepAliveMinutes),
                webClient: new ForbiddenWeb(), chatStore: Store)
            { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
            Store.AppendTurn(Window.CurrentConversationId!, new(0, "SECRET_USER_HISTORY", "SECRET_ASSISTANT_HISTORY", Model,
                Array.Empty<ChatStoredImage>(), Array.Empty<PetWebSource>(), null, DateTimeOffset.UtcNow));
            Input = FindInput(Window);
        }

        internal string Snapshot() => JsonSerializer.Serialize(new
        {
            conversation = Store.GetConversation(Window.CurrentConversationId!),
            turns = Store.ReadTurns(Window.CurrentConversationId!), memories = Store.ListMemories(Scope)
        });
        public void Dispose() { Window.Shutdown(); Store.Dispose(); }
    }

    private static TextBox FindInput(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBox input && input.Name == "OllamaChatInput") return input;
            if (child is DependencyObject nested && TryFindInput(nested) is { } found) return found;
        }
        throw new InvalidOperationException("Chat input fixture not found.");
    }
    private static TextBox? TryFindInput(DependencyObject root)
    {
        if (root is TextBox input && input.Name == "OllamaChatInput") return input;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject nested && TryFindInput(nested) is { } found) return found;
        return null;
    }
    private sealed class ForbiddenWeb : IPetWebClient
    {
        public Task<PetWebResult> SearchAsync(string query, CancellationToken ct) => throw new InvalidOperationException("Unexpected web query.");
        public Task<PetWebResult> FetchAsync(string url, CancellationToken ct) => throw new InvalidOperationException("Unexpected web fetch.");
        public void Dispose() { }
    }
}
