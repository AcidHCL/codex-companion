using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Provider dispatch only: the caller owns priorities, consent, expiry and the
// client's lifetime. This lane never reads/writes chat, memory or speech state.
internal static class BackgroundModelRequests
{
    internal static Task<string?> GenerateTextAsync(IOllamaClient client, string model,
        string instruction, string source, CancellationToken ct) => client switch
    {
        OllamaClient ollama => ollama.SummarizeResidentAsync(model, instruction, source, ct),
        OpenAiCompatibleClient api => GenerateApiAsync(api, model, instruction, source, null, ct),
        _ => Task.FromResult<string?>(null)
    };

    internal static async Task<string?> GenerateApiAsync(OpenAiCompatibleClient client, string model,
        string instruction, string source, string? imageBase64, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!client.CanAttemptBackgroundRequest || !ChatApiModel.IsValid(model) ||
            string.IsNullOrWhiteSpace(instruction) || string.IsNullOrWhiteSpace(source) ||
            Encoding.UTF8.GetByteCount(instruction) > 24_000 || imageBase64?.Length > 1_400_000) return null;
        var capabilities = await client.GetModelInfoAsync(model, ct).ConfigureAwait(false);
        if (capabilities.SupportsChat != true || imageBase64 != null && capabilities.SupportsImages != true) return null;
        // A conservative 8K budget, including the existing screen instruction.
        int budget = Math.Min(24_000, 8192 - Encoding.UTF8.GetByteCount(instruction) - 1024 - (imageBase64 != null ? 3072 : 0));
        if (budget < 256) return null;
        source = BoundSource(source, budget);
        var messages = new[]
        {
            new OllamaMessage("system", instruction),
            new OllamaMessage("user", source, imageBase64 == null ? null : new[] { imageBase64 })
        };
        var answer = new StringBuilder();
        bool done = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        await foreach (var delta in client.ChatAsync(model, messages, deadline.Token).ConfigureAwait(false))
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (delta.ToolCalls?.Count > 0 || done) return null;
            answer.Append(delta.Content);
            if (answer.Length > 8192) return null;
            done = delta.Done;
            // Separate reasoning is deliberately ignored, never spoken/logged.
        }
        ct.ThrowIfCancellationRequested();
        return done && answer.Length > 0 ? answer.ToString() : null;
    }

    private static string BoundSource(string source, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(source) <= bytes) return source;
        const string omitted = "\n[Middle of long source omitted; use only the excerpts shown.]\n";
        int half = (bytes - Encoding.UTF8.GetByteCount(omitted)) / 2;
        var runes = source.EnumerateRunes().ToArray();
        int first = 0, last = runes.Length, count = 0;
        while (first < last && count + runes[first].Utf8SequenceLength <= half) count += runes[first++].Utf8SequenceLength;
        count = 0;
        while (last > first && count + runes[last - 1].Utf8SequenceLength <= half) count += runes[--last].Utf8SequenceLength;
        return string.Concat(runes.Take(first).Select(x => x.ToString())) + omitted + string.Concat(runes.Skip(last).Select(x => x.ToString()));
    }
}
