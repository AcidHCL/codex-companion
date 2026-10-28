using System;
using System.Collections.Generic;
using System.IO;

namespace DesktopPet;

// A constrained discovery hint, never proof of identity: every connection must
// still pass CodexPipeTrust before sending any RPC or saved caller context.
internal static class CodexEndpointPolicy
{
    internal const string PipePathPrefix = @"\\.\pipe\";
    private static readonly string[] KnownNames = { "codex-app-tools-", "codex-browser-use-" };

    internal static bool TryNormalize(string? value, out string endpoint)
    {
        endpoint = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        foreach (string name in KnownNames)
        {
            string prefix = PipePathPrefix + name;
            if (value.Length == prefix.Length + 36 && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(value.Substring(prefix.Length), "D", out var id) && id != Guid.Empty)
            {
                endpoint = prefix + id.ToString("D");
                return true;
            }
        }
        return false;
    }

    internal static IReadOnlyList<string> Discover(Func<string, string, IEnumerable<string>>? enumerate = null)
    {
        enumerate ??= Directory.EnumerateFiles;
        var endpoints = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // AppTools moved away from the browser pipe. Enumerate it first so a
        // large set of legacy browser pipes cannot consume the candidate limit.
        foreach (string name in KnownNames)
        {
            try
            {
                foreach (string path in enumerate(PipePathPrefix, name + "*"))
                {
                    if (!TryNormalize(path, out var endpoint) || !seen.Add(endpoint)) continue;
                    endpoints.Add(endpoint);
                    if (endpoints.Count == 64) return endpoints;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return endpoints;
    }
}
