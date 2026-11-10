using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DesktopPet.WorkCompanion;

internal static class WorkCompanionSettingsChecks
{
    // A unique test directory is injected; never opens the user's feature file.
    internal static int Run()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "work-companion-settings-fixture-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        bool passed = false; string? error = null;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Directory.CreateDirectory(fixture);
            var path = Path.Combine(fixture, "settings.json");
            var store = new WorkCompanionOptionsStore(path);
            Check(!store.Load().Enabled && !File.Exists(path), "missing_file_stays_disabled_without_creating_settings");
            var defaults = new WorkCompanionOptions();
            Check(defaults.ObserveApplications && !defaults.IncludeWindowTitles && !defaults.SpeakReactions && defaults.BreakAfterMinutes == 45 && defaults.CooldownMinutes == 5,
                "new_feature_defaults_are_explicit_opt_in_without_titles_or_speech");
            Check(defaults.WorkEnabled && defaults.GameEnabled && defaults.MediaEnabled && defaults.DailyEnabled && defaults.QuietFullscreen &&
                defaults.GameBreakAfterMinutes == 60 && defaults.MediaBreakAfterMinutes == 60 && defaults.DailyBreakAfterMinutes == 45 && defaults.AppActivities.Count == 0,
                "all_contexts_available_but_master_still_opt_in_and_fullscreen_quiet_by_default");
            File.WriteAllText(path, "{\"Enabled\":true,\"IncludeWindowTitles\":true,\"BreakAfterMinutes\":30,\"CooldownMinutes\":10}");
            var legacy = store.Load();
            Check(legacy.Enabled && legacy.IncludeWindowTitles && legacy.BreakAfterMinutes == 30 && legacy.CooldownMinutes == 10 &&
                legacy.WorkEnabled && legacy.GameEnabled && legacy.MediaEnabled && legacy.DailyEnabled && legacy.QuietFullscreen && legacy.AppActivities.Count == 0,
                "legacy_work_settings_keep_choices_and_gain_safe_context_defaults");
            var normalized = (defaults with { BreakAfterMinutes = -1, CooldownMinutes = int.MaxValue,
                ExcludedApps = new[] { " Chrome.EXE ", "chrome", " Code ", @"C:\\private\\app.exe", "*", "../secret", "bad\nname" } }).Normalize();
            Check(normalized.BreakAfterMinutes == 45 && normalized.CooldownMinutes == 5, "unsupported_durations_are_bounded");
            Check(normalized.ExcludedApps.SequenceEqual(new[] { "chrome", "code" }), "excluded_names_reject_paths_wildcards_and_control_characters_and_deduplicate");
            Check((defaults with { ExcludedApps = Enumerable.Range(0, 90).Select(i => "app" + i).ToArray() }).Normalize().ExcludedApps.Length == 64,
                "excluded_app_count_is_bounded");
            Check(!WorkCompanionOptions.TryParseExcludedApps("chrome,C:\\secret\\browser.exe", out _), "interactive_exclusion_edit_rejects_whole_invalid_list");
            Check(WorkCompanionOptions.TryParseExcludedApps("Chrome.exe\r\ncode，chrome", out var names) && names.SequenceEqual(new[] { "chrome", "code" }),
                "interactive_exclusions_support_newlines_and_localized_commas");
            Check(WorkCompanionOptions.TryParseExcludedApps("osu!.exe", out var punctuationNames) && punctuationNames.SequenceEqual(new[] { "osu!" }),
                "real_osu_process_name_can_be_excluded_without_accepting_wildcards");
            var a = defaults.Normalize(); a.ExcludedApps[0] = "mutated";
            Check(defaults.ExcludedApps[0] != "mutated" && new WorkCompanionOptions().ExcludedApps[0] != "mutated", "normalized_snapshots_do_not_share_exclusion_arrays");
            var invalidMappings = defaults with { GameBreakAfterMinutes = -1, MediaBreakAfterMinutes = 200, DailyBreakAfterMinutes = 0,
                AppActivities = new Dictionary<string, string>
                { [" MyGame.EXE "] = " GAME ", ["chrome"] = "media", ["invalid"] = "admin", [@"C:\private\game.exe"] = "game",
                    ["duplicate.EXE"] = "game", ["DUPLICATE"] = "work" } };
            var mappingOptions = invalidMappings.Normalize();
            Check(mappingOptions.AppActivities.Count == 2 && mappingOptions.AppActivities["mygame"] == "game" && mappingOptions.AppActivities["chrome"] == "media",
                "overrides_normalize_case_and_extension_reject_paths_unknown_scenes_and_ambiguous_duplicates");
            Check(mappingOptions.GameBreakAfterMinutes == 60 && mappingOptions.MediaBreakAfterMinutes == 60 && mappingOptions.DailyBreakAfterMinutes == 45,
                "invalid_context_durations_restore_context_specific_defaults");
            mappingOptions.AppActivities["mygame"] = "daily";
            Check(invalidMappings.AppActivities[" MyGame.EXE "] == " GAME " && defaults.AppActivities.Count == 0,
                "normalized_override_dictionaries_have_independent_ownership");
            Check((defaults with { AppActivities = Enumerable.Range(0, 90).ToDictionary(i => "game" + i, _ => "game") }).Normalize().AppActivities.Count == 64,
                "app_context_override_count_is_bounded");
            foreach (int duration in new[] { 30, 45, 60, 90 })
            {
                var expected = defaults with { Enabled = true, BreakAfterMinutes = duration, CooldownMinutes = 10,
                    IncludeWindowTitles = true, SpeakReactions = true, ExcludedApps = new[] { "chrome", "code" },
                    WorkEnabled = false, GameEnabled = true, MediaEnabled = false, DailyEnabled = true, QuietFullscreen = false,
                    GameBreakAfterMinutes = duration, MediaBreakAfterMinutes = duration, DailyBreakAfterMinutes = duration,
                    AppActivities = new Dictionary<string, string> { ["mygame"] = "game", ["browser"] = "daily", ["Osu!.exe"] = "game" } };
                store.Save(expected); var restored = store.Load();
                Check(restored.Enabled && restored.BreakAfterMinutes == duration && restored.CooldownMinutes == 10 && restored.IncludeWindowTitles && restored.SpeakReactions && restored.ExcludedApps.SequenceEqual(expected.ExcludedApps),
                    "atomic_settings_roundtrip_" + duration);
                Check(!restored.WorkEnabled && restored.GameEnabled && !restored.MediaEnabled && restored.DailyEnabled && !restored.QuietFullscreen &&
                    restored.GameBreakAfterMinutes == duration && restored.MediaBreakAfterMinutes == duration && restored.DailyBreakAfterMinutes == duration &&
                    restored.AppActivities.Count == 3 && restored.AppActivities["mygame"] == "game" && restored.AppActivities["browser"] == "daily" && restored.AppActivities["osu!"] == "game",
                    "context_toggles_durations_and_overrides_roundtrip_" + duration);
            }
            Check(Directory.GetFiles(fixture).Length == 1, "successful_atomic_replacements_leave_no_temporary_files");
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            {
                string[] allowed = { "Enabled", "ObserveApplications", "IncludeWindowTitles", "ReactToAppChanges", "BreakReminders", "BreakAfterMinutes", "UseCodexEvents", "SpeakReactions", "CooldownMinutes", "ExcludedApps",
                    "WorkEnabled", "GameEnabled", "MediaEnabled", "DailyEnabled", "QuietFullscreen", "GameBreakAfterMinutes", "MediaBreakAfterMinutes", "DailyBreakAfterMinutes", "AppActivities" };
                Check(json.RootElement.EnumerateObject().All(p => allowed.Contains(p.Name)), "persisted_schema_contains_options_only_no_observation_history_or_replies");
            }
            string before = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { store.Save(defaults); } catch (IOException) { failed = true; }
                Check(failed, "locked_destination_reports_failed_save");
            }
            Check(File.ReadAllText(path) == before && Directory.GetFiles(fixture).Length == 1, "failed_replace_preserves_previous_settings_and_cleans_exact_temporary_file");
            foreach (string invalid in new[] { "not json", "{\"Enabled\":true,", "null", "{\"Enabled\":\"true\"}", new string(' ', 33 * 1024) })
            {
                File.WriteAllText(path, invalid, new UTF8Encoding(false));
                Check(!store.Load().Enabled, "malformed_or_oversize_config_fails_closed_" + checks.Count);
            }
            File.WriteAllText(path, "{\"Enabled\":true,\"ExcludedApps\":null,\"AppActivities\":null,\"CooldownMinutes\":0}", new UTF8Encoding(false));
            Check(store.Load().ExcludedApps.Length > 0 && store.Load().AppActivities.Count == 0 && store.Load().CooldownMinutes == 5, "null_collections_and_invalid_numbers_restore_safe_defaults");
            passed = true;
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            // This test created this exact unique directory and these top-level files.
            if (Directory.Exists(fixture))
            {
                foreach (string file in Directory.GetFiles(fixture)) File.Delete(file);
                Directory.Delete(fixture);
            }
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "work-companion-settings-validation.json"), JsonSerializer.Serialize(new
        { passed, version = AppVersion.Number, assertions = checks.Count, checks, error, realModelOperations = 0, userSettingsWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
