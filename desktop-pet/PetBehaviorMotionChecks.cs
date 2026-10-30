using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

// Uses only a non-persistent test pet and synthetic platforms. Native window
// moves target its own HWND; no mouse/keyboard input or real settings are used.
internal static class PetBehaviorMotionChecks
{
    public static int Run()
    {
        var checks = new List<string>();
        string fixture = Path.Combine(AppContext.BaseDirectory, "pet-behavior-motion-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(180) };
        PetWindow? pet = null;
        bool finished = false;
        int result = 1;
        string phase = "startup";
        void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try { pet?.Close(); } catch (Exception ex) { passed = false; error = ex.Message; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "pet-behavior-motion-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, error, phase,
                userSettingsWrites = 0, realAccountRequests = 0, realModelRequests = 0,
                userWindowsMoved = 0, operatingSystemInputInjected = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "test_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                var settings = new DesktopSettings
                {
                    SkinId = skins.First(skin => skin.Id != "deepseek").Id, Size = 200,
                    MovementEnabled = false, MotionEnabled = false, SoundEnabled = false, Topmost = false,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false
                };
                pet = new PetWindow(skins, settings, false, () => new MotionPreferences(true, false),
                    () => new WindowsSessionSnapshot(42, false, true, false), false,
                    handle => new DesktopPlatformSnapshot(new[] { NativeMethods.GetWindowWorkAreaPixels(handle) }, Array.Empty<DesktopPlatform>()))
                    { RendererProfileDirectory = Path.Combine(fixture, "webview") };
                pet.Show();
                await Until(() => pet.IsLoaded && pet.MovementController != null && pet.CompanionSurfacesAvailable);
                var movement = pet.MovementController!;
                var hwnd = new WindowInteropHelper(pet).Handle;
                var events = new List<string>();
                pet.InteractionOccurred += item => events.Add(item.Kind);
                var snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(!snapshot.Enabled, "default_mode_has_no_model_authority");
                Require(!(await pet.ExecuteAsync(new("walk", Direction: "right"), snapshot.Revision, CancellationToken.None)).Accepted, "disabled_mode_rejects_model_movement");
                settings.LlmBehaviorEnabled = true;
                pet.SetLlmBehaviorActive(true);
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                var disabled = await pet.ExecuteAsync(new("walk", Direction: "right"), snapshot.Revision, CancellationToken.None);
                Require(!disabled.Accepted && disabled.Message.Contains("开启桌面行走", StringComparison.Ordinal), "disabled_movement_reports_user_setting_without_enabling_it");
                Require(snapshot.Performances.Count == 0 && !(await pet.ExecuteAsync(new("play_performance", "sing"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "skin_without_performance_does_not_advertise_or_execute_it");
                Require(!(await pet.ExecuteAsync(new("play_performance", "fall"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "passive_animations_are_not_executable_capabilities");

                phase = "directed_movement";
                pet.SetMovement(true);
                pet.ResetPosition();
                await Until(() => movement.Grounded);
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(!snapshot.CanJump && !snapshot.CanClimb &&
                    !(await pet.ExecuteAsync(new("jump", Direction: "right"), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("climb", Direction: "left"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "other_skin_without_motion_assets_cannot_advertise_or_execute_jump_and_climb");
                pet.SetCompanionOpen(true); pet.SetChatBusy(true);
                snapshot = await Task.Run(() => pet.GetSnapshotAsync(CancellationToken.None));
                var before = NativeMethods.GetWindowBoundsPixels(hwnd);
                Require((await Task.Run(() => pet.ExecuteAsync(new("walk", Direction: "left", Seconds: 1), snapshot.Revision, CancellationToken.None))).Accepted,
                    "directed_walk_marshals_to_ui_and_works_while_chat_blocks_random_walking");
                await Task.Delay(300);
                Require(NativeMethods.GetWindowBoundsPixels(hwnd).Left < before.Left - 3 && movement.IsDirectedWalking,
                    "directed_lease_moves_own_hwnd_using_physics");
                Require((await pet.GetSnapshotAsync(CancellationToken.None)).Revision == snapshot.Revision,
                    "model_action_does_not_invalidate_its_own_batch_revision");
                await Task.Delay(1050);
                before = NativeMethods.GetWindowBoundsPixels(hwnd);
                await Task.Delay(180);
                Require(!movement.IsDirectedWalking && Math.Abs(NativeMethods.GetWindowBoundsPixels(hwnd).Left - before.Left) <= 1,
                    "directed_walk_expires_without_resuming_random_movement");
                Require(!(await pet.ExecuteAsync(new("walk", Direction: "left", Seconds: 99), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("walk", Direction: "left", Seconds: double.NaN), snapshot.Revision, CancellationToken.None)).Accepted,
                    "unbounded_and_nonfinite_movement_rejected");
                Require(!(await pet.ExecuteAsync(new("walk", Direction: "left", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("stop_walk", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("jump", Direction: "right", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("climb", Direction: "left", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted,
                    "passive_reactions_cannot_start_or_stop_movement");
                Require((await pet.ExecuteAsync(new("walk", Direction: "left"), snapshot.Revision, CancellationToken.None)).Accepted, "second_directed_walk_started");
                pet.InvalidateLlmBehavior();
                Require(!movement.IsDirectedWalking && !(await pet.ExecuteAsync(new("walk", Direction: "right"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "external_invalidation_revokes_lease_and_stale_plan");
                pet.SetChatBusy(false); pet.SetCompanionOpen(false);

                phase = "physical_events";
                events.Clear();
                pet.BeginBehaviorPointerInteraction(); movement.BeginDrag(); movement.EndDrag(); pet.CompleteBehaviorPointerInteraction();
                Require(events.SequenceEqual(new[] { "clicked" }), "click_does_not_report_a_lift_or_drag");
                events.Clear();
                pet.BeginBehaviorPointerInteraction(); movement.BeginDrag();
                before = NativeMethods.GetWindowBoundsPixels(hwnd);
                NativeMethods.SetWindowPositionPixels(hwnd, (int)before.Left - 20, (int)before.Top - 85);
                await Task.Delay(60);
                movement.EndDrag(); pet.CompleteBehaviorPointerInteraction();
                await Until(() => movement.Grounded, 8);
                Require(events.Count(kind => kind == "drag_started") == 1 && events.Count(kind => kind == "released") == 1,
                    "real_drag_distance_produces_one_start_and_release_event");
                Require(events.Count(kind => kind == "fall_started") == 1 && events.Count(kind => kind == "landed") == 1,
                    "gravity_remains_active_in_model_mode_and_emits_edge_events_once");
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require((await pet.ExecuteAsync(new("walk", Direction: "left"), snapshot.Revision, CancellationToken.None)).Accepted, "walk_before_hide_started");
                pet.Hide();
                Require(!movement.IsDirectedWalking && !(await pet.ExecuteAsync(new("walk", Direction: "left"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "hide_revokes_lease_and_pending_actions");
                pet.Show();

                phase = "performance_ownership";
                pet.SelectSkin("deepseek");
                await Until(() => pet.UsesBuiltInScenes && pet.IsScenePlaying && !pet.IsSceneLoading, 30);
                await Until(() => movement.Grounded && pet.CurrentSceneAction is "idle" or "working", 8);
                await pet.SingAsync();
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(snapshot.UserPerformance && snapshot.Performances.SequenceEqual(PetPerformanceCatalog.Names) && snapshot.Performances.Count == 13,
                    "manual_singing_is_user_owned_and_all_thirteen_first_category_performances_are_advertised");
                Require(!(await pet.ExecuteAsync(new("stop_performance"), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("play_performance", "sing"), snapshot.Revision, CancellationToken.None)).Accepted && pet.IsSinging,
                    "model_cannot_stop_or_replace_user_performance");
                pet.StopSinging();
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require((await pet.ExecuteAsync(new("play_performance", "sing"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "registered_model_performance_plays_with_saved_sound_setting");
                Require(!(await pet.GetSnapshotAsync(CancellationToken.None)).UserPerformance && !settings.SoundEnabled,
                    "model_ownership_does_not_enable_muted_audio");
                movement.BeginDrag();
                Require(!pet.IsSinging && (await pet.GetSnapshotAsync(CancellationToken.None)).State == "drag", "drag_preempts_model_singing");
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(!(await pet.ExecuteAsync(new("play_performance", "sing"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "model_cannot_replace_passive_drag_with_performance");
                movement.EndDrag();
                await Until(() => movement.Grounded && pet.CurrentSceneAction is "idle" or "working", 8);

                phase = "new_emotions";
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(snapshot.CanJump && snapshot.CanClimb, "grounded_builtin_character_advertises_new_locomotion");
                foreach (string action in new[] { "jump-left", "jump-right", "climb-left", "climb-right", "climb-top-left", "climb-top-right", "fall", "land", "drag-start", "drag-loop" })
                    Require(!(await pet.ExecuteAsync(new("play_performance", action), snapshot.Revision, CancellationToken.None)).Accepted,
                        "installed_physical_animation_cannot_be_requested_as_performance_" + action);
                Require(!(await pet.ExecuteAsync(new("jump", Direction: "left", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("climb", Direction: "right", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted,
                    "passive_origin_cannot_use_new_locomotion_even_when_assets_and_capability_are_available");

                foreach (string emotion in new[] { "happy", "angry", "startled", "bashful" })
                {
                    snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                    var origin = emotion == "startled" ? PetBehaviorOrigin.PassiveReaction : PetBehaviorOrigin.Conversation;
                    Require((await pet.ExecuteAsync(new("play_performance", emotion, Origin: origin), snapshot.Revision, CancellationToken.None)).Accepted &&
                        pet.CurrentSceneAction == emotion && pet.IsScenePlaying,
                        "new_emotion_starts_actual_renderer_with_valid_host_receipt_" + emotion);
                    var playing = await pet.GetSnapshotAsync(CancellationToken.None);
                    Require(!playing.UserPerformance && !playing.CanJump && !playing.CanClimb && !settings.SoundEnabled,
                        "model_emotion_owns_performance_and_blocks_locomotion_without_unmuting_" + emotion);
                    await Until(() => pet.CurrentSceneAction is "idle" or "working", 6);
                    await Task.Delay(220);
                    Require(pet.CurrentSceneAction is "idle" or "working" && (await pet.GetSnapshotAsync(CancellationToken.None)).CanWalk,
                        "new_emotion_plays_once_and_restores_automatic_state_" + emotion);
                }

                phase = "gif_performances";
                foreach (string action in new[] { "token-come", "bowl-tap", "bowl-tap-eager", "dance", "dance-2", "dance-3", "fireworks" })
                {
                    var definition = pet.CurrentScenePack!.Actions[action];
                    double duration = ActionDurationSeconds(definition);
                    Require(!definition.Repeat && definition.Audio == null && double.IsFinite(duration) && duration > 0,
                        "gif_performance_has_finite_manifest_duration_and_no_unassigned_audio_" + action);
                    snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                    var origin = action == "bowl-tap" ? PetBehaviorOrigin.PassiveReaction : PetBehaviorOrigin.Conversation;
                    var started = Stopwatch.StartNew();
                    Require((await pet.ExecuteAsync(new("play_performance", action, Origin: origin), snapshot.Revision, CancellationToken.None)).Accepted &&
                        pet.CurrentSceneAction == action && pet.IsScenePlaying,
                        "gif_performance_starts_actual_renderer_with_valid_host_receipt_" + action);
                    var playing = await pet.GetSnapshotAsync(CancellationToken.None);
                    Require(!playing.UserPerformance && !playing.CanWalk && !playing.CanJump && !playing.CanClimb && !settings.SoundEnabled,
                        "gif_performance_reserves_animation_without_movement_or_unmuting_" + action);
                    await Until(() => pet.CurrentSceneAction is "idle" or "working", (int)Math.Ceiling(duration) + 8);
                    await Task.Delay(220);
                    Require(started.Elapsed.TotalSeconds >= duration * .8 &&
                        pet.CurrentSceneAction is "idle" or "working" && pet.IsScenePlaying &&
                        (await pet.GetSnapshotAsync(CancellationToken.None)).CanWalk,
                        "gif_performance_completes_once_and_restores_automatic_renderer_" + action);
                }
                phase = "gif_settings_entry";
                var preferences = new AppSettingsWindow(pet) { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
                try
                {
                    preferences.Show();
                    preferences.UpdateLayout();
                    var choices = Descendants<ComboBox>(preferences).Single(control => control.Name == "PetEmotion");
                    var playButton = Descendants<Button>(preferences).Single(control => control.Name == "PlayPetEmotion");
                    Require(choices.Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag).SequenceEqual(PetPerformanceCatalog.Names),
                        "settings_lists_all_installed_songs_expressions_and_performances");
                    choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "dance-2");
                    pet.SetSoundVolume(.42);
                    Require((choices.SelectedItem as ComboBoxItem)?.Tag as string == "dance-2" && playButton.IsEnabled,
                        "settings_refresh_keeps_selected_gif_performance");
                    preferences.SavePreview(Path.Combine(fixture, "gif-settings.png"));
                    playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => pet.CurrentSceneAction == "dance-2" && pet.IsScenePlaying, 8);
                    Require((await pet.GetSnapshotAsync(CancellationToken.None)).UserPerformance,
                        "settings_play_button_starts_selected_gif_as_user_owned_performance");
                    await Until(() => pet.CurrentSceneAction is "idle" or "working", 8);
                    Require(playButton.IsEnabled && !settings.SoundEnabled,
                        "settings_gif_playback_finishes_without_changing_sound_preference");
                }
                finally { preferences.Close(); }
                phase = "performance_ownership_shutdown";
                await pet.PreviewSceneAsync("angry");
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(snapshot.UserPerformance && pet.CurrentSceneAction == "angry" &&
                    !(await pet.ExecuteAsync(new("play_performance", "happy", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("stop_performance"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "manual_emotion_retains_ownership_against_model_replacement_and_stop");
                await Until(() => pet.CurrentSceneAction is "idle" or "working", 6);
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require((await pet.ExecuteAsync(new("play_performance", "bashful"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "model_emotion_started_before_mode_shutdown");
                pet.SetLlmBehaviorActive(false);
                Require(!(await pet.GetSnapshotAsync(CancellationToken.None)).Enabled && !movement.IsDirectedWalking,
                    "leaving_model_mode_revokes_only_model_actions");
                await Until(() => pet.CurrentSceneAction is "idle" or "working", 6);
                Require(!movement.HasScriptedMotion, "leaving_model_mode_clears_model_emotion_and_locomotion_ownership");
                phase = "explicit_music_without_model_behavior";
                pet.SetExplicitBehaviorCommandsActive(true);
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(!(await pet.ExecuteAsync(new("play_performance", "happy"), snapshot.Revision, CancellationToken.None)).Accepted &&
                    !(await pet.ExecuteAsync(new("play_performance", "bangyou-dance", Origin: PetBehaviorOrigin.PassiveReaction), snapshot.Revision, CancellationToken.None)).Accepted,
                    "text_only_music_commands_do_not_enable_other_actions_or_passive_performances");
                Require((await pet.ExecuteAsync(new("play_performance", "bangyou-dance"), snapshot.Revision, CancellationToken.None)).Accepted &&
                    pet.CurrentSceneAction == "bangyou-dance" && !settings.SoundEnabled,
                    "explicit_bangyou_request_starts_its_actual_renderer_without_enabling_sound_or_full_behavior_control");
                snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                Require(!snapshot.UserPerformance && (await pet.ExecuteAsync(new("stop_performance"), snapshot.Revision, CancellationToken.None)).Accepted,
                    "text_only_command_can_stop_its_own_bangyou_performance");
                await Until(() => pet.CurrentSceneAction is "idle" or "working", 6);
                pet.SetExplicitBehaviorCommandsActive(false);
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }

    private static double ActionDurationSeconds(SceneAnimationAction action) =>
        ClipDurationSeconds(action.Start) + ClipDurationSeconds(action.Loop) + ClipDurationSeconds(action.End);

    private static double ClipDurationSeconds(SceneAnimationClip? clip)
    {
        if (clip == null) return 0;
        if (clip.DurationsMs is { Length: > 0 }) return clip.DurationsMs.Sum() / 1000d;
        int frames = clip.Type == "frames" ? clip.Frames.Length : clip.FrameCount;
        if (frames <= 0 || !double.IsFinite(clip.Fps) || clip.Fps <= 0)
            throw new InvalidOperationException("fixture_requires_manifest_timed_animation");
        return frames / clip.Fps;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("fixture_condition_timeout");
            await Task.Delay(25);
        }
    }
}
