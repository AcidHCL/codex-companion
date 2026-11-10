using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.ScreenUnderstanding;

namespace DesktopPet;

// This removable partial supplies a stateless, resident-only multimodal lane.
// It never calls ordinary ChatAsync (which would resize context), preloads,
// unloads, switches the selected model, or accesses a conversation store.
internal sealed partial class OllamaClient
{
    internal async Task<ScreenModelReadiness> ReadScreenModelReadinessAsync(string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var info = await ReadModelInfoAsync(model, deadline.Token).ConfigureAwait(false);
            if (info.Capabilities.SupportsChat != true || info.Think is string)
                return new(false, false, false, "unsupported_model");
            var resident = (await ListRunningModelsAsync(deadline.Token).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.Name, model, StringComparison.Ordinal));
            if (resident?.ContextLength is not >= 4096 ||
                resident.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(3))
                return new(false, false, false, "model_not_ready");
            bool vision = info.Capabilities.SupportsImages == true && resident.ContextLength >= 8192;
            return new(true, true, vision, vision ? "vision_ready" : "text_ready") { ContextLength = resident.ContextLength.Value };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { return new(false, false, false, "model_unavailable"); }
    }

    internal async Task<string?> AnalyzeScreenResidentAsync(string model, string instruction, string source,
        string? imageBase64, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(instruction) || string.IsNullOrWhiteSpace(source) ||
            imageBase64 != null && !ScreenUnderstandingPlanner.IsBoundedImage(imageBase64)) return null;
        // Recheck immediately before submission. Ollama has no atomic resident-
        // only request flag; external eviction between this read and POST remains
        // a server race, just as for the existing scheduled-summary path.
        var ready = await ReadScreenModelReadinessAsync(model, ct).ConfigureAwait(false);
        if (!ready.Available || imageBase64 != null && !ready.SupportsImages) return null;
        int budget = ScreenUnderstandingPlanner.EvidenceByteBudget(ready.ContextLength, instruction, imageBase64 != null);
        if (budget < 256 || Encoding.UTF8.GetByteCount(source) > budget) return null;
        var messages = new[]
        {
            new OllamaMessage("system", instruction),
            new OllamaMessage("user", source, imageBase64 == null ? null : new[] { imageBase64 })
        };
        using var request = JsonRequest("api/chat", new
        {
            model,
            messages = messages.Select(message => new { role = message.Role, content = message.Content, images = message.Images }).ToArray(),
            stream = false, think = false, format = "json",
            keep_alive = RequestKeepAlive(configuredOnly: true),
            options = new { num_ctx = ready.ContextLength, num_predict = 768, temperature = 0.2 }
        });
        using var activity = OllamaRequestActivity.Begin(Endpoint);
        WorkCompanion.CompanionDiagnostics.Record("model", "model_request");
        using var response = await SendAsync(request, ct, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct, ct).ConfigureAwait(false);
        string body = await ReadBodyAsync(response, 64 * 1024, truncate: false, ct, ct).ConfigureAwait(false);
        using var document = ParseDocument(body);
        var root = document.RootElement;
        ThrowResponseError(root);
        if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True) activity.CompleteResponse();
        if (done.ValueKind != JsonValueKind.True || StringProperty(root, "done_reason") == "length" ||
            !root.TryGetProperty("message", out var reply) || reply.ValueKind != JsonValueKind.Object ||
            StringProperty(reply, "role") != "assistant" ||
            reply.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null &&
                (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 0)) return null;
        return StringProperty(reply, "content"); // Never return separate thinking.
    }
}
