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
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
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
                phase = "background_reaction";
                await pet.ReactToClickAsync();
                await Until(() => Descendants<TextBlock>(pet).Any(item => item.Text == FixtureClient.Answer));
                Require(client.InferenceCalls == 1 && session?.IsVisible == false && client.LastMessages.Any(message => message.Content.Contains("clicked", StringComparison.Ordinal)),
                    "real_timer_sends_clicked_event_and_displays_background_bubble_without_opening_chat");
                var conversations = store!.ListConversations(OllamaEndpoint.DefaultAddress, settings.SkinId);
                Require(conversations.Count == 1 && store.ReadTurns(conversations[0].Id).Count == 0,
                    "passive_reaction_does_not_add_fake_user_turn_to_history");
                phase = "text_only_model";
                client.SupportsTools = false; await session!.CheckConnectionAsync(); coordinator.RefreshMode();
                var textOnlySnapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(session.BehaviorCommandsReady && session.BehaviorReactionReady && !session.BehaviorServiceReady && !pet.IsLlmBehaviorActive && textOnlySnapshot.Enabled,
                    "text_only_explicit_commands_do_not_take_over_default_autonomous_movement");
                var unavailable = await pet.ExecuteAsync(new PetBehaviorCommand("walk", Direction: "left"), textOnlySnapshot.Revision, CancellationToken.None);
                Require(!unavailable.Accepted, "text_only_command_permission_does_not_enable_arbitrary_movement");
                // Use a fresh coordinator for this separate capability case;
                // cooldown behavior is covered by PetInteractionQueueChecks.
                coordinator.Dispose();
                coordinator = new PetBehaviorCoordinator(pet, () => session,
                    () => throw new InvalidOperationException("The existing text-only session must be reused."));
                await pet.ReactToClickAsync();
                await Until(() => client.InferenceCalls == 2);
                await Until(() => session.CanReactToInteraction);
                Require(!pet.IsLlmBehaviorActive && session.BehaviorReactionReady && !session.BehaviorServiceReady &&
                    client.LastMessages.Any(message => message.Content.Contains("clicked", StringComparison.Ordinal)) &&
                    client.LastMessages.Any(message => message.Content.Contains("本轮没有动作工具", StringComparison.Ordinal)),
                    "text_only_model_consumes_click_queue_without_tools_or_movement_takeover");
                Require(!session.IsVisible && store.ReadTurns(conversations[0].Id).Count == 0,
                    "text_only_passive_response_keeps_composer_hidden_and_history_unchanged");
                int metadataBeforeDisable = client.MetadataCalls;
                phase = "disable_mode";
                toggle.IsChecked = false;
                await pet.ReactToClickAsync();
                await Task.Delay(750);
                Require(!pet.IsLlmBehaviorActive && !(await pet.GetSnapshotAsync(CancellationToken.None)).Enabled && client.InferenceCalls == 2 && client.MetadataCalls == metadataBeforeDisable && preferenceChanges == 2,
                    "turning_mode_off_restores_default_authority_and_stops_background_requests");
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
        internal int MetadataCalls, InferenceCalls;
        internal bool SupportsTools = true;
        internal IReadOnlyList<OllamaMessage> LastMessages = Array.Empty<OllamaMessage>();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); MetadataCalls++; return Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel }); }
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); MetadataCalls++; return Task.FromResult(new OllamaModelCapabilities(true, true, false, SupportsTools)); }
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); InferenceCalls++; LastMessages = messages.ToArray();
            await Task.Delay(15, ct);
            yield return new OllamaChatDelta(Answer, true);
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
