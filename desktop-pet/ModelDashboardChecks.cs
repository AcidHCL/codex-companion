using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// All endpoint, model, GPU, and voice operations are fixtures; no real inference.
internal static class ModelDashboardChecks
{
    internal static int Run()
    {
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        AppSettingsWindow? dashboard = null; PetWindow? pet = null;
        bool finished = false; int result = 1;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(55) };
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return; finished = true; timeout.Stop();
            dashboard?.Close(); pet?.Close();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "model-dashboard-validation.json"), JsonSerializer.Serialize(new
                { passed, assertions = checks.Count, checks, error, realModelOperations = 0, userSettingsWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1; app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "MODEL_DASHBOARD_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.ToString()); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                LocalGpuTelemetryChecks.Run(Require);
                CheckVoiceProfileSettings(Require);
                ModelVramBreakdownChecks.Run(Require);
                await OllamaModelMemoryChecks.RunAsync(Require);
                await CheckSelectionSyncAsync(Require);
                var settings = new DesktopSettings { CodexEnabled = false, Topmost = false, MotionEnabled = false, MovementEnabled = false,
                    SharedMemoryEnabled = false, WebSearchEnabled = false, OllamaAddress = "http://192.0.2.10:11434" };
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false, subscribeToSystemEvents: false);
                var synth = new VoiceFixture();
                var field = typeof(PetWindow).GetField("_voice", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ((PetSpeechService?)field.GetValue(pet))?.Dispose();
                field.SetValue(pet, new PetSpeechService(settings, app.Dispatcher, synth, new NoPlayer()));
                int reads = 0, loads = 0, unloads = 0; bool loaded = false, fail = false;
                string? blocked = null;
                TaskCompletionSource? loadRelease = null;
                TaskCompletionSource<IReadOnlyList<LocalGpuInfo>>? delayedGpu = null;
                IReadOnlyList<LocalGpuInfo> gpuRows = new[] { new LocalGpuInfo("Fixture GPU", 32L << 30, 4L << 30, 12) };
                var services = new DashboardModelServices(
                    (_, ct) =>
                    {
                        reads++;
                        if (fail) throw new InvalidOperationException("fixture unavailable");
                        IReadOnlyList<OllamaRunningModel> rows = loaded ? new[]
                        {
                            new OllamaRunningModel(settings.OllamaModel, 12L << 30, 10L << 30, 8192, DateTimeOffset.Now.AddMinutes(5)),
                            new OllamaRunningModel("another:latest", null, null, null, null)
                        } : Array.Empty<OllamaRunningModel>();
                        return Task.FromResult(rows);
                    },
                    pet.ReadSpeechModelHealthAsync,
                    _ => { var delayed = delayedGpu; delayedGpu = null; return delayed?.Task ?? Task.FromResult(gpuRows); },
                    async load =>
                    {
                        if (load) { loads++; if (loadRelease != null) await loadRelease.Task; }
                        else unloads++;
                        loaded = load;
                        return load ? "fixture loaded" : "fixture unloaded";
                    }, LlmBlockedReason: () => blocked);
                var studio = new StudioFixture();
                dashboard = new AppSettingsWindow(pet, modelServices: services, voiceStudio: studio) { ShowActivated = false, ShowInTaskbar = false };
                dashboard.Show(); await Settle();
                await CheckSettingsSeparationAsync(pet, settings, dashboard, Require);
                Require(reads == 0 && loads == 0 && synth.Loads == 0, "general_settings_page_does_not_poll_or_load_models");
                Click(dashboard, "SettingsNavModels"); await Settle(); await Until(() => Text(dashboard, "ModelUpdated").Contains("最近读取"));
                Require(Text(dashboard, "DashboardLlmState") == "未加载" && Text(dashboard, "DashboardVoiceState").Contains("未加载"), "model_page_reads_real_state_without_inference");
                Require(Text(dashboard, "DashboardLlmEndpoint").StartsWith("服务器") && Text(dashboard, "GpuInfo0").Contains("Fixture GPU"), "remote_ollama_is_separate_from_local_gpu");
                Require(ModelSettingsView.MemorySize(null) == "未提供" && ModelSettingsView.MemorySize(0).Contains("0"), "missing_memory_is_not_fabricated_zero");
                await CheckVoiceProfileEditorAsync(dashboard, settings, () => synth.Loads, Require);
                await CheckVoiceStudioAsync(dashboard, settings, studio, Require);
                await CheckVoiceStudioCloseAsync(pet, settings, services, studio, Require);
                loadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Click(dashboard, "DashboardLlmLoad");
                Require(loads == 1 && !Named<Button>(dashboard, "DashboardLlmUnload").IsEnabled && Text(dashboard, "DashboardLlmState").Contains("加载"), "load_disables_conflicting_controls_and_shows_progress");
                Click(dashboard, "DashboardLlmUnload");
                Require(unloads == 0, "double_action_is_ignored_even_for_routed_events");
                loadRelease.SetResult();
                await Until(() => Text(dashboard, "DashboardLlmState") == "已加载");
                Require(Text(dashboard, "DashboardLlmMemory").Contains("10.00 GiB") && Text(dashboard, "DashboardLlmOthers").Contains("未提供"), "loaded_model_reports_vram_and_unknown_other_model_metrics");
                var staleGpu = delayedGpu = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Click(dashboard, "ModelRefresh");
                Click(dashboard, "DashboardLlmUnload"); await Until(() => Text(dashboard, "DashboardLlmState") == "未加载");
                staleGpu.SetResult(gpuRows); await Settle();
                Require(Text(dashboard, "DashboardLlmState") == "未加载", "pre_unload_poll_cannot_overwrite_fresh_model_state");
                Require(unloads == 1, "unload_uses_same_selected_model_control");
                Click(dashboard, "DashboardVoiceLoad"); await Until(() => synth.Loads == 1 && Text(dashboard, "DashboardVoiceState") == "已加载");
                Require(Text(dashboard, "DashboardVoiceMemory").Contains("2.00 GiB"), "voice_controls_reach_shared_service_and_display_allocator_usage");
                settings.OllamaAddress = OllamaEndpoint.DefaultAddress; loaded = true;
                gpuRows = new[] { new LocalGpuInfo("Fixture GPU", 32L << 30, 14L << 30, 12) };
                Click(dashboard, "ModelRefresh"); await Settle();
                Capture(dashboard, "model-dashboard-breakdown.png");
                Click(dashboard, "DashboardVoiceUnload"); await Until(() => synth.Unloads == 1 && Text(dashboard, "DashboardVoiceState").Contains("未加载"));
                Require(synth.Loads == 1 && synth.Unloads == 1, "voice_unload_refreshes_state_without_starting_worker");
                blocked = "正在生成聊天回复"; Click(dashboard, "ModelRefresh"); await Settle();
                Require(!Named<Button>(dashboard, "DashboardLlmLoad").IsEnabled && Text(dashboard, "DashboardLlmAction") == blocked, "active_chat_disables_dashboard_model_actions");
                blocked = null; Click(dashboard, "ModelRefresh"); await Settle();
                Require(Named<Button>(dashboard, "DashboardLlmLoad").IsEnabled && Text(dashboard, "DashboardLlmAction") == "", "ended_chat_clears_obsolete_busy_notice");
                fail = true; Click(dashboard, "ModelRefresh"); await Settle();
                Require(Text(dashboard, "DashboardLlmState") == "状态不可用" && !Text(dashboard, "DashboardLlmMemory").Contains("10.00"), "connection_failure_clears_stale_memory_instead_of_reporting_unloaded");
                fail = false; Click(dashboard, "ModelRefresh"); await Settle();
                await CheckVoiceLibraryAsync(dashboard, pet, settings, studio, Require);
                dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight; await Settle();
                Require(Fits(dashboard, Named<Button>(dashboard, "DashboardLlmLoad")) && Fits(dashboard, Named<Button>(dashboard, "DashboardVoiceLoad")), "model_actions_fit_minimum_dashboard_size");
                Capture(dashboard, "model-dashboard-minimum.png");
                dashboard.Width = 1180; dashboard.Height = 850; await Settle(); Capture(dashboard, "model-dashboard-default.png");
                int beforePause = reads; Click(dashboard, "SettingsNavGeneral"); await Task.Delay(5200);
                Require(reads == beforePause && Named<Slider>(dashboard, "PetSize").IsVisible && !Named<Button>(dashboard, "SpeechPreload").IsVisible, "switching_to_general_settings_stops_model_polling");
                Click(dashboard, "SettingsNavModels"); await Settle();
                Require(reads > beforePause, "reopening_model_page_refreshes_immediately");
                int beforeHidden = reads; dashboard.Hide(); await Task.Delay(5200);
                Require(reads == beforeHidden, "hidden_settings_window_stops_model_polling");
                dashboard.Show(); await Settle();
                Require(reads > beforeHidden, "showing_settings_window_refreshes_visible_model_page");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        };
        app.Run(); return result;
    }

    private static async Task CheckSelectionSyncAsync(Action<bool, string> require)
    {
        var settings = new DesktopSettings { CodexEnabled = false, SharedMemoryEnabled = false, WebSearchEnabled = false,
            OllamaModel = OllamaModels.FastModel };
        var client = new SelectionFixtureClient();
        int saves = 0, changes = 0;
        using var monitor = new CodexMonitor(settings, new NoConnection(), persistSettings: false);
        var chat = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => saves++, client: client);
        var services = new DashboardModelServices((_, ct) => client.ListRunningModelsAsync(ct),
            _ => Task.FromResult<PetVoiceHealth?>(null), _ => Task.FromResult<IReadOnlyList<LocalGpuInfo>>(Array.Empty<LocalGpuInfo>()),
            chat.SetDashboardModelLoadedAsync, () => chat.ModelMemoryOperationText, () => chat.DashboardModelControlBlockedReason,
            (_, ct) => client.ListModelsAsync(ct), chat.SelectDashboardModelAsync);
        var pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false, subscribeToSystemEvents: false);
        var dash = new AppSettingsWindow(pet, modelServices: services, voiceStudio: new StudioFixture()) { ShowActivated = false, ShowInTaskbar = false };
        chat.ModelSelectionChanged += () => { changes++; dash.ModelSelectionChanged(); };
        try
        {
            dash.Show(); Click(dash, "SettingsNavModels"); await Settle();
            var select = Named<ComboBox>(dash, "DashboardLlmSelector");
            require(select.Items.Count == 2 && select.SelectedValue as string == OllamaModels.FastModel && saves == 0,
                "dashboard_lists_installed_models_without_changing_selection");
            // Disable periodic refresh to prove synchronization is driven by the
            // committed configuration event, rather than the five-second poll.
            ((DispatcherTimer)typeof(ModelSettingsView).GetField("_modelTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Walk(dash).OfType<ModelSettingsView>().Single())!).Stop();
            select.SelectedValue = OllamaModels.QualityModel;
            await Until(() => settings.OllamaModel == OllamaModels.QualityModel && select.IsEnabled);
            require(!chat.IsVisible && changes > 0 && saves == 1 && client.ChatCalls == 0 && client.Preloads == 0,
                "dashboard_switch_commits_to_hidden_chat_without_loading_or_inference");
            chat.Show(); await Settle();
            var chatSelect = Named<ComboBox>(chat, "OllamaModelSelector");
            await Until(() => chatSelect.IsEnabled);
            var input = Named<TextBox>(chat, "OllamaChatInput"); input.Text = "保留这份草稿";
            string? conversation = chat.CurrentConversationId;
            require(chatSelect.SelectedValue as string == OllamaModels.QualityModel, "opening_chat_uses_dashboard_selection");
            int changesBefore = changes;
            chatSelect.SelectedValue = OllamaModels.FastModel;
            await Until(() => select.SelectedValue as string == OllamaModels.FastModel && select.IsEnabled);
            require(changes > changesBefore && settings.OllamaModel == OllamaModels.FastModel && input.Text == "保留这份草稿" && chat.CurrentConversationId == conversation,
                "chat_switch_immediately_updates_dashboard_and_preserves_conversation_draft");
            client.FailCapabilities = true;
            int previousSaves = saves;
            select.SelectedValue = OllamaModels.QualityModel;
            await Until(() => select.IsEnabled && select.SelectedValue as string == OllamaModels.FastModel);
            require(settings.OllamaModel == OllamaModels.FastModel && chatSelect.SelectedValue as string == OllamaModels.FastModel && saves == previousSaves,
                "failed_dashboard_switch_rolls_back_both_selectors_without_persisting");
            require(client.ChatCalls == 0 && client.Preloads == 0, "switching_never_starts_generation_or_preload");
        }
        finally { dash.Close(); chat.Shutdown(); pet.Close(); }
    }

    private static async Task CheckSettingsSeparationAsync(PetWindow pet, DesktopSettings settings, AppSettingsWindow preferences,
        Action<bool, string> require)
    {
        int requested = 0;
        var wardrobe = new SettingsWindow(pet, SkinCatalog.Load(AppContext.BaseDirectory), openSettings: () => requested++)
            { ShowActivated = false, ShowInTaskbar = false };
        using var monitor = new CodexMonitor(settings, new NoConnection(), persistSettings: false);
        var work = new CodexDashboard(monitor, settings, () => { }, pet, () => requested++)
            { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            wardrobe.Show(); await Settle();
            require(wardrobe.Title.Contains("换装") && Walk(wardrobe).OfType<Button>().Count(x => x.Name.StartsWith("SkinCard_")) == 9,
                "wardrobe_keeps_nine_skin_choices_and_clear_window_identity");
            require(!Walk(wardrobe).OfType<CheckBox>().Any() && !Walk(wardrobe).OfType<Slider>().Any() &&
                !Walk(wardrobe).OfType<FrameworkElement>().Any(x => x.Name is "LlmBehaviorEnabled" or "PetEmotion"),
                "wardrobe_no_longer_duplicates_desktop_sound_or_behavior_settings");
            Click(wardrobe, "OpenSettings");
            work.Show(); await Settle(); Click(work, "DashboardSettings");
            require(requested == 2 && !Walk(work).OfType<ModelSettingsView>().Any() &&
                !Walk(work).OfType<FrameworkElement>().Any(x => x.Name is "SpeechEnabled" or "ModelPage" or "DashboardModels"),
                "wardrobe_and_work_dashboard_open_settings_without_embedding_model_or_speech_controls");
            Capture(wardrobe, "model-dashboard-wardrobe.png");
        }
        finally { wardrobe.Close(); work.Close(); }

        var size = Named<Slider>(preferences, "PetSize");
        double originalSize = settings.Size, originalVolume = settings.SoundVolume;
        bool originalSound = settings.SoundEnabled;
        size.Value = 300;
        require(settings.Size == 300, "general_settings_size_control_uses_existing_pet_preferences");
        pet.SetSize(260);
        require(size.Value == 260, "external_pet_preference_updates_standalone_settings_immediately");
        var sound = Named<CheckBox>(preferences, "SoundEnabled");
        var volume = Named<Slider>(preferences, "SoundVolume");
        sound.IsChecked = false;
        require(!settings.SoundEnabled && !volume.IsEnabled, "general_settings_sound_toggle_updates_volume_availability");
        sound.IsChecked = true; volume.Value = 55;
        require(Math.Abs(settings.SoundVolume - .55) < .001, "general_settings_music_volume_reuses_existing_sound_state");
        pet.SetSize(originalSize); pet.SetSoundVolume(originalVolume); pet.SetSoundEnabled(originalSound);
        require(new[] { "PetTopmost", "PetMotion", "PetClickThrough", "PetMovement", "LlmBehaviorEnabled" }
            .All(name => Named<CheckBox>(preferences, name).IsVisible) &&
            new[] { "JumpNearbyWindow", "ClimbLeft", "ClimbRight", "StopPetPerformance", "PlayPetEmotion", "PetVisibility", "ResetPetPosition" }
            .All(name => Walk(preferences).OfType<Button>().Any(button => button.Name == name)),
            "all_former_wardrobe_controls_are_available_in_general_settings");
        Capture(preferences, "model-dashboard-general-settings.png");
        double width = preferences.Width, height = preferences.Height;
        preferences.Width = preferences.MinWidth; preferences.Height = preferences.MinHeight;
        Named<Button>(preferences, "ResetPetPosition").BringIntoView(); await Settle();
        require(Fits(preferences, Named<Button>(preferences, "ResetPetPosition")), "general_settings_last_action_is_reachable_at_minimum_size");
        Capture(preferences, "model-dashboard-general-settings-minimum.png");
        preferences.Width = width; preferences.Height = height;
        Named<ScrollViewer>(preferences, "GeneralSettingsScroll").ScrollToTop(); await Settle();
    }

    private static void CheckVoiceProfileSettings(Action<bool, string> require)
    {
        var settings = new DesktopSettings();
        require(settings.GetVoiceProfile("deepseek").ModelId == "base" && settings.GetVoiceProfile("gpt").ModelId == "gpt-fixed" &&
            PetVoiceProfiles.Characters.Where(x => x.Id is not ("deepseek" or "gpt")).All(x => settings.GetVoiceProfile(x.Id).ModelId == "disabled"),
            "default_voice_preferences_keep_deepseek_and_gpt_bundled_references_and_other_roles_silent");
        foreach (var role in PetVoiceProfiles.Characters)
            settings.SetVoiceProfile(role.Id, new("custom-voice", "Serena", "Chinese", "自然、清晰。"));
        var restored = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
        require(PetVoiceProfiles.Characters.All(x => restored.GetVoiceProfile(x.Id) == settings.GetVoiceProfile(x.Id)),
            "all_nine_character_voice_profiles_roundtrip_through_local_settings");
        restored.SetVoiceProfile("gpt", new("voice-design", "not-a-speaker", "unsupported", "\u0000" + new string('a', 700)));
        var normalized = restored.GetVoiceProfile("gpt");
        require(normalized.Speaker == "Vivian" && normalized.Language == "Chinese" && normalized.Instruct.Length == 600 && !normalized.Instruct.Contains('\0'),
            "voice_profile_normalization_bounds_instructions_and_whitelists_fields");
        restored.CharacterVoices["../private"] = new("base", "Vivian", "Chinese", ""); restored.Normalize();
        require(restored.CharacterVoices.Count == 9 && !restored.CharacterVoices.ContainsKey("../private") &&
            PetVoiceProfiles.Normalize(new("arbitrary-model-path", "Vivian", "Chinese", ""), "gpt").ModelId == "gpt-fixed",
            "unknown_character_and_model_paths_cannot_become_voice_runtime_configuration");
        require(PetVoiceProfiles.Normalize(new("voice-design", "Vivian", "Chinese", ""), "gpt").Instruct.Contains("小骄傲"),
            "empty_saved_gpt_design_recovers_character_appropriate_default");
    }

    private static async Task CheckVoiceProfileEditorAsync(AppSettingsWindow dashboard, DesktopSettings settings, Func<int> voiceLoads,
        Action<bool, string> require)
    {
        string skin = settings.SkinId, llm = settings.OllamaModel;
        int loadsBefore = voiceLoads();
        var role = Named<ComboBox>(dashboard, "DashboardVoiceCharacter");
        var model = Named<ComboBox>(dashboard, "DashboardVoiceModel");
        var library = Named<ListBox>(dashboard, "VoiceLibraryList");
        var speaker = Named<ComboBox>(dashboard, "DashboardVoiceSpeaker");
        var instruction = Named<TextBox>(dashboard, "DashboardVoiceInstruction");
        var methods = model.Items.Cast<PetVoiceChoice>().ToArray();
        require(role.Items.Count == 9 && speaker.Items.Count == 9 && methods.Length == 4 &&
            methods.Select(x => x.Id).ToHashSet().SetEquals(new[] { "disabled", "fixed", "custom-voice", "voice-design" }) &&
            methods.All(x => !x.Label.Contains("Let Me Go") && !x.Label.Contains("龙娘")),
            "voice_editor_exposes_four_generation_methods_without_mixing_character_voice_names");
        var originalGpt = settings.GetVoiceProfile("gpt");
        role.SelectedValue = "gpt";
        require(model.SelectedValue as string == "fixed" && library.SelectedValue as string == "builtin:gpt-fixed" &&
            settings.GetVoiceProfile("gpt") == originalGpt && settings.SkinId == skin && settings.OllamaModel == llm,
            "existing_gpt_fixed_profile_maps_to_fixed_method_without_migration_or_other_settings_changes");
        model.SelectedValue = "voice-design";
        require(instruction.IsVisible && instruction.Text.Contains("小骄傲") && !speaker.IsVisible,
            "voice_design_offers_gpt_description_without_reference_or_preset_fields");
        instruction.Text = "清亮、自信的中文女声，温柔地说。";
        Click(dashboard, "DashboardVoiceProfileSave");
        require(settings.GetVoiceProfile("gpt").ModelId == "voice-design" && settings.GetVoiceProfile("gpt").Instruct == instruction.Text &&
            settings.GetVoiceProfile("deepseek").ModelId == "base" && voiceLoads() == loadsBefore,
            "saving_inactive_character_voice_is_independent_and_never_loads_models");
        instruction.BringIntoView(); await Settle();
        require(Fits(dashboard, instruction), "voice_design_description_is_readable_inside_scrollable_dashboard");
        Capture(dashboard, "model-dashboard-voice-design.png");
        role.SelectedValue = "claude"; model.SelectedValue = "custom-voice"; speaker.SelectedValue = "Serena";
        instruction.Text = "平静、亲切。"; Click(dashboard, "DashboardVoiceProfileSave");
        speaker.BringIntoView(); await Settle();
        require(Fits(dashboard, speaker), "custom_voice_speaker_choice_is_visible_inside_dashboard");
        Capture(dashboard, "model-dashboard-custom-voice.png");
        role.SelectedValue = "gpt";
        require(model.SelectedValue as string == "voice-design" && instruction.Text == "清亮、自信的中文女声，温柔地说。" &&
            settings.GetVoiceProfile("claude").Speaker == "Serena", "switching_voice_edit_target_restores_separate_saved_profiles");
        instruction.Text = "尚未保存的音色草稿";
        Click(dashboard, "ModelRefresh"); await Settle();
        require(instruction.Text == "尚未保存的音色草稿" && settings.GetVoiceProfile("gpt").Instruct != instruction.Text,
            "model_status_poll_does_not_overwrite_unsaved_voice_draft");
        instruction.Text = ""; Click(dashboard, "DashboardVoiceProfileSave");
        require(Text(dashboard, "DashboardVoiceProfileStatus").Contains("请先填写") && settings.GetVoiceProfile("gpt").Instruct.Length > 0,
            "empty_design_description_cannot_replace_saved_voice");
        model.SelectedValue = "fixed"; library.SelectedValue = "builtin:gpt-fixed";
        require(!instruction.IsVisible && !speaker.IsVisible && library.IsVisible,
            "fixed_voice_method_uses_named_library_instead_of_design_or_preset_fields");
        Click(dashboard, "DashboardVoiceProfileSave");
        require(settings.GetVoiceProfile("gpt").ModelId == "gpt-fixed" && settings.GetVoiceProfile("gpt").Instruct == "" &&
            settings.GetVoiceProfile("deepseek").ModelId == "base" && settings.SkinId == skin && settings.OllamaModel == llm && voiceLoads() == loadsBefore,
            "saving_fixed_dragon_keeps_deepseek_and_chat_settings_without_loading_models");
        Named<Button>(dashboard, "DashboardVoiceProfileSave").BringIntoView(); await Settle();
        Capture(dashboard, "model-dashboard-gpt-fixed.png");
        role.SelectedValue = "deepseek";
        require(model.SelectedValue as string == "fixed" && library.SelectedValue as string == "builtin:base" &&
            settings.GetVoiceProfile("deepseek").ModelId == "base" && settings.SkinId == skin && settings.OllamaModel == llm && voiceLoads() == loadsBefore,
            "existing_deepseek_base_maps_to_fixed_method_without_migration_or_model_loading");
        var save = Named<Button>(dashboard, "DashboardVoiceProfileSave"); save.BringIntoView(); await Settle();
        require(Fits(dashboard, save), "voice_profile_save_is_reachable_in_scrollable_model_page");
        Capture(dashboard, "model-dashboard-voice-profiles.png");
        Named<ScrollViewer>(dashboard, "ModelPage").ScrollToTop(); await Settle();
    }

    private static async Task CheckVoiceStudioAsync(AppSettingsWindow dashboard, DesktopSettings settings, StudioFixture studio, Action<bool, string> require)
    {
        var role = Named<ComboBox>(dashboard, "DashboardVoiceCharacter");
        var model = Named<ComboBox>(dashboard, "DashboardVoiceModel");
        var library = Named<ListBox>(dashboard, "VoiceLibraryList");
        var instruction = Named<TextBox>(dashboard, "VoiceStudioInstruction");
        var text = Named<TextBox>(dashboard, "VoiceStudioText");
        var generate = Named<Button>(dashboard, "VoiceStudioGenerate");
        var save = Named<Button>(dashboard, "VoiceStudioSave");
        var voiceName = Named<TextBox>(dashboard, "VoiceStudioName");
        var deepseek = settings.GetVoiceProfile("deepseek"); string current = settings.SkinId, llm = settings.OllamaModel;
        role.SelectedValue = "gpt";
        voiceName.Text = "白龙 · 清亮御姐";
        var original = settings.GetVoiceProfile("gpt");
        instruction.Text = "青年女性，清亮、优雅，带一点克制的傲娇。";
        text.Text = "任务已经完成啦，早点休息。";
        require(instruction.MaxLength == 600 && text.MaxLength == 100 && !text.AcceptsReturn && studio.Generations == 0 && studio.Plays == 0,
            "voice_studio_has_bounded_inputs_and_does_not_generate_or_play_on_open");
        studio.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(dashboard, "VoiceStudioGenerate");
        require(studio.Generations == 1 && !role.IsEnabled && !instruction.IsEnabled && !generate.IsEnabled &&
            Named<Button>(dashboard, "VoiceStudioCancel").IsEnabled && !Named<Button>(dashboard, "DashboardVoiceLoad").IsEnabled,
            "voice_generation_disables_conflicting_controls_and_offers_cancel");
        Click(dashboard, "VoiceStudioGenerate");
        require(studio.Generations == 1 && settings.GetVoiceProfile("gpt") == original && studio.Plays == 0,
            "duplicate_generation_is_ignored_and_pending_preview_does_not_change_voice");
        studio.Pending.SetResult(); await Until(() => save.IsEnabled);
        require(studio.Plays == 0 && settings.GetVoiceProfile("gpt") == original && settings.GetVoiceProfile("deepseek") == deepseek,
            "successful_generation_stays_a_candidate_until_explicit_save");
        settings.SpeechEnabled = false;
        Click(dashboard, "VoiceStudioPlay"); await Settle();
        require(studio.Plays == 1 && studio.Generations == 1 && studio.LastVolume == settings.SpeechVolume,
            "explicit_studio_preview_replays_existing_audio_even_when_normal_speech_is_off");
        settings.SpeechEnabled = true;
        instruction.Text += "语速从容。";
        require(!save.IsEnabled && Named<Button>(dashboard, "VoiceStudioPlay").IsEnabled,
            "editing_description_keeps_old_preview_available_but_invalidates_fix_action");
        Click(dashboard, "VoiceStudioSave");
        require(studio.Saves == 0 && settings.GetVoiceProfile("gpt") == original,
            "routed_save_event_cannot_commit_stale_sample");
        string edited = instruction.Text;
        role.SelectedValue = "claude"; instruction.Text = "平和、清楚的另一种声音。";
        require(!save.IsEnabled && !Named<Button>(dashboard, "VoiceStudioPlay").IsEnabled,
            "different_character_does_not_inherit_another_character_candidate");
        role.SelectedValue = "gpt";
        require(instruction.Text == edited && Named<Button>(dashboard, "VoiceStudioPlay").IsEnabled && !save.IsEnabled,
            "returning_to_character_restores_its_own_unsaved_prompt_and_candidate");
        studio.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(dashboard, "VoiceStudioGenerate"); Click(dashboard, "VoiceStudioCancel");
        await Until(() => generate.IsEnabled);
        require(studio.Cancelled == 1 && settings.GetVoiceProfile("gpt") == original && !save.IsEnabled,
            "cancelled_generation_never_replaces_active_voice_or_promotes_old_candidate");
        studio.Pending = null; studio.FailGeneration = true;
        Click(dashboard, "VoiceStudioGenerate"); await Until(() => generate.IsEnabled);
        require(Text(dashboard, "VoiceStudioStatus").Contains("生成未完成") && settings.GetVoiceProfile("gpt") == original,
            "failed_generation_preserves_saved_voice_and_explains_failure");
        studio.FailGeneration = false;
        Click(dashboard, "VoiceStudioGenerate"); await Until(() => save.IsEnabled);
        int generated = studio.Generations;
        voiceName.Text = "";
        require(!save.IsEnabled, "unnamed_fixed_voice_cannot_be_saved");
        Click(dashboard, "VoiceStudioSave");
        require(studio.Saves == 0 && settings.GetVoiceProfile("gpt") == original,
            "routed_save_cannot_bypass_required_voice_name");
        voiceName.Text = "白龙 · 清亮御姐";
        require(save.IsEnabled && studio.Generations == generated,
            "naming_a_generated_voice_keeps_candidate_valid_without_regeneration");
        studio.FailSave = true; Click(dashboard, "VoiceStudioSave");
        require(settings.GetVoiceProfile("gpt") == original && Text(dashboard, "VoiceStudioStatus").Contains("原声音未替换"),
            "reference_storage_failure_does_not_replace_saved_character_voice");
        studio.FailSave = false; Click(dashboard, "VoiceStudioSave");
        var fixedProfile = settings.GetVoiceProfile("gpt");
        require(fixedProfile.ModelId == "saved-voice" && fixedProfile.ReferenceId?.Length == 32 && fixedProfile.Instruct == "" &&
            settings.GetVoiceProfile("deepseek") == deepseek && settings.SkinId == current && settings.OllamaModel == llm,
            "fix_and_save_commits_only_generated_character_reference_without_changing_deepseek_or_ollama");
        require(studio.ListSavedVoices("gpt").Single().Name == "白龙 · 清亮御姐" && voiceName.MaxLength == 60,
            "voice_studio_saves_user_selected_name_with_the_fixed_reference");
        require(model.SelectedValue as string == "fixed" && library.SelectedValue as string == fixedProfile.ReferenceId,
            "new_saved_voice_uses_fixed_method_and_preserves_its_exact_reference_selection");
        model.SelectedValue = "voice-design"; model.SelectedValue = "fixed";
        library.SelectedValue = fixedProfile.ReferenceId; Click(dashboard, "DashboardVoiceProfileSave");
        require(settings.GetVoiceProfile("gpt").ReferenceId == fixedProfile.ReferenceId,
            "ordinary_voice_settings_save_preserves_custom_reference_id");
        model.SelectedValue = "custom-voice"; Click(dashboard, "DashboardVoiceProfileSave");
        require(settings.GetVoiceProfile("gpt").ModelId == "custom-voice" && settings.GetVoiceProfile("gpt").ReferenceId == null,
            "switching_away_from_fixed_voice_does_not_attach_reference_to_preset_mode");
        model.SelectedValue = "fixed"; library.SelectedValue = fixedProfile.ReferenceId;
        Click(dashboard, "DashboardVoiceProfileSave");
        require(settings.GetVoiceProfile("gpt").ReferenceId == fixedProfile.ReferenceId,
            "switching_back_to_saved_voice_restores_character_reference_from_library");
        save.BringIntoView(); await Settle();
        require(Fits(dashboard, save) && Fits(dashboard, instruction) && Fits(dashboard, voiceName),
            "voice_studio_name_fields_and_save_fit_scrolled_dashboard");
        Capture(dashboard, "model-dashboard-voice-studio.png");
        double width = dashboard.Width, height = dashboard.Height;
        dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight; save.BringIntoView(); await Settle();
        require(Fits(dashboard, save), "voice_studio_save_is_reachable_at_minimum_window_size");
        Capture(dashboard, "model-dashboard-voice-studio-minimum.png");
        dashboard.Width = width; dashboard.Height = height; await Settle();
        var savedGpt = settings.GetVoiceProfile("gpt");
        role.SelectedValue = "gemini";
        require(library.SelectedItem == null && model.SelectedValue as string == "disabled",
            "silent_character_has_no_fake_disabled_voice_in_library_or_inherited_selection");
        model.SelectedValue = "fixed"; library.SelectedValue = fixedProfile.ReferenceId;
        Click(dashboard, "DashboardVoiceProfileSave");
        require(library.SelectedItem == null && settings.GetVoiceProfile("gemini").ModelId == "disabled" &&
            settings.GetVoiceProfile("gpt") == savedGpt && Text(dashboard, "DashboardVoiceProfileStatus").Contains("选择"),
            "another_character_cannot_select_or_overwrite_a_foreign_saved_voice");
        role.SelectedValue = "deepseek";
        Named<ScrollViewer>(dashboard, "ModelPage").ScrollToTop(); await Settle();
    }

    private static async Task CheckVoiceLibraryAsync(AppSettingsWindow dashboard, PetWindow pet, DesktopSettings settings,
        StudioFixture studio, Action<bool, string> require)
    {
        var view = Walk(dashboard).OfType<ModelSettingsView>().Single();
        var role = Named<ComboBox>(dashboard, "DashboardVoiceCharacter");
        var list = Named<ListBox>(dashboard, "VoiceLibraryList");
        var name = Named<TextBox>(dashboard, "VoiceLibraryName");
        var rename = Named<Button>(dashboard, "VoiceLibraryRename");
        var delete = Named<Button>(dashboard, "VoiceLibraryDelete");
        role.SelectedValue = "gpt";
        var first = studio.ListSavedVoices("gpt").Single();
        require(list.Items.Cast<object>().All(item => item.GetType().GetProperty("Key")?.GetValue(item) as string != "builtin:disabled"),
            "fixed_voice_library_contains_sound_choices_without_a_disabled_pseudo_voice");
        list.SelectedValue = "builtin:gpt-fixed";
        require(!rename.IsEnabled && !delete.IsEnabled, "bundled_gpt_voice_cannot_be_renamed_or_deleted");
        int deletes = studio.Deletes;
        view.DeleteSelectedSavedVoice(true);
        require(studio.Deletes == deletes && studio.Saved.ContainsKey(first.ReferenceId),
            "routed_delete_cannot_bypass_protection_of_builtin_gpt_voice");
        Click(dashboard, "VoiceLibraryApply");
        require(settings.GetVoiceProfile("gpt").ModelId == "gpt-fixed", "bundled_gpt_voice_can_be_restored_from_library");

        list.SelectedValue = first.ReferenceId; Click(dashboard, "VoiceLibraryApply");
        string studioDraftName = Named<TextBox>(dashboard, "VoiceStudioName").Text;
        name.Text = "白龙 · 温润御姐"; Click(dashboard, "VoiceLibraryRename");
        require(studio.Saved[first.ReferenceId].Name == "白龙 · 温润御姐" &&
            settings.GetVoiceProfile("gpt").ReferenceId == first.ReferenceId && studio.Renames == 1,
            "voice_library_rename_updates_display_name_without_replacing_reference_or_active_selection");
        require(Named<TextBox>(dashboard, "VoiceStudioName").Text == studioDraftName,
            "renaming_saved_voice_keeps_independent_studio_candidate_name_even_when_names_match");
        studio.FailRename = true; name.Text = "失败不得覆盖的名字"; Click(dashboard, "VoiceLibraryRename");
        require(studio.Saved[first.ReferenceId].Name == "白龙 · 温润御姐" &&
            settings.GetVoiceProfile("gpt").ReferenceId == first.ReferenceId,
            "voice_library_rename_failure_preserves_original_name_and_selection");
        studio.FailRename = false;

        Named<TextBox>(dashboard, "VoiceStudioName").Text = "白龙 · 第二版";
        Click(dashboard, "VoiceStudioSave");
        var second = studio.ListSavedVoices("gpt").Single(info => info.ReferenceId != first.ReferenceId);
        require(second.Name == "白龙 · 第二版" && studio.Saved.ContainsKey(first.ReferenceId) &&
            settings.GetVoiceProfile("gpt").ReferenceId == second.ReferenceId,
            "saving_another_named_voice_keeps_previous_character_versions_selectable");
        list.SelectedValue = first.ReferenceId; Click(dashboard, "VoiceLibraryApply");
        require(settings.GetVoiceProfile("gpt").ReferenceId == first.ReferenceId,
            "voice_library_can_apply_an_older_custom_voice_without_regeneration");
        view.DeleteSelectedSavedVoice(false);
        require(studio.Deletes == deletes && settings.GetVoiceProfile("gpt").ReferenceId == first.ReferenceId,
            "unconfirmed_custom_voice_deletion_does_not_mutate_library_or_settings");
        studio.FailDelete = true;
        view.DeleteSelectedSavedVoice(true);
        require(studio.Saved.ContainsKey(first.ReferenceId) && settings.GetVoiceProfile("gpt").ReferenceId == first.ReferenceId,
            "failed_voice_delete_restores_previous_selected_reference");
        studio.FailDelete = false;

        var unrelated = settings.GetVoiceProfile("qwen");
        var shared = settings.GetVoiceProfile("gpt");
        pet.SetCharacterVoiceProfile("deepseek", shared);
        settings.SetVoiceProfile("claude", shared);
        list.SelectedValue = first.ReferenceId;
        view.DeleteSelectedSavedVoice(true);
        require(!studio.Saved.ContainsKey(first.ReferenceId) && studio.Saved.ContainsKey(second.ReferenceId) &&
            settings.GetVoiceProfile("gpt").ModelId == "gpt-fixed" && settings.GetVoiceProfile("deepseek").ModelId == "base" &&
            settings.GetVoiceProfile("claude").ModelId == "disabled" && settings.GetVoiceProfile("qwen") == unrelated,
            "deleting_shared_custom_voice_restores_each_referencing_role_default_and_preserves_other_voices");

        role.SelectedValue = "deepseek"; list.SelectedValue = "builtin:base";
        deletes = studio.Deletes; view.DeleteSelectedSavedVoice(true);
        require(!rename.IsEnabled && !delete.IsEnabled && studio.Deletes == deletes && settings.GetVoiceProfile("deepseek").ModelId == "base",
            "deepseek_let_me_go_builtin_voice_remains_protected_and_selected");

        studio.Saved[second.ReferenceId] = second with { Language = "English" };
        role.SelectedValue = "gpt"; list.SelectedValue = second.ReferenceId;
        Named<ComboBox>(dashboard, "DashboardVoiceModel").SelectedValue = "fixed";
        Click(dashboard, "DashboardVoiceProfileSave");
        require(settings.GetVoiceProfile("gpt").ReferenceId == second.ReferenceId && settings.GetVoiceProfile("gpt").Language == "English",
            "ordinary_voice_settings_save_uses_selected_reference_language_instead_of_previous_profile_language");
        studio.Saved[second.ReferenceId] = second;
        name.Text = "白龙 · 第二版（可改名）";
        list.ScrollIntoView(list.SelectedItem); await Settle();
        var selectedRow = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem);
        var rowTexts = selectedRow == null ? Array.Empty<TextBlock>() : Walk(selectedRow).OfType<TextBlock>().ToArray();
        require(rowTexts.Any(block => BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Path?.Path == "Name" && block.Text == second.Name) &&
            rowTexts.Any(block => BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Path?.Path == "Details" && block.Text.Contains(second.ReferenceId[..6])),
            "saved_voice_row_displays_exact_user_name_separately_from_date_and_reference_details");
        delete.BringIntoView(); await Settle();
        require(Fits(dashboard, list) && Fits(dashboard, name) && Fits(dashboard, rename) && Fits(dashboard, delete),
            "voice_library_selection_name_and_actions_fit_scrolled_settings");
        Capture(dashboard, "model-dashboard-voice-library.png");
        double width = dashboard.Width, height = dashboard.Height;
        dashboard.Width = dashboard.MinWidth; dashboard.Height = dashboard.MinHeight;
        delete.BringIntoView(); await Settle();
        require(Fits(dashboard, list) && Fits(dashboard, rename) && Fits(dashboard, delete),
            "voice_library_selection_and_actions_fit_minimum_settings_window");
        Capture(dashboard, "model-dashboard-voice-library-minimum.png");
        dashboard.Width = width; dashboard.Height = height; await Settle();
        var selectedProfile = settings.GetVoiceProfile("gpt");
        studio.Saved.Remove(second.ReferenceId);
        role.SelectedValue = "deepseek"; role.SelectedValue = "gpt";
        var method = Named<ComboBox>(dashboard, "DashboardVoiceModel");
        require(method.SelectedValue as string == "fixed" && list.SelectedItem == null &&
            settings.GetVoiceProfile("gpt") == selectedProfile,
            "missing_saved_reference_does_not_silently_select_another_named_voice_or_migrate_settings");
        method.SelectedValue = "voice-design"; method.SelectedValue = "fixed";
        Click(dashboard, "DashboardVoiceProfileSave");
        require(settings.GetVoiceProfile("gpt") == selectedProfile && Text(dashboard, "DashboardVoiceProfileStatus").Contains("选择"),
            "missing_saved_reference_requires_explicit_voice_choice_before_saving_fixed_method");
        studio.Saved[second.ReferenceId] = second;
        role.SelectedValue = "deepseek";
        Named<ScrollViewer>(dashboard, "ModelPage").ScrollToTop(); await Settle();
    }

    private static async Task CheckVoiceStudioCloseAsync(PetWindow pet, DesktopSettings settings, DashboardModelServices services, StudioFixture savedStudio, Action<bool, string> require)
    {
        using var monitor = new CodexMonitor(settings, new NoConnection(), persistSettings: false);
        var studio = new StudioFixture { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        foreach (var entry in savedStudio.Saved) studio.Saved.Add(entry.Key, entry.Value);
        var dashboard = new AppSettingsWindow(pet, modelServices: services, voiceStudio: studio) { ShowActivated = false, ShowInTaskbar = false };
        var before = settings.GetVoiceProfile("qwen");
        var originalProfile = settings.GetVoiceProfile("gpt");
        try
        {
            dashboard.Show(); Click(dashboard, "SettingsNavModels"); await Settle();
            Named<ComboBox>(dashboard, "DashboardVoiceCharacter").SelectedValue = "gpt";
            var saved = studio.ReadSavedVoice("gpt")!;
            require(Named<TextBox>(dashboard, "VoiceStudioInstruction").Text == saved.Instruction &&
                Named<TextBox>(dashboard, "VoiceStudioText").Text == saved.Text &&
                Named<ComboBox>(dashboard, "VoiceStudioLanguage").SelectedItem as string == saved.Language && studio.Generations == 0 &&
                Named<ComboBox>(dashboard, "DashboardVoiceModel").SelectedValue as string == "fixed" &&
                Named<ListBox>(dashboard, "VoiceLibraryList").SelectedValue as string == originalProfile.ReferenceId &&
                settings.GetVoiceProfile("gpt") == originalProfile,
                "reopened_saved_voice_maps_to_fixed_method_and_restores_inputs_without_migration_or_regeneration");
            Named<ComboBox>(dashboard, "DashboardVoiceCharacter").SelectedValue = "qwen";
            Click(dashboard, "VoiceStudioGenerate");
            dashboard.Close();
            require(studio.Disposed && studio.LastToken.IsCancellationRequested, "closing_dashboard_cancels_generation_and_disposes_studio");
            studio.Pending!.SetResult(); await Settle();
            require(studio.Saves == 0 && studio.Plays == 0 && settings.GetVoiceProfile("qwen") == before,
                "late_generation_after_close_cannot_save_or_play_any_result");
        }
        finally { if (dashboard.IsLoaded) dashboard.Close(); }
    }

    private sealed class StudioFixture : IPetVoiceStudio
    {
        internal int Generations, Plays, Saves, Cancelled, Renames, Deletes;
        internal bool FailGeneration, FailSave, FailRename, FailDelete, IgnoreCancellation, Disposed;
        internal double LastVolume;
        internal CancellationToken LastToken;
        internal TaskCompletionSource? Pending;
        internal readonly Dictionary<string, PetSavedVoiceInfo> Saved = new(StringComparer.OrdinalIgnoreCase);
        public async Task<PetVoiceDesignSample> GenerateAsync(string skinId, string instruction, string text, string language, CancellationToken ct)
        {
            Generations++; LastToken = ct;
            try
            {
                if (Pending != null)
                { if (IgnoreCancellation) await Pending.Task; else await Pending.Task.WaitAsync(ct); }
                if (FailGeneration) throw new InvalidOperationException("fixture generation unavailable");
                return new(skinId, instruction, text, language, new byte[] { 1, 2, 3 });
            }
            catch (OperationCanceledException) { Cancelled++; throw; }
        }
        public Task PlayAsync(PetVoiceDesignSample sample, double volume, CancellationToken ct)
        { Plays++; LastVolume = volume; ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public void StopPlayback() { }
        public PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null)
        {
            if (FailSave) throw new IOException("fixture storage unavailable");
            Saves++;
            string id = Guid.NewGuid().ToString("N");
            Saved[id] = new(id, sample.SkinId, sample.Instruction, sample.Text, sample.Language,
                "fixture-design", "fixture-base", "", DateTimeOffset.UtcNow, 6, name ?? "自定义声音");
            return new("saved-voice", "Vivian", sample.Language, "", id);
        }
        public IReadOnlyList<PetSavedVoiceInfo> ListSavedVoices(string skinId) => Saved.Values
            .Where(info => info.SkinId.Equals(skinId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(info => info.CreatedUtc).ToArray();
        public PetSavedVoiceInfo? ReadSavedVoice(string skinId) => ListSavedVoices(skinId).FirstOrDefault();
        public PetSavedVoiceInfo RenameSavedVoice(string referenceId, string name)
        {
            if (FailRename) throw new IOException("fixture rename unavailable");
            if (!Saved.TryGetValue(referenceId, out var info)) throw new InvalidOperationException("Unknown fixture reference");
            Renames++; return Saved[referenceId] = info with { Name = name };
        }
        public void DeleteSavedVoice(string referenceId)
        {
            if (FailDelete) throw new IOException("fixture delete unavailable");
            if (!Saved.Remove(referenceId)) throw new InvalidOperationException("Unknown fixture reference");
            Deletes++;
        }
        public void Dispose() { Disposed = true; }
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Named<T>(Window root, string name) where T : FrameworkElement => Walk(root).OfType<T>().First(x => x.Name == name);
    private static string Text(Window root, string name) => Named<TextBlock>(root, name).Text;
    private static void Click(Window root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Settle() { await Task.Delay(80); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static async Task Until(Func<bool> predicate)
    {
        for (int i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(20); }
        throw new TimeoutException("model dashboard fixture condition");
    }
    private static bool Fits(Window window, FrameworkElement element)
    {
        var origin = element.TransformToAncestor(window).Transform(new Point());
        return element.IsVisible && origin.Y >= 0 && origin.Y + element.ActualHeight <= window.ActualHeight && origin.X >= 0 && origin.X + element.ActualWidth <= window.ActualWidth;
    }
    private static void Capture(Window window, string name)
    {
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name)); png.Save(file);
    }
    private sealed class VoiceFixture : IPetSpeechSynthesizer, IPetVoiceLifecycle, IPetVoiceDiagnostics
    {
        internal int Loads, Unloads; private bool _loaded;
        public Task PreloadAsync(string id, CancellationToken ct) { Loads++; _loaded = true; return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken ct) { Unloads++; _loaded = false; return Task.CompletedTask; }
        public Task<PetVoiceHealth?> ReadHealthAsync(CancellationToken ct) => Task.FromResult<PetVoiceHealth?>(_loaded ? new("ready", "faster", true, false, false, "Qwen3-TTS · fixture", "cuda:0", 2L << 30, 3L << 30) : null);
        public Task SetRetentionAsync(bool active, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken ct) => throw new InvalidOperationException("No audio in fixture");
        public Task CancelAsync(string id) => Task.CompletedTask;
        public Task ReleaseAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class NoPlayer : IPetSpeechPlayer
    {
        public Task PlayAsync(byte[] wave, double volume, CancellationToken ct) => throw new InvalidOperationException("No audio in fixture");
        public void Stop() { } public void SetVolume(double volume) { } public void Dispose() { }
    }
    private sealed class NoConnection : ICodexConnection
    {
        public string ConnectionDescription => "fixture"; public string? CallerThreadId => null;
        public bool IsPairingRevoked => false; public bool HasSavedPairing => false; public bool ResumeSavedPairing() => false;
        public void SetMessageAccess(bool enabled) { }
        public Task EnsureConnectedAsync(CancellationToken token = default) => throw new InvalidOperationException("No real connection");
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default) => throw new InvalidOperationException("No RPC");
        public void Disconnect() { } public Task ForgetAsync() => Task.CompletedTask; public void Dispose() { }
    }

    private sealed class SelectionFixtureClient : IOllamaClient
    {
        internal int ChatCalls, Preloads; internal bool FailCapabilities;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel, OllamaModels.QualityModel });
        public Task<IReadOnlyList<OllamaRunningModel>> ListRunningModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<OllamaRunningModel>>(Array.Empty<OllamaRunningModel>());
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => FailCapabilities ?
            Task.FromException<OllamaModelCapabilities>(new InvalidOperationException("fixture capabilities failed")) :
            Task.FromResult(new OllamaModelCapabilities(true, true, false, false));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        { ChatCalls++; await Task.Yield(); yield return new("fixture", true); }
        public Task PreloadAsync(string model, CancellationToken ct) { Preloads++; return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
}
