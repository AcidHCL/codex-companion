using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DesktopPet;

// Synthetic notification data only: never opens an account connection, Ollama,
// conversation database, settings file, or an audio output device.
internal static class ScheduledNoticeUiChecks
{
    internal static int Run()
    {
        string language = L.Language;
        string evidence = Path.Combine(AppContext.BaseDirectory, "scheduled-notice-ui-evidence");
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        PetWindow? pet = null;
        int result = 1;
        bool finished = false;

        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true;
            timeout.Stop();
            try { pet?.Close(); }
            catch (Exception ex) { passed = false; error = "cleanup_" + ex.GetType().Name; }
            L.Initialize(language);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "scheduled-notice-ui-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, assertions = checks.Count, checks, error,
                audioOutput = false, realAccountRequests = 0, httpRequests = 0,
                userSettingsWrites = 0, productionDatabaseOpened = false, modelLoads = 0,
                screenshots = new[] { "scheduled-notice-ui-evidence/scheduled-result-top.png", "scheduled-notice-ui-evidence/scheduled-result-bottom.png" }
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "scheduled_notice_ui_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                await CheckPresenterAsync(Check);
                L.Initialize(language);
                var settings = new DesktopSettings
                {
                    SkinId = "gemini", Size = 240, CodexEnabled = false,
                    WebSearchEnabled = false, SharedMemoryEnabled = false,
                    SpeechEnabled = true, SoundEnabled = false,
                    Topmost = false, MovementEnabled = false, MotionEnabled = false,
                    SceneAutoEnabled = false
                };
                pet = new PetWindow(SkinCatalog.Load(AppContext.BaseDirectory), settings, persist: false, subscribeToSystemEvents: false);
                pet.Show();
                var speech = Field<TextBlock>(pet, "_speech");
                var scroll = Field<ScrollViewer>(pet, "_speechScroll");
                var bubble = Field<Border>(pet, "_bubble");
                var timer = Field<DispatcherTimer>(pet, "_speechTimer");
                string body = string.Concat(Enumerable.Repeat("这是合成定时任务的完整结果，关键数字和限制必须全部保留。", 20))[..400];
                string full = L.T("定时任务完成：", "Scheduled task completed: ") + body;
                pet.SayScheduledTask(full);
                await Settle(pet);
                Check(speech.Text == full && speech.Text.Length > 400, "scheduled_short_result_retains_all_400_characters_and_prefix");
                Check(speech.TextTrimming == TextTrimming.None && double.IsPositiveInfinity(speech.MaxHeight), "scheduled_result_has_no_ellipsis_or_text_height_truncation");
                Check(bubble.IsHitTestVisible && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                    "scheduled_result_allows_scroll_interaction");
                Check(scroll.ScrollableHeight > 0 && scroll.ViewportHeight <= 220 && scroll.ViewportHeight > 80,
                    "long_result_has_a_bounded_scrollable_viewport");
                Check(timer.Interval.TotalSeconds >= 80 && timer.IsEnabled, "scheduled_reading_time_scales_with_text_length");
                Check(!pet.IsSpeaking, "persist_false_fixture_never_generates_or_plays_voice");
                pet.SavePreview(Path.Combine(evidence, "scheduled-result-top.png"));
                scroll.ScrollToBottom();
                await Settle(pet);
                Check(scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                    "last_line_of_complete_result_is_reachable");
                pet.SavePreview(Path.Combine(evidence, "scheduled-result-bottom.png"));

                pet.SayScheduledTask(full);
                await Settle(pet);
                Check(scroll.VerticalOffset == 0, "next_scheduled_result_starts_at_its_first_line");
                pet.Say(L.T("任务已完成：合成普通任务", "Task completed: synthetic ordinary task"));
                await Settle(pet);
                Check(speech.MaxHeight == 62 && speech.TextTrimming == TextTrimming.CharacterEllipsis &&
                    speech.TextAlignment == TextAlignment.Center && !bubble.IsHitTestVisible &&
                    scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                    "ordinary_notification_restores_compact_noninteractive_bubble");
                Check(Math.Abs(timer.Interval.TotalSeconds - 3.3) < .001, "ordinary_notification_restores_default_timeout");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run();
        return result;
    }

