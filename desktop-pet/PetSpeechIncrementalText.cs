using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopPet;

// Keeps incomplete Markdown and hidden reasoning together until they can be
// safely normalized. Only visible answer deltas, never tool output, enter here.
internal sealed class PetSpeechIncrementalText
{
    private readonly StringBuilder _pending = new();
    private int _received;
    private bool _emitted;

    internal IReadOnlyList<string> Append(string? delta, bool final = false)
    {
        delta ??= "";
        if (_received + delta.Length > 16000) throw new InvalidOperationException("语音正文超过允许长度。");
        _received += delta.Length;
        _pending.Append(delta);
        var result = new List<string>();
        string text = _pending.ToString();
        int end = final ? text.Length : StableBoundary(text, _emitted ? 70 : 24);
        if (end <= 0) return result;
        string prefix = text[..end];
        if (final) prefix = Regex.Replace(prefix, @"<[^>]*$", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        prefix = Regex.Replace(prefix, @"(?s)~~~.*?(?:~~~|$)", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        string clean = PetSpeechText.Normalize(prefix);
        // Normalize removes a trailing Chinese full stop. Retain the sentence
        // boundary for speech prosody when more answer text will follow.
        if (clean.Length > 0 && prefix.TrimEnd().EndsWith('。')) clean += "。";
        _pending.Remove(0, end);
        if (clean.Length > 0) { _emitted = true; result.Add(clean); }
        return result;
    }

    private static int StableBoundary(string text, int minimum)
    {
        bool fenced = false, thinking = false, rawUrl = false;
        char fenceCharacter = '\0';
        int inlineTicks = 0, brackets = 0, parentheses = 0, boundary = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (rawUrl)
            {
                if (!char.IsWhiteSpace(ch) && !"。！？".Contains(ch)) continue;
                rawUrl = false;
            }
            if (!fenced && !thinking && inlineTicks == 0 && brackets == 0 && parentheses == 0 &&
                (text.AsSpan(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 text.AsSpan(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            { rawUrl = true; continue; }
            if (ch is '`' or '~')
            {
                int count = 1;
                while (i + count < text.Length && text[i + count] == ch) count++;
                // A delimiter can arrive across several token updates.
                if (i + count == text.Length) break;
                if (count >= 3 && inlineTicks == 0 && !thinking)
                {
                    if (!fenced) { fenced = true; fenceCharacter = ch; }
                    else if (ch == fenceCharacter) fenced = false;
                }
                else if (ch == '`' && !fenced && !thinking)
                {
                    if (inlineTicks == 0) inlineTicks = count;
                    else if (inlineTicks == count) inlineTicks = 0;
                }
                i += count - 1;
                continue;
            }
            if (fenced || inlineTicks > 0) continue;
            if (ch == '<')
            {
                int close = text.IndexOf('>', i + 1);
                if (close < 0) break;
                string tag = text.Substring(i, close - i + 1);
                if (tag.Equals("<think>", StringComparison.OrdinalIgnoreCase)) thinking = true;
                else if (tag.Equals("</think>", StringComparison.OrdinalIgnoreCase)) thinking = false;
                i = close;
                continue;
            }
            if (thinking) continue;
            if (ch == '[') brackets++;
            else if (ch == ']' && brackets > 0) brackets--;
            else if (ch == '(') parentheses++;
            else if (ch == ')' && parentheses > 0) parentheses--;
            if (brackets == 0 && parentheses == 0 && "。！？!?\n".Contains(ch))
            {
                // A single word/title is too short for a stable first voice
                // request. Full sentences/paragraphs give the model context.
                if (i + 1 >= minimum) boundary = i + 1;
                if (boundary >= 100) return boundary;
            }
        }
        return boundary;
    }
}
