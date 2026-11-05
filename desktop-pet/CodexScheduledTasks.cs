using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopPet;

internal sealed record CodexScheduledTask(string Id, string ThreadId);
internal sealed record CodexCompletedTurn(string Id, string Status, string? AutomationId, string Output);

// Automation files are read-only local metadata. No schedule prompt is retained,
// logged, sent to a model, or used to infer that an ordinary user turn is scheduled.
internal static class CodexScheduledTasks
{
    private const int MaximumFileBytes = 128 * 1024;
    private static readonly Regex Heartbeat = new(
        @"\A\s*<heartbeat>\s*<automation_id>([^<>\r\n]+)</automation_id>\s*<current_time_iso>([^<>\r\n]+)</current_time_iso>\s*<instructions>[\s\S]*</instructions>\s*</heartbeat>\s*\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<CodexScheduledTask> ReadLocal()
    {
        string? configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        string root = Path.Combine(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configured, "automations");
        return ReadDirectory(root);
    }

    internal static IReadOnlyList<CodexScheduledTask> ReadDirectory(string root)
    {
        var result = new List<CodexScheduledTask>();
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root).Take(256))
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    var file = new FileInfo(Path.Combine(directory, "automation.toml"));
                    if (!file.Exists || file.Length > MaximumFileBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    // Bound even if a file grows after the size check.
                    var buffer = new char[MaximumFileBytes + 1];
                    int count = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (count > MaximumFileBytes) continue;
                    var task = ParseMetadata(new string(buffer, 0, count));
                    if (task != null) result.Add(task);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return result;
    }

    internal static CodexScheduledTask? ParseMetadata(string text)
    {
        if (text.Length > MaximumFileBytes) return null;
        var values = ReadRootStrings(text);
        if (values == null || !values.TryGetValue("kind", out string? kind) || kind != "heartbeat" ||
            !values.TryGetValue("id", out string? id) || !ValidIdentifier(id) ||
            !values.TryGetValue("target_thread_id", out string? threadId) || !ValidIdentifier(threadId)) return null;
        return new CodexScheduledTask(id, threadId);
    }

    internal static bool IsBound(CodexTaskInfo task, IReadOnlyList<CodexScheduledTask> schedules) =>
        task.Kind == "codex" && task.Source == TaskSource.Codex && task.HostId is null or "local" &&
        schedules.Any(schedule => schedule.ThreadId == task.Id);

    internal static bool IsScheduled(CodexTaskInfo task, CodexCompletedTurn turn, IReadOnlyList<CodexScheduledTask> schedules) =>
        IsBound(task, schedules) && turn.AutomationId != null &&
        schedules.Any(schedule => schedule.ThreadId == task.Id && schedule.Id == turn.AutomationId);

    internal static CodexCompletedTurn? LatestTurn(JsonElement data)
    {
        var turns = CodexParser.Get(data, "turns");
        if (turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0) return null;
        var turn = turns[0];
        string? id = CodexParser.Text(turn, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        string? automationId = null;
        var outputs = new List<string>();
        bool outputTruncated = CodexParser.Bool(turn, "textTruncated") == true || CodexParser.Bool(turn, "truncated") == true;
        var items = CodexParser.Get(turn, "items");
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                string? type = CodexParser.Text(item, "type");
                if (type == "userMessage" && automationId == null)
                {
                    var content = CodexParser.Get(item, "content");
                    if (content.ValueKind != JsonValueKind.Array) continue;
                    string text = string.Join("\n", content.EnumerateArray()
                        .Where(part => CodexParser.Text(part, "type") == "text")
                        .Select(part => CodexParser.Text(part, "text") ?? ""));
                    // Only the native scheduled-turn envelope is recognized.
                    // Mentioning a schedule, a matching title, or an assistant
                    // quoting a heartbeat cannot turn a manual task into one.
                    if (text.Length <= MaximumFileBytes)
                    {
                        try
                        {
                            var match = Heartbeat.Match(text);
                            if (match.Success && ValidIdentifier(match.Groups[1].Value) &&
                                DateTimeOffset.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                                automationId = match.Groups[1].Value;
                        }
                        catch (RegexMatchTimeoutException) { }
                    }
                }
                else if (type == "agentMessage" && CodexParser.Text(item, "phase") is "final_answer" or "final")
                {
                    outputTruncated |= CodexParser.Bool(item, "textTruncated") == true || CodexParser.Bool(item, "truncated") == true;
                    string? output = CodexParser.Text(item, "text");
                    if (!string.IsNullOrWhiteSpace(output)) outputs.Add(output.Trim());
                }
            }
        }
        // A truncated excerpt must not be passed off as the complete short result.
        return new CodexCompletedTurn(id, CodexParser.Status(turn), automationId, outputTruncated ? "" : string.Join("\n\n", outputs));
    }

