using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Chat Completions compatibility adapter. It owns no credentials, settings,
// history or background loop. The caller binds its key callback to this endpoint.
internal sealed class OpenAiCompatibleClient : IOllamaClient
{
    private const int MaximumDocument = 2 * 1024 * 1024, MaximumEvent = 1024 * 1024;
    private const int MaximumReply = 512 * 1024, MaximumArguments = 32 * 1024, MaximumCalls = 32;
    private static readonly Regex ToolName = new("^[A-Za-z_][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _configuredModel;
    private readonly bool _images, _tools;
    public Uri Endpoint { get; }
    internal bool ModelsListedRemotely { get; private set; }
    private bool IsOfficial => Endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
    // Configuration readiness only. No paid probe or model-list permission is
    // needed by background features; authorization is checked by the server.
    internal bool CanAttemptBackgroundRequest
    {
        get
        {
            try
            {
                string? key = _apiKey();
                return string.IsNullOrEmpty(key) ? !IsOfficial :
                    key.Length <= 8192 && key.All(character => character is >= '!' and <= '~');
            }
            catch { return false; }
        }
    }

    public OpenAiCompatibleClient(Uri endpoint, Func<string?> apiKey, string configuredModel,
        bool supportsImages = false, bool supportsTools = false)
        : this(endpoint, apiKey, configuredModel, supportsImages, supportsTools, CreateHandler()) { }

    internal OpenAiCompatibleClient(Uri endpoint, Func<string?> apiKey, string configuredModel,
        bool supportsImages, bool supportsTools, HttpMessageHandler handler)
    {
        Endpoint = ChatApiEndpoint.Parse(endpoint?.OriginalString ?? "");
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _configuredModel = configuredModel?.Trim() ?? "";
        if (_configuredModel.Length != 0 && !ChatApiModel.IsValid(_configuredModel)) throw InvalidModel();
        _images = supportsImages; _tools = supportsTools;
        _http = new HttpClient(handler, true) { BaseAddress = Endpoint, Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        ModelsListedRemotely = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(HttpMethod.Get, "models");
        using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
        if ((response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented) &&
            ChatApiModel.IsValid(_configuredModel))
            return new[] { _configuredModel }; // Unsupported listing, not a verified model.
        EnsureSuccess(response);
        string body = await ReadDocumentAsync(response.Content, deadline.Token, ct).ConfigureAwait(false);
        using var json = Parse(body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 10000)
            throw ProtocolError();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw ProtocolError();
            string? id = String(item, "id");
            if (ChatApiModel.IsValid(id)) names.Add(id!);
        }
        if (ChatApiModel.IsValid(_configuredModel)) names.Add(_configuredModel);
        ModelsListedRemotely = true;
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ChatApiModel.IsValid(model)) throw InvalidModel();
        return Task.FromResult(new OllamaModelCapabilities(true, _images, false, _tools));
    }

    public Task<IReadOnlyList<OllamaRunningModel>> ListRunningModelsAsync(CancellationToken ct)
        => Task.FromException<IReadOnlyList<OllamaRunningModel>>(RemoteModelOperation());
    public Task PreloadAsync(string model, CancellationToken ct) => Task.FromException(RemoteModelOperation());
    public Task UnloadAsync(string model, CancellationToken ct) => Task.FromException(RemoteModelOperation());

