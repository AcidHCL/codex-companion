using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// All inference traffic below is handled in memory; no model/service is touched.
internal static class OllamaRequestActivityChecks
{
    internal static async Task<object> RunAsync()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Ollama activity check failed: " + name);
        }
        Uri Endpoint(int suffix) => new("http://127.0.0.1:" + (39100 + suffix) + "/");
        OllamaRequestSnapshot State(Uri endpoint) => OllamaRequestActivity.Snapshot(endpoint.AbsoluteUri);
        void Count(Uri endpoint, int inference, int maintenance, string name)
        {
            var snapshot = State(endpoint);
            Check(snapshot.ActiveInference == inference && snapshot.ActiveMaintenance == maintenance, name);
        }
        void Grace(Uri endpoint, TimeSpan expected, string name)
        {
            var snapshot = State(endpoint);
            Check(snapshot.LastFinishedAt != null && snapshot.GraceUntil != null &&
                snapshot.GraceUntil - snapshot.LastFinishedAt >= expected - TimeSpan.FromMilliseconds(50), name);
        }

        Uri grouped = Endpoint(1);
        var one = OllamaRequestActivity.Begin(grouped);
        var two = OllamaRequestActivity.Begin(new Uri("http://localhost:39101/"));
        var maintenance = OllamaRequestActivity.Begin(new Uri("http://[::1]:39101/"), maintenance: true);
        Count(grouped, 2, 1, "all_client_instances_and_loopback_aliases_share_counts");
        Count(Endpoint(2), 0, 0, "different_server_port_does_not_inherit_counts");
        two.CompleteResponse(); two.Dispose(); two.Dispose();
        Count(grouped, 1, 1, "idempotent_scope_disposal_keeps_other_calls_active");
        one.Dispose(); maintenance.CompleteResponse(); maintenance.Dispose();
        Count(grouped, 0, 0, "overlapping_inference_and_maintenance_finish_independently");
        Grace(grouped, OllamaRequestActivity.InterruptedGrace, "successful_finish_does_not_shorten_interrupted_request_grace");
        Check(State(Endpoint(2)).LastFinishedAt == null && State(Endpoint(2)).GraceUntil == null,
            "snapshot_does_not_invent_activity_for_unseen_endpoint");

        Uri metadata = Endpoint(3);
        using (var client = new OllamaClient(metadata, new Handler((request, _) =>
        {
            Count(metadata, 0, 0, "metadata_read_is_not_a_generation_or_load_request");
            return Task.FromResult(Metadata(request));
        })))
        {
            await client.ListModelsAsync(CancellationToken.None);
            await client.GetModelInfoAsync(OllamaModels.FastModel, CancellationToken.None);
            await client.ListRunningModelsAsync(CancellationToken.None);
        }
        Check(State(metadata).LastFinishedAt == null, "read_only_status_poll_does_not_reset_activity_grace");

        Uri streaming = Endpoint(4);
        using (var body = new StagedStream("{\"message\":{\"content\":\"first\"},\"done\":false}\n",
            "{\"message\":{\"content\":\"last\"},\"done\":true}\n"))
        using (var client = new OllamaClient(streaming, new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat") return Task.FromResult(Metadata(request));
            Count(streaming, 1, 0, "streaming_request_is_counted_before_response_headers");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        })))
        {
            await using var iterator = client.ChatAsync(OllamaModels.FastModel, Messages(), CancellationToken.None).GetAsyncEnumerator();
            Check(await iterator.MoveNextAsync() && !iterator.Current.Done, "first_chunk_arrives_before_completion");
            Count(streaming, 1, 0, "streaming_body_is_counted_after_headers_until_end");
            body.Release();
            Check(await iterator.MoveNextAsync() && iterator.Current.Done && !await iterator.MoveNextAsync(), "stream_consumes_confirmed_completion");
            Count(streaming, 0, 0, "finished_stream_releases_request_count");
            Grace(streaming, OllamaRequestActivity.CompletedGrace, "successful_stream_has_completed_request_grace");
        }

        Uri interrupted = Endpoint(5);
        using (var body = new StagedStream("{\"message\":{\"content\":\"first\"},\"done\":false}\n", "{\"done\":true}\n"))
        using (var client = new OllamaClient(interrupted, new Handler((request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/api/chat"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) } : Metadata(request)))))
        {
            await using (var iterator = client.ChatAsync(OllamaModels.FastModel, Messages(), CancellationToken.None).GetAsyncEnumerator())
                Check(await iterator.MoveNextAsync(), "early_consumer_stop_receives_first_chunk");
            Count(interrupted, 0, 0, "early_consumer_stop_disposes_activity");
            Grace(interrupted, OllamaRequestActivity.InterruptedGrace, "early_stop_receives_longer_grace_without_cancellation_token");
        }

        Uri canceled = Endpoint(6);
        using (var cancel = new CancellationTokenSource())
        using (var body = new StagedStream("{\"message\":{\"content\":\"first\"},\"done\":false}\n", "{\"done\":true}\n"))
        using (var client = new OllamaClient(canceled, new Handler((request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/api/chat"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) } : Metadata(request)))))
        {
            await using var iterator = client.ChatAsync(OllamaModels.FastModel, Messages(), cancel.Token).GetAsyncEnumerator();
            await iterator.MoveNextAsync();
            var pending = iterator.MoveNextAsync().AsTask();
            cancel.Cancel();
            bool wasCanceled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { wasCanceled = true; }
            Check(wasCanceled, "canceling_pending_stream_propagates_cancellation");
            Count(canceled, 0, 0, "canceled_stream_releases_activity_without_waiting_for_server");
            Grace(canceled, OllamaRequestActivity.InterruptedGrace, "canceled_stream_keeps_server_work_grace");
        }

        Uri failure = Endpoint(7);
        using (var client = new OllamaClient(failure, new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat") return Task.FromResult(Metadata(request));
            Count(failure, 1, 0, "request_is_active_when_transport_throws");
            throw new HttpRequestException("Simulated transport failure");
        })))
        {
            bool failed = false;
            try { await foreach (var _ in client.ChatAsync(OllamaModels.FastModel, Messages(), CancellationToken.None)) { } }
            catch (OllamaException) { failed = true; }
            Check(failed, "transport_failure_keeps_existing_client_error_behavior");
            Count(failure, 0, 0, "transport_failure_does_not_leak_active_count");
            Grace(failure, OllamaRequestActivity.InterruptedGrace, "transport_failure_is_treated_as_unconfirmed_completion");
        }

        Uri tools = Endpoint(8);
        using (var client = new OllamaClient(tools, new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat") return Task.FromResult(Metadata(request));
            Count(tools, 1, 0, "tool_behavior_and_search_chat_share_inference_tracking");
            return Task.FromResult(Json("{\"message\":{\"content\":\"\"},\"done\":true}\n"));
        })))
            await foreach (var _ in client.ChatWithToolsAsync(OllamaModels.FastModel, Messages(),
                new[] { new OllamaToolDefinition("lookup", "Lookup", "{\"type\":\"object\",\"properties\":{}}") }, CancellationToken.None)) { }
        Count(tools, 0, 0, "tool_chat_finishes_tracking");

        Uri loading = Endpoint(9);
        using (var client = new OllamaClient(loading, new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/generate") return Task.FromResult(Metadata(request));
            Count(loading, 0, 1, "load_or_unload_is_maintenance_not_inference");
            return Task.FromResult(Json("{\"done\":true}"));
        })))
        {
            await client.PreloadAsync(OllamaModels.FastModel, CancellationToken.None);
            Count(loading, 0, 0, "preload_confirmation_finishes_maintenance");
            await client.UnloadAsync(OllamaModels.FastModel, CancellationToken.None);
            Count(loading, 0, 0, "unload_confirmation_finishes_maintenance");
        }

        Uri summary = Endpoint(10);
        using (var client = new OllamaClient(summary, new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat") return Task.FromResult(Metadata(request));
            Count(summary, 1, 0, "separate_stateless_summary_client_counts_as_pet_inference");
            return Task.FromResult(Json("{\"message\":{\"role\":\"assistant\",\"content\":\"summary\"},\"done\":true}"));
        })))
            Check(await client.SummarizeResidentAsync(OllamaModels.FastModel, "Summarize", "Source", CancellationToken.None) == "summary",
                "summary_response_is_unchanged");
        Count(summary, 0, 0, "summary_completion_releases_activity");

        return new { passed = true, checks };
    }

    private static OllamaMessage[] Messages() => new[] { new OllamaMessage("user", "Test") };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Metadata(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/api/tags" => Json("{\"models\":[{\"name\":\"qwen3.5:9b-q8_0\"}]}"),
        "/api/show" => Json("{\"capabilities\":[\"completion\",\"tools\"],\"thinking\":{\"values\":[false]}}"),
        "/api/ps" => Json("{\"models\":[{\"name\":\"qwen3.5:9b-q8_0\",\"context_length\":8192}]}"),
        _ => throw new InvalidOperationException("Unexpected test request path")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class StagedStream(string first, string last) : Stream
    {
        private readonly byte[][] _chunks = { Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(last) };
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _chunk, _offset;
        internal void Release() => _released.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_chunk >= _chunks.Length) return 0;
            if (_chunk == 1) await _released.Task.WaitAsync(cancellationToken);
            int count = Math.Min(buffer.Length, _chunks[_chunk].Length - _offset);
            _chunks[_chunk].AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == _chunks[_chunk].Length) { _chunk++; _offset = 0; }
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
