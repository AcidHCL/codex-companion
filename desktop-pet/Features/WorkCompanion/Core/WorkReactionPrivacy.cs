using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopPet.WorkCompanion;

internal static class WorkReactionPrivacy
{
    private static readonly Regex Identifier = new(
        @"(?:https?://|www\.|[a-z]:[\\/]|\\\\|(?:^|\s)/(?:[^\s/]+/)+|[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b(?:\d{1,3}\.){3}\d{1,3}\b|[0-9]{5,}|[a-z0-9_-]{24,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Quotation = new(@"[""“‘「『`].+?[""”’」』`]|(?:^|\s)'[^']{2,}'", RegexOptions.CultureInvariant);

    // Treat model fields as untrusted even after asking for a paraphrase.
    // Check before delivery so the spoken line and its eventual transcript agree.
    internal static bool CanShare(string value, string? title, string? windowText = null)
    {
        if (SharedMemoryContext.ContainsLikelySecret(value) || Identifier.IsMatch(value) || Quotation.IsMatch(value)) return false;
        string normalized = Normalize(value);
        return !Echoes(normalized, title) && !Echoes(normalized, windowText);
    }

    private static bool Echoes(string line, string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || line.Length == 0) return false;
        foreach (string item in source.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string original = Normalize(item);
            if (original.Length == 0) continue;
            if (line == original) return true;
            // Exact short titles/labels and longer excerpts embedded in a sentence.
            int fullLabel = original.Any(c => c is >= '\u3400' and <= '\u9fff') ? 4 : 8;
            if (original.Length >= fullLabel && line.Contains(original, StringComparison.Ordinal)) return true;
            for (int i = 0; i < line.Length; i++)
            {
                int span = line[i] is >= '\u3400' and <= '\u9fff' ? 8 : 18;
                if (i + span <= line.Length && original.AsSpan().Contains(line.AsSpan(i, span), StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static string Normalize(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    internal static string EventSummary(string? proposed, WorkReactionCandidate candidate, bool english,
        string? windowText = null, bool screen = false)
    {
        string summary = proposed?.Trim() ?? "";
        if (summary.Length > 0 && summary.EnumerateRunes().Count() <= (english ? 160 : 100) &&
            !summary.Any(char.IsControl) && CanShare(summary, candidate.Title, windowText))
            return summary;
        // Fallbacks depend only on host-defined triggers, never on captured data.
        return (candidate.Kind, screen) switch
        {
            (_, true) => english ? "The pet responded to activity in the foreground window." : "桌宠回应了当前窗口中的活动。",
            ("app_changed", _) => english ? "The foreground application changed." : "用户切换了前台应用。",
            ("codex_completed", _) => english ? "Codex reported that a task turn finished." : "Codex 报告一轮任务已完成。",
            ("codex_failed" or "codex_fail", _) => english ? "Codex reported a task error." : "Codex 报告任务出现错误。",
            ("codex_attention", _) => english ? "A Codex task requested attention." : "Codex 有一项任务需要关注。",
            _ => english ? "The pet offered a break after a period of activity." : "活跃使用一段时间后，桌宠提醒可以休息。"
        };
    }
}
