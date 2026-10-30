using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopPet;

// Explicit diagnostic only: the real renderer and bundled artwork run inside a
// non-persisting pet with synthetic tasks, no model client and disabled sound.
internal static class GptAnimationIntegrationChecks
{
    internal static int Run()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "gpt-animation-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var captures = new List<object>();
        PetWindow? pet = null;
        string phase = "startup";
        JsonElement? lastState = null;
        bool finished = false;
        int result = 1;
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
            finished = true; timeout.Stop();
            var host = new { skin = pet?.CurrentSkin.Id, pack = pet?.CurrentScenePack?.Id,
                action = pet?.CurrentSceneAction, pet?.IsSceneLoading, pet?.SceneStatus, rendererFailed = pet?.SceneRenderer?.IsFailed };
            try { pet?.Close(); }
            catch (Exception ex) { passed = false; error = "Cleanup: " + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gpt-animation-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, phase, host, lastState, captures, error,
                userSettingsWrites = 0, realAccountRequests = 0, modelRequests = 0, userWindowsMoved = 0,
                operatingSystemInputInjected = false, soundDisabled = true, browserProfileIsolated = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        async Task Wait(Func<bool> condition, string label, int milliseconds = 15000)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.ElapsedMilliseconds > milliseconds) throw new TimeoutException(label);
                await Task.Delay(30);
            }
        }
        async Task WaitAsync(Func<Task<bool>> condition, string label, int milliseconds = 15000)
        {
            var elapsed = Stopwatch.StartNew();
            while (!await condition())
            {
                if (elapsed.ElapsedMilliseconds > milliseconds) throw new TimeoutException(label);
                await Task.Delay(30);
            }
        }
        async Task<JsonElement> Read()
        {
            var renderer = pet?.SceneRenderer ?? throw new InvalidOperationException("GPT renderer is unavailable.");
            string? json = JsonSerializer.Deserialize<string>(await renderer.GetDiagnosticsAsync());
            if (json == null) throw new InvalidOperationException("GPT renderer returned no diagnostics.");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("GPT renderer diagnostics are not an object.");
            lastState = document.RootElement.Clone();
            return lastState.Value;
        }
        async Task<JsonElement> WaitScene(string action)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < 20)
            {
                if (pet is { IsSceneLoading: false, IsScenePlaying: true } && pet.CurrentSceneAction == action)
                {
                    var state = await Read();
                    if (state.GetProperty("displayedAction").GetString() == action && state.GetProperty("hasFrame").GetBoolean()) return state;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Expected " + action + ", got " + pet?.CurrentSceneAction + ": " + pet?.SceneStatus);
        }
        async Task Capture(string name)
        {
            string path = Path.Combine(output, name + ".png");
            await pet!.SceneRenderer!.CaptureAsync(path);
            captures.Add(new { name, path, skin = pet.CurrentSkin.Id, action = pet.CurrentSceneAction });
        }
        timeout.Tick += (_, _) => Finish(false, "GPT_ANIMATION_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var settings = new DesktopSettings
                {
                    SkinId = "gpt", DeepSeekAnimationPackId = BundledSceneAnimations.DeepSeekRedrawnId,
                    Size = 200, Left = 180, Top = 80, Topmost = false, ClickThrough = true,
                    MovementEnabled = false, MotionEnabled = false, SceneAutoEnabled = false,
                    SoundEnabled = false, SoundVolume = 0, SpeechEnabled = false,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false
                };
                settings.Normalize();
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false,
                    motionPreferences: () => new MotionPreferences(true, false),
                    querySession: () => new WindowsSessionSnapshot(42, false, true, false), subscribeToSystemEvents: false,
                    movementPlatformProvider: handle => new DesktopPlatformSnapshot(
                        new[] { NativeMethods.GetWindowWorkAreaPixels(handle) }, Array.Empty<DesktopPlatform>()))
                    { RendererProfileDirectory = Path.Combine(output, "browser") };
                pet.Show();
                await Wait(() => pet.IsLoaded && pet.MovementController != null && pet.DisplayLifecycle != null, "isolated GPT startup");
                var idle = await WaitScene("idle");
                Require(pet.UsesBuiltInScenes && pet.CurrentScenePack?.Id == BundledSceneAnimations.GptId && settings.ScenePackId == BundledSceneAnimations.GptId,
                    "GPT automatically selects its own built-in animated idle");
                Require(settings.DeepSeekAnimationPackId == BundledSceneAnimations.DeepSeekRedrawnId && !settings.MotionEnabled && !settings.SceneAutoEnabled,
                    "GPT defaults work independently of breathing and custom automation while retaining DeepSeek preference");
                Require(!pet.CanSing && PetPerformanceCatalog.Installed(pet.CurrentScenePack).Count == 4 &&
                    !pet.CanPlayPerformance("bangyou-dance") && !pet.CanPlayPerformance("dance") && !pet.CanPlayPerformance("walk-right"),
                    "GPT exposes four expressions without unavailable songs, dances or physical-state performance tools");
                Require(!idle.GetProperty("audioActive").GetBoolean() && !idle.GetProperty("thoughtVisible").GetBoolean(),
                    "GPT idle does not inherit DeepSeek music or thoughts");
                await Capture("gpt-idle");

                phase = "automatic-working";
                pet.SetChatBusy(true);
                var working = await WaitScene("working");
                string? request = working.GetProperty("requestId").GetString();
                int draws = working.GetProperty("draws").GetInt32();
                pet.SetCodexStatus(CodexSnapshot.Empty);
                await Task.Delay(550);
                var advanced = await Read();
                Require(pet.CurrentSceneAction == "working" && advanced.GetProperty("requestId").GetString() == request &&
                    advanced.GetProperty("draws").GetInt32() > draws,
                    "Local chat drives advancing GPT work animation despite disconnected Codex and disabled breathing");
                await Capture("gpt-working");
                pet.SetChatBusy(false); await WaitScene("idle");
                pet.SetCodexStatus(CodexSnapshot.Empty with { Connected = true,
                    Tasks = new[] { new CodexTaskInfo("gpt-fixture", "fixture", "active", "codex", null, null, 0, false) } });
                await WaitScene("working");
                Require(pet.CurrentSceneAction == "working", "Synthetic active Codex task drives GPT working");
                pet.SetCodexStatus(CodexSnapshot.Empty); await WaitScene("idle");

                phase = "all-rendered-actions";
                Point contact = pet.VisibleFootPixels;
                double bodyHeight = (await Read()).GetProperty("bodyViewportHeight").GetDouble();
                var actions = pet.CurrentScenePack!.Actions.Keys.ToArray();
                foreach (string action in actions)
                {
                    await pet.PreviewSceneAsync(action);
                    var state = await WaitScene(action);
                    Require(state.GetProperty("hasFrame").GetBoolean() && !state.GetProperty("audioActive").GetBoolean() &&
                        !state.GetProperty("thoughtVisible").GetBoolean() &&
                        Math.Abs(state.GetProperty("bodyViewportHeight").GetDouble() - bodyHeight) < .01 &&
                        (pet.VisibleFootPixels - contact).Length < .25,
                        "GPT action renders at its ordinary body scale and fixed contact: " + action);
                    if (action is "angry" or "drag" or "climb-top-right") await Capture("gpt-" + action);
                    pet.StopScenePreview(); await WaitScene("idle");
                }
                Require(actions.Length == 17 && pet.IsScenePlaying, "All 17 GPT actions preview and restore animated idle");

                phase = "passive-drag-and-expression-completion";
                pet.SetChatBusy(true); await WaitScene("working");
                await pet.PreviewSceneAsync("happy"); await WaitScene("happy");
                await WaitScene("working");
                Require(pet.IsChatBusy, "A one-shot GPT expression restores the active working state");
                pet.MovementController!.BeginDrag();
                var drag = await WaitScene("drag");
                Require(drag.GetProperty("phase").GetString() == "start", "Mouse lift starts GPT's lift segment once");
                await WaitAsync(async () => (await Read()).GetProperty("phase").GetString() == "loop", "GPT drag reaches struggle loop");
                Require(pet.CurrentSceneAction == "drag" && !settings.MovementEnabled, "Dragging overrides GPT work even with movement disabled");
                pet.MovementController.EndDrag(); await WaitScene("working");
                pet.SetChatBusy(false); await WaitScene("idle");

                phase = "skin-and-display-restore";
                pet.SelectSkin("deepseek"); await WaitScene("idle");
                Require(pet.CurrentScenePack?.Id == BundledSceneAnimations.DeepSeekRedrawnId, "Returning to DeepSeek restores its previous animation version");
                pet.SelectSkin("gpt"); await WaitScene("idle");
                Require(pet.CurrentScenePack?.Id == BundledSceneAnimations.GptId && settings.DeepSeekAnimationPackId == BundledSceneAnimations.DeepSeekRedrawnId,
                    "Returning to GPT restores its own pack without changing DeepSeek preference");
                pet.SelectSkin("gemini");
                await Wait(() => pet.CurrentScenePack == null && pet.SceneRenderer == null && !pet.IsSceneLoading, "Gemini static default");
                Require(!pet.UsesBuiltInScenes, "Other character skins retain their existing static defaults");
                pet.SelectSkin("gpt"); await WaitScene("idle");
                pet.DisplayLifecycle!.SimulateSignal(DisplayLifecycleSignal.SessionLocked);
                Require(pet.SceneRenderer == null, "Lock releases the GPT renderer");
                pet.DisplayLifecycle.SimulateSignal(DisplayLifecycleSignal.SessionUnlocked);
                await pet.DisplayLifecycle.RestorePendingAsync(); await WaitScene("idle");
                Require(pet.CurrentScenePack?.Id == BundledSceneAnimations.GptId && settings.DeepSeekAnimationPackId == BundledSceneAnimations.DeepSeekRedrawnId,
                    "Display restore retains GPT selection and the independent DeepSeek version preference");
                Require(!settings.SoundEnabled && !settings.SpeechEnabled && !settings.CodexEnabled && !settings.CodexMessagesEnabled,
                    "Isolated animation verification keeps sound and real account connections disabled");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }
}
