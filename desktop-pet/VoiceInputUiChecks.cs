using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopPet;

// Isolated chat fixture: no microphone, speech backend, actual Ollama, or production storage.
internal static class VoiceInputUiChecks
{
    internal static int Run()
    {
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string evidence = Path.Combine(AppContext.BaseDirectory, "voice-input-ui-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var checks = new List<string>(); var pauses = new List<bool>();
        var recorder = new Recorder(); var recognizer = new Recognizer(); var client = new Client();
        OllamaChatWindow? chat = null; int result = 1; bool finished = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            try { chat?.Shutdown(); } catch (Exception ex) { passed = false; error = "cleanup:" + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "voice-input-ui-validation.json"), JsonSerializer.Serialize(new
            {
                passed, assertions = checks.Count, checks, error, evidence,
                microphoneOpened = false, realAsrRequests = 0, realModelRequests = 0,
                userSettingsWrites = 0, productionDatabaseOpened = false, inputEventsInjected = false,
                fakeRecorderStarts = recorder.Starts, fakeTranscriptions = recognizer.Calls, fakeChatRequests = client.Chats,
                recorderDisposed = recorder.Disposed, recognizerDisposed = recognizer.Disposed
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "VOICE_INPUT_UI_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var settings = new DesktopSettings { CodexEnabled = false, WebSearchEnabled = false, SharedMemoryEnabled = false, LlmBehaviorEnabled = false };
                var session = new PetVoiceInputSession(app.Dispatcher, recorder, recognizer);
                chat = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => { }, client: client,
                    clientFactory: endpoint => new Client { Endpoint = endpoint }, chatStore: new ChatMemoryStore(Path.Combine(evidence, "fixture.db")),
                    voiceInputSession: session, voiceInputActive: pauses.Add) { ShowActivated = false, ShowInTaskbar = false };
                chat.Show();
                var mic = Named<Button>(chat, "OllamaMicrophone");
                var input = Named<TextBox>(chat, "OllamaChatInput"); var send = Named<Button>(chat, "OllamaSend");
                await Until(() => mic.IsEnabled && Named<ComboBox>(chat, "OllamaModelSelector").IsEnabled);
                Require(recorder.Starts == 0 && recognizer.Calls == 0 && pauses.Count == 0, "opening_chat_never_opens_microphone_or_transcribes");
                chat.Width = chat.MinWidth; chat.Height = chat.MinHeight; await Settle(chat);
                Require(Fits(chat, mic) && Fits(chat, send) && !Overlaps(chat, mic, send), "microphone_and_send_fit_320px_compact_chat");
                input.Text = "先写好的草稿";
                await chat.ToggleVoiceInputAsync();
                Require(session.State == PetVoiceInputState.Recording && recorder.IsRecording && pauses.Last() && input.IsReadOnly, "explicit_mic_click_records_and_pauses_pet_playback");
                Require(!send.IsEnabled && !Named<Button>(chat, "OllamaAttachImage").IsEnabled && !Named<Button>(chat, "OllamaPasteImage").IsEnabled, "recording_disables_send_and_image_mutations");
                await chat.SendAsync(); Require(client.Chats == 0, "enter_or_programmatic_send_cannot_send_while_recording");
                await Settle(chat);
                Require(Fits(chat, Named<TextBlock>(chat, "OllamaVoiceInputStatus")), "recording_status_visible_in_compact_chat");
                Capture(chat, Path.Combine(evidence, "voice-recording-compact.png"));
                await chat.ToggleVoiceInputAsync();
                Require(session.State == PetVoiceInputState.Idle && !pauses.Last() && !recorder.IsRecording, "stop_finishes_transcription_and_releases_playback_pause");
                Require(input.Text == "先写好的草稿" + Environment.NewLine + Recognizer.Text, "transcription_appends_without_replacing_draft");
                Require(client.Chats == 0 && send.IsEnabled && !input.IsReadOnly, "transcribed_text_requires_explicit_send_and_can_be_edited");
                input.Text += "，请简短回答。"; await chat.SendAsync();
                Require(client.Chats == 1, "edited_transcript_uses_existing_llm_send_flow");
                await Until(() => mic.IsEnabled);

                string firstConversation = chat.CurrentConversationId!;
                recognizer.Pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await chat.ToggleVoiceInputAsync(); var pending = chat.ToggleVoiceInputAsync();
                await Until(() => session.State == PetVoiceInputState.Transcribing);
                await chat.NewConversationAsync(); string secondConversation = chat.CurrentConversationId!;
                Require(secondConversation != firstConversation && session.State == PetVoiceInputState.Idle && !pauses.Last(), "new_conversation_cancels_inflight_transcription");
                recognizer.Pending.SetResult("不应落到新对话的旧转写"); await pending; recognizer.Pending = null;
                Require(input.Text.Length == 0 && client.Chats == 1, "late_asr_result_cannot_mutate_new_conversation_or_send");

                await chat.ToggleVoiceInputAsync(); await chat.SwitchConversationAsync(firstConversation);
                Require(session.State == PetVoiceInputState.Idle && !recorder.IsRecording, "history_switch_releases_microphone");
                await chat.ToggleVoiceInputAsync();
                var selector = Named<ComboBox>(chat, "OllamaModelSelector"); selector.SelectedValue = OllamaModels.QualityModel;
                await Until(() => selector.IsEnabled && session.State == PetVoiceInputState.Idle);
                Require(!recorder.IsRecording, "model_switch_cancels_voice_input");

                await chat.ToggleVoiceInputAsync();
                Named<Button>(chat, "OllamaConnectionSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(session.State == PetVoiceInputState.Idle && !recorder.IsRecording, "opening_endpoint_settings_cancels_voice_input");
                foreach (var owned in chat.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
                await Until(() => mic.IsEnabled);

                await chat.ToggleVoiceInputAsync(); settings.SkinId = "gemini"; chat.RefreshCharacter();
                await Until(() => session.State == PetVoiceInputState.Idle && mic.IsEnabled);
                Require(!recorder.IsRecording, "changing_character_cancels_voice_input");

                await chat.ToggleVoiceInputAsync(); chat.Hide();
                Require(session.State == PetVoiceInputState.Idle && !recorder.IsRecording && !pauses.Last(), "hiding_chat_stops_recording_immediately");
                chat.Show(); await Until(() => mic.IsEnabled);
                await chat.ToggleVoiceInputAsync();
                typeof(OllamaChatWindow).GetMethod("VoiceInputSessionSwitch", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(chat,
                    new object[] { chat, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock) });
                await Until(() => session.State == PetVoiceInputState.Idle);
                Require(!recorder.IsRecording && !pauses.Last(), "session_lock_handler_releases_microphone");

                recognizer.Pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await chat.ToggleVoiceInputAsync(); var lockPending = chat.ToggleVoiceInputAsync();
                await Until(() => session.State == PetVoiceInputState.Transcribing);
                string beforeLock = input.Text;
                typeof(OllamaChatWindow).GetMethod("VoiceInputSessionSwitch", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(chat,
                    new object[] { chat, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock) });
                // Model an already queued transcription callback winning before
                // the dispatcher can run the system-event cleanup callback.
                typeof(OllamaChatWindow).GetMethod("VoiceInputTranscribed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(chat,
                    new object[] { "锁屏后到达的文字不得写入" });
                Require(input.Text == beforeLock, "lock_epoch_rejects_text_before_ui_cancellation_callback_runs");
                recognizer.Pending.SetResult("锁屏后完成的识别不得写入"); await lockPending; recognizer.Pending = null;
                await Until(() => session.State == PetVoiceInputState.Idle);
                Require(input.Text == beforeLock, "lock_and_completion_race_cannot_append_text");

                await chat.ToggleVoiceInputAsync();
                var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(chat), 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                chat.RaiseEvent(escape);
                Require(session.State == PetVoiceInputState.Idle && chat.IsVisible, "escape_cancels_voice_without_closing_chat");
                await chat.ToggleVoiceInputAsync(); chat.Close();
                Require(!chat.IsVisible && !recorder.IsRecording && !pauses.Last(), "closing_chat_releases_microphone_and_mute_lease");
                chat.Shutdown(); Require(recorder.Disposed && recognizer.Disposed, "shutdown_disposes_recording_and_recognizer_resources");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!condition()) await Task.Delay(15, timeout.Token); }
    private static async Task Settle(Window window)
    { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(30); window.UpdateLayout(); }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(x => x.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private static Rect Bounds(Window window, FrameworkElement element) => element.TransformToAncestor((FrameworkElement)window.Content).TransformBounds(new Rect(element.RenderSize));
    private static bool Fits(Window window, FrameworkElement element)
    { var root = (FrameworkElement)window.Content; var rect = Bounds(window, element); return rect.Left >= 0 && rect.Top >= 0 && rect.Right <= root.ActualWidth + 1 && rect.Bottom <= root.ActualHeight + 1; }
    private static bool Overlaps(Window window, FrameworkElement a, FrameworkElement b) => Bounds(window, a).IntersectsWith(Bounds(window, b));
    private static void Capture(Window window, string path)
    { var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file); }

    private sealed class Recorder : IPetMicrophoneRecorder
    {
        public bool IsRecording { get; private set; }
        public event Action? LimitReached { add { } remove { } }
        internal int Starts; internal bool Disposed;
        public void Start() { Starts++; IsRecording = true; }
        public byte[] Stop() { IsRecording = false; return Wave(); }
        public void Cancel() => IsRecording = false;
        public void Dispose() { Disposed = true; Cancel(); }
        private static byte[] Wave()
        {
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(32000); writer.Write(new byte[32000]); return memory.ToArray();
        }
    }

    private sealed class Recognizer : IPetSpeechRecognizer
    {
        internal const string Text = "请帮我看看今天的任务。";
        internal int Calls; internal bool Disposed;
        internal TaskCompletionSource<string>? Pending;
        public async Task<string> TranscribeAsync(byte[] wave, CancellationToken ct)
        { Calls++; if (Pending != null) return await Pending.Task; await Task.Delay(20, ct); return Text; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Client : IOllamaClient
    {
        public Uri Endpoint { get; init; } = OllamaEndpoint.Parse(OllamaEndpoint.DefaultAddress);
        internal int Chats;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel, OllamaModels.QualityModel });
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(new OllamaModelCapabilities(true, false, false, false));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        { Chats++; await Task.Delay(15, ct); yield return new("收到，正在处理。", true, 25, 10); }
        public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
}
