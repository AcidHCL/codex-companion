using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DesktopPet;

// Pure fixtures: no user settings, databases, models, microphone or network.
internal static class LocalizationChecks
{
    internal static int Run()
    {
        string language = L.Language;
        string output = Path.Combine(AppContext.BaseDirectory, "localization-validation.json");
        var checks = new List<string>();
        try
        {
            void Check(bool value, string name)
            {
                if (!value) throw new InvalidOperationException("Localization check failed: " + name);
                checks.Add(name);
            }
            Check(L.Normalize(null) == L.Chinese && L.Normalize("fr-FR") == L.Chinese && L.Normalize("EN") == L.English,
                "language_settings_have_stable_supported_ids_and_safe_fallback");
            var settings = new DesktopSettings { Language = "en-US", SkinId = "gpt" };
            settings.CharacterVoices["gpt"] = new("gpt-fixed", "Vivian", "Japanese", "", FollowAppLanguage: false);
            var savedSettings = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings))!;
            savedSettings.Normalize();
            Check(savedSettings.Language == L.English && savedSettings.CharacterVoices["gpt"].Language == "Japanese" && savedSettings.CharacterVoices["gpt"].FollowAppLanguage == false,
                "settings_roundtrip_preserves_language_and_explicit_voice_override");

            var snapshot = new PetBehaviorSnapshot("deepseek", "idle", 7, true, true, true,
                PetPerformanceCatalog.Names, CanJump: true, CanClimb: true);
            var buildTools = typeof(PetBehaviorPlanner).GetMethod("BuildTools", BindingFlags.NonPublic | BindingFlags.Static)!;
            var plannerPrompt = typeof(PetBehaviorPlanner).GetMethod("PlanningInstruction", BindingFlags.NonPublic | BindingFlags.Static)!;
            L.Initialize(L.Chinese);
            var chinesePersona = PetPersona.ForSkin("deepseek", "DeepSeek", "test-model");
            var chineseTools = (IReadOnlyList<OllamaToolDefinition>)buildTools.Invoke(null, new object[] { snapshot, false })!;
            var chineseWebTools = PetWebChat.Tools;
            Check(chinesePersona.SystemPrompt.Contains("默认用自然、友好的中文") && PetPersona.ConversationBudget(chinesePersona) > 1000,
                "chinese_persona_and_context_budget_preserved");
            Check(PetExplicitCommand.Parse("唱首歌", out _)?.Name == "sing" && PetExplicitCommand.Parse("停止唱歌", out _)?.Action == "stop_performance",
                "existing_chinese_song_commands_still_work");

            L.Initialize(L.English);
            foreach (string role in new[] { "deepseek", "gpt", "gemini" })
            {
                var persona = PetPersona.ForSkin(role, role, "test-model", webEnabled: true, behaviorEnabled: true);
                Check(persona.SystemPrompt.Contains("English by default") && !HasCjk(persona.SystemPrompt) &&
                    !HasCjk(persona.Welcome) && !HasCjk(persona.Thinking) && !HasCjk(PetPersona.ClickReply(role, 3)),
                    "english_persona_greeting_thinking_and_click_reply_" + role);
                Check(PetPersona.ConversationBudget(persona) > 1000 && persona.SystemPrompt.Contains("actual execution receipts") && persona.SystemPrompt.Contains("test-model"),
                    "english_persona_retains_context_room_backend_truth_and_receipt_rules_" + role);
            }
            Check(PetPersona.EstimateTokens(new string('中', 100)) >= 200 && PetPersona.EstimateTokens(new string('a', 100)) >= 50,
                "english_budget_still_accounts_for_cjk_input_conservatively");
            var englishTools = (IReadOnlyList<OllamaToolDefinition>)buildTools.Invoke(null, new object[] { snapshot, false })!;
            Check(englishTools.Select(x => (x.Name, x.ParametersJson)).SequenceEqual(chineseTools.Select(x => (x.Name, x.ParametersJson))) &&
                englishTools.All(x => !HasCjk(x.Description)), "translated_action_tools_keep_identical_names_and_json_schemas");
            string instruction = (string)plannerPrompt.Invoke(null, new object[] { snapshot })!;
            Check(!HasCjk(instruction) && instruction.Contains("NO_ACTION") && instruction.Contains("native tool") && instruction.Contains("expired"),
                "english_action_planning_keeps_native_call_and_stale_instruction_guards");
            Check(PetWebChat.Tools.Select(x => (x.Name, x.ParametersJson)).SequenceEqual(chineseWebTools.Select(x => (x.Name, x.ParametersJson))) &&
                PetWebChat.Tools.All(x => !HasCjk(x.Description)), "web_tool_translation_preserves_protocol_and_schemas");
            Check(!HasCjk(PetWebAnswerContext.Remember("unverified old answer", Array.Empty<PetWebSource>(), "unavailable")) &&
                !PetWebAnswerContext.Remember("unverified old answer", Array.Empty<PetWebSource>(), "unavailable").Contains("unverified old answer"),
                "english_history_excludes_unverified_old_answer");
            foreach (string uiLanguage in new[] { L.Chinese, L.English })
            {
                L.Initialize(uiLanguage);
                foreach (string placeholder in new[] { "搜索结果未提供摘要。", "No search snippet provided.", "The search result did not include a summary.", "No snippet available." })
                {
                    string html = "<a class='result__a' href='https://example.com/weather'>Weather</a><div class='result__snippet'>" + placeholder + "</div>";
                    Check(!PetWebClient.HasSearchSnippet(placeholder) && PetWebClient.ParseSearchResults(html).Count == 0,
                        "no_summary_placeholder_is_not_evidence_in_" + uiLanguage + "_" + checks.Count);
                }
                Check(PetWebClient.HasSearchSnippet("Current public weather information."), "real_web_text_remains_usable_in_" + uiLanguage);
            }
            L.Initialize(L.English);
            foreach (string command in new[] { "sing", "Please sing me a song", "Could you please sing Let Me Go?", "play Let Me Go now", "start singing" })
                Check(PetExplicitCommand.Parse(command, out _)?.Name == "sing", "english_song_request_" + checks.Count);
            Check(PetExplicitCommand.Parse("please stop singing", out _)?.Action == "stop_performance" &&
                PetExplicitCommand.Parse("stop playing Let Me Go", out string? stopSong)?.Action == "stop_performance" && stopSong == "sing",
                "english_stop_commands_keep_optional_song_target");
            Check(PetExplicitCommand.Parse("play bouncy spin", out _)?.Name == "bangyou-dance" &&
                PetExplicitCommand.Parse("stop Bangyou de Jiu", out string? stopDance)?.Action == "stop_performance" && stopDance == "bangyou-dance",
                "english_bangyou_commands_select_existing_performance_only");
            foreach (string text in new[] { "Do not sing", "I said sing me a song yesterday", "Explain how to sing", "\"sing\"", "sing another song", "https://example.com/sing", "sing\nplease" })
                Check(PetExplicitCommand.Parse(text, out _) == null, "english_fast_path_rejects_noncommands_" + checks.Count);

