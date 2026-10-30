using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopPet;

// The fixture owns the sole HWND it moves. Platforms are synthetic rectangles;
// real desktop windows, account state, model endpoints and input are untouched.
internal static class DeepSeekMotionIntegrationChecks
{
    public static int Run(bool redrawn = false, bool gpt = false)
    {
        var checks = new List<string>();
        var captures = new List<object>();
        var transitions = new List<object>();
        object? failureState = null;
        string evidenceName = gpt ? "gpt-motion" : redrawn ? "redrawn-motion" : "deepseek-motion";
        string output = Path.Combine(AppContext.BaseDirectory, evidenceName + "-evidence");
        Directory.CreateDirectory(output);
        string profile = Path.Combine(output, "webview-" + Guid.NewGuid().ToString("N"));
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        PetWindow? pet = null;
        bool finished = false;
        string phase = "startup";
        int result = 1;
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try { pet?.Close(); } catch (Exception ex) { passed = false; error = ex.ToString(); }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, evidenceName + "-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, count = checks.Count, checks, captures, transitions, failureState, error, phase,
                userSettingsWrites = 0, accountRequests = 0, modelRequests = 0, userWindowsMoved = 0, operatingSystemInputInjected = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "fixture_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                DesktopPlatformSnapshot? synthetic = null;
                var settings = new DesktopSettings
                {
                    SkinId = gpt ? "gpt" : "deepseek", Size = 200, MovementEnabled = false, MotionEnabled = false,
                    DeepSeekAnimationPackId = redrawn ? DeepSeekMotionTimeline.RedrawnPackId : "builtin-deepseek-small",
                    SoundEnabled = false, Topmost = false, CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false
                };
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, false,
                    () => new MotionPreferences(true, false), () => new WindowsSessionSnapshot(42, false, true, false), false,
                    handle => synthetic ?? new DesktopPlatformSnapshot(new[] { NativeMethods.GetWindowWorkAreaPixels(handle) }, Array.Empty<DesktopPlatform>()))
                    { RendererProfileDirectory = profile };
                pet.Show();
                await Until(() => pet.IsLoaded && pet.MovementController != null && pet.UsesBuiltInScenes && pet.IsScenePlaying && !pet.IsSceneLoading, 30);
                var movement = pet.MovementController!;
                var timeline = DeepSeekMotionTimeline.ForPack(pet.CurrentScenePack!);
                Require(timeline.IsLegacy == !(redrawn || gpt), "selected_pack_has_matching_motion_profile");
                void Record(string reason)
                {
                    if (transitions.Count >= 300) transitions.RemoveAt(0);
                    transitions.Add(new { at = DateTimeOffset.UtcNow, reason, movement.State, movement.HasScriptedMotion,
                        movement.MotionAction, movement.MotionFrame, movement.Grounded, scene = pet.CurrentSceneAction,
                        pet.IsScenePlaying, pet.SceneStatus });
                }
                movement.StateChanged += state => Record("state:" + state);
                movement.MotionFrameChanged += () => Record("frame");
                movement.Landed += () => Record("landed");
                pet.SettingsChanged += () => Record("settings/scene");
                pet.SceneRenderer!.Completed += () => Record("renderer-complete");
                var hwnd = new WindowInteropHelper(pet).Handle;
                var work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
                double dpi = NativeMethods.GetDpiScale(hwnd);
                double center = work.Left + work.Width / 2;
                pet.SetLlmBehaviorActive(true);
                var frames = new List<(string? Action, int Frame)>();
                movement.MotionFrameChanged += () => frames.Add((movement.MotionAction, movement.MotionFrame));
                int impacts = 0;
                movement.Landed += () => impacts++;

