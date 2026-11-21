using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopPet.WorkCompanion;

internal static class CompanionDiagnosticsChecks
{
    // Only private temporary files and synthetic clocks/codes. Never initialize
    // the production singleton, observe windows, or invoke a model.
    internal static object Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Companion diagnostics: " + name); checks.Add(name); }
        string directory = Path.Combine(Path.GetTempPath(), "AIPet-companion-diagnostics-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DateTimeOffset epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            long elapsed = 0;
            string path = Path.Combine(directory, "events.jsonl");
            var sink = new CompanionDiagnosticSink(path, () => epoch.AddMilliseconds(elapsed), () => elapsed);
            Check(sink.ReadRecent().Count == 0 && !File.Exists(path), "reading_absent_log_creates_no_file");
            Check(!sink.Record("private_application_name", "start") && !sink.Record("model", "private reply or title") && !File.Exists(path),
                "unknown_layers_and_dynamic_text_are_rejected_without_writing");
            Check(sink.Record("layer1", "start") && !sink.Record("work_companion", "start"), "aliases_share_one_rate_limit_key");
            elapsed = 59_999;
            Check(!sink.Record("work_companion", "start"), "same_reason_is_suppressed_within_sixty_seconds");
            elapsed = 60_000;
            Check(sink.Record("work_companion", "start") && sink.Record("model", "model_request") && sink.Record("screen", "model_request"),
                "sixty_seconds_and_distinct_layer_reason_pairs_are_allowed");
            string[] lines = File.ReadAllLines(path);
            Check(lines.Length == 4 && lines.All(line =>
            {
                using var json = JsonDocument.Parse(line);
                string[] fields = json.RootElement.EnumerateObject().Select(field => field.Name).OrderBy(name => name).ToArray();
                return fields.SequenceEqual(new[] { "code", "layer", "timestamp" });
            }), "persisted_schema_contains_only_timestamp_layer_and_code");
            var recent = sink.ReadRecent();
            Check(recent.Count == 4 && recent.All(row => row.ReasonZh.Length > 0 && row.ReasonEn.Length > 0 &&
                row.LayerZh.Length > 0 && row.LayerEn.Length > 0) && recent[0].Display(false) == recent[0].ReasonZh &&
                recent[0].Display(true) == recent[0].ReasonEn, "fixed_bilingual_explanations_are_mapped_only_for_display");
            Check(!File.ReadAllText(path).Contains("private", StringComparison.Ordinal) && !File.ReadAllText(path).Contains("陪伴", StringComparison.Ordinal),
                "neither_dynamic_text_nor_display_text_is_persisted");

            string parallelPath = Path.Combine(directory, "parallel.jsonl");
            var parallel = new CompanionDiagnosticSink(parallelPath, () => epoch, () => 0);
            Parallel.For(0, 100, _ => parallel.Record("model", "model_busy"));
            Check(File.ReadAllLines(parallelPath).Length == 1, "parallel_duplicate_calls_are_atomic_and_rate_limited");

            string rotationPath = Path.Combine(directory, "rotation.jsonl");
            var rotating = new CompanionDiagnosticSink(rotationPath, () => epoch.AddMilliseconds(elapsed), () => elapsed);
            // Start at an exact byte boundary instead of thousands of rapid
            // writes whose individual IO failures could go unnoticed. Both
            // rotation and replacing the previous archive are checked directly.
            SeedFullLog(rotationPath, epoch, "observing");
            elapsed += 60_000;
            Check(rotating.Record("work_companion", "observing"), "first_full_file_rotation_write_succeeds");
            Check(File.Exists(rotationPath + ".1"), "first_full_file_rotation_creates_archive");
            SeedFullLog(rotationPath, epoch, "candidate");
            elapsed += 60_000;
            Check(rotating.Record("work_companion", "observing"), "second_full_file_rotation_replaces_archive_and_writes");
            Check(new FileInfo(rotationPath).Length <= CompanionDiagnosticSink.MaximumFileBytes &&
                new FileInfo(rotationPath + ".1").Length == CompanionDiagnosticSink.MaximumFileBytes,
                "both_rotation_files_respect_exact_byte_limit");
            Check(Directory.GetFiles(directory).Count(file => Path.GetFileName(file).StartsWith("rotation.jsonl", StringComparison.Ordinal)) == 2,
                "rotation_retains_exactly_one_archive_without_platform_wildcard_matching");
            var rotated = rotating.ReadRecent();
            Check(rotated.Count == 12 && rotated[^1].Timestamp == epoch.AddMilliseconds(elapsed) &&
                rotated.Take(11).All(row => row.Code == "candidate") &&
                rotating.ReadRecent(500).Count == 50 && rotating.ReadRecent(0).Count == 0,
                "recent_entries_are_bounded_and_include_latest_events_after_rotation");

            string malformedPath = Path.Combine(directory, "malformed.jsonl");
            File.WriteAllText(malformedPath,
                "not json\n" +
                "{\"timestamp\":\"2026-01-01T00:00:00Z\",\"layer\":\"model\",\"code\":\"private_model_reply\"}\n" +
                "{\"timestamp\":\"2026-01-01T00:00:00Z\",\"layer\":\"model\",\"code\":\"start\",\"text\":\"private text\"}\n" +
                "{\"timestamp\":\"2026-01-01T00:00:00Z\",\"layer\":\"model\",\"layer\":\"screen\",\"code\":\"start\"}\n" +
                "{\"timestamp\":\"2026-01-01T00:00:00Z\",\"layer\":\"model\",\"code\":\"start\"}\n", new UTF8Encoding(false));
            Check(new CompanionDiagnosticSink(malformedPath).ReadRecent().Count == 1,
                "malformed_unknown_extra_and_duplicate_fields_never_become_display_text");
            File.WriteAllText(malformedPath, new string('x', CompanionDiagnosticSink.MaximumFileBytes + 1));
            var oversized = new CompanionDiagnosticSink(malformedPath, () => epoch, () => 0);
            Check(oversized.ReadRecent().Count == 0 && oversized.Record("lifecycle", "start") &&
                new FileInfo(malformedPath).Length <= CompanionDiagnosticSink.MaximumFileBytes,
                "externally_oversized_files_are_not_read_or_archived_unbounded");

            string blockedPath = Path.Combine(directory, "blocked");
            Directory.CreateDirectory(blockedPath);
            var blocked = new CompanionDiagnosticSink(blockedPath, () => epoch, () => elapsed);
            Check(!blocked.Record("lifecycle", "start") && blocked.ReadRecent().Count == 0,
                "io_failures_do_not_escape_or_interrupt_companionship");
            Directory.Delete(blockedPath); elapsed += 60_000;
            Check(blocked.Record("lifecycle", "start"), "logging_recovers_after_io_failure_and_retry_interval");
            string[] required = { "start", "candidate", "host_busy", "cooldown", "model_busy", "model_not_ready", "model_request", "model_silent",
                "cancelled", "stale_result", "presented", "fullscreen", "input_active", "away", "own_app", "excluded", "observer_unavailable",
                "screen_capture", "screen_capture_failed", "metadata_fallback", "screen_analyze", "screen_result", "screen_memory_saved", "screen_disabled", "remote_disabled",
                "disabled", "paused", "user_interaction", "observing", "waiting_stable", "context_disabled", "context_updated", "notification_priority",
                "unsupported_model", "protected_content", "capture_timeout", "no_result", "pet_ui_open", "speech_busy", "interaction_busy", "chat_busy" };
            Check(required.All(code => CompanionDiagnosticCatalog.TryEntry(epoch, "work_companion", code, out var row) && row != null),
                "all_requested_reason_codes_have_fixed_bilingual_mappings");
            var captureFailures = new[]
            {
                "foreground_unavailable", "foreground_changed", "session_unavailable", "excluded", "desktop_shell",
                "protected_window", "window_size_unavailable", "identity_unavailable", "accessibility_unavailable",
                "accessibility_limit", "accessibility_timeout", "content_unavailable", "worker_unavailable",
                "worker_isolation_unavailable", "invalid_request", "protected_content", "capture_timeout"
            };
            Check(captureFailures.All(code =>
            {
                var reason = CompanionDiagnosticCatalog.ScreenCaptureReason(code);
                return reason.Code == code && CompanionDiagnosticCatalog.TryEntry(epoch, "screen", code, out var entry) &&
                    reason.Zh == entry!.ReasonZh && reason.En == entry.ReasonEn;
            }), "specific_window_capture_failures_have_matching_log_and_settings_explanations");
            Check(CompanionDiagnosticCatalog.ScreenCaptureReason("password").Code == "protected_content" &&
                CompanionDiagnosticCatalog.ScreenCaptureReason("fullscreen_quiet").Code == "fullscreen" &&
                CompanionDiagnosticCatalog.ScreenCaptureReason("timeout").Code == "capture_timeout" &&
                new[] { "arbitrary private title", "presented", "capture_unavailable" }.All(value =>
                    CompanionDiagnosticCatalog.ScreenCaptureReason(value).Code == "screen_capture_failed"),
                "capture_diagnostics_normalize_known_aliases_and_discard_unrecognized_input");
            return new { passed = true, assertions = checks.Count, checks, productionLogWrites = 0, observedDesktop = false, modelRequests = 0 };
        }
        finally
        {
            // This unique directory contains only this check's synthetic files.
            string resolved = Path.GetFullPath(directory);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("AIPet-companion-diagnostics-check-", StringComparison.Ordinal))
            {
                foreach (string file in Directory.GetFiles(resolved)) File.Delete(file);
                foreach (string child in Directory.GetDirectories(resolved)) Directory.Delete(child);
                Directory.Delete(resolved);
            }
        }
    }

    private static void SeedFullLog(string path, DateTimeOffset timestamp, string code)
    {
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { timestamp, layer = "work_companion", code }) + "\n");
        byte[] fixture = Enumerable.Repeat((byte)'\n', CompanionDiagnosticSink.MaximumFileBytes).ToArray();
        for (int offset = 0; offset + line.Length <= fixture.Length; offset += line.Length)
            line.CopyTo(fixture, offset);
        File.WriteAllBytes(path, fixture);
    }
}
