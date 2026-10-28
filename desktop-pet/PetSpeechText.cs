using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopPet;

internal static class PetSpeechText
{
    private static string Replace(string text, string pattern, string replacement) =>
        Regex.Replace(text, pattern, replacement, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Callers supply visible prose or a stable segment of the visible answer.
        string fence = new((char)96, 3);
        text = Replace(text, "(?s)" + fence + ".*?(?:" + fence + "|$)", " ");
        text = Replace(text, @"(?is)<think>.*?(?:</think>|$)", " ");
        text = Replace(text, @"!\[[^\]]*\]\([^)]*\)", " ");
        text = Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Replace(text, @"(?i)https?://[^\s<>。！？]+", " ");
        text = Replace(text, @"\[\d+(?:[,，\-]\d+)*\]", "");
        text = Replace(text, @"(?m)^\s{0,4}(?:#{1,6}\s+|[-*+>]\s+|\d+[.)]\s+)", "");
        text = Replace(text, @"<[^>]+>", "");
        text = text.Replace(new string((char)96, 1), "").Replace("**", "").Replace("__", "").Replace("|", "，");
        text = WebUtility.HtmlDecode(text);
        text = Replace(text, @"[\r\n]+", "。");
        text = Replace(text, @"[ \t\u00a0]+", " ");
        text = Replace(text, @"[。]{2,}", "。");
        return text.Trim(' ', '。', '\t', '\r', '\n');
    }

    internal static IReadOnlyList<string> Split(string text, int maximum = 100)
    {
        if (maximum < 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        var chunks = new List<string>();
        var part = new StringBuilder();
        int lastBoundary = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            part.Append(rune.ToString());
            if (rune.Value <= char.MaxValue && "。！？!?；;\n，,：: ".Contains((char)rune.Value)) lastBoundary = part.Length;
            // Keep adjacent short sentences together. A separate inference for
            // every sentence adds startup overhead and resets the voice prosody.
            if (part.Length >= maximum)
            {
                int end = lastBoundary >= maximum / 2 ? lastBoundary : part.Length;
                string chunk = part.ToString(0, end).Trim();
                if (chunk.Length > 0) chunks.Add(chunk);
                part.Remove(0, end);
                lastBoundary = 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(part.ToString())) chunks.Add(part.ToString().Trim());
        return chunks;
    }
}
