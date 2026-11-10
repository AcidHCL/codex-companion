using System;

namespace DesktopPet;

internal static class ChatModelConnection
{
    internal static string ActiveModel(DesktopSettings settings) => settings.UseChatApi ? settings.ApiProfile.Model : settings.OllamaModel;
    internal static Uri ActiveEndpoint(DesktopSettings settings) => settings.UseChatApi ? settings.ApiProfile.Endpoint : OllamaEndpoint.Parse(settings.OllamaAddress);
    internal static IOllamaClient CreateClient(DesktopSettings settings, ApiKeyStore? keys = null)
    {
        if (!settings.UseChatApi) return new OllamaClient(OllamaEndpoint.Parse(settings.OllamaAddress), () => settings.OllamaKeepAliveMinutes);
        var profile = settings.ApiProfile;
        var endpoint = profile.Endpoint;
        keys ??= new ApiKeyStore();
        // Capture the endpoint, never resolve credentials through mutable settings.
        return new OpenAiCompatibleClient(endpoint, () => keys.Read(endpoint), profile.Model, profile.SupportsImages, profile.SupportsTools);
    }

    // Keep legacy Ollama scopes unchanged. API scopes distinguish provider and
    // complete base path, so no local history or memory leaks to a new service.
    internal static string Scope(Uri endpoint, bool api) => api
        ? "api:" + ChatApiEndpoint.Parse(endpoint.AbsoluteUri).AbsoluteUri.TrimEnd('/')
        : endpoint.GetLeftPart(UriPartial.Authority);
    internal static string NormalizeScope(string scope) => scope.StartsWith("api:", StringComparison.Ordinal)
        ? Scope(ChatApiEndpoint.Parse(scope[4..]), true)
        : OllamaEndpoint.Parse(scope).AbsoluteUri.TrimEnd('/');
}
