using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// Explicit isolated CLI check. Silent PCM fixtures exercise the actual browser
// audio clock without making noise, accessing Codex, or saving user preferences.
internal static class SceneAudioUiChecks
{
    internal static int Run()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "scene-audio-ui-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var captures = new List<object>();
        PetWindow? pet = null;
        int result = 1;
        bool finished = false;
        bool realMp3DecodedMuted = false;
        bool supportAvailable = true;
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true;
            timeout.Stop();
            try { pet?.Close(); }
            catch (Exception ex) { passed = false; error = "Cleanup: " + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "scene-audio-ui-validation.json"), JsonSerializer.Serialize(new
            {
                passed, assertions = checks.Count, checks, captures, error, fixtures = output,
                silentPcmFixtures = true, realMp3DecodedMuted, browserProfileIsolated = true, realAccountRequests = 0,
                userSettingsWrites = 0, realSessionChanged = false, syntheticMouseOrKeyboardInput = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "SCENE_AUDIO_UI_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var settings = new DesktopSettings
                {
                    SkinId = "deepseek", Size = 160, Left = 160, Top = 80, Topmost = false, ClickThrough = true,
                    MovementEnabled = false, MotionEnabled = false, SceneAutoEnabled = true,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false
                };
                Require(settings.SoundEnabled && Math.Abs(settings.SoundVolume - .35) < .001, "New and legacy settings default to enabled sound at 35 percent");
                settings.SoundVolume = double.NaN; settings.Normalize();
                Require(settings.SoundVolume == .35, "Invalid persisted audio volume recovers safely");
                settings.SoundVolume = 2; settings.Normalize(); Require(settings.SoundVolume == 1, "Excess volume clamps at 100 percent");
                settings.SoundVolume = -.5; settings.Normalize(); Require(settings.SoundVolume == 0, "Negative volume clamps at zero");
                settings.SoundVolume = .35;
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false,
                    motionPreferences: () => new MotionPreferences(true, false),
                    querySession: () => new WindowsSessionSnapshot(42, false, true, false), subscribeToSystemEvents: false,
                    movementPlatformProvider: hwnd =>
                    {
                        var work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
                        return new DesktopPlatformSnapshot(new[] { work }, supportAvailable ?
                            new[] { new DesktopPlatform(new IntPtr(9876), work.Left, work.Right, work.Bottom - 240,
                                new Rect(work.Left, work.Bottom - 240, work.Width, 150)) } : Array.Empty<DesktopPlatform>());
                    })
                { RendererProfileDirectory = Path.Combine(output, "browser") };
                pet.Show();
                await WaitFor(() => Task.FromResult(pet.IsLoaded && pet.DisplayLifecycle != null && pet.MovementController != null), "isolated pet startup");
                await WaitAction(pet, "idle");
                pet.SetChatBusy(true);
                await WaitAction(pet, "working");
                pet.SetCodexStatus(CodexSnapshot.Empty);
                await WaitAction(pet, "working");
                Require(pet.IsChatBusy && !(await Read(pet)).GetProperty("audioEnabled").GetBoolean(), "Local chat drives silent work while disconnected Codex updates remain independent");
                pet.SetChatBusy(false);
                await WaitAction(pet, "idle");
                Require(!pet.IsChatBusy, "Completing or cancelling local chat restores idle without a Codex task");
                var pack = CreateFixture(output);
                Require(await pet.SelectScenePackAsync(pack), "Local singing fixture loads in the production pet");
                await WaitAction(pet, "idle");
                var initial = await Read(pet);
                Require(!pet.IsSinging && pet.CanSing && !initial.GetProperty("audioEnabled").GetBoolean() && !initial.GetProperty("audioActive").GetBoolean(), "Selecting a music-capable pack keeps idle silent and does not sing");

                await pet.SingAsync(); await WaitAction(pet, "sing");
                await WaitFor(async () => (await Read(pet)).GetProperty("audioTime").GetDouble() > .15, "singing audio clock");
                var singing = await Read(pet);
                Require(pet.IsSinging && singing.GetProperty("audioEnabled").GetBoolean() && !singing.GetProperty("audioMuted").GetBoolean(), "Explicit singing enables the silent test track and runs its clock");
                pet.SetChatBusy(true);
                Require(pet.IsSinging && pet.CurrentSceneAction == "sing", "A local reply does not override explicit singing");
                pet.SetChatBusy(false);
                Require(pet.IsSinging && pet.CurrentSceneAction == "sing", "Ending a local reply does not stop explicit singing");
                pet.SetSoundEnabled(false);
                await WaitFor(async () => (await Read(pet)).GetProperty("audioMuted").GetBoolean(), "mute acknowledgement");
                Require(pet.IsSinging && !settings.SoundEnabled, "Muting preserves the explicitly selected song state");
                pet.SetSoundVolume(.2); pet.SetSoundEnabled(true);
                await WaitFor(async () => Math.Abs((await Read(pet)).GetProperty("audioVolume").GetDouble() - .2) < .001, "volume acknowledgement");
                Require(settings.SoundVolume == .2, "Live sound volume uses the persisted preference");
                await WaitAction(pet, "idle");
                Require(!pet.IsSinging && !(await Read(pet)).GetProperty("audioActive").GetBoolean(), "Song end releases its sound and restores automatic idle");

                await pet.SingAsync(); await WaitAction(pet, "sing");
                Point footBeforeDrag = WorldFoot(pet);
                pet.MovementController!.BeginDrag(); await WaitAction(pet, "drag");
                Require(!pet.IsSinging && !(await Read(pet)).GetProperty("audioActive").GetBoolean(), "Dragging interrupts a manual song even when autonomous movement is disabled");
                Require(pet.SceneHeadroomDip == 0 && (WorldFoot(pet) - footBeforeDrag).Length <= 1.5, "Dragging removes thought headroom without moving the character");
                pet.MovementController.EndDrag(); await WaitAction(pet, "idle");

                var hwnd = new WindowInteropHelper(pet).Handle;
                var work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
                var own = NativeMethods.GetWindowBoundsPixels(hwnd);
                NativeMethods.SetWindowPositionPixels(hwnd, (int)own.Left, (int)Math.Round(work.Bottom - 240 - pet.VisibleFootPixels.Y));
                pet.SetMovement(true);
                await WaitFor(() => Task.FromResult(pet.MovementController.Grounded), "synthetic support contact");
                await pet.SingAsync(); await WaitAction(pet, "sing");
                supportAvailable = false;
                await WaitAction(pet, "fall");
                Require(!pet.IsSinging && !(await Read(pet)).GetProperty("audioActive").GetBoolean(), "Losing window support interrupts a song and selects the falling state");
                pet.SetMovement(false); await WaitAction(pet, "idle");

                await pet.SingAsync(); await WaitAction(pet, "sing");
                Point footBeforeHide = WorldFoot(pet);
                pet.Hide();
                await WaitFor(async () => !pet.IsSinging && (await Read(pet)).GetProperty("audioMuted").GetBoolean(), "hidden sound stop");
                Require(!pet.CanSing, "A hidden pet cannot start a new song");
                pet.Show(); await WaitAction(pet, "idle");
                Require(!pet.IsSinging && !(await Read(pet)).GetProperty("audioActive").GetBoolean(), "Showing a hidden pet does not restart the song");
                Require(pet.SceneHeadroomDip == 0 && (WorldFoot(pet) - footBeforeHide).Length <= 1.5, "Hide and restore retain ordinary body geometry and position");

                await pet.SingAsync(); await WaitAction(pet, "sing");
                Point footBeforeLock = WorldFoot(pet);
                pet.DisplayLifecycle!.SimulateSignal(DisplayLifecycleSignal.SessionLocked);
                Require(pet.SceneRenderer == null && !pet.IsSinging && !pet.CanSing, "Locking releases the sound renderer and clears manual singing");
                pet.DisplayLifecycle.SimulateSignal(DisplayLifecycleSignal.SessionUnlocked);
                await pet.DisplayLifecycle.RestorePendingAsync(); await WaitAction(pet, "idle");
                Require(!pet.IsSinging && !(await Read(pet)).GetProperty("audioActive").GetBoolean(), "Unlocking restores idle without replaying an old song");
                Require(pet.SceneHeadroomDip == 0 && (WorldFoot(pet) - footBeforeLock).Length <= 1.5, "Lock and unlock release temporary headroom without moving the feet");

                pet.SetCodexStatus(CodexSnapshot.Empty with
                {
                    Connected = true, Tasks = new[] { new CodexTaskInfo("audio-ui", "fixture", "active", "codex", null, null, 0, false) }
                });
                await WaitAction(pet, "working");
                Require(!(await Read(pet)).GetProperty("audioEnabled").GetBoolean(), "Automatic task changes keep imported action audio inaudible");
                await pet.SingAsync(); await WaitAction(pet, "sing");
                await WaitAction(pet, "working");
                Require(!pet.IsSinging && !(await Read(pet)).GetProperty("audioEnabled").GetBoolean(), "Song completion restores a running Codex work state silently");
                await pet.SingAsync(); await WaitAction(pet, "sing");
                pet.StopSinging();
                await WaitAction(pet, "working");
                await WaitFor(async () => !pet.IsSinging && !(await Read(pet)).GetProperty("audioEnabled").GetBoolean(), "manual stop");
                Require(!pet.IsSinging, "The explicit stop control cancels the song");
                pet.SelectSkin("gemini");
                Require(!pet.CanSing, "Singing controls are disabled for skins without a selected singing action");
                pet.SetSoundEnabled(false);
                pet.SetCodexStatus(CodexSnapshot.Empty);
                pet.SelectSkin("deepseek");
                await WaitAction(pet, "idle");
                Require(pet.CanSing && !pet.IsSinging, "Bundled DeepSeek exposes singing without starting it on selection");
                await pet.SingAsync(); await WaitAction(pet, "sing");
                await WaitFor(async () => (await Read(pet)).GetProperty("audioTime").GetDouble() >= 9, "actual bundled MP3 decoding while muted");
                var realTrack = await Read(pet);
                Require(realTrack.GetProperty("audioDuration").GetDouble() > 30 && realTrack.GetProperty("audioMuted").GetBoolean() && realTrack.GetProperty("thoughtVisible").GetBoolean() && realTrack.GetProperty("thoughtText").GetString()!.Length > 8,
                    "Bundled MP3 decodes silently and drives the visible thinking panel");
                realMp3DecodedMuted = true;
                Point footBeforeResize = WorldFoot(pet);
                pet.SetSize(240); await Task.Delay(150);
                Require(pet.IsSinging && pet.SceneHeadroomDip > 0 && (WorldFoot(pet) - footBeforeResize).Length <= 1.5 &&
                    Math.Abs((await Read(pet)).GetProperty("bodyViewportHeight").GetDouble() - 246) < .01,
                    "Resizing an active song scales the ordinary body viewport while preserving screen-space feet");
                Point footBeforeBubble = WorldFoot(pet);
                double headroomBeforeBubble = pet.SceneHeadroomDip;
                pet.Say("Layout verification"); await Task.Delay(120);
                captures.Add(new { kind = "bubble-layout", headroomBeforeBubble, headroomAfterBubble = pet.SceneHeadroomDip,
                    footBeforeBubble, footAfterBubble = WorldFoot(pet), dpi = NativeMethods.GetDpiScale(new WindowInteropHelper(pet).Handle) });
                Require(pet.SceneHeadroomDip >= headroomBeforeBubble + 60 && (WorldFoot(pet) - footBeforeBubble).Length <= 1.5,
                    "A status bubble receives separate space above thoughts without overlapping or moving the body");
                foreach (int size in new[] { 160, 240, 400 })
                {
                    pet.StopSinging(); await WaitAction(pet, "idle");
                    pet.SetSize(size); pet.UpdateLayout(); await Task.Delay(120);
                    var idle = await Read(pet);
                    double idleWidth = idle.GetProperty("mediaRect").GetProperty("w").GetDouble();
                    double idleHeight = idle.GetProperty("mediaRect").GetProperty("h").GetDouble();
                    double idleBaseline = idle.GetProperty("mediaRect").GetProperty("y").GetDouble();
                    double windowHeight = pet.Height;
                    Point footBeforeSong = WorldFoot(pet);
                    await pet.SingAsync(); await WaitAction(pet, "sing"); await Task.Delay(150);
                    var sing = await Read(pet);
                    Point footDuringSong = WorldFoot(pet);
                    Require(Math.Abs(sing.GetProperty("mediaRect").GetProperty("w").GetDouble() - idleWidth) <= .01 &&
                        Math.Abs(sing.GetProperty("mediaRect").GetProperty("h").GetDouble() - idleHeight) <= .01 &&
                        Math.Abs(sing.GetProperty("mediaRect").GetProperty("y").GetDouble() - idleBaseline) <= .01,
                        "Singing preserves the idle canvas scale and local foot baseline at " + size + " px");
                    Require((footDuringSong - footBeforeSong).Length <= 1.5 && settings.Size == size && pet.Height > windowHeight,
                        "Singing adds headroom above fixed screen-space feet without changing size preference at " + size + " px");
                    Require(sing.GetProperty("canvasTop").GetDouble() >= sing.GetProperty("thoughtHeadroom").GetDouble() - 1,
                        "Thinking panel stays above the unchanged body viewport at " + size + " px");
                    string file = Path.Combine(output, "deepseek-sing-" + size + ".png");
                    await pet.SceneRenderer!.CaptureAsync(file);
                    var renderer = pet.SceneRenderer;
                    Point origin = renderer.TranslatePoint(new Point(0, 0), pet);
                    double dpi = VisualTreeHelper.GetDpi(pet).DpiScaleY;
                    double renderedFootY = (origin.Y + FootGeometry.GetPngFoot(file).Y * renderer.ActualHeight) * dpi;
                    double expectedFootY = pet.VisibleFootPixels.Y;
                    Require(Math.Abs(renderedFootY - expectedFootY) <= 3 * dpi,
                        "Thinking panel keeps the rendered feet aligned with physical contact at " + size + " px");
                    pet.StopSinging(); await WaitAction(pet, "idle");
                    Require(Math.Abs(pet.Height - windowHeight) < .01 && pet.SceneHeadroomDip == 0 && (WorldFoot(pet) - footBeforeSong).Length <= 1.5,
                        "Stopping restores ordinary geometry with no screen-space foot jump at " + size + " px");
                    captures.Add(new { size, file, audioMuted = true, renderedFootY, expectedFootY, idleWidth, idleHeight, footBeforeSong, footDuringSong });
                }
                pet.StopSinging(); await WaitAction(pet, "idle");
                Require(!(await Read(pet)).GetProperty("audioActive").GetBoolean(), "Stopping the real track releases music and restores DeepSeek idle");
                var imported = new SceneAnimationCatalog().Validate(pack.ManifestPath, "custom-audio-layout-check");
                Require(await pet.SelectScenePackAsync(imported), "An imported-style action pack uses the generic contact measurement path");
                // Imported packs may use the static skin for automatic idle.
                // This silent, three-second preview gives a stable body-only
                // baseline without changing their normal automatic behavior.
                await pet.PreviewSceneAsync("working"); await WaitAction(pet, "working");
                var importedIdle = await pet.SceneRenderer!.CaptureContactGeometryAsync();
                await pet.SingAsync(); await WaitAction(pet, "sing");
                var importedSing = await pet.SceneRenderer!.CaptureContactGeometryAsync();
                Require(pet.SceneHeadroomDip > 0 && Math.Abs(importedIdle.Foot.Y - importedSing.Foot.Y) < .01 &&
                    Math.Abs(importedIdle.Bounds.Height - importedSing.Bounds.Height) < .01,
                    "Imported singing contact measurement excludes the thinking panel and stays normalized to the body");
                pet.StopSinging(); await WaitFor(() => Task.FromResult(!pet.IsScenePlaying), "imported default static skin");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run();
        return result;
    }

