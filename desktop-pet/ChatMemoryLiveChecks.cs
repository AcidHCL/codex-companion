using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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

// Runs only on generated fixture data in the executable's workspace evidence
// directory. No settings/account files, production DB, public network, or input
// automation are used. Both inference requests go to loopback Ollama.
internal static class ChatMemoryLiveChecks
{
    internal static int Run()
    {
        string evidence = Path.Combine(AppContext.BaseDirectory, "chat-memory-live-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        string databasePath = Path.Combine(evidence, "synthetic-chat-memory.db");
        const string nickname = "海盐芝士";
        const string finalDraft = "下次继续测试本地记忆。";
        var checks = new List<string>();
        var captures = new List<string>();
        var clients = new List<RecordingClient>();
        var forbiddenWeb = new NoWebClient();
        var unloaded = new List<string>();
        var cleanupErrors = new List<string>();
        var metadata = new List<object>();
        object? serverVersion = null;
        int settingsCallbacks = 0, result = 1;
        bool finished = false;
        string phase = "startup";
        string? expectedActive = null;
        var settings = new DesktopSettings
        {
            SkinId = "deepseek", OllamaAddress = OllamaEndpoint.DefaultAddress, OllamaModel = OllamaModels.FastModel,
            SharedMemoryEnabled = true, WebSearchEnabled = false, CodexEnabled = false,
            CodexMessagesEnabled = false, NotificationsEnabled = false
        };
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OllamaChatWindow? window = null;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(180) };
        var elapsed = Stopwatch.StartNew();

        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }

