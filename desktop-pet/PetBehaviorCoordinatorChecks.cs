using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

// Exercises the real coordinator's timer with a synthetic Ollama transport,
// memory-only database, and isolated pet. No OS input or user data is touched.
internal static class PetBehaviorCoordinatorChecks
{
    public static int Run()
    {
        var checks = new List<string>();
        var clock = Stopwatch.StartNew();
        string evidence = Path.Combine(AppContext.BaseDirectory, "pet-behavior-coordinator-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        var client = new FixtureClient();
        PetWindow? pet = null;
        OllamaChatWindow? session = null;
        AppSettingsWindow? settingsWindow = null;
        PetBehaviorCoordinator? coordinator = null;
        ChatMemoryStore? store = null;
        int result = 1, sessionsCreated = 0, preferenceChanges = 0;
        bool finished = false;
        string phase = "startup";
        void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try { settingsWindow?.Close(); coordinator?.Dispose(); session?.Shutdown(); pet?.Close(); }
            catch (Exception ex) { passed = false; error = "cleanup: " + ex.Message; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "pet-behavior-coordinator-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, error, phase, evidence,
                elapsedMs = clock.ElapsedMilliseconds, sessionsCreated, client.MetadataCalls, client.InferenceCalls, preferenceChanges,
                userSettingsWrites = 0, realAccountRequests = 0, realModelRequests = 0, userWindowsMoved = 0,
                operatingSystemInputInjected = false, capturesContainOnlyTestWindow = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "coordinator_test_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                var settings = new DesktopSettings
                {
                    SkinId = skins.First(skin => skin.Id != "deepseek").Id, Size = 180,
                    MovementEnabled = false, MotionEnabled = false, SoundEnabled = false, Topmost = false,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false,
                    WebSearchEnabled = false, LlmBehaviorEnabled = false
                };
                pet = new PetWindow(skins, settings, false, () => new MotionPreferences(true, false),
                    () => new WindowsSessionSnapshot(42, false, true, false), false,
                    handle => new DesktopPlatformSnapshot(new[] { NativeMethods.GetWindowWorkAreaPixels(handle) }, Array.Empty<DesktopPlatform>()));
                pet.Show();
                await Until(() => pet.IsLoaded && pet.MovementController != null && pet.CompanionSurfacesAvailable);
                coordinator = new PetBehaviorCoordinator(pet, () => session, () =>
                {
                    sessionsCreated++;
                    store = new ChatMemoryStore(":memory:");
                    session = new OllamaChatWindow(settings, () => "合成桌宠", pet.SetChatBusy, _ => { }, () => preferenceChanges++,
                        client, webClient: new ForbiddenWeb(), chatStore: store, behaviorHost: pet);
                    return session;
                });
                settingsWindow = new AppSettingsWindow(pet, setLlmBehavior: enabled =>
                {
                    preferenceChanges++;
                    settings.LlmBehaviorEnabled = enabled;
                    coordinator.RefreshMode();
                }) { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
                settingsWindow.Show();
                var toggle = Descendants<CheckBox>(settingsWindow).Single(item => item.Name == "LlmBehaviorEnabled");
                phase = "default_mode";
                await Task.Delay(650);
                Require(sessionsCreated == 0 && client.MetadataCalls == 0 && client.InferenceCalls == 0 && preferenceChanges == 0,
                    "default_mode_and_settings_refresh_create_no_session_or_requests");
                toggle.IsChecked = true;
                phase = "metadata_connection";
                await Until(() => pet.IsLlmBehaviorActive && session?.BehaviorServiceReady == true);
                Require(sessionsCreated == 1 && client.MetadataCalls == 2 && client.InferenceCalls == 0 && !session!.IsVisible,
                    "enabled_mode_creates_hidden_session_and_checks_only_metadata");
                Require(preferenceChanges == 1, "readiness_and_settings_refresh_do_not_resave_preferences");
                settingsWindow.SavePreview(Path.Combine(evidence, "llm-behavior-settings.png"));
                phase = "hidden_event";
                await pet.ReactToClickAsync();
                pet.Hide();
                await pet.ReactToClickAsync();
                await Task.Delay(800);
                Require(client.InferenceCalls == 0 && client.MetadataCalls == 2,
                    "hide_discards_queued_interaction_and_performs_no_background_request");
                pet.Show();
                await Until(() => pet.CompanionSurfacesAvailable);
                string firstId = session!.CurrentConversationId!;
                const string draft = "还没发出去的草稿";
                store!.SaveDraft(firstId, draft, Array.Empty<ChatStoredImage>());
                phase = "background_reaction";
                await pet.ReactToClickAsync();
                await Until(() => Descendants<TextBlock>(pet).Any(item => item.Text == FixtureClient.Answer));
                Require(client.InferenceCalls == 1 && session?.IsVisible == false && client.LastMessages.Any(message => message.Content.Contains("clicked", StringComparison.Ordinal)),
                    "real_timer_sends_clicked_event_and_displays_background_bubble_without_opening_chat");
                var click = store.ReadTurns(firstId).Single();
                Require(click.User == "" && click.Assistant == FixtureClient.Answer && click.CompanionEventSummary?.Contains("点") == true &&
                    click.Images.Count == 0 && click.Sources.Count == 0 && click.WebWarning == null && store.GetConversation(firstId)?.Draft == draft,
                    "one_shown_click_remark_and_factual_note_saved_without_a_user_turn_or_draft_change");
                phase = "text_only_model";
                client.SupportsTools = false; await session!.CheckConnectionAsync(); coordinator.RefreshMode();
                var textOnlySnapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(session.BehaviorCommandsReady && session.BehaviorReactionReady && !session.BehaviorServiceReady && !pet.IsLlmBehaviorActive && textOnlySnapshot.Enabled,
                    "text_only_explicit_commands_do_not_take_over_default_autonomous_movement");
                var unavailable = await pet.ExecuteAsync(new PetBehaviorCommand("walk", Direction: "left"), textOnlySnapshot.Revision, CancellationToken.None);
                Require(!unavailable.Accepted, "text_only_command_permission_does_not_enable_arbitrary_movement");
                // Each independent scenario starts with a fresh cooldown. The
                // real queue's timing and coalescing have their own checks.
                void ResetCoordinator()
                {
                    coordinator.Dispose();
                    coordinator = new PetBehaviorCoordinator(pet, () => session,
                        () => throw new InvalidOperationException("The existing session must be reused."));
                }
                ResetCoordinator();
                const string releaseReply = "下次松手前记得提醒我呀。";
                client.Reply = releaseReply;
                pet.BeginBehaviorPointerInteraction();
                pet.CompleteBehaviorPointerInteraction(forceDrag: true);
                await Until(() => Descendants<TextBlock>(pet).Any(item => item.Text == releaseReply));
                var afterRelease = store.ReadTurns(firstId);
                Require(!pet.IsLlmBehaviorActive && session.BehaviorReactionReady && !session.BehaviorServiceReady &&
                    client.LastMessages.Any(message => message.Content.Contains("drag_started") && message.Content.Contains("released")) &&
                    client.LastMessages.Any(message => message.Content.Contains("本轮没有动作工具", StringComparison.Ordinal)),
                    "text_only_model_observes_pickup_and_release_without_taking_over_movement");
                string releaseSummary = new PetInteractionEvent("released", default, 0, RecentKinds: new[] { "drag_started", "released" }).DescribeForChat(false);
                Require(!session.IsVisible && client.InferenceCalls == 2 && afterRelease.Count == 2 &&
                    afterRelease[1].Assistant == releaseReply && afterRelease[1].User == "" && afterRelease[1].CompanionEventSummary == releaseSummary,
                    "coalesced_pickup_and_release_save_exactly_one_displayed_remark");

                phase = "silent_reaction";
                ResetCoordinator();
                client.Reply = "SILENT";
                await pet.ReactToClickAsync();
                await Until(() => client.InferenceCalls == 3 && session.CanReactToInteraction);
                Require(store.ReadTurns(firstId).Count == 2 && !Descendants<TextBlock>(pet).Any(item => item.Text == "SILENT"),
                    "silent_model_does_not_display_or_store_an_event");

                phase = "conversation_switch_before_dispatch";
                ResetCoordinator();
                client.Reply = "不该出现在新对话里的旧回复。";
                var beforeSwitch = DateTimeOffset.UtcNow;
                await pet.ReactToClickAsync(beforeSwitch);
                await session.NewConversationAsync();
                string secondId = session.CurrentConversationId!;
                await Task.Delay(1300);
                Require(firstId != secondId && client.InferenceCalls == 3 && store.ReadTurns(firstId).Count == 2 && store.ReadTurns(secondId).Count == 0,
                    "switching_chat_discards_queued_mouse_event_before_model_dispatch");
                string singleClickSummary = new PetInteractionEvent("clicked", default, 0, ClickCount: 1).DescribeForChat(false);
                client.Reply = "新对话里的第一次招呼。";
                await pet.ReactToClickAsync(beforeSwitch.AddMilliseconds(100));
                await Until(() => store.ReadTurns(secondId).Count == 1);
                var firstInNewChat = store.ReadTurns(secondId).Single();
                Require(firstInNewChat.CompanionEventSummary == singleClickSummary && firstInNewChat.Assistant == client.Reply &&
                    client.InferenceCalls == 4 && store.ReadTurns(firstId).Count == 2,
                    "first_click_after_switch_excludes_clicks_from_previous_chat_even_within_one_burst");

                phase = "disable_during_response";
                ResetCoordinator();
                client.Reply = "这句迟到的回复不应出现。";
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.BlockResponse = release.Task;
                await pet.ReactToClickAsync();
                await Until(() => client.InferenceCalls == 5);
                int metadataBeforeDisable = client.MetadataCalls;
                toggle.IsChecked = false;
                release.SetResult(true);
                await Until(() => client.CompletedReplies == 5);
                await pet.ReactToClickAsync();
                await Task.Delay(750);
                Require(!pet.IsLlmBehaviorActive && !(await pet.GetSnapshotAsync(CancellationToken.None)).Enabled && client.InferenceCalls == 5 && client.MetadataCalls == metadataBeforeDisable && preferenceChanges == 2,
                    "turning_mode_off_restores_default_authority_and_stops_background_requests");
                Require(store.ReadTurns(firstId).Count == 2 && store.ReadTurns(secondId).Count == 1 &&
                    !Descendants<TextBlock>(pet).Any(item => item.Text == client.Reply),
                    "late_model_reply_and_offline_click_do_not_write_to_chat");

                phase = "disconnected_interaction";
                client.BlockResponse = null;
                client.Connected = false;
                toggle.IsChecked = true;
                await session.CheckConnectionAsync();
                Require(!session.BehaviorReactionReady, "fixture_model_disconnected");
                ResetCoordinator();
                var beforeReconnect = DateTimeOffset.UtcNow;
                await pet.ReactToClickAsync(beforeReconnect);
                client.Connected = true;
                await session.CheckConnectionAsync();
                await Task.Delay(1300);
                Require(session.BehaviorReactionReady && client.InferenceCalls == 5 && store.ReadTurns(secondId).Count == 1,
                    "reconnecting_does_not_replay_a_click_made_while_disconnected");
                client.Reply = "连接回来后第一次打招呼。";
                await pet.ReactToClickAsync(beforeReconnect.AddMilliseconds(100));
                await Until(() => store.ReadTurns(secondId).Count == 2);
                var afterReconnect = store.ReadTurns(secondId).Last();
                Require(afterReconnect.CompanionEventSummary == singleClickSummary && afterReconnect.Assistant == client.Reply &&
                    client.InferenceCalls == 6, "first_click_after_reconnect_excludes_offline_clicks_within_one_burst");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("coordinator_fixture_condition_timeout");
            await Task.Delay(25);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class FixtureClient : IOllamaClient
    {
        internal const string Answer = "合成事件：我知道你点我啦。";
        internal int MetadataCalls, InferenceCalls, CompletedReplies;
        internal bool SupportsTools = true;
        internal bool Connected = true;
        internal string Reply = Answer;
        internal Task? BlockResponse;
        internal IReadOnlyList<OllamaMessage> LastMessages = Array.Empty<OllamaMessage>();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); MetadataCalls++;
            if (!Connected) throw new OllamaException("Fixture connection unavailable.");
            return Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel });
        }
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); MetadataCalls++; return Task.FromResult(new OllamaModelCapabilities(true, true, false, SupportsTools)); }
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); InferenceCalls++; LastMessages = messages.ToArray();
            if (BlockResponse != null) await BlockResponse; // Deliberately simulate a transport ignoring cancellation.
            else await Task.Delay(15, ct);
            CompletedReplies++;
            yield return new OllamaChatDelta(Reply, true);
        }
        public IAsyncEnumerable<OllamaChatDelta> ChatWithToolsAsync(string model, IReadOnlyList<OllamaMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct)
            => throw new InvalidOperationException("Non-DeepSeek fixture must not advertise a performance or passive movement tool.");
        public Task UnloadAsync(string model, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class ForbiddenWeb : IPetWebClient
    {
        public Task<PetWebResult> SearchAsync(string query, CancellationToken ct) => throw new InvalidOperationException("Unexpected public web call.");
        public Task<PetWebResult> FetchAsync(string url, CancellationToken ct) => throw new InvalidOperationException("Unexpected public web call.");
        public void Dispose() { }
    }
}