    public IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, CancellationToken ct)
        => StreamAsync(model, messages, Array.Empty<OllamaToolDefinition>(), ct);
    public IAsyncEnumerable<OllamaChatDelta> ChatWithToolsAsync(string model, IReadOnlyList<OllamaMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools, CancellationToken ct)
        => StreamAsync(model, messages, tools, ct);

    private async IAsyncEnumerable<OllamaChatDelta> StreamAsync(string model, IReadOnlyList<OllamaMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ChatApiModel.IsValid(model)) throw InvalidModel();
        var allowedTools = new HashSet<string>(StringComparer.Ordinal);
        var definitions = BuildTools(tools, allowedTools);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model, ["messages"] = BuildMessages(messages), ["stream"] = true
        };
        payload[IsOfficial ? "max_completion_tokens" : "max_tokens"] = IsOfficial ? 4096 : 1024;
        if (definitions.Count != 0) { payload["tools"] = definitions; payload["tool_choice"] = "auto"; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var request = Request(HttpMethod.Post, "chat/completions");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, deadline.Token, ct).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentType?.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var full = ParseCompletion(await ReadDocumentAsync(response.Content, deadline.Token, ct).ConfigureAwait(false), allowedTools);
            ct.ThrowIfCancellationRequested(); yield return full; yield break;
        }
        using var stream = await OpenStreamAsync(response.Content, deadline.Token, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, false);
        var state = new ResponseState(allowedTools);
        await foreach (string data in EventsAsync(reader, deadline.Token, ct).ConfigureAwait(false))
        {
            if (data == "[DONE]") break;
            var delta = state.Read(data);
            if (delta != null) yield return delta;
        }
        ct.ThrowIfCancellationRequested();
        yield return state.Finish(); // Tools are published once, only after a valid finish.
    }

    private HttpRequestMessage Request(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, new Uri(Endpoint, relative));
        string? key;
        try { key = _apiKey(); }
        catch { request.Dispose(); throw new OllamaException(L.T("无法读取保存的 API Key，请重新设置。", "The saved API key could not be read. Set it again.")); }
        if (!string.IsNullOrEmpty(key))
        {
            if (key.Length > 8192 || key.Any(character => character is < '!' or > '~'))
            { request.Dispose(); throw new OllamaException(L.T("API Key 格式无效，请检查后重新填写。", "Invalid API key format. Check and enter it again.")); }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken deadline, CancellationToken caller)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline).ConfigureAwait(false); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) { throw TimeoutError(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        { throw ConnectionError(); }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new OllamaException(L.T("API 身份验证失败，请检查 API Key 和模型权限。", "API authentication failed. Check the API key and model access.")),
            HttpStatusCode.TooManyRequests => new OllamaException(L.T("API 额度不足或请求过于频繁，请检查服务额度并稍后重试。", "The API quota or rate limit was reached. Check your allowance and retry later.")),
            HttpStatusCode.NotFound => new OllamaException(L.T("API 地址或模型不存在，请检查 Base URL 和模型名称。", "The API route or model was not found. Check the base URL and model name.")),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new OllamaException(L.T("API 不接受当前请求，请检查模型是否支持图片、工具调用和 Chat Completions。", "The API rejected this request. Check the model's image, tool and Chat Completions support.")),
            _ when (int)response.StatusCode is >= 300 and < 400 => new OllamaException(L.T("API 返回了重定向，为保护密钥未继续跳转，请填写最终服务地址。", "The API redirected the request. To protect the key, enter the final service URL.")),
            _ => new OllamaException(L.T($"API 请求失败（HTTP {(int)response.StatusCode}），请稍后重试。", $"The API request failed (HTTP {(int)response.StatusCode}). Retry later."))
        };
    }

    private static async Task<Stream> OpenStreamAsync(HttpContent content, CancellationToken deadline, CancellationToken caller)
    {
        try { return await content.ReadAsStreamAsync(deadline).ConfigureAwait(false); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) { throw TimeoutError(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw ConnectionError(); }
    }

    private static async Task<int> ReadAsync(StreamReader reader, Memory<char> buffer, CancellationToken deadline, CancellationToken caller)
    {
        try { return await reader.ReadAsync(buffer, deadline).ConfigureAwait(false); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) { throw TimeoutError(); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or DecoderFallbackException) { throw ConnectionError(); }
    }

    private static async Task<string> ReadDocumentAsync(HttpContent content, CancellationToken deadline, CancellationToken caller)
    {
        using var stream = await OpenStreamAsync(content, deadline, caller).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        char[] buffer = new char[4096]; var text = new StringBuilder(); int count;
        while ((count = await ReadAsync(reader, buffer, deadline, caller).ConfigureAwait(false)) != 0)
        { if (text.Length + count > MaximumDocument) throw ProtocolError(); text.Append(buffer, 0, count); }
        return text.ToString();
    }

    private static async IAsyncEnumerable<string> EventsAsync(StreamReader reader, CancellationToken deadline,
        [EnumeratorCancellation] CancellationToken caller)
    {
        char[] buffer = new char[4096]; var line = new StringBuilder(); var data = new StringBuilder();
        int read, total = 0, events = 0;
        string? ConsumeLine()
        {
            string value = line.ToString().TrimEnd('\r'); line.Clear();
            if (value.Length == 0)
            {
                if (data.Length == 0) return null;
                string result = data.ToString(); data.Clear(); return result;
            }
            if (value.StartsWith("data:", StringComparison.Ordinal))
            {
                string part = value[5..]; if (part.StartsWith(' ')) part = part[1..];
                if (data.Length != 0) data.Append('\n');
                if (data.Length + part.Length > MaximumEvent) throw ProtocolError();
                data.Append(part);
            }
            return null;
        }
        while ((read = await ReadAsync(reader, buffer, deadline, caller).ConfigureAwait(false)) != 0)
        {
            total += read; if (total > 8 * 1024 * 1024) throw ProtocolError();
            for (int index = 0; index < read; index++)
            {
                if (buffer[index] == '\n')
                {
                    string? item = ConsumeLine();
                    if (item != null) { if (++events > 16384) throw ProtocolError(); yield return item; }
                }
                else { if (line.Length >= MaximumEvent) throw ProtocolError(); line.Append(buffer[index]); }
            }
        }
        if (line.Length != 0) ConsumeLine();
        if (data.Length != 0) yield return data.ToString();
    }

    private List<object> BuildMessages(IReadOnlyList<OllamaMessage> messages)
    {
        if (messages == null || messages.Count is 0 or > 256) throw ProtocolError();
        var result = new List<object>(); var pending = new List<(string Id, string Name)>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal); long total = 0, imageCharacters = 0; int sequence = 0, imageCount = 0;
        foreach (var message in messages)
        {
            if (message == null || message.Role is not ("system" or "user" or "assistant" or "tool") || message.Content == null ||
                message.Content.Length > 256 * 1024 || message.Thinking?.Length > MaximumReply ||
                message.Role != "assistant" && message.Thinking != null) throw ProtocolError();
            total += message.Content.Length + (message.Thinking?.Length ?? 0); if (total > 1024 * 1024) throw ProtocolError();
            var wire = new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Content };
            if (message.Role == "tool")
            {
                if (!ValidToolName(message.ToolName) || message.Images?.Count > 0 || message.ToolCalls?.Count > 0 || message.Thinking != null) throw ProtocolError();
                int match = pending.FindIndex(item => item.Name == message.ToolName);
                if (match < 0) throw ProtocolError();
                wire["tool_call_id"] = pending[match].Id; pending.RemoveAt(match);
            }
            else
            {
                if (pending.Count != 0 || message.ToolName != null) throw ProtocolError();
                if (message.ToolCalls?.Count > 0)
                {
                    if (message.Role != "assistant" || message.ToolCalls.Count > MaximumCalls) throw ProtocolError();
                    var calls = new List<object>();
                    foreach (var call in message.ToolCalls)
                    {
                        if (call == null || !ValidToolName(call.Name)) throw ProtocolError();
                        _ = Object(call.ArgumentsJson);
                        total += call.ArgumentsJson.Length; if (total > 1024 * 1024) throw ProtocolError();
                        string id = string.IsNullOrEmpty(call.Id) ? "call_pet_" + sequence++ : call.Id;
                        if (!ValidId(id) || !seenIds.Add(id)) throw ProtocolError();
                        calls.Add(new { id, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } });
                        pending.Add((id, call.Name));
                    }
                    wire["tool_calls"] = calls;
                }
                if (!IsOfficial && message.Role == "assistant" && !string.IsNullOrEmpty(message.Thinking)) wire["reasoning_content"] = message.Thinking;
                if (message.Images?.Count > 0)
                {
                    if (!_images) throw new OllamaException(L.T("当前 API 模型未启用图片支持，请在连接设置中启用或移除图片。", "Image support is off for this API model. Enable it in connection settings or remove the image."));
                    if (message.Role != "user" || (imageCount += message.Images.Count) > 8) throw ProtocolError();
                    var content = new List<object> { new { type = "text", text = message.Content } };
                    foreach (string image in message.Images)
                    {
                        imageCharacters += image?.Length ?? 0; if (imageCharacters > 32 * 1024 * 1024) throw ProtocolError();
                        content.Add(new { type = "image_url", image_url = new { url = ImageUrl(image!) } });
                    }
                    wire["content"] = content;
                }
            }
            result.Add(wire);
        }
        if (pending.Count != 0) throw ProtocolError();
        return result;
    }

    private List<object> BuildTools(IReadOnlyList<OllamaToolDefinition> tools, HashSet<string> names)
    {
        if (tools == null || tools.Count > MaximumCalls) throw ProtocolError();
        if (tools.Count > 0 && !_tools) throw new OllamaException(L.T("当前 API 模型未启用工具调用，请在连接设置中启用或关闭相关功能。", "Tool calling is off for this API model. Enable it in connection settings or turn off features that require tools."));
        var result = new List<object>();
        foreach (var tool in tools)
        {
            if (tool == null || !ValidToolName(tool.Name) || !names.Add(tool.Name) || tool.Description == null || tool.Description.Length > 4096) throw ProtocolError();
            result.Add(new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = Object(tool.ParametersJson) } });
        }
        return result;
    }

    private static string ImageUrl(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 24 * 1024 * 1024 || value.Any(char.IsWhiteSpace)) throw ProtocolError();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); } catch (FormatException) { throw ProtocolError(); }
        string type = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "png" :
            bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? "jpeg" :
            bytes.Length >= 6 && (Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a") ? "gif" :
            bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? "webp" : "";
        if (type.Length == 0) throw ProtocolError();
        return "data:image/" + type + ";base64," + value;
    }

    private sealed class CallBuilder
    { internal string? Id; internal readonly StringBuilder Name = new(), Arguments = new(); }

    private sealed class ResponseState
    {
        private readonly HashSet<string> _allowed;
        private readonly SortedDictionary<int, CallBuilder> _calls = new();
        private int _characters;
        private bool _finished;
        private string? _finishReason;
        internal int? Tokens;
        internal ResponseState(HashSet<string> allowed) { _allowed = allowed; }
        internal OllamaChatDelta? Read(string json)
        {
            using var document = Parse(json); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) throw ProtocolError();
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("completion_tokens", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int tokens) && tokens >= 0) Tokens = tokens;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 1) throw ProtocolError();
            if (choices.GetArrayLength() == 0) return null; // Optional usage-only terminal chunk.
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object || choice.TryGetProperty("index", out var index) &&
                (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int choiceIndex) || choiceIndex != 0)) throw ProtocolError();
            if (_finished) throw ProtocolError();
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) throw ProtocolError();
            string content = NullableText(delta, "content") ?? NullableText(delta, "refusal") ?? "";
            string? thinking = NullableText(delta, "reasoning_content");
            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
            {
                if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > MaximumCalls || _allowed.Count == 0) throw ProtocolError();
                foreach (var call in calls.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("index", out var toolIndex) ||
                        toolIndex.ValueKind != JsonValueKind.Number || !toolIndex.TryGetInt32(out int number) || number is < 0 or >= MaximumCalls) throw ProtocolError();
                    if (!_calls.TryGetValue(number, out var builder)) { builder = new(); _calls.Add(number, builder); }
                    string? id = NullableText(call, "id");
                    if (id != null)
                    { if (!ValidId(id) || builder.Id != null && builder.Id != id) throw ProtocolError(); builder.Id = id; }
                    string? type = NullableText(call, "type"); if (type != null && type != "function") throw ProtocolError();
                    if (call.TryGetProperty("function", out var function))
                    {
                        if (function.ValueKind != JsonValueKind.Object) throw ProtocolError();
                        builder.Name.Append(NullableText(function, "name")); builder.Arguments.Append(NullableText(function, "arguments"));
                        if (builder.Name.Length > 64 || builder.Arguments.Length > MaximumArguments) throw ProtocolError();
                    }
                }
            }
            _characters += content.Length + (thinking?.Length ?? 0); if (_characters > MaximumReply) throw ProtocolError();
            string? finish = NullableText(choice, "finish_reason");
            if (finish != null) { _finished = true; _finishReason = finish; }
            return content.Length > 0 || thinking?.Length > 0 ? new OllamaChatDelta(content, false, Thinking: thinking) : null;
        }
        internal OllamaChatDelta Finish()
        {
            if (_finished && _finishReason == "length") throw new OllamaException(L.T(
                "API 输出达到本次长度上限，回答尚未完成；未执行未完成的工具调用。",
                "The API reached this request's output limit before completing its answer. Unfinished tool calls were not executed."));
            if (_finished && _finishReason == "content_filter") throw new OllamaException(L.T(
                "API 服务商中止了这次回答，请修改问题后重试。",
                "The API provider stopped this answer. Revise the request and retry."));
            if (!_finished || _finishReason is not ("stop" or "tool_calls")) throw ProtocolError();
            if ((_calls.Count > 0) != (_finishReason == "tool_calls")) throw ProtocolError();
            var calls = new List<OllamaToolCall>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in _calls)
            {
                var call = pair.Value; string name = call.Name.ToString(), args = call.Arguments.ToString();
                if (pair.Key != calls.Count || !ValidToolName(name) || !_allowed.Contains(name) || !ValidId(call.Id) || !ids.Add(call.Id!)) throw ProtocolError();
                _ = Object(args); calls.Add(new OllamaToolCall(name, args, call.Id));
            }
            return new OllamaChatDelta("", true, Tokens, ToolCalls: calls.Count == 0 ? null : calls);
        }
    }

    private static OllamaChatDelta ParseCompletion(string body, HashSet<string> allowed)
    {
        using var doc = Parse(body); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) throw ProtocolError();
        string? role = NullableText(message, "role"); if (role != null && role != "assistant") throw ProtocolError();
        var delta = new Dictionary<string, object?> { ["content"] = NullableText(message, "content"),
            ["refusal"] = NullableText(message, "refusal"), ["reasoning_content"] = NullableText(message, "reasoning_content") };
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
        {
            if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > MaximumCalls) throw ProtocolError();
            var converted = new List<object>();
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) throw ProtocolError();
                converted.Add(new { index = converted.Count, id = String(call, "id"), type = String(call, "type"), function = function.Clone() });
            }
            delta["tool_calls"] = converted;
        }
        var state = new ResponseState(allowed);
        var content = state.Read(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = String(choices[0], "finish_reason") } },
            usage = root.TryGetProperty("usage", out var usage) ? (object)usage.Clone() : null }, JsonOptions));
        var final = state.Finish();
        return final with { Content = content?.Content ?? "", Thinking = content?.Thinking };
    }

    private static string? NullableText(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw ProtocolError();
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw ProtocolError();
        return property.GetString();
    }
    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? NullableText(value, name) : null;
    private static bool ValidToolName(string? name) => name != null && ToolName.IsMatch(name);
    private static bool ValidId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 256 && id.All(c => c is >= '!' and <= '~');
    private static JsonElement Object(string json)
    {
        if (json == null || json.Length > MaximumArguments) throw ProtocolError();
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw ProtocolError();
        return document.RootElement.Clone();
    }
    private static JsonDocument Parse(string value)
    { try { return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 }); } catch (JsonException) { throw ProtocolError(); } }
    private static OllamaException InvalidModel() => new(L.T("请填写有效的 API 模型名称。", "Enter a valid API model name."));
    private static OllamaException ProtocolError() => new(L.T("API 返回格式不兼容或响应不完整；未执行未完成的工具调用。", "The API response is incompatible or incomplete. Unfinished tool calls were not executed."));
    private static OllamaException ConnectionError() => new(L.T("无法连接 API 服务，请检查地址、网络和代理设置。", "Could not connect to the API. Check the address, network and proxy settings."));
    private static OllamaException TimeoutError() => new(L.T("API 请求超时，请稍后重试。", "The API request timed out. Retry later."));
    private static NotSupportedException RemoteModelOperation() => new(L.T("API 模型由服务商管理，不支持本机加载、卸载或显存查询。", "API models are managed by the provider; local loading, unloading and VRAM queries are unavailable."));
    public void Dispose() => _http.Dispose();
}
