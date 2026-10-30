using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed record OllamaModelChoice(string Id, string DisplayName);
internal sealed record OllamaRunningModel(string Name, long? SizeBytes, long? VramBytes,
    int? ContextLength, DateTimeOffset? ExpiresAt);

internal static class OllamaModels
{
    public const string FastModel = "qwen3.5:9b-q8_0";
    public const string QualityModel = "qwen3.8:27b-q4_K_M";
    public static IReadOnlyList<OllamaModelChoice> Supported { get; } = Array.AsReadOnly(new[]
    {
        new OllamaModelChoice(FastModel, "Qwen3.5 9B · Q8_0（快速）"),
        new OllamaModelChoice(QualityModel, "Qwen3.8 27B · Q4_K_M（高质量）")
    });

    private static readonly Regex ModelName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*(?::[0-9]{1,5})?(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*(?::[A-Za-z0-9][A-Za-z0-9._-]*)?$", RegexOptions.CultureInvariant);
    public static string Normalize(string? model) => IsSupported(model?.Trim()) ? model!.Trim() : FastModel;
    internal static bool IsSupported(string? model) => model != null && model.Length <= 512 && ModelName.IsMatch(model) &&
        !model.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) && !model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase);
}

internal sealed record OllamaToolCall(string Name, string ArgumentsJson);
internal sealed record OllamaToolDefinition(string Name, string Description, string ParametersJson);
internal sealed record OllamaMessage(string Role, string Content, IReadOnlyList<string>? Images = null,
    IReadOnlyList<OllamaToolCall>? ToolCalls = null, string? ToolName = null, string? Thinking = null);
internal sealed record OllamaChatDelta(string Content, bool Done, int? EvalCount = null, double? TokensPerSecond = null,
    IReadOnlyList<OllamaToolCall>? ToolCalls = null, string? Thinking = null);
internal sealed record OllamaModelCapabilities(bool? SupportsChat, bool? SupportsImages, bool? SupportsThinking, bool? SupportsTools = null)
{
    public static OllamaModelCapabilities Unknown { get; } = new(null, null, null);
}

internal interface IOllamaClient : IDisposable
{
    Uri Endpoint => OllamaClient.LocalEndpoint;
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
    Task<IReadOnlyList<OllamaRunningModel>> ListRunningModelsAsync(CancellationToken ct)
        => Task.FromException<IReadOnlyList<OllamaRunningModel>>(new NotSupportedException("此服务器暂不提供模型驻留状态。"));
    Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(OllamaModelCapabilities.Unknown);
    IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, CancellationToken ct);
    IAsyncEnumerable<OllamaChatDelta> ChatWithToolsAsync(string model, IReadOnlyList<OllamaMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct) => ChatAsync(model, messages, ct);
    Task UnloadAsync(string model, CancellationToken ct);
    Task PreloadAsync(string model, CancellationToken ct) => Task.FromException(new NotSupportedException("此连接暂不支持手动预加载。"));
}

// Only the user-selected server receives messages. No proxy, redirect, cloud model,
// automatic download, authentication bypass, or certificate-validation bypass.
internal sealed class OllamaClient : IOllamaClient
{
    internal const int ContextTokens = 8192;
    internal const int ResponseTokens = 1024;
    internal static readonly Uri LocalEndpoint = new(OllamaEndpoint.DefaultAddress + "/");
    private const int MaxErrorChars = 16 * 1024;
    private const int MaxDocumentChars = 2 * 1024 * 1024;
    private const int MaxStreamLineChars = 1024 * 1024;
    private const int MaxToolArgumentsChars = 32 * 1024;
    private const int MaxToolCalls = 32;
    private const int MaxReplyChars = 512 * 1024;
    private static readonly Regex ToolNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient http;
    private readonly object cacheLock = new();
    private readonly SemaphoreSlim modelInfoGate = new(1, 1);
    private HashSet<string>? installedModels;
    private readonly Dictionary<string, ModelInfo> modelInfo = new(StringComparer.Ordinal);
    private sealed record ModelInfo(OllamaModelCapabilities Capabilities, object? Think, DateTimeOffset ReadAt);
    public Uri Endpoint { get; }

