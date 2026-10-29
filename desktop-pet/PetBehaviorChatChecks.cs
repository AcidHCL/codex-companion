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
using System.Windows.Threading;

namespace DesktopPet;

// Isolated synthetic UI/service checks: no real Ollama, account, pet window,
// production database, desktop movement, audio or synthesized user input.
internal static class PetBehaviorChatChecks
{
    internal static int Run()
    {
        var checks = new List<string>();
        var settings = new DesktopSettings { SkinId = "deepseek", LlmBehaviorEnabled = false, WebSearchEnabled = false, SharedMemoryEnabled = false, CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false };
        var client = new FixtureClient();
        var host = new FixtureHost();
        var store = new ChatMemoryStore(":memory:");
        var busy = new List<bool>();
        var bubbles = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OllamaChatWindow? window = null;
        int result = 1, availabilityEvents = 0, contextEvents = 0;
        bool finished = false;
        string phase = "startup";
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        void Require(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; deadline.Stop();
            try { window?.Shutdown(); } catch (Exception ex) { passed = false; error = "cleanup_" + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "pet-behavior-chat-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, phase, error, assertions = checks.Count, checks,
                modelCalls = client.Calls.Count, client.MaximumConcurrentCalls, availabilityEvents, contextEvents,
                executedActions = host.Commands.Select(command => new { command.Action, command.Name, command.Direction, command.Origin }).ToArray(),
                realOllamaRequests = 0, publicWebRequests = 0, productionDatabaseAccess = false,
                realMouseOrKeyboardInput = false, desktopMovement = false, audioPlayback = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        deadline.Tick += (_, _) => Finish(false, "timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            deadline.Start();
            try
            {
                window = new OllamaChatWindow(settings, () => "DeepSeek", value => busy.Add(value), text => bubbles.Add(text), () => { },
                    client: client, webClient: new ForbiddenWeb(), chatStore: store, behaviorHost: host)
                { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
                window.BehaviorAvailabilityChanged += () => availabilityEvents++;
                window.BehaviorContextInvalidated += () => contextEvents++;
                window.Show(); window.UpdateLayout();
                await UntilAsync(() => window.BehaviorServiceReady);
                var input = Named<TextBox>(window, "OllamaChatInput");
                window.Hide();
                Require(window.BehaviorServiceReady && !window.CanReactToInteraction, "capability_ready_is_independent_of_disabled_mode");
                phase = "default_off";
                client.Enqueue(Scenario.Speech("正常回答。"));
                input.Text = "普通问答。"; await window.SendAsync();
                Require(client.Calls.Count == 1 && !client.Calls[0].Tools, "default_off_preserves_single_chat_call");
                Require(host.Commands.Count == 0, "default_off_executes_no_action");
                Require(client.Calls[0].Messages[0].Content.Contains("没有电脑操作", StringComparison.Ordinal), "default_persona_keeps_unavailable_tools_truthful");

                phase = "foreground_receipt";
                settings.LlmBehaviorEnabled = true; window.RefreshBehaviorConfiguration();
                int readyEvents = availabilityEvents;
                client.Enqueue(Scenario.Tools(new OllamaToolCall("walk", "{\"direction\":\"left\",\"seconds\":2}")));
                client.Enqueue(Scenario.Speech("收到，已经开始向左走啦。"));
                input.Text = "请往左走两秒。"; await window.SendAsync();
                Require(client.Calls.Count == 3 && client.Calls[^2].Tools && !client.Calls[^1].Tools, "enabled_foreground_plans_before_final_answer");
                Require(host.Commands.Count == 1 && host.Commands[0].Action == "walk" && host.Commands[0].Origin == PetBehaviorOrigin.Conversation, "conversation_action_reaches_only_behavior_host");
                Require(client.Calls[^1].Messages[0].Content.Contains("fixture-action-accepted", StringComparison.Ordinal), "final_answer_receives_real_host_receipt");
                Require(!client.Calls[^1].Messages[0].Content.Contains("你没有电脑操作、外包给其他模型或播放声音的工具", StringComparison.Ordinal), "enabled_persona_describes_available_pet_tools");
                Require(availabilityEvents == readyEvents && window.BehaviorServiceReady, "foreground_busy_is_not_reported_as_disconnect");

                phase = "background_isolation";
                input.Text = "保留这段未发送草稿。";
                Require(window.SaveDraftForTesting(), "background_fixture_draft_saved");
                int turns = store.ReadTurns(window.CurrentConversationId!).Count;
                int busyBefore = busy.Count, bubblesBefore = bubbles.Count;
                bool focusBefore = input.IsKeyboardFocusWithin;
                Require(!window.IsVisible && window.CanReactToInteraction, "hidden_composer_with_draft_accepts_background_event");
                client.Enqueue(Scenario.Speech("别戳啦，饭碗差点掉了。", tools: true));
                string? reaction = await window.ReactToInteractionAsync(new("clicked", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None);
                Require(reaction == "别戳啦，饭碗差点掉了。", "background_returns_independent_bubble_text");
                Require(input.Text == "保留这段未发送草稿。" && store.ReadTurns(window.CurrentConversationId!).Count == turns, "background_does_not_change_draft_or_archive");
                Require(busy.Count == busyBefore && bubbles.Count == bubblesBefore && input.IsKeyboardFocusWithin == focusBefore, "background_does_not_set_busy_focus_or_show_bubble_itself");
                Require(client.Calls[^1].Messages.All(message => !message.Content.Contains("未发送草稿", StringComparison.Ordinal)), "background_does_not_send_editor_draft");

                phase = "quiet_event";
                client.Enqueue(Scenario.Speech("SILENT", tools: true));
                reaction = await window.ReactToInteractionAsync(new("landed", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None);
                Require(reaction == null && window.BehaviorServiceReady, "normal_silence_keeps_connection_ready");

                phase = "user_priority";
                var blocked = Scenario.Block(cancelAware: true); client.Enqueue(blocked);
                var background = window.ReactToInteractionAsync(new("clicked", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None);
                await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                client.Enqueue(Scenario.Speech("NO_ACTION", tools: true)); client.Enqueue(Scenario.Speech("先回答你的问题。"));
                input.Text = "先回答我这个问题。";
                await window.SendAsync();
                Require(await background == null && blocked.Cancelled, "user_send_cancels_and_awaits_background");
                Require(client.MaximumConcurrentCalls == 1 && !client.MetadataDuringInference, "all_model_calls_remain_serial");
                Require(window.BehaviorServiceReady, "user_cancellation_keeps_connection_ready");

                phase = "scope_guard";
                var late = Scenario.Block(cancelAware: false); client.Enqueue(late);
                background = window.ReactToInteractionAsync(new("clicked", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None);
                await late.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                string? previousId = window.CurrentConversationId;
                var switchConversation = window.NewConversationAsync();
                await Task.Delay(30);
                Require(!switchConversation.IsCompleted, "conversation_switch_waits_for_old_model_call");
                late.Release.TrySetResult(true);
                await switchConversation;
                Require(await background == null && previousId != window.CurrentConversationId, "late_background_cannot_enter_new_conversation");
                Require(store.ReadTurns(window.CurrentConversationId!).Count == 0 && input.Text.Length == 0, "new_conversation_has_no_fabricated_event_turn");

                phase = "metadata_reconnect";
                client.SupportsTools = false; await window.CheckConnectionAsync();
                int count = client.Calls.Count;
                Require(!window.BehaviorServiceReady && window.BehaviorReactionReady && window.CanReactToInteraction,
                    "text_model_can_react_without_autonomous_action_capability");
                Require(window.BehaviorCommandsReady, "text_model_still_accepts_explicit_host_commands");
                int commandCount = host.Commands.Count;
                int textOnlyTurns = store.ReadTurns(window.CurrentConversationId!).Count;
                input.Text = "文字模型的未发送草稿。";
                client.Enqueue(Scenario.Speech("知道啦，别一直戳本鲸。"));
                reaction = await window.ReactToInteractionAsync(new("clicked", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None);
                Require(reaction == "知道啦，别一直戳本鲸。" && client.Calls.Count == count + 1 && !client.Calls[^1].Tools,
                    "text_model_receives_passive_event_through_one_plain_chat_call");
                Require(host.Commands.Count == commandCount && client.Calls[^1].Messages.Any(message => message.Content.Contains("本轮没有动作工具", StringComparison.Ordinal)),
                    "text_only_reaction_cannot_claim_or_execute_animation_tools");
                Require(input.Text == "文字模型的未发送草稿。" && store.ReadTurns(window.CurrentConversationId!).Count == textOnlyTurns &&
                    client.Calls[^1].Messages.All(message => !message.Content.Contains("未发送草稿", StringComparison.Ordinal)),
                    "text_only_reaction_preserves_draft_history_and_background_isolation");
                count = client.Calls.Count;
                client.Enqueue(Scenario.Speech("已经开始唱歌啦。"));
                input.Text = "请唱首歌给我听。"; await window.SendAsync();
                Require(client.Calls.Count == count + 1 && !client.Calls[^1].Tools && host.Commands.Count == commandCount + 1 && host.Commands[^1].Name == "sing",
                    "explicit_song_on_text_model_skips_unreliable_planner_and_executes_once");
                Require(client.Calls[^1].Messages[0].Content.Contains("fixture-action-accepted", StringComparison.Ordinal) &&
                    client.Calls[^1].Messages[0].Content.Contains("当前模型没有自主动作工具", StringComparison.Ordinal), "text_model_song_uses_real_receipt_and_limited_capability_prompt");
                settings.LlmBehaviorEnabled = false; window.RefreshBehaviorConfiguration();
                Require(window.BehaviorReactionReady && !window.CanReactToInteraction &&
                    await window.ReactToInteractionAsync(new("clicked", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None) == null && client.Calls.Count == count + 1,
                    "text_only_passive_reactions_still_require_explicit_behavior_mode");
                client.Enqueue(Scenario.Speech("动作控制暂未开启。"));
                input.Text = "唱歌"; await window.SendAsync();
                Require(host.Commands.Count == commandCount + 1, "explicit_song_fast_path_respects_disabled_llm_mode");
                settings.LlmBehaviorEnabled = true; window.RefreshBehaviorConfiguration();
                client.SupportsTools = true; await window.CheckConnectionAsync();
                Require(window.BehaviorServiceReady, "metadata_refresh_restores_behavior_readiness");
                client.Enqueue(Scenario.Fail());
                Require(await window.ReactToInteractionAsync(new("clicked", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None) == null && !window.BehaviorServiceReady, "real_background_failure_marks_connection_unavailable");
                await window.CheckConnectionAsync(); Require(window.BehaviorServiceReady, "failure_recovers_via_metadata_without_idle_inference");

                phase = "mode_guard";
                var disabled = Scenario.Block(cancelAware: false); client.Enqueue(disabled);
                background = window.ReactToInteractionAsync(new("clicked", DateTimeOffset.UtcNow, host.Revision), CancellationToken.None);
                await disabled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                int beforeContext = contextEvents;
                settings.LlmBehaviorEnabled = false; window.RefreshBehaviorConfiguration();
                disabled.Release.TrySetResult(true);
                Require(await background == null && contextEvents > beforeContext && !window.CanReactToInteraction, "disabling_mode_invalidates_late_event_output");
                int afterContext = contextEvents;
                window.CancelBehaviorReaction();
                Require(contextEvents == afterContext, "plain_reaction_cancel_does_not_reinvalidate_host");
                Require(client.PendingCount == 0 && client.MaximumConcurrentCalls == 1, "all_fixture_scenarios_consumed_without_parallel_inference");
                phase = "complete"; Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run(); return result;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(element => element.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed class ForbiddenWeb : IPetWebClient
    {
        public Task<PetWebResult> SearchAsync(string query, CancellationToken ct) => throw new InvalidOperationException("Unexpected public web call.");
        public Task<PetWebResult> FetchAsync(string url, CancellationToken ct) => throw new InvalidOperationException("Unexpected public web call.");
        public void Dispose() { }
    }
    private sealed class FixtureHost : IPetBehaviorHost
    {
        internal long Revision = 7;
        internal List<PetBehaviorCommand> Commands { get; } = new();
        public Task<PetBehaviorSnapshot> GetSnapshotAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new PetBehaviorSnapshot("deepseek", "idle", Revision, true, true, true, new[] { "sing" })); }
        public Task<PetBehaviorResult> ExecuteAsync(PetBehaviorCommand command, long expectedRevision, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Commands.Add(command); return Task.FromResult(new PetBehaviorResult(expectedRevision == Revision, "fixture-action-accepted", command.Action)); }
    }
    private sealed class Scenario
    {
        internal bool ToolsExpected, IsBlock, CancelAware, Throw, Cancelled;
        internal string Text = "";
        internal OllamaToolCall[] ToolCalls = Array.Empty<OllamaToolCall>();
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static Scenario Speech(string text, bool tools = false) => new() { Text = text, ToolsExpected = tools };
        internal static Scenario Tools(params OllamaToolCall[] calls) => new() { ToolsExpected = true, ToolCalls = calls };
        internal static Scenario Block(bool cancelAware) => new() { ToolsExpected = true, IsBlock = true, CancelAware = cancelAware, Text = "迟到的背景回应。" };
        internal static Scenario Fail() => new() { ToolsExpected = true, Throw = true };
    }
    private sealed record CapturedCall(bool Tools, IReadOnlyList<OllamaMessage> Messages);
    private sealed class FixtureClient : IOllamaClient
    {
        private readonly Queue<Scenario> plans = new();
        private int active;
        internal int MaximumConcurrentCalls;
        internal bool SupportsTools = true, MetadataDuringInference;
        internal List<CapturedCall> Calls { get; } = new();
        internal int PendingCount { get { lock (plans) return plans.Count; } }
        internal void Enqueue(Scenario scenario) { lock (plans) plans.Enqueue(scenario); }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        { MetadataDuringInference |= Volatile.Read(ref active) != 0; ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel, OllamaModels.QualityModel }); }
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct)
        { MetadataDuringInference |= Volatile.Read(ref active) != 0; ct.ThrowIfCancellationRequested(); return Task.FromResult(new OllamaModelCapabilities(true, true, false, SupportsTools)); }
        public IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, CancellationToken ct) => Run(messages, false, ct);
        public IAsyncEnumerable<OllamaChatDelta> ChatWithToolsAsync(string model, IReadOnlyList<OllamaMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct) => Run(messages, true, ct);
        private async IAsyncEnumerable<OllamaChatDelta> Run(IReadOnlyList<OllamaMessage> messages, bool tools, [EnumeratorCancellation] CancellationToken ct)
        {
            Scenario plan;
            lock (plans) { if (plans.Count == 0) throw new InvalidOperationException("Unexpected model request."); plan = plans.Dequeue(); }
            if (plan.ToolsExpected != tools) throw new InvalidOperationException("Unexpected model request kind.");
            Calls.Add(new(tools, messages.ToArray()));
            int concurrent = Interlocked.Increment(ref active); MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, concurrent);
            try
            {
                plan.Started.TrySetResult(true);
                if (plan.Throw) throw new InvalidOperationException("synthetic transport failure");
                if (plan.IsBlock)
                {
                    if (plan.CancelAware)
                    {
                        try { await Task.Delay(Timeout.Infinite, ct); }
                        catch (OperationCanceledException) { plan.Cancelled = true; throw; }
                    }
                    else await plan.Release.Task;
                }
                else await Task.Yield();
                yield return new OllamaChatDelta(plan.Text, true, 3, 25, plan.ToolCalls.Length == 0 ? null : plan.ToolCalls);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task UnloadAsync(string model, CancellationToken ct)
        { MetadataDuringInference |= Volatile.Read(ref active) != 0; ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
