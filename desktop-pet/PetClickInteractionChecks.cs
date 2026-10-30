using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopPet;

// Exercises the same committed-click entry point as the pointer adapter, with
// real bundled frames and an isolated renderer. No OS input, account, user
// settings, speech worker, or model transport is used by this fixture.
internal static class PetClickInteractionChecks
{
    internal static int Run(bool pointerFixture = false)
    {
        var checks = new List<string>();
        string fixture = Path.Combine(AppContext.BaseDirectory, "pet-click-interaction-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        PetWindow? pet = null;
        bool finished = false, closed = false;
        int result = 1;
        string phase = "startup";
        var events = new List<PetInteractionEvent>();
        var startupStates = new List<object>();
        Rect pointerStart = Rect.Empty;
        object Snapshot()
        {
            var display = pet?.DisplayLifecycle;
            return new
            {
                isLoaded = pet?.IsLoaded, isVisible = pet?.IsVisible,
                companionSurfacesAvailable = pet?.CompanionSurfacesAvailable,
                sceneStatus = pet?.SceneStatus, isSceneLoading = pet?.IsSceneLoading,
                scenePackId = pet?.CurrentScenePack?.Id, isScenePlaying = pet?.IsScenePlaying,
                sceneAction = pet?.CurrentSceneAction,
                hasSceneRenderer = pet?.SceneRenderer != null,
                rendererFailed = pet?.SceneRenderer?.IsFailed,
                compatibilityMode = GraphicsRecovery.CompatibilityMode,
                display = display == null ? null : new
                {
                    display.IsSuspended, display.IsRestoring, display.IsDisposed,
                    display.RestoreAttempts, display.CompletedRestores, display.RecoveryScheduled,
                    display.RetryExhausted, display.RendererRetryExhausted,
                    display.IsSessionLocked, display.IsSessionDisconnected,
                    display.AllowsCompanionInteraction
                }
            };
        }
        void RecordStartupState()
        {
            if (phase != "startup") return;
            startupStates.Add(Snapshot());
            if (startupStates.Count > 16) startupStates.RemoveAt(0);
        }
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true;
            timeout.Stop();
            // Closing disposes renderers and changes visibility/lifecycle;
            // capture the failing state before cleanup can erase the evidence.
            object stateBeforeCleanup = Snapshot();
            object? pointerPosition = null;
            if (pointerFixture && pet != null && !closed && !pointerStart.IsEmpty)
            {
                var finalBounds = NativeMethods.GetWindowBoundsPixels(new WindowInteropHelper(pet).Handle);
                pointerPosition = new
                {
                    startLeft = pointerStart.Left, startTop = pointerStart.Top,
                    finalLeft = finalBounds.Left, finalTop = finalBounds.Top,
                    deltaX = finalBounds.Left - pointerStart.Left, deltaY = finalBounds.Top - pointerStart.Top
                };
            }
            try { if (!closed) pet?.Close(); }
            catch (Exception ex) { passed = false; error = "cleanup: " + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,
                pointerFixture ? "pet-click-pointer-fixture-validation.json" : "pet-click-interaction-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, error, phase, pointerFixture,
                stateBeforeCleanup, startupStates, pointerPosition,
                userSettingsWrites = 0, realAccountRequests = 0, realModelRequests = 0,
                userWindowsMoved = 0, operatingSystemInputInjected = pointerFixture,
                interactions = events.Select(item => new { item.Kind, item.ClickCount, item.ClickReaction })
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "click_interaction_test_timeout");
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message);
        };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                phase = "click_policy_boundaries";
                var sequence = new PetClickSequence();
                var clockBase = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                Require(sequence.Register(clockBase) == 1 && sequence.Register(clockBase.AddMilliseconds(2999)) == 2,
                    "click_burst_retains_sub_three_second_gap");
                Require(sequence.Register(clockBase.AddMilliseconds(5999)) == 1,
                    "exactly_three_quiet_seconds_reset_click_burst");
                Require(sequence.Register(clockBase.AddMilliseconds(5000)) == 1,
                    "backward_clock_resets_instead_of_extending_old_burst");
                for (int i = 0; i < 150; i++) sequence.Register(clockBase.AddMilliseconds(5001 + i));
                Require(sequence.Register(clockBase.AddMilliseconds(5200)) == 100,
                    "click_count_has_bounded_one_hundred_limit");
                sequence.Reset();
                Require(sequence.Register(clockBase.AddMilliseconds(5201)) == 1,
                    "explicit_drag_or_lifecycle_reset_forgets_click_burst");

