using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DesktopPet.WorkCompanion;

// Only fixed reason codes cross this boundary. Window titles, app names,
// prompts, screen text/images, model replies and exception messages never do.
internal static class CompanionDiagnostics
{
    private static CompanionDiagnosticSink? _sink;

    // The real feature composition root explicitly opts in. Synthetic checks
    // construct their own sink and must never touch the user's diagnostics.
    internal static void Initialize()
    {
        if (Volatile.Read(ref _sink) != null) return;
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) return;
            var sink = new CompanionDiagnosticSink(Path.Combine(local, "AIPet", "companion-diagnostics.jsonl"));
            Interlocked.CompareExchange(ref _sink, sink, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    internal static void Record(string layer, string code) => Volatile.Read(ref _sink)?.Record(layer, code);
    internal static void ModelRequestStarted() => Record("model", "model_request");
    internal static IReadOnlyList<CompanionDiagnosticEntry> ReadRecent(int count = 12) =>
        Volatile.Read(ref _sink)?.ReadRecent(count) ?? Array.Empty<CompanionDiagnosticEntry>();
}

internal sealed record CompanionDiagnosticEntry(DateTimeOffset Timestamp, string Layer, string Code,
    string LayerZh, string LayerEn, string ReasonZh, string ReasonEn)
{
    internal string Display(bool english) => english ? ReasonEn : ReasonZh;
}

internal sealed class CompanionDiagnosticSink
{
    internal const int MaximumFileBytes = 128 * 1024;
    internal const long RepeatIntervalMilliseconds = 60_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _previousPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long> _elapsed;
    private readonly Dictionary<(string Layer, string Code), long> _attempts = new();

    internal CompanionDiagnosticSink(string path, Func<DateTimeOffset>? now = null, Func<long>? elapsedMilliseconds = null)
    {
        _path = Path.GetFullPath(path);
        _previousPath = _path + ".1";
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _elapsed = elapsedMilliseconds ?? (() => Environment.TickCount64);
    }

    internal bool Record(string layer, string code)
    {
        if (!CompanionDiagnosticCatalog.TryCodes(layer, code, out string canonicalLayer)) return false;
        lock (_gate)
        {
            try
            {
                long elapsed = _elapsed();
                var key = (canonicalLayer, code);
                if (_attempts.TryGetValue(key, out long previous) && elapsed >= previous &&
                    elapsed - previous < RepeatIntervalMilliseconds) return false;
                // Rate-limit failed IO as well, so a read-only folder never
                // becomes a two-second disk retry loop on the observation path.
                _attempts[key] = elapsed;
                var row = new PersistedEntry(_now().ToUniversalTime(), canonicalLayer, code);
                byte[] bytes = Utf8.GetBytes(JsonSerializer.Serialize(row) + "\n");
                if (bytes.Length > MaximumFileBytes) return false;
                string? directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(directory)) return false;
                Directory.CreateDirectory(directory);
                if (File.Exists(_path))
                {
                    long length = new FileInfo(_path).Length;
                    if (length > MaximumFileBytes)
                    {
                        // A file modified outside this sink must not become an
                        // oversized archive; our private diagnostics start anew.
                        File.Delete(_path);
                    }
                    else if (length + bytes.Length > MaximumFileBytes)
                        File.Move(_path, _previousPath, overwrite: true);
                }
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }
    }

    internal IReadOnlyList<CompanionDiagnosticEntry> ReadRecent(int count = 12)
    {
        if (count <= 0) return Array.Empty<CompanionDiagnosticEntry>();
        count = Math.Min(count, 50);
        lock (_gate)
        {
            var recent = new Queue<CompanionDiagnosticEntry>(count);
            ReadFile(_previousPath, recent, count);
            ReadFile(_path, recent, count);
            return recent.ToArray();
        }
    }