        async Task FinishAsync(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; deadline.Stop(); overall.Cancel();
            try { window?.Shutdown(); }
            catch (Exception ex) { passed = false; cleanupErrors.Add("window_" + ex.GetType().Name); }
            var usedModels = clients.SelectMany(client => client.Snapshot()).Select(run => run.Model).Distinct(StringComparer.Ordinal).ToArray();
            using (var cleanupClient = new OllamaClient())
            {
                foreach (string model in usedModels)
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await cleanupClient.UnloadAsync(model, cleanupTimeout.Token); unloaded.Add(model); }
                    catch (Exception ex) { passed = false; cleanupErrors.Add(model + ":" + ex.GetType().Name); }
                }
            }
            foreach (var client in clients) client.Dispose();
            var calls = clients.SelectMany(client => client.Snapshot()).Select(run => new
            {
                run.Model, run.Answer, run.Completed, run.Error, run.ElapsedSeconds, run.TokensPerSecond,
                messageCount = run.Messages.Count,
                sharedMemoryOnlyInSystem = run.Messages.Where(message => message.Role != "system").All(message => !message.Content.Contains(nickname, StringComparison.Ordinal)),
                memoryPresent = run.Messages.Any(message => message.Role == "system" && message.Content.Contains("<shared_memory_data>", StringComparison.Ordinal) && message.Content.Contains(nickname, StringComparison.Ordinal))
            }).ToArray();
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chat-memory-live-validation.json"), JsonSerializer.Serialize(new
                {
                    passed, version = AppVersion.Number, phase, error, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    assertions = checks.Count, checks, evidence, databasePath, captures, serverVersion, metadata,
                    calls, unloadedModels = unloaded, cleanupErrors, restoredConversationId = expectedActive,
                    localServiceOnly = true, publicWebRequests = forbiddenWeb.Calls, userSettingsWrites = 0,
                    productionDatabaseAccess = false, codexAccountRequests = 0, syntheticMouseOrKeyboardInput = false,
                    syntheticSettingsSaveCallbacks = settingsCallbacks
                }, new JsonSerializerOptions { WriteIndented = true }));
                result = passed ? 0 : 1;
            }
            finally { app.Shutdown(result); }
        }

        deadline.Tick += async (_, _) => await FinishAsync(false, "test_timeout");
        app.DispatcherUnhandledException += async (_, e) =>
        {
            e.Handled = true;
            await FinishAsync(false, e.Exception.GetType().Name + ": " + e.Exception.Message);
        };
        app.Startup += async (_, _) =>
        {
            deadline.Start();
            bool passed = false;
            string? failure = null;
            try
            {
                phase = "service_metadata";
                using (var http = new HttpClient(OllamaClient.CreateLocalHandler()) { BaseAddress = OllamaClient.LocalEndpoint, Timeout = TimeSpan.FromSeconds(10) })
                {
                    using var versionDocument = JsonDocument.Parse(await http.GetStringAsync("api/version", overall.Token));
                    serverVersion = versionDocument.RootElement.Clone();
                    using var tagsDocument = JsonDocument.Parse(await http.GetStringAsync("api/tags", overall.Token));
                    foreach (var choice in OllamaModels.Supported)
                    {
                        JsonElement tag = tagsDocument.RootElement.GetProperty("models").EnumerateArray()
                            .FirstOrDefault(item => item.TryGetProperty("name", out var name) && name.GetString() == choice.Id);
                        Require(tag.ValueKind == JsonValueKind.Object, choice.Id + "_installed_locally");
                        metadata.Add(new { model = choice.Id, installedModelMetadata = tag.Clone() });
                    }
                }

                var recording = new RecordingClient(new OllamaClient()); clients.Add(recording);
                var store = new ChatMemoryStore(databasePath);
                window = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => settingsCallbacks++,
                    client: recording, webClient: forbiddenWeb, chatStore: store)
                { ShowActivated = false, ShowInTaskbar = false, Topmost = false, Left = 32, Top = 32 };
                window.Show(); window.UpdateLayout();
                await UntilAsync(() => Named<ComboBox>(window, "OllamaModelSelector").IsEnabled, overall.Token, "initial_connection", 35);
                var input = Named<TextBox>(window, "OllamaChatInput");
                var models = Named<ComboBox>(window, "OllamaModelSelector");
                Require(window.CurrentConversationId != null, "synthetic_conversation_created");

                phase = "explicit_memory_command";
                input.Text = "记住：我的测试昵称是" + nickname + "。";
                string originalConversation = window.CurrentConversationId!;
                await window.SendAsync();
                Require(recording.Snapshot().Length == 0, "explicit_memory_saved_without_model_call");
                Require(store.ListMemories(OllamaEndpoint.DefaultAddress).Single().Content.Contains(nickname, StringComparison.Ordinal), "explicit_memory_saved_in_fixture_database");
                Require(store.ReadTurns(originalConversation).Count == 1, "local_memory_acknowledgement_archived");

                foreach (var choice in OllamaModels.Supported)
                {
                    phase = "fresh_conversation_" + choice.Id;
                    // Create the next conversation before switching models so neither
                    // model can read an earlier answer containing the test nickname.
                    string previousConversation = window.CurrentConversationId!;
                    await window.NewConversationAsync();
                    Require(window.CurrentConversationId != previousConversation, choice.Id + "_uses_fresh_conversation");
                    Require(store.ReadTurns(window.CurrentConversationId!).Count == 0, choice.Id + "_fresh_archive_has_no_answer");
                    if (settings.OllamaModel != choice.Id)
                    {
                        models.SelectedValue = choice.Id;
                        await UntilAsync(() => models.IsEnabled && settings.OllamaModel == choice.Id, overall.Token, "switch_model", 35);
                    }
                    input.Text = "我保存的测试昵称是什么？只回答昵称。";
                    int before = recording.Snapshot().Length;
                    phase = "live_recall_" + choice.Id;
                    await window.SendAsync().WaitAsync(overall.Token);
                    var runs = recording.Snapshot();
                    Require(runs.Length == before + 1, choice.Id + "_one_inference_request");
                    var run = runs[^1];
                    Require(run.Completed && run.Error == null, choice.Id + "_reply_completed");
                    Require(run.Answer.Contains(nickname, StringComparison.Ordinal), choice.Id + "_recalls_shared_nickname");
                    Require(run.Messages.Count == 2 && run.Messages[0].Role == "system" && run.Messages[1].Role == "user", choice.Id + "_no_old_conversation_sent");
                    Require(run.Messages[0].Content.Contains("<shared_memory_data>", StringComparison.Ordinal) && run.Messages[0].Content.Contains(nickname, StringComparison.Ordinal), choice.Id + "_retrieved_note_added_as_bounded_data");
                    Require(!run.Messages[1].Content.Contains(nickname, StringComparison.Ordinal), choice.Id + "_question_does_not_supply_answer");
                    Require(store.ReadTurns(window.CurrentConversationId!).Single().Assistant.Contains(nickname, StringComparison.Ordinal), choice.Id + "_real_answer_archived");
                    string capture = choice.Id == OllamaModels.FastModel ? "9b-shared-memory.png" : "27b-shared-memory.png";
                    Capture(window, Path.Combine(evidence, capture)); captures.Add(capture);
                }

                phase = "shutdown_and_restore";
                expectedActive = window.CurrentConversationId;
                input.Text = finalDraft;
                Require(window.SaveDraftForTesting(), "last_draft_flushed");
                window.Shutdown(); window = null;
                settings.OllamaModel = OllamaModels.FastModel;
                var restoredStore = new ChatMemoryStore(databasePath);
                Require(restoredStore.GetActiveConversationId(OllamaEndpoint.DefaultAddress, "deepseek") == expectedActive, "active_conversation_survives_closed_database");
                Require(restoredStore.ListConversations(OllamaEndpoint.DefaultAddress, "deepseek").Count == 3, "all_three_conversations_survive_restart");
                Require(restoredStore.ListMemories(OllamaEndpoint.DefaultAddress).Single().Content.Contains(nickname, StringComparison.Ordinal), "shared_memory_survives_restart");
                var reopenedClient = new RecordingClient(new OllamaClient()); clients.Add(reopenedClient);
                window = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => settingsCallbacks++,
                    client: reopenedClient, webClient: forbiddenWeb, chatStore: restoredStore)
                { ShowActivated = false, ShowInTaskbar = false, Topmost = false, Left = 32, Top = 32 };
                window.Show(); window.UpdateLayout();
                await UntilAsync(() => Named<ComboBox>(window, "OllamaModelSelector").IsEnabled, overall.Token, "reopened_metadata_connection", 35);
                Require(window.CurrentConversationId == expectedActive, "reopened_chat_restores_selected_conversation");
                Require(Named<TextBox>(window, "OllamaChatInput").Text == finalDraft, "reopened_chat_restores_unsent_draft");
                Require(Descendants<TextBox>(Named<ScrollViewer>(window, "OllamaMessages")).Any(text => text.Text.Contains(nickname, StringComparison.Ordinal)), "reopened_chat_renders_archived_real_answer");
                Require(reopenedClient.Snapshot().Length == 0, "reopening_and_restoring_issues_no_inference");
                Require(forbiddenWeb.Calls == 0, "web_disabled_and_no_public_requests");
                Require(clients.SelectMany(client => client.Snapshot()).Count() == 2, "exactly_two_real_model_inferences");
                Capture(window, Path.Combine(evidence, "reopened-draft-and-answer.png")); captures.Add("reopened-draft-and-answer.png");
                phase = "complete"; passed = true;
            }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
            finally { await FinishAsync(passed, failure); }
        };
        app.Run();
        return result;
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken ct, string label, int seconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(label);
            await Task.Delay(25, ct);
        }
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => Descendants<T>(root).Single(element => element.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private sealed class NoWebClient : IPetWebClient
    {
        internal int Calls;
        public Task<PetWebResult> SearchAsync(string query, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Public web is forbidden in this local memory test."); }
        public Task<PetWebResult> FetchAsync(string url, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Public web is forbidden in this local memory test."); }
        public void Dispose() { }
    }

    private sealed class ChatRun
    {
        internal string Model = "";
        internal IReadOnlyList<OllamaMessage> Messages = Array.Empty<OllamaMessage>();
        internal string Answer = "";
        internal bool Completed;
        internal string? Error;
        internal double ElapsedSeconds;
        internal double? TokensPerSecond;
    }

    private sealed class RecordingClient : IOllamaClient
    {
        private readonly IOllamaClient inner;
        private readonly List<ChatRun> runs = new();
        private bool disposed;
        internal RecordingClient(IOllamaClient inner) => this.inner = inner;
        public Uri Endpoint => inner.Endpoint;
        internal ChatRun[] Snapshot() { lock (runs) return runs.ToArray(); }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => inner.ListModelsAsync(ct);
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => inner.GetModelInfoAsync(model, ct);
        public Task UnloadAsync(string model, CancellationToken ct) => inner.UnloadAsync(model, ct);
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            var run = new ChatRun { Model = model, Messages = messages.ToArray() };
            lock (runs) runs.Add(run);
            var elapsed = Stopwatch.StartNew();
            await using var enumerator = inner.ChatAsync(model, messages, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool next;
                    try { next = await enumerator.MoveNextAsync(); }
                    catch (Exception ex) { run.Error = ex.GetType().Name; throw; }
                    if (!next) break;
                    var delta = enumerator.Current;
                    run.Answer += delta.Content;
                    if (delta.Done) { run.Completed = true; run.TokensPerSecond = delta.TokensPerSecond; }
                    yield return delta;
                }
            }
            finally { run.ElapsedSeconds = elapsed.Elapsed.TotalSeconds; }
        }
        public void Dispose() { if (disposed) return; disposed = true; inner.Dispose(); }
    }
}
