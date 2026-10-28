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

namespace DesktopPet;

// Isolated fixtures only: never opens the production database or real settings.
internal static class ChatHistoryUiChecks
{
    internal static int Run()
    {
        string evidence = Path.Combine(AppContext.BaseDirectory, "chat-history-ui-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        string database = Path.Combine(evidence, "fixture.db");
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        int result = 1;
        OllamaChatWindow? window = null;
        Window? panel = null;
        string phase = "startup";
        void Require(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        app.Startup += async (_, _) =>
        {
            string? error = null;
            try
            {
                var settings = new DesktopSettings { CodexEnabled = false, WebSearchEnabled = false, SharedMemoryEnabled = true };
                string endpoint = OllamaClient.LocalEndpoint.GetLeftPart(UriPartial.Authority);
                string persona = settings.SkinId.ToLowerInvariant();
                var client = new FixtureClient();
                var store = new ChatMemoryStore(database);
                window = Create(settings, client, store); window.Show();
                await Ready(window);
                string first = window.CurrentConversationId!;
                Require(first.Length > 0, "initial_local_conversation_created");
                var input = Named<TextBox>(window, "OllamaChatInput");
                string imagePath = Path.Combine(evidence, "synthetic.png");
                var pixels = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)240, 256).ToArray(), 32);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(pixels));
                using (var file = File.Create(imagePath)) png.Save(file);
                await window.AddImageFilesAsync(new[] { imagePath });
                input.Text = "图片里有什么？"; await window.SendAsync();
                Require(store.ReadTurns(first).Count == 1 && store.ReadTurns(first)[0].Images.Count == 1, "completed_image_turn_committed");
                input.Text = "第一段会话的草稿";
                await window.NewConversationAsync();
                string second = window.CurrentConversationId!;
                Require(second != first && store.GetConversation(first)!.Draft == "第一段会话的草稿", "new_conversation_preserves_old_draft_and_history");
                input.Text = "请记住：我喜欢青绿色的桌面主题"; int calls = client.Calls;
                await window.SendAsync();
                Require(client.Calls == calls && store.ListMemories(endpoint).Count == 1, "explicit_remember_saved_without_model_request");
                await window.NewConversationAsync();
                input.Text = "我喜欢什么颜色的桌面主题？"; await window.SendAsync();
                Require(client.Messages[0].Content.Contains("青绿色", StringComparison.Ordinal), "memory_retrieved_in_separate_conversation");
                Require(!client.Messages.Any(m => m.Role == "user" && m.Content.StartsWith("请记住", StringComparison.Ordinal)), "shared_memory_does_not_copy_other_conversation_messages");
                settings.SharedMemoryEnabled = false;
                input.Text = "桌面主题呢？"; await window.SendAsync();
                Require(!client.Messages[0].Content.Contains("青绿色", StringComparison.Ordinal), "memory_toggle_excludes_notes_from_system_context");
                settings.SharedMemoryEnabled = true;
                store.DeleteMemory(store.ListMemories(endpoint).Single().Id);
                input.Text = "我的桌面主题是什么颜色？"; await window.SendAsync();
                Require(!client.Messages[0].Content.Contains("青绿色", StringComparison.Ordinal), "deleted_memory_not_injected_next_request");

                phase = "switch_cancel_restore";
                await window.SwitchConversationAsync(first);
                Require(input.Text == "第一段会话的草稿", "switch_restores_text_draft");
                input.Text = "继续分析刚才的图片"; await window.SendAsync();
                Require(client.Messages.Any(m => (m.Images?.Count ?? 0) == 1), "restored_image_available_for_followup");
                client.Block = true; client.Started = false;
                input.Text = "这是等待取消的请求"; Task pending = window.SendAsync();
                await Until(() => client.Started);
                await window.SwitchConversationAsync(second); await pending;
                Require(window.CurrentConversationId == second && store.ReadTurns(first).Count == 2, "switch_cancels_before_committing_partial_reply");
                Require(store.GetConversation(first)!.Draft == "这是等待取消的请求", "cancelled_request_persisted_as_original_conversation_draft");
                client.Block = false;
                input.Text = "重启后仍在的草稿"; await window.AddImageFilesAsync(new[] { imagePath });
                window.Shutdown(); window = null;
                client = new FixtureClient(); store = new ChatMemoryStore(database);
                window = Create(settings, client, store); window.Show(); await Ready(window);
                input = Named<TextBox>(window, "OllamaChatInput");
                Require(window.CurrentConversationId == second && input.Text == "重启后仍在的草稿", "restart_restores_last_selected_conversation_and_draft");
                Require(Named<WrapPanel>(window, "OllamaImagePreviews").Children.Count == 1, "restart_restores_pending_image_preview");

                phase = "paging_and_panels";
                var many = store.CreateConversation(endpoint, persona, settings.OllamaModel);
                for (int i = 0; i < 58; i++) store.AppendTurn(many.Id, Turn("历史问题 " + i, "历史回答 " + i, i == 0));
                await window.SwitchConversationAsync(many.Id);
                Require(MessageCount(window) == 100, "initial_history_page_loads_latest_50_turns");
                window.LoadOlderMessages();
                Require(MessageCount(window) == 116 && Descendants<StackPanel>(window).Any(p => p.Name == "OllamaSources"), "load_older_preserves_messages_and_saved_sources");
                input.Text = "请继续"; await window.SendAsync();
                Require(client.Messages.Count <= 25 && store.ReadTurns(many.Id, 100).Count == 59, "archive_not_trimmed_to_model_context_limit");
                panel = new ChatLibraryWindow(store, endpoint, persona, settings.OllamaModel, many.Id) { ShowActivated = false, Topmost = false };
                panel.Show(); panel.UpdateLayout();
                Named<TextBox>(panel, "ChatLibraryTitle").Text = "图片与记忆测试"; Click(panel, "ChatLibraryRename");
                Require(store.GetConversation(many.Id)!.Title == "图片与记忆测试", "history_panel_renames_persistently");
                Capture(panel, Path.Combine(evidence, "history-panel.png"));
                panel.Width = panel.MinWidth; panel.Height = panel.MinHeight; await Task.Delay(100); panel.UpdateLayout();
                Capture(panel, Path.Combine(evidence, "history-minimum.png"));
                Require(Fits(panel, Named<Button>(panel, "ChatLibraryOpen")), "history_actions_fit_minimum_window");
                Click(panel, "ChatLibraryDelete"); panel.UpdateLayout();
                Require(Fits(panel, Named<Button>(panel, "ChatLibraryOpen")) && Fits(panel, Named<Button>(panel, "ChatLibraryConfirmDelete")), "history_delete_confirmation_fits_minimum_window");
                Capture(panel, Path.Combine(evidence, "history-confirm-minimum.png"));
                Click(panel, "ChatLibraryCancelDelete");
                panel.Close(); panel = null;
                panel = new SharedMemoryWindow(store, endpoint, true, enabled => settings.SharedMemoryEnabled = enabled) { ShowActivated = false, Topmost = false };
                panel.Show();
                Named<TextBox>(panel, "SharedMemoryTitle").Text = "称呼";
                Named<TextBox>(panel, "SharedMemoryContent").Text = "用户的昵称是小海鲸";
                Named<CheckBox>(panel, "SharedMemoryPinned").IsChecked = true; Click(panel, "SharedMemorySave");
                Require(store.ListMemories(endpoint).Single().Pinned, "memory_panel_saves_pinned_record");
                Named<TextBox>(panel, "SharedMemoryContent").Text = "用户的昵称是小蓝鲸"; Click(panel, "SharedMemorySave");
                Require(store.ListMemories(endpoint).Single().Content.Contains("小蓝鲸", StringComparison.Ordinal), "memory_panel_edits_record_without_duplicate");
                Capture(panel, Path.Combine(evidence, "memory-panel.png"));
                panel.Close(); panel = null;
                settings.SkinId = "02-gemini-small"; window.RefreshCharacter();
                await Until(() => window.CurrentConversationId != many.Id);
                input.Text = "我叫什么？"; await window.SendAsync();
                Require(client.Messages[0].Content.Contains("小蓝鲸", StringComparison.Ordinal) && !client.Messages.Any(m => m.Content.StartsWith("历史问题", StringComparison.Ordinal)), "other_character_shares_memory_not_transcript");
                Require(store.ListMemories("http://127.0.0.1:22434").Count == 0, "other_endpoint_has_separate_memory_scope");
                window.Width = 320; window.Height = 360; await Task.Delay(100); window.UpdateLayout();
                Require(Fits(window, Named<TextBox>(window, "OllamaChatInput")) && Fits(window, Named<Button>(window, "OllamaSend")), "history_chat_composer_fits_compact_window");
                Capture(window, Path.Combine(evidence, "compact-chat.png"));
                window.Shutdown(); window = null;

                phase = "save_failure";
                var faulty = new FaultyStore(new ChatMemoryStore(Path.Combine(evidence, "failures.db")));
                client = new FixtureClient(); window = Create(settings, client, faulty); window.Show(); await Ready(window);
                input = Named<TextBox>(window, "OllamaChatInput"); input.Text = "磁盘失败时保留这条草稿";
                faulty.FailDraft = true; string originalId = window.CurrentConversationId!;
                await window.NewConversationAsync();
                Require(window.CurrentConversationId == originalId && input.Text.Contains("保留", StringComparison.Ordinal), "failed_draft_save_blocks_destructive_switch");
                faulty.FailDraft = false; faulty.FailAppend = true;
                await window.SendAsync();
                Require(input.Text.Contains("保留", StringComparison.Ordinal) && Descendants<TextBox>(window).Any(x => x.IsReadOnly && x.Text.Contains("合成模型的回答", StringComparison.Ordinal)), "failed_turn_save_keeps_answer_visible_and_restores_input");
                Require(faulty.ReadTurns(originalId).Count == 0, "failed_turn_not_reported_as_durable_history");
                faulty.FailDelete = true;
                panel = new ChatLibraryWindow(faulty, endpoint, settings.SkinId.ToLowerInvariant(), settings.OllamaModel, originalId) { ShowActivated = false, Topmost = false };
                panel.Show(); Click(panel, "ChatLibraryDelete"); Click(panel, "ChatLibraryConfirmDelete");
                Require(faulty.GetConversation(originalId) != null && faulty.GetActiveConversationId(endpoint, settings.SkinId.ToLowerInvariant()) == originalId, "failed_delete_restores_original_active_conversation");
                panel.Close(); panel = null;
                result = 0;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                panel?.Close(); window?.Shutdown();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chat-history-ui-validation.json"), JsonSerializer.Serialize(new { passed = result == 0, version = AppVersion.Number, phase, error, assertions = checks.Count, checks, evidence, productionDataAccess = false }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(result);
            }
        };
        app.Run(); return result;
    }

    private static OllamaChatWindow Create(DesktopSettings settings, FixtureClient client, IChatMemoryStore store)
        => new(settings, () => settings.SkinId.StartsWith("02", StringComparison.Ordinal) ? "Gemini" : "DeepSeek", _ => { }, _ => { }, () => { }, client, chatStore: store)
        { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
    private static ChatStoredTurn Turn(string question, string answer, bool source = false) => new(0, question, answer, OllamaModels.FastModel, Array.Empty<ChatStoredImage>(),
        source ? new[] { new PetWebSource("测试网页", "https://example.com/test", "") } : Array.Empty<PetWebSource>(), source ? "测试读取提示" : null, DateTimeOffset.UtcNow);
    private static int MessageCount(Window window) => ((StackPanel)Named<ScrollViewer>(window, "OllamaMessages").Content).Children.OfType<Border>().Count();
    private static Task Ready(Window window) => Until(() => Named<ComboBox>(window, "OllamaModelSelector").IsEnabled);
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!condition()) await Task.Delay(25, timeout.Token); }
    private static void Click(Window window, string name) => Named<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(x => x.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private static bool Fits(Window window, FrameworkElement element)
    { var root = (FrameworkElement)window.Content; var rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); return rect.Left >= 0 && rect.Top >= 0 && rect.Right <= root.ActualWidth + 1 && rect.Bottom <= root.ActualHeight + 1; }
    private static void Capture(Window window, string path)
    { window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file); }

    private sealed class FixtureClient : IOllamaClient
    {
        internal int Calls; internal bool Block, Started;
        internal IReadOnlyList<OllamaMessage> Messages = Array.Empty<OllamaMessage>();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel, OllamaModels.QualityModel });
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(new OllamaModelCapabilities(true, true, false, false));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        { Calls++; Messages = messages; Started = true; if (Block) await Task.Delay(Timeout.Infinite, ct); await Task.Delay(5, ct); yield return new("这是合成模型的回答。", true, 10, 100); }
        public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FaultyStore(IChatMemoryStore inner) : IChatMemoryStore
    {
        internal bool FailDraft, FailAppend, FailDelete;
        public IReadOnlyList<ChatConversation> ListConversations(string e, string p) => inner.ListConversations(e, p);
        public ChatConversation CreateConversation(string e, string p, string m) => inner.CreateConversation(e, p, m);
        public ChatConversation? GetConversation(string id) => inner.GetConversation(id);
        public void SaveDraft(string id, string text, IReadOnlyList<ChatStoredImage> images) { if (FailDraft) throw new IOException("synthetic failure"); inner.SaveDraft(id, text, images); }
        public void RenameConversation(string id, string title) => inner.RenameConversation(id, title);
        public void DeleteConversation(string id) { if (FailDelete) throw new IOException("synthetic failure"); inner.DeleteConversation(id); }
        public long AppendTurn(string id, ChatStoredTurn t) { if (FailAppend) throw new IOException("synthetic failure"); return inner.AppendTurn(id, t); }
        public IReadOnlyList<ChatStoredTurn> ReadTurns(string id, int limit = 50, long? beforeSequence = null) => inner.ReadTurns(id, limit, beforeSequence);
        public string? GetActiveConversationId(string e, string p) => inner.GetActiveConversationId(e, p);
        public void SetActiveConversation(string id) => inner.SetActiveConversation(id);
        public IReadOnlyList<ChatSharedMemory> ListMemories(string e) => inner.ListMemories(e);
        public ChatSharedMemory UpsertMemory(ChatSharedMemory m) => inner.UpsertMemory(m);
        public void DeleteMemory(string id) => inner.DeleteMemory(id);
        public void Dispose() => inner.Dispose();
    }
}