    private static void ReadFile(string path, Queue<CompanionDiagnosticEntry> recent, int count)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) return;
            // A concurrently appended or externally changed file is bounded
            // during the actual read, not merely by its initial length.
            byte[] bytes = new byte[MaximumFileBytes + 1];
            int used = 0;
            while (used < bytes.Length)
            {
                int read = stream.Read(bytes, used, bytes.Length - used);
                if (read == 0) break;
                used += read;
            }
            if (used > MaximumFileBytes) return;
            string content = Utf8.GetString(bytes, 0, used);
            using var reader = new StringReader(content);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line.Length > 512) continue;
                try
                {
                    using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 2 });
                    var row = json.RootElement;
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    var fields = row.EnumerateObject().ToArray();
                    if (fields.Length != 3 || fields.Count(item => item.Name == "timestamp") != 1 ||
                        fields.Count(item => item.Name == "layer") != 1 || fields.Count(item => item.Name == "code") != 1 ||
                        !row.TryGetProperty("timestamp", out var at) || at.ValueKind != JsonValueKind.String ||
                        !at.TryGetDateTimeOffset(out DateTimeOffset timestamp) ||
                        !row.TryGetProperty("layer", out var layer) || layer.ValueKind != JsonValueKind.String ||
                        !row.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
                        !CompanionDiagnosticCatalog.TryEntry(timestamp, layer.GetString()!, code.GetString()!, out var entry)) continue;
                    recent.Enqueue(entry!);
                    while (recent.Count > count) recent.Dequeue();
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    // These three fields are the entire persisted schema. Display text comes
    // from the fixed bilingual catalog below and is never stored in the file.
    private sealed record PersistedEntry(
        [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
        [property: JsonPropertyName("layer")] string Layer,
        [property: JsonPropertyName("code")] string Code);
}

