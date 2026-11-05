using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// In-memory protocol checks only: never start Ollama, load a model, or open the
// chat/memory stores. Request bodies below contain synthetic fixture text only.
internal static class ScheduledTaskAnnouncementChecks
{
    public static async Task<object> RunAsync()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Scheduled announcement check failed: " + name);
        }
        var endpoint = OllamaClient.LocalEndpoint;
        var never = new ScheduledTaskAnnouncement(_ => throw new InvalidOperationException("Short output must not create a client."));
        string shortSource = "搜索完成。\r\n温度：8°C。\nhttps://example.invalid/result\n**完整结果**";
        var shortResult = await never.CreateAsync(shortSource, "selected:latest", endpoint, CancellationToken.None);
        Check(shortResult.Output == shortSource.Replace("\r\n", "\n") && !shortResult.IsSummary,
            "whole_short_result_preserves_lines_links_and_formatting_without_http");
        Check((await never.CreateAsync(new string('中', 400), "selected:latest", endpoint, CancellationToken.None)).Output?.Length == 400,
            "400_unicode_characters_remain_whole");
        Check((await never.CreateAsync(string.Concat(Enumerable.Repeat("🐉", 400)), "selected:latest", endpoint, CancellationToken.None)).Output?.Length == 800,
            "unicode_surrogate_pairs_count_as_one_character");
        Check((await never.CreateAsync("\r\n \t", "selected:latest", endpoint, CancellationToken.None)).Output == null,
            "empty_output_uses_title_only_fallback");

        string longSource = new string('中', 401);
        var emptyHandler = new Handler((request, _) => Task.FromResult(Json("{\"models\":[]}")));
        var emptyService = Service(emptyHandler);
        Check((await emptyService.CreateAsync(longSource, "unloaded:latest", endpoint, CancellationToken.None)).Output == null &&
            emptyHandler.Paths.SequenceEqual(new[] { "/api/ps" }), "no_loaded_models_causes_zero_metadata_or_generation_requests");

        foreach (string residency in new[]
        {
            "{\"models\":[{\"name\":\"remote:cloud\",\"context_length\":8192}]}",
            "{\"models\":[{\"name\":\"resident:latest\"}]}",
            "{\"models\":[{\"name\":\"resident:latest\",\"context_length\":1024}]}",
            "{\"models\":[{\"name\":\"resident:latest\",\"context_length\":8192,\"expires_at\":\"2000-01-01T00:00:00Z\"}]}"
        })
        {
            var handler = new Handler((_, _) => Task.FromResult(Json(residency)));
            Check((await Service(handler).CreateAsync(longSource, "resident:latest", endpoint, CancellationToken.None)).Output == null &&
                handler.Paths.SequenceEqual(new[] { "/api/ps" }), "unavailable_remote_unknown_context_or_expired_runner_is_not_loaded_" + checks.Count);
        }

        string expectedSummary = "完成了天气检查，最高温 14°C、最低温 7°C。";
        var success = new Handler(async (request, ct) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/ps") return Json(ResidentJson("other:latest", "selected:latest"));
            if (path == "/api/tags") return Json(ResidentJson("other:latest", "selected:latest"));
            if (path == "/api/show") return Json("{\"capabilities\":[\"completion\",\"thinking\"],\"thinking\":{\"values\":[true,false]}}");
            Check(path == "/api/chat", "summary_never_calls_download_load_unload_or_any_other_route");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            Check(root.GetProperty("model").GetString() == "selected:latest", "prefer_current_selection_only_when_it_is_resident");
            Check(!root.GetProperty("stream").GetBoolean() && !root.GetProperty("think").GetBoolean() &&
                !root.TryGetProperty("keep_alive", out _) && !root.TryGetProperty("tools", out _),
                "stateless_summary_disables_thinking_and_has_no_tools_or_keepalive_override");
            Check(root.GetProperty("options").GetProperty("num_ctx").GetInt32() == 4096 &&
                root.GetProperty("options").GetProperty("num_predict").GetInt32() == 512,
                "summary_preserves_resident_context_instead_of_normal_chat_8192");
            var messages = root.GetProperty("messages");
            Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("role").GetString() == "system" &&
                messages[1].GetProperty("role").GetString() == "user" &&
                messages[1].GetProperty("content").GetString() == longSource &&
                messages.EnumerateArray().All(message => !message.TryGetProperty("tool_calls", out _) &&
                    !message.TryGetProperty("images", out _) && !message.TryGetProperty("thinking", out _)),
                "request_contains_only_summary_instruction_and_current_result_no_conversation_or_memory");
            return Reply(expectedSummary, thinking: "NEVER DISPLAY OR SPEAK THIS PRIVATE THINKING");
        });
        var summarized = await Service(success).CreateAsync(longSource, "selected:latest", endpoint, CancellationToken.None);
        Check(summarized.Output == expectedSummary && summarized.IsSummary, "only_assistant_final_content_is_returned");
        Check(success.Paths.SequenceEqual(new[] { "/api/ps", "/api/tags", "/api/show", "/api/ps", "/api/chat" }),
            "residency_is_rechecked_immediately_before_single_generation");

        string? actualModel = null;
        string? capturedSource = null;
        var alternate = MetadataHandler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            actualModel = document.RootElement.GetProperty("model").GetString();
            capturedSource = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            return Reply(new string('好', 300));
        });
        var bounded = await Service(alternate).CreateAsync("START" + new string('中', 20_000) + "END", "unloaded:latest", endpoint, CancellationToken.None);
        Check(actualModel == "resident:latest", "uses_existing_resident_chat_model_without_loading_unloaded_selection");
        Check(capturedSource!.StartsWith("START", StringComparison.Ordinal) && capturedSource.EndsWith("END", StringComparison.Ordinal) &&
            Encoding.UTF8.GetByteCount(capturedSource) < 3072 && capturedSource.Contains("omitted", StringComparison.Ordinal),
            "large_input_budget_preserves_head_and_tail_and_marks_omitted_middle");
        Check(bounded.IsSummary && bounded.Output!.EnumerateRunes().Count() == 240,
            "verbose_summary_is_bounded_to_240_characters");

        var embedding = MetadataHandler((_, _) => throw new InvalidOperationException("Embedding model must not generate."), supportsChat: false);
        Check((await Service(embedding).CreateAsync(longSource, "resident:latest", endpoint, CancellationToken.None)).Output == null &&
            !embedding.Paths.Contains("/api/chat"), "embedding_only_models_are_skipped_without_inference");
        int psCalls = 0;
        var evicted = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/ps" => Json(++psCalls == 1 ? ResidentJson("resident:latest") : "{\"models\":[]}"),
            "/api/tags" => Json(ResidentJson("resident:latest")),
            "/api/show" => Json("{\"capabilities\":[\"completion\"]}"),
            _ => throw new InvalidOperationException("Evicted runner must not generate.")
        }));
        Check((await Service(evicted).CreateAsync(longSource, "resident:latest", endpoint, CancellationToken.None)).Output == null &&
            !evicted.Paths.Contains("/api/chat"), "eviction_during_metadata_reads_falls_back_without_reload");

        foreach (var reply in new[]
        {
            "{\"done\":true,\"message\":{\"role\":\"assistant\",\"content\":\"\",\"thinking\":\"secret\"}}",
            "{\"done\":false,\"message\":{\"role\":\"assistant\",\"content\":\"incomplete\"}}",
            "{\"done\":true,\"done_reason\":\"length\",\"message\":{\"role\":\"assistant\",\"content\":\"cut off\"}}",
            "{\"done\":true,\"message\":{\"role\":\"assistant\",\"content\":\"<think>unfinished secret\"}}",
            "{\"done\":true,\"message\":{\"role\":\"assistant\",\"content\":\"bad\",\"tool_calls\":[{}]}}",
            "not JSON"
        })
        {
            var bad = MetadataHandler((_, _) => Task.FromResult(Json(reply)));
            Check((await Service(bad).CreateAsync(longSource, "resident:latest", endpoint, CancellationToken.None)).Output == null,
                "empty_incomplete_thinking_tool_or_malformed_response_falls_back_" + checks.Count);
        }
        var failed = MetadataHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
        Check((await Service(failed).CreateAsync(longSource, "resident:latest", endpoint, CancellationToken.None)).Output == null,
            "server_generation_failure_returns_title_only_fallback");

        using var canceled = new CancellationTokenSource();
        var cancelDuring = MetadataHandler(async (_, ct) =>
        {
            canceled.Cancel();
            await Task.Delay(1, ct);
            return Reply("must not return");
        });
        bool propagated = false;
        try { await Service(cancelDuring).CreateAsync(longSource, "resident:latest", endpoint, canceled.Token); }
        catch (OperationCanceledException) { propagated = true; }
        Check(propagated, "privacy_or_exit_cancellation_propagates_and_does_not_return_stale_summary");
        return new { passed = true, checks };
    }

    private static ScheduledTaskAnnouncement Service(Handler handler) => new(endpoint => new OllamaClient(endpoint, handler));
    private static Handler MetadataHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> generate, bool supportsChat = true)
        => new((request, ct) => request.RequestUri!.AbsolutePath switch
        {
            "/api/ps" or "/api/tags" => Task.FromResult(Json(ResidentJson("resident:latest"))),
            "/api/show" => Task.FromResult(Json(supportsChat ? "{\"capabilities\":[\"completion\"]}" : "{\"capabilities\":[\"embedding\"]}")),
            "/api/chat" => generate(request, ct),
            _ => throw new InvalidOperationException("Unexpected route.")
        });
    private static string ResidentJson(params string[] names) => JsonSerializer.Serialize(new
    {
        models = names.Select(name => new { name, context_length = 4096 })
    });
    private static HttpResponseMessage Reply(string content, string? thinking = null) => Json(JsonSerializer.Serialize(new
    {
        done = true, message = new { role = "assistant", content, thinking }
    }));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return send(request, cancellationToken);
        }
    }
}
