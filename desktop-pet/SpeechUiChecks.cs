using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// No real connection, production settings/database, audio output, or global pet mutex.
internal static class SpeechUiChecks
{
    internal static int Run()
    {
        string evidence = Path.Combine(AppContext.BaseDirectory, "speech-ui-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        var answers = new List<(string Full, string Bubble)>();
        var fallbackBubbles = new List<string>();
        var voiceSessions = new List<bool>();
        int stopped = 0, result = 1;
        PetWindow? pet = null; CodexDashboard? dashboard = null; CodexMonitor? monitor = null; OllamaChatWindow? chat = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        bool finished = false;
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try { chat?.Shutdown(); dashboard?.Close(); monitor?.Dispose(); pet?.Close(); }
            catch (Exception ex) { passed = false; error = "Cleanup: " + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "speech-ui-validation.json"), JsonSerializer.Serialize(new
            {
                passed, assertions = checks.Count, checks, error, evidence, audioOutput = false,
                realAccountRequests = 0, userSettingsWrites = 0, productionDatabaseOpened = false,
                finalReplyCallbacks = answers.Count, legacyBubbleCallbacks = fallbackBubbles.Count, stopCallbacks = stopped
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "SPEECH_UI_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var settings = new DesktopSettings { CodexEnabled = false, WebSearchEnabled = false, SharedMemoryEnabled = false, Topmost = false, MovementEnabled = false, MotionEnabled = false };
                Require(settings.SpeechEnabled && settings.SpeechVolume == .6, "new_and_legacy_preferences_enable_speech_at_60_percent");
                settings.SpeechVolume = double.NaN; settings.Normalize(); Require(settings.SpeechVolume == .6, "invalid_volume_recovers");
                settings.SpeechVolume = 2; settings.Normalize(); Require(settings.SpeechVolume == 1, "volume_clamps_high");
                settings.SpeechVolume = -1; settings.Normalize(); Require(settings.SpeechVolume == 0, "volume_clamps_low");
                settings.SpeechVolume = .6;
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false, subscribeToSystemEvents: false);
                monitor = new CodexMonitor(settings, new NoConnection(), persistSettings: false);
                dashboard = new CodexDashboard(monitor, settings, () => { }, pet) { ShowActivated = false, ShowInTaskbar = false };
                dashboard.Show(); await Settle(dashboard);
                var enabled = Named<CheckBox>(dashboard, "SpeechEnabled");
                var volume = Named<Slider>(dashboard, "SpeechVolume");
                Require(enabled.IsEnabled && enabled.IsChecked == true && !monitor.ConnectionEnabled, "speech_controls_work_with_codex_disabled");
                enabled.IsChecked = false;
                Require(!settings.SpeechEnabled && !volume.IsEnabled && !pet.IsSpeaking, "dashboard_off_applies_immediately_without_settings_file");
                enabled.IsChecked = true; volume.Value = 35;
                Require(settings.SpeechEnabled && Math.Abs(settings.SpeechVolume - .35) < .001, "dashboard_volume_applies_immediately");
                pet.SetSpeechEnabled(false);
                Require(enabled.IsChecked == false, "external_preference_change_updates_open_dashboard");
                pet.SetSpeechEnabled(true);
                Named<Button>(dashboard, "SpeechPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Named<Button>(dashboard, "SpeechStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Named<Button>(dashboard, "SpeechPreload").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Named<Button>(dashboard, "SpeechUnload").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!pet.IsSpeaking, "persist_false_ui_fixture_never_plays_audio");
                dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight; await Settle(dashboard);
                Require(Fits(dashboard, Named<Button>(dashboard, "SpeechStop")) && Fits(dashboard, Named<TextBlock>(dashboard, "SpeechStatus")), "voice_controls_fit_minimum_dashboard_size");
                Require(Fits(dashboard, Named<Button>(dashboard, "SpeechPreload")) && Fits(dashboard, Named<Button>(dashboard, "SpeechUnload")), "voice_model_load_controls_fit_dashboard");
                Capture(dashboard, Path.Combine(evidence, "dashboard-voice-minimum.png"));

                var client = new FixtureClient(() => answers.Count == 0);
                var store = new ChatMemoryStore(Path.Combine(evidence, "fixture.db"));
                chat = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, fallbackBubbles.Add, () => { }, client: client, chatStore: store,
                    answerCompleted: (full, bubble) => answers.Add((full, bubble)), stopSpeech: () => stopped++, voiceChatSessionActive: voiceSessions.Add);
                chat.Show();
                var input = Named<TextBox>(chat, "OllamaChatInput"); input.Text = "请给我完整的回答。";
                await Until(() => Named<Button>(chat, "OllamaSend").IsEnabled);
                Require(voiceSessions.Count > 0 && voiceSessions[^1] && client.Preloads == 0, "opening_chat_retains_voice_without_loading_chat_llm");
                string beforeLoadDraft = input.Text; string? beforeLoadConversation = chat.CurrentConversationId;
                var preload = chat.SetModelLoadedAsync(true);
                await Until(() => client.Preloads == 1);
                Require(!Named<ComboBox>(chat, "OllamaModelSelector").IsEnabled && !Named<Button>(chat, "OllamaSend").IsEnabled &&
                    !Named<Button>(chat, "OllamaMicrophone").IsEnabled && Named<Button>(chat, "OllamaStop").IsEnabled,
                    "preload_serializes_selected_model_and_disables_send_and_recording");
                await chat.SendAsync();
                Require(answers.Count == 0 && input.Text == beforeLoadDraft, "model_load_never_submits_a_chat_or_changes_draft");
                client.PreloadRelease.TrySetResult(); await preload;
                Require(chat.CurrentConversationId == beforeLoadConversation && Named<TextBlock>(chat, "OllamaChatStatus").Text.Contains("原有设置"),
                    "manual_preload_keeps_conversation_and_reports_existing_retention_policy");
                int unloads = client.Unloads; await chat.SetModelLoadedAsync(false);
                Require(client.Unloads == unloads + 1 && input.Text == beforeLoadDraft && Named<Button>(chat, "OllamaSend").IsEnabled,
                    "manual_unload_releases_only_model_and_keeps_chat_available");
                chat.Width = chat.MinWidth; chat.Height = chat.MinHeight; await Settle(chat);
                Require(Fits(chat, Named<Button>(chat, "OllamaPreloadModel")) && Fits(chat, Named<Button>(chat, "OllamaUnloadModel")) &&
                    Fits(chat, input) && Fits(chat, Named<Button>(chat, "OllamaMicrophone")), "model_load_buttons_and_composer_fit_small_chat");
                Capture(chat, Path.Combine(evidence, "chat-model-memory-minimum.png"));
                chat.Width = 410; chat.Height = 550;
                string first = chat.CurrentConversationId!;
                int beforeSend = stopped;
                await chat.SendAsync();
                Require(stopped > beforeSend && client.SilentBeforeFinal, "new_request_stops_previous_voice_and_partial_deltas_are_silent");
                Require(answers.Count == 1 && answers[0].Full == FixtureClient.Answer && answers[0].Full.Length > 100 && answers[0].Bubble.Length == 101, "one_complete_answer_callback_retains_full_text_and_short_bubble");
                Require(fallbackBubbles.Count == 0, "full_reply_callback_does_not_double_speak_legacy_bubble");
                int beforeNew = stopped;
                await chat.NewConversationAsync();
                Require(stopped > beforeNew && answers.Count == 1, "new_conversation_cancels_voice_without_reading_history");
                int beforeSwitch = stopped;
                await chat.SwitchConversationAsync(first);
                Require(stopped > beforeSwitch && answers.Count == 1 && fallbackBubbles.Count == 0, "restoring_conversation_stays_silent");
                int beforeClose = stopped;
                chat.Close();
                Require(stopped > beforeClose && !chat.IsVisible, "closing_chat_cancels_current_voice");
                Require(voiceSessions[^1] == false, "closing_chat_releases_voice_retention_lease");
                chat.Show(); await Until(() => Named<Button>(chat, "OllamaPreloadModel").IsEnabled);
                client.PreloadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelledPreload = chat.SetModelLoadedAsync(true);
                await Until(() => client.Preloads == 2); chat.Close(); await cancelledPreload;
                Require(client.PreloadCancellations == 1 && !chat.IsVisible && !voiceSessions[^1], "closing_chat_cancels_model_load_and_does_not_keep_voice_hot");
                await CheckStreamingChatAsync(Require);
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!condition()) await Task.Delay(25, timeout.Token); }
    private static async Task Settle(Window window)
    { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(50); window.UpdateLayout(); }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(x => x.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private static bool Fits(Window window, FrameworkElement element)
    { var root = (FrameworkElement)window.Content; var rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); return rect.Left >= 0 && rect.Top >= 0 && rect.Right <= root.ActualWidth + 1 && rect.Bottom <= root.ActualHeight + 1; }
    private static void Capture(Window window, string path)
    { var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file); }

    private static async Task CheckStreamingChatAsync(Action<bool, string> require)
    {
        var client = new StreamingFixtureClient();
        var segments = new List<string>();
        int begins = 0, completes = 0, cancels = 0, bubbles = 0, legacyReplies = 0;
        var callbacks = new PetChatSpeechCallbacks(() => ++begins, (id, text) => segments.Add(text), _ => completes++, () => cancels++, _ => bubbles++);
        var settings = new DesktopSettings { CodexEnabled = false, WebSearchEnabled = false, SharedMemoryEnabled = false };
        var chat = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => legacyReplies++, () => { }, client: client,
            answerCompleted: (_, _) => legacyReplies++, streamingSpeech: callbacks) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            chat.Show(); var input = Named<TextBox>(chat, "OllamaChatInput"); input.Text = "请分两段回答这个测试问题。";
            await Until(() => Named<Button>(chat, "OllamaSend").IsEnabled);
            string conversation = chat.CurrentConversationId!;
            var send = chat.SendAsync(); await Until(() => segments.Count > 0);
            require(begins == 1 && completes == 0 && !client.Completed && !segments[0].Contains("隐藏思考") && segments[0].Contains("第一段"),
                "first_visible_sentence_reaches_speech_before_final_llm_answer_and_hides_thoughts");
            client.Release.TrySetResult(); await send;
            require(completes == 1 && bubbles == 1 && legacyReplies == 0 && string.Concat(segments).Contains("第二段"),
                "streamed_answer_finishes_once_without_replaying_full_answer_callback");
            int segmentCount = segments.Count;
            await chat.NewConversationAsync(); await chat.SwitchConversationAsync(conversation);
            require(begins == 1 && segments.Count == segmentCount && completes == 1, "conversation_history_is_never_streamed_to_speech_again");
            client.Release = new(TaskCreationOptions.RunContinuationsAsynchronously); client.Completed = false;
            input.Text = "这个回复开始后会取消。";
            send = chat.SendAsync(); await Until(() => begins == 2 && segments.Count > segmentCount);
            Named<Button>(chat, "OllamaStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await send;
            client.Release.TrySetResult(); await Task.Delay(20);
            require(cancels == 1 && completes == 1 && bubbles == 1 && legacyReplies == 0 && input.Text.Contains("取消"),
                "cancelled_answer_stops_streamed_speech_without_late_final_or_duplicate_audio");
        }
        finally { chat.Shutdown(); }
    }

    private sealed class StreamingFixtureClient : IOllamaClient
    {
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Completed;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel });
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(new OllamaModelCapabilities(true, false, false, false));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new("<think>隐藏思考绝不能朗读。</think>这是第一段已经确定的正文，本鲸准备先说出来，再等待下一段。", false);
            await Release.Task.WaitAsync(ct);
            Completed = true;
            yield return new("这是第二段正文，整条回答现在才完成。", true);
        }
        public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FixtureClient(Func<bool> noFinalYet) : IOllamaClient
    {
        internal static readonly string Answer = string.Concat(Enumerable.Repeat("这是合成回答，会完整朗读而不是截断气泡中的一小段。", 8));
        internal bool SilentBeforeFinal;
        internal int Preloads, Unloads, PreloadCancellations;
        internal TaskCompletionSource PreloadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel });
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(new OllamaModelCapabilities(true, false, false, false));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new(Answer[..35], false); await Task.Delay(180, ct);
            SilentBeforeFinal = noFinalYet();
            yield return new(Answer[35..], true, 120, 100);
        }
        public async Task PreloadAsync(string model, CancellationToken ct)
        {
            Preloads++;
            try { await PreloadRelease.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { PreloadCancellations++; throw; }
        }
        public Task UnloadAsync(string model, CancellationToken ct) { Unloads++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class NoConnection : ICodexConnection
    {
        public string ConnectionDescription => "disabled synthetic transport";
        public string? CallerThreadId => null;
        public bool IsPairingRevoked => false;
        public bool HasSavedPairing => false;
        public bool ResumeSavedPairing() => false;
        public void SetMessageAccess(bool enabled) { }
        public Task EnsureConnectedAsync(CancellationToken token = default) => throw new InvalidOperationException("Real connection is forbidden in voice UI checks.");
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default) => throw new InvalidOperationException("RPC is forbidden in voice UI checks.");
        public void Disconnect() { }
        public Task ForgetAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
