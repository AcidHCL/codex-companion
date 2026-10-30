using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal sealed class CodexMonitor : IDisposable
{
    private readonly ICodexConnection _connection;
    private readonly DesktopSettings _settings;
    private readonly bool _persistSettings;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _accessLifetime = new();
    private int _accessGeneration;
    private CancellationTokenSource? _readCancellation;
    private int _readGeneration;
    private int _sourceGeneration;
    private bool _olderLoaded;
    private bool _disposed;
    private string? _accountId;
    private int _failures;
    private DateTimeOffset? _lastUsageAttempt;
    private LifecycleFailure? _lastConnectionFailure;
    internal string? CallerThreadId => _connection.CallerThreadId;
    public CodexSnapshot Snapshot { get; private set; } = CodexSnapshot.Empty;
    public DashboardTaskFilter DashboardFilter { get; private set; } = DashboardTaskFilter.Codex;
    public IReadOnlyList<CodexTaskInfo> DashboardTasks => Snapshot.Tasks.Where(task => TaskSourcePolicy.Matches(task, DashboardFilter)).ToArray();
    public CodexSnapshot CodexPetSnapshot => TaskSourcePolicy.Project(Snapshot, TaskSource.Codex);
    public CodexSnapshot DotTaskSnapshot => TaskSourcePolicy.Project(Snapshot, TaskSource.Dot);
    public ConversationPage? Conversation { get; private set; }
    public CodexTaskInfo? SelectedTask { get; private set; }
    public bool IsReading { get; private set; }
    public string? ConversationError { get; private set; }
    public bool ConnectionRequested => _settings.CodexEnabled;
    public CodexPairingState PairingState => _connection.PairingState;
    public bool ConnectionEnabled => ConnectionRequested && !_connection.IsPairingRevoked;
    internal bool AutomaticRetryScheduled => _timer.IsEnabled;
    public bool MessagesEnabled => ConnectionEnabled && _settings.CodexMessagesEnabled;
    public event Action? Changed;
    public event Action<PetNotice>? Notice;
    public event Action<PetNotice>? VerifiedTransition;

    public CodexMonitor(DesktopSettings settings, ICodexConnection? connection = null, bool persistSettings = true)
    {
        _settings = settings;
        _settings.TaskSourceOverrides = TaskSourcePolicy.NormalizeOverrides(_settings.TaskSourceOverrides);
        _persistSettings = persistSettings;
        _connection = connection ?? new CodexConnection();
        _connection.SetMessageAccess(settings.CodexMessagesEnabled);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(settings.RefreshSeconds) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }
    public void Start()
    {
        // A transient storage error must not prevent the timer from ever starting.
        // The transport still refuses every RPC until its own checks succeed.
        if (ConnectionRequested && PairingState != CodexPairingState.Revoked) { _timer.Start(); _ = RefreshAsync(true); }
        else { Snapshot = DisabledSnapshot(); NotifyChanged(); }
    }
    private static CodexSnapshot DisabledSnapshot() => CodexSnapshot.Empty with { StatusText = L.T("连接已关闭", "Connection off") };
    private void InvalidateAccess()
    {
        ++_accessGeneration;
        _accessLifetime.Cancel(); _accessLifetime.Dispose(); _accessLifetime = new CancellationTokenSource();
        ClearSelection();
    }
    public void SetMessageAccess(bool enabled)
    {
        if (!ConnectionEnabled && enabled) return;
        InvalidateAccess();
        _settings.CodexMessagesEnabled = enabled;
        _connection.SetMessageAccess(enabled);
        Snapshot = Snapshot with { Refreshing = false };
        SaveSettings(); NotifyChanged();
    }
    public async Task ForgetConnectionAsync()
    {
        _settings.CodexEnabled = false; _settings.CodexMessagesEnabled = false;
        _timer.Stop(); InvalidateAccess(); _connection.SetMessageAccess(false);
        Snapshot = DisabledSnapshot(); _accountId = null; _lastUsageAttempt = null;
        SaveSettings(); NotifyChanged();
        await _connection.ForgetAsync();
    }
    public async Task ResumeConnectionAsync()
    {
        if (!_connection.ResumeSavedPairing())
            throw new InvalidOperationException(L.T("请先在 Codex 中按 Codex首次连接.txt 重新配对，再点重新连接。", "Pair again in Codex using Codex首次连接.txt, then click Reconnect."));
        InvalidateAccess(); _settings.CodexEnabled = true; _settings.CodexMessagesEnabled = false;
        _connection.SetMessageAccess(false); _failures = 0; SaveSettings();
        _timer.Start(); await RefreshAsync(true);
    }
    public void SetRefreshSeconds(int seconds)
    {
        _settings.RefreshSeconds = Math.Clamp(seconds, 5, 120);
        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        SaveSettings(); NotifyChanged();
    }
    public void SetNotifications(bool enabled) { _settings.NotificationsEnabled = enabled; SaveSettings(); NotifyChanged(); }

    public void SetDashboardFilter(DashboardTaskFilter filter)
    {
        if (_disposed || !Enum.IsDefined(filter) || filter == DashboardFilter) return;
        DashboardFilter = filter;
        CancelConversationRead();
        ReconcileSelection();
        NotifyChanged();
    }

    public void SetTaskSourceOverride(CodexTaskInfo task, TaskSource? source)
    {
        if (_disposed) return;
        if (source.HasValue && source is not (TaskSource.Codex or TaskSource.Dot))
            throw new ArgumentOutOfRangeException(nameof(source));
        if (task.Kind != "codex" || !Snapshot.Tasks.Any(row => row.Key == task.Key)) return;
        var priorOverrides = _settings.TaskSourceOverrides;
        var nextOverrides = new Dictionary<string, string>(priorOverrides, StringComparer.Ordinal);
        if (source.HasValue)
        {
            if (!nextOverrides.ContainsKey(task.Key) && nextOverrides.Count >= TaskSourcePolicy.MaximumOverrides)
                throw new InvalidOperationException(L.T("手动来源记录已达上限，请先将不需要的记录恢复为自动判断。", "The manual source record limit has been reached. Restore unused entries to automatic detection first."));
            nextOverrides[task.Key] = source == TaskSource.Codex ? "codex" : "dot";
        }
        else nextOverrides.Remove(task.Key);
        // This operation promises a durable label. A failed settings save must
        // reach the dashboard instead of silently showing an unsaved assignment.
        _settings.TaskSourceOverrides = nextOverrides;
        try { if (_persistSettings) _settings.Save(); }
        catch { _settings.TaskSourceOverrides = priorOverrides; throw; }
        ++_sourceGeneration;
        CancelConversationRead();
        Snapshot = Snapshot with { Tasks = Snapshot.Tasks.Select(ClassifyTask).ToArray() };
        ReconcileSelection();
        NotifyChanged();
    }

    private CodexTaskInfo ClassifyTask(CodexTaskInfo task) => TaskSourcePolicy.ApplyOverride(task, _settings.TaskSourceOverrides);
    private CodexTaskInfo? FindDashboardTask(string key) => Snapshot.Tasks.FirstOrDefault(task => task.Key == key && TaskSourcePolicy.Matches(task, DashboardFilter));
    private void ReconcileSelection()
    {
        if (SelectedTask == null) return;
        var current = FindDashboardTask(SelectedTask.Key);
        if (current == null) ClearSelection();
        else SelectedTask = current;
    }

    private void SaveSettings() { if (!_persistSettings) return; try { _settings.Save(); } catch (Exception ex) { Program.Log(ex); } }

    public async Task RefreshAsync(bool forceUsage = false)
    {
        if (_disposed || !ConnectionEnabled || !await _refreshGate.WaitAsync(0)) return;
        int generation = _accessGeneration;
        int sourceGeneration = _sourceGeneration;
        var prior = Snapshot;
        try
        {
            // Every operation after acquiring the gate, including UI notification,
            // belongs inside this try/finally. Rendering cannot strand polling.
            Snapshot = Snapshot with { Refreshing = true, StatusText = Snapshot.Connected ? L.T("正在同步…", "Syncing…") : L.T("正在连接 Codex…", "Connecting to Codex…") };
            NotifyChanged();
            using var access = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accessLifetime.Token);
            var token = access.Token;
            await _connection.EnsureConnectedAsync(token);
            var tasksData = await _connection.CallAsync("list_threads", new { limit = 50 }, token);
            token.ThrowIfCancellationRequested();
            var taskList = CodexParser.ParseTasks(tasksData).Select(ClassifyTask).ToList();
            for (int i = 0; i < taskList.Count; i++)
            {
                taskList[i] = ClassifyTask(taskList[i]);
                if (!MessagesEnabled || taskList[i].Source != TaskSource.Codex || !taskList[i].IsRunning) continue;
                try { var runtime = await _connection.CallAsync("read_thread", ReadArguments(taskList[i], null, 1), token); taskList[i] = CodexParser.WithRuntimeStatus(taskList[i], runtime); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* Keep the list status if detailed state is unavailable. */ }
            }
            IReadOnlyList<CodexTaskInfo> tasks = taskList.OrderByDescending(t => t.IsRunning).ThenByDescending(t => t.NeedsAttention).ThenByDescending(t => t.Pinned).ThenByDescending(t => t.UpdatedAt).ToArray();
            var unavailable = CodexParser.Get(tasksData, "unavailableHosts");
            string? error = unavailable.ValueKind == JsonValueKind.Array && unavailable.GetArrayLength() > 0 ? L.T("部分远程设备当前不可用。", "Some remote devices are currently unavailable.") : null;
            var buckets = Snapshot.Buckets;
            var usageTime = Snapshot.UsageUpdatedAt;
            var resetCredits = Snapshot.ResetCreditsAvailable;
            if (forceUsage || !_lastUsageAttempt.HasValue || (DateTimeOffset.UtcNow - _lastUsageAttempt.Value).TotalSeconds >= 60)
            {
                _lastUsageAttempt = DateTimeOffset.UtcNow;
                try
                {
                    var usageData = await _connection.CallAsync("get_usage_limits", new { }, token);
                    token.ThrowIfCancellationRequested();
                    var usage = CodexParser.ParseUsage(usageData);
                    if (_accountId != null && usage.AccountId != null && _accountId != usage.AccountId)
                    {
                        ClearSelection();
                        prior = CodexSnapshot.Empty;
                    }
                    _accountId = usage.AccountId;
                    buckets = usage.Buckets; resetCredits = usage.ResetCredits; usageTime = DateTimeOffset.Now;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { error = L.T("用量暂时无法更新；下次自动重试。已有用量为上次成功读取的结果。", "Usage could not be updated; it will retry automatically. Displayed usage is from the last successful read."); }
            }
            if (generation != _accessGeneration || !ConnectionEnabled) return;
            // Manual ownership may have changed while any RPC was in flight.
            // Commit against the latest overrides, never an earlier local copy.
            tasks = tasks.Select(ClassifyTask).ToArray();
            _failures = 0;
            if (_lastConnectionFailure.HasValue)
            {
                LifecycleLog.Record(LifecycleEvent.ConnectionRecovered);
                _lastConnectionFailure = null;
            }
            else if (!prior.Connected) LifecycleLog.Record(LifecycleEvent.ConnectionEstablished);
            _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
            Snapshot = new CodexSnapshot(true, false, error == null ? L.T("已连接 Codex", "Connected to Codex") : L.T("已连接 · 部分数据待更新", "Connected · Some data pending"), DateTimeOffset.Now, usageTime, buckets, tasks, error, resetCredits);
            ReconcileSelection();
            NotifyChanged();
            if (MessagesEnabled && sourceGeneration == _sourceGeneration && prior.Connected && (_settings.NotificationsEnabled || (_settings.SceneAutoEnabled && _settings.ScenePackId != null)))
                await CheckTransitionsAsync(prior.Tasks, tasks, sourceGeneration, token);
            if (MessagesEnabled && SelectedTask != null && !IsReading) await ReadLatestAsync(SelectedTask, false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || generation != _accessGeneration) { }
        catch (Exception ex)
        {
            if (generation != _accessGeneration || !ConnectionRequested) return;
            _connection.Disconnect();
            var failure = LifecycleLog.Classify(ex);
            if (_lastConnectionFailure != failure)
            {
                LifecycleLog.RecordException(LifecycleEvent.ConnectionRetry, ex);
                _lastConnectionFailure = failure;
            }
            if (PairingState == CodexPairingState.Revoked)
            {
                _timer.Stop(); ClearSelection();
                Snapshot = DisabledSnapshot(); NotifyChanged(); return;
            }
            _failures++;
            double seconds = Math.Min(120, _settings.RefreshSeconds * Math.Pow(2, Math.Min(_failures - 1, 5)));
            _timer.Interval = TimeSpan.FromSeconds(seconds);
            string error = PairingState switch
            {
                CodexPairingState.Unavailable => L.T("连接记录暂时无法读取；会自动重试，无需重新配对。", "The connection record is temporarily unreadable. Retrying automatically; no new pairing is needed."),
                CodexPairingState.Invalid => L.T("连接记录未通过完整性、权限或当前用户解密检查；已停止访问，修复后会重试。", "The connection record failed integrity, permissions, or current-user decryption checks. Access stopped and will retry after repair."),
                CodexPairingState.Missing => L.T("未找到连接记录，请在 Codex 中完成首次配对。", "No connection record found. Complete initial pairing in Codex."),
                CodexPairingState.Expired => L.T("连接记录已过期，请在 Codex 中主动重新配对。", "The connection record has expired. Pair again explicitly in Codex."),
                _ => FriendlyError(ex)
            };
            Snapshot = Snapshot with { Connected = false, Refreshing = false, StatusText = L.T($"Codex 未连接 · {seconds:0} 秒后自动重试", $"Codex disconnected · Retrying in {seconds:0} s"), Error = error };
            NotifyChanged();
        }
        finally
        {
            bool notify = generation == _accessGeneration && Snapshot.Refreshing;
            if (notify) Snapshot = Snapshot with { Refreshing = false };
            _refreshGate.Release();
            if (notify) NotifyChanged();
        }
    }

    private async Task CheckTransitionsAsync(IReadOnlyList<CodexTaskInfo> before, IReadOnlyList<CodexTaskInfo> after, int sourceGeneration, CancellationToken token)
    {
        var prior = before.ToDictionary(t => t.Key, StringComparer.Ordinal);
        int emitted = 0;
        foreach (var task in after)
        {
            token.ThrowIfCancellationRequested();
            if (!MessagesEnabled || sourceGeneration != _sourceGeneration) return;
            if (emitted >= 3 || !prior.TryGetValue(task.Key, out var old)) continue;
            if (old.Source != TaskSource.Codex || task.Source != TaskSource.Codex) continue;
            if (old.Status == task.Status) continue;
            if (task.NeedsAttention && !old.NeedsAttention)
            {
                EmitNotice(new PetNotice(L.T("任务需要关注", "Task needs attention"), task.Title, "attention"), task.Key, sourceGeneration); emitted++; continue;
            }
            if ((!old.IsRunning && !old.NeedsAttention) || task.IsRunning || task.Kind != "codex") continue;
            try
            {
                var data = await _connection.CallAsync("read_thread", ReadArguments(task, null, 1), token);
                token.ThrowIfCancellationRequested();
                if (!MessagesEnabled || sourceGeneration != _sourceGeneration ||
                    Snapshot.Tasks.FirstOrDefault(row => row.Key == task.Key)?.Source != TaskSource.Codex) return;
                var state = CodexParser.ParseConversation(data, task.Key).LatestTurnStatus;
                if (state == "completed") { EmitNotice(new PetNotice(L.T("任务已完成", "Task completed"), task.Title, "complete"), task.Key, sourceGeneration); emitted++; }
                else if (state is "failed" or "interrupted") { EmitNotice(new PetNotice(state == "failed" ? L.T("任务执行失败", "Task failed") : L.T("任务已中断", "Task interrupted"), task.Title, "attention"), task.Key, sourceGeneration); emitted++; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* A missing task is not evidence of completion. */ }
        }
    }

    private bool CanEmitNotice(string taskKey, int sourceGeneration) =>
        !_disposed && !_lifetime.IsCancellationRequested && MessagesEnabled && sourceGeneration == _sourceGeneration &&
        Snapshot.Tasks.FirstOrDefault(task => task.Key == taskKey)?.Source == TaskSource.Codex;

    private void EmitNotice(PetNotice notice, string taskKey, int sourceGeneration)
    {
        if (!CanEmitNotice(taskKey, sourceGeneration)) return;
        NotifyNotice(VerifiedTransition, notice, taskKey, sourceGeneration);
        if (_settings.NotificationsEnabled) NotifyNotice(Notice, notice, taskKey, sourceGeneration);
    }

    // UI subscribers are independent consumers of a successful read. A disposed
    // view, rendering failure, or tray error must not invalidate the Codex session
    // or stop other windows receiving status. Log only the redacted exception code.
    private void NotifyChanged()
    {
        var subscribers = Changed;
        if (_disposed || subscribers == null) return;
        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            if (_disposed) return;
            try { subscriber(); }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    private void NotifyNotice(Action<PetNotice>? subscribers, PetNotice notice, string taskKey, int sourceGeneration)
    {
        if (subscribers == null) return;
        foreach (Action<PetNotice> subscriber in subscribers.GetInvocationList())
        {
            if (!CanEmitNotice(taskKey, sourceGeneration)) return;
            try { subscriber(notice); }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    public async Task SelectTaskAsync(CodexTaskInfo task)
    {
        if (_disposed || !MessagesEnabled || task.Kind != "codex") return;
        var current = FindDashboardTask(task.Key);
        if (current == null) return;
        task = current;
        bool changed = SelectedTask?.Key != task.Key;
        SelectedTask = task;
        if (changed) { Conversation = null; _olderLoaded = false; ConversationError = null; }
        await ReadLatestAsync(task, true);
    }

    private async Task ReadLatestAsync(CodexTaskInfo task, bool cancelPrevious)
    {
        if (_disposed || !MessagesEnabled || FindDashboardTask(task.Key) == null || SelectedTask?.Key != task.Key) return;
        if (IsReading && !cancelPrevious) return;
        _readCancellation?.Cancel();
        _readCancellation?.Dispose();
        _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accessLifetime.Token);
        var token = _readCancellation.Token;
        int generation = ++_readGeneration;
        IsReading = true; ConversationError = null; NotifyChanged();
        try
        {
            var data = await _connection.CallAsync("read_thread", ReadArguments(task, null, 3), token);
            var latest = CodexParser.ParseConversation(data, task.Key);
            if (generation != _readGeneration || !MessagesEnabled || SelectedTask?.Key != task.Key || FindDashboardTask(task.Key) == null) return;
            (Conversation, _olderLoaded) = ConversationHistory.MergeLatest(Conversation, latest, _olderLoaded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == _readGeneration) ConversationError = FriendlyError(ex); }
        finally { if (generation == _readGeneration) { IsReading = false; NotifyChanged(); } }
    }

    public async Task LoadOlderAsync()
    {
        if (_disposed || !MessagesEnabled || IsReading || SelectedTask == null || FindDashboardTask(SelectedTask.Key) == null || Conversation?.HasMore != true || string.IsNullOrEmpty(Conversation.NextCursor)) return;
        var task = SelectedTask;
        string cursor = Conversation.NextCursor;
        _readCancellation?.Cancel(); _readCancellation?.Dispose();
        _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accessLifetime.Token);
        var token = _readCancellation.Token;
        int generation = ++_readGeneration;
        IsReading = true; ConversationError = null; NotifyChanged();
        try
        {
            var data = await _connection.CallAsync("read_thread", ReadArguments(task, cursor, 3), token);
            var older = CodexParser.ParseConversation(data, task.Key);
            if (generation != _readGeneration || !MessagesEnabled || Conversation?.TaskKey != task.Key || SelectedTask?.Key != task.Key || FindDashboardTask(task.Key) == null) return;
            Conversation = ConversationHistory.MergeOlder(Conversation, older, cursor);
            _olderLoaded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == _readGeneration) ConversationError = FriendlyError(ex); }
        finally { if (generation == _readGeneration) { IsReading = false; NotifyChanged(); } }
    }

    private static object ReadArguments(CodexTaskInfo task, string? cursor, int limit)
    {
        var args = new Dictionary<string, object> { ["threadId"] = task.Id, ["turnLimit"] = limit, ["includeOutputs"] = false, ["maxOutputCharsPerItem"] = 0 };
        if (!string.IsNullOrEmpty(task.HostId)) args["hostId"] = task.HostId;
        if (!string.IsNullOrEmpty(cursor)) args["cursor"] = cursor;
        return args;
    }
    public void ClearSelection()
    {
        CancelConversationRead();
        SelectedTask = null; Conversation = null; ConversationError = null; _olderLoaded = false;
    }
    private void CancelConversationRead()
    {
        _readCancellation?.Cancel(); ++_readGeneration;
        IsReading = false;
    }
    private static string FriendlyError(Exception ex)
    {
        if (ex is OperationCanceledException or TimeoutException) return L.T("连接超时。请确认 Codex App 已打开，稍后会自动重试。", "Connection timed out. Make sure Codex App is open; retrying automatically shortly.");
        return L.T("连接验证或读取未完成。请检查 Codex 签名、运行状态和配对记录；无法识别的接口会被拒绝。", "Connection validation or reading did not finish. Check Codex's signature, running state, and pairing record. Unrecognized interfaces are rejected.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _lifetime.Cancel(); _accessLifetime.Cancel(); _readCancellation?.Cancel();
        _connection.Dispose();
    }
}
