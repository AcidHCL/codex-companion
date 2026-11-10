using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.ScreenUnderstanding;

// This feature-local adapter keeps the active provider intact. The Ollama path
// remains resident-only; API preflight reads configuration, not /models or /ps.
internal static class ScreenModelConnection
{
    internal static async Task<ScreenModelReadiness> ReadinessAsync(IOllamaClient client, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (client is OllamaClient local) return await local.ReadScreenModelReadinessAsync(model, ct).ConfigureAwait(false);
        if (client is not OpenAiCompatibleClient api || !ChatApiModel.IsValid(model)) return new(false, false, false, "unsupported_model");
        if (!api.CanAttemptBackgroundRequest) return new(false, false, false, "model_unavailable");
        try
        {
            var capabilities = await client.GetModelInfoAsync(model, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            bool chat = capabilities.SupportsChat == true;
            bool images = chat && capabilities.SupportsImages == true;
            // A conservative request budget, not a claim about remote residency.
            return new(chat, chat, images, chat ? images ? "api_vision_ready" : "api_text_ready" : "unsupported_model")
            { ContextLength = 8192 };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { return new(false, false, false, "model_unavailable"); }
    }

    internal static async Task<string?> AnalyzeAsync(IOllamaClient client, string model, string instruction,
        string evidence, string? imageBase64, CancellationToken ct)
    {
        if (client is OllamaClient local)
            return await local.AnalyzeScreenResidentAsync(model, instruction, evidence, imageBase64, ct).ConfigureAwait(false);
        if (client is not OpenAiCompatibleClient api || string.IsNullOrWhiteSpace(instruction) ||
            string.IsNullOrWhiteSpace(evidence) || imageBase64 != null && !ScreenUnderstandingPlanner.IsBoundedImage(imageBase64)) return null;
        var ready = await ReadinessAsync(client, model, ct).ConfigureAwait(false);
        if (!ready.Available || imageBase64 != null && !ready.SupportsImages) return null;
        int budget = ScreenUnderstandingPlanner.EvidenceByteBudget(ready.ContextLength, instruction, imageBase64 != null);
        if (budget < 256 || Encoding.UTF8.GetByteCount(evidence) > budget) return null;
        return await BackgroundModelRequests.GenerateApiAsync(api, model, instruction, evidence, imageBase64, ct).ConfigureAwait(false);
    }
}