internal static class CompanionDiagnosticCatalog
{
    // Extend this source-controlled allowlist with another literal reason and
    // both translations. There is intentionally no runtime registration API.
    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Reasons =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["start"] = ("陪伴功能已启动，正在建立观察基线。", "Companion started and is establishing an observation baseline."),
            ["observing"] = ("正在观察桌面活动，等待合适的回应时机。", "Observing desktop activity and waiting for an appropriate moment."),
            ["candidate"] = ("发现了可以主动回应的活动变化。", "An activity change is eligible for a proactive reaction."),
            ["waiting_stable"] = ("当前窗口尚未稳定，继续等待。", "Waiting for the current window to remain stable."),
            ["host_busy"] = ("桌宠正在聊天或表演，暂缓主动回应。", "The pet is chatting or performing; proactive replies are deferred."),
            ["cooldown"] = ("仍在提醒间隔内，暂时保持安静。", "Still within the quiet interval between reactions."),
            ["model_busy"] = ("模型正在处理其他请求，暂缓主动回应。", "The model is handling another request; the reaction is deferred."),
            ["model_not_ready"] = ("聊天模型尚未就绪，无法生成主动回应。", "The chat model is not ready to generate a proactive reply."),
            ["model_request"] = ("正在请模型判断是否需要回应。", "Asking the model whether a reaction is useful."),
            ["model_silent"] = ("模型选择保持安静，没有生成需要展示的回应。", "The model chose to remain quiet; no reply needs to be shown."),
            ["reply_filtered"] = ("回应触发隐私过滤，已跳过展示。", "The reply was withheld by the privacy filter."),
            ["no_result"] = ("此次未取得可展示的回应。", "No displayable reaction was obtained this time."),
            ["pet_ui_open"] = ("桌宠界面正在打开，暂缓主动回应。", "The pet's interface is open; proactive reactions are deferred."),
            ["speech_busy"] = ("桌宠正在播报语音，等待播报结束。", "The pet is speaking; waiting for playback to finish."),
            ["interaction_busy"] = ("桌宠正在处理直接互动，暂缓主动回应。", "The pet is handling direct interaction; proactive reactions are deferred."),
            ["chat_busy"] = ("桌宠正在处理聊天请求，暂缓主动回应。", "The pet is handling a chat request; proactive reactions are deferred."),
            ["cancelled"] = ("本次回应已取消。", "This reaction was cancelled."),
            ["stale_result"] = ("桌面情境已变化，已丢弃过期回应。", "The desktop context changed, so the outdated reaction was discarded."),
            ["presented"] = ("主动回应已展示。", "The proactive reaction was shown."),
            ["chat_save_failed"] = ("发言已展示，但未能保存到对话。", "The remark was shown, but could not be saved to chat."),
            ["fullscreen"] = ("当前为全屏使用，按设置保持安静。", "Remaining quiet during fullscreen use, as configured."),
            ["input_active"] = ("你正在操作键盘或鼠标，等待输入暂停。", "Waiting for a pause in keyboard or mouse activity."),
            ["away"] = ("检测到离开或长时间空闲，暂缓主动回应。", "You appear to be away or idle; proactive reactions are deferred."),
            ["own_app"] = ("当前正在使用桌宠自己的界面，跳过观察。", "The pet's own interface is in use, so observation is skipped."),
            ["excluded"] = ("当前应用在排除列表中，跳过观察。", "The current app is excluded from observation."),
            ["observer_unavailable"] = ("暂时无法读取前台窗口信息。", "Foreground-window information is temporarily unavailable."),
            ["screen_capture"] = ("正在按需读取当前窗口的可见内容。", "Reading the current window's visible content on demand."),
            ["screen_capture_failed"] = ("此次窗口读取未成功，未向模型发送画面。", "Window capture failed; no image was sent to the model."),
            ["metadata_fallback"] = ("窗口内容不可用，改用应用切换或休息提醒来判断是否回应。", "Window content is unavailable. Considering a reply based on the app change or break reminder."),
            ["foreground_unavailable"] = ("无法确认目标窗口仍在前台且可见。", "Could not verify that the target window was still visible and in the foreground."),
            ["foreground_changed"] = ("读取期间目标窗口发生变化，此次内容已丢弃。", "The target window changed during capture. Its contents were discarded."),
            ["session_unavailable"] = ("当前 Windows 会话未连接或桌面已锁定。", "The Windows session is disconnected or the desktop is locked."),
            ["desktop_shell"] = ("当前目标是桌面或任务栏，已跳过读取。", "The target is the desktop or taskbar. Capture was skipped."),
            ["protected_window"] = ("该窗口已启用 Windows 窗口内容保护。", "Windows content protection is enabled for this window."),
            ["window_size_unavailable"] = ("窗口尺寸超出可读取范围。", "The window dimensions are outside the supported capture range."),
            ["identity_unavailable"] = ("无法核实前台应用的身份，已跳过读取。", "The foreground app’s identity could not be verified. Capture was skipped."),
            ["accessibility_unavailable"] = ("无法通过 Windows 辅助功能检查敏感输入，已跳过读取。", "Windows accessibility could not check for sensitive inputs. Capture was skipped."),
            ["accessibility_limit"] = ("窗口的辅助功能结构超过检查上限，已停止读取。", "The window’s accessibility tree exceeded the inspection limit. Capture stopped."),
            ["accessibility_timeout"] = ("窗口的辅助功能检查超时，已停止读取。", "Accessibility inspection timed out. Capture stopped."),
            ["content_unavailable"] = ("未取得此次允许读取的窗口文字或画面。", "No usable content was available from the permitted window inputs."),
            ["worker_unavailable"] = ("未能启动窗口读取进程。", "The window capture process could not be started."),
            ["worker_isolation_unavailable"] = ("无法隔离窗口读取进程，已停止读取。", "The window capture process could not be isolated. Capture stopped."),
            ["invalid_request"] = ("窗口读取请求未通过校验。", "The window capture request failed validation."),
            ["screen_analyze"] = ("正在理解此次读取的窗口内容。", "Analyzing the captured window content."),
            ["screen_result"] = ("已获得此次窗口理解结果。", "The window-understanding result is ready."),
            ["screen_memory_saved"] = ("已保存识别出的应用类型，供以后使用。", "The recognized app category was saved for future use."),
            ["screen_disabled"] = ("当前未开启按需窗口理解。", "On-demand window understanding is turned off."),
            ["remote_disabled"] = ("当前未允许向远程模型发送窗口内容。", "Sending window content to a remote model is not enabled."),
            ["disabled"] = ("主动陪伴功能当前已关闭。", "Proactive companionship is turned off."),
            ["paused"] = ("主动陪伴功能当前已暂停。", "Proactive companionship is paused."),
            ["user_interaction"] = ("检测到直接互动，已取消此前的主动回应。", "Direct interaction cancelled the pending proactive reaction."),
            ["context_disabled"] = ("当前场景的陪伴已关闭。", "Companionship is off for this activity."),
            ["context_updated"] = ("观察情境已更新，后续回应将使用新的情境。", "The observed context was updated for future reactions."),
            ["notification_priority"] = ("优先展示任务通知，暂缓主动陪伴。", "A task notification takes priority over proactive companionship."),
            ["unsupported_model"] = ("当前模型不支持此次所需的能力。", "The current model does not support the required capability."),
            ["protected_content"] = ("当前窗口包含受保护内容，已停止读取。", "The window contains protected content, so capture was stopped."),
            ["capture_timeout"] = ("窗口读取超时，已停止此次读取。", "Window capture timed out and was stopped.")
        };

    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Layers =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["work_companion"] = ("主动陪伴", "Proactive companion"),
            ["screen_understanding"] = ("窗口理解", "Window understanding"),
            ["lifecycle"] = ("功能状态", "Feature state"),
            ["observer"] = ("活动观察", "Activity observation"),
            ["policy"] = ("回应时机", "Reaction timing"),
            ["model"] = ("模型判断", "Model decision"),
            ["presentation"] = ("回应展示", "Reaction presentation"),
            ["screen"] = ("窗口读取", "Window capture"),
            ["memory"] = ("应用记忆", "App memory")
        };

    // Map only known worker outcomes. Arbitrary worker text (including a valid
    // diagnostic code for an unrelated layer) cannot enter the log or the UI.
    internal static (string Code, string Zh, string En) ScreenCaptureReason(string status)
    {
        string code = status switch
        {
            "protected_content" or "sensitive_content" or "password" => "protected_content",
            "timeout" or "capture_timeout" => "capture_timeout",
            "fullscreen_quiet" => "fullscreen",
            "excluded" or "foreground_unavailable" or "foreground_changed" or "session_unavailable" or
            "desktop_shell" or "protected_window" or "window_size_unavailable" or "identity_unavailable" or
            "accessibility_unavailable" or "accessibility_limit" or "accessibility_timeout" or
            "content_unavailable" or "worker_unavailable" or "worker_isolation_unavailable" or "invalid_request" => status,
            _ => "screen_capture_failed"
        };
        var reason = Reasons[code];
        return (code, reason.Zh, reason.En);
    }

    internal static bool TryCodes(string? layer, string? code, out string canonicalLayer)
    {
        canonicalLayer = layer switch
        { "layer1" or "companion" => "work_companion", "layer2" => "screen_understanding", _ => layer ?? "" };
        return Layers.ContainsKey(canonicalLayer) && code != null && Reasons.ContainsKey(code);
    }

    internal static bool TryEntry(DateTimeOffset timestamp, string layer, string code, out CompanionDiagnosticEntry? entry)
    {
        entry = null;
        if (!TryCodes(layer, code, out string canonical)) return false;
        var group = Layers[canonical]; var reason = Reasons[code];
        entry = new(timestamp, canonical, code, group.Zh, group.En, reason.Zh, reason.En);
        return true;
    }
}