    public OllamaClient() : this(LocalEndpoint) { }
    public OllamaClient(Uri endpoint) : this(endpoint, CreateLocalHandler()) { }

    // The injectable handler supports protocol checks without sockets or real inference.
    internal OllamaClient(HttpMessageHandler handler) : this(LocalEndpoint, handler) { }
    internal OllamaClient(Uri endpoint, HttpMessageHandler handler)
    {
        Endpoint = OllamaEndpoint.Parse(endpoint?.OriginalString ?? "");
        http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = Endpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static SocketsHttpHandler CreateLocalHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, deadline.Token, ct).ConfigureAwait(false);
        string body = await ReadBodyAsync(response, MaxDocumentChars, truncate: false, deadline.Token, ct).ConfigureAwait(false);
        using var document = ParseDocument(body);
        ThrowResponseError(document.RootElement);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw ProtocolFailure();
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || HasNonEmptyString(item, "remote_model") || HasNonEmptyString(item, "remote_host")) continue;
            string? name = StringProperty(item, "name") ?? StringProperty(item, "model");
            if (OllamaModels.IsSupported(name)) available.Add(name!);
        }
        lock (cacheLock)
        {
            installedModels = available;
            // A refresh may reflect a replaced local model or changed capabilities.
            modelInfo.Clear();
        }
        return available.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Observes server residency without touching selection, installed-model caches,
    // keep_alive, or inference. Missing telemetry is unknown, not zero usage.
    public async Task<IReadOnlyList<OllamaRunningModel>> ListRunningModelsAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/ps");
        using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            throw new NotSupportedException("此 Ollama 服务器暂不提供模型驻留状态。连接与聊天仍可正常使用。");
        await EnsureSuccessAsync(response, deadline.Token, ct).ConfigureAwait(false);
        string body = await ReadBodyAsync(response, MaxDocumentChars, truncate: false, deadline.Token, ct).ConfigureAwait(false);
        using var document = ParseDocument(body);
        ThrowResponseError(document.RootElement);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw ProtocolFailure();
        var running = new List<OllamaRunningModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || HasNonEmptyString(item, "remote_model") || HasNonEmptyString(item, "remote_host")) continue;
            string? name = StringProperty(item, "name") ?? StringProperty(item, "model");
            if (!OllamaModels.IsSupported(name) || !seen.Add(name!)) continue;
            long? Bytes(string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long number) && number >= 0 ? number : null;
            long? context = Bytes("context_length");
            DateTimeOffset? expires = item.TryGetProperty("expires_at", out var expiry) && expiry.ValueKind == JsonValueKind.String &&
                expiry.TryGetDateTimeOffset(out var parsedExpiry) ? parsedExpiry : null;
            running.Add(new OllamaRunningModel(name!, Bytes("size"), Bytes("size_vram"),
                context is > 0 and <= int.MaxValue ? (int)context.Value : null, expires));
        }
        return running.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct)
        => (await ReadModelInfoAsync(model, ct).ConfigureAwait(false)).Capabilities;

    private async Task<ModelInfo> ReadModelInfoAsync(string model, CancellationToken ct)
    {
        RequireSupportedModel(model);
        ct.ThrowIfCancellationRequested();
        lock (cacheLock)
            if (modelInfo.TryGetValue(model, out var initialCached) && DateTimeOffset.UtcNow - initialCached.ReadAt < TimeSpan.FromMinutes(2)) return initialCached;
        await modelInfoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (cacheLock)
                if (modelInfo.TryGetValue(model, out var currentCached) && DateTimeOffset.UtcNow - currentCached.ReadAt < TimeSpan.FromMinutes(2)) return currentCached;
            await RequireInstalledModelAsync(model, ct).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = JsonRequest("api/show", new { model, verbose = false });
            using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
            // Old compatible servers may lack /api/show. /api/tags has already
            // established that this is installed and has no remote/cloud metadata.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
                return CacheModelInfo(model, OllamaModelCapabilities.Unknown, null);
            await EnsureSuccessAsync(response, deadline.Token, ct).ConfigureAwait(false);
            string body = await ReadBodyAsync(response, MaxDocumentChars, truncate: false, deadline.Token, ct).ConfigureAwait(false);
            using var document = ParseDocument(body);
            var root = document.RootElement;
            ThrowResponseError(root);
            if (HasNonEmptyString(root, "remote_model") || HasNonEmptyString(root, "remote_host"))
                throw new OllamaException("所选模型会转发到云端，桌宠仅使用该 Ollama 服务器已安装的本地模型。");
            bool? chat = null, images = null, thinking = null, tools = null;
            if (root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
            {
                var names = new HashSet<string>(capabilities.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!), StringComparer.OrdinalIgnoreCase);
                chat = names.Contains("completion"); images = names.Contains("vision"); thinking = names.Contains("thinking"); tools = names.Contains("tools");
            }
            object? thinkOption = null;
            if (root.TryGetProperty("thinking", out var thinkingInfo) && thinkingInfo.ValueKind == JsonValueKind.Object &&
                thinkingInfo.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                bool supportsFalse = false, supportsReasoning = false, supportsLow = false;
                foreach (var value in values.EnumerateArray())
                {
                    supportsFalse |= value.ValueKind == JsonValueKind.False;
                    supportsReasoning |= value.ValueKind is JsonValueKind.True or JsonValueKind.String;
                    supportsLow |= value.ValueKind == JsonValueKind.String && value.GetString() == "low";
                }
                thinking = supportsReasoning;
                if (supportsFalse) thinkOption = false;
                else if (supportsLow) thinkOption = "low";
            }
            return CacheModelInfo(model, new OllamaModelCapabilities(chat, images, thinking, tools), thinkOption);
        }
        finally { modelInfoGate.Release(); }
    }

    private ModelInfo CacheModelInfo(string model, OllamaModelCapabilities capabilities, object? think)
    {
        var info = new ModelInfo(capabilities, think, DateTimeOffset.UtcNow);
        lock (cacheLock) modelInfo[model] = info;
        return info;
    }

    private async Task RequireInstalledModelAsync(string model, CancellationToken ct)
    {
        if (!await IsInstalledModelAsync(model, ct).ConfigureAwait(false))
            throw new OllamaException("所选模型尚未安装在当前 Ollama 服务器，或属于云端模型。请刷新模型列表后重新选择。");
    }

    private async Task<bool> IsInstalledModelAsync(string model, CancellationToken ct)
    {
        lock (cacheLock) if (installedModels?.Contains(model) == true) return true;
        var models = await ListModelsAsync(ct).ConfigureAwait(false);
        return models.Contains(model, StringComparer.Ordinal);
    }

    public IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, CancellationToken ct)
        => ChatCoreAsync(model, messages, null, ct);

    public IAsyncEnumerable<OllamaChatDelta> ChatWithToolsAsync(string model, IReadOnlyList<OllamaMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct) => ChatCoreAsync(model, messages, tools, ct);

    private async IAsyncEnumerable<OllamaChatDelta> ChatCoreAsync(string model, IReadOnlyList<OllamaMessage> messages,
        IReadOnlyList<OllamaToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        RequireSupportedModel(model);
        ValidateMessages(messages);
        ValidateTools(tools);
        ct.ThrowIfCancellationRequested();
        var info = await ReadModelInfoAsync(model, ct).ConfigureAwait(false);
        if (info.Capabilities.SupportsChat == false) throw new OllamaException("所选模型不支持聊天，请选择支持对话的模型。");
        if (info.Capabilities.SupportsImages == false && messages.Any(message => message.Images?.Count > 0))
            throw new OllamaException("所选模型不支持图片理解。请移除图片或选择支持图片的模型。");
        if (info.Capabilities.SupportsTools == false && tools?.Count > 0)
            throw new OllamaException("所选模型不支持工具调用，请使用兼容的联网模式或切换模型。");
        var payload = new
        {
            model,
            messages = messages.Select(message => new
            {
                role = message.Role, content = message.Content, images = message.Images,
                tool_calls = message.ToolCalls?.Count > 0 ? message.ToolCalls.Select((call, index) => new
                {
                    type = "function", function = new { index, name = call.Name, arguments = JsonObject(call.ArgumentsJson) }
                }).ToArray() : null,
                tool_name = message.ToolName,
                thinking = message.Thinking
            }).ToArray(),
            tools = tools?.Count > 0 ? tools.Select(tool => new
            {
                type = "function", function = new { name = tool.Name, description = tool.Description, parameters = JsonObject(tool.ParametersJson) }
            }).ToArray() : null,
            stream = true,
            think = info.Think,
            keep_alive = "5m",
            options = new { num_ctx = ContextTokens, num_predict = ResponseTokens, temperature = 0.6 }
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = JsonRequest("api/chat", payload);
        using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, deadline.Token, ct).ConfigureAwait(false);
        using var stream = await OpenStreamAsync(response, deadline.Token, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
        var buffer = new char[4096];
        var line = new StringBuilder();
        int replyChars = 0, toolCalls = 0;
        while (true)
        {
            int count = await ReadAsync(reader, buffer.AsMemory(), deadline.Token, ct).ConfigureAwait(false);
            if (count == 0)
            {
                // A final record without a newline is valid; EOF without done:true is not.
                if (line.Length > 0)
                {
                    var final = ParseDelta(line.ToString());
                    if (final != null)
                    {
                        BoundDelta(final, ref replyChars, ref toolCalls);
                        yield return final;
                        if (final.Done) yield break;
                    }
                }
                throw new OllamaException("Ollama 的回复中途断开，内容可能不完整。请重新发送。");
            }
            for (int index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                char value = buffer[index];
                if (value == '\n')
                {
                    var delta = ParseDelta(line.ToString());
                    line.Clear();
                    if (delta != null)
                    {
                        BoundDelta(delta, ref replyChars, ref toolCalls);
                        yield return delta;
                        if (delta.Done) yield break;
                    }
                }
                else
                {
                    if (line.Length >= MaxStreamLineChars) throw new OllamaException("Ollama 返回了过大的单段回复，请重新发送。");
                    line.Append(value);
                }
            }
        }
    }

    public async Task PreloadAsync(string model, CancellationToken ct)
    {
        RequireSupportedModel(model);
        ct.ThrowIfCancellationRequested();
        await RequireInstalledModelAsync(model, ct).ConfigureAwait(false);
        var info = await ReadModelInfoAsync(model, ct).ConfigureAwait(false);
        if (info.Capabilities.SupportsChat == false) throw new OllamaException("当前模型不支持聊天，请选择其他模型。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        // Empty prompt is Ollama's explicit load operation. Do not override its
        // automatic retention policy or global settings. Match the existing chat
        // context allocation so the next chat need not reload a different runner.
        using var request = JsonRequest("api/generate", new { model, prompt = "", stream = false, options = new { num_ctx = ContextTokens } });
        using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, deadline.Token, ct).ConfigureAwait(false);
        string body = await ReadBodyAsync(response, MaxDocumentChars, truncate: false, deadline.Token, ct).ConfigureAwait(false);
        using var document = ParseDocument(body);
        ThrowResponseError(document.RootElement);
        if (!document.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
            throw new OllamaException("Ollama 未确认加载完成，请稍后重试。");
    }

    public async Task UnloadAsync(string model, CancellationToken ct)
    {
        RequireSupportedModel(model);
        ct.ThrowIfCancellationRequested();
        if (!await IsInstalledModelAsync(model, ct).ConfigureAwait(false)) return;
        await ReadModelInfoAsync(model, ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = JsonRequest("api/generate", new { model, keep_alive = 0, stream = false });
        using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
        // A previously selected model may never have been installed, or may have been
        // removed in Ollama. It cannot retain VRAM, so it must not block a model switch.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }
        await EnsureSuccessAsync(response, deadline.Token, ct).ConfigureAwait(false);
        string body = await ReadBodyAsync(response, MaxDocumentChars, truncate: false, deadline.Token, ct).ConfigureAwait(false);
        using var document = ParseDocument(body);
        ThrowResponseError(document.RootElement);
        if (!document.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
            throw new OllamaException("Ollama 未确认释放模型，请稍后重试。");
    }

    private static void RequireSupportedModel(string? model)
    {
        if (!OllamaModels.IsSupported(model)) throw new OllamaException("模型名称无效，不能使用 URL、路径跳转或云端模型。请从已安装的模型列表选择。");
    }

    private static void ValidateMessages(IReadOnlyList<OllamaMessage> messages)
    {
        if (messages == null || messages.Count == 0 || messages.Count > 256) throw new OllamaException("聊天内容为空或过长，请新建对话后重试。");
        long textChars = 0;
        foreach (var message in messages)
        {
            if (message == null || message.Role is not ("user" or "assistant" or "system" or "tool") || message.Content == null)
                throw new OllamaException("聊天消息格式无效，请新建对话后重试。");
            if (message.Content.Length > 256 * 1024 || message.Thinking?.Length > MaxReplyChars)
                throw new OllamaException("聊天内容过长，请新建对话后重试。");
            textChars += message.Content.Length + (message.Thinking?.Length ?? 0);
            if (message.Role == "tool")
            {
                if (!ValidToolName(message.ToolName) || message.Images?.Count > 0 || message.ToolCalls?.Count > 0 || message.Thinking != null)
                    throw ProtocolFailure();
            }
            else if (message.ToolName != null) throw ProtocolFailure();
            if (message.Role != "assistant" && (message.ToolCalls?.Count > 0 || message.Thinking != null)) throw ProtocolFailure();
            if (message.ToolCalls != null)
            {
                if (message.ToolCalls.Count > MaxToolCalls) throw ProtocolFailure();
                foreach (var call in message.ToolCalls)
                {
                    ValidateToolCall(call);
                    textChars += call.ArgumentsJson.Length;
                }
            }
            if (textChars > 2 * 1024 * 1024) throw new OllamaException("聊天内容过长，请新建对话后重试。");
            if (message.Images == null) continue;
            foreach (string image in message.Images)
            {
                // API image values are base64 bytes, never paths or URLs that could be followed.
                if (string.IsNullOrWhiteSpace(image) || image.Length > 32 * 1024 * 1024 || image.Length % 4 != 0 || image.Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '+' or '/' or '=')))
                    throw new OllamaException("图片数据无效或过大，请重新选择图片。");
            }
        }
    }

    private static void ValidateTools(IReadOnlyList<OllamaToolDefinition>? tools)
    {
        if (tools == null) return;
        if (tools.Count > 16) throw ProtocolFailure();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool == null || !ValidToolName(tool.Name) || !names.Add(tool.Name) || tool.Description == null || tool.Description.Length > 4096)
                throw ProtocolFailure();
            var schema = JsonObject(tool.ParametersJson);
            if (StringProperty(schema, "type") != "object") throw ProtocolFailure();
        }
    }

    private static bool ValidToolName(string? value) => value != null && ToolNamePattern.IsMatch(value);

    private static void ValidateToolCall(OllamaToolCall? call)
    {
        if (call == null || !ValidToolName(call.Name)) throw ProtocolFailure();
        JsonObject(call.ArgumentsJson);
    }

    private static JsonElement JsonObject(string value)
    {
        if (value == null || value.Length > MaxToolArgumentsChars) throw ProtocolFailure();
        using var document = ParseDocument(value);
        return document.RootElement.Clone();
    }

    private static void BoundDelta(OllamaChatDelta delta, ref int replyChars, ref int toolCalls)
    {
        replyChars += delta.Content.Length + (delta.Thinking?.Length ?? 0);
        if (delta.ToolCalls != null)
        {
            toolCalls += delta.ToolCalls.Count;
            foreach (var call in delta.ToolCalls) replyChars += call.ArgumentsJson.Length;
        }
        if (replyChars > MaxReplyChars || toolCalls > MaxToolCalls)
            throw new OllamaException("Ollama 返回的回复或工具调用过多，已停止接收。请简化问题后重试。");
    }

    private static HttpRequestMessage JsonRequest(string path, object payload) => new(HttpMethod.Post, path)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
    };

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken active, CancellationToken caller)
    {
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, active).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw TimeoutFailure(); }
        catch (HttpRequestException) { throw new OllamaException("无法连接 Ollama。请确认服务已启动、地址和端口正确，并检查网络及 HTTPS 证书后重试。"); }
        catch (IOException) { throw new OllamaException("与 Ollama 的连接已中断，请重试。"); }
    }

    private static async Task<Stream> OpenStreamAsync(HttpResponseMessage response, CancellationToken active, CancellationToken caller)
    {
        try { return await response.Content.ReadAsStreamAsync(active).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw TimeoutFailure(); }
        catch (HttpRequestException) { throw new OllamaException("与 Ollama 的连接已中断，请重试。"); }
        catch (IOException) { throw new OllamaException("与 Ollama 的连接已中断，请重试。"); }
    }

    private static async Task<int> ReadAsync(StreamReader reader, Memory<char> buffer, CancellationToken active, CancellationToken caller)
    {
        try { return await reader.ReadAsync(buffer, active).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw TimeoutFailure(); }
        catch (HttpRequestException) { throw new OllamaException("Ollama 的回复连接已中断，内容可能不完整。请重试。"); }
        catch (IOException) { throw new OllamaException("Ollama 的回复连接已中断，内容可能不完整。请重试。"); }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, int limit, bool truncate, CancellationToken active, CancellationToken caller)
    {
        using var stream = await OpenStreamAsync(response, active, caller).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
        var buffer = new char[Math.Min(4096, limit)];
        while (true)
        {
            int count = await ReadAsync(reader, buffer.AsMemory(0, Math.Min(buffer.Length, limit - result.Length)), active, caller).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            result.Append(buffer, 0, count);
            if (result.Length < limit) continue;
            if (truncate) return result.ToString();
            if (await ReadAsync(reader, buffer.AsMemory(0, 1), active, caller).ConfigureAwait(false) != 0)
                throw new OllamaException("Ollama 返回的数据过大，请检查本机服务后重试。");
            return result.ToString();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken active, CancellationToken caller)
    {
        if (response.IsSuccessStatusCode) return;
        // Read a bounded diagnostic only for classification; never expose server text or user images.
        string diagnostic = await ReadBodyAsync(response, MaxErrorChars, truncate: true, active, caller).ConfigureAwait(false);
        throw DescribeServerError(diagnostic, response.StatusCode);
    }

    private static OllamaChatDelta? ParseDelta(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        using var document = ParseDocument(line);
        JsonElement root = document.RootElement;
        ThrowResponseError(root);
        string content = "";
        string? thinking = null;
        IReadOnlyList<OllamaToolCall>? calls = null;
        if (root.TryGetProperty("message", out var message))
        {
            if (message.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
            if (message.TryGetProperty("content", out var contentValue) && contentValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw ProtocolFailure();
            content = StringProperty(message, "content") ?? "";
            if (message.TryGetProperty("thinking", out var thinkingValue) && thinkingValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw ProtocolFailure();
            thinking = StringProperty(message, "thinking");
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
            {
                if (toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() > MaxToolCalls) throw ProtocolFailure();
                var parsed = new List<OllamaToolCall>();
                foreach (var item in toolCalls.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        (item.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "function")) ||
                        !item.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
                        !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
                        throw ProtocolFailure();
                    var call = new OllamaToolCall(StringProperty(function, "name") ?? "", arguments.GetRawText());
                    ValidateToolCall(call);
                    parsed.Add(call);
                }
                if (parsed.Count > 0) calls = parsed.AsReadOnly();
            }
        }
        bool done = root.TryGetProperty("done", out var doneValue) && doneValue.ValueKind == JsonValueKind.True;
        if (!done && !root.TryGetProperty("message", out _)) throw ProtocolFailure();
        int? evalCount = root.TryGetProperty("eval_count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int countValue) && countValue >= 0 ? countValue : null;
        double? tokensPerSecond = null;
        if (evalCount.HasValue && root.TryGetProperty("eval_duration", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out double nanoseconds) && nanoseconds > 0 && double.IsFinite(nanoseconds))
            tokensPerSecond = evalCount.Value * 1_000_000_000d / nanoseconds;
        return new OllamaChatDelta(content, done, evalCount, tokensPerSecond, calls, thinking);
    }

    private static JsonDocument ParseDocument(string value)
    {
        try
        {
            var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw ProtocolFailure();
        }
        catch (JsonException) { throw ProtocolFailure(); }
    }

    private static string? StringProperty(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool HasNonEmptyString(JsonElement element, string name) => !string.IsNullOrEmpty(StringProperty(element, name));
    private static void ThrowResponseError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw DescribeServerError(error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : "", null);
    }

    private static OllamaException DescribeServerError(string diagnostic, HttpStatusCode? status)
    {
        if (status == HttpStatusCode.NotFound || diagnostic.Contains("not found", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("pull model", StringComparison.OrdinalIgnoreCase))
            return new OllamaException("当前 Ollama 服务器尚未安装所选模型。请刷新模型列表，或安装对应模型后重试。");
        if (diagnostic.Contains("memory", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("out of vram", StringComparison.OrdinalIgnoreCase))
            return new OllamaException("Ollama 服务器可用内存或显存不足。请释放资源，或切换到较小的模型后重试。");
        if (diagnostic.Contains("image", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("vision", StringComparison.OrdinalIgnoreCase))
            return new OllamaException("Ollama 无法处理这张图片。请尝试较小的 PNG/JPEG 图片，并确认模型支持图片理解。");
        if (diagnostic.Contains("does not support tools", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("tools not supported", StringComparison.OrdinalIgnoreCase))
            return new OllamaException("所选模型不支持工具调用，请使用兼容的联网模式或切换模型。");
        if (status.HasValue && (int)status.Value >= 300 && (int)status.Value < 400)
            return new OllamaException("Ollama 返回了重定向，已阻止转发聊天内容。请填写最终服务器地址。");
        return new OllamaException(status.HasValue ? $"Ollama 请求失败（HTTP {(int)status.Value}），请稍后重试。" : "Ollama 生成回复时发生错误，请稍后重试。");
    }

    private static OllamaException TimeoutFailure() => new("等待 Ollama 超时。首次加载模型可能较慢，请确认服务地址和运行状态后重试。");
    private static OllamaException ProtocolFailure() => new("Ollama 返回了无法识别的数据，请检查服务版本后重试。");
    public void Dispose() => http.Dispose();
}

internal sealed class OllamaException : Exception
{
    public OllamaException(string message) : base(message) { }
}