                async Task<JsonElement> Read()
                {
                    string json = JsonSerializer.Deserialize<string>(await pet.SceneRenderer!.GetDiagnosticsAsync())
                        ?? throw new InvalidOperationException("empty_renderer_diagnostics");
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.Clone();
                }
                async Task<JsonElement> WaitScene(string action, int? frame = null)
                {
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed.TotalSeconds < 12)
                    {
                        if (pet.CurrentSceneAction == action && pet.IsScenePlaying)
                        {
                            var state = await Read();
                            if (state.GetProperty("displayedAction").GetString() == action && state.GetProperty("hasFrame").GetBoolean()
                                && (!frame.HasValue || state.GetProperty("frame").GetInt32() == frame.Value)) return state;
                        }
                        await Task.Delay(12);
                    }
                    throw new TimeoutException("scene_expected_" + action + "_" + frame + "_actual_" + pet.CurrentSceneAction + "_motion_" + movement.State);
                }
                async Task ResetOnFloor()
                {
                    pet.SetMovement(false);
                    synthetic = new DesktopPlatformSnapshot(new[] { work }, Array.Empty<DesktopPlatform>(), Array.Empty<DesktopClimbEdge>());
                    var anchor = pet.VisibleFootPixels;
                    NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(center - anchor.X), (int)Math.Round(work.Bottom - anchor.Y));
                    movement.NotifyExternalMove();
                    pet.SetMovement(true);
                    await Until(() => movement.Grounded && !movement.HasScriptedMotion && pet.CurrentSceneAction is "idle" or "working");
                    frames.Clear();
                }
                void SetTarget(Rect target)
                {
                    var surfaces = new[] { new DesktopWindowSurface(new IntPtr(59101), target) };
                    synthetic = new DesktopPlatformSnapshot(new[] { work }, DesktopWindowPlatforms.VisibleEdges(surfaces), DesktopWindowPlatforms.VisibleSides(surfaces));
                }
                async Task Capture(string name, Rect target)
                {
                    var renderer = pet.SceneRenderer!;
                    string file = Path.Combine(output, name + ".png");
                    var state = await Read();
                    var origin = renderer.PointToScreen(new Point());
                    var bounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                    string? action = movement.MotionAction;
                    int frame = movement.MotionFrame;
                    Point? contact = action == null ? null : pet.MotionContactPixels(action, frame);
                    await renderer.CaptureAsync(file);
                    string composite = Path.Combine(output, name + "-ledge.png");
                    using (var source = new System.Drawing.Bitmap(file))
                    using (var canvas = new System.Drawing.Bitmap(1000, Math.Max(900, (int)Math.Ceiling(target.Height) + 500)))
                    using (var graphics = System.Drawing.Graphics.FromImage(canvas))
                    using (var border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(45, 137, 192), 3))
                    using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(225, 235, 245)))
                    {
                        graphics.Clear(System.Drawing.Color.FromArgb(244, 247, 251));
                        double nearEdge = action?.EndsWith("-left", StringComparison.Ordinal) == true ? target.Right : target.Left;
                        double offsetX = nearEdge - 500, offsetY = target.Top - 380;
                        graphics.FillRectangle(brush, (float)(target.Left - offsetX), (float)(target.Top - offsetY), (float)target.Width, (float)target.Height);
                        graphics.DrawRectangle(border, (float)(target.Left - offsetX), (float)(target.Top - offsetY), (float)target.Width, (float)target.Height);
                        graphics.DrawImageUnscaled(source, (int)Math.Round(origin.X - offsetX), (int)Math.Round(origin.Y - offsetY));
                        canvas.Save(composite, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    captures.Add(new { name, file, composite, action, frame, state, hwnd = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                        sceneOrigin = new { origin.X, origin.Y },
                        contact = contact.HasValue ? new { x = bounds.X + contact.Value.X, y = bounds.Y + contact.Value.Y } : null,
                        target = new { target.X, target.Y, target.Width, target.Height } });
                }

                foreach (int direction in new[] { 1, -1 })
                {
                    string side = direction > 0 ? "right" : "left";
                    phase = "jump_" + side;
                    await ResetOnFloor();
                    double height = Math.Min(160, 105 * dpi);
                    var target = new Rect(direction > 0 ? center + 180 : center - 410, work.Bottom - height, 230, height - 5);
                    SetTarget(target);
                    var before = await Read();
                    var snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                    int impactsBefore = impacts;
                    var accepted = await pet.ExecuteAsync(new PetBehaviorCommand("jump", Direction: side), snapshot.Revision, CancellationToken.None);
                    Require(accepted.Accepted && movement.HasScriptedMotion, "model_jump_begins_scripted_" + side);
                    var jumpClip = timeline.Clip("jump-" + side)!;
                    await WaitScene("jump-" + side, redrawn ? jumpClip.TakeoffFrame + 1 : 2);
                    Require(!movement.Grounded && movement.State == "jump", "airborne_uses_directed_jump_" + side);
                    var air = await Read();
                    Require(air.GetProperty("motionControlled").GetBoolean() && !air.GetProperty("timerActive").GetBoolean(), "physics_owns_jump_frame_clock_" + side);
                    await Capture("jump-" + side + "-air", target);
                    await Until(() => !movement.HasScriptedMotion && movement.Grounded, 10);
                    Require(movement.SupportHandle == new IntPtr(59101) && Math.Abs(movement.FootScreen.Y - target.Top) < 1,
                        "actual_hwnd_jump_lands_on_selected_top_" + side);
                    Require(impacts == impactsBefore + 1 && frames.Where(x => x.Action == "jump-" + side).Select(x => x.Frame).Contains(jumpClip.FrameCount - 1),
                        "jump_finish_pose_and_single_impact_" + side);
                    Require((await Read()).GetProperty("blankClears").GetInt32() == before.GetProperty("blankClears").GetInt32(), "jump_has_no_transparent_clear_" + side);
                    await Until(() => pet.CurrentSceneAction is "idle" or "working", 8);
                }

                foreach (int direction in new[] { 1, -1 })
                {
                    string side = direction > 0 ? "right" : "left";
                    phase = "climb_" + side;
                    await ResetOnFloor();
                    var bounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                    var hand = pet.MotionContactPixels("climb-" + side, 0)!.Value;
                    double edgeX = bounds.Left + hand.X + direction * 10 * dpi;
                    double height = Math.Min(390 * dpi, work.Height - 320 * dpi);
                    var target = new Rect(direction > 0 ? edgeX : edgeX - 360 * dpi, work.Bottom - height, 360 * dpi, height - 4);
                    SetTarget(target);
                    var snapshot = await pet.GetSnapshotAsync(CancellationToken.None);
                    var accepted = await pet.ExecuteAsync(new PetBehaviorCommand("climb", Direction: side), snapshot.Revision, CancellationToken.None);
                    Require(accepted.Accepted && movement.HasScriptedMotion, "model_climb_begins_near_exposed_side_" + side);
                    await WaitScene("climb-" + side, 1);
                    var climbing = await Read();
                    Require(climbing.GetProperty("motionControlled").GetBoolean(), "climb_uses_external_frame_control_" + side);
                    await Capture("climb-" + side, target);
                    await WaitScene("climb-top-" + side, 0);
                    Require(movement.State == "climb-top" && movement.HasScriptedMotion, "climb_reaches_top_out_" + side);
                    await Capture("climb-top-" + side + "-hang", target);
                    var topClip = timeline.Clip("climb-top-" + side)!;
                    // The redraw's final pose lasts only ~40 ms. Sample early
                    // in its standing segment, before an async WebView read
                    // can race the return to the canonical idle anchor. Full
                    // final-frame coverage is asserted from motion events below.
                    await WaitScene("climb-top-" + side, redrawn ? topClip.FrameCount - 5 : topClip.FrameCount - 1);
                    var topBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                    var topContact = pet.MotionContactPixels("climb-top-" + side, movement.MotionFrame)!.Value;
                    Require(Math.Abs(topBounds.Top + topContact.Y - target.Top) <= 1.1, "final_visual_foot_contacts_top_edge_" + side);
                    await Capture("climb-top-" + side + "-stand", target);
                    await Until(() => !movement.HasScriptedMotion && movement.Grounded, 6);
                    Require(movement.SupportHandle == new IntPtr(59101) && Math.Abs(movement.FootScreen.Y - target.Top) < 1,
                        "climb_returns_canonical_foot_to_supported_top_" + side);
                    Require(frames.Where(x => x.Action == "climb-top-" + side).Select(x => x.Frame).Distinct().Count() == topClip.FrameCount,
                        "all_topout_poses_bound_to_real_locomotion_" + side);
                    await Until(() => pet.CurrentSceneAction is "idle" or "working", 6);
                }

                phase = "interruptions";
                await ResetOnFloor();
                var throwStart = NativeMethods.GetWindowBoundsPixels(hwnd);
                movement.BeginDrag();
                await Task.Delay(40);
                NativeMethods.SetWindowPositionPixels(hwnd, (int)throwStart.Left, (int)Math.Round(throwStart.Top - 24 * dpi));
                movement.ObserveDrag();
                await Task.Delay(40);
                NativeMethods.SetWindowPositionPixels(hwnd, (int)throwStart.Left, (int)Math.Round(throwStart.Top - 48 * dpi));
                movement.ObserveDrag();
                movement.EndDrag();
                Require(!movement.HasScriptedMotion && movement.State == "jump" && movement.MotionAction == null,
                    "upward_mouse_release_is_physical_throw_without_scripted_jump_authority");
                var passiveAir = await WaitScene("fall");
                Require(!passiveAir.GetProperty("motionControlled").GetBoolean() && passiveAir.GetProperty("displayedAction").GetString() == "fall",
                    "upward_throw_renders_passive_air_without_jump_crouch_or_landing_poses");
                await Until(() => movement.Grounded && !movement.HasScriptedMotion, 8);
                await Until(() => pet.CurrentSceneAction is "idle" or "working", 8);
                Require(Math.Abs(movement.FootScreen.Y - work.Bottom) <= 1, "upward_throw_returns_to_real_floor_and_idle");
                await ResetOnFloor();
                var baseBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                var rightHand = pet.MotionContactPixels("climb-right", 0)!.Value;
                var interruptionTarget = new Rect(baseBounds.Left + rightHand.X, work.Bottom - 380 * dpi, 360 * dpi, 375 * dpi);
                SetTarget(interruptionTarget);
                Require(movement.TryClimbToWindow(1), "climb_started_before_target_disappears");
                await Until(() => !movement.Grounded && movement.HasScriptedMotion);
                synthetic = new DesktopPlatformSnapshot(new[] { work }, Array.Empty<DesktopPlatform>(), Array.Empty<DesktopClimbEdge>());
                await Until(() => !movement.HasScriptedMotion, 3);
                Require(movement.State is "fall" or "idle", "missing_target_releases_into_physical_fall");
                await Until(() => movement.Grounded, 8);
                await ResetOnFloor();
                baseBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
                interruptionTarget = new Rect(baseBounds.Left + rightHand.X, work.Bottom - 380 * dpi, 360 * dpi, 375 * dpi);
                SetTarget(interruptionTarget);
                Require(movement.TryClimbToWindow(1), "climb_started_before_drag");
                movement.BeginDrag();
                Require(!movement.HasScriptedMotion && movement.State == "drag" && movement.MotionAction == null,
                    "mouse_drag_cancels_scripted_animation_and_motion");
                movement.EndDrag();
                await Until(() => movement.Grounded, 8);
                await ResetOnFloor();
                SetTarget(new Rect(center + 180, work.Bottom - 120, 230, 115));
                Require(movement.TryJumpToWindow(1), "jump_started_before_hide");
                pet.Hide();
                Require(!movement.HasScriptedMotion && movement.MotionAction == null, "hide_releases_scripted_frame_control");
                Require(!settings.SoundEnabled, "motion_never_unmutes_sound");
                Finish(true);
            }
            catch (Exception ex)
            {
                try
                {
                    string? raw = pet?.SceneRenderer == null ? null : JsonSerializer.Deserialize<string>(await pet.SceneRenderer.GetDiagnosticsAsync());
                    failureState = new { scene = pet?.CurrentSceneAction, pet?.SceneStatus, pet?.IsScenePlaying,
                        pet?.CompanionSurfacesAvailable, pet?.IsVisible,
                        state = pet?.MovementController?.State, scripted = pet?.MovementController?.HasScriptedMotion,
                        action = pet?.MovementController?.MotionAction, frame = pet?.MovementController?.MotionFrame,
                        grounded = pet?.MovementController?.Grounded,
                        renderer = raw == null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(raw) };
                }
                catch (Exception diagnostics) { failureState = new { diagnosticError = diagnostics.Message }; }
                Finish(false, ex.ToString());
            }
        };
        app.Run(); return result;
    }

    private static async Task Until(Func<bool> predicate, int seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException("fixture_condition_timeout");
            await Task.Delay(20);
        }
    }
}
