using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopPet;

// Synthetic names only; no real pipe, pairing record, or RPC is accessed.
internal static class CodexEndpointPolicyChecks
{
    internal static object Run()
    {
        var checks = new List<string>();
        void Check(bool passed, string label)
        {
            if (!passed) throw new InvalidOperationException("Endpoint check: " + label);
            checks.Add(label);
        }
        string id = "12345678-90ab-4cde-8123-456789abcdef";
        string modern = @"\\.\pipe\codex-app-tools-" + id;
        string legacy = @"\\.\pipe\codex-browser-use-" + id;
        Check(CodexEndpointPolicy.TryNormalize(modern, out var normalized) && normalized == modern, "current AppTools endpoint accepted");
        Check(CodexEndpointPolicy.TryNormalize(legacy, out normalized) && normalized == legacy, "legacy pairing endpoint accepted");
        Check(CodexEndpointPolicy.TryNormalize(" " + modern.ToUpperInvariant() + " ", out normalized) && normalized == modern, "local endpoint normalization is deterministic");
        foreach (string? invalid in new[] { null, "", "codex-app-tools-" + id, @"\\server\pipe\codex-app-tools-" + id,
            @"\\.\pipe\unrelated-" + id, modern + "-extra", modern + @"\child", modern.Replace(id, "not-a-guid"),
            modern.Replace(id, id.Replace("-", "")), modern.Replace(id, "{" + id + "}"), @"C:\codex-app-tools-" + id,
            modern.Replace(id, " " + id), modern.Replace(id, Guid.Empty.ToString("D")) })
            Check(!CodexEndpointPolicy.TryNormalize(invalid, out _), "nonlocal, malformed or unrelated endpoint rejected");

        var patterns = new List<string>();
        var found = CodexEndpointPolicy.Discover((root, pattern) =>
        {
            Check(root == @"\\.\pipe\", "discovery remains local");
            patterns.Add(pattern);
            return pattern == "codex-app-tools-*" ? new[] { modern, modern.ToUpperInvariant(), "unrelated" } : new[] { legacy };
        });
        Check(patterns.SequenceEqual(new[] { "codex-app-tools-*", "codex-browser-use-*" }), "only two constrained patterns enumerated in priority order");
        Check(found.SequenceEqual(new[] { modern, legacy }), "new endpoint precedes legacy and duplicates are removed");
        found = CodexEndpointPolicy.Discover((_, pattern) => pattern == "codex-app-tools-*" ? new[] { modern } :
            Enumerable.Range(0, 100).Select(i => @"\\.\pipe\codex-browser-use-" + i.ToString("x8") + "-0000-4000-8000-000000000000"));
        Check(found.Count == 64 && found[0] == modern, "legacy pipe volume cannot crowd out new AppTools and cap remains 64");
        found = CodexEndpointPolicy.Discover((_, pattern) => pattern == "codex-app-tools-*" ? throw new IOException("fixture") : new[] { legacy });
        Check(found.SequenceEqual(new[] { legacy }), "legacy discovery remains available after an enumeration failure");
        return new { passed = true, assertions = checks.Count, checks, rpcRequestsSent = 0 };
    }
}
