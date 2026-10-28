using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopPet;

internal static class PetStreamingTextChecks
{
    internal static int Run()
    {
        var checks = new List<string>(); string? error = null;
        void Require(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        static string Parse(string value, int block)
        {
            var parser = new PetSpeechIncrementalText(); var outputs = new List<string>();
            for (int i = 0; i < value.Length; i += block) outputs.AddRange(parser.Append(value.Substring(i, Math.Min(block, value.Length - i))));
            outputs.AddRange(parser.Append(null, true));
            return string.Concat(outputs);
        }
        try
        {
            const string first = "哼，本鲸已经把今天的任务处理完成啦，你可以先检查一下结果。";
            const string last = "接下来我会陪你继续完成剩余工作，然后一起吃白米饭！";
            var parser = new PetSpeechIncrementalText();
            Require(string.Concat(parser.Append(first)) == first, "complete_first_sentence_is_available_before_full_answer");
            Require(parser.Append(last[..5]).Count == 0, "incomplete_next_sentence_is_not_spoken");
            string rest = string.Concat(parser.Append(last[5..])) + string.Concat(parser.Append(null, true));
            Require(rest == last, "tail_is_flushed_once_without_repeating_first_sentence");
            foreach (int block in new[] { 1, 2, 7, 1000 })
            {
                string result = Parse(first + last, block);
                Require(result == first + last, "arbitrary_model_token_boundaries_preserve_prose_" + block);
                result = Parse("<think>这里是不能朗读的私密推理。\n我还在思考。</think>" + first, block);
                Require(result == first, "split_think_tags_never_leak_reasoning_" + block);
                result = Parse("```python\nprint('不能朗读的代码。')\n```\n" + first, block);
                Require(PetSpeechText.Normalize(result) == PetSpeechText.Normalize(first), "split_backtick_fences_stay_silent_" + block);
                result = Parse("~~~python\nprint('另一段不能朗读的代码。')\n~~~\n" + first, block);
                Require(PetSpeechText.Normalize(result) == PetSpeechText.Normalize(first), "tilde_fences_stay_silent_" + block);
                result = Parse("参考[天气资料](https://example.com/private?a=1)。[1]\n" + first, block);
                Require(!result.Contains("http") && !result.Contains("private") && !result.Contains("[1]") && result.Contains("天气资料"), "markdown_links_and_citations_are_normalized_across_deltas_" + block);
                result = Parse("这里附上本轮使用的网页来源地址供你之后打开查看 https://example.com/longpath?private=secret!hidden\n" + first, block);
                Require(!result.Contains("private") && !result.Contains("secret") && !result.Contains("hidden") && !result.Contains("example"), "url_query_punctuation_cannot_turn_remaining_url_into_spoken_prose_" + block);
                result = Parse("网页 HTTPS://example.com/private?q=secret!hidden。" + first, block);
                Require(!result.Contains("HTTPS") && !result.Contains("secret") && !result.Contains("hidden") && result.Contains(first), "uppercase_url_is_silent_and_adjacent_chinese_prose_is_preserved_" + block);
            }
            Require(Parse(first + "<think>尚未闭合的推理。", 1) == first, "unterminated_think_is_not_read_on_completion");
            Require(Parse(first + "```\n尚未闭合的代码。", 1) == first, "unterminated_code_is_not_read_on_completion");
            Require(Parse(first + "<thi", 1) == first, "incomplete_html_tag_is_not_spoken_on_completion");
            parser = new(); parser.Append(new string('鲸', 16000));
            bool bounded = false; try { parser.Append("鲸"); } catch (InvalidOperationException) { bounded = true; }
            Require(bounded, "stream_text_memory_is_bounded");
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "streaming-text-validation.json"), JsonSerializer.Serialize(new
        { passed = error == null, assertions = checks.Count, checks, error, realModelRequests = 0, userSettingsWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;
    }
}
