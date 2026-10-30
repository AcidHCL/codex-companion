using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed record PetWebSource(string Title, string Url, string Text);
internal sealed record PetWebResult(IReadOnlyList<PetWebSource> Sources, string? Error = null);

internal interface IPetWebClient : IDisposable
{
    Task<PetWebResult> SearchAsync(string query, CancellationToken cancellationToken);
    Task<PetWebResult> FetchAsync(string url, CancellationToken cancellationToken);
}

// Public, read-only web access. This transport never shares Ollama's LAN permissions,
// application credentials, browser cookies, proxy configuration, or conversation data.
internal sealed class PetWebClient : IPetWebClient
{
    internal const int MaximumBodyBytes = 1024 * 1024;
    internal const int MaximumPageCharacters = 6000;
    private const int MaximumRedirects = 3;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly Regex SearchElements = new(@"<(?<tag>a|div)\b(?<attributes>[^>]{0,12000})>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex AnchorEnd = new(@"</a\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex DivEnd = new(@"</div\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex Attributes = new("(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.Singleline | RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex WhiteSpace = new(@"\s+", RegexOptions.CultureInvariant, PatternTimeout);
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public PetWebClient() : this(CreatePublicHandler(), ResolveAsync) { }

    // Synthetic tests still exercise URI, DNS, redirects, body limits, and parsers.
    internal PetWebClient(HttpMessageHandler handler, Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        _resolve = resolver;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AI-Pet/1.0 (+public-web-reader)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
    }

    internal static SocketsHttpHandler CreatePublicHandler() => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 2,
        MaxResponseHeadersLength = 32,
        ConnectCallback = ConnectPublicAsync
    };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        // Resolve again at connection time and connect to the exact verified IP, never
        // hand the hostname back to the socket resolver (DNS rebinding protection).
        IPAddress[] addresses = await ResolvePublicAsync(context.DnsEndPoint.Host, ResolveAsync, cancellationToken).ConfigureAwait(false);
        foreach (IPAddress address in addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) { socket.Dispose(); throw; }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Public web connection failed.");
    }

    private static Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    internal static async Task<IPAddress[]> ResolvePublicAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolver, CancellationToken cancellationToken)
    {
        string normalized = host.Trim('[', ']').TrimEnd('.');
        IPAddress[] addresses = IPAddress.TryParse(normalized, out var literal)
            ? new[] { literal }
            : await resolver(normalized, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new WebReadException(L.T("只允许读取公开互联网网页；本机、内网和保留地址已被阻止。", "Only public internet pages can be read. Local, private-network, and reserved addresses are blocked."));
        return addresses;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
                bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                bytes[0] == 169 && bytes[1] == 254 ||
                bytes[0] == 168 && bytes[1] == 63 && bytes[2] == 129 && bytes[3] == 16 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2 || bytes[1] == 88 && bytes[2] == 99) ||
                bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100) ||
                bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0) return false;
        // Only ordinary global unicast. Exclude transition/tunnel, documentation, and
        // protocol-assignment prefixes even where an embedded IPv4 target looks public.
        return (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    internal static Uri ParsePublicUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || url.Any(character => char.IsControl(character) || character == '\\') ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Port is not (80 or 443) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new WebReadException(L.T("请输入不含账号密码的公开 HTTP/HTTPS 网页链接（端口 80 或 443）。", "Enter a public HTTP/HTTPS URL without credentials, using port 80 or 443."));
        string host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host.Contains('%') ||
            IPAddress.TryParse(host, out var address) && !IsPublicAddress(address) ||
            !IPAddress.TryParse(host, out _) && (!host.Contains('.') ||
                new[] { ".localhost", ".local", ".internal", ".lan", ".home", ".test", ".invalid", ".onion" }.Any(host.EndsWith)))
            throw new WebReadException(L.T("只允许读取公开互联网网页；本机、内网和保留地址已被阻止。", "Only public internet pages can be read. Local, private-network, and reserved addresses are blocked."));
        return new UriBuilder(uri) { Fragment = string.Empty }.Uri;
    }

    public async Task<PetWebResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0 || query.Length > 300 || query.Any(char.IsControl))
            return Failure(L.T("搜索词需要为 1～300 个字符，且不能包含控制字符。", "Search queries must contain 1–300 characters without control characters."));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            // DuckDuckGo documents this HTML endpoint for browsers without JavaScript.
            // No unofficial API token, browser profile, CAPTCHA workaround, or page action.
            var page = await ReadPublicAsync(new Uri("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query)), deadline.Token).ConfigureAwait(false);
            if (IsChallenge(page.Body)) return Failure(L.T("搜索服务要求人机验证，暂时无法自动搜索。可以稍后重试，或发送具体网页链接。", "The search service requires human verification. Retry later or send a specific webpage link."));
            var sources = ParseSearchResults(page.Body);
            if (sources.Count > 0) return new PetWebResult(sources);
            return Failure(page.Body.Contains("no-results", StringComparison.OrdinalIgnoreCase) || page.Body.Contains("No results found", StringComparison.OrdinalIgnoreCase)
                ? L.T("没有找到可读取的搜索结果，请换一个更具体的搜索词。", "No readable search results found. Try a more specific query.")
                : L.T("搜索服务没有返回可识别的结果，可能暂时限制访问或页面格式发生变化。可以发送具体网页链接。", "The search service returned no recognizable results. Access may be limited or the page format may have changed. Try a specific webpage link."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure(L.T("联网搜索超时，请检查网络后重试。", "Web search timed out. Check the network and retry.")); }
        catch (OperationCanceledException) { throw; }
        catch (WebReadException exception) { return Failure(exception.Message); }
        catch (Exception exception) when (IsExpectedFailure(exception)) { return Failure(L.T("暂时无法连接搜索服务，请检查网络后重试；也可以发送具体网页链接。", "Could not connect to the search service. Check the network and retry, or send a specific webpage link.")); }
    }

    public async Task<PetWebResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Uri uri = ParsePublicUri(url);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var page = await ReadPublicAsync(uri, deadline.Token).ConfigureAwait(false);
            if (IsChallenge(page.Body)) return Failure(L.T("该网页要求人机验证或登录，无法自动读取。请粘贴需要分析的正文。", "This webpage requires human verification or login. Paste the text you want to analyze."));
            string title = ExtractTitle(page.Body, page.Uri.Host);
            string text = page.MediaType is "text/plain" or "text/markdown" ? CleanPlainText(page.Body) : ExtractPageText(page.Body);
            if (text.Length < 30) return Failure(L.T("该网页没有足够的可读正文，可能需要登录、运行 JavaScript，或是媒体/下载页面。请粘贴正文。", "This webpage has insufficient readable text. It may need login or JavaScript, or be a media/download page. Paste its text instead."));
            return new PetWebResult(new[] { new PetWebSource(title, page.Uri.AbsoluteUri, Limit(text, MaximumPageCharacters)) });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure(L.T("读取网页超时，请稍后重试或粘贴正文。", "Reading the webpage timed out. Retry later or paste its text.")); }
        catch (OperationCanceledException) { throw; }
        catch (WebReadException exception) { return Failure(exception.Message); }
        catch (Exception exception) when (IsExpectedFailure(exception)) { return Failure(L.T("无法读取该网页，可能是网络或网站访问限制。请稍后重试或粘贴正文。", "Could not read the webpage due to a network or site restriction. Retry later or paste its text.")); }
    }

    private async Task<ReadPage> ReadPublicAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (int redirect = 0; ; redirect++)
        {
            uri = ParsePublicUri(uri.AbsoluteUri);
            await ResolvePublicAsync(uri.IdnHost, _resolve, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirect >= MaximumRedirects || response.Headers.Location is not { } location)
                    throw new WebReadException(L.T("网页重定向次数过多或地址无效，无法读取。", "Too many webpage redirects or an invalid address. The page could not be read."));
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.Accepted)
                throw new WebReadException(L.T("网站限制自动访问、要求验证或暂时限流。请稍后重试，或粘贴网页正文。", "The website restricts automated access, requires verification, or is rate-limiting requests. Retry later or paste the text."));
            if (!response.IsSuccessStatusCode) throw new WebReadException(L.T("网站返回访问失败（HTTP ", "The website returned HTTP ") + (int)response.StatusCode + L.T("），未读取到正文。", "; no page text was retrieved."));
            string mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            if (mediaType is not ("text/html" or "application/xhtml+xml" or "text/plain" or "text/markdown"))
                throw new WebReadException(L.T("暂时只支持 HTML 和纯文本网页；PDF、图片、视频或下载文件请提供正文。", "Only HTML and plain-text webpages are supported. Provide text for PDF, image, video, or downloadable files."));
            if (response.Content.Headers.ContentLength > MaximumBodyBytes)
                throw new WebReadException(L.T("网页超过读取大小限制（1 MB），请提供更精简的页面或粘贴正文。", "The webpage exceeds the 1 MB reading limit. Use a smaller page or paste its text."));
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] block = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(block.AsMemory(0, Math.Min(block.Length, MaximumBodyBytes + 1 - (int)buffer.Length)), cancellationToken).ConfigureAwait(false)) != 0)
            {
                buffer.Write(block, 0, count);
                if (buffer.Length > MaximumBodyBytes) throw new WebReadException(L.T("网页超过读取大小限制（1 MB），请提供更精简的页面或粘贴正文。", "The webpage exceeds the 1 MB reading limit. Use a smaller page or paste its text."));
            }
            Encoding encoding = Encoding.UTF8;
            string? charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { encoding = Encoding.GetEncoding(charset); }
                catch (ArgumentException) { encoding = CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? Encoding.UTF8; }
            }
            return new ReadPage(uri, encoding.GetString(buffer.ToArray()), mediaType);
        }
    }

    internal static IReadOnlyList<PetWebSource> ParseSearchResults(string html)
    {
        var result = new List<PetWebSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? pendingTitle = null, pendingUrl = null;
        foreach (Match element in SearchElements.Matches(html))
        {
            var attributes = ReadAttributes(element.Groups["attributes"].Value);
            string[] classes = WhiteSpace.Split(attributes.GetValueOrDefault("class", string.Empty));
            bool isAnchor = element.Groups["tag"].Value.Equals("a", StringComparison.OrdinalIgnoreCase);
            bool isTitle = isAnchor && classes.Contains("result__a");
            bool isSnippet = classes.Contains("result__snippet");
            if (!isTitle && !isSnippet) continue;
            // A title without a readable snippet is only a navigation link. Do
            // not manufacture evidence or pair an invalid title with its neighbor.
            if (isTitle) pendingTitle = pendingUrl = null;
            int bodyStart = element.Index + element.Length;
            Match end = (isAnchor ? AnchorEnd : DivEnd).Match(html, bodyStart);
            if (!end.Success) continue;
            string body = html[bodyStart..end.Index];
            if (isTitle)
            {
                if (!attributes.TryGetValue("href", out var href)) continue;
                try
                {
                    Uri target = DecodeSearchTarget(href);
                    if (seen.Contains(target.AbsoluteUri)) continue;
                    pendingTitle = Limit(Flatten(body), 200);
                    pendingUrl = target.AbsoluteUri;
                }
                catch (Exception exception) when (exception is WebReadException or UriFormatException or ArgumentException) { }
            }
            else if (pendingUrl != null && isSnippet)
            {
                string snippet = Flatten(body);
                if (HasSearchSnippet(snippet) && seen.Add(pendingUrl))
                    result.Add(new PetWebSource(string.IsNullOrWhiteSpace(pendingTitle) ? pendingUrl : pendingTitle, pendingUrl, Limit(snippet, 900)));
                pendingTitle = pendingUrl = null;
                if (result.Count >= 5) break;
            }
        }
        return result;
    }

    // These are incoming provider/data sentinels, not UI text. Recognize every
    // supported form in either interface language before counting evidence.
    internal static bool HasSearchSnippet(string text) => !string.IsNullOrWhiteSpace(text) &&
        !new[] { "搜索结果未提供摘要", "No search snippet provided", "The search result did not include a summary",
            "No snippet available", "No description available", "No description available for this site" }
            .Contains(text.Trim().TrimEnd('.', '。', '!', '！'), StringComparer.OrdinalIgnoreCase);

    private static Uri DecodeSearchTarget(string href)
    {
        href = WebUtility.HtmlDecode(href);
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        // Only the provider's exact redirect route may be resolved relative to
        // its origin. All other relative paths remain unsupported.
        else if (href.StartsWith("/l/?", StringComparison.Ordinal)) href = "https://html.duckduckgo.com" + href;
        var uri = ParsePublicUri(href);
        if ((uri.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("html.duckduckgo.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.Equals("/l/", StringComparison.Ordinal))
        {
            string[][] destinations = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2 && parts[0] == "uddg").ToArray();
            if (destinations.Length != 1) throw new WebReadException(L.T("无有效链接。", "No valid link."));
            href = Uri.UnescapeDataString(destinations[0][1]);
        }
        return ParsePublicUri(href);
    }

    private static Dictionary<string, string> ReadAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Attributes.Matches(text)) result[match.Groups["name"].Value] = WebUtility.HtmlDecode(match.Groups["value"].Value);
        return result;
    }

    internal static string ExtractTitle(string html, string fallback)
    {
        var match = Regex.Match(html, @"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);
        string title = match.Success ? Flatten(match.Groups[1].Value) : string.Empty;
        return title.Length > 0 ? Limit(title, 200) : fallback;
    }

    internal static string ExtractPageText(string html)
    {
        string text = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline, PatternTimeout);
        foreach (string tag in new[] { "script", "style", "noscript", "iframe", "svg", "canvas", "form", "nav", "footer", "header", "aside", "button" })
            text = Regex.Replace(text, @"<" + tag + @"\b[^>]*>.*?</" + tag + @"\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);
        // Prefer an article/main body when present; navigation is already discarded.
        foreach (string tag in new[] { "article", "main" })
        {
            Match main = Regex.Match(text, @"<" + tag + @"\b[^>]*>(.*?)</" + tag + @"\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);
            if (main.Success && Flatten(main.Groups[1].Value).Length >= 80) { text = main.Groups[1].Value; break; }
        }
        text = Regex.Replace(text, @"<head\b[^>]*>.*?</head\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeout);
        text = Regex.Replace(text, @"</?(?:p|div|h[1-6]|br|li|tr|section|article|main)\b[^>]*>", "\n", RegexOptions.IgnoreCase, PatternTimeout);
        text = WebUtility.HtmlDecode(Tags.Replace(text, " "));
        return string.Join("\n", text.Split('\n').Select(line => WhiteSpace.Replace(line, " ").Trim()).Where(line => line.Length > 0));
    }

    private static string Flatten(string html) => WhiteSpace.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim();
    private static string CleanPlainText(string text) => string.Join("\n", text.Split('\n').Select(line => WhiteSpace.Replace(line, " ").Trim()).Where(line => line.Length > 0));
    private static string Limit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
    private static bool IsChallenge(string body)
    {
        string title = ExtractTitle(body, string.Empty);
        return body.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase) || body.Contains("id=\"challenge-form\"", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || title.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Verify you are human", StringComparison.OrdinalIgnoreCase) ||
            ((title.Contains("sign in", StringComparison.OrdinalIgnoreCase) || title.Contains("log in", StringComparison.OrdinalIgnoreCase) || title.Contains("login", StringComparison.OrdinalIgnoreCase) || title.Contains("登录", StringComparison.Ordinal) || title.Contains("登入", StringComparison.Ordinal)) &&
                Regex.IsMatch(body, "<input\\b[^>]*\\btype\\s*=\\s*['\"]?password\\b", RegexOptions.IgnoreCase, PatternTimeout));
    }
    private static bool IsExpectedFailure(Exception exception) => exception is HttpRequestException or IOException or SocketException or RegexMatchTimeoutException or ArgumentException or InvalidOperationException or NotSupportedException;
    private static PetWebResult Failure(string error) => new(Array.Empty<PetWebSource>(), error);
    public void Dispose() => _http.Dispose();
    private sealed record ReadPage(Uri Uri, string Body, string MediaType);
    private sealed class WebReadException(string message) : Exception(message);
}