                phase = "startup";
                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                var settings = new DesktopSettings
                {
                    SkinId = "deepseek", DeepSeekAnimationPackId = BundledSceneAnimations.DeepSeekId,
                    Size = 200, MovementEnabled = false, MotionEnabled = false,
                    SpeechEnabled = false, SoundEnabled = false, Topmost = false,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false,
                    LlmBehaviorEnabled = true, WebSearchEnabled = false
                };
                pet = new PetWindow(skins, settings, false, () => new MotionPreferences(true, false),
                    () => new WindowsSessionSnapshot(42, false, true, false), false,
                    handle => new DesktopPlatformSnapshot(new[] { NativeMethods.GetWindowWorkAreaPixels(handle) }, Array.Empty<DesktopPlatform>()))
                    { RendererProfileDirectory = Path.Combine(fixture, "webview") };
                pet.InteractionOccurred += events.Add;
                pet.SettingsChanged += RecordStartupState;
                pet.Show();
                RecordStartupState();
                await Until(() => pet.IsLoaded && pet.MovementController != null && pet.CompanionSurfacesAvailable &&
                    pet.CurrentScenePack?.Id == BundledSceneAnimations.DeepSeekId && !pet.IsSceneLoading && pet.IsScenePlaying, 35);
                var movement = pet.MovementController!;
                Require(!movement.IsEnabled && !pet.Settings.MotionEnabled, "click_reactions_do_not_require_walking_or_breathing");
                if (pointerFixture)
                {
                    phase = "native_pointer_fixture";
                    pet.Title = "AI Pet 点击拖拽验证 · v" + AppVersion.Number;
                    // Only this opt-in test window is discoverable/activatable
                    // by the desktop input harness. Production stays a no-activate tool window.
                    pet.ShowInTaskbar = true;
                    pet.SetTopmost(true);
                    NativeMethods.SetToolWindow(new WindowInteropHelper(pet).Handle, false);
                    NativeMethods.SetNoActivate(new WindowInteropHelper(pet).Handle, false);
                    pet.Activate();
                    pointerStart = NativeMethods.GetWindowBoundsPixels(new WindowInteropHelper(pet).Handle);
                    pet.Say("轻点三次，等表情出现，再把我拖动一小段。验证后会自动关闭。");
                    while (!finished)
                    {
                        if (events.Any(item => item.Kind == "clicked" && item.ClickCount >= 3) &&
                            events.Any(item => item.Kind == "drag_started") && events.Any(item => item.Kind == "released"))
                        {
                            Require(true, "native_pointer_produces_three_committed_clicks_and_a_drag_release");
                            Require(!movement.IsDragging, "native_drag_releases_controller_ownership");
                            Finish(true);
                            return;
                        }
                        await Task.Delay(50);
                    }
                    return;
                }
                var at = DateTimeOffset.UtcNow;

