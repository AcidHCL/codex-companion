using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopPet.ScreenUnderstanding;

internal static class ScreenUnderstandingSettingsChecks
{
    // Every file belongs to a newly created fixture directory. No production
    // settings, screenshots, user windows, model endpoint or chat DB is accessed.
    internal static object Run()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "screen-settings-fixture-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        Directory.CreateDirectory(fixture);
        try
        {
            string path = Path.Combine(fixture, "options.json");
            var options = new ScreenUnderstandingOptionsStore(path);
            var defaults = options.Load();
            Check(!defaults.Enabled && defaults.ReadWindowText && defaults.UseScreenshots && defaults.LearnApplications &&
                !defaults.AllowRemoteEndpoint && defaults.AutomaticIntervalMinutes == 5 && !File.Exists(path),
                "missing_options_default_to_off_and_local_without_writing_a_file");
            File.WriteAllText(path, "{\"Enabled\":true}");
            Check(options.Load() is { Enabled: true, ReadWindowText: true, UseScreenshots: true, LearnApplications: true, AllowRemoteEndpoint: false },
                "partial_options_receive_safe_independent_defaults");
            options.Save(defaults with { Enabled = true, ReadWindowText = false, UseScreenshots = false,
                LearnApplications = false, AllowRemoteEndpoint = true, AutomaticIntervalMinutes = 10 });
            Check(options.Load() is { Enabled: true, ReadWindowText: false, UseScreenshots: false,
                LearnApplications: false, AllowRemoteEndpoint: true, AutomaticIntervalMinutes: 10 }, "options_round_trip_without_implicit_capture_or_model_operation");
            Check((defaults with { AutomaticIntervalMinutes = -1 }).Normalize().AutomaticIntervalMinutes == 5,
                "unsupported_intervals_are_normalized");
            File.WriteAllText(path, "{invalid"); Check(!options.Load().Enabled, "malformed_options_fail_closed");
            File.WriteAllText(path, new string('x', 8193)); Check(!options.Load().Enabled, "oversized_options_are_not_parsed");
            Check(!Directory.GetFiles(fixture, "*.tmp").Any(), "atomic_options_save_leaves_no_temporary_file");

            DateTimeOffset now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
            string memoryPath = Path.Combine(fixture, "apps.json");
            var store = new LearnedApplicationStore(memoryPath, () => now);
            string identity = new('a', 64), otherIdentity = new('b', 64);
            Check(store.List().Count == 0 && !File.Exists(memoryPath), "empty_memory_load_is_read_only");
            Check(!store.SaveLearned("mygame", identity, "game", .84) && !store.SaveLearned("mygame", identity, "game", double.NaN) &&
                !store.SaveLearned("mygame", identity, "game", 1.01) && !store.SaveLearned("mygame", "path", "game", .95) &&
                !store.SaveLearned(@"C:\secret\game.exe", identity, "game", .95) &&
                !store.SaveLearned("mygame", identity, "unknown", .95) && !File.Exists(memoryPath),
                "low_confidence_unbounded_values_paths_invalid_identity_and_unknown_category_are_rejected_without_writes");
            Check(store.SaveLearned(" MyGame.EXE ", identity.ToUpperInvariant(), " GAME ", .92789), "confident_app_classification_can_be_remembered");
            var saved = store.GetByApp("MYGAME.exe")!;
            Check(saved.App == "mygame" && saved.Identity == identity && saved.Activity == "game" && saved.Confidence == .9279 &&
                saved.ExpiresAt - saved.LearnedAt == TimeSpan.FromDays(90) && !saved.UserCorrected,
                "app_memory_normalizes_basename_identity_category_confidence_and_expiry");
            Check(new LearnedApplicationStore(memoryPath, () => now).GetByApp("mygame") == saved,
                "valid_memory_survives_restart");
            using (var json = JsonDocument.Parse(File.ReadAllText(memoryPath)))
            {
                string[] permitted = { "Id", "App", "Identity", "Activity", "Confidence", "LearnedAt", "ExpiresAt", "UserCorrected" };
                Check(json.RootElement[0].EnumerateObject().Select(item => item.Name).OrderBy(x => x).SequenceEqual(permitted.OrderBy(x => x)),
                    "persisted_schema_contains_no_summary_title_text_screenshot_prompt_or_path_fields");
            }
            Check(store.UpdateCategory(saved.Id, "work") && store.GetByApp("mygame") is { Activity: "work", UserCorrected: true, Confidence: 1 },
                "user_can_correct_a_learned_category");
            Check(!store.SaveLearned("mygame", identity, "game", .99) && store.GetByApp("mygame")?.Activity == "work",
                "automatic_learning_never_overwrites_a_user_correction");
            Check(!store.UpdateCategory(saved.Id, "run commands"), "memory_editor_only_accepts_category_enum");
            Check(store.SaveLearned("mygame", otherIdentity, "game", .95) && store.GetByApp("mygame") == null,
                "same_basename_multiple_executable_identities_is_ambiguous_even_when_a_memory_exists");
            Check(store.GetByIdentity("mygame", identity)?.Activity == "work" && store.GetByIdentity("mygame", otherIdentity)?.Activity == "game",
                "exact_identity_lookup_distinguishes_same_name_applications");
            var second = store.GetByIdentity("mygame", otherIdentity)!;
            Check(store.Delete(second.Id) && store.GetByApp("mygame")?.Id == saved.Id && !store.Delete(second.Id),
                "delete_removes_only_selected_memory_and_restores_unambiguous_lookup");
            now = now.AddDays(90);
            Check(store.List().Count == 0 && store.GetByApp("mygame") == null && new LearnedApplicationStore(memoryPath, () => now).List().Count == 0,
                "expired_entries_are_ignored_in_memory_and_after_restart");

            bool inserted = true;
            for (int index = 0; index < 260; index++)
            {
                now = now.AddSeconds(1);
                inserted &= store.SaveLearned("fixtureapp" + index, index.ToString("x64"), "daily", .9);
                if (index == 0) inserted &= store.UpdateCategory(store.GetByApp("fixtureapp0")!.Id, "work");
            }
            Check(inserted && store.List().Count == LearnedApplicationStore.MaximumEntries && store.GetByApp("fixtureapp1") == null &&
                store.GetByApp("fixtureapp259") != null, "memory_capacity_evicts_oldest_automatic_entry");
            Check(store.GetByApp("fixtureapp0") is { UserCorrected: true, Activity: "work" },
                "capacity_eviction_preserves_user_corrected_entries");
            Check(new FileInfo(memoryPath).Length < 256 * 1024, "maximum_memory_stays_within_bounded_file_size");
            string stableFile = File.ReadAllText(memoryPath);
            string oldFile = Path.Combine(fixture, "apps-before-failed-save.json");
            File.Move(memoryPath, oldFile); Directory.CreateDirectory(memoryPath);
            bool failed = false;
            try { store.SaveLearned("anotherapp", identity, "work", .99); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed && store.GetByApp("anotherapp") == null && store.List().Count == 256,
                "failed_atomic_save_does_not_mutate_live_memory");
            Directory.Delete(memoryPath); File.Move(oldFile, memoryPath);
            Check(File.ReadAllText(memoryPath) == stableFile, "failed_save_preserves_previous_memory_file");
            store.Clear();
            Check(store.List().Count == 0 && new LearnedApplicationStore(memoryPath, () => now).List().Count == 0,
                "clear_removes_all_app_memory_and_survives_restart");
            File.WriteAllText(memoryPath, new string('x', 256 * 1024 + 1));
            Check(new LearnedApplicationStore(memoryPath, () => now).List().Count == 0, "oversized_memory_file_is_ignored");
            File.WriteAllText(memoryPath, "[null,{\"App\":\"private\",\"Identity\":\"bad\",\"Activity\":\"game\"}]");
            Check(new LearnedApplicationStore(memoryPath, () => now).List().Count == 0, "malformed_entries_do_not_become_memory");
            return new { passed = true, assertions = checks.Count, checks, userSettingsWrites = 0, realModelRequests = 0, capturedWindows = 0 };
        }
        finally
        {
            string absolute = Path.GetFullPath(fixture);
            if (absolute.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(absolute).StartsWith("screen-settings-fixture-", StringComparison.Ordinal) && Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }
}