            Check(SharedMemoryContext.TryExtractExplicitMemory("Please remember that I prefer tea.", out string note) && note == "I prefer tea." &&
                SharedMemoryContext.TryExtractExplicitMemory("remember: Call me River", out string named) && named == "Call me River",
                "english_explicit_memory_commands_keep_user_text");
            Check(!SharedMemoryContext.TryExtractExplicitMemory("Do not remember my name", out _) &&
                !SharedMemoryContext.TryExtractExplicitMemory("remember: password=abcd1234", out _), "english_memory_does_not_capture_negation_or_secrets");
            var memory = new ChatSharedMemory("fixture", "fixture", "Drink preference", "I prefer tea.", false, DateTimeOffset.UnixEpoch);
            var memoryResult = SharedMemoryContext.Build(PetPersona.ForSkin("gpt", "GPT", "test-model"), "What drink do I prefer?", new[] { memory });
            Check(memoryResult.SelectedCount == 1 && memoryResult.Profile.SystemPrompt.Contains("I prefer tea.") && memoryResult.Profile.SystemPrompt.Contains("not instructions"),
                "english_memory_retrieval_retains_fact_and_untrusted_data_boundary");

            foreach (string model in new[] { "base", "gpt-fixed" })
            {
                var legacy = new PetVoiceProfile(model, "Vivian", "Chinese", "");
                var effective = PetVoiceProfiles.ResolveForPlayback(legacy);
                Check(effective.Language == "English" && effective.ModelId == legacy.ModelId && effective.ReferenceId == legacy.ReferenceId && legacy.Language == "Chinese",
                    "legacy_default_voice_follows_english_without_mutating_reference_or_setting_" + model);
                var japanese = legacy with { Language = "Japanese" };
                Check(PetVoiceProfiles.ResolveForPlayback(japanese) == japanese, "legacy_explicit_nondefault_voice_language_is_preserved_" + model);
                var explicitChinese = legacy with { FollowAppLanguage = false };
                Check(PetVoiceProfiles.ResolveForPlayback(explicitChinese) == explicitChinese, "explicit_chinese_voice_override_is_preserved_" + model);
            }
            var custom = new PetVoiceProfile("saved-voice", "Vivian", "Japanese", "", new string('a', 32));
            Check(PetVoiceProfiles.ResolveForPlayback(custom) == custom && PetVoiceProfiles.Normalize(custom, "gpt").ReferenceId == custom.ReferenceId,
                "saved_custom_voice_language_and_reference_are_unchanged");
            var designed = new PetVoiceProfile("voice-design", "Vivian", "Chinese", "My custom voice description", FollowAppLanguage: true);
            Check(PetVoiceProfiles.ResolveForPlayback(designed) is { Language: "English", Instruct: "My custom voice description" },
                "follow_language_does_not_rewrite_user_voice_prompt");
            Check(!HasCjk(PetVoiceProfiles.DefaultInstruction("gpt")) && PetVoiceProfiles.DefaultInstruction("gpt").Contains("English") &&
                !HasCjk(PetVoiceProfiles.CharacterName("gpt")) && PetPerformanceCatalog.Names.All(name => !HasCjk(PetPerformanceCatalog.Label(name))),
                "english_voice_defaults_character_and_performance_labels");
            L.Initialize(L.Chinese);
            Check(PetVoiceProfiles.ResolveForPlayback(PetVoiceProfiles.DefaultForSkin("deepseek")).Language == "Chinese" &&
                PetVoiceProfiles.DefaultInstruction("gpt").Contains("普通话") && PetVoiceProfiles.CharacterName("gpt") == "GPT 小龙娘",
                "switching_back_restores_chinese_runtime_defaults");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, checks, error = ex.Message }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { L.Initialize(language); }
    }

    private static bool HasCjk(string value) => value.Any(c => c is >= '\u3400' and <= '\u9fff');
}
