using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopPet;

// Unique fixture directory and fabricated credentials only. No network, user
// settings, real credentials, models, or application state are accessed.
internal static class ChatApiSettingsChecks
{
    internal static object Run(string temporaryParent)
    {
        var checks = new List<string>();
        void Check(bool success, string label) { if (!success) throw new InvalidOperationException("API settings check failed: " + label); checks.Add(label); }
        bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        Check(ChatApiEndpoint.Parse("HTTPS://API.EXAMPLE.TEST:443/custom/v1").AbsoluteUri == "https://api.example.test/custom/v1/", "canonical_https_preserves_custom_base_path");
        Check(ChatApiEndpoint.Parse("http://127.0.0.1:1234/v1").Port == 1234 && ChatApiEndpoint.Parse("http://[::1]:1234/v1").IsLoopback, "loopback_http_and_ipv6_supported");
        foreach (string invalid in new[] { "", "api.example.test", "file:///v1", "http://api.example.test/v1", "https://secret@example.test/v1",
            "https://api.example.test/v1?key=fixture", "https://api.example.test/v1#fixture", "https://api.example.test/a/../v1",
            "https://api.example.test/a/%2e%2e/v1", "https://api.example.test/%252e%252e/v1",
            "https://api.example.test/v1/chat/completions", "https://api.example.test/v1/models" })
            Check(Rejects(() => ChatApiEndpoint.Parse(invalid)), "invalid_endpoint_" + checks.Count);
        Check(new[] { "gpt-4.1-mini", "deepseek-chat", "qwen/model:latest", "provider/model@v1+fast" }.All(ChatApiModel.IsValid), "provider_model_identifiers_supported");
        Check(new[] { "", "two models", "https://api.example.test", "../model", new string('a', 257), "模型" }.All(x => !ChatApiModel.IsValid(x)), "invalid_model_identifiers_rejected");
        var profile = new ChatApiProfile { BaseUrl = "https://api.example.test/custom/v1/", Model = "gpt-4.1-mini", SupportsImages = true };
        Check(profile.Validate().BaseUrl == "https://api.example.test/custom/v1", "profile_canonicalized_without_secrets");
        Check(new ChatApiProfile().Validate(requireModel: false).Model == "" && Rejects(() => new ChatApiProfile().Validate()), "listing_can_precede_model_selection");
        Check(!new ChatApiProfile().SupportsImages && !new ChatApiProfile().SupportsTools, "provider_capabilities_opt_in");
        const string fixtureKey = "fixture-only-not-a-real-key-0123456789";
        string json = JsonSerializer.Serialize(profile);
        var update = new ChatApiConnectionUpdate(true, profile, ApiKeyChange.Replace, fixtureKey);
        Check(!json.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) && !JsonSerializer.Serialize(update).Contains(fixtureKey, StringComparison.Ordinal) &&
            !update.ToString().Contains(fixtureKey, StringComparison.Ordinal), "profile_update_serialization_and_logging_exclude_key");
        Check(ApiKeyStore.ValidateKey("  " + fixtureKey + "  ") == fixtureKey && ApiKeyStore.ValidateKey(new string('a', 8192)).Length == 8192,
            "key_input_limits_match_transport");
        foreach (string invalid in new[] { "", " ", "fixture key", "fixture\nkey", "密钥", new string('a', 8193) })
            Check(Rejects(() => ApiKeyStore.ValidateKey(invalid)), "invalid_key_" + checks.Count);

        string parent = Path.GetFullPath(temporaryParent);
        string directory = Path.Combine(parent, "api-key-fixture-" + Guid.NewGuid().ToString("N"));
        var endpoint = profile.Endpoint; var otherPath = ChatApiEndpoint.Parse("https://api.example.test/other/v1");
        var otherHost = ChatApiEndpoint.Parse("https://another.example.test/custom/v1");
        var store = new ApiKeyStore(directory);
        string PathFor(Uri uri) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".key");
        bool StorageRejects(Action action) { try { action(); return false; } catch (IOException) { return true; } }
        try
        {
            Check(store.Read(endpoint) == null && !store.HasKey(endpoint) && !Directory.Exists(directory), "missing_key_read_has_no_filesystem_writes");
            store.Save(endpoint, fixtureKey);
            Check(store.Read(endpoint) == fixtureKey && store.HasKey(endpoint), "current_user_dpapi_roundtrip");
            Check(new ApiKeyStore(directory).Read(ChatApiEndpoint.Parse("https://API.EXAMPLE.TEST:443/custom/v1/")) == fixtureKey,
                "canonical_endpoint_roundtrip_across_instances");
            byte[] encrypted = File.ReadAllBytes(PathFor(endpoint));
            Check(!Encoding.UTF8.GetString(encrypted).Contains(fixtureKey, StringComparison.Ordinal) && Directory.GetFiles(directory).All(x => !x.Contains("example", StringComparison.Ordinal)),
                "only_ciphertext_and_hashed_endpoint_written");
            Check(store.Read(otherPath) == null && store.Read(otherHost) == null, "different_path_or_host_never_reuses_key");
            File.WriteAllBytes(PathFor(otherPath), encrypted);
            Check(StorageRejects(() => store.Read(otherPath)) && !store.HasKey(otherPath), "ciphertext_is_bound_to_endpoint_entropy");
            store.Delete(otherPath);
            store.Save(otherHost, "second-fixture-key");
            store.Save(endpoint, "replacement-fixture-key");
            Check(store.Read(endpoint) == "replacement-fixture-key" && Directory.GetFiles(directory, "*.tmp").Length == 0, "key_replace_is_atomic_and_cleans_temporary_file");
            byte[] damaged = File.ReadAllBytes(PathFor(endpoint)); damaged[^1] ^= 0x20; File.WriteAllBytes(PathFor(endpoint), damaged);
            Check(StorageRejects(() => store.Read(endpoint)) && !store.HasKey(endpoint), "tampered_blob_fails_closed");
            File.WriteAllBytes(PathFor(endpoint), Encoding.UTF8.GetBytes(fixtureKey));
            Check(StorageRejects(() => store.Read(endpoint)), "plaintext_file_is_never_accepted");
            File.WriteAllBytes(PathFor(endpoint), new byte[16_385]);
            Check(StorageRejects(() => store.Read(endpoint)), "oversized_file_rejected_before_decryption");
            store.Delete(endpoint);
            Check(store.Read(endpoint) == null && store.Read(otherHost) == "second-fixture-key", "delete_is_endpoint_scoped");
            return new { passed = true, assertions = checks.Count, checks, realApiRequests = 0, userSettingsWrites = 0, realCredentialsAccessed = false };
        }
        finally
        {
            // Only this unique, freshly created fixture may be removed.
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("api-key-fixture-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
