using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.WorkCompanion;

namespace DesktopPet.ScreenUnderstanding;

internal static class ScreenUnderstandingModuleChecks
{
    // No WPF application, desktop, network, model runner, actual capture or
    // production memory is used. Each fixture injects all boundary services.
    internal static async Task<object> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool ok, string name)
        { if (!ok) throw new InvalidOperationException("Screen module: " + name); checks.Add(name); }

        using (var fixture = new Fixture(new ScreenUnderstandingOptions()))
        {
            var reply = await fixture.Generate(fixture.Candidate());
            Check(reply?.Text == "fallback" && fixture.Fallbacks == 1 && fixture.Capture.Requests.Count == 0 && fixture.ReadinessCalls == 0,
                "disabled_layer_uses_metadata_fallback_without_model_preflight_or_capture");
            Check(fixture.Module.RequestContext(fixture.Observation()) == null, "disabled_layer_never_requests_initial_or_periodic_analysis");
        }
        using (var fixture = new Fixture(remote: true))
        {
            Check(await fixture.Generate(fixture.Candidate()) == null && fixture.Capture.Requests.Count == 0 && fixture.ReadinessCalls == 0,
                "remote_endpoint_is_blocked_before_any_capture_or_model_request");
            fixture.Module.UpdateOptions(fixture.Module.Options with { AllowRemoteEndpoint = true });
            Check(await fixture.Generate(fixture.Candidate()) != null && fixture.Capture.Requests.Count == 1,
                "explicit_remote_opt_in_allows_the_same_bounded_pipeline");
        }
        using (var fixture = new Fixture())
        {
            fixture.Readiness = new(false, false, false, "not_resident");
            Check(await fixture.Generate(fixture.Candidate()) == null && fixture.ReadinessCalls == 1 && fixture.Capture.Requests.Count == 0,
                "cold_model_is_checked_before_reading_any_window_content");
            Check(fixture.Module.RequestContext(fixture.Observation()) == null,
                "cold_model_attempt_is_rate_limited_instead_of_repeatedly_capturing");
        }
        using (var fixture = new Fixture(new ScreenUnderstandingOptions { Enabled = true, ReadWindowText = false }))
        {
            fixture.Readiness = new(true, true, false, "resident");
            Check(await fixture.Generate(fixture.Candidate()) == null && fixture.Capture.Requests.Count == 0,
                "image_only_setting_with_nonvision_model_reads_nothing");
        }
        using (var fixture = new Fixture())
        {
            fixture.Readiness = new(true, true, false, "resident");
            await fixture.Generate(fixture.Candidate());
            Check(fixture.Capture.Requests.Count == 1 && fixture.Capture.Requests[0].IncludeText && !fixture.Capture.Requests[0].IncludeImage &&
                fixture.AnalyzedCapture?.ImageBase64 == null, "nonvision_model_can_use_enabled_text_without_attempting_a_screenshot");
        }
        using (var fixture = new Fixture())
        {
            await fixture.Generate(fixture.Candidate("code", WorkActivity.Work));
            Check(fixture.Capture.Requests.Count == 1 && fixture.Capture.Requests[0] is { IncludeText: true, IncludeImage: false },
                "known_work_app_with_enough_accessible_text_does_not_need_an_image");
        }
        using (var fixture = new Fixture())
        {
            fixture.Capture.Text = "short text";
            await fixture.Generate(fixture.Candidate("code", WorkActivity.Work));
            Check(fixture.Capture.Requests.Count == 2 && !fixture.Capture.Requests[0].IncludeImage && fixture.Capture.Requests[1].IncludeImage,
                "sparse_accessible_text_can_fall_back_to_one_window_image");
        }
        using (var fixture = new Fixture())
        {
            fixture.Capture.Text = "short text"; fixture.Capture.DropImage = true;
            await fixture.Generate(fixture.Candidate());
            Check(fixture.Capture.Requests.Count == 1 && fixture.Capture.Requests[0].IncludeImage,
                "already_attempted_unavailable_image_is_not_captured_a_second_time");
        }
        using (var fixture = new Fixture())
        {
            fixture.Capture.Text = ""; fixture.Capture.FirstFailure = "content_unavailable";
            var reply = await fixture.Generate(fixture.Candidate("code", WorkActivity.Work));
            Check(reply != null && fixture.Capture.Requests.Count == 2 &&
                !fixture.Capture.Requests[0].IncludeImage && fixture.Capture.Requests[1].IncludeImage &&
                fixture.AnalyzedCapture is { Text.Length: 0, ImageBase64: not null },
                "safe_but_empty_window_text_falls_back_to_one_image_for_a_loaded_vision_model");
        }
        foreach (string status in new[] { "protected_content", "sensitive_content", "password", "timeout", "foreground_changed" })
        {
            using var fixture = new Fixture();
            fixture.Capture.FirstFailure = status;
            Check(await fixture.Generate(fixture.Candidate("code", WorkActivity.Work)) == null &&
                fixture.Capture.Requests.Count == 1 && !fixture.Capture.Requests[0].IncludeImage && fixture.AnalysisCalls == 0,
                "text_read_rejection_never_falls_back_to_screenshot_" + status);
        }
        foreach (bool vision in new[] { false, true })
        {
            using var fixture = new Fixture(new ScreenUnderstandingOptions { Enabled = true, UseScreenshots = !vision });
            fixture.Readiness = fixture.Readiness with { SupportsImages = vision };
            fixture.Capture.FirstFailure = "content_unavailable";
            Check(await fixture.Generate(fixture.Candidate("code", WorkActivity.Work)) == null && fixture.Capture.Requests.Count == 1 &&
                fixture.AnalysisCalls == 0,
                "empty_text_fallback_respects_" + (vision ? "screenshot_setting_off" : "nonvision_model"));
        }
        using (var fixture = new Fixture())
        {
            var candidate = fixture.Candidate();
            var reply = await fixture.Generate(candidate);
            Check(reply?.Text == fixture.Analysis.Text && fixture.Capture.Requests.Count == 1 && fixture.Capture.Requests[0].IncludeImage,
                "unfamiliar_app_requests_a_bounded_image_when_vision_is_available");
            Check(fixture.Memory.List().Count == 0 && fixture.Module.Status.LastSummary == null && !File.Exists(fixture.MemoryPath),
                "generation_alone_cannot_persist_memory_or_publish_a_screen_summary");
            fixture.Module.FinishCandidate(candidate, accepted: false);
            fixture.Module.FinishCandidate(candidate, accepted: true);
            Check(fixture.Memory.List().Count == 0 && fixture.Module.Status.LastSummary == null,
                "discarded_candidate_cannot_be_accepted_later_to_teach_memory");
        }
        using (var fixture = new Fixture())
        {
            var candidate = fixture.Candidate();
            await fixture.Generate(candidate); fixture.Module.FinishCandidate(candidate, true);
            Check(fixture.Memory.GetByIdentity("mygame", fixture.Capture.Identity) is { Activity: "game", Confidence: .94 } &&
                fixture.Module.Status.LastSummary == fixture.Analysis.Summary,
                "freshly_accepted_high_confidence_reusable_app_result_saves_only_classification");
            Check(!File.ReadAllText(fixture.MemoryPath).Contains(fixture.Analysis.Summary, StringComparison.Ordinal) &&
                !File.ReadAllText(fixture.MemoryPath).Contains(fixture.Capture.Text, StringComparison.Ordinal),
                "screen_summary_and_visible_text_are_absent_from_persistent_app_memory");
            Check(await fixture.Module.RecallActivityAsync(fixture.Observation(), CancellationToken.None) == WorkActivity.Game &&
                fixture.Capture.IdentityReads == 1, "remembered_app_category_requires_exact_executable_identity");
            fixture.Capture.Identity = new string('b', 64);
            Check(await fixture.Module.RecallActivityAsync(fixture.Observation(windowKey: "123:DEF"), CancellationToken.None) == null,
                "same_app_basename_with_another_identity_does_not_reuse_old_memory");
            fixture.Capture.Identity = new string('c', 64);
            fixture.Memory.SaveLearned("mygame", fixture.Capture.Identity, "work", .95);
            Check(await fixture.Module.RecallActivityAsync(fixture.Observation(), CancellationToken.None) == WorkActivity.Work,
                "ambiguous_basename_is_disambiguated_by_exact_executable_identity");
            fixture.Capture.Identity = new string('d', 64);
            Check(await fixture.Module.RecallActivityAsync(fixture.Observation(), CancellationToken.None) == null,
                "reused_window_key_does_not_reuse_a_previous_executable_identity");
        }
        foreach (string app in new[] { "chrome", "javaw", "steam", "mstsc" })
        {
            using var fixture = new Fixture();
            var candidate = fixture.Candidate(app);
            await fixture.Generate(candidate); fixture.Module.FinishCandidate(candidate, true);
            Check(fixture.Memory.List().Count == 0 && fixture.Module.Status.LastSummary != null,
                "one_screen_does_not_permanently_classify_a_multipurpose_app_" + app);
        }
        foreach (string reason in new[] { "low_confidence", "not_reusable", "unknown_category", "learning_off", "manual_override" })
        {
            using var fixture = new Fixture();
            switch (reason)
            {
                case "low_confidence": fixture.Analysis = fixture.Analysis with { Confidence = .84 }; break;
                case "not_reusable": fixture.Analysis = fixture.Analysis with { ReusableApp = false }; break;
                case "unknown_category": fixture.Analysis = fixture.Analysis with { Activity = null, ReusableApp = false }; break;
                case "learning_off": fixture.Module.UpdateOptions(fixture.Module.Options with { LearnApplications = false }); break;
                case "manual_override": fixture.Module.Configure(new WorkCompanionOptions { Enabled = true,
                    AppActivities = new Dictionary<string, string> { ["mygame"] = "daily" } }, false); break;
            }
            var candidate = fixture.Candidate();
            await fixture.Generate(candidate); fixture.Module.FinishCandidate(candidate, true);
            Check(fixture.Memory.List().Count == 0, "accepted_screen_result_is_not_saved_as_app_memory_when_" + reason);
        }
        using (var fixture = new Fixture())
        {
            fixture.Analysis = fixture.Analysis with { Text = "", Expression = null };
            var candidate = fixture.Candidate(); var reply = await fixture.Generate(candidate);
            Check(reply is { ContextOnly: true, Text.Length: 0 }, "classification_without_remark_can_be_accepted_without_a_bubble");
            fixture.Module.FinishCandidate(candidate, true);
            Check(fixture.Memory.List().Count == 1, "silent_understanding_can_still_remember_a_valid_app_category");
        }
        using (var fixture = new Fixture())
        {
            string? first = fixture.Module.RequestContext(fixture.Observation());
            Check(first?.StartsWith("context:", StringComparison.Ordinal) == true, "enabled_layer_can_request_an_initial_stable_window_opportunity");
            await fixture.Generate(fixture.Candidate());
            Check(fixture.Module.RequestContext(fixture.Observation()) == null, "automatic_analysis_obeys_its_minimum_interval");
            fixture.Module.RequestNextWindowAnalysis();
            Check(fixture.Module.RequestContext(fixture.Observation(own: true)) == null &&
                fixture.Module.RequestContext(fixture.Observation(fullscreen: true)) == null,
                "manual_request_waits_for_a_nonself_nonquiet_fullscreen_target");
            string? request = fixture.Module.RequestContext(fixture.Observation());
            var candidate = fixture.Candidate() with { ContextRequestKey = request };
            Check(request?.StartsWith("manual:", StringComparison.Ordinal) == true && fixture.Module.IsExplicitRequest(candidate),
                "manual_request_receives_a_distinct_explicit_context_key");
            int captures = fixture.Capture.Requests.Count;
            await fixture.Generate(candidate);
            Check(fixture.Capture.Requests.Count == captures + 1 && !fixture.Module.IsExplicitRequest(candidate),
                "one_explicit_request_bypasses_automatic_cooldown_and_is_consumed_once");
            fixture.Now = fixture.Now.AddMinutes(5);
            Check(fixture.Module.RequestContext(fixture.Observation()) != null, "automatic_opportunity_returns_after_the_configured_interval");
        }
        using (var fixture = new Fixture())
        {
            fixture.Capture.WrongWindow = true;
            Check(await fixture.Generate(fixture.Candidate()) == null && fixture.AnalysisCalls == 0,
                "capture_from_a_changed_window_is_never_sent_to_the_model");
        }
        using (var fixture = new Fixture())
        {
            fixture.Capture.Failure = "sensitive_content";
            Check(await fixture.Generate(fixture.Candidate()) == null && fixture.AnalysisCalls == 0,
                "sensitive_or_unavailable_capture_never_reaches_analysis_or_memory");
        }
        using (var fixture = new Fixture())
        {
            var blocked = new TaskCompletionSource<ScreenAnalysis?>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Analyze = (_, ct) => { fixture.AnalysisToken = ct; return blocked.Task; };
            var candidate = fixture.Candidate(); Task<WorkReactionReply?> generating = fixture.Generate(candidate);
            Check(!generating.IsCompleted && fixture.AnalysisCalls == 1, "in_flight_model_analysis_can_be_cancelled_independently");
            fixture.Module.UpdateOptions(fixture.Module.Options with { Enabled = false });
            var disabledStatus = fixture.Module.Status;
            Check(fixture.AnalysisToken.IsCancellationRequested, "disabling_screen_understanding_cancels_the_current_model_operation");
            blocked.SetResult(fixture.Analysis);
            bool canceled = false;
            try { await generating; } catch (OperationCanceledException) { canceled = true; }
            fixture.Module.FinishCandidate(candidate, true);
            Check(canceled && fixture.Memory.List().Count == 0 && fixture.Module.Status.LastSummary == null,
                "late_model_output_after_settings_change_never_enters_app_memory_or_summary");
            Check(fixture.Module.Status == disabledStatus,
                "late_cancelled_operation_cannot_overwrite_the_new_disabled_status");
        }
        using (var fixture = new Fixture())
        {
            var candidate = fixture.Candidate(); await fixture.Generate(candidate);
            fixture.Module.ClearTransientState(); fixture.Module.FinishCandidate(candidate, true);
            Check(fixture.Memory.List().Count == 0 && fixture.Module.Status.LastSummary == null,
                "clearing_transient_state_revokes_unaccepted_model_results");
            fixture.SaveFails = true;
            bool failed = false;
            try { fixture.Module.UpdateOptions(fixture.Module.Options with { Enabled = false }); }
            catch (IOException) { failed = true; }
            Check(failed && fixture.Module.Options.Enabled, "failed_settings_save_does_not_change_running_screen_preferences");
        }
        return new { passed = true, assertions = checks.Count, checks, realModelRequests = 0, actualScreenshots = 0, userWindowsRead = 0, productionMemoryAccess = false };
    }

    private sealed class Fixture : IDisposable
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal readonly string DirectoryPath;
        internal string MemoryPath => Path.Combine(DirectoryPath, "apps.json");
        internal LearnedApplicationStore Memory { get; }
        internal readonly CaptureFixture Capture = new();
        internal readonly NoNetworkHandler Handler = new();
        internal readonly OllamaClient Client;
        internal ScreenUnderstandingModule Module { get; }
        internal ScreenModelReadiness Readiness = new(true, true, true, "resident") { ContextLength = 8192 };
        internal ScreenAnalysis Analysis = new(WorkActivity.Game, .94, true, "Synthetic game menu is visible.", "A short companion remark.", "happy");
        internal Func<ScreenCaptureResult, CancellationToken, Task<ScreenAnalysis?>>? Analyze;
        internal ScreenCaptureResult? AnalyzedCapture;
        internal int ReadinessCalls, AnalysisCalls, Fallbacks;
        internal bool SaveFails;
        internal CancellationToken AnalysisToken;

        internal Fixture(ScreenUnderstandingOptions? options = null, bool remote = false)
        {
            DirectoryPath = Path.Combine(AppContext.BaseDirectory, "screen-module-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Memory = new LearnedApplicationStore(MemoryPath, () => Now);
            Client = new OllamaClient(new Uri(remote ? "http://192.0.2.7:11434/" : "http://127.0.0.1:11434/"), Handler);
            Module = new ScreenUnderstandingModule(Capture, options ?? new ScreenUnderstandingOptions { Enabled = true },
                _ => { if (SaveFails) throw new IOException("synthetic save failure"); }, Memory,
                new ScreenModelServices((_, _, ct) => { ct.ThrowIfCancellationRequested(); ReadinessCalls++; return Task.FromResult(Readiness); },
                    (_, _, _, _, capture, ct) => { AnalysisCalls++; AnalyzedCapture = capture; return Analyze?.Invoke(capture, ct) ?? Task.FromResult<ScreenAnalysis?>(Analysis); }), () => Now);
            Module.Configure(new WorkCompanionOptions { Enabled = true }, paused: false);
        }
        internal WorkReactionCandidate Candidate(string app = "mygame", WorkActivity activity = WorkActivity.Daily) =>
            new(Guid.NewGuid().ToString("N"), "context_requested", Now, Now.AddMinutes(1), app, null, null, 0)
            { Activity = activity, WindowKey = "123:ABC" };
        internal WorkObservation Observation(string app = "mygame", string windowKey = "123:ABC", bool own = false, bool fullscreen = false) =>
            new(Now, app, null, windowKey, TimeSpan.FromSeconds(6), true, fullscreen, own) { Activity = WorkActivity.Daily };
        internal Task<WorkReactionReply?> Generate(WorkReactionCandidate candidate) => Module.GenerateAsync(Client, "fixture-model", "deepseek", candidate,
            () => { Fallbacks++; return Task.FromResult<WorkReactionReply?>(new("fallback", null)); }, CancellationToken.None);
        public void Dispose()
        {
            Module.Dispose(); Client.Dispose();
            if (Handler.Calls != 0) throw new InvalidOperationException("Synthetic module unexpectedly invoked a network transport.");
            string directory = Path.GetFullPath(DirectoryPath);
            if (directory.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(directory).StartsWith("screen-module-fixture-", StringComparison.Ordinal) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class CaptureFixture : IScreenWindowCapture
    {
        private const string Image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6T6cAAAAASUVORK5CYII=";
        internal readonly List<ScreenCaptureRequest> Requests = new();
        internal string Text = new('x', 320);
        internal string Identity = new('a', 64);
        internal int IdentityReads;
        internal bool WrongWindow, DropImage;
        internal string? Failure, FirstFailure;
        public Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            string? failure = Requests.Count == 1 ? FirstFailure ?? Failure : Failure;
            return Task.FromResult(failure != null ? ScreenCaptureResult.Unavailable(failure) :
                new ScreenCaptureResult(true, "available", request.IncludeText ? Text : "", request.IncludeImage && !DropImage ? Image : null,
                    WrongWindow ? "456:DEF" : request.WindowKey, Identity));
        }
        public Task<string?> ReadIdentityAsync(string app, string windowKey, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); IdentityReads++; return Task.FromResult<string?>(Identity); }
    }
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromException<HttpResponseMessage>(new InvalidOperationException("No network expected in module fixtures.")); }
    }
}
