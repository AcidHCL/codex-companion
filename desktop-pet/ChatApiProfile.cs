using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DesktopPet;

// Credentials intentionally do not belong to this JSON-serializable profile.
internal sealed record ChatApiProfile
{
    public string BaseUrl { get; init; } = ChatApiEndpoint.DefaultAddress;
    public string Model { get; init; } = "";
    public bool SupportsImages { get; init; }
    public bool SupportsTools { get; init; }
    [JsonIgnore] public Uri Endpoint => ChatApiEndpoint.Parse(BaseUrl);

    internal ChatApiProfile Normalize()
    {
        string address;
        try { address = ChatApiEndpoint.Parse(BaseUrl).AbsoluteUri.TrimEnd('/'); }
        catch (ArgumentException) { address = ChatApiEndpoint.DefaultAddress; }
        string model = (Model ?? "").Trim();
        return this with { BaseUrl = address, Model = ChatApiModel.IsValid(model) ? model : "" };
    }

    internal ChatApiProfile Validate(bool requireModel = true)
    {
        string address = ChatApiEndpoint.Parse(BaseUrl).AbsoluteUri.TrimEnd('/');
        string model = (Model ?? "").Trim();
        if (requireModel && !ChatApiModel.IsValid(model) || model.Length > 0 && !ChatApiModel.IsValid(model))
            throw new ArgumentException(L.T("请填写服务商提供的模型 ID，例如 deepseek-chat；不要填写网址或说明文字。",
                "Enter the provider's model ID, such as deepseek-chat, rather than a URL or description."));
        return this with { BaseUrl = address, Model = model };
    }
}

internal static class ChatApiEndpoint
{
    internal const string DefaultAddress = "https://api.openai.com/v1";
    internal static Uri Parse(string? address)
    {
        string value = (address ?? "").Trim();
        if (value.Length is < 1 or > 2048 || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host) ||
            uri.HostNameType == UriHostNameType.Unknown || uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Port is < 1 or > 65535 ||
            uri.Scheme == "http" && !uri.IsLoopback)
            throw InvalidAddress();
        // Inspect the source path before Uri normalizes dot segments. Credentials
        // bind to the exact base path, not merely to its host.
        int authorityEnd = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        string path = authorityEnd < 0 ? "/" : value[authorityEnd..];
        string decoded;
        try { decoded = Uri.UnescapeDataString(path); } catch (UriFormatException) { throw InvalidAddress(); }
        if (decoded.Contains('\\') || decoded.Contains('?') || decoded.Contains('#') || decoded.Any(char.IsControl) ||
            decoded.Split('/').Any(part => part is "." or "..") || decoded.Contains('%')) throw InvalidAddress();
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.TrimEnd('/').EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(L.T("请填写 API 基础地址（如 https://api.example.com/v1），不要加 /chat/completions 或 /models。",
                "Enter the API base URL, such as https://api.example.com/v1, without /chat/completions or /models."));
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static ArgumentException InvalidAddress() => new(L.T(
        "请输入完整的 HTTPS API 基础地址；本机地址也可用 HTTP。不要包含密钥、用户名、查询参数或片段。",
        "Enter a full HTTPS API base URL; local loopback addresses may also use HTTP. Do not include keys, usernames, query parameters or fragments."));
}

internal static class ChatApiModel
{
    private static readonly Regex Name = new(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\-]{0,255}$", RegexOptions.CultureInvariant);
    internal static bool IsValid(string? model) => model != null && Name.IsMatch(model) &&
        !model.Contains("://", StringComparison.Ordinal) && !model.Contains("..", StringComparison.Ordinal);
}

internal enum ApiKeyChange { Keep, Replace, Delete }

// This transient command is never serialized or logged. ToString deliberately
// excludes all fields because the update can contain a newly typed credential.
internal sealed class ChatApiConnectionUpdate
{
    internal bool UseApi { get; }
    internal ChatApiProfile Profile { get; }
    internal ApiKeyChange KeyChange { get; }
    internal string? NewApiKey { get; }
    internal ChatApiConnectionUpdate(bool useApi, ChatApiProfile profile, ApiKeyChange keyChange, string? newApiKey)
    { UseApi = useApi; Profile = profile; KeyChange = keyChange; NewApiKey = newApiKey; }
    public override string ToString() => nameof(ChatApiConnectionUpdate);
}
