using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal static class PetWebClientChecks
{
    public static async Task<object> RunAsync()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Web client check failed: " + name);
        }
        foreach (string address in new[] { "0.0.0.0", "10.2.3.4", "100.64.0.1", "100.127.255.255", "127.0.0.1", "169.254.169.254", "172.16.0.1", "172.31.255.255", "192.168.0.1", "192.0.0.1", "192.0.2.1", "192.88.99.1", "198.18.0.1", "198.19.1.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "255.255.255.255", "::", "::1", "::ffff:127.0.0.1", "::ffff:192.168.1.1", "fe80::1", "fd00::1", "ff02::1", "64:ff9b::a00:1", "2001::1", "2001:db8::1", "2002:a00:1::", "3fff::1" })
            Check(!PetWebClient.IsPublicAddress(IPAddress.Parse(address)), "blocks_nonpublic_address_" + address);
        foreach (string address in new[] { "8.8.8.8", "1.1.1.1", "100.128.0.1", "172.32.0.1", "::ffff:8.8.8.8", "2001:4860:4860::8888", "2606:4700:4700::1111" })
            Check(PetWebClient.IsPublicAddress(IPAddress.Parse(address)), "allows_public_address_" + address);
        foreach (string address in new[] { "", "file:///C:/secret", "ftp://example.com/a", "http://localhost", "http://127.1/a", "http://2130706433/a", "http://0x7f000001/a", "http://user:password@example.com", "http://example.com:11434/api", "https://example.com:8443", "http://printer.local", "http://x.internal/", "http://host.lan/", "http://host/", "http://[::1]", "http://[fe80::1%25eth0]/", "http://example.com\\@127.0.0.1/", "https://example.com/\nsecret" })
        {
            bool blocked = false;
            try { PetWebClient.ParsePublicUri(address); } catch { blocked = true; }
            Check(blocked, "blocks_unsafe_url_shape_" + checks.Count);
        }
        Check(PetWebClient.ParsePublicUri("https://example.com/article?q=cat#chapter").AbsoluteUri == "https://example.com/article?q=cat", "fragments_removed_query_preserved");
        Check(!PetWebClient.IsPublicAddress(IPAddress.Parse("168.63.129.16")), "cloud_platform_virtual_internal_endpoint_blocked");
        using (var handler = PetWebClient.CreatePublicHandler())
            Check(!handler.UseProxy && !handler.UseCookies && !handler.AllowAutoRedirect && handler.Credentials == null && handler.SslOptions.RemoteCertificateValidationCallback == null && handler.ConnectCallback != null, "real_transport_pins_verified_dns_without_proxy_cookie_credentials_redirect_or_tls_bypass");

        bool mixedDnsBlocked = false;
        try { await PetWebClient.ResolvePublicAsync("example.com", (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.1.2.3") }), CancellationToken.None); } catch { mixedDnsBlocked = true; }
        Check(mixedDnsBlocked, "any_private_dns_answer_blocks_entire_target");
        bool emptyDnsBlocked = false;
        try { await PetWebClient.ResolvePublicAsync("example.com", (_, _) => Task.FromResult(Array.Empty<IPAddress>()), CancellationToken.None); } catch { emptyDnsBlocked = true; }
        Check(emptyDnsBlocked, "empty_dns_answers_blocked");

        string html = "<html><head><title>Example &amp; title</title></head><body><header>HEADERSECRET</header><nav>NAVSECRET</nav><main><h1>正文标题</h1><p>这是用于网页抽取测试的一段公开正文。这些文字应该保留给模型，包含事实、时间和普通段落。</p><p>第二段正文，字符实体 &amp; 正确解码，链接 <a href='/next'>仍只作为普通文本</a>，不会递归访问。</p><script>JAVASCRIPTSECRET</script><style>STYLESECRET</style><form>FORMSECRET<input value='secret'/></form></main><footer>FOOTERSECRET</footer></body></html>";
        var requests = new List<string>();
        using (var client = Fake((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Check(request.Method == HttpMethod.Get && request.Content == null && !request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie") && request.Headers.Referrer == null, "page_read_is_credential_free_get_without_referrer_or_body");
            return Task.FromResult(Html(html));
        }))
        {
            var result = await client.FetchAsync("https://example.com/article#heading", CancellationToken.None);
            Check(result.Error == null && result.Sources.Count == 1 && result.Sources[0].Url == "https://example.com/article" && result.Sources[0].Title == "Example & title", "fetch_returns_actual_final_url_and_decoded_title");
            Check(result.Sources[0].Text.Contains("第二段正文") && !result.Sources[0].Text.Contains("SECRET") && requests.Count == 1, "extracts_main_content_strips_active_forms_navigation_and_does_not_follow_links");
        }
        using (var client = Fake((_, _) => Task.FromResult(Html("<html><main><p>" + new string('文', 10000) + "</p></main></html>"))))
        {
            var result = await client.FetchAsync("https://example.com/a", CancellationToken.None);
            Check(result.Sources[0].Text.Length == PetWebClient.MaximumPageCharacters, "page_evidence_character_limit");
        }
        requests.Clear();
        using (var client = Fake((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/start" ? Redirect("/article") : Html(html));
        }))
        {
            var result = await client.FetchAsync("https://example.com/start", CancellationToken.None);
            Check(result.Sources.Count == 1 && result.Sources[0].Url == "https://example.com/article" && requests.Count == 2, "relative_public_redirect_is_revalidated_and_reports_final_url");
        }
        int sends = 0;
        using (var client = Fake((_, _) => { sends++; return Task.FromResult(Redirect("http://127.0.0.1:80/secret")); }))
        {
            var result = await client.FetchAsync("https://example.com/start", CancellationToken.None);
            Check(result.Error != null && result.Sources.Count == 0 && sends == 1, "redirect_to_loopback_blocked_before_second_request");
        }
        sends = 0;
        using (var client = Fake((_, _) => { sends++; return Task.FromResult(Redirect("https://example.net/private")); }, host => host == "example.net" ? new[] { IPAddress.Parse("192.168.1.2") } : PublicDns()))
        {
            var result = await client.FetchAsync("https://example.com/start", CancellationToken.None);
            Check(result.Error != null && sends == 1, "redirect_host_resolving_private_blocked_before_second_request");
        }
        sends = 0;
        using (var client = Fake((_, _) => { sends++; return Task.FromResult(Redirect("/again")); }))
        {
            var result = await client.FetchAsync("https://example.com/start", CancellationToken.None);
            Check(result.Error != null && sends == 4, "at_most_three_redirect_hops");
        }
        sends = 0;
        using (var client = Fake((_, _) => { sends++; return Task.FromResult(Html(html)); }, _ => new[] { IPAddress.Parse("169.254.169.254") }))
        {
            var result = await client.FetchAsync("https://example.com/start", CancellationToken.None);
            Check(result.Error != null && sends == 0, "initial_metadata_dns_target_blocked_before_http");
        }
        string resultHtml = SearchHtml();
        using (var client = Fake((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "html.duckduckgo.com" && Uri.UnescapeDataString(request.RequestUri.Query).Contains("q=今天 北京 天气"), "search_uses_documented_nonjavascript_endpoint_with_only_query");
            return Task.FromResult(Html(resultHtml));
        }))
        {
            var result = await client.SearchAsync("今天 北京 天气", CancellationToken.None);
            Check(result.Error == null && result.Sources.Count == 5 && result.Sources[0].Title == "News & facts" && result.Sources[0].Url == "https://example.com/news", "search_decodes_destination_entities_deduplicates_and_limits_five");
            Check(result.Sources[0].Text == "Useful fresh information" && result.Sources.All(source => !source.Url.Contains("127.0.0.1") && !source.Url.Contains("localhost")), "search_snippets_parsed_and_private_links_not_returned");
        }
        foreach (string body in new[]
        {
            "<a class='result__a' href='https://example.com/weather'>Weather</a>",
            "<a class='result__a' href='https://example.com/weather'>Weather</a><a class='result__snippet'> \t<b> </b>&nbsp;</a>",
            "<a class='result__a' href='https://example.com/weather'>Weather</a><div class='result__snippet'>搜索结果未提供摘要。</div>",
            "<a class='result__a' href='https://example.com/weather'>Weather</a><a class='result__snippet'>No snippet available.</a>"
        })
        {
            using var client = Fake((_, _) => Task.FromResult(Html(body)));
            var result = await client.SearchAsync("weather", CancellationToken.None);
            Check(result.Sources.Count == 0 && result.Error != null, "missing_empty_or_placeholder_snippet_is_not_evidence_" + checks.Count);
        }
        var divSnippet = PetWebClient.ParseSearchResults("<div class='result'><a class='result__a' href='https://example.com/weather'>Weather</a><div class='result__snippet'>Current <b>weather</b> from a public source.</div></div>");
        Check(divSnippet.Count == 1 && divSnippet[0].Text == "Current weather from a public source.", "div_snippet_inside_result_wrapper_is_extracted");
        var whitespaceClasses = PetWebClient.ParseSearchResults("<a class='extra\tresult__a\nmore' href='https://example.com/weather'>Weather</a><div class='extra\r\nresult__snippet\fmore'>Current public weather information.</div>");
        Check(whitespaceClasses.Count == 1 && whitespaceClasses[0].Text == "Current public weather information.", "html_tab_newline_carriage_return_and_formfeed_classes_supported");
        var relativeDestination = PetWebClient.ParseSearchResults("<a class='result__a' href='/l/?uddg=https%3A%2F%2Fexample.com%2Fweather%3Fday%3Dtoday&amp;rut=abc'>Weather</a><a class='result__snippet'>Current public weather information.</a>");
        Check(relativeDestination.Count == 1 && relativeDestination[0].Url == "https://example.com/weather?day=today", "provider_relative_redirect_decodes_to_validated_public_target");
        foreach (string destination in new[]
        {
            "/weather", "/l/other?uddg=https%3A%2F%2Fexample.com%2Fweather", "/l/?uddg=", "/l/?rut=abc",
            "/l/?uddg=http%3A%2F%2F127.0.0.1%2Fprivate", "/l/?uddg=http%3A%2F%2F192.168.1.2%2Fprivate",
            "/l/?uddg=http%3A%2F%2Flocalhost%2Fprivate", "/l/?uddg=http%3A%2F%2F%5B%3A%3A1%5D%2Fprivate",
            "/l/?uddg=file%3A%2F%2F%2FC%3A%2Fprivate", "/l/?uddg=https%3A%2F%2Fuser%3Apassword%40example.com",
            "/l/?uddg=https%3A%2F%2Fexample.com%3A11434%2Fapi", "/l/?uddg=%2Fprivate",
            "/l/?uddg=https%253A%252F%252Fexample.com", "/l/?uddg=https%3A%2F%2Fexample.com&amp;uddg=http%3A%2F%2F127.0.0.1",
            "//127.0.0.1/l/?uddg=https%3A%2F%2Fexample.com", "ftp://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com",
            "https://duckduckgo.com:11434/l/?uddg=https%3A%2F%2Fexample.com"
        })
        {
            var rejected = PetWebClient.ParseSearchResults("<a class='result__a' href='" + destination + "'>Invalid</a><div class='result__snippet'>This must not become usable evidence.</div>");
            Check(rejected.Count == 0, "relative_redirect_and_target_validation_rejects_unsafe_or_ambiguous_link_" + checks.Count);
        }
        var mixedResults = PetWebClient.ParseSearchResults(
            "<a class='result__a' href='https://example.com/weather'>Title without snippet</a>" +
            "<a class='result__a' href='/l/?uddg=http%3A%2F%2F127.0.0.1%2Fprivate'>Private title</a><div class='result__snippet'>PRIVATE_INVALID_SNIPPET</div>" +
            "<a class='result__a' href='https://example.com/weather'>Readable duplicate</a><div class='result__snippet'>Public readable summary.</div>" +
            "<a class='result__a' href='https://example.com/weather'>Already used duplicate</a><a class='result__snippet'>DUPLICATE_SNIPPET</a>" +
            "<a class='result__a' href='https://example.org/next'>Next result</a><a class='result__snippet'>Independent next summary.</a>");
        Check(mixedResults.Count == 2 && mixedResults[0].Title == "Readable duplicate" && mixedResults[0].Text == "Public readable summary." && mixedResults[1].Text == "Independent next summary.",
            "mixed_invalid_missing_and_duplicate_results_do_not_cross_pair_or_consume_valid_url");
        var emptyBeforeValid = PetWebClient.ParseSearchResults(string.Concat(Enumerable.Range(0, 7).Select(index =>
            "<a class='result__a' href='https://example.com/empty" + index + "'>No summary</a>")) +
            "<a class='result__a' href='https://example.com/actual'>Actual</a><a class='result__snippet'>Readable summary after empty results.</a>");
        Check(emptyBeforeValid.Count == 1 && emptyBeforeValid[0].Url == "https://example.com/actual", "empty_results_do_not_use_up_source_limit");
        sends = 0;
        using (var client = Fake((_, _) => { sends++; return Task.FromResult(Html(resultHtml)); }))
        {
            var empty = await client.SearchAsync("  ", CancellationToken.None);
            var longQuery = await client.SearchAsync(new string('x', 301), CancellationToken.None);
            Check(empty.Error != null && longQuery.Error != null && sends == 0, "invalid_queries_do_not_contact_search_service");
        }
        foreach (var (body, expected) in new[] { ("<html><div class='anomaly-modal'>verify</div></html>", "人机验证"), ("<html><div class='no-results'>No results found</div></html>", "没有找到"), ("<html><p>Unrecognized service layout</p></html>", "可识别") })
        {
            using var client = Fake((_, _) => Task.FromResult(Html(body)));
            var result = await client.SearchAsync("weather", CancellationToken.None);
            Check(result.Sources.Count == 0 && result.Error?.Contains(expected) == true, "search_failure_is_truthful_" + expected);
        }
        using (var client = Fake((_, _) => Task.FromResult(Html("<html><title>Just a moment...</title><body>Verify before you continue with a CAPTCHA challenge</body></html>"))))
            Check((await client.FetchAsync("https://example.com/a", CancellationToken.None)).Error?.Contains("验证") == true, "fetch_challenge_does_not_become_page_evidence");
        using (var client = Fake((_, _) => Task.FromResult(Html("<html><div id='app'></div></html>"))))
            Check((await client.FetchAsync("https://example.com/a", CancellationToken.None)).Error?.Contains("JavaScript") == true, "empty_javascript_page_reports_limitation");
        using (var client = Fake((_, _) => Task.FromResult(Html("<html><title>Sign in - Account</title><form><input type='password'/></form><p>Please sign in to view the private content of your account.</p></html>"))))
            Check((await client.FetchAsync("https://example.com/a", CancellationToken.None)).Error?.Contains("登录") == true, "login_page_is_not_presented_as_requested_private_content");
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.Accepted, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            using var client = Fake((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("SECRET raw server diagnostics") }));
            var result = await client.FetchAsync("https://example.com/a", CancellationToken.None);
            Check(result.Sources.Count == 0 && result.Error != null && !result.Error.Contains("SECRET"), "http_failure_sanitized_" + (int)status);
        }
        using (var client = Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("SECRET", Encoding.UTF8, "application/pdf") })))
            Check((await client.FetchAsync("https://example.com/a", CancellationToken.None)).Error?.Contains("PDF") == true, "unsupported_binary_never_interpreted_as_text");
        using (var client = Fake((_, _) => Task.FromResult(Html(new string('x', PetWebClient.MaximumBodyBytes + 1)))))
            Check((await client.FetchAsync("https://example.com/a", CancellationToken.None)).Error?.Contains("1 MB") == true, "oversized_declared_body_rejected");
        var stream = new CountingStream(Encoding.UTF8.GetBytes(new string('x', PetWebClient.MaximumBodyBytes + 10000)));
        using (var client = Fake((_, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }))
        {
            var result = await client.FetchAsync("https://example.com/a", CancellationToken.None);
            Check(result.Error?.Contains("1 MB") == true && stream.BytesRead <= PetWebClient.MaximumBodyBytes + 1, "stream_and_decompressed_limit_bounds_allocation_even_without_content_length");
        }
        using (var client = Fake((_, _) => throw new HttpRequestException("PRIVATE PATH secret credential")))
        {
            var result = await client.FetchAsync("https://example.com/a", CancellationToken.None);
            Check(result.Error != null && !result.Error.Contains("secret") && !result.Error.Contains("PRIVATE"), "raw_network_errors_never_exposed");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var client = Fake(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Html(html); }))
        {
            cancellation.CancelAfter(30);
            bool cancelled = false;
            try { await client.FetchAsync("https://example.com/a", cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "user_cancellation_propagates_to_request");
        }
        return new { passed = true, assertions = checks.Count, checks };
    }

    // Explicit network validation only: fixed public facts, no user conversation/images.
    public static async Task<object> RunLiveAsync()
    {
        using var client = new PetWebClient();
        var search = await client.SearchAsync("Ollama documentation", CancellationToken.None);
        var page = await client.FetchAsync("https://docs.ollama.com/api/introduction", CancellationToken.None);
        return new
        {
            passed = search.Sources.Count > 0 && search.Error == null && page.Sources.Count == 1 && page.Error == null,
            provider = "DuckDuckGo HTML",
            search = new { count = search.Sources.Count, error = search.Error, sources = search.Sources.Select(source => new { source.Title, source.Url, characters = source.Text.Length }) },
            page = new { count = page.Sources.Count, error = page.Error, sources = page.Sources.Select(source => new { source.Title, source.Url, characters = source.Text.Length }) },
            checkedUtc = DateTimeOffset.UtcNow
        };
    }

    private static PetWebClient Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Func<string, IPAddress[]>? dns = null) => new(new Handler(send), (host, _) => Task.FromResult(dns?.Invoke(host) ?? PublicDns()));
    private static IPAddress[] PublicDns() => new[] { IPAddress.Parse("8.8.8.8") };
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static string SearchHtml()
    {
        var html = new StringBuilder("<html>");
        html.Append("<a class='result__a' href='//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fnews&amp;rut=abc'>News &amp; facts</a><a class='result__snippet'>Useful <b>fresh</b> information</a>");
        html.Append("<a class='result__a' href='http://127.0.0.1/'>Bad</a><a class='result__snippet'>Private</a>");
        html.Append("<a class='result__a' href='http://localhost/'>Bad</a><a class='result__snippet'>Private</a>");
        html.Append("<a class='result__a' href='https://example.com/news'>duplicate</a><a class='result__snippet'>Duplicate</a>");
        for (int i = 0; i < 7; i++) html.Append("<a class='result__a' href='https://example.com/" + i + "'>Result " + i + "</a><a class='result__snippet'>Summary " + i + "</a>");
        return html.Append("</html>").ToString();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private int _offset;
        public int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(bytes.Length - _offset, buffer.Length);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
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
