using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// Runs an isolated chat surface: no Codex transport, settings load/save, global
// desktop-pet mutex, synthetic input, or interaction with the user's pet.
internal static class OllamaChatChecks
{
    internal static int Run(bool live = false)
    {
        string name = live ? "ollama-live" : "ollama-chat";
        string evidence = Path.Combine(AppContext.BaseDirectory, name + "-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        var captures = new List<string>();
        var modelMetadata = new List<object>();
        var busy = new List<bool>();
        var bubbles = new List<string>();
        var settings = new DesktopSettings { CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false, WebSearchEnabled = false };
        var fake = live ? null : new FixtureHandler();
        var recording = new RecordingClient(fake == null ? new OllamaClient() : new OllamaClient(fake));
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OllamaChatWindow? window = null;
        int savedCallbacks = 0, result = 1;
        bool finished = false;
        string phase = "startup";
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromMinutes(live ? 15 : 2) };

        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }

        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true;
            deadline.Stop();
            try { window?.Shutdown(); recording.Dispose(); }
            catch (Exception ex) { passed = false; error = "cleanup_" + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, name + "-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, live, phase, error, assertions = checks.Count, checks, captures,
                evidence, modelMetadata, realAccountRequests = 0, userSettingsWrites = 0, syntheticMouseOrKeyboardInput = false,
                realSessionChanged = false, localServiceOnly = true, selectedModelSaveCallbacks = savedCallbacks,
                generationBusyEvents = busy, completedReplyBubbles = bubbles.Count,
                calls = recording.Runs.Select(run => new
                {
                    run.Model, messages = run.Messages.Count, images = run.Messages.Sum(message => message.Images?.Count ?? 0),
                    run.Completed, run.Cancelled, run.ErrorType, run.Answer, run.ElapsedSeconds, run.TokensPerSecond
                }).ToArray(),
                unloadedModels = recording.UnloadedModels.ToArray(), fakeWireRequests = fake?.Snapshot().Count ?? 0,
                clientDisposed = recording.Disposed
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }

