using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// In-memory protocol checks only: no real key, network, paid request or model load.
internal static class OpenAiCompatibleClientChecks
{
    private const string DummyKey = "dummy-test-key-NOT-A-REAL-SECRET";
    private const string Model = "provider/chat-model:stable";
    private static readonly Uri Endpoint = new("https://provider.example/custom/openai/v1/");
    private static readonly OllamaMessage[] Messages = { new("system", "A helpful pet"), new("user", "Hello") };
    private static readonly OllamaToolDefinition[] Tools = { new("pet_action", "Play an action", "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"}}}") };

    internal static async Task<object> RunAsync()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("OpenAI-compatible client check failed: " + name);
        }
        using (var handler = OpenAiCompatibleClient.CreateHandler())
            Check(!handler.AllowAutoRedirect && !handler.UseCookies && handler.Credentials == null &&
                handler.SslOptions.RemoteCertificateValidationCallback == null && handler.UseProxy,
                "credentials_never_follow_redirects_or_cookies_and_tls_verification_remains_enabled");

        var chatHandler = new FakeHandler(async (request, ct) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/chat/completions", "official_chat_route");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization?.Parameter == DummyKey,
                "key_is_only_in_bearer_header");
            string body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body); var root = document.RootElement;
            Check(root.GetProperty("stream").GetBoolean() && root.GetProperty("max_completion_tokens").GetInt32() == 4096 &&
                !root.TryGetProperty("max_tokens", out _) && !root.TryGetProperty("temperature", out _) && !root.TryGetProperty("top_p", out _),
                "official_reasoning_models_use_compatible_bounded_completion_parameters");
            Check(root.GetProperty("messages")[0].GetProperty("role").GetString() == "system" &&
                root.GetProperty("messages")[1].GetProperty("content").GetString() == "Hello" && !body.Contains(DummyKey) &&
                !root.TryGetProperty("keep_alive", out _) && !root.TryGetProperty("options", out _) && !root.TryGetProperty("tools", out _),
                "history_preserved_without_key_or_ollama_only_fields");
            return Sse(": heartbeat\r\n\r\n" + Chunk(new { role = "assistant", content = "你好" }) +
                Chunk(new { content = "！" }, "stop") + "data: {\"choices\":[],\"usage\":{\"completion_tokens\":3}}\n\ndata: [DONE]\n\n");
        });
        using (var client = Client(chatHandler, endpoint: new Uri("https://api.openai.com/v1/")))
        {
            var deltas = await Collect(client);
            Check(string.Concat(deltas.Select(d => d.Content)) == "你好！" && deltas.Count == 3 && deltas[^1].Done && deltas[^1].EvalCount == 3,
                "sse_text_final_content_and_usage_are_preserved");
        }
        Check(chatHandler.Disposed, "client_disposes_owned_handler");

        var listing = new FakeHandler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://provider.example/custom/openai/v1/models" && request.Content == null,
                "listing_uses_custom_base_path_without_inference");
            return Task.FromResult(Json("{\"data\":[{\"id\":\"other/model\"},{\"id\":\"other/model\"},{\"id\":\"../bad\"}]}"));
        });
        using (var client = Client(listing))
        {
            var models = await client.ListModelsAsync(CancellationToken.None);
            Check(models.SequenceEqual(new[] { "other/model", Model }) && client.ModelsListedRemotely,
                "remote_listing_merges_manual_model_deduplicates_and_filters_invalid_ids");
        }
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotImplemented })
        {
            using var client = Client(new FakeHandler((_, _) => Task.FromResult(Json("PRIVATE_SERVER_DETAILS", status))));
            Check((await client.ListModelsAsync(CancellationToken.None)).SequenceEqual(new[] { Model }) && !client.ModelsListedRemotely,
                "unsupported_listing_is_explicit_unverified_manual_model_" + (int)status);
        }
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound))), model: ""))
            Check(await Fails(() => client.ListModelsAsync(CancellationToken.None)), "unsupported_listing_without_manual_model_is_error");
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(Json("{\"data\":null}")))))
            Check(await Fails(() => client.ListModelsAsync(CancellationToken.None)) && !client.ModelsListedRemotely, "malformed_listing_is_never_reported_as_connected");

        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
        var imageHandler = new FakeHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = document.RootElement;
            var image = root.GetProperty("messages")[0]; var content = image.GetProperty("content");
            Check(content.GetArrayLength() == 2 && content[0].GetProperty("text").GetString() == "看图" &&
                content[1].GetProperty("image_url").GetProperty("url").GetString() == "data:image/png;base64," + png &&
                !image.TryGetProperty("images", out _) && root.GetProperty("max_tokens").GetInt32() == 1024,
                "compatible_provider_receives_standard_image_content_and_output_limit");
            return Sse(Chunk(new { content = "看到了" }, "stop") + Done);
        });
        using (var client = Client(imageHandler, images: true))
            Check((await Collect(client, new[] { new OllamaMessage("user", "看图", new[] { png }) })).Last().Done, "image_chat_completes");
        var neverSend = new FakeHandler((_, _) => throw new InvalidOperationException("Must not send"));
        using (var client = Client(neverSend))
        {
            Check(await Fails(() => Collect(client, new[] { new OllamaMessage("user", "看图", new[] { png }) })), "disabled_image_capability_rejects_before_send");
            Check(await Fails(() => Collect(client, tools: Tools)), "disabled_tool_capability_rejects_before_send");
            var capability = await client.GetModelInfoAsync(Model, CancellationToken.None);
            Check(capability.SupportsChat == true && capability.SupportsImages == false && capability.SupportsTools == false, "capabilities_are_explicit_configuration_without_inference");
            Check(await Unsupported(() => client.PreloadAsync(Model, default)) && await Unsupported(() => client.UnloadAsync(Model, default)) &&
                await Unsupported(() => client.ListRunningModelsAsync(default)) && neverSend.Calls == 0, "remote_models_never_request_local_loading_unloading_or_vram");
        }

        await CheckTools(Check);
        await CheckErrors(Check);
        await CheckStreaming(Check);
        return new { passed = true, checks, realRequests = 0 };
    }

    private static async Task CheckTools(Action<bool, string> check)
    {
        string fragments = Chunk(new { tool_calls = new[] {
            new { index = 0, id = "call_actual_A", type = "function", function = new { name = "pet_", arguments = "{\"action\":" } },
            new { index = 1, id = "call_actual_B", type = "function", function = new { name = "pet_action", arguments = "{" } } } }) +
            Chunk(new { tool_calls = new[] {
                new { index = 1, function = new { name = "", arguments = "\"action\":\"walk\"}" } },
                new { index = 0, function = new { name = "action", arguments = "\"sing\"}" } } } }, "tool_calls") + Done;
        List<OllamaToolCall> calls;
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(Sse(fragments))), tools: true))
        {
            var deltas = await Collect(client, tools: Tools);
            calls = deltas.SelectMany(d => d.ToolCalls ?? Array.Empty<OllamaToolCall>()).ToList();
            check(deltas.Count == 1 && deltas[0].Done && calls.Count == 2 && calls[0].Id == "call_actual_A" && calls[1].Id == "call_actual_B" &&
                calls[0].Name == "pet_action" && calls[0].ArgumentsJson == "{\"action\":\"sing\"}" && calls[1].ArgumentsJson == "{\"action\":\"walk\"}",
                "interleaved_tool_fragments_aggregate_once_with_actual_provider_ids");
        }
        var history = new[] { new OllamaMessage("user", "Do things"), new OllamaMessage("assistant", "", ToolCalls: calls, Thinking: "reasoning"),
            new OllamaMessage("tool", "singing", ToolName: "pet_action"), new OllamaMessage("tool", "walking", ToolName: "pet_action") };
        var roundtrip = new FakeHandler(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var messages = doc.RootElement.GetProperty("messages");
            check(messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString() == "call_actual_A" &&
                messages[2].GetProperty("tool_call_id").GetString() == "call_actual_A" && messages[3].GetProperty("tool_call_id").GetString() == "call_actual_B" &&
                messages[1].GetProperty("reasoning_content").GetString() == "reasoning", "tool_history_roundtrip_keeps_call_ids_order_and_compatible_reasoning");
            return Sse(Chunk(new { content = "Done" }, "stop") + Done);
        });
        using (var client = Client(roundtrip, tools: true)) await Collect(client, history, Tools);

        foreach (var invalid in new[]
        {
            Chunk(new { tool_calls = new[] { new { index = 0, id = "id", type = "function", function = new { name = "pet_action", arguments = "{" } } } }, "tool_calls") + Done,
            Chunk(new { tool_calls = new[] { new { index = 0, type = "function", function = new { name = "pet_action", arguments = "{}" } } } }, "tool_calls") + Done,
            Chunk(new { tool_calls = new[] { new { index = 0, id = "id", type = "function", function = new { name = "undeclared", arguments = "{}" } } } }, "tool_calls") + Done,
            Chunk(new { tool_calls = new[] { new { index = "PRIVATE_SERVER_DETAILS", id = "id", function = new { name = "pet_action", arguments = "{}" } } } }, "tool_calls") + Done,
            Chunk(new { tool_calls = new[] { new { index = 0, id = "id", function = new { name = "pet_action", arguments = "{}" } } } }, "length") + Done,
            Chunk(new { tool_calls = new[] { new { index = 0, id = "id", function = new { name = "pet_action", arguments = "{}" } } } }) + Done
        })
        {
            using var client = Client(new FakeHandler((_, _) => Task.FromResult(Sse(invalid))), tools: true);
            var emitted = new List<OllamaChatDelta>();
            bool failed = await Fails(async () => { await foreach (var delta in client.ChatWithToolsAsync(Model, Messages, Tools, default)) emitted.Add(delta); });
            check(failed && emitted.All(d => !d.Done && (d.ToolCalls?.Count ?? 0) == 0), "invalid_or_incomplete_tools_never_reach_action_execution");
        }
        var notSent = new FakeHandler((_, _) => throw new InvalidOperationException("Must not send"));
        using (var client = Client(notSent, tools: true))
        {
            check(await Fails(() => Collect(client, history.Take(2).ToArray(), Tools)) &&
                await Fails(() => Collect(client, new[] { new OllamaMessage("tool", "orphan", ToolName: "pet_action") }, Tools)) && notSent.Calls == 0,
                "unresolved_and_orphan_tool_history_rejected_locally");
        }
        string json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = "Whole answer" }, finish_reason = "stop" } }, usage = new { completion_tokens = 2 } });
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(Json(json)))))
        {
            var result = await Collect(client);
            check(result.Count == 1 && result[0].Done && result[0].Content == "Whole answer" && result[0].EvalCount == 2,
                "non_streaming_compatible_response_preserves_answer_and_usage");
        }
    }

    private static async Task CheckErrors(Action<bool, string> check)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests,
            HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError, HttpStatusCode.TemporaryRedirect })
        {
            var handler = new FakeHandler((_, _) =>
            {
                var response = Json("PRIVATE_SERVER_DETAILS " + DummyKey, status);
                response.Headers.Location = new Uri("https://do-not-follow.invalid/" + DummyKey);
                return Task.FromResult(response);
            });
            using var client = Client(handler);
            bool safe = await Fails(() => Collect(client));
            check(safe && handler.Calls == 1, "http_errors_are_safe_and_redirect_is_not_followed_" + (int)status);
            if (status == HttpStatusCode.Unauthorized)
                check(await Fails(() => client.ListModelsAsync(default)) && !client.ModelsListedRemotely, "authentication_failure_cannot_fall_back_to_successful_model_listing");
        }
        using (var client = Client(new FakeHandler((_, _) => throw new HttpRequestException("PRIVATE_SERVER_DETAILS " + DummyKey))))
            check(await Fails(() => Collect(client)), "network_exception_details_are_not_exposed");
        using (var client = new OpenAiCompatibleClient(Endpoint, () => throw new Exception(DummyKey), Model, false, false,
            new FakeHandler((_, _) => throw new InvalidOperationException("Must not send"))))
            check(await Fails(() => Collect(client)), "credential_storage_exception_is_sanitized");
        foreach (string data in new[] { "PRIVATE_SERVER_DETAILS " + DummyKey, "{\"error\":{\"message\":\"PRIVATE_SERVER_DETAILS\"}}",
            "{\"choices\":[null]}", "{\"choices\":[{\"index\":\"PRIVATE_SERVER_DETAILS\",\"delta\":{}}]}",
            "{\"choices\":[{\"index\":0,\"delta\":{\"content\":42}}]}" })
        {
            using var client = Client(new FakeHandler((_, _) => Task.FromResult(Sse("data: " + data + "\n\n" + Done))));
            check(await Fails(() => Collect(client)), "malformed_stream_error_is_safe");
        }
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(Sse(Chunk(new { content = "partial" }) + Done)))))
            check(await Fails(() => Collect(client)), "truncated_response_is_not_silently_completed");
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(Sse("data: " + new string('x', 1024 * 1024 + 1))))))
            check(await Fails(() => Collect(client)), "oversized_sse_line_is_bounded");
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(Json("{\"choices\":[null]}")))))
            check(await Fails(() => Collect(client)), "malformed_json_fallback_is_safe");
    }

    private static async Task CheckStreaming(Action<bool, string> check)
    {
        var stream = new StagedStream(Chunk(new { content = "early" }), Chunk(new { content = "late" }, "stop") + Done);
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }))))
        {
            await using var iterator = client.ChatAsync(Model, Messages, default).GetAsyncEnumerator();
            check(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)) && iterator.Current.Content == "early" && !stream.Released,
                "first_text_is_delivered_before_the_entire_response");
            stream.Release();
            check(await iterator.MoveNextAsync() && iterator.Current.Content == "late" && await iterator.MoveNextAsync() && iterator.Current.Done && !await iterator.MoveNextAsync(),
                "stream_finishes_once_after_remaining_data");
        }
        check(stream.Disposed, "finished_stream_is_disposed");
        var canceledStream = new StagedStream(Chunk(new { content = "early" }), Chunk(new { }, "stop") + Done);
        using (var ct = new CancellationTokenSource())
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(canceledStream) }))))
        {
            await using var iterator = client.ChatAsync(Model, Messages, ct.Token).GetAsyncEnumerator();
            check(await iterator.MoveNextAsync(), "cancel_test_receives_partial_text");
            var pending = iterator.MoveNextAsync().AsTask(); ct.Cancel();
            bool canceled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { canceled = true; }
            check(canceled, "caller_cancellation_interrupts_stream_without_fake_final_response");
        }
        check(canceledStream.Disposed, "canceling_releases_response_stream");
        var abandonedStream = new StagedStream(Chunk(new { content = "first" }), "never read");
        using (var client = Client(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(abandonedStream) }))))
        {
            await using (var iterator = client.ChatAsync(Model, Messages, default).GetAsyncEnumerator()) await iterator.MoveNextAsync();
            check(abandonedStream.Disposed, "early_consumer_stop_disposes_response_without_waiting_for_server");
        }
    }

    private const string Done = "data: [DONE]\n\n";
    private static string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\r\n\r\n";
    private static OpenAiCompatibleClient Client(HttpMessageHandler handler, Uri? endpoint = null, string model = Model, bool images = false, bool tools = false)
        => new(endpoint ?? Endpoint, () => DummyKey, model, images, tools, handler);
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static async Task<List<OllamaChatDelta>> Collect(OpenAiCompatibleClient client, IReadOnlyList<OllamaMessage>? messages = null, IReadOnlyList<OllamaToolDefinition>? tools = null)
    {
        var list = new List<OllamaChatDelta>();
        await foreach (var delta in client.ChatWithToolsAsync(Model, messages ?? Messages, tools ?? Array.Empty<OllamaToolDefinition>(), default)) list.Add(delta);
        return list;
    }
    private static async Task<bool> Fails(Func<Task> run)
    {
        try { await run(); }
        catch (OllamaException exception)
        {
            return !exception.ToString().Contains(DummyKey, StringComparison.Ordinal) && !exception.ToString().Contains("PRIVATE_SERVER_DETAILS", StringComparison.Ordinal) && exception.InnerException == null;
        }
        return false;
    }
    private static async Task<bool> Unsupported(Func<Task> run)
    { try { await run(); } catch (NotSupportedException) { return true; } return false; }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        internal int Calls; internal bool Disposed;
        internal FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { _send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return _send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class StagedStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _chunk, _offset;
        internal bool Released => _release.Task.IsCompleted;
        internal bool Disposed;
        internal StagedStream(string first, string second) { _chunks = new[] { Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second) }; }
        internal void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_chunk == _chunks.Length) return 0;
            if (_chunk == 1) await _release.Task.WaitAsync(cancellationToken);
            int count = Math.Min(buffer.Length, _chunks[_chunk].Length - _offset);
            _chunks[_chunk].AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            if (_offset == _chunks[_chunk].Length) { _chunk++; _offset = 0; }
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