    private static async Task CheckPresenterAsync(Action<bool, string> check)
    {
        const string address = "http://127.0.0.1:11434";
        var shown = new List<(PetNotice Notice, CodexNoticePresentation Presentation)>();
        int describes = 0;
        var scheduled = new PetNotice("定时任务完成", "合成对话", "complete") { IsScheduled = true, ScheduledOutput = "已完成核验，全部通过。" };
        using (var presenter = new CodexNoticePresenter((source, _, _, _) =>
        {
            describes++;
            return Task.FromResult(new ScheduledTaskAnnouncementResult(source, false));
        }, (notice, presentation) => shown.Add((notice, presentation))))
        {
            foreach (string language in new[] { L.Chinese, L.English })
            {
                L.Initialize(language);
                var localized = scheduled with { Title = L.T("定时任务完成", "Scheduled task completed") };
                await presenter.ShowAsync(localized, "fixture-model", address, () => true);
                var last = shown[^1].Presentation;
                check(last.Body == scheduled.ScheduledOutput && last.Expanded &&
                    last.Text == localized.Title + (L.IsEnglish ? ": " : "：") + scheduled.ScheduledOutput,
                    "whole_short_output_and_localized_scheduled_title_" + language);
            }
            L.Initialize(L.Chinese);
            var normal = new PetNotice("任务已完成", "合成手动任务", "complete");
            int before = describes;
            await presenter.ShowAsync(normal, "fixture-model", address, () => true);
            check(describes == before && shown[^1].Presentation.Text == "任务已完成：合成手动任务" && !shown[^1].Presentation.Expanded,
                "ordinary_completion_bypasses_summary_and_preserves_existing_announcement");
            int count = shown.Count;
            await presenter.ShowAsync(scheduled, "fixture-model", address, () => false);
            check(shown.Count == count && describes == before, "initially_revoked_notice_does_not_summarize_or_display");
        }
        using (var presenter = new CodexNoticePresenter((_, _, _, _) =>
            Task.FromResult(ScheduledTaskAnnouncementResult.Fallback), (notice, presentation) => shown.Add((notice, presentation))))
        {
            await presenter.ShowAsync(scheduled, "fixture-model", address, () => true);
            check(shown[^1].Presentation.Text == "定时任务完成：合成对话" && !shown[^1].Presentation.Expanded,
                "missing_resident_model_falls_back_to_scheduled_title_and_conversation_name");
        }
        using (var presenter = new CodexNoticePresenter((_, _, _, _) =>
            throw new HttpRequestException("synthetic unavailable service"), (notice, presentation) => shown.Add((notice, presentation))))
        {
            await presenter.ShowAsync(scheduled, "fixture-model", address, () => true);
            check(shown[^1].Presentation.Text == "定时任务完成：合成对话",
                "summary_service_error_does_not_drop_the_completion_notice");
        }

        var release = new TaskCompletionSource<ScheduledTaskAnnouncementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        using (var presenter = new CodexNoticePresenter((source, _, _, token) =>
        {
            if (source == "slow") { oldToken = token; return release.Task; }
            return Task.FromResult(new ScheduledTaskAnnouncementResult("newest result", false));
        }, (notice, presentation) => shown.Add((notice, presentation))))
        {
            int before = shown.Count;
            Task old = presenter.ShowAsync(scheduled with { ScheduledOutput = "slow" }, "fixture-model", address, () => true);
            await presenter.ShowAsync(scheduled with { ScheduledOutput = "new" }, "fixture-model", address, () => true);
            check(oldToken.IsCancellationRequested && shown.Count == before + 1 && shown[^1].Presentation.Body == "newest result",
                "new_notice_cancels_pending_summary_and_displays_newest_result");
            release.SetResult(new("stale result", true));
            await old;
            check(shown.Count == before + 1 && shown[^1].Presentation.Body == "newest result",
                "late_noncooperative_summary_cannot_overwrite_new_notice");
        }

        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool eligible = true;
        using (var presenter = new CodexNoticePresenter((_, _, _, token) =>
        { oldToken = token; return release.Task; }, (notice, presentation) => shown.Add((notice, presentation))))
        {
            int before = shown.Count;
            Task pending = presenter.ShowAsync(scheduled, "fixture-model", address, () => eligible);
            eligible = false;
            presenter.Revalidate();
            check(oldToken.IsCancellationRequested, "revoking_message_access_cancels_pending_summary");
            // Re-enabling must not revive a result started under the old permission.
            eligible = true;
            release.SetResult(new("revoked result", true));
            await pending;
            check(shown.Count == before, "revoked_result_stays_suppressed_after_access_is_reenabled");
        }

        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new CodexNoticePresenter((_, _, _, token) =>
        { oldToken = token; return release.Task; }, (notice, presentation) => shown.Add((notice, presentation)));
        int prior = shown.Count;
        Task inFlight = disposed.ShowAsync(scheduled, "fixture-model", address, () => true);
        disposed.Dispose();
        release.SetResult(new("disposed result", true));
        await inFlight;
        check(oldToken.IsCancellationRequested && shown.Count == prior,
            "application_exit_cancels_summary_and_prevents_late_bubble_or_speech");
    }

    private static T Field<T>(PetWindow pet, string name) =>
        (T)(typeof(PetWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(pet)
            ?? throw new InvalidOperationException("missing_test_field_" + name));

    private static async Task Settle(FrameworkElement element)
    {
        await element.Dispatcher.InvokeAsync(element.UpdateLayout, DispatcherPriority.Render);
        await Task.Delay(35);
        element.UpdateLayout();
    }
}
