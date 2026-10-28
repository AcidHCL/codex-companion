using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// Explicit isolated diagnostic: real bundled artwork and the production WPF /
// WebView renderer, but no account client, saved settings or audible playback.
internal static class RedrawnAnimationIntegrationChecks
{
    internal static int Run()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "redrawn-animation-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var captures = new List<object>();
        var measurements = new List<object>();
        var clock = Stopwatch.StartNew();
        PetWindow? pet = null;
        SceneAnimationLibraryWindow? libraryWindow = null;
        string phase = "startup";
        int result = 1;
        bool finished = false;
        JsonElement? lastRendererState = null;
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };

        void Require(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name);
        }

        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true;
            timeout.Stop();
            var hostState = new
            {
                visible = pet?.IsVisible, loaded = pet?.IsLoaded, sceneLoading = pet?.IsSceneLoading,
                sceneStatus = pet?.SceneStatus, sceneAction = pet?.CurrentSceneAction,
                scenePackId = pet?.CurrentScenePack?.Id, rendererPresent = pet?.SceneRenderer != null,
                rendererFailed = pet?.SceneRenderer?.IsFailed,
                displaySuspended = pet?.DisplayLifecycle?.IsSuspended,
                displayRestoring = pet?.DisplayLifecycle?.IsRestoring,
                displayRestoreAttempts = pet?.DisplayLifecycle?.RestoreAttempts
            };
            try { libraryWindow?.Close(); }
            catch (Exception ex) { passed = false; error = "Library cleanup: " + ex.GetType().Name; }
            try { pet?.Close(); }
            catch (Exception ex) { passed = false; error = "Pet cleanup: " + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "redrawn-animation-validation.json"), JsonSerializer.Serialize(new
            {
                passed, assertions = checks.Count, checks, phase, error, captures, measurements, hostState, lastRendererState,
                elapsedMs = clock.ElapsedMilliseconds, evidenceDirectory = output,
                realAccountRequests = 0, userSettingsWrites = 0, realSessionChanged = false,
                existingPetTouched = false, syntheticMouseOrKeyboardInput = false,
                browserProfileIsolated = true, soundExplicitlyDisabled = true,
                preferencePersistenceCheck = "in-memory JSON round-trip; no real settings file read or written"
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }

        async Task Wait(Func<Task<bool>> condition, string label, int milliseconds = 20000)
        {
            var watch = Stopwatch.StartNew();
            while (!finished && watch.ElapsedMilliseconds < milliseconds && clock.Elapsed.TotalSeconds < 115)
            {
                if (await condition()) return;
                await Task.Delay(25);
            }
            throw new TimeoutException(label);
        }

        async Task<JsonElement> Read()
        {
            if (pet?.SceneRenderer == null) throw new InvalidOperationException("Scene renderer is unavailable during " + phase);
            using var response = JsonDocument.Parse(await pet.SceneRenderer.GetDiagnosticsAsync());
            // ExecuteScriptAsync serializes the return value; the renderer
            // deliberately returns JSON.stringify(...), so decode both layers.
            if (response.RootElement.ValueKind == JsonValueKind.String)
            {
                using var document = JsonDocument.Parse(response.RootElement.GetString()!);
                lastRendererState = document.RootElement.Clone();
            }
            else lastRendererState = response.RootElement.Clone();
            return lastRendererState.Value;
        }

        async Task<JsonElement> WaitAction(string action)
        {
            JsonElement state = default;
            await Wait(async () =>
            {
                if (pet?.SceneRenderer == null || pet.IsSceneLoading || pet.CurrentSceneAction != action) return false;
                state = await Read();
                return state.GetProperty("playing").GetBoolean() && state.GetProperty("hasFrame").GetBoolean()
                    && state.GetProperty("action").GetString() == action;
            }, "Rendered action: " + action);
            return state;
        }

        async Task Capture(string label)
        {
            string file = Path.Combine(output, label + ".png");
            await pet!.SceneRenderer!.CaptureAsync(file);
            var state = await Read();
            captures.Add(new { label, file, pack = pet.CurrentScenePack!.Id,
                action = state.GetProperty("action").GetString(), frame = state.GetProperty("frame").GetInt32(),
                renderedFrame = state.TryGetProperty("renderedFrame", out var rendered) ? rendered.GetInt32() : (int?)null,
                waitingFrame = state.TryGetProperty("waitingFrame", out var waiting) && waiting.GetBoolean() });
        }

        async Task CaptureLibrary(string label)
        {
            libraryWindow = new SceneAnimationLibraryWindow(pet!)
            {
                ShowActivated = false, ShowInTaskbar = false, Topmost = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = 380, Top = 80
            };
            libraryWindow.Show();
            libraryWindow.UpdateLayout();
            await Task.Delay(80);
            var lists = Descendants<ListBox>(libraryWindow).ToArray();
            var listed = lists.SelectMany(list => list.Items.OfType<SceneAnimationPack>()).ToArray();
            Require(listed.Any(p => p.Id == BundledSceneAnimations.DeepSeekId)
                && listed.Any(p => p.Id == BundledSceneAnimations.DeepSeekRedrawnId), label + "_library_displays_both_bundles");
            Require(lists.Any(list => list.SelectedItem is SceneAnimationPack pack && pack.Id == pet!.CurrentScenePack!.Id),
                label + "_library_selects_actual_active_pack");
            var dpi = VisualTreeHelper.GetDpi(libraryWindow);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(libraryWindow.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(libraryWindow.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(libraryWindow);
            string file = Path.Combine(output, label + ".png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
            captures.Add(new { label, file, pack = pet!.CurrentScenePack!.Id, kind = "scene-library" });
            libraryWindow.Close(); libraryWindow = null;
        }

        timeout.Tick += (_, _) => Finish(false, "REDRAWN_ANIMATION_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                phase = "catalog-and-mapping";
                var packs = SceneAnimationLibrary.Load(out var issues);
                var legacy = packs.Single(p => p.Id == BundledSceneAnimations.DeepSeekId);
                var redrawn = packs.Single(p => p.Id == BundledSceneAnimations.DeepSeekRedrawnId);
                Require(legacy.Actions.Count == 25 && redrawn.Actions.Count == 25, "both_bundles_have_all_25_actions");
                Require(Clips(legacy).Count() == 26 && Clips(redrawn).Count() == 26, "both_bundles_have_26_start_loop_end_phases");
                Require(Clips(legacy).Sum(c => c.Frames.Length) == 212 && Clips(redrawn).Sum(c => c.Frames.Length) == 1567,
                    "legacy_212_and_redrawn_1567_png_frames");
                Require(redrawn.Actions["drag"].Start?.Frames.Length == 12 && redrawn.Actions["drag"].Loop.Frames.Length == 24
                    && Duration(redrawn.Actions["drag"].Start!) == 500 && Duration(redrawn.Actions["drag"].Loop) == 1000,
                    "drag_lift_12_frames_500ms_then_struggle_24_frames_1000ms");
                Require(Duration(redrawn.Actions["dance"].Loop) == 9710 && Duration(redrawn.Actions["dance-2"].Loop) == 5670
                    && Duration(redrawn.Actions["dance-3"].Loop) == 19000 && redrawn.Actions["dance-3"].Loop.Frames.Length == 456,
                    "all_three_dances_preserve_full_reviewed_durations");
                Require(Clips(redrawn).All(c => c.Type == "frames" && c.DurationsMs?.Length == c.Frames.Length
                    && c.DurationsMs.All(ms => ms >= 40 && ms <= 43)), "all_redrawn_phases_use_explicit_near_24fps_timings");
                Require(redrawn.Actions.All(pair => pair.Value.Repeat == legacy.Actions[pair.Key].Repeat)
                    && redrawn.Actions["sing"].Audio?.File == legacy.Actions["sing"].Audio?.File
                    && redrawn.Actions["sing"].Thoughts!.Lines.SequenceEqual(legacy.Actions["sing"].Thoughts!.Lines),
                    "legacy_repeat_music_and_thoughts_behavior_preserved");
                Require(File.ReadAllBytes(Path.Combine(legacy.RootDirectory, legacy.Actions["sing"].Audio!.File))
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(redrawn.RootDirectory, redrawn.Actions["sing"].Audio!.File))),
                    "singing_music_is_byte_identical_across_versions");
                measurements.Add(new { kind = "catalog", legacyFrames = 212, redrawnFrames = 1567,
                    legacyFiles = legacy.Files.Count, redrawnFiles = redrawn.Files.Count, unrelatedLibraryIssueCount = issues.Count });

                phase = "isolated-pet-startup";
                var settings = new DesktopSettings
                {
                    SkinId = "deepseek", ScenePackId = legacy.Id, DeepSeekAnimationPackId = legacy.Id,
                    Size = 200, Left = 100, Top = 100, Topmost = false, ClickThrough = true,
                    MovementEnabled = false, MotionEnabled = false, SceneAutoEnabled = true,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false,
                    SoundEnabled = false, SoundVolume = 0, LlmBehaviorEnabled = false, WebSearchEnabled = false
                };
                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                pet = new PetWindow(skins, settings, persist: false,
                    motionPreferences: () => new MotionPreferences(true, false),
                    querySession: () => new WindowsSessionSnapshot(42, false, true, false), subscribeToSystemEvents: false)
                { RendererProfileDirectory = Path.Combine(output, "browser") };
                pet.Show();
                await WaitAction("idle");

                phase = "redrawn-idle-and-working";
                Require(await pet.SelectScenePackAsync(redrawn), "new_bundle_selection_succeeds");
                await WaitAction("idle");
                Require(pet.UsesBuiltInScenes && settings.DeepSeekAnimationPackId == redrawn.Id, "new_bundle_is_builtin_and_sets_version_preference");
                await Wait(async () => (await Read()).GetProperty("frame").GetInt32() >= 10, "New idle advances beyond old eight frames");
                await Capture("redrawn-idle");
                var idle = await Read();
                Require(!idle.GetProperty("audioEnabled").GetBoolean() && !idle.GetProperty("audioActive").GetBoolean(),
                    "automatic_new_idle_has_no_active_audio");
                pet.SetChatBusy(true); await WaitAction("working");
                Require(pet.CurrentScenePack!.Actions["working"].Loop.Frames.Length == 24, "local_chat_uses_new_24_frame_working");
                await Capture("redrawn-working");

                phase = "short-performance-restores-automatic-state";
                await pet.PreviewSceneAsync("bowl-tap"); await WaitAction("bowl-tap");
                await WaitAction("working");
                Require(pet.IsChatBusy && pet.CurrentSceneAction == "working", "one_shot_performance_restores_automatic_working");

                phase = "dance3-above-300-and-final-frame";
                var danceClock = Stopwatch.StartNew();
                await pet.PreviewSceneAsync("dance-3"); await WaitAction("dance-3");
                long decodeMs = danceClock.ElapsedMilliseconds;
                pet.SceneRenderer!.SetMotionFrame("dance-3", 305);
                await Wait(async () => RenderedFrame(await Read(), 305), "dance-3 frame 305 actually drawn");
                Require((await Read()).GetProperty("motionControlled").GetBoolean(), "dance3_external_frame_control_exceeds_previous_300_frame_cap");
                pet.SceneRenderer.SetMotionFrame("dance-3", 455);
                await Wait(async () => RenderedFrame(await Read(), 455), "dance-3 final frame 455 actually drawn");
                Require(RenderedFrame(await Read(), 455), "dance3_final_frame_is_rendered_not_only_requested");
                await Capture("redrawn-dance3-frame-456");
                pet.SceneRenderer.SetMotionFrame(null, 0);
                await WaitAction("working");
                Require(pet.CurrentSceneAction == "working", "dance3_final_frame_completes_and_restores_automatic_state");
                measurements.Add(new { kind = "dance3", frames = 456, decodeMs, controlledFrames = new[] { 305, 455 }, totalMs = 19000 });

                phase = "singing-layout-and-muted-audio";
                pet.SetChatBusy(false); await WaitAction("idle");
                var beforeSong = await Read();
                double bodyHeight = beforeSong.GetProperty("bodyViewportHeight").GetDouble();
                var beforeRect = beforeSong.GetProperty("mediaRect");
                Point beforeFoot = WorldFoot(pet);
                await pet.SingAsync(); await WaitAction("sing");
                await Wait(async () => (await Read()).GetProperty("audioTime").GetDouble() > .35, "Muted MP3 playback clock");
                var song = await Read();
                Require(song.GetProperty("audioActive").GetBoolean() && song.GetProperty("audioMuted").GetBoolean()
                    && !song.GetProperty("audioEnabled").GetBoolean() && song.GetProperty("audioVolume").GetDouble() == 0,
                    "real_music_decodes_with_sound_disabled_and_zero_volume");
                Require(song.GetProperty("thoughtVisible").GetBoolean() && song.GetProperty("thoughtText").GetString()!.Length > 0,
                    "singing_thinking_panel_is_visible_and_advances");
                var songRect = song.GetProperty("mediaRect");
                Require(Math.Abs(song.GetProperty("bodyViewportHeight").GetDouble() - bodyHeight) < .01
                    && Math.Abs(bodyHeight - (settings.Size + 6)) < .01
                    && Math.Abs(songRect.GetProperty("w").GetDouble() - beforeRect.GetProperty("w").GetDouble()) < .01
                    && Math.Abs(songRect.GetProperty("h").GetDouble() - beforeRect.GetProperty("h").GetDouble()) < .01,
                    "singing_keeps_idle_body_viewport_and_model_canvas_size");
                Require(pet.SceneHeadroomDip > 0 && (WorldFoot(pet) - beforeFoot).Length <= 1.5,
                    "singing_adds_thought_headroom_without_moving_physical_feet");
                await Capture("redrawn-sing-muted");
                pet.StopSinging(); await WaitAction("idle");
                Require(pet.SceneHeadroomDip == 0 && !(await Read()).GetProperty("audioActive").GetBoolean(),
                    "stopping_song_releases_audio_and_headroom");
                await CaptureLibrary("library-redrawn-selected");

                phase = "legacy-rollback-and-working";
                Require(await pet.SelectScenePackAsync(legacy), "legacy_bundle_selection_succeeds");
                var oldIdle = await WaitAction("idle");
                Require(pet.CurrentScenePack!.Id == legacy.Id && settings.DeepSeekAnimationPackId == legacy.Id
                    && pet.CurrentScenePack.Actions["idle"].Loop.Frames.Length == 8 && oldIdle.GetProperty("frame").GetInt32() < 8,
                    "rollback_uses_real_legacy_eight_frame_idle_and_updates_preference");
                await Capture("legacy-idle");
                pet.SetChatBusy(true); await WaitAction("working");
                await Wait(async () => (await Read()).GetProperty("frame").GetInt32() >= 2, "Legacy working frame advances");
                Require(pet.CurrentScenePack.Actions["working"].Loop.Frames.Length == 8, "legacy_automatic_working_remains_eight_frames");
                pet.SetChatBusy(false); await WaitAction("idle");
                await CaptureLibrary("library-legacy-selected");

                phase = "version-preference-survives-skin-roundtrip";
                pet.SelectSkin("gemini");
                Require(pet.CurrentSkin.Id == "gemini" && settings.DeepSeekAnimationPackId == legacy.Id,
                    "other_skin_keeps_legacy_version_preference");
                pet.SelectSkin("deepseek"); await WaitAction("idle");
                Require(pet.CurrentScenePack!.Id == legacy.Id, "returning_to_deepseek_restores_selected_legacy_version");
                Require(await pet.SelectScenePackAsync(redrawn), "new_version_can_be_reselected_after_rollback");
                await WaitAction("idle");
                pet.SelectSkin("gemini");
                Require(settings.DeepSeekAnimationPackId == redrawn.Id, "other_skin_keeps_redrawn_version_preference");
                var restored = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings))!;
                restored.Normalize();
                Require(restored.DeepSeekAnimationPackId == redrawn.Id, "redrawn_preference_survives_settings_serialization_without_disk_write");
                pet.SelectSkin("deepseek"); await WaitAction("idle");
                Require(pet.CurrentScenePack!.Id == redrawn.Id && settings.ScenePackId == redrawn.Id
                    && settings.DeepSeekAnimationPackId == redrawn.Id, "returning_to_deepseek_restores_redrawn_bundle_and_preference");
                Require(!settings.CodexEnabled && !settings.CodexMessagesEnabled && !settings.LlmBehaviorEnabled
                    && !settings.SoundEnabled && settings.SoundVolume == 0, "all_isolated_connection_and_sound_guards_remain_disabled");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
            finally
            {
                // Finish also handles the dispatcher timeout, and is idempotent.
                if (!finished) Finish(false, "Diagnostic ended without a result");
            }
        };
        app.Run();
        return result;
    }

    private static IEnumerable<SceneAnimationClip> Clips(SceneAnimationPack pack) => pack.Actions.Values
        .SelectMany(action => new[] { action.Start, action.Loop, action.End }).OfType<SceneAnimationClip>();

    private static long Duration(SceneAnimationClip clip) => clip.DurationsMs?.Sum(ms => (long)ms)
        ?? (long)Math.Round(clip.Frames.Length * 1000 / clip.Fps);

    private static bool RenderedFrame(JsonElement state, int frame) => state.GetProperty("frame").GetInt32() == frame
        && state.GetProperty("renderedFrame").GetInt32() == frame && !state.GetProperty("waitingFrame").GetBoolean();

    private static Point WorldFoot(PetWindow pet)
    {
        var bounds = NativeMethods.GetWindowBoundsPixels(new WindowInteropHelper(pet).Handle);
        var foot = pet.VisibleFootPixels;
        return new Point(bounds.Left + foot.X, bounds.Top + foot.Y);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
