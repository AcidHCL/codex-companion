using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.WorkCompanion;

internal sealed record WorkHostState(bool Available, string ContextKey, long Revision, string? Reason = null,
    bool CanObserve = true);

internal interface IWorkCompanionHost
{
    WorkHostState Capture();
    Task<WorkReactionReply?> GenerateAsync(WorkReactionCandidate candidate, CancellationToken ct);
    Task<bool> PresentAsync(WorkReactionReply reply, WorkHostState expected, bool speak, CancellationToken ct);
    void FinishCandidate(WorkReactionCandidate candidate, bool accepted) { }
}

// Called on the host's serialized UI context. Polling continues while a separate,
// cancellable response task runs. The policy and host are replaceable for later
// observation layers; no application settings, chat store or memory DB here.
internal sealed class WorkCompanionController : IWorkCompanionSettingsHost, IDisposable
{
    private readonly IDesktopObserver _observer;
    private readonly IWorkCompanionHost _host;
    private readonly Action<WorkCompanionOptions> _save;
    private readonly Func<DateTimeOffset> _now;
    private readonly IWorkContextExtension? _contextExtension;
    private readonly WorkTriggerPolicy _policy = new();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private WorkCompanionOptions _options;
    private CancellationTokenSource? _responseCancellation;
    private WorkReactionCandidate? _pending;
    private WorkHostState? _expected;
    private DateTimeOffset? _lastAttempt;
    private string? _lastReaction;
    private bool _paused, _disposed, _ticking, _presenting;
    private long _epoch;

    public WorkCompanionOptions Options => _options with
    {
        ExcludedApps = (string[])_options.ExcludedApps.Clone(),
        AppActivities = new Dictionary<string, string>(_options.AppActivities, StringComparer.OrdinalIgnoreCase)
    };
    public WorkCompanionStatus Status { get; private set; } = new("");
    public event Action? Changed;
    internal Task PendingTask { get; private set; } = Task.CompletedTask;
    internal bool ShouldObserve => !_disposed && _options.Enabled && !_paused && _options.ObserveApplications &&
        (_options.WorkEnabled || _options.GameEnabled || _options.MediaEnabled || _options.DailyEnabled);

    internal WorkCompanionController(IDesktopObserver observer, IWorkCompanionHost host,
        WorkCompanionOptions options, Action<WorkCompanionOptions> save, Func<DateTimeOffset>? now = null,
        IWorkContextExtension? contextExtension = null)
    {
        _observer = observer; _host = host; _options = options.Normalize(); _save = save;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _contextExtension = contextExtension;
        _contextExtension?.Configure(Options, _paused);
        if (_contextExtension != null) _contextExtension.Invalidated += InvalidateExtension;
        ShowReadyStatus();
    }

    public void UpdateOptions(WorkCompanionOptions options)
    {
        if (_disposed) return;
        var normalized = options.Normalize();
        _save(normalized); // A failed save leaves the old live configuration intact.
        _options = normalized;
        _contextExtension?.Configure(Options, _paused);
        Invalidate(); _policy.Reset();
        ShowReadyStatus(force: true);
    }

    public void SetPaused(bool paused)
    {
        if (_disposed || _paused == paused) return;
        _paused = paused; Invalidate(); _policy.Reset(); ShowReadyStatus(force: true);
        _contextExtension?.Configure(Options, _paused);
    }

    public void ClearTransientState()
    {
        if (_disposed) return;
        Invalidate(); _policy.Reset(); _lastAttempt = null; _lastReaction = null;
        _contextExtension?.ClearTransientState();
        ShowReadyStatus(force: true);
    }

    internal void Invalidate()
    {
        if (_pending != null) CompanionDiagnostics.Record("policy", "cancelled");
        _epoch++;
        _responseCancellation?.Cancel();
        _pending = null; _expected = null;
    }

    // Codex's existing verified notifications already own their bubble, speech
    // and celebration. Coordinate priority without passing titles/body to an LLM.
    internal void ObserveCodexTransition()
    {
        if (_disposed || !_options.Enabled || _paused || !_options.UseCodexEvents || !_options.WorkEnabled) return;
        Invalidate(); _lastAttempt = _now();
        CompanionDiagnostics.Record("policy", "notification_priority");
        SetStatus(L.T("优先显示 Codex 任务通知", "Giving Codex task notifications priority"),
            L.T("沿用现有通知与庆祝，本模块不重复生成或播报。", "Existing notifications and celebrations are reused, without an extra generated announcement."));
    }