    private static async Task<JsonElement> Read(PetWindow pet)
    {
        string json = JsonSerializer.Deserialize<string>(await pet.SceneRenderer!.GetDiagnosticsAsync())!;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static Point WorldFoot(PetWindow pet)
    {
        var bounds = NativeMethods.GetWindowBoundsPixels(new WindowInteropHelper(pet).Handle);
        var foot = pet.VisibleFootPixels;
        return new Point(bounds.Left + foot.X, bounds.Top + foot.Y);
    }
    private static Task WaitAction(PetWindow pet, string action) => WaitFor(async () =>
    {
        if (pet.SceneRenderer == null || pet.IsSceneLoading || pet.CurrentSceneAction != action || !pet.IsScenePlaying) return false;
        var state = await Read(pet);
        return state.GetProperty("action").GetString() == action && state.GetProperty("hasFrame").GetBoolean();
    }, "action " + action);
    private static async Task WaitFor(Func<Task<bool>> condition, string phase)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await condition()) return;
            await Task.Delay(30);
        }
        throw new TimeoutException(phase);
    }

    private static SceneAnimationPack CreateFixture(string output)
    {
        string root = Path.Combine(output, "pack"); Directory.CreateDirectory(root);
        byte[] pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 140; pixels[i + 2] = 40; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(root, "frame.png"))) png.Save(stream);
        const int sampleRate = 8000, samples = sampleRate * 3;
        using (var writer = new BinaryWriter(File.Create(Path.Combine(root, "silent.wav")), Encoding.ASCII))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); writer.Write(new byte[samples * 2]);
        }
        object Action(bool audio, bool showThoughts = false) => new
        {
            loop = new { type = "frames", frames = new[] { "frame.png" }, fps = 12 }, repeat = true,
            audio = audio ? new { file = "silent.wav" } : null,
            thoughts = showThoughts ? new { title = "DeepSeek", lines = new[] { "Thinking fixture", "Let me go." } } : null
        };
        string manifest = Path.Combine(root, "fixture.petanim.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            version = 1, name = "Silent audio UI verification", skinId = "deepseek",
            actions = new Dictionary<string, object>
            {
                ["idle"] = Action(false), ["working"] = Action(true), ["drag"] = Action(false), ["fall"] = Action(false),
                ["land"] = Action(false), ["walk-left"] = Action(false), ["walk-right"] = Action(false), ["sing"] = Action(true, true)
            }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        return new SceneAnimationCatalog().Validate(manifest, BundledSceneAnimations.DeepSeekId);
    }
}
