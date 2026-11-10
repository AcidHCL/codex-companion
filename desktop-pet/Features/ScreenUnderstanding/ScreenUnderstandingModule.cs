using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DesktopPet.WorkCompanion;

namespace DesktopPet.ScreenUnderstanding;

internal sealed record ScreenModelServices(
    Func<IOllamaClient, string, CancellationToken, Task<ScreenModelReadiness>> Readiness,
    Func<IOllamaClient, string, string, WorkReactionCandidate, ScreenCaptureResult, CancellationToken, Task<ScreenAnalysis?>> Analyze)
{
    internal static ScreenModelServices Default { get; } = new(
        ScreenModelConnection.ReadinessAsync,
        ScreenUnderstandingPlanner.AnalyzeAsync);
}

// Layer-two composition root. The first layer supplies only transient target
// metadata and an already-serialized model lane. No raw capture escapes here.
internal sealed class ScreenUnderstandingModule : IWorkContextExtension, IScreenUnderstandingSettingsHost, IDisposable
{
    private sealed record Pending(string App, string Identity, ScreenAnalysis Analysis, bool MayLearn);
    private readonly IScreenWindowCapture _capture;
    private readonly LearnedApplicationStore _memory;
    private readonly Action<ScreenUnderstandingOptions> _save;
    private readonly ScreenModelServices _models;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _multipurpose = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "iexplore", "browser", "vivaldi",
        "applicationframehost", "runtimebroker", "java", "javaw", "python", "pythonw", "powershell", "pwsh",
        "windowsterminal", "cmd", "electron", "cefsharpbrowsersubprocess", "msedgewebview2", "explorer",
        "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "riotclientservices",
        "mstsc", "msrdc", "windowsapp", "anydesk", "teamviewer", "parsec", "rustdesk", "sunshine", "moonlight",
        "vncviewer", "vmware", "virtualboxvm", "wsl", "putty", "notepad", "notepad++", "sublime_text",
        "discord", "wechat", "weixin", "qq", "telegram", "slack", "teams", "ms-teams", "zoom",
        "codex", "chatgpt", "claude", "dot"
    };
    private ScreenUnderstandingOptions _options;
    private WorkCompanionOptions _companion = new();
    private CancellationTokenSource? _operation;
    private bool _paused, _disposed, _manualPending;
    private long _manualSequence;
    private DateTimeOffset _nextAttempt;
    private string? _lastSummary;

    public ScreenUnderstandingOptions Options => _options;
    public ScreenUnderstandingStatus Status { get; private set; } = new("");
    public event Action? Changed;
    public event Action? Invalidated;
    public TimeSpan ResponseTimeout => Active ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(25);
    private bool Active => !_disposed && _options.Enabled && _companion.Enabled && _companion.ObserveApplications &&
        !_paused && (_companion.WorkEnabled || _companion.GameEnabled || _companion.MediaEnabled || _companion.DailyEnabled);

    internal ScreenUnderstandingModule() : this(new WindowsScreenWindowCapture(),
        new ScreenUnderstandingOptionsStore().Load(), new ScreenUnderstandingOptionsStore().Save,
        new LearnedApplicationStore()) { }

    internal ScreenUnderstandingModule(IScreenWindowCapture capture, ScreenUnderstandingOptions options,
        Action<ScreenUnderstandingOptions> save, LearnedApplicationStore memory,
        ScreenModelServices? models = null, Func<DateTimeOffset>? now = null)
    {
        _capture = capture; _options = options.Normalize(); _save = save; _memory = memory;
        _models = models ?? ScreenModelServices.Default; _now = now ?? (() => DateTimeOffset.UtcNow);
        ReadyStatus();
    }

    internal FrameworkElement CreateSettingsView() => new ScreenUnderstandingSettingsView(this);

    public void Configure(WorkCompanionOptions options, bool paused)
    {
        _companion = options.Normalize(); _paused = paused;
        CancelTransient(); ReadyStatus();
    }

    public void UpdateOptions(ScreenUnderstandingOptions options)
    {
        if (_disposed) return;
        var normalized = options.Normalize(); _save(normalized); _options = normalized;
        CancelTransient(); _nextAttempt = default;
        Invalidated?.Invoke(); ReadyStatus();
    }

    public void RequestNextWindowAnalysis()
    {
        if (!Active || !_options.ReadWindowText && !_options.UseScreenshots)
        { ReadyStatus(); return; }
        CancelTransient(); _manualPending = true; _manualSequence++;
        Invalidated?.Invoke();
        SetStatus(L.T("已预约一次窗口理解", "One window analysis is queued"),
            L.T("请切回目标窗口，停下输入后会查看一次。全屏安静和排除应用仍优先。", "Return to the target window and pause input. Full-screen quiet and app exclusions still take priority."));
    }

    public async Task<WorkActivity?> RecallActivityAsync(WorkObservation observation, CancellationToken ct)
    {
        if (!Active || !_options.LearnApplications || !observation.IsAvailable || observation.IsOwnApp ||
            IsMultipurpose(observation.App) || !_memory.List().Any(item => item.App == NormalizeApp(observation.App))) return null;
        string? identity = await IdentityAsync(observation.App, observation.WindowKey, ct);
        if (!Active || !_options.LearnApplications || identity == null) return null;
        var item = _memory.GetByIdentity(observation.App, identity);
        return item != null && CompanionActivityClassifier.TryParse(item.Activity, out var activity) ? activity : null;
    }

    private async Task<string?> IdentityAsync(string app, string key, CancellationToken ct)
    {
        // Only remembered apps take this cheap background metadata path. Recheck
        // each observation rather than trusting potentially reused PID/HWND keys.
        string? identity = await _capture.ReadIdentityAsync(app, key, ct);
        return Active ? identity : null;
    }

    public string? RequestContext(WorkObservation observation)
    {
        if (!Active || !_options.ReadWindowText && !_options.UseScreenshots || !observation.IsAvailable || observation.IsOwnApp ||
            observation.IsFullscreen && _companion.QuietFullscreen || !ActivityEnabled(observation.Activity)) return null;
        if (_manualPending) return "manual:" + _manualSequence + ":" + observation.WindowKey;
        if (_now() < _nextAttempt) return null;
        // A bounded periodic opportunity, not continuous capture. Sampling the
        // app never touches pixels/UIA; only a stable quiet candidate may do so.
        long bucket = _now().ToUnixTimeSeconds() / (_options.AutomaticIntervalMinutes * 60);
        return "context:" + bucket + ":" + observation.WindowKey;
    }

    public bool IsExplicitRequest(WorkReactionCandidate candidate) => _manualPending &&
        candidate.ContextRequestKey?.StartsWith("manual:" + _manualSequence + ":", StringComparison.Ordinal) == true;

    public async Task<WorkReactionReply?> GenerateAsync(IOllamaClient client, string model, string persona,
        WorkReactionCandidate candidate, Func<Task<WorkReactionReply?>> fallback, CancellationToken ct)
    {
        bool explicitRequest = IsExplicitRequest(candidate);
        if (!Active || !_options.ReadWindowText && !_options.UseScreenshots || string.IsNullOrWhiteSpace(candidate.WindowKey) ||
            !ActivityEnabled(candidate.Activity) || !explicitRequest && _now() < _nextAttempt)
            return await fallback();
        if (!client.Endpoint.IsLoopback && !_options.AllowRemoteEndpoint)
        {
            CompanionDiagnostics.Record("screen", "remote_disabled");
            _nextAttempt = _now().AddMinutes(_options.AutomaticIntervalMinutes);
            SetStatus(L.T("远程屏幕传送未开启", "Remote screen sharing is off"),
                L.T("当前模型服务不在本机。可在此页开启远程传送，或改用本机模型。", "The selected model service is remote. Enable remote sharing here, or use a local model."));
            return null;
        }
        _manualPending = false;
        _nextAttempt = _now().AddMinutes(_options.AutomaticIntervalMinutes);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _operation = cancellation;
        try
        {
            var ready = await _models.Readiness(client, model, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Active || candidate.ExpiresAt <= _now()) return null;
            if (!ready.Available)
            {
                CompanionDiagnostics.Record("model", "model_not_ready");
                SetStatus(client is OpenAiCompatibleClient ? L.T("等待可用的 API 模型", "Waiting for a configured API model") : L.T("等待已加载的聊天模型", "Waiting for an already-loaded chat model"),
                    L.T("模型未就绪，本次没有读取窗口文字或截图。", "The model is not ready. No window text or screenshot was read."));
                return null;
            }
            bool text = _options.ReadWindowText && ready.SupportsText;
            bool image = _options.UseScreenshots && ready.SupportsImages;
            if (!text && !image)
            {
                CompanionDiagnostics.Record("model", "unsupported_model");
                SetStatus(L.T("当前模型不能使用已启用的输入", "This model cannot use the enabled input"),
                    L.T("图片需要视觉模型；也可开启窗口文字读取，使用文字方式。", "Images require a vision model. Window text can be enabled for text-only understanding."));
                return null;
            }
            SetStatus(L.T("正在按需读取当前窗口", "Reading the current window on demand"),
                L.T("只处理本次前台窗口，切换窗口或恢复输入会取消。", "Only this foreground window is used. Switching windows or resuming input cancels the response."));
            bool unknown = !CompanionActivityClassifier.HasBuiltInCategory(candidate.App) && !IsMultipurpose(candidate.App) &&
                _memory.GetByApp(candidate.App) == null;
            bool visualContext = explicitRequest || unknown || candidate.Activity is WorkActivity.Game or WorkActivity.Media;
            var request = new ScreenCaptureRequest(candidate.App, candidate.WindowKey!, text,
                image && (visualContext || !text), !_companion.QuietFullscreen, _companion.ExcludedApps);
            CompanionDiagnostics.Record("screen", "screen_capture");
            var captured = await _capture.CaptureAsync(request, cancellation.Token);
            // Only the explicit safe-but-empty result may fall back after a
            // failed text read. Protected, changed or timed-out windows must
            // never turn into a second capture attempt.
            bool needsImage = captured.Success
                ? captured.ImageBase64 == null && captured.Text.Length < 160
                : captured.Status == "content_unavailable";
            if (image && !request.IncludeImage && needsImage)
                captured = await _capture.CaptureAsync(request with { IncludeImage = true }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Active || candidate.ExpiresAt <= _now() || !captured.Success || captured.WindowKey != candidate.WindowKey)
            {
                CompanionDiagnostics.Record("screen", captured.Status is "protected_content" ? "protected_content" :
                    captured.Status is "capture_timeout" ? "capture_timeout" : "screen_capture_failed");
                SetStatus(L.T("本次窗口无法读取", "This window could not be read"),
                    CaptureReason(captured.Status));
                return null;
            }
            if (captured.Text.Length == 0 && captured.ImageBase64 == null)
            { SetStatus(L.T("没有可理解的窗口内容", "No readable window content")); return null; }
            SetStatus(L.T("正在理解窗口内容", "Understanding the window"),
                captured.ImageBase64 != null ? L.T("使用窗口截图与可用文字。", "Using a window image and available text.") :
                L.T("使用窗口文字；本次没有发送截图。", "Using window text; no screenshot was sent."));
            CompanionDiagnostics.Record("screen", "screen_analyze");
            var analysis = await _models.Analyze(client, model, persona, candidate, captured, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Active || analysis == null) { CompanionDiagnostics.Record("screen", "no_result"); SetStatus(L.T("本次理解没有可靠结果", "No reliable understanding this time")); return null; }
            CompanionDiagnostics.Record("screen", analysis.Text.Length == 0 ? "model_silent" : "screen_result");
            bool mayLearn = _options.LearnApplications && analysis.ReusableApp &&
                analysis.Activity != null && analysis.Confidence >= LearnedApplicationStore.MinimumConfidence &&
                !IsMultipurpose(candidate.App) && !CompanionActivityClassifier.HasBuiltInCategory(candidate.App) &&
                !_companion.AppActivities.ContainsKey(NormalizeApp(candidate.App));
            if (_pending.Count >= 4) _pending.Clear();
            // Store only validated structured output temporarily. Pixels/text
            // are released with the local capture after this method returns.
            _pending[candidate.Id] = new Pending(candidate.App, captured.ApplicationIdentity, analysis, mayLearn);
            return new WorkReactionReply(analysis.Text, analysis.Expression) { ContextOnly = analysis.Text.Length == 0 };
        }
        catch (OperationCanceledException)
        {
            CompanionDiagnostics.Record("screen", "cancelled");
            if (ReferenceEquals(_operation, cancellation) && Active) SetStatus(L.T("本次理解已取消", "Window understanding was cancelled"));
            throw;
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            if (ReferenceEquals(_operation, cancellation) && Active) SetStatus(L.T("窗口理解暂不可用", "Window understanding is temporarily unavailable"));
            return null;
        }
        finally { if (ReferenceEquals(_operation, cancellation)) _operation = null; }
    }

    public void FinishCandidate(WorkReactionCandidate candidate, bool accepted)
    {
        if (!_pending.Remove(candidate.Id, out var pending)) return;
        if (!accepted || !Active) return;
        // The controller calls this only after a fresh foreground/idle/host
        // validation. A stale model response cannot teach persistent memory.
        _lastSummary = pending.Analysis.Summary;
        bool learned = false;
        if (pending.MayLearn && _options.LearnApplications && pending.Analysis.Activity is { } category)
        {
            try { learned = _memory.SaveLearned(pending.App, pending.Identity, CompanionActivityClassifier.Key(category), pending.Analysis.Confidence); }
            catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
            { SetStatus(L.T("已理解，但应用分类未保存", "Understood, but app classification was not saved")); return; }
        }
        if (learned) CompanionDiagnostics.Record("memory", "screen_memory_saved");
        SetStatus(learned ? L.T("已理解并记住应用分类", "Understood and remembered the app category") :
            L.T("已理解当前窗口", "Current window understood"),
            learned ? L.T("只保存应用身份和分类；可在下方纠正或删除。", "Only app identity and category were saved. Correct or delete them below.") :
            L.T("理解摘要只留在本次运行中；多用途应用不会凭单个页面记成固定分类。", "The summary stays in memory for this run. A multipurpose app is not classified permanently from one page."));
    }

    public IReadOnlyList<LearnedApplication> GetMemories() => _memory.List();
    public void UpdateMemoryCategory(string id, string category)
    {
        if (!_memory.UpdateCategory(id, category)) throw new InvalidOperationException(L.T("这条记忆已过期或分类无效。", "This memory expired or the category is invalid."));
        CancelTransient(); Invalidated?.Invoke(); SetStatus(L.T("已纠正应用记忆", "App memory corrected"));
    }
    public void DeleteMemory(string id)
    {
        _memory.Delete(id); CancelTransient(); Invalidated?.Invoke(); SetStatus(L.T("已删除应用记忆", "App memory deleted"));
    }
    public void ClearMemories()
    {
        _memory.Clear(); CancelTransient(); Invalidated?.Invoke(); SetStatus(L.T("已清空应用记忆", "App memory cleared"));
    }
    public void ClearTransientState() { CancelTransient(); ReadyStatus(); }
    private void CancelTransient()
    {
        var operation = _operation; _operation = null;
        operation?.Cancel(); _pending.Clear(); _lastSummary = null; _manualPending = false;
    }
    private void ReadyStatus() => SetStatus(!_options.Enabled ? L.T("屏幕理解已关闭", "Screen understanding is off") :
        !Active ? L.T("等待主动陪伴开启或恢复", "Waiting for proactive companion to be enabled or resumed") :
        !_options.ReadWindowText && !_options.UseScreenshots ? L.T("请选择窗口文字或截图", "Enable window text or screenshots") :
        L.T("按需窗口理解已就绪", "On-demand window understanding is ready"),
        L.T("使用当前选中的 Ollama 或 API 模型。Ollama 需已加载；沿用场景开关、排除应用和全屏安静，不保存截图或正文。",
            "Uses the selected Ollama or API model. Ollama must already be loaded. Activity switches, exclusions and full-screen quiet still apply; screenshots and window text are not saved."));
    private void SetStatus(string summary, string? detail = null)
    {
        if (_disposed) return;
        var state = new ScreenUnderstandingStatus(summary, detail, _lastSummary);
        if (Status == state) return;
        Status = state; Changed?.Invoke();
    }
    private bool IsMultipurpose(string app) => _multipurpose.Contains(NormalizeApp(app));
    private static string NormalizeApp(string app) => WorkCompanionOptions.NormalizeAppName(app) ?? "";
    private bool ActivityEnabled(WorkActivity activity) => activity switch
    {
        WorkActivity.Work => _companion.WorkEnabled, WorkActivity.Game => _companion.GameEnabled,
        WorkActivity.Media => _companion.MediaEnabled, _ => _companion.DailyEnabled
    };
    private static string CaptureReason(string status) => status switch
    {
        "password" or "sensitive_content" or "protected_content" => L.T("窗口含敏感输入，本次已跳过。", "Sensitive input was found; this window was skipped."),
        "timeout" or "capture_timeout" => L.T("该应用没有及时提供内容，已停止读取。", "The app did not provide content in time; reading stopped."),
        "fullscreen_quiet" => L.T("已开启全屏时安静，不读取全屏窗口。", "Fullscreen quiet mode is on; fullscreen windows are not read."),
        _ => L.T("窗口可能已切换、被排除、无法提供内容，或当前会话不可用。", "The window may have changed, be excluded, expose no usable content, or the session may be unavailable.")
    };
    public void Dispose()
    {
        if (_disposed) return;
        CancelTransient(); _disposed = true;
    }
}