    private static bool ValidIdentifier(string value) => value.Length is > 0 and <= 256 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    // A deliberately narrow, bounded TOML reader for top-level string metadata.
    // It understands all four string forms so text inside a multiline prompt
    // cannot masquerade as metadata. Unknown values are never retained.
    private static Dictionary<string, string>? ReadRootStrings(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position >= text.Length || text[position] == '[') break;
            if (text[position] == '#') { SkipLine(text, ref position); continue; }
            int start = position;
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] is '_' or '-')) position++;
            if (position == start) return null;
            string key = text[start..position];
            while (position < text.Length && text[position] is ' ' or '\t') position++;
            if (position >= text.Length || text[position++] != '=') return null;
            while (position < text.Length && text[position] is ' ' or '\t') position++;
            if (position >= text.Length) return null;
            if (text[position] is not ('\'' or '"')) { SkipLine(text, ref position); continue; }
            bool keep = key is "id" or "kind" or "target_thread_id";
            string? value = ReadString(text, ref position, keep);
            if (value == null) return null;
            if (keep && !result.TryAdd(key, value)) return null;
            while (position < text.Length && text[position] is ' ' or '\t') position++;
            if (position < text.Length && text[position] == '#') SkipLine(text, ref position);
            else if (position < text.Length && text[position] is not ('\r' or '\n')) return null;
        }
        return result;
    }

    private static void SkipLine(string text, ref int position)
    {
        while (position < text.Length && text[position] != '\n') position++;
    }

    private static string? ReadString(string text, ref int position, bool keep)
    {
        char quote = text[position++];
        bool multiline = position + 1 < text.Length && text[position] == quote && text[position + 1] == quote;
        if (multiline)
        {
            position += 2;
            if (position < text.Length && text[position] == '\r') position++;
            if (position < text.Length && text[position] == '\n') position++;
        }
        var value = keep ? new StringBuilder() : null;
        while (position < text.Length)
        {
            char ch = text[position++];
            if (ch == quote)
            {
                if (!multiline) return value?.ToString() ?? "";
                int count = 1;
                while (position < text.Length && text[position] == quote) { count++; position++; }
                if (count >= 3)
                {
                    if (count > 5) return null;
                    value?.Append(quote, count - 3);
                    return value?.ToString() ?? "";
                }
                value?.Append(quote, count); continue;
            }
            if (!multiline && ch is '\r' or '\n') return null;
            if (ch != '\\' || quote == '\'') { value?.Append(ch); continue; }
            if (position >= text.Length) return null;
            char escaped = text[position++];
            if (multiline && escaped is ' ' or '\t' or '\r' or '\n')
            {
                bool newline = escaped is '\r' or '\n';
                while (position < text.Length && char.IsWhiteSpace(text[position]))
                { newline |= text[position] is '\r' or '\n'; position++; }
                if (!newline) return null;
                continue;
            }
            if (escaped is 'u' or 'U')
            {
                int digits = escaped == 'u' ? 4 : 8;
                if (position + digits > text.Length || !int.TryParse(text.AsSpan(position, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int scalar) ||
                    scalar < 0 || scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff) return null;
                position += digits; value?.Append(char.ConvertFromUtf32(scalar)); continue;
            }
            char decoded = escaped switch { 'b' => '\b', 't' => '\t', 'n' => '\n', 'f' => '\f', 'r' => '\r', '"' => '"', '\\' => '\\', _ => '\0' };
            if (decoded == '\0') return null;
            value?.Append(decoded);
        }
        return null;
    }
}
