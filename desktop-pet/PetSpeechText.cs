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
        text = text.Replace(new string((char)96, 1), "").Replace("**", "").Replace("__", "").Replace("|", L.IsEnglish ? ", " : "，");
        text = WebUtility.HtmlDecode(text);
        if (L.IsEnglish)
        {
            // A paragraph ending in punctuation already supplies a pause.
            // Preserve that punctuation without inserting another full stop.
            text = text.Trim();
            text = Replace(text, @"(?<=[.!?;:。！？；：])[ \t]*[\r\n]+", " ");
            text = Replace(text, @"[\r\n]+", ". ");
        }
        else text = Replace(text, @"[\r\n]+", "。");
        text = Replace(text, @"[ \t\u00a0]+", " ");
        text = Replace(text, @"[。]{2,}", "。");
        return text.Trim(' ', '。', '\t', '\r', '\n');
    }

    internal static IReadOnlyList<string> Split(string text, int maximum = 100)
    {
        if (maximum < 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        var chunks = new List<string>();
        var part = new StringBuilder();
        int lastBoundary = 0, offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            part.Append(rune.ToString());
            if (rune.Value <= char.MaxValue && ("。！？!?；;\n，,：: ".Contains((char)rune.Value) ||
                rune.Value == '.' && IsSentencePeriod(text, offset))) lastBoundary = part.Length;
            offset += rune.Utf16SequenceLength;
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

    // A stream ending in a dot is incomplete: the next delta could be a
    // decimal fraction or an abbreviation. Require whitespace lookahead and
    // conservatively keep initials and common abbreviations with their text.
    internal static bool IsSentencePeriod(string text, int index)
    {
        if (index <= 0 || index + 1 >= text.Length || text[index] != '.' || !char.IsWhiteSpace(text[index + 1]) || text[index - 1] == '.') return false;
        int start = index - 1;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '.')) start--;
        string word = text[(start + 1)..index];
        if (word.Length == 0 || word.Length == 1 && char.IsLetter(word[0])) return false;
        bool numeric = true;
        foreach (char value in word) if (!char.IsDigit(value)) { numeric = false; break; }
        if (numeric) return false; // numbered lists and incomplete numeric tokens
        string lower = word.ToLowerInvariant();
        if (lower is "mr" or "mrs" or "ms" or "dr" or "prof" or "sr" or "jr" or "st" or "vs" or "etc" or
            "e.g" or "i.e" or "a.m" or "p.m" or "no" or "fig" or "inc" or "ltd" or "ph.d") return false;
        string[] initials = word.Split('.');
        if (initials.Length > 1)
        {
            bool allInitials = true;
            foreach (string initial in initials) if (initial.Length != 1 || !char.IsLetter(initial[0])) { allInitials = false; break; }
            if (allInitials) return false;
        }
        return true;
    }
}