                async Task<JsonElement> ReadScene()
                {
                    var renderer = pet.SceneRenderer ?? throw new InvalidOperationException("test_scene_renderer_missing");
                    string json = JsonSerializer.Deserialize<string>(await renderer.GetDiagnosticsAsync())
                        ?? throw new InvalidOperationException("test_scene_diagnostics_missing");
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.Clone();
                }
                async Task WaitAction(string action)
                {
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed < TimeSpan.FromSeconds(8))
                    {
                        if (pet.CurrentSceneAction == action && pet.IsScenePlaying)
                        {
                            var state = await ReadScene();
                            if (state.GetProperty("hasFrame").GetBoolean() && state.GetProperty("playing").GetBoolean() &&
                                state.GetProperty("action").GetString() == action && state.GetProperty("displayedAction").GetString() == action)
                                return;
                        }
                        await Task.Delay(10);
                    }
                    throw new TimeoutException("expected_rendered_" + action);
                }
                async Task Click(DateTimeOffset occurredAt, string action, int count)
                {
                    int before = events.Count;
                    await pet.ReactToClickAsync(occurredAt);
                    await WaitAction(action);
                    Require(events.Count == before + 1 && events[^1].Kind == "clicked" &&
                        events[^1].ClickCount == count && events[^1].ClickReaction == action,
                        "click_" + count + "_reports_actual_" + action + "_once");
                    Require(!movement.IsDragging && movement.State != "drag", "click_" + count + "_does_not_enter_drag");
                }

                phase = "click_sequence";
                await Click(at, "happy", 1);
                await Click(at.AddMilliseconds(150), "startled", 2);
                await Click(at.AddMilliseconds(300), "angry", 3);
                await Click(at.AddMilliseconds(450), "angry", 4);
                await Click(at.AddSeconds(4), "happy", 1);
                Require(events.All(item => item.Kind == "clicked"), "ordinary_click_sequence_emits_no_drag_fall_or_landing");

                phase = "rapid_async_clicks";
                int beforeRapid = events.Count;
                var first = pet.ReactToClickAsync(at.AddSeconds(8));
                var second = pet.ReactToClickAsync(at.AddSeconds(8.1));
                var third = pet.ReactToClickAsync(at.AddSeconds(8.2));
                await Task.WhenAll(first, second, third);
                await WaitAction("angry");
                Require(events.Count > beforeRapid && events[^1].Kind == "clicked" &&
                    events[^1].ClickCount == 3 && events[^1].ClickReaction == "angry",
                    "rapid_clicks_keep_latest_count_and_rendered_expression");
                Require(events.Skip(beforeRapid).Where(item => item.Kind == "clicked").Select(item => item.ClickCount)
                    .SequenceEqual(events.Skip(beforeRapid).Where(item => item.Kind == "clicked").Select(item => item.ClickCount).OrderBy(count => count)),
                    "late_frame_completion_cannot_replay_older_click_event");

                phase = "manual_performance_priority";
                await pet.SingAsync();
                await WaitAction("sing");
                int beforeSongClick = events.Count;
                await pet.ReactToClickAsync(at.AddSeconds(12));
                await WaitAction("sing");
                Require(pet.IsSinging && events.Count == beforeSongClick + 1 && events[^1].Kind == "clicked" &&
                    events[^1].ClickReaction == null, "click_notifies_llm_without_replacing_manual_song");
                pet.StopSinging();
                await WaitAction("idle");

                phase = "physical_priority";
                movement.BeginDrag();
                await WaitAction("drag");
                int beforeDragClick = events.Count;
                await pet.ReactToClickAsync(at.AddSeconds(16));
                await WaitAction("drag");
                Require(movement.IsDragging && events.Count == beforeDragClick + 1 && events[^1].Kind == "clicked" &&
                    events[^1].ClickReaction == null, "click_notifies_llm_without_overriding_active_drag");
                movement.EndDrag();
                await WaitAction("idle");

                phase = "offline_default";
                settings.LlmBehaviorEnabled = false;
                // Exercise real click -> Say -> voice routing with fake audio
                // endpoints; persist:false still protects all user settings.
                var voiceField = typeof(PetWindow).GetField("_voice", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ((PetSpeechService?)voiceField.GetValue(pet))?.Dispose();
                var clickSynth = new ClickVoiceSynth();
                var clickPlayer = new ClickVoicePlayer();
                voiceField.SetValue(pet, new PetSpeechService(settings, app.Dispatcher, clickSynth, clickPlayer));
                settings.SpeechEnabled = true;
                int beforeOffline = events.Count;
                await pet.ReactToClickAsync(at.AddSeconds(20));
                await WaitAction("happy");
                Require(events.Count == beforeOffline, "disabled_llm_mode_keeps_local_expression_without_model_event");
                await Until(() => clickPlayer.Played == 1);
                Require(clickSynth.Texts.Single().Contains("找本鲸有什么事"),
                    "offline_click_uses_voice_without_llm_walking_or_breathing");
                await pet.ReactToClickAsync(at.AddSeconds(20.1));
                await Until(() => clickPlayer.Played == 2);
                Require(clickSynth.Texts[^1].Contains("怎么又戳本鲸"), "second_click_speaks_its_new_reaction");
                await pet.ReactToClickAsync(at.AddSeconds(20.2));
                await Until(() => clickPlayer.Played == 3);
                await pet.ReactToClickAsync(at.AddSeconds(20.3));
                await Until(() => clickPlayer.Played == 4);
                Require(clickSynth.Texts.Any(text => text.Contains("赔一碗白米饭")) && clickPlayer.Played == 4,
                    "repeated_angry_clicks_are_new_utterances_even_with_identical_text");
                settings.SpeechEnabled = false;
                await pet.ReactToClickAsync(at.AddSeconds(20.4));
                Require(clickPlayer.Played == 4, "offline_click_still_respects_speech_disabled");

                phase = "routed_pointer_input";
                settings.LlmBehaviorEnabled = true;
                // Routed test events enter the real handler without injecting
                // OS input. ClickCount is WPF-owned, so this checks repeated
                // press/release routing rather than faking a native double-click.
                var character = ((Grid)pet.Content).Children.OfType<Grid>().Single();
                int chatOpenRequests = 0;
                pet.OpenChatRequested += () => chatOpenRequests++;
                var movementStates = new List<string>();
                movement.StateChanged += movementStates.Add;
                void RaisePointer(RoutedEvent routedEvent)
                    => character.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = routedEvent });
                int beforePointer = events.Count;
                RaisePointer(UIElement.MouseLeftButtonDownEvent);
                Require(character.IsMouseCaptured && !movement.IsDragging,
                    "pointer_press_captures_without_premature_drag");
                RaisePointer(UIElement.MouseLeftButtonUpEvent);
                RaisePointer(UIElement.MouseLeftButtonDownEvent);
                RaisePointer(UIElement.MouseLeftButtonUpEvent);
                await Until(() => events.Count > beforePointer && events[^1].ClickCount == 2);
                await WaitAction("startled");
                Require(chatOpenRequests == 0 && !character.IsMouseCaptured && !movementStates.Contains("drag"),
                    "repeated_pointer_clicks_react_without_chat_or_drag");
                int beforeLostCapture = events.Count;
                RaisePointer(UIElement.MouseLeftButtonDownEvent);
                character.ReleaseMouseCapture();
                RaisePointer(UIElement.MouseLeftButtonUpEvent);
                await Task.Delay(30);
                Require(events.Count == beforeLostCapture && !movement.IsDragging,
                    "lost_pointer_capture_does_not_commit_a_click");

                phase = "gpt_offline_persona_and_expressions";
                settings.LlmBehaviorEnabled = false;
                pet.SelectSkin("gpt");
                await WaitAction("idle");
                Require(pet.CurrentScenePack?.SkinId == "gpt", "gpt_clicks_use_its_own_bundled_character_artwork");
                var speechBlock = (TextBlock)typeof(PetWindow).GetField("_speech", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
                int beforeDragon = events.Count;
                foreach (var item in new[] { (Offset: 40d, Action: "happy", Marker: "小龙娘"),
                    (Offset: 40.1d, Action: "startled", Marker: "龙角"), (Offset: 40.2d, Action: "angry", Marker: "本龙") })
                {
                    await pet.ReactToClickAsync(at.AddSeconds(item.Offset));
                    await WaitAction(item.Action);
                    Require(speechBlock.Text.Contains(item.Marker, StringComparison.Ordinal) &&
                        !speechBlock.Text.Contains("本鲸", StringComparison.Ordinal) && !speechBlock.Text.Contains("米饭", StringComparison.Ordinal),
                        "gpt_offline_" + item.Action + "_uses_dragon_dialogue_and_expression");
                }
                Require(events.Count == beforeDragon && clickPlayer.Played == 4,
                    "gpt_offline_clicks_do_not_call_a_model_or_borrow_deepseek_voice");

                phase = "closed_pet";
                settings.LlmBehaviorEnabled = true;
                int beforeClose = events.Count;
                pet.Close(); closed = true;
                await pet.ReactToClickAsync(at.AddSeconds(24));
                Require(events.Count == beforeClose, "closed_pet_discards_click_without_event_or_renderer_work");
                Finish(true);
            }
            catch (Exception ex)
            {
                Finish(false, ex.GetType().Name + ": " + ex.Message);
            }
        };
        app.Run();
        return result;
    }

    private static async Task Until(Func<bool> condition, int seconds = 8)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds >= seconds) throw new TimeoutException("click_fixture_not_ready");
            await Task.Delay(20);
        }
    }

    private sealed class ClickVoiceSynth : IPetSpeechSynthesizer
    {
        internal readonly List<string> Texts = new();
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken token)
        {
            Texts.Add(text);
            using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
            writer.Write("RIFF"u8); writer.Write(40); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(4);
            writer.Write(new byte[] { 1, 0, 2, 0 });
            return Task.FromResult(buffer.ToArray());
        }
        public Task CancelAsync(string id) => Task.CompletedTask;
        public Task ReleaseAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class ClickVoicePlayer : IPetSpeechPlayer
    {
        internal int Played;
        public Task PlayAsync(byte[] wave, double volume, CancellationToken token) { token.ThrowIfCancellationRequested(); Played++; return Task.CompletedTask; }
        public void Stop() { }
        public void SetVolume(double volume) { }
        public void Dispose() { }
    }
}
