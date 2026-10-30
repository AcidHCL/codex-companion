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

// In-memory HTTP only. No server startup, model downloads, inference, or user settings.
internal static class OllamaClientChecks
{
    public static async Task<object> RunAsync()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Ollama client check failed: " + name);
        }

        Check(OllamaModels.Supported.Count == 2 && OllamaModels.Normalize(null) == OllamaModels.FastModel && OllamaModels.Normalize("remote:cloud") == OllamaModels.FastModel && OllamaModels.Normalize("llama3.1:8b") == "llama3.1:8b", "friendly_recommended_labels_and_dynamic_model_settings");
        using (var handler = OllamaClient.CreateLocalHandler())
            Check(!handler.UseProxy && !handler.AllowAutoRedirect && !handler.UseCookies && handler.Credentials == null && handler.SslOptions.RemoteCertificateValidationCallback == null && handler.ConnectTimeout == TimeSpan.FromSeconds(5), "real_transport_disables_proxy_redirect_cookies_credentials_and_tls_bypass");
        Check(OllamaClient.LocalEndpoint.AbsoluteUri == "http://127.0.0.1:11434/" && OllamaClient.LocalEndpoint.IsLoopback, "default_endpoint_remains_loopback");
        Check(OllamaEndpoint.Parse(" 192.168.1.20 ").AbsoluteUri == "http://192.168.1.20:11434/" && OllamaEndpoint.Parse("ollama-box:12345/").Port == 12345 && OllamaEndpoint.Parse("[::1]").Port == 11434, "bare_host_ipv6_and_explicit_port_parse");
        Check(OllamaEndpoint.Parse("http://ollama-box").Port == 80 && OllamaEndpoint.Parse("https://ollama.example.com/").Port == 443 && OllamaEndpoint.Parse("https://ollama.example.com:11434").Port == 11434, "explicit_http_and_https_use_standard_ports_unless_supplied");
        foreach (string address in new[] { "", "ftp://example.com", "http://user:pass@example.com", "http://example.com/api", "http://example.com/api/..", "http://example.com/?x=1", "http://example.com/#x", "http://example.com\\api", "http://example.com:0", "http://example.com:65536", "http://example.com/%2f", "http://example.com\r\n" + "x" })
        {
            bool rejected = false;
            try { OllamaEndpoint.Parse(address); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "reject_invalid_endpoint_" + checks.Count);
        }
        Check(OllamaEndpoint.Normalize(null) == OllamaEndpoint.DefaultAddress && OllamaEndpoint.Normalize("http://example.com/api") == OllamaEndpoint.DefaultAddress && OllamaEndpoint.Normalize("https://ollama.example.com/") == "https://ollama.example.com", "settings_endpoint_normalization_is_canonical_with_safe_fallback");

        var received = new List<string>();
        var chatHandler = new FakeHandler(async (request, ct) =>
        {
            Check(request.RequestUri?.AbsoluteUri == "http://127.0.0.1:11434/api/chat" && request.Method == HttpMethod.Post, "chat_uses_only_local_api_chat");
            string body = await request.Content!.ReadAsStringAsync(ct);
            received.Add(body);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Check(root.GetProperty("model").GetString() == OllamaModels.QualityModel && root.GetProperty("stream").GetBoolean() && !root.GetProperty("think").GetBoolean(), "exact_model_streaming_and_thinking_disabled");
            Check(root.GetProperty("keep_alive").GetString() == "5m" && root.GetProperty("options").GetProperty("num_ctx").GetInt32() == 8192 && root.GetProperty("options").GetProperty("num_predict").GetInt32() == 1024, "bounded_context_output_and_keep_alive");
            Check(!root.TryGetProperty("tools", out _), "ordinary_chat_omits_tools_without_changing_existing_payload");
            var messages = root.GetProperty("messages");
            Check(messages.GetArrayLength() == 4 && messages[0].GetProperty("role").GetString() == "system" && messages[1].GetProperty("role").GetString() == "user" && messages[2].GetProperty("role").GetString() == "assistant" && messages[3].GetProperty("role").GetString() == "user", "history_keeps_role_and_message_order");
            Check(messages[1].GetProperty("content").GetString() == "看图" && messages[1].GetProperty("images")[0].GetString() == "AQID" && !messages[0].TryGetProperty("images", out _), "images_are_in_original_user_message_only");
            return Json("\r\n{\"message\":{\"content\":\"你\"},\"done\":false}\n{\"message\":{\"content\":\"好\"},\"done\":false}\n{\"message\":{\"content\":\"！\"},\"done\":true,\"eval_count\":3,\"eval_duration\":150000000}");
        });
        using (var client = new OllamaClient(chatHandler))
        {
            var deltas = await CollectAsync(client, OllamaModels.QualityModel, new[]
            {
                new OllamaMessage("system", "你是桌宠"),
                new OllamaMessage("user", "看图", new[] { "AQID" }),
                new OllamaMessage("assistant", "我看到了"),
                new OllamaMessage("user", "你好")
            });
            Check(deltas.Count == 3 && string.Concat(deltas.Select(delta => delta.Content)) == "你好！" && !deltas[0].Done && !deltas[1].Done && deltas[2].Done, "all_deltas_and_final_content_survive_chunking_and_no_final_newline");
            Check(deltas[2].EvalCount == 3 && deltas[2].TokensPerSecond == 20, "server_timing_converts_nanoseconds_to_tokens_per_second");
        }
        Check(chatHandler.Disposed, "disposing_client_disposes_owned_transport");

        var stagedStream = new StagedStream("{\"message\":{\"content\":\"先到\"},\"done\":false}\n", "{\"message\":{\"content\":\"后到\"},\"done\":true}\n");
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stagedStream) }))))
        {
            await using var enumerator = client.ChatAsync(OllamaModels.FastModel, UserMessage(), CancellationToken.None).GetAsyncEnumerator();
            bool first = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(first && enumerator.Current.Content == "先到" && !stagedStream.SecondChunkReleased, "first_delta_arrives_before_server_finishes_stream");
            stagedStream.ReleaseSecondChunk();
            Check(await enumerator.MoveNextAsync() && enumerator.Current.Done && enumerator.Current.Content == "后到", "stream_finishes_after_remaining_chunk_arrives");
        }

        using (var client = new OllamaClient(new FakeHandler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "http://127.0.0.1:11434/api/tags", "discovery_only_requests_local_tags");
            return Task.FromResult(Json("{\"models\":[{\"name\":\"llama3.1:8b\"},{\"name\":\"qwen3.8:27b-q4_K_M\"},{\"name\":\"qwen3.5:9b-q8_0\"},{\"name\":\"qwen3.5:9b-q8_0\"}]}"));
        }, autoMetadata: false)))
            Check((await client.ListModelsAsync(CancellationToken.None)).SequenceEqual(new[] { "llama3.1:8b", OllamaModels.FastModel, OllamaModels.QualityModel }), "discovery_returns_all_valid_installed_models_without_duplicates");
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"models\":[{\"name\":\"qwen3.5:9b-q8_0\",\"remote_model\":\"remote\"},{\"name\":\"qwen3.8:27b-q4_K_M\",\"remote_host\":\"https://example.invalid\"},{\"name\":\"gpt-oss:20b-cloud\"},{\"name\":\"https://example.invalid/model\"},{\"name\":\"../model\"}]}")), autoMetadata: false)))
            Check((await client.ListModelsAsync(CancellationToken.None)).Count == 0, "cloud_metadata_never_counts_as_installed_local_model");

        var runningHandler = new FakeHandler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/ps" && request.Content == null,
                "residency_only_reads_ps_without_inference_payload");
            return Task.FromResult(Json("""
                {"models":[
                  {"name":"qwen3.5:9b-q8_0","size":9000000000,"size_vram":8000000000,"context_length":8192,"expires_at":"2026-10-29T10:00:00Z"},
                  {"name":"qwen3.5:9b-q8_0","size":1},
                  {"model":"llama3.1:8b","size":-1,"size_vram":"unknown","context_length":999999999999,"expires_at":"invalid"},
                  {"name":"cpu:latest","size_vram":0,"context_length":0},
                  {"name":"remote:cloud"},{"name":"../invalid"},{"name":"remote-model","remote_host":"https://remote.invalid"}
                ]}
                """));
        }, autoMetadata: false);
        using (var client = new OllamaClient(runningHandler))
        {
            var running = await client.ListRunningModelsAsync(CancellationToken.None);
            var qwen = running.Single(item => item.Name == OllamaModels.FastModel);
            var missing = running.Single(item => item.Name == "llama3.1:8b");
            Check(runningHandler.Calls == 1 && running.Count == 3 && qwen.SizeBytes == 9000000000 && qwen.VramBytes == 8000000000 &&
                qwen.ContextLength == 8192 && qwen.ExpiresAt == DateTimeOffset.Parse("2026-10-29T10:00:00Z"), "residency_parses_64_bit_memory_and_expiry_and_filters_invalid_duplicates");
            Check(missing.SizeBytes == null && missing.VramBytes == null && missing.ContextLength == null && missing.ExpiresAt == null,
                "residency_missing_negative_and_invalid_fields_are_unknown");
            Check(running.Single(item => item.Name == "cpu:latest").VramBytes == 0 && running[0].ContextLength == null,
                "residency_preserves_real_zero_vram_without_fabricating_context");
        }
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"models\":[]}")), autoMetadata: false)))
            Check((await client.ListRunningModelsAsync(CancellationToken.None)).Count == 0, "empty_residency_means_no_loaded_models");
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound)), autoMetadata: false)))
        {
            bool unsupported = false;
            try { await client.ListRunningModelsAsync(CancellationToken.None); } catch (NotSupportedException) { unsupported = true; }
            Check(unsupported, "missing_ps_reports_unsupported_instead_of_unloaded");
        }
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"models\":null}")), autoMetadata: false)))
            Check((await FailureAsync(() => client.ListRunningModelsAsync(CancellationToken.None))).Length > 0,
                "malformed_ps_is_not_treated_as_zero_memory");
        using (var client = new OllamaClient(new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }, autoMetadata: false)))
        {
            using var cancelled = new CancellationTokenSource(30);
            Check(await IsCanceledAsync(() => client.ListRunningModelsAsync(cancelled.Token)), "residency_poll_honors_cancellation");
        }

        var blockedHandler = new FakeHandler((_, _) => throw new InvalidOperationException("must not send"));
        using (var client = new OllamaClient(blockedHandler))
        {
            string invalidModel = await FailureAsync(() => CollectAsync(client, "qwen3.8:27b-cloud", UserMessage()));
            string invalidUnload = await FailureAsync(() => client.UnloadAsync("https://example.invalid/model", CancellationToken.None));
            string imageUrl = await FailureAsync(() => CollectAsync(client, OllamaModels.FastModel, new[] { new OllamaMessage("user", "image", new[] { "https://example.invalid/image.png" }) }));
            Check(blockedHandler.Calls == 0 && invalidModel.Contains("模型名称") && invalidUnload.Contains("模型名称") && imageUrl.Contains("图片数据"), "cloud_model_url_and_image_url_rejected_before_http");
        }

        using (var client = new OllamaClient(new FakeHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            Check(request.RequestUri?.AbsolutePath == "/api/generate" && root.GetProperty("model").GetString() == OllamaModels.FastModel &&
                root.GetProperty("prompt").GetString() == "" && !root.GetProperty("stream").GetBoolean(), "manual_preload_uses_empty_prompt_for_selected_installed_model");
            Check(!root.TryGetProperty("keep_alive", out _) && !root.TryGetProperty("messages", out _) &&
                root.GetProperty("options").EnumerateObject().Count() == 1 && root.GetProperty("options").GetProperty("num_ctx").GetInt32() == OllamaClient.ContextTokens,
                "manual_preload_preserves_ollama_retention_and_matches_existing_chat_context_without_chat_content");
            return Json("{\"done\":true,\"done_reason\":\"load\"}");
        })))
            await client.PreloadAsync(OllamaModels.FastModel, CancellationToken.None);
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"done\":false}")))))
            Check((await FailureAsync(() => client.PreloadAsync(OllamaModels.FastModel, CancellationToken.None))).Contains("未确认加载"), "manual_preload_requires_completion_acknowledgement");
        var invalidPreloadHandler = new FakeHandler((_, _) => throw new InvalidOperationException("must_not_send"));
        using (var client = new OllamaClient(invalidPreloadHandler))
        {
            await FailureAsync(() => client.PreloadAsync("remote:cloud", CancellationToken.None));
            Check(invalidPreloadHandler.Calls == 0, "manual_preload_rejects_cloud_model_before_network");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool cancellationObserved = false;
            try { await client.PreloadAsync(OllamaModels.FastModel, cancelled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Check(cancellationObserved && invalidPreloadHandler.Calls == 0, "manual_preload_honors_cancellation_before_metadata_or_loading");
        }

        using (var client = new OllamaClient(new FakeHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            Check(request.RequestUri?.AbsoluteUri == "http://127.0.0.1:11434/api/generate" && root.GetProperty("model").GetString() == OllamaModels.FastModel && root.GetProperty("keep_alive").GetInt32() == 0 && !root.GetProperty("stream").GetBoolean() && !root.TryGetProperty("prompt", out _), "unload_does_not_send_prompt_or_start_generation");
            return Json("{\"done\":true,\"done_reason\":\"unload\"}");
        })))
            await client.UnloadAsync(OllamaModels.FastModel, CancellationToken.None);

        using (var client = new OllamaClient(new FakeHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (document.RootElement.GetProperty("model").GetString() == OllamaModels.FastModel)
                return Json("{\"error\":\"model not found\"}", HttpStatusCode.NotFound);
            return Json("{\"message\":{\"content\":\"可以聊天\"},\"done\":true}\n");
        })))
        {
            await FailureAsync(() => CollectAsync(client, OllamaModels.FastModel, UserMessage()));
            await client.UnloadAsync(OllamaModels.FastModel, CancellationToken.None);
            var deltas = await CollectAsync(client, OllamaModels.QualityModel, UserMessage());
            Check(deltas.Count == 1 && deltas[0].Done && deltas[0].Content == "可以聊天", "missing_previous_model_unload_is_idempotent_and_does_not_block_switch");
        }
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("server failed", HttpStatusCode.InternalServerError)))))
        {
            string message = await FailureAsync(() => client.UnloadAsync(OllamaModels.FastModel, CancellationToken.None));
            Check(message.Contains("HTTP 500"), "unload_still_rejects_other_http_failures");
        }

        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"error\":\"model not found: PRIVATE_IMAGE_BYTES\"}", HttpStatusCode.NotFound)))))
        {
            string message = await FailureAsync(() => CollectAsync(client, OllamaModels.FastModel, UserMessage()));
            Check(message.Contains("尚未安装") && !message.Contains("PRIVATE_IMAGE_BYTES"), "missing_model_has_friendly_sanitized_error");
        }
        using (var client = new OllamaClient(new FakeHandler((_, _) => throw new HttpRequestException("PRIVATE_SOCKET_DETAIL"), autoMetadata: false)))
        {
            string message = await FailureAsync(() => client.ListModelsAsync(CancellationToken.None));
            Check(message.Contains("地址和端口") && !message.Contains("PRIVATE_SOCKET_DETAIL"), "service_unavailable_has_friendly_sanitized_error");
        }
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("PRIVATE_SERVER_BODY", HttpStatusCode.TemporaryRedirect)))))
        {
            string message = await FailureAsync(() => CollectAsync(client, OllamaModels.FastModel, UserMessage()));
            Check(message.Contains("已阻止") && !message.Contains("PRIVATE_SERVER_BODY"), "redirect_response_is_rejected");
        }

        await CheckPartialFailureAsync("{\"message\":{\"content\":\"部分\"},\"done\":false}\n{\"error\":\"PRIVATE_IMAGE_BYTES\"}\n", "stream_error_after_partial_answer_never_reports_done", checks);
        await CheckPartialFailureAsync("{\"message\":{\"content\":\"部分\"},\"done\":false}\n", "truncated_stream_after_partial_answer_never_reports_done", checks);
        await CheckPartialFailureAsync("{\"message\":{\"content\":\"部分\"},\"done\":false}\nnot json\n", "malformed_stream_after_partial_answer_never_reports_done", checks);

        var boundedBody = new CountingStream(Encoding.UTF8.GetBytes(new string('x', 100_000)));
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StreamContent(boundedBody) }), autoMetadata: false)))
        {
            string message = await FailureAsync(() => client.ListModelsAsync(CancellationToken.None));
            Check(message.Contains("HTTP 500") && boundedBody.BytesRead <= 20 * 1024, "http_error_body_read_is_bounded");
        }

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            var handler = new FakeHandler((_, _) => throw new InvalidOperationException("must not send"));
            using var client = new OllamaClient(handler);
            Check(await IsCanceledAsync(() => CollectAsync(client, OllamaModels.FastModel, UserMessage(), cts.Token)) && handler.Calls == 0, "pre_canceled_chat_never_sends_request");
        }
        using (var cts = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new OllamaClient(new FakeHandler(async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Json("{}");
            }, autoMetadata: false));
            Task<IReadOnlyList<string>> operation = client.ListModelsAsync(cts.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cts.Cancel();
            Check(await IsCanceledAsync(() => operation), "cancellation_interrupts_pending_http_headers");
        }
        var cancelStream = new StagedStream("{\"message\":{\"content\":\"部分\"},\"done\":false}\n", "{\"done\":true}\n");
        using (var cts = new CancellationTokenSource())
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(cancelStream) }))))
        {
            await using var enumerator = client.ChatAsync(OllamaModels.FastModel, UserMessage(), cts.Token).GetAsyncEnumerator();
            Check(await enumerator.MoveNextAsync() && !enumerator.Current.Done, "partial_answer_available_before_cancel");
            Task<bool> pending = enumerator.MoveNextAsync().AsTask();
            cts.Cancel();
            Check(await IsCanceledAsync(() => pending), "cancellation_interrupts_pending_stream_read_without_fake_done");
        }
        Check(cancelStream.WasDisposed, "canceled_stream_is_disposed");

        var endpoint = OllamaEndpoint.Parse("https://ollama.test.invalid:21434");
        var showBodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["llama3.1:8b"] = "{\"capabilities\":[\"completion\"]}",
            ["gpt-oss:20b"] = "{\"capabilities\":[\"completion\",\"thinking\"],\"thinking\":{\"values\":[\"low\",\"medium\",\"high\"],\"default\":\"medium\"}}",
            ["nomic-embed-text:latest"] = "{\"capabilities\":[\"embedding\"]}",
            ["my-org/vision-model:q4"] = "{\"capabilities\":[\"completion\",\"vision\"]}",
            ["legacy:latest"] = "{}",
            ["no-show:latest"] = "{}",
            ["remote-alias:latest"] = "{\"capabilities\":[\"completion\"],\"remote_host\":\"https://cloud.example.invalid\"}"
        };
        var showCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = new List<string>();
        var requests = new List<JsonElement>();
        using (var client = new OllamaClient(endpoint, new FakeHandler(async (request, ct) =>
        {
            Check(request.RequestUri?.GetLeftPart(UriPartial.Authority) == endpoint.GetLeftPart(UriPartial.Authority), "requests_reach_only_configured_authority_" + paths.Count);
            string path = request.RequestUri!.AbsolutePath; paths.Add(path);
            if (path == "/api/tags") return Json(JsonSerializer.Serialize(new { models = showBodies.Keys.Select(name => new { name }).ToArray() }));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            string model = body.GetProperty("model").GetString()!;
            if (path == "/api/show")
            {
                showCounts[model] = showCounts.GetValueOrDefault(model) + 1;
                Check(!body.TryGetProperty("messages", out _) && !body.TryGetProperty("prompt", out _), "capability_request_has_no_chat_or_image_payload_" + showCounts.Values.Sum());
                return Json(showBodies[model], model == "no-show:latest" ? HttpStatusCode.NotFound : HttpStatusCode.OK);
            }
            if (path == "/api/chat")
            {
                requests.Add(body.Clone());
                return Json("{\"message\":{\"content\":\"回答\"},\"done\":true}\n");
            }
            throw new InvalidOperationException("Unexpected fixture path.");
        }, autoMetadata: false)))
        {
            Check(client.Endpoint == endpoint, "client_exposes_normalized_configured_endpoint");
            var models = await client.ListModelsAsync(CancellationToken.None);
            Check(models.Contains("my-org/vision-model:q4") && models.Contains("llama3.1:8b") && models.Count == showBodies.Count, "custom_and_namespaced_models_are_discovered");
            var llama = await client.GetModelInfoAsync("llama3.1:8b", CancellationToken.None);
            var llamaAgain = await client.GetModelInfoAsync("llama3.1:8b", CancellationToken.None);
            Check(llama == new OllamaModelCapabilities(true, false, false, false) && llamaAgain == llama && showCounts["llama3.1:8b"] == 1, "text_capabilities_are_explicit_and_cached_without_model_load");
            var vision = await client.GetModelInfoAsync("my-org/vision-model:q4", CancellationToken.None);
            Check(vision.SupportsImages == true && paths.All(path => path is "/api/tags" or "/api/show"), "discovery_and_show_do_not_load_or_generate");
            int beforeRejected = requests.Count;
            string textImage = await FailureAsync(() => CollectAsync(client, "llama3.1:8b", new[] { new OllamaMessage("user", "看图", new[] { "AQID" }) }));
            string embedding = await FailureAsync(() => CollectAsync(client, "nomic-embed-text:latest", UserMessage()));
            string cloud = await FailureAsync(() => CollectAsync(client, "remote-alias:latest", UserMessage()));
            string missing = await FailureAsync(() => CollectAsync(client, "missing:latest", UserMessage()));
            Check(textImage.Contains("不支持图片") && embedding.Contains("不支持聊天") && cloud.Contains("云端") && missing.Contains("尚未安装") && requests.Count == beforeRejected, "nonvision_embedding_remote_and_missing_models_do_not_generate");
            await CollectAsync(client, "llama3.1:8b", UserMessage());
            Check(!requests[^1].TryGetProperty("think", out _), "ordinary_chat_omits_unsupported_think_parameter");
            await CollectAsync(client, "gpt-oss:20b", UserMessage());
            Check(requests[^1].GetProperty("think").GetString() == "low", "named_thinking_model_uses_supported_low_effort");
            var legacy = await client.GetModelInfoAsync("legacy:latest", CancellationToken.None);
            await CollectAsync(client, "legacy:latest", UserMessage());
            Check(legacy == OllamaModelCapabilities.Unknown && !requests[^1].TryGetProperty("think", out _), "absent_capabilities_stay_unknown_and_omit_think");
            var noShow = await client.GetModelInfoAsync("no-show:latest", CancellationToken.None);
            await CollectAsync(client, "no-show:latest", UserMessage());
            Check(noShow == OllamaModelCapabilities.Unknown && !requests[^1].TryGetProperty("think", out _), "legacy_show_404_does_not_block_confirmed_installed_model");
            int beforeRefresh = showCounts["llama3.1:8b"];
            await client.ListModelsAsync(CancellationToken.None);
            await client.GetModelInfoAsync("llama3.1:8b", CancellationToken.None);
            Check(showCounts["llama3.1:8b"] == beforeRefresh + 1, "manual_model_refresh_invalidates_capability_cache");
            Check(paths.All(path => path is "/api/tags" or "/api/show" or "/api/chat"), "dynamic_client_uses_only_expected_api_paths");
        }

        using (var client = new OllamaClient(endpoint, new FakeHandler((_, _) => Task.FromResult(Json("redirect", HttpStatusCode.TemporaryRedirect)), autoMetadata: false)))
            Check((await FailureAsync(() => client.GetModelInfoAsync("llama3.1:8b", CancellationToken.None))).Contains("已阻止"), "configured_endpoint_metadata_redirect_is_rejected");

        using (var cts = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new OllamaClient(endpoint, new FakeHandler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/tags") return Json("{\"models\":[{\"name\":\"llama3.1:8b\"}]}");
                started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Json("{}");
            }, autoMetadata: false));
            Task<OllamaModelCapabilities> pending = client.GetModelInfoAsync("llama3.1:8b", cts.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cts.Cancel();
            Check(await IsCanceledAsync(() => pending), "capability_lookup_respects_cancellation");
        }

        await CheckToolsAsync(Check);
        return new { passed = true, assertions = checks.Count, checks, syntheticOnly = true, networkRequestsSent = 0, modelDownloads = 0, gpuInferenceRequests = 0 };
    }

    private static async Task CheckToolsAsync(Action<bool, string> check)
    {
        var definitions = new[]
        {
            new OllamaToolDefinition("web_search", "Search public web information", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}"),
            new OllamaToolDefinition("read_webpage", "Read a public webpage", "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}")
        };
        var requests = new List<JsonElement>();
        using (var client = new OllamaClient(new FakeHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            requests.Add(document.RootElement.Clone());
            if (requests.Count == 1)
                return Json("""
                    {"message":{"role":"assistant","thinking":"Need "},"done":false}
                    {"message":{"role":"assistant","thinking":"sources.","content":"查询中"},"done":false}
                    {"message":{"role":"assistant","tool_calls":[{"function":{"index":0,"name":"web_search","arguments":{"query":"实时 信息"}}}]},"done":false}
                    {"message":{"role":"assistant","tool_calls":[{"type":"function","function":{"index":1,"name":"read_webpage","arguments":{"url":"https://example.org/"}}}]},"done":true,"eval_count":10}
                    """);
            return Json("{\"message\":{\"content\":\"根据来源回答。\"},\"done\":true}\n");
        })))
        {
            var info = await client.GetModelInfoAsync(OllamaModels.FastModel, CancellationToken.None);
            check(info.SupportsTools == true, "native_tools_capability_is_discovered");
            var first = await CollectToolsAsync(client, OllamaModels.FastModel, UserMessage(), definitions);
            var calls = first.SelectMany(delta => delta.ToolCalls ?? Array.Empty<OllamaToolCall>()).ToArray();
            string thinking = string.Concat(first.Select(delta => delta.Thinking));
            string content = string.Concat(first.Select(delta => delta.Content));
            check(first.Count == 4 && first[^1].Done && first[^1].Content.Length == 0 && calls.Length == 2, "streaming_tool_only_final_is_valid_and_calls_survive_multiple_chunks");
            check(thinking == "Need sources." && content == "查询中" && calls[0].Name == "web_search" && calls[1].Name == "read_webpage", "thinking_content_and_ordered_tool_calls_are_preserved_independently");
            using (var argument = JsonDocument.Parse(calls[0].ArgumentsJson))
                check(argument.RootElement.GetProperty("query").GetString() == "实时 信息", "unicode_tool_arguments_remain_json_objects");
            var firstPayload = requests[0];
            check(firstPayload.GetProperty("tools").GetArrayLength() == 2 && firstPayload.GetProperty("tools")[0].GetProperty("type").GetString() == "function" &&
                firstPayload.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").ValueKind == JsonValueKind.Object, "tools_are_serialized_as_native_function_schemas_not_json_strings");
            var nextMessages = new[]
            {
                UserMessage()[0],
                new OllamaMessage("assistant", content, ToolCalls: calls, Thinking: thinking),
                new OllamaMessage("tool", "搜索摘要", ToolName: "web_search"),
                new OllamaMessage("tool", "网页正文", ToolName: "read_webpage")
            };
            var final = await CollectToolsAsync(client, OllamaModels.FastModel, nextMessages, definitions);
            var sent = requests[^1].GetProperty("messages");
            check(final[^1].Done && final[^1].Content == "根据来源回答。", "tool_round_finishes_with_normal_answer");
            check(sent[1].GetProperty("thinking").GetString() == thinking && sent[1].GetProperty("content").GetString() == content &&
                sent[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").ValueKind == JsonValueKind.Object &&
                sent[1].GetProperty("tool_calls")[1].GetProperty("function").GetProperty("index").GetInt32() == 1,
                "followup_assistant_keeps_accumulated_thinking_content_and_structured_calls");
            check(sent[2].GetProperty("role").GetString() == "tool" && sent[2].GetProperty("tool_name").GetString() == "web_search" &&
                sent[3].GetProperty("content").GetString() == "网页正文" && !sent[2].TryGetProperty("images", out _), "tool_results_keep_native_role_name_and_content_without_images");
            await CollectAsync(client, OllamaModels.FastModel, nextMessages);
            check(!requests[^1].TryGetProperty("tools", out _) && requests[^1].GetProperty("messages")[2].GetProperty("role").GetString() == "tool", "final_round_can_keep_tool_history_with_tools_disabled");
        }

        int unsupportedChats = 0;
        using (var client = new OllamaClient(new FakeHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tags") return Task.FromResult(Json("{\"models\":[{\"name\":\"plain:latest\"}]}"));
            if (path == "/api/show") return Task.FromResult(Json("{\"capabilities\":[\"completion\"]}"));
            unsupportedChats++; return Task.FromResult(Json("{}"));
        }, autoMetadata: false)))
        {
            string failure = await FailureAsync(() => CollectToolsAsync(client, "plain:latest", UserMessage(), definitions));
            check(failure.Contains("不支持工具调用") && unsupportedChats == 0, "explicit_unsupported_tools_are_rejected_before_inference");
        }
        using (var client = new OllamaClient(new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags") return Task.FromResult(Json("{\"models\":[{\"name\":\"legacy:latest\"}]}"));
            if (request.RequestUri!.AbsolutePath == "/api/show") return Task.FromResult(Json("{}"));
            return Task.FromResult(Json("{\"message\":{\"content\":\"兼容\"},\"done\":true}\n"));
        }, autoMetadata: false)))
        {
            var info = await client.GetModelInfoAsync("legacy:latest", CancellationToken.None);
            var result = await CollectToolsAsync(client, "legacy:latest", UserMessage(), definitions);
            check(info.SupportsTools == null && result[^1].Done, "legacy_unknown_tool_capability_does_not_falsely_reject_request");
        }

        var invalidHandler = new FakeHandler((_, _) => throw new InvalidOperationException("must not send"));
        using (var client = new OllamaClient(invalidHandler))
        {
            foreach (var invalid in new[]
            {
                new OllamaToolDefinition("../execute", "bad", "{\"type\":\"object\"}"),
                new OllamaToolDefinition("web_search", "bad", "[]"),
                new OllamaToolDefinition("web_search", "bad", "{\"type\":\"string\"}"),
                new OllamaToolDefinition("web_search", "bad", new string('x', 33 * 1024))
            }) await FailureAsync(() => CollectToolsAsync(client, OllamaModels.FastModel, UserMessage(), new[] { invalid }));
            await FailureAsync(() => CollectToolsAsync(client, OllamaModels.FastModel, UserMessage(), new[] { definitions[0], definitions[0] }));
            foreach (var invalid in new[]
            {
                new OllamaMessage("tool", "result"),
                new OllamaMessage("user", "content", ToolCalls: new[] { new OllamaToolCall("web_search", "{}") }),
                new OllamaMessage("tool", "result", Images: new[] { "AQID" }, ToolName: "web_search"),
                new OllamaMessage("assistant", "", ToolCalls: new[] { new OllamaToolCall("web_search", "[]") }),
                new OllamaMessage("assistant", "", ToolName: "web_search")
            }) await FailureAsync(() => CollectToolsAsync(client, OllamaModels.FastModel, new[] { invalid }, definitions));
            check(invalidHandler.Calls == 0, "malformed_tool_schemas_and_history_are_rejected_before_any_http");
        }

        int rejectedCalls = 0;
        foreach (string malformed in new[]
        {
            "{\"tool_calls\":{}}",
            "{\"tool_calls\":[{\"function\":{\"name\":\"web_search\",\"arguments\":\"{}\"}}]}",
            "{\"tool_calls\":[{\"function\":{\"name\":\"web_search\",\"arguments\":[]}}]}",
            "{\"tool_calls\":[{\"function\":{\"name\":\"../execute\",\"arguments\":{}}}]}",
            "{\"tool_calls\":[{\"type\":\"shell\",\"function\":{\"name\":\"web_search\",\"arguments\":{}}}]}",
            "{\"tool_calls\":[{\"function\":{\"name\":\"web_search\"}}]}",
            JsonSerializer.Serialize(new { tool_calls = new[] { new { function = new { name = "web_search", arguments = new { query = new string('x', 33 * 1024) } } } } }),
            "{\"thinking\":123}"
        })
        {
            using var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"message\":" + malformed + ",\"done\":true}\n"))));
            string failure = await FailureAsync(() => CollectToolsAsync(client, OllamaModels.FastModel, UserMessage(), definitions));
            if (failure.Contains("无法识别")) rejectedCalls++;
        }
        check(rejectedCalls == 8, "malformed_or_oversized_native_tool_calls_never_reach_executor");

        string manyCall = "{\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"web_search\",\"arguments\":{}}}]},\"done\":false}\n";
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json(string.Concat(Enumerable.Repeat(manyCall, 33)) + "{\"done\":true}\n")))))
            check((await FailureAsync(() => CollectToolsAsync(client, OllamaModels.FastModel, UserMessage(), definitions))).Contains("工具调用过多"), "tool_call_count_is_bounded_across_all_stream_chunks");

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            var handler = new FakeHandler((_, _) => throw new InvalidOperationException("must not send"));
            using var client = new OllamaClient(handler);
            check(await IsCanceledAsync(() => CollectToolsAsync(client, OllamaModels.FastModel, UserMessage(), definitions, cts.Token)) && handler.Calls == 0, "pre_canceled_tool_request_never_sends");
        }
        var cancelStream = new StagedStream(manyCall, "{\"done\":true}\n");
        using (var cts = new CancellationTokenSource())
        using (var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(cancelStream) }))))
        {
            await using var iterator = client.ChatWithToolsAsync(OllamaModels.FastModel, UserMessage(), definitions, cts.Token).GetAsyncEnumerator();
            check(await iterator.MoveNextAsync() && iterator.Current.ToolCalls?.Count == 1 && !iterator.Current.Done, "tool_delta_is_available_before_final_completion");
            var pending = iterator.MoveNextAsync().AsTask(); cts.Cancel();
            check(await IsCanceledAsync(() => pending), "canceling_tool_stream_does_not_emit_fake_completion");
        }
        check(cancelStream.WasDisposed, "canceled_tool_stream_is_disposed");
    }

    private static IReadOnlyList<OllamaMessage> UserMessage() => new[] { new OllamaMessage("user", "你好") };

    private static async Task<List<OllamaChatDelta>> CollectAsync(IOllamaClient client, string model, IReadOnlyList<OllamaMessage> messages, CancellationToken ct = default)
    {
        var deltas = new List<OllamaChatDelta>();
        await foreach (var delta in client.ChatAsync(model, messages, ct)) deltas.Add(delta);
        return deltas;
    }

    private static async Task<List<OllamaChatDelta>> CollectToolsAsync(IOllamaClient client, string model, IReadOnlyList<OllamaMessage> messages,
        IReadOnlyList<OllamaToolDefinition> definitions, CancellationToken ct = default)
    {
        var deltas = new List<OllamaChatDelta>();
        await foreach (var delta in client.ChatWithToolsAsync(model, messages, definitions, ct)) deltas.Add(delta);
        return deltas;
    }

    private static async Task<string> FailureAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OllamaException exception) { return exception.Message; }
        throw new InvalidOperationException("Expected an OllamaException.");
    }

    private static async Task<bool> IsCanceledAsync(Func<Task> operation)
    {
        try { await operation().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { return true; }
        return false;
    }

    private static async Task CheckPartialFailureAsync(string body, string name, List<object> checks)
    {
        using var client = new OllamaClient(new FakeHandler((_, _) => Task.FromResult(Json(body))));
        var deltas = new List<OllamaChatDelta>();
        string failure = await FailureAsync(async () =>
        {
            await foreach (var delta in client.ChatAsync(OllamaModels.FastModel, UserMessage(), CancellationToken.None)) deltas.Add(delta);
        });
        bool passed = deltas.Count == 1 && deltas[0].Content == "部分" && !deltas[0].Done && !failure.Contains("PRIVATE_IMAGE_BYTES");
        checks.Add(new { name, passed });
        if (!passed) throw new InvalidOperationException("Ollama client check failed: " + name);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        private readonly bool autoMetadata;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, bool autoMetadata = true) { this.send = send; this.autoMetadata = autoMetadata; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (autoMetadata && request.RequestUri?.AbsolutePath == "/api/tags")
                return Task.FromResult(Json("{\"models\":[{\"name\":\"qwen3.5:9b-q8_0\"},{\"name\":\"qwen3.8:27b-q4_K_M\"},{\"name\":\"llama3.1:8b\"}]}"));
            if (autoMetadata && request.RequestUri?.AbsolutePath == "/api/show")
                return Task.FromResult(Json("{\"capabilities\":[\"completion\",\"vision\",\"thinking\",\"tools\"],\"thinking\":{\"values\":[true,false],\"default\":true}}"));
            return send(request, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CountingStream : MemoryStream
    {
        public int BytesRead { get; private set; }
        public CountingStream(byte[] body) : base(body, writable: false) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = base.Read(buffer.Span);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class StagedStream : Stream
    {
        private readonly byte[][] chunks;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int chunk;
        private int offset;
        public bool SecondChunkReleased => release.Task.IsCompleted;
        public bool WasDisposed { get; private set; }
        public StagedStream(string first, string second) => chunks = new[] { Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second) };
        public void ReleaseSecondChunk() => release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk >= chunks.Length) return 0;
            if (chunk == 1) await release.Task.WaitAsync(cancellationToken);
            int count = Math.Min(buffer.Length, chunks[chunk].Length - offset);
            chunks[chunk].AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            if (offset == chunks[chunk].Length) { chunk++; offset = 0; }
            return count;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
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