        deadline.Tick += (_, _) => Finish(false, "test_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            deadline.Start();
            bool passed = false;
            string? failure = null;
            try
            {
                window = new OllamaChatWindow(settings, () => settings.SkinId.ToLowerInvariant() switch { "gpt" => "GPT", "gemini" => "Gemini", _ => "DeepSeek" }, value => busy.Add(value), text => bubbles.Add(text), () => savedCallbacks++, recording)
                { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
                window.Show();
                await SettleAsync(window);
                await UntilAsync(() => Named<ComboBox>(window, "OllamaModelSelector").IsEnabled, "initial_connection_check", 45);
                var input = Named<TextBox>(window, "OllamaChatInput");
                var models = Named<ComboBox>(window, "OllamaModelSelector");
                var previews = Named<WrapPanel>(window, "OllamaImagePreviews");
                Require(window.Title.EndsWith(AppVersion.Label, StringComparison.Ordinal), "chat_title_has_running_binary_version");
                var versions = Descendants<TextBlock>(window).Where(block => block.Name == "AppVersionLabel").ToArray();
                Require(versions.Length == 1 && versions[0].IsVisible && versions[0].Text.Contains(AppVersion.Label, StringComparison.Ordinal), "exactly_one_visible_chat_version_label");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await SettleAsync(window);
                var root = (FrameworkElement)window.Content;
                var versionBounds = versions[0].TransformToAncestor(root).TransformBounds(new Rect(versions[0].RenderSize));
                Require(versionBounds.Left >= 0 && versionBounds.Top >= 0 && versionBounds.Right <= root.ActualWidth + 1 && versionBounds.Bottom <= root.ActualHeight + 1, "version_fits_minimum_window");
                Capture(window, Path.Combine(evidence, "minimum-window.png")); captures.Add("minimum-window.png");
                window.Width = 410; window.Height = 550;
                string imagePath = Path.Combine(evidence, "test-shapes.png");
                CreateImageFixture(imagePath);

                async Task SendComplete(string prompt, string label)
                {
                    input.Text = prompt;
                    int before = recording.Runs.Count;
                    await window.SendAsync();
                    Require(recording.Runs.Count == before + 1 && recording.Runs[^1].Completed && !string.IsNullOrWhiteSpace(recording.Runs[^1].Answer), label + "_complete_nonempty_reply");
                    Require(Status(window).StartsWith("回复完成", StringComparison.Ordinal), label + "_ui_reports_completion");
                    Require(busy.Count > 1 && !busy[^1], label + "_busy_released");
                }

                if (live)
                {
                    phase = "live_models";
                    Require(Status(window).StartsWith("Ollama 已连接", StringComparison.Ordinal), "loopback_ollama_and_first_model_available");
                    var available = await recording.ListModelsAsync(CancellationToken.None);
                    Require(OllamaModels.Supported.All(model => available.Contains(model.Id)), "both_requested_local_models_installed");
                    foreach (string installed in available)
                    {
                        using var metadataTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                        var capabilities = await recording.GetModelInfoAsync(installed, metadataTimeout.Token);
                        modelMetadata.Add(new { model = installed, capabilities.SupportsChat, capabilities.SupportsImages, capabilities.SupportsThinking });
                    }
                    Require(recording.Runs.Count == 0, "all_installed_model_metadata_read_without_loading_or_chatting_unknown_models");
                    foreach (var choice in OllamaModels.Supported)
                    {
                        phase = "live_" + choice.Id;
                        if (settings.OllamaModel != choice.Id)
                        {
                            models.SelectedValue = choice.Id;
                            await UntilAsync(() => models.IsEnabled && settings.OllamaModel == choice.Id, "model_switch", 45);
                        }
                        Click(Named<Button>(window, "OllamaNewConversation"));
                        await UntilAsync(() => Status(window).StartsWith("新对话", StringComparison.Ordinal), "new_chat");
                        await SendComplete("这是本机桌宠的合成验收对话。请简短介绍你是谁、最爱吃什么，再记住我们的测试代号是松果。", choice.Id + "_chinese");
                        Require(recording.Runs[^1].Answer.Contains('饭'), choice.Id + "_deepseek_persona_likes_rice");
                        await window.AddImageFilesAsync(new[] { imagePath });
                        Require(previews.Children.Count == 1, choice.Id + "_image_preview_visible");
                        await SendComplete("请用一句中文说明图片左边和右边分别是什么颜色、什么形状。", choice.Id + "_vision");
                        string visualAnswer = recording.Runs[^1].Answer;
                        Require(visualAnswer.Contains('红') && visualAnswer.Contains('蓝') && (visualAnswer.Contains('圆') || visualAnswer.Contains("circle", StringComparison.OrdinalIgnoreCase)), choice.Id + "_recognizes_fixture_colors_and_circle");
                        await SendComplete("不需要重新看图，请用一句中文告诉我：我们的测试代号是什么，以及刚才图片右边是什么形状？", choice.Id + "_followup");
                        Require(recording.Runs[^1].Messages.Count == 6 && recording.Runs[^1].Messages.Any(message => message.Images?.Count == 1), choice.Id + "_followup_retains_image_and_prior_turns");
                        Require(recording.Runs[^1].Answer.Contains("松果", StringComparison.Ordinal), choice.Id + "_remembers_prior_text");
                        await SettleAsync(window);
                        string capture = choice.Id == OllamaModels.FastModel ? "9b-live-conversation.png" : "27b-live-conversation.png";
                        Capture(window, Path.Combine(evidence, capture)); captures.Add(capture);
                    }
                    Require(recording.Runs.Count == 6 && recording.Runs.All(run => run.Completed && run.ErrorType == null), "all_six_real_ui_requests_complete");
                }
                else
                {
                    phase = "fake_multiturn";
                    fake!.Enqueue(FixturePlan.Complete("你好，松果。"));
                    await SendComplete("测试代号是松果，请记住。", "first_turn");
                    var personaSystem = LastChat(fake).Payload.GetProperty("messages")[0].GetProperty("content").GetString()!;
                    Require(personaSystem.Contains("蓝色大肥鱼", StringComparison.Ordinal) && personaSystem.Contains("白米饭", StringComparison.Ordinal) && personaSystem.Contains("非官方", StringComparison.Ordinal), "deepseek_skin_sends_requested_fan_persona_in_system_role");
                    Require(personaSystem.Contains(OllamaModels.FastModel, StringComparison.Ordinal), "persona_distinguishes_actual_local_model_from_character");
                    fake.Enqueue(FixturePlan.Complete("代号是松果。"));
                    await SendComplete("刚才的代号是什么？", "second_turn");
                    var second = LastChat(fake).Payload.GetProperty("messages");
                    Require(second.GetArrayLength() == 4 && second[1].GetProperty("content").GetString() == "测试代号是松果，请记住。" && second[2].GetProperty("content").GetString() == "你好，松果。", "wire_contains_complete_prior_user_and_assistant_turn");
                    Require(LastChat(fake).Payload.GetProperty("think").ValueKind == JsonValueKind.False && LastChat(fake).Payload.GetProperty("options").GetProperty("num_ctx").GetInt32() == 8192, "wire_uses_non_thinking_8k_local_configuration");

                    phase = "fake_images";
                    await window.AddImageFilesAsync(new[] { imagePath });
                    Require(previews.Children.Count == 1 && Descendants<Image>(previews).Single().Source != null, "selected_image_has_real_preview");
                    Click(Descendants<Button>(previews).Single());
                    Require(previews.Children.Count == 0, "remove_image_button_removes_pending_attachment");
                    await window.AddImageFilesAsync(new[] { imagePath });
                    fake.Enqueue(FixturePlan.Complete("左边红色方块，右边蓝色圆形。"));
                    await SendComplete("请看图片。", "image_turn");
                    var imageMessages = LastChat(fake).Payload.GetProperty("messages");
                    string encoded = imageMessages[imageMessages.GetArrayLength() - 1].GetProperty("images")[0].GetString()!;
                    byte[] bytes = Convert.FromBase64String(encoded);
                    using (var imageStream = new MemoryStream(bytes))
                    {
                        var frame = BitmapDecoder.Create(imageStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                        Require(frame.PixelWidth == 480 && frame.PixelHeight == 240 && bytes[0] == 137 && bytes[1] == 80, "wire_image_is_decodable_png_base64_not_a_path");
                    }
                    Require(previews.Children.Count == 0, "successful_send_clears_pending_image_preview");
                    await SettleAsync(window);
                    Capture(window, Path.Combine(evidence, "image-conversation.png")); captures.Add("image-conversation.png");

                    phase = "fake_model_switch";
                    int requestBeforeSwitch = fake.Snapshot().Count;
                    models.SelectedValue = OllamaModels.QualityModel;
                    await UntilAsync(() => models.IsEnabled && settings.OllamaModel == OllamaModels.QualityModel, "quality_model_switch");
                    var switching = fake.Snapshot().Skip(requestBeforeSwitch).ToArray();
                    var unloads = switching.Where(call => call.Path == "/api/generate").ToArray();
                    Require(unloads.Length == 1 && unloads[0].Payload.GetProperty("model").GetString() == OllamaModels.FastModel && unloads[0].Payload.GetProperty("keep_alive").GetInt32() == 0 && switching.All(call => call.Path != "/api/chat"), "switch_unloads_old_model_before_new_inference");
                    fake.Enqueue(FixturePlan.Complete("换模型后我仍记得上下文。"));
                    await SendComplete("换模型后还记得对话吗？", "switched_turn");
                    Require(LastChat(fake).Payload.GetProperty("model").GetString() == OllamaModels.QualityModel && LastChat(fake).Payload.GetProperty("messages").GetArrayLength() == 8, "new_model_receives_retained_complete_history");
                    Require(LastChat(fake).Payload.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("蓝色大肥鱼", StringComparison.Ordinal), "model_switch_keeps_deepseek_character");
                    fake.FailNextUnload = true;
                    models.SelectedValue = OllamaModels.FastModel;
                    await UntilAsync(() => models.IsEnabled, "failed_switch_settle");
                    Require(settings.OllamaModel == OllamaModels.QualityModel && Equals(models.SelectedValue, OllamaModels.QualityModel) && savedCallbacks == 1, "failed_unload_keeps_prior_model_and_preference");

                    phase = "fake_stop";
                    int completedBubbles = bubbles.Count;
                    fake.Enqueue(FixturePlan.Block("这段尚未完成的回答"));
                    input.Text = "请生成一段需要停止的回复。";
                    Task pending = window.SendAsync();
                    await UntilAsync(() => fake.BlockedReads > 0 && Named<Button>(window, "OllamaStop").IsEnabled, "stream_waiting_for_stop");
                    Require(busy[^1], "generation_sets_independent_pet_busy_callback");
                    Click(Named<Button>(window, "OllamaStop"));
                    await pending;
                    Require(recording.Runs[^1].Cancelled && bubbles.Count == completedBubbles && input.Text.Contains("停止", StringComparison.Ordinal) && !busy[^1], "stop_cancels_transport_restores_draft_and_suppresses_reply_bubble");
                    fake.Enqueue(FixturePlan.Complete("停止后可以继续。"));
                    await SendComplete("停止后重新发一个问题。", "after_stop");
                    Require(LastChat(fake).Payload.GetProperty("messages").GetArrayLength() == 10 && !WireMessageText(LastChat(fake)).Contains("这段尚未完成", StringComparison.Ordinal), "cancelled_turn_never_enters_next_request_context");

                    phase = "fake_errors";
                    await window.AddImageFilesAsync(new[] { imagePath });
                    fake.Enqueue(FixturePlan.Error("错误前的残缺回答"));
                    input.Text = "图片请求遇到错误。";
                    await window.SendAsync();
                    Require(!recording.Runs[^1].Completed && recording.Runs[^1].ErrorType != null && previews.Children.Count == 1 && input.Text == "图片请求遇到错误。", "stream_error_restores_message_and_image");
                    fake.Enqueue(FixturePlan.Eof("断开前的残缺回答"));
                    await window.SendAsync();
                    Require(!recording.Runs[^1].Completed && recording.Runs[^1].ErrorType != null && !Status(window).StartsWith("回复完成", StringComparison.Ordinal), "stream_without_done_is_failure");
                    fake.Enqueue(FixturePlan.Complete("现在回复完整了。"));
                    await SendComplete("失败后重试图片。", "after_failure");
                    var retry = WireMessageText(LastChat(fake));
                    Require(LastChat(fake).Payload.GetProperty("messages").GetArrayLength() == 12 && !retry.Contains("残缺回答", StringComparison.Ordinal), "failed_and_incomplete_turns_never_enter_context");

                    phase = "fake_hide_reopen";
                    window.Close();
                    Require(!window.IsVisible && !recording.Disposed, "close_hides_reusable_chat_without_disposing_client");
                    window.Show(); await SettleAsync(window);
                    fake.Enqueue(FixturePlan.Complete("重新打开后对话还在。"));
                    await SendComplete("重新打开窗口后继续聊天。", "reopened_turn");
                    Require(LastChat(fake).Payload.GetProperty("messages").GetArrayLength() == 14, "reopening_retains_memory_history");
                    Click(Named<Button>(window, "OllamaNewConversation"));
                    await UntilAsync(() => Status(window).StartsWith("新对话", StringComparison.Ordinal), "clear_conversation");
                    fake.Enqueue(FixturePlan.Complete("这是全新的对话。"));
                    await SendComplete("开始一个新对话。", "new_conversation");
                    Require(LastChat(fake).Payload.GetProperty("messages").GetArrayLength() == 2, "new_conversation_clears_previous_context");

                    phase = "fake_persona_scope";
                    settings.SkinId = "gemini"; window.RefreshCharacter();
                    fake.Enqueue(FixturePlan.Complete("这是另一位桌宠的对话。"));
                    await SendComplete("新角色的第一句话。", "other_skin");
                    var otherSkin = LastChat(fake).Payload.GetProperty("messages");
                    Require(otherSkin.GetArrayLength() == 2 && !otherSkin[0].GetProperty("content").GetString()!.Contains("蓝色大肥鱼", StringComparison.Ordinal), "other_skin_has_no_deepseek_persona_or_prior_character_history");
                    settings.SkinId = "gpt"; window.RefreshCharacter();
                    fake.Enqueue(FixturePlan.Complete("宝库先锁好了，把问题给我看看。"));
                    await SendComplete("白龙娘，今天一起工作吧。", "gpt_skin");
                    var dragonMessages = LastChat(fake).Payload.GetProperty("messages");
                    string dragonSystem = dragonMessages[0].GetProperty("content").GetString()!;
                    Require(dragonMessages.GetArrayLength() == 2 && dragonSystem.Contains("GPT 小龙娘", StringComparison.Ordinal) &&
                        dragonSystem.Contains("白发紫眼", StringComparison.Ordinal) && dragonSystem.Contains("非官方", StringComparison.Ordinal) &&
                        dragonSystem.Contains("角色名是GPT", StringComparison.Ordinal) &&
                        !dragonSystem.Contains("白米饭", StringComparison.Ordinal) && !dragonSystem.Contains("蓝色大肥鱼", StringComparison.Ordinal),
                        "gpt_skin_sends_its_dragon_persona_without_whale_identity_or_other_character_history");
                    Require(dragonSystem.Contains(settings.OllamaModel, StringComparison.Ordinal) &&
                        dragonSystem.Contains("角色皮肤不代表后端模型", StringComparison.Ordinal) &&
                        dragonSystem.Contains("不能索取真实付款", StringComparison.Ordinal) &&
                        dragonSystem.Contains("本轮未启用联网", StringComparison.Ordinal),
                        "gpt_persona_preserves_actual_model_identity_no_real_payment_and_web_capability_boundaries");
                    settings.SkinId = "deepseek"; window.RefreshCharacter();
                    fake.Enqueue(FixturePlan.Complete("本鲸回来了。"));
                    await SendComplete("切回大肥鱼继续。", "returning_skin");
                    var returned = LastChat(fake).Payload.GetProperty("messages");
                    Require(returned.GetArrayLength() == 4 && !WireMessageText(LastChat(fake)).Contains("新角色的第一句话", StringComparison.Ordinal), "returning_skin_restores_its_own_complete_turns");

                    phase = "fake_persona_budget";
                    await window.AddImageFilesAsync(new[] { imagePath, imagePath });
                    int beforeOversize = recording.Runs.Count;
                    input.Text = new string('饭', 2000); await window.SendAsync();
                    Require(recording.Runs.Count == beforeOversize && input.Text.Length == 2000 && previews.Children.Count == 2 && Status(window).Contains("缩短", StringComparison.Ordinal), "persona_and_two_images_reserve_context_before_sending_and_keep_draft");
                    fake.Enqueue(FixturePlan.Complete("两张图片都收到了。"));
                    await SendComplete("请比较两张图。", "two_images_with_persona");
                    int plannedTokens = recording.Runs[^1].Messages.Sum(message => PetPersona.EstimateTokens(message.Content, message.Images?.Count ?? 0));
                    Require(plannedTokens + OllamaClient.ResponseTokens + 256 <= OllamaClient.ContextTokens, "persona_history_images_and_output_fit_conservative_8k_budget");

                    phase = "fake_skin_change_during_stream";
                    int beforeSkinCancel = bubbles.Count;
                    string? oldSkinConversation = window.CurrentConversationId;
                    int skinBlocked = fake.BlockedReads;
                    fake.Enqueue(FixturePlan.Block("未完成的大肥鱼回复"));
                    input.Text = "换皮肤时保留这条草稿。";
                    Task skinPending = window.SendAsync();
                    await UntilAsync(() => fake.BlockedReads > skinBlocked, "skin_change_active_stream");
                    settings.SkinId = "gemini"; window.RefreshCharacter(); await skinPending;
                    await UntilAsync(() => window.CurrentConversationId != oldSkinConversation, "skin_archive_switch_completed");
                    Require(recording.Runs[^1].Cancelled && bubbles.Count == beforeSkinCancel && input.Text.Length == 0, "skin_change_cancels_old_reply_and_keeps_other_character_draft_separate");
                    settings.SkinId = "deepseek"; window.RefreshCharacter();
                    await UntilAsync(() => window.CurrentConversationId == oldSkinConversation, "skin_archive_return_completed");
                    Require(input.Text == "换皮肤时保留这条草稿。", "returning_skin_recovers_cancelled_request_draft");

                    phase = "fake_shutdown";
                    int blockedBefore = fake.BlockedReads;
                    fake.Enqueue(FixturePlan.Block("退出前的残缺回答"));
                    input.Text = "关闭程序时取消本条。";
                    pending = window.SendAsync();
                    await UntilAsync(() => fake.BlockedReads > blockedBefore, "stream_waiting_for_shutdown");
                    window.Shutdown();
                    await pending;
                    Require(recording.Disposed && fake.Disposed && recording.Runs[^1].Cancelled && !busy[^1], "shutdown_cancels_generation_disposes_client_and_releases_busy");
                    Require(fake.Snapshot().All(call => call.IsLoopback && call.Path is "/api/tags" or "/api/show" or "/api/chat" or "/api/generate"), "all_requests_use_allowed_loopback_endpoints");
                    phase = "fake_dynamic_models_and_connections";
                    await CheckDynamicConnectionsAsync(imagePath, evidence, captures, Require);
                }
                passed = true;
            }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                if (live)
                {
                    phase = passed ? "live_cleanup" : phase;
                    foreach (string model in recording.Runs.Select(run => run.Model).Distinct().ToArray())
                    {
                        try
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                            await recording.UnloadAsync(model, cleanup.Token);
                            checks.Add(model + "_unloaded_after_test");
                        }
                        catch (Exception ex) { passed = false; failure ??= "model_cleanup_" + ex.GetType().Name; }
                    }
                }
                Finish(passed, failure);
            }
        };
        app.Run();
        return result;
    }

    private static async Task CheckDynamicConnectionsAsync(string imagePath, string evidence, List<string> captures, Action<bool, string> require)
    {
        var customSettings = new DesktopSettings { OllamaAddress = " http://192.0.2.20:21434/ ", OllamaModel = "myorg/fine-tune:Q4_K_M", CodexEnabled = false };
        customSettings.Normalize();
        var restored = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(customSettings))!;
        restored.Normalize();
        require(restored.OllamaAddress == "http://192.0.2.20:21434" && restored.OllamaModel == "myorg/fine-tune:Q4_K_M" && !restored.CodexEnabled, "settings_json_roundtrip_and_normalize_preserve_custom_server_and_model");
        var legacy = JsonSerializer.Deserialize<DesktopSettings>("{\"OllamaModel\":\"qwen3.8:27b-q4_K_M\"}")!;
        legacy.Normalize();
        require(legacy.OllamaAddress == OllamaEndpoint.DefaultAddress && legacy.OllamaModel == OllamaModels.QualityModel, "legacy_settings_without_address_fall_back_to_local_server");
        const string textModel = "synthetic-text:3b";
        const string embeddingModel = "synthetic-embed:small";
        const string lateModel = "synthetic-stale:1b";
        const string currentModel = "synthetic-current:2b";
        var settings = new DesktopSettings { CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false, WebSearchEnabled = false };
        var sourceHandler = new FixtureHandler { ModelNames = new[] { OllamaModels.FastModel, OllamaModels.QualityModel, textModel, embeddingModel } };
        sourceHandler.ModelCapabilities[embeddingModel] = new[] { "embedding" };
        var source = new RecordingClient(new OllamaClient(sourceHandler));
        var created = new List<(RecordingClient Client, FixtureHandler Handler)>();
        int saves = 0;
        bool targetUnavailable = true;
        IOllamaClient Factory(Uri endpoint)
        {
            // Every client, including address tests, stays inside an injected memory transport.
            var handler = new FixtureHandler { FailTags = targetUnavailable };
            var client = new RecordingClient(new OllamaClient(endpoint, handler));
            created.Add((client, handler));
            return client;
        }
        var window = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, _ => { }, () => saves++, source, Factory)
            { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        TagsPlan? delayed = null;
        try
        {
            window.Show(); await SettleAsync(window);
            var models = Named<ComboBox>(window, "OllamaModelSelector");
            var input = Named<TextBox>(window, "OllamaChatInput");
            var previews = Named<WrapPanel>(window, "OllamaImagePreviews");
            var send = Named<Button>(window, "OllamaSend");
            string[] Listed() => models.Items.Cast<OllamaModelChoice>().Select(choice => choice.Id).ToArray();
            async Task Select(string model)
            {
                models.SelectedValue = model;
                await UntilAsync(() => models.IsEnabled && settings.OllamaModel == model, "dynamic_select_" + model);
            }
            async Task Complete(FixtureHandler handler, RecordingClient client, string prompt, string response)
            {
                int before = client.Runs.Count;
                handler.Enqueue(FixturePlan.Complete(response)); input.Text = prompt; await window.SendAsync();
                require(client.Runs.Count == before + 1 && client.Runs[^1].Completed && client.Runs[^1].ErrorType == null, "dynamic_complete_" + prompt);
            }
            OllamaConnectionWindow OpenSettings()
            {
                Click(Named<Button>(window, "OllamaConnectionSettings"));
                return window.OwnedWindows.OfType<OllamaConnectionWindow>().Single(child => child.IsVisible);
            }
            await UntilAsync(() => models.IsEnabled, "dynamic_models_ready");
            require(Listed().Length == 4 && Listed().Contains(textModel) && Listed().Contains(embeddingModel), "selector_lists_arbitrary_installed_models_not_only_two_recommendations");
            await window.AddImageFilesAsync(new[] { imagePath });
            await Complete(sourceHandler, source, "旧服务器图片历史标记", "图中有红色方块和蓝色圆形。");
            await Select(textModel);
            require(!Named<Button>(window, "OllamaAttachImage").IsEnabled && !Named<Button>(window, "OllamaPasteImage").IsEnabled, "text_only_capability_disables_image_buttons");
            await window.AddImageFilesAsync(new[] { imagePath });
            require(previews.Children.Count == 0, "text_only_capability_blocks_direct_attachment_and_drop_path");
            await Complete(sourceHandler, source, "第三个模型的文字问题", "第三个模型也可以回复。");
            var textRequest = LastChat(sourceHandler).Payload;
            require(textRequest.GetProperty("model").GetString() == textModel && source.Runs[^1].Messages.All(message => message.Images?.Count is not > 0), "dynamic_text_model_receives_its_name_and_no_old_image_turns");
            require(!textRequest.TryGetProperty("think", out _), "text_only_model_does_not_receive_unsupported_thinking_option");
            await Select(embeddingModel); input.Text = "不可向向量模型发送的合成输入";
            int beforeUnsupported = source.Runs.Count;
            require(!send.IsEnabled, "embedding_only_capability_disables_send");
            await window.SendAsync();
            require(source.Runs.Count == beforeUnsupported, "embedding_only_capability_blocks_direct_send_entry");

            sourceHandler.ModelNames = new[] { OllamaModels.FastModel, currentModel };
            await window.CheckConnectionAsync();
            require(!Listed().Contains(embeddingModel) && Listed().Contains(currentModel) && models.SelectedValue is string selected && Listed().Contains(selected), "refresh_replaces_deleted_selection_with_installed_model");
            sourceHandler.ModelNames = Array.Empty<string>(); await window.CheckConnectionAsync();
            input.Text = "空服务器不应发送"; int beforeEmpty = source.Runs.Count;
            await window.SendAsync();
            require(models.Items.Count == 0 && !models.IsEnabled && !send.IsEnabled && source.Runs.Count == beforeEmpty, "empty_model_list_has_no_selection_and_cannot_infer");
            sourceHandler.ModelNames = new[] { OllamaModels.FastModel, currentModel }; await window.CheckConnectionAsync();
            require(models.IsEnabled && Listed().Contains(currentModel), "refresh_recovers_after_models_are_installed");

            delayed = new TagsPlan(lateModel); sourceHandler.EnqueueTags(delayed);
            Task oldRefresh = window.CheckConnectionAsync(); await delayed.Started.Task;
            int tagsDuringRefresh = sourceHandler.Snapshot().Count(call => call.Path == "/api/tags");
            await window.CheckConnectionAsync();
            require(sourceHandler.Snapshot().Count(call => call.Path == "/api/tags") == tagsDuringRefresh && !send.IsEnabled, "duplicate_refresh_during_pending_discovery_does_not_race_or_allow_send");
            window.Hide(); window.Show(); await window.CheckConnectionAsync();
            await UntilAsync(() => models.IsEnabled && Listed().Contains(currentModel), "new_discovery_after_cancel");
            delayed.Release.TrySetResult(true); await oldRefresh;
            require(Listed().Contains(currentModel) && !Listed().Contains(lateModel), "cancelled_late_discovery_cannot_replace_new_model_list");
            // Refresh the transport cache too after the intentionally late server reply.
            await window.CheckConnectionAsync(); await Select(OllamaModels.FastModel);
            await Complete(sourceHandler, source, "旧服务器上下文不应跨地址", "只有旧服务器知道的合成回复。");
            await window.AddImageFilesAsync(new[] { imagePath }); input.Text = "旧服务器未发送的图片草稿";
            string oldAddress = settings.OllamaAddress; string oldModel = settings.OllamaModel; int oldSaves = saves;
            var connection = OpenSettings(); await SettleAsync(connection);
            require(connection.Owner == window && !send.IsEnabled && !models.IsEnabled, "connection_settings_owns_dialog_and_blocks_composer_until_closed");
            var address = Named<TextBox>(connection, "OllamaAddressInput");
            address.Text = "http://127.0.0.1:21434";
            await connection.TestConnectionAsync();
            require(!Named<Button>(connection, "OllamaApplyConnection").IsEnabled && settings.OllamaAddress == oldAddress && saves == oldSaves && !source.Disposed, "unreachable_address_test_preserves_current_server_and_saved_settings");
            require(created.Count > 0 && created.All(entry => entry.Handler.Snapshot().All(call => call.Path == "/api/tags")), "testing_address_only_lists_models_without_show_chat_or_unload");
            targetUnavailable = false;
            await connection.TestConnectionAsync();
            require(Named<Button>(connection, "OllamaApplyConnection").IsEnabled, "successful_address_test_enables_apply");
            address.Text = "http://127.0.0.1:21435";
            require(!Named<Button>(connection, "OllamaApplyConnection").IsEnabled, "editing_tested_address_requires_new_test_before_apply");
            address.Text = "http://127.0.0.1:21434"; await connection.TestConnectionAsync();
            created[^1].Handler.FailShow = true;
            await connection.ApplyAsync();
            require(connection.IsVisible && settings.OllamaAddress == oldAddress && settings.OllamaModel == oldModel && saves == oldSaves && !source.Disposed && previews.Children.Count == 1 && input.Text == "旧服务器未发送的图片草稿", "failed_address_apply_keeps_old_client_preferences_and_pending_data");
            await connection.TestConnectionAsync();
            require(created.All(entry => entry.Client.Runs.Count == 0), "connection_validation_never_sends_conversation_or_attachments_to_candidate");
            await SettleAsync(connection);
            require(connection.Title.EndsWith(AppVersion.Label, StringComparison.Ordinal) && Descendants<TextBlock>(connection).Any(block => block.Name == "AppVersionLabel" && block.Text.Contains(AppVersion.Label, StringComparison.Ordinal)), "connection_window_displays_running_version");
            Capture(connection, Path.Combine(evidence, "connection-settings.png")); captures.Add("connection-settings.png");
            await connection.ApplyAsync();
            require(!connection.IsVisible && settings.OllamaAddress == "http://127.0.0.1:21434" && saves == oldSaves + 1 && source.Disposed, "successful_apply_saves_new_endpoint_once_and_releases_old_client");
            require(previews.Children.Count == 0 && input.Text.Length == 0, "server_change_clears_pending_images_and_unsent_draft");
            var active = created.Last(entry => !entry.Client.Disposed);
            await Complete(active.Handler, active.Client, "新服务器的第一条合成输入", "新服务器只收到新对话。");
            var newMessages = active.Client.Runs[^1].Messages;
            require(newMessages.Count == 2 && newMessages.All(message => message.Images?.Count is not > 0) && newMessages.All(message => !message.Content.Contains("旧服务器", StringComparison.Ordinal)), "new_server_first_request_excludes_old_history_images_and_draft");
            require(source.UnloadedModels.Contains(OllamaModels.FastModel), "changing_server_releases_model_used_on_previous_endpoint");
            window.Shutdown();
            require(created.All(entry => entry.Client.Disposed && entry.Handler.Disposed), "all_test_candidate_and_adopted_clients_are_disposed");
        }
        finally
        {
            delayed?.Release.TrySetResult(true);
            window.Shutdown(); source.Dispose();
            foreach (var entry in created) entry.Client.Dispose();
        }
    }

    private static string Status(OllamaChatWindow window) => Named<TextBlock>(window, "OllamaChatStatus").Text;
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(element => element.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(70);
        window.UpdateLayout();
    }
    private static async Task UntilAsync(Func<bool> condition, string label, int seconds = 15)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(label);
            await Task.Delay(20);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void CreateImageFixture(string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 480, 240));
            drawing.DrawRectangle(Brushes.Red, null, new Rect(55, 55, 130, 130));
            drawing.DrawEllipse(Brushes.Blue, null, new Point(345, 120), 65, 65);
        }
        var bitmap = new RenderTargetBitmap(480, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private sealed class ChatRun
    {
        internal required string Model { get; init; }
        internal required IReadOnlyList<OllamaMessage> Messages { get; init; }
        internal bool Completed, Cancelled;
        internal string? ErrorType;
        internal string Answer = "";
        internal double ElapsedSeconds;
        internal double? TokensPerSecond;
    }
    private sealed class RecordingClient : IOllamaClient
    {
        private readonly IOllamaClient inner;
        internal List<ChatRun> Runs { get; } = new();
        internal List<string> UnloadedModels { get; } = new();
        internal bool Disposed { get; private set; }
        internal RecordingClient(IOllamaClient inner) => this.inner = inner;
        public Uri Endpoint => inner.Endpoint;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => inner.ListModelsAsync(ct);
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => inner.GetModelInfoAsync(model, ct);
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            var run = new ChatRun { Model = model, Messages = messages.ToArray() }; Runs.Add(run);
            var timer = Stopwatch.StartNew();
            await using var enumerator = inner.ChatAsync(model, messages, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool next;
                    try { next = await enumerator.MoveNextAsync(); }
                    catch (Exception ex) { run.Cancelled = ex is OperationCanceledException; run.ErrorType = ex.GetType().Name; throw; }
                    if (!next) break;
                    var delta = enumerator.Current;
                    run.Answer += delta.Content;
                    if (delta.Done) { run.Completed = true; run.TokensPerSecond = delta.TokensPerSecond; }
                    yield return delta;
                }
            }
            finally { run.ElapsedSeconds = timer.Elapsed.TotalSeconds; }
        }
        public async Task UnloadAsync(string model, CancellationToken ct) { await inner.UnloadAsync(model, ct); UnloadedModels.Add(model); }
        public void Dispose() { if (Disposed) return; Disposed = true; inner.Dispose(); }
    }

    private sealed record WireCall(string Path, bool IsLoopback, JsonElement Payload);
    private static string WireMessageText(WireCall call) => string.Join("\n", call.Payload.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").GetString()));
    private sealed record FixturePlan(string Text, string End)
    {
        internal static FixturePlan Complete(string text) => new(text, "done");
        internal static FixturePlan Block(string text) => new(text, "block");
        internal static FixturePlan Error(string text) => new(text, "error");
        internal static FixturePlan Eof(string text) => new(text, "eof");
    }
    private static WireCall LastChat(FixtureHandler handler) => handler.Snapshot().Last(call => call.Path == "/api/chat");
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Queue<FixturePlan> plans = new();
        private readonly Queue<TagsPlan> tagsPlans = new();
        private readonly List<WireCall> requests = new();
        internal bool FailNextUnload, Disposed;
        internal bool FailTags, FailShow;
        internal string[] ModelNames = OllamaModels.Supported.Select(choice => choice.Id).ToArray();
        internal readonly Dictionary<string, string[]> ModelCapabilities = new(StringComparer.Ordinal);
        private int blockedReads;
        internal int BlockedReads => Volatile.Read(ref blockedReads);
        internal void Enqueue(FixturePlan plan) { lock (gate) plans.Enqueue(plan); }
        internal void EnqueueTags(TagsPlan plan) { lock (gate) tagsPlans.Enqueue(plan); }
        internal IReadOnlyList<WireCall> Snapshot() { lock (gate) return requests.ToArray(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var uri = request.RequestUri!;
            lock (gate) requests.Add(new WireCall(uri.AbsolutePath, uri.IsLoopback && uri.Port == 11434 && uri.Scheme == "http", document.RootElement.Clone()));
            if (uri.AbsolutePath == "/api/tags")
            {
                TagsPlan? tagsPlan;
                lock (gate) tagsPlan = tagsPlans.Count > 0 ? tagsPlans.Dequeue() : null;
                if (tagsPlan != null)
                {
                    tagsPlan.Started.TrySetResult(true);
                    // Deliberately allow late replies even after cancellation, to test UI request generations.
                    await tagsPlan.Release.Task;
                }
                if (FailTags) return Json(new { error = "synthetic unavailable server" }, HttpStatusCode.ServiceUnavailable);
                return Json(new { models = (tagsPlan?.Models ?? ModelNames).Select(name => new { name }).ToArray() });
            }
            if (uri.AbsolutePath == "/api/show")
            {
                if (FailShow) return Json(new { error = "synthetic model metadata failure" }, HttpStatusCode.ServiceUnavailable);
                string model = document.RootElement.GetProperty("model").GetString()!;
                var capabilities = ModelCapabilities.TryGetValue(model, out var configured) ? configured :
                    model == OllamaModels.FastModel || model == OllamaModels.QualityModel ? new[] { "completion", "vision", "thinking" } : new[] { "completion" };
                if (capabilities.Contains("thinking")) return Json(new { capabilities, thinking = new { values = new[] { true, false }, @default = true } });
                return Json(new { capabilities });
            }
            if (uri.AbsolutePath == "/api/generate")
            {
                if (FailNextUnload) { FailNextUnload = false; return Json(new { error = "synthetic unload failure" }, HttpStatusCode.InternalServerError); }
                return Json(new { done = true });
            }
            if (uri.AbsolutePath != "/api/chat") throw new InvalidOperationException("Unexpected endpoint.");
            FixturePlan plan;
            lock (gate) plan = plans.Count > 0 ? plans.Dequeue() : throw new InvalidOperationException("Unexpected inference request.");
            string prefix = JsonSerializer.Serialize(new { message = new { content = plan.Text }, done = false }) + "\n";
            string suffix = plan.End switch
            {
                "done" => JsonSerializer.Serialize(new { message = new { content = "" }, done = true, eval_count = 12, eval_duration = 200_000_000 }) + "\n",
                "error" => "{\"error\":\"synthetic generation error\"}\n",
                _ => ""
            };
            var stream = new FixtureStream(Encoding.UTF8.GetBytes(prefix + suffix), plan.End == "block", () => Interlocked.Increment(ref blockedReads));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TagsPlan
    {
        internal string[] Models { get; }
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TagsPlan(params string[] models) => Models = models;
    }
    private sealed class FixtureStream : Stream
    {
        private readonly byte[] bytes;
        private readonly bool block;
        private readonly Action onBlocked;
        private int offset;
        internal FixtureStream(byte[] bytes, bool block, Action onBlocked) { this.bytes = bytes; this.block = block; this.onBlocked = onBlocked; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset < bytes.Length)
            {
                int count = Math.Min(buffer.Length, bytes.Length - offset);
                bytes.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
            }
            if (block) { onBlocked(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(start, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int start, int count) => ReadAsync(buffer, start, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
}
