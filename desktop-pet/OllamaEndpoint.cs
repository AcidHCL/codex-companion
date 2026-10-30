using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopPet;

internal static class OllamaEndpoint
{
    public const string DefaultAddress = "http://127.0.0.1:11434";
    private static readonly Regex ServerRoot = new(@"^(https?)://(\[[0-9a-fA-F:.]+\]|[A-Za-z0-9\p{L}][A-Za-z0-9\p{L}._-]*)(?::([0-9]{1,5}))?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Uri Parse(string address)
    {
        string value = address?.Trim() ?? "";
        bool explicitScheme = value.Contains("://", StringComparison.Ordinal);
        if (!explicitScheme) value = "http://" + value;
        var match = ServerRoot.Match(value);
        if (!match.Success || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown)
            throw new ArgumentException(L.T("请输入 Ollama 服务器地址，例如 127.0.0.1:11434 或 https://ollama.example.com；不要填写用户名、密码、API 路径、查询参数或片段。", "Enter an Ollama server address, such as 127.0.0.1:11434 or https://ollama.example.com. Do not include a username, password, API path, query, or fragment."), nameof(address));
        int port = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : explicitScheme ? uri.Port : 11434;
        if (port is < 1 or > 65535) throw new ArgumentException(L.T("Ollama 端口必须在 1–65535 之间。", "The Ollama port must be between 1 and 65535."), nameof(address));
        return new UriBuilder(uri.Scheme, uri.Host, port) { Path = "/", Query = "", Fragment = "" }.Uri;
    }

    public static string Normalize(string? address)
    {
        try { return Parse(address ?? "").AbsoluteUri.TrimEnd('/'); }
        catch (ArgumentException) { return DefaultAddress; }
    }
}
