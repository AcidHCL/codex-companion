using System;
using System.Collections.Generic;

namespace DesktopPet;

internal static class PetWebAnswerContextChecks
{
    public static object Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Web answer context check failed: " + name);
            checks.Add(name);
        }

        const string failedAnswer = "去搜了一下，Ithaca 今天最高 14°C，最低 7°C。[1]";
        const string failedWarning = "搜索未取得可用资料，实时信息尚未核实。";
        var empty = Array.Empty<PetWebSource>();
        var sources = new[] { new PetWebSource("合成天气来源", "https://example.com/weather", "TRANSIENT_PAGE_BODY") };
        string failed = PetWebAnswerContext.Remember(failedAnswer, empty, failedWarning);
        Check(!failed.Contains("14", StringComparison.Ordinal) && !failed.Contains("7", StringComparison.Ordinal) && !failed.Contains("[1]", StringComparison.Ordinal), "failed_history_omits_old_weather_values_and_fabricated_citation");
        Check(failed.Contains("旧答复未经核实", StringComparison.Ordinal) && failed.Contains(failedWarning, StringComparison.Ordinal), "failed_history_preserves_failure_context");

        string success = PetWebAnswerContext.Remember("当时天气晴朗。[1]", sources, null);
        Check(success.Contains("当时天气晴朗。[1]", StringComparison.Ordinal) && success.Contains(sources[0].Url, StringComparison.Ordinal), "successful_history_keeps_answer_and_source_link");
        Check(success.Contains("不是本轮检索结果", StringComparison.Ordinal) && success.Contains("不能据此认定当前实时事实", StringComparison.Ordinal) && success.Contains("编号仅在该历史轮次有效", StringComparison.Ordinal), "successful_history_marks_source_scope_and_time_limit");
        Check(!success.Contains("TRANSIENT_PAGE_BODY", StringComparison.Ordinal), "history_does_not_replay_transient_page_body");

        const string partialWarning = "部分查询未成功，请结合已取得的来源核对。";
        string partial = PetWebAnswerContext.Remember("公开资料摘要。", sources, partialWarning);
        Check(partial.Contains("公开资料摘要。", StringComparison.Ordinal) && partial.Contains("当时联网提示：" + partialWarning, StringComparison.Ordinal), "partial_success_history_retains_answer_and_warning");
        const string offline = "今天一起读一本书吧。";
        Check(PetWebAnswerContext.Remember(offline, empty, null) == offline && PetWebAnswerContext.Remember(offline, empty, " ") == offline, "ordinary_offline_history_is_unchanged");

        IReadOnlyList<PetWebSource> currentSources = empty;
        string? currentWarning = null;
        PetWebAnswerContext.ApplyMetadata(new(Warning: failedWarning), ref currentSources, ref currentWarning);
        PetWebAnswerContext.ApplyMetadata(new(Content: "中间片段"), ref currentSources, ref currentWarning);
        Check(currentWarning == failedWarning, "intermediate_omitted_warning_keeps_current_status");
        PetWebAnswerContext.ApplyMetadata(new(Done: true, Sources: sources), ref currentSources, ref currentWarning);
        Check(currentSources.Count == 1 && currentWarning == null, "final_success_clears_intermediate_failure_warning");
        PetWebAnswerContext.ApplyMetadata(new(Done: true, Warning: partialWarning), ref currentSources, ref currentWarning);
        Check(currentSources.Count == 1 && currentWarning == partialWarning, "final_omitted_sources_preserves_legacy_source_metadata");
        PetWebAnswerContext.ApplyMetadata(new(Done: true, Sources: empty, Warning: failedWarning), ref currentSources, ref currentWarning);
        Check(currentSources.Count == 0 && currentWarning == failedWarning, "final_empty_sources_overrides_earlier_results");
        return new { passed = true, assertions = checks.Count, checks };
    }
}