    internal async Task TickAsync()
    {
        if (_disposed || _ticking || _presenting) return;
        if (!ShouldObserve) { ShowReadyStatus(); return; }
        _ticking = true;
        long epoch = _epoch;
        try
        {
            var host = _host.Capture();
            if (!host.CanObserve)
            {
                CompanionDiagnostics.Record("observer", "observer_unavailable");
                Invalidate(); _policy.Reset();
                SetStatus(L.T("暂时保持安静", "Staying quiet for now"), host.Reason);
                return;
            }
            var observation = await SampleAsync(_lifetime.Token);
            if (_disposed || epoch != _epoch || !ShouldObserve) return;
            if (observation.IsAvailable && !ActivityEnabled(observation.Activity))
            {
                CompanionDiagnostics.Record("policy", "context_disabled");
                Invalidate(); _policy.Reset();
                SetStatus(L.T("该场景的陪伴已关闭", "Companionship is off for this activity"),
                    CompanionActivityClassifier.Label(observation.Activity));
                return;
            }
            var candidate = _policy.Observe(observation, _options.ReactToAppChanges,
                _options.BreakReminders, BreakMinutes(observation.Activity), allowFullscreen: true,
                contextRequestKey: _contextExtension?.RequestContext(observation),
                candidateLifetime: (_contextExtension?.ResponseTimeout ?? TimeSpan.FromSeconds(25)) + TimeSpan.FromSeconds(5));
            if (_pending != null && (!_policy.IsCurrent(_pending, _now()) ||
                !SameContext(_expected!, _host.Capture()))) Invalidate();
            if (observation.IsFullscreen && _options.QuietFullscreen)
            {
                CompanionDiagnostics.Record("policy", "fullscreen");
                if (_pending != null) Invalidate();
                if (candidate != null) _policy.MarkHandled(candidate, _now());
                SetStatus(L.T("全屏期间保持安静", "Staying quiet during full screen"),
                    L.T($"当前分类：{CompanionActivityClassifier.Label(observation.Activity)}。只累计活跃使用时长，不发起回应。",
                        $"Activity: {CompanionActivityClassifier.Label(observation.Activity)}. Only active time is tracked; no response is requested."));
                return;
            }
            if (!observation.IsAvailable || observation.IsOwnApp || observation.IdleFor >= WorkTriggerPolicy.AwayTime)
            {
                CompanionDiagnostics.Record("observer", observation.IsOwnApp ? "own_app" :
                    observation.IdleFor >= WorkTriggerPolicy.AwayTime ? "away" :
                    observation.Status == "excluded" ? "excluded" : "observer_unavailable");
                SetStatus(L.T("当前不观察", "Observation paused for this context"), ObservationReason(observation));
                return;
            }
            // A temporary bubble, speech or animation has presentation priority,
            // but does not erase a continuous work period. Keep sampling input.
            host = _host.Capture();
            if (!host.Available)
            {
                CompanionDiagnostics.Record("policy", "host_busy");
                if (candidate != null) _policy.ReleaseCandidate(candidate);
                SetStatus(L.T("暂时保持安静", "Staying quiet for now"), host.Reason);
                return;
            }
            if (_pending != null) return;
            if (candidate == null)
            {
                CompanionDiagnostics.Record("policy", observation.IdleFor < WorkTriggerPolicy.QuietTime ? "input_active" : "observing");
                SetStatus(L.T("主动陪伴观察中 · ", "Companion observing · ") + CompanionActivityClassifier.Label(observation.Activity),
                    L.T($"前台应用：{observation.App}；本段活跃使用约 {_policy.ActiveMinutes} 分钟。分类可在下方修正，停下输入后才考虑回应。",
                        $"Foreground app: {observation.App}; about {_policy.ActiveMinutes} active minutes in this activity. You can correct the category below. Replies wait for an input pause."));
                return;
            }
            if (!PendingTask.IsCompleted || _contextExtension?.IsExplicitRequest(candidate) != true &&
                _lastAttempt.HasValue && _now() - _lastAttempt.Value < TimeSpan.FromMinutes(_options.CooldownMinutes))
            {
                _policy.ReleaseCandidate(candidate);
                CompanionDiagnostics.Record("policy", !PendingTask.IsCompleted ? "model_busy" : "cooldown");
                SetStatus(L.T("减少打扰，暂不回应", "Keeping interruptions infrequent"));
                return;
            }
            host = _host.Capture();
            if (!host.Available) { _policy.ReleaseCandidate(candidate); return; }
            _lastAttempt = _now(); // Also rate-limit silence, failed/cold/busy-model attempts.
            _pending = candidate; _expected = host;
            CompanionDiagnostics.Record("policy", "candidate");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cancellation.CancelAfter(_contextExtension?.ResponseTimeout ?? TimeSpan.FromSeconds(25));
            _responseCancellation = cancellation;
            SetStatus(L.T("正在考虑是否回应", "Considering whether to respond"),
                L.T("使用当前聊天服务和模型；Ollama 需已加载，API 按需请求。回应不写入对话记录或共享记忆。", "Uses the selected chat service and model: already-loaded Ollama or API on demand. Reactions stay out of chat history and shared memory."));
            PendingTask = RespondAsync(candidate, host, _epoch, cancellation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            Invalidate(); _policy.Reset();
            // Never log observed titles or model responses through exceptions.
            SetStatus(L.T("观察暂不可用", "Observation temporarily unavailable"),
                L.T("稍后自动重试；原有桌宠功能不受影响。", "Will retry automatically; other pet features are unaffected."));
        }
        finally { _ticking = false; }
    }

    private async Task<WorkObservation> SampleAsync(CancellationToken ct)
    {
        await _sampleGate.WaitAsync(ct);
        try
        {
            var observation = await _observer.ReadAsync(_options.IncludeWindowTitles, _options.ExcludedApps, ct);
            var activity = CompanionActivityClassifier.Classify(observation.App, _options.AppActivities);
            if (observation.IsAvailable && !_options.AppActivities.ContainsKey(WorkCompanionOptions.NormalizeAppName(observation.App) ?? "") && _contextExtension != null)
                activity = await _contextExtension.RecallActivityAsync(observation, ct) ?? activity;
            return observation with { Activity = activity };
        }
        finally { _sampleGate.Release(); }
    }

    private async Task RespondAsync(WorkReactionCandidate candidate, WorkHostState expected,
        long epoch, CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        bool accepted = false;
        try
        {
            var reply = await _host.GenerateAsync(candidate, ct);
            ct.ThrowIfCancellationRequested();
            if (reply == null)
            {
                CompanionDiagnostics.Record("model", "no_result");
                SetStatus(L.T("本次保持安静", "Staying quiet this time"),
                    L.T("当前模型未就绪、忙碌、请求失败，或判断不需要回应；不会自动加载 Ollama 模型，也不会切换到其他服务。", "The selected model is unavailable, busy, failed to respond, or chose silence. Ollama is not loaded automatically and no other service is used."));
                return;
            }
            if (_disposed || epoch != _epoch || !ShouldObserve) return;
            // A new read just before display closes the gap between timer ticks.
            var fresh = await SampleAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed || epoch != _epoch || !ShouldObserve) return;
            if (!ActivityEnabled(fresh.Activity) || fresh.IsFullscreen && _options.QuietFullscreen) return;
            _policy.Observe(fresh, _options.ReactToAppChanges, _options.BreakReminders, BreakMinutes(fresh.Activity), allowFullscreen: true);
            if (!_policy.IsCurrent(candidate, _now()) || !SameContext(expected, _host.Capture()))
            { CompanionDiagnostics.Record("policy", "stale_result"); return; }
            _host.FinishCandidate(candidate, accepted: true); accepted = true;
            if (reply.ContextOnly)
            {
                CompanionDiagnostics.Record("policy", "context_updated");
                SetStatus(L.T("已更新窗口理解", "Window context updated"),
                    L.T("没有需要打扰你的内容；应用识别结果可在屏幕理解页查看。", "No interruption is needed. App recognition is available in Screen understanding."));
                return;
            }
            bool presented;
            // Showing our own bubble/expression changes host availability and
            // revision. Pause polling only during this presentation, otherwise
            // it would cancel itself; explicit invalidation still cancels it.
            _presenting = true;
            try { presented = await _host.PresentAsync(reply, expected, _options.SpeakReactions, ct); }
            finally { _presenting = false; }
            ct.ThrowIfCancellationRequested();
            if (_disposed || epoch != _epoch || !ShouldObserve) return;
            if (presented)
            {
                CompanionDiagnostics.Record("presentation", "presented");
                _lastReaction = reply.Text;
                SetStatus(L.T("已作出轻量回应", "A brief response was shown"),
                    L.T("这条回应只在桌宠显示，不保存到对话或记忆。", "This response is shown by the pet only, not saved to chat or memory."));
            }
        }
        catch (OperationCanceledException) { CompanionDiagnostics.Record("policy", "cancelled"); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { if (!_disposed && epoch == _epoch) SetStatus(L.T("本次保持安静", "Staying quiet this time"), L.T("回应暂不可用；稍后继续观察。", "A response is unavailable; observation will continue.")); }
        finally
        {
            if (!accepted) _host.FinishCandidate(candidate, accepted: false);
            _policy.MarkHandled(candidate, _now());
            if (ReferenceEquals(_responseCancellation, cancellation))
            { _responseCancellation = null; _pending = null; _expected = null; }
            cancellation.Dispose();
        }
    }

    internal static bool SameContext(WorkHostState expected, WorkHostState current) => current.Available &&
        expected.ContextKey == current.ContextKey && expected.Revision == current.Revision;

    private void ShowReadyStatus(bool force = false)
    {
        if (!ShouldObserve) CompanionDiagnostics.Record("policy", _paused ? "paused" : "disabled");
        string summary = !_options.Enabled ? L.T("主动陪伴已关闭", "Proactive companion is off") :
            _paused ? L.T("主动陪伴已暂停", "Proactive companion is paused") :
            !_options.ObserveApplications ? L.T("应用观察已关闭", "Application observation is off") :
            !ShouldObserve ? L.T("所有陪伴场景已关闭", "All companion activities are off") :
            L.T("等待应用活动", "Waiting for app activity");
        SetStatus(summary, L.T("可陪伴工作、游戏、影音和日常使用。按应用名称分类，窗口标题需单独开启。",
            "Companionship for work, games, media and daily use. Categories use app names; titles require a separate opt-in."), force);
    }

    private bool ActivityEnabled(WorkActivity activity) => activity switch
    {
        WorkActivity.Work => _options.WorkEnabled, WorkActivity.Game => _options.GameEnabled,
        WorkActivity.Media => _options.MediaEnabled, _ => _options.DailyEnabled
    };

    private int BreakMinutes(WorkActivity activity) => activity switch
    {
        WorkActivity.Work => _options.BreakAfterMinutes, WorkActivity.Game => _options.GameBreakAfterMinutes,
        WorkActivity.Media => _options.MediaBreakAfterMinutes, _ => _options.DailyBreakAfterMinutes
    };

    private void SetStatus(string summary, string? detail = null, bool force = false)
    {
        if (_disposed) return;
        var status = new WorkCompanionStatus(summary, detail, _lastReaction, _paused);
        if (status == Status && !force) return;
        Status = status; Changed?.Invoke();
    }

    private static string ObservationReason(WorkObservation observation) => observation.IsFullscreen
        ? L.T("全屏应用期间保持安静。", "Staying quiet during full-screen applications.")
        : observation.IdleFor >= WorkTriggerPolicy.AwayTime ? L.T("已离开一段时间，重新开始工作后再观察。", "Away for a while; observation resumes with a new work period.")
        : observation.Status is "own_app" || observation.IsOwnApp ? L.T("你正在使用桌宠自己的界面。", "You are using the pet's own interface.")
        : observation.Status is "excluded" ? L.T("当前应用在排除范围内。", "This application is excluded.")
        : L.T("锁屏、会话断开或当前窗口不可读取。", "The session is locked, disconnected, or the current window is unavailable.");

    public void Dispose()
    {
        if (_disposed) return;
        Invalidate(); _policy.Reset(); _lastReaction = null;
        if (_contextExtension != null) _contextExtension.Invalidated -= InvalidateExtension;
        _disposed = true; _lifetime.Cancel();
        // In-flight reads own the semaphore/token until their finally blocks run.
    }

    private void InvalidateExtension() { Invalidate(); _policy.Reset(); }
}
