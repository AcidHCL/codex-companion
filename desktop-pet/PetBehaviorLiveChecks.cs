using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Only synthetic prompts and a synthetic executor are used. This never creates
// PetWindow, reads settings/history, moves the desktop pet, or starts audio.
internal static class PetBehaviorLiveChecks
{
    public static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        var modelReports = new List<object>();
        var failures = new List<string>();
        var elapsed = Stopwatch.StartNew();
        using var client = new RecordingClient(new OllamaClient());
        foreach (string model in new[] { OllamaModels.FastModel, OllamaModels.QualityModel })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            var modelElapsed = Stopwatch.StartNew();
            string phase = "metadata";
            string? failure = null;
            var outcomes = new List<object>();
            var commands = new List<PetBehaviorCommand>();
            void Require(bool condition, string label)
            {
                if (!condition) throw new InvalidOperationException(label);
                checks.Add(model + "_" + label);
            }
            try
            {
                var installed = await client.ListModelsAsync(deadline.Token).ConfigureAwait(false);
                Require(installed.Contains(model, StringComparer.Ordinal), "installed_locally");
                var capabilities = await client.GetModelInfoAsync(model, deadline.Token).ConfigureAwait(false);
                Require(capabilities.SupportsChat == true && capabilities.SupportsTools == true, "native_chat_and_tools_available");

                phase = "sing"; client.Phase = phase;
                var singingHost = new SyntheticHost();
                var song = await PetBehaviorPlanner.PlanConversationAsync(client, model,
                    new[] { new OllamaMessage("user", "请现在唱 Let Me Go 给我听。") },
                    singingHost, capabilities.SupportsTools, deadline.Token).ConfigureAwait(false);
                outcomes.Add(new { phase, song.Instruction, song.Results, commands = singingHost.SnapshotCommands() });
                commands.AddRange(singingHost.SnapshotCommands());
                Require(singingHost.SnapshotCommands().Any(command => command.Action == "play_performance" && command.Name == "sing"), "explicit_song_produces_registered_performance_call");
                Require(song.Results.Any(result => result.Action == "play_performance" && result.Accepted) && song.Instruction.Contains("合成唱歌已启动", StringComparison.Ordinal), "song_receipt_contains_actual_synthetic_host_result");
                Require(song.Instruction.Length <= PetBehaviorPlanner.MaxReceiptCharacters, "song_receipt_is_bounded");

                phase = "walk_left"; client.Phase = phase;
                var walkingHost = new SyntheticHost();
                var walking = await PetBehaviorPlanner.PlanConversationAsync(client, model,
                    new[] { new OllamaMessage("user", "请现在向左走3秒。") },
                    walkingHost, capabilities.SupportsTools, deadline.Token).ConfigureAwait(false);
                outcomes.Add(new { phase, walking.Instruction, walking.Results, commands = walkingHost.SnapshotCommands() });
                commands.AddRange(walkingHost.SnapshotCommands());
                Require(walkingHost.SnapshotCommands().Any(command => command.Action == "walk" && command.Direction == "left" && Math.Abs(command.Seconds - 3) < 0.001), "explicit_walk_produces_left_three_second_command");
                Require(walking.Results.Any(result => result.Action == "walk" && result.Accepted) && walking.Instruction.Contains("合成左行3秒已启动", StringComparison.Ordinal), "walk_receipt_contains_actual_synthetic_host_result");

                phase = "clicked"; client.Phase = phase;
                var interactionHost = new SyntheticHost();
                var interaction = new PetInteractionEvent("clicked", DateTimeOffset.UtcNow, SyntheticHost.Revision);
                var persona = PetPersona.ForSkin("deepseek", "DeepSeek", model, supportsImages: true, webEnabled: false);
                string? reaction = await PetBehaviorPlanner.ReactAsync(client, model, persona, interaction,
                    interactionHost, capabilities.SupportsTools, deadline.Token).ConfigureAwait(false);
                outcomes.Add(new { phase, reaction, silent = reaction == string.Empty, commands = interactionHost.SnapshotCommands() });
                commands.AddRange(interactionHost.SnapshotCommands());
                Require(reaction != null, "clicked_reaction_completes_without_connection_or_protocol_failure");
                Require(reaction!.Length <= 160, "clicked_reaction_is_short_or_intentionally_silent");
                Require(interactionHost.SnapshotCommands().All(command => command.Origin == PetBehaviorOrigin.PassiveReaction && (command.Action is "play_performance" or "stop_performance")), "clicked_reaction_never_controls_locomotion_or_physics");
                Require(commands.All(command => command.Action is "play_performance" or "stop_performance" or "walk" or "stop_walk"), "all_commands_stay_inside_synthetic_pet_capabilities");
                phase = "complete";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The message is one of our synthetic assertions or a generic
                // client error, not server payloads or real user input.
                failure = ex is OperationCanceledException ? "model_deadline_exceeded" : ex.GetType().Name + ": " + ex.Message;
                failures.Add(model + "/" + phase + ": " + failure);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await client.UnloadAsync(model, cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failures.Add(model + "/cleanup: " + ex.GetType().Name); }
            }
            modelReports.Add(new { model, passed = failure == null, phase, error = failure, elapsedSeconds = modelElapsed.Elapsed.TotalSeconds, outcomes });
        }
        var runs = client.SnapshotRuns();
        return new
        {
            passed = failures.Count == 0, assertions = checks.Count, checks, models = modelReports, failures,
            elapsedSeconds = elapsed.Elapsed.TotalSeconds, runs, localServiceOnly = true,
            productionSettingsAccess = false, productionDatabaseAccess = false, actualPetMovement = false,
            actualAudioPlayback = false, publicWebRequests = 0, syntheticMouseOrKeyboardInput = false
        };
    }

    private sealed class SyntheticHost : IPetBehaviorHost
    {
        internal const long Revision = 17;
        private readonly List<PetBehaviorCommand> commands = new();
        internal PetBehaviorCommand[] SnapshotCommands() { lock (commands) return commands.ToArray(); }
        public Task<PetBehaviorSnapshot> GetSnapshotAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PetBehaviorSnapshot("deepseek", "idle", Revision, true, true, true, new[] { "sing" }));
        }
        public Task<PetBehaviorResult> ExecuteAsync(PetBehaviorCommand command, long expectedRevision, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (expectedRevision != Revision) return Task.FromResult(new PetBehaviorResult(false, "合成状态版本已过期", command.Action));
            lock (commands) commands.Add(command);
            bool accepted = command.Action switch
            {
                "play_performance" => command.Name == "sing",
                "stop_performance" or "stop_walk" => true,
                "walk" => command.Origin == PetBehaviorOrigin.Conversation && (command.Direction is "left" or "right") && command.Seconds is >= 1 and <= 8,
                _ => false
            };
            string message = !accepted ? "合成宿主拒绝不支持的动作" : command.Action switch
            {
                "play_performance" => "合成唱歌已启动",
                "walk" when command.Direction == "left" && Math.Abs(command.Seconds - 3) < 0.001 => "合成左行3秒已启动",
                "walk" => "合成行走已启动",
                _ => "合成停止指令已接受"
            };
            return Task.FromResult(new PetBehaviorResult(accepted, message, command.Action));
        }
    }

    private sealed class RecordedRun
    {
        public required string Model { get; init; }
        public required string Phase { get; init; }
        public required string Mode { get; init; }
        public string[] Tools { get; init; } = Array.Empty<string>();
        public List<OllamaToolCall> Calls { get; } = new();
        public string Answer { get; set; } = "";
        public bool Completed { get; set; }
        public double ElapsedSeconds { get; set; }
        public double? TokensPerSecond { get; set; }
    }

    private sealed class RecordingClient(IOllamaClient inner) : IOllamaClient
    {
        internal string Phase = "metadata";
        private readonly List<RecordedRun> runs = new();
        internal RecordedRun[] SnapshotRuns() { lock (runs) return runs.ToArray(); }
        public Uri Endpoint => inner.Endpoint;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => inner.ListModelsAsync(ct);
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => inner.GetModelInfoAsync(model, ct);
        public Task UnloadAsync(string model, CancellationToken ct) => inner.UnloadAsync(model, ct);
        public IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, CancellationToken ct) =>
            RecordAsync(model, Array.Empty<OllamaToolDefinition>(), inner.ChatAsync(model, messages, ct), ct);
        public IAsyncEnumerable<OllamaChatDelta> ChatWithToolsAsync(string model, IReadOnlyList<OllamaMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct) =>
            RecordAsync(model, tools, inner.ChatWithToolsAsync(model, messages, tools, ct), ct);
        private async IAsyncEnumerable<OllamaChatDelta> RecordAsync(string model, IReadOnlyList<OllamaToolDefinition> tools,
            IAsyncEnumerable<OllamaChatDelta> stream, [EnumeratorCancellation] CancellationToken ct)
        {
            var run = new RecordedRun { Model = model, Phase = Phase, Mode = tools.Count > 0 ? "tools" : "speech", Tools = tools.Select(tool => tool.Name).ToArray() };
            lock (runs) runs.Add(run);
            var timer = Stopwatch.StartNew(); var answer = new StringBuilder();
            try
            {
                await foreach (var delta in stream.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (answer.Length < 5000) answer.Append(delta.Content[..Math.Min(delta.Content.Length, 5000 - answer.Length)]);
                    if (delta.ToolCalls != null) run.Calls.AddRange(delta.ToolCalls.Take(Math.Max(0, 4 - run.Calls.Count)));
                    if (delta.Done) { run.Completed = true; run.TokensPerSecond = delta.TokensPerSecond; }
                    yield return delta;
                }
            }
            finally { run.Answer = answer.ToString(); run.ElapsedSeconds = timer.Elapsed.TotalSeconds; }
        }
        public void Dispose() => inner.Dispose();
    }
}
