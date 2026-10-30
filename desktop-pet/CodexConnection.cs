using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Private installed-App adapter, not a supported public OpenAI API.
// Only the App's supplied/known pipe prefix and verified tool catalog are used. No credentials,
// application databases, or other applications' data are inspected.
internal sealed class CodexConnection : ICodexConnection
{
    private const int MaxFrameBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal) { "get_usage_limits", "list_threads", "read_thread" };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object stateLock = new();
    private readonly string baseDirectory;
    private readonly CodexConnectionStore store;
    private readonly CodexPairingMigration? migration;
    private readonly bool explicitPairing;
    private volatile bool messageAccess;
    private volatile bool forgotten;
    private volatile CodexPairingState pairingState = CodexPairingState.Unknown;
    private CancellationTokenSource sessionCancellation = new();
    private Session? session;
    private bool disposed;
    private long nextRequestId;
    private int candidateAttempts;
    internal int CandidateAttempts => Volatile.Read(ref candidateAttempts);
    private string description = L.T("未连接 · Codex 本机只读接口", "Disconnected · Local read-only Codex interface");
    private string? discoveryFailure;
    private int discoveryFailurePriority;

    public CodexConnection(string? baseDirectory = null, bool explicitPairing = false, CodexConnectionStore? store = null)
    {
        this.baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        this.explicitPairing = explicitPairing;
        this.store = store ?? new CodexConnectionStore();
        // Fixture/injected stores never enumerate or inspect real user storage.
        if (store == null && !explicitPairing) migration = CodexPairingMigration.CreateDefault(this.store);
    }
    public string ConnectionDescription { get { lock (stateLock) return description; } }
    public string? CallerThreadId { get { lock (stateLock) return session?.Context.ThreadId; } }
    public bool IsPairingRevoked => forgotten || pairingState == CodexPairingState.Revoked;
    public CodexPairingState PairingState => forgotten ? CodexPairingState.Revoked : pairingState;
    public bool HasSavedPairing
    {
        get { var loaded = LoadSavedPairing(); pairingState = loaded.State; return loaded.Hint != null; }
    }
    public bool ResumeSavedPairing()
    {
        if (!HasSavedPairing)
        {
            if (pairingState is CodexPairingState.Unavailable or CodexPairingState.Invalid) throw PairingFailure(pairingState);
            return false;
        }
        forgotten = false;
        return true;
    }
    internal static string HintPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "connection.dat");

    public void SetMessageAccess(bool enabled)
    {
        messageAccess = enabled;
        if (!enabled) Disconnect();
    }

    public async Task ForgetAsync()
    {
        forgotten = true;
        pairingState = CodexPairingState.Revoked;
        messageAccess = false;
        Disconnect();
        await operations.WaitAsync().ConfigureAwait(false);
        try { store.Forget(); }
        finally { operations.Release(); }
    }

    internal async Task CommitPairingAsync()
    {
        if (!explicitPairing) throw new InvalidOperationException(L.T("只有主动配对可以重建连接记录。", "Only explicit pairing can recreate the connection record."));
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Session connected;
            lock (stateLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (forgotten || session == null || !session.Pipe.IsConnected) throw new InvalidOperationException(L.T("配对连接已经关闭。", "The pairing connection has been closed."));
                connected = session;
            }
            if (!store.SaveVerified(new CodexConnectionHint(connected.Endpoint, connected.Context.ThreadId, connected.Context.TurnId, DateTimeOffset.UtcNow), allowReactivation: true))
                throw new IOException(L.T("无法安全保存连接记录。", "Could not save the connection record securely."));
        }
        finally { operations.Release(); }
    }

    public async Task EnsureConnectedAsync(CancellationToken token = default)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CreateDeadline(token, TimeSpan.FromSeconds(30));
            await EnsureConnectedCoreAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ResetConnection(L.T("连接中断或超时 · 请确认 Codex App 正在运行", "Connection interrupted or timed out · Check that Codex App is running"));
            throw new TimeoutException(L.T("连接 Codex App 已中断或超时，请确认 App 正在运行后重试。", "The Codex App connection was interrupted or timed out. Check that the app is running, then retry."));
        }
        catch (Exception ex)
        {
            ResetConnection(L.T("未连接 · ", "Disconnected · ") + SafeError(ex));
            throw;
        }
        finally { operations.Release(); }
    }

    public async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
    {
        if (!AllowedTools.Contains(tool) && tool != "tools/list")
            throw new InvalidOperationException(L.T("桌宠连接仅允许 get_usage_limits、list_threads、read_thread 和 tools/list 四种只读请求。", "The pet allows only four read-only requests: get_usage_limits, list_threads, read_thread, and tools/list."));
        if (tool == "read_thread" && !messageAccess) throw new InvalidOperationException(L.T("对话内容读取已关闭。", "Conversation reading is off."));
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (tool == "read_thread" && !messageAccess) throw new InvalidOperationException(L.T("对话内容读取已关闭。", "Conversation reading is off."));
            using var deadline = CreateDeadline(token, TimeSpan.FromSeconds(40));
            var connected = await EnsureConnectedCoreAsync(deadline.Token).ConfigureAwait(false);
            if (tool == "tools/list")
                return await RequestAsync(connected.Pipe, "tools/list", new { threadStartKind = "all" }, deadline.Token).ConfigureAwait(false);
            var result = await CallOnSessionAsync(connected, tool, arguments, deadline.Token).ConfigureAwait(false);
            return Unpack(result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ResetConnection(L.T("查询中断或超时 · 下次刷新将重新连接", "Query interrupted or timed out · Reconnecting on the next refresh"));
            throw new TimeoutException(L.T("Codex 只读查询已中断或超时；下次刷新会重新连接。", "The read-only Codex query was interrupted or timed out. The next refresh will reconnect."));
        }
        catch (Exception ex)
        {
            // Discard the entire stream after cancellation/error. A late reply must
            // never be mistaken for a subsequent call's result.
            ResetConnection(L.T("未连接 · ", "Disconnected · ") + SafeError(ex));
            throw;
        }
        finally { operations.Release(); }
    }

    public void Disconnect()
    {
        Session? oldSession;
        CancellationTokenSource oldCancellation;
        lock (stateLock)
        {
            if (disposed) return;
            oldSession = session; session = null;
            oldCancellation = sessionCancellation;
            sessionCancellation = new CancellationTokenSource();
            description = L.T("已断开 · Codex 本机只读接口", "Disconnected · Local read-only Codex interface");
        }
        oldCancellation.Cancel();
        oldSession?.Dispose();
        oldCancellation.Dispose();
    }

    public void Dispose()
    {
        Session? oldSession;
        CancellationTokenSource oldCancellation;
        lock (stateLock)
        {
            if (disposed) return;
            disposed = true;
            oldSession = session; session = null;
            oldCancellation = sessionCancellation;
            description = L.T("连接已关闭", "Connection off");
        }
        oldCancellation.Cancel();
        oldSession?.Dispose();
        oldCancellation.Dispose();
        // Do not dispose the semaphore while an in-flight operation can release it.
    }

    private CancellationTokenSource CreateDeadline(CancellationToken caller, TimeSpan timeout)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, sessionCancellation.Token);
            linked.CancelAfter(timeout);
            return linked;
        }
    }

    private async Task<Session> EnsureConnectedCoreAsync(CancellationToken token)
    {
        if (forgotten) throw PairingFailure(CodexPairingState.Revoked);
        // Every call rechecks the authoritative marker before reusing a stream.
        // An unknown/unsafe store blocks RPC, but never becomes a permanent revoke.
        var revocation = store.CheckRevocation();
        if (revocation != CodexPairingState.Ready && !(explicitPairing && revocation == CodexPairingState.Revoked))
        {
            pairingState = revocation;
            throw PairingFailure(revocation);
        }
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session != null && session.Pipe.IsConnected) return session;
            session?.Dispose(); session = null;
            description = L.T("正在连接 Codex App…", "Connecting to Codex App…");
            discoveryFailure = null;
            discoveryFailurePriority = 0;
        }
        var loaded = LoadSavedPairing();
        var hint = loaded.Hint;
        pairingState = loaded.State;
        if (hint == null && (!explicitPairing || pairingState is CodexPairingState.Unavailable or CodexPairingState.Invalid))
            throw PairingFailure(pairingState);
        var context = ResolveContext(hint);
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? endpoint, string source)
        {
            if (CodexEndpointPolicy.TryNormalize(endpoint, out string normalized) && seen.Add(normalized)) candidates.Add(new Candidate(normalized, source));
        }
        Add(Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH"), L.T("App 环境端点", "App environment endpoint"));
        Add(hint?.Endpoint, L.T("本机连接记录", "Local connection record"));
        Session? found = null;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            found = await TryCandidateAsync(candidate, context, token).ConfigureAwait(false);
            if (found != null) break;
        }
        if (found == null)
        {
            // A prefix-constrained pipe name listing is the only discovery operation.
            // No process environments, databases, auth files, or other pipe contents
            // are read. Each candidate must expose the three expected read tools.
            var discovered = CodexEndpointPolicy.Discover().Where(endpoint => seen.Add(endpoint)).Select(endpoint => new Candidate(endpoint, L.T("本机管道发现", "Local pipe discovery"))).ToArray();
            found = await DiscoverSessionAsync(discovered, context, token).ConfigureAwait(false);
        }
        if (found == null)
        {
            string? failure;
            lock (stateLock) failure = discoveryFailure;
            throw new IOException(failure == null
                ? L.T("未找到兼容的 Codex AppTools 连接。请打开 Codex App；App 升级后接口也可能发生变化。", "No compatible Codex AppTools connection found. Open Codex App; an app update may also have changed its interface.")
                : L.T("Codex 自动连接失败：", "Automatic Codex connection failed: ") + failure);
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (forgotten) throw new OperationCanceledException();
            if (!explicitPairing)
            {
                bool saved = loaded.IsMigrationCandidate
                    ? migration!.CommitVerified(hint!, new CodexConnectionHint(found.Endpoint,
                        found.Context.ThreadId, found.Context.TurnId, hint!.SavedAtUtc))
                    : store.SaveVerified(new CodexConnectionHint(found.Endpoint, found.Context.ThreadId, found.Context.TurnId, DateTimeOffset.UtcNow));
                if (!saved) { pairingState = store.LastState; throw PairingFailure(pairingState); }
            }
            lock (stateLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                session = found;
                pairingState = CodexPairingState.Ready;
                description = L.T("已连接 · 已验证 OpenAI 签名与本机用户 · 桌宠只读请求", "Connected · OpenAI signature and local user verified · Pet read-only requests");
            }
            return found;
        }
        catch { found.Dispose(); throw; }
    }

    private async Task<Session?> DiscoverSessionAsync(Candidate[] candidates, CallerContext context, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var slots = new SemaphoreSlim(4, 4);
        async Task<Session?> Probe(Candidate candidate)
        {
            bool entered = false;
            try
            {
                await slots.WaitAsync(stop.Token).ConfigureAwait(false); entered = true;
                return await TryCandidateAsync(candidate, context, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            finally { if (entered) slots.Release(); }
        }
        var pending = candidates.Take(64).Select(Probe).ToList();
        Session? winner = null;
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var candidate = await completed.ConfigureAwait(false);
                if (candidate != null) { winner = candidate; break; }
            }
        }
        finally
        {
            stop.Cancel();
            var others = await Task.WhenAll(pending).ConfigureAwait(false);
            foreach (var other in others) other?.Dispose();
        }
        if (token.IsCancellationRequested) { winner?.Dispose(); token.ThrowIfCancellationRequested(); }
        return winner;
    }

    private async Task<Session?> TryCandidateAsync(Candidate candidate, CallerContext context, CancellationToken token)
    {
        Interlocked.Increment(ref candidateAttempts);
        var pipeName = candidate.Endpoint.Substring(CodexEndpointPolicy.PipePathPrefix.Length);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        bool keep = false;
        string stage = L.T("连接本机管道", "Connect to local pipe");
        try
        {
            using var candidateTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            candidateTimeout.CancelAfter(TimeSpan.FromSeconds(7));
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(candidateTimeout.Token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(1.2));
                await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            stage = L.T("验证 Codex 进程身份", "Verify Codex process identity");
            CodexPipeTrust.Validate(pipe);
            candidateTimeout.Token.ThrowIfCancellationRequested();
            stage = L.T("读取接口目录", "Read interface directory");
            var catalog = await RequestAsync(pipe, "tools/list", new { threadStartKind = "all" }, candidateTimeout.Token).ConfigureAwait(false);
            var toolNamespaces = ValidateCatalog(catalog);
            var connected = new Session(pipe, candidate.Endpoint, candidate.Source, context, toolNamespaces);
            // The signed peer accepts the caller context via a usage-only request.
            // Pairing and reconnecting never retrieve conversation messages.
            stage = L.T("验证只读连接", "Verify read-only connection");
            var raw = await CallOnSessionAsync(connected, "get_usage_limits", new { }, candidateTimeout.Token).ConfigureAwait(false);
            CodexParser.ParseUsage(Unpack(raw));
            keep = true;
            return connected;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { RememberDiscoveryFailure(stage, new TimeoutException(stage + L.T("超时。", " timed out."))); return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        { RememberDiscoveryFailure(stage, ex); return null; }
        finally { if (!keep) pipe.Dispose(); }
    }

    private async Task<JsonElement> CallOnSessionAsync(Session connected, string tool, object arguments, CancellationToken token)
    {
        if (!AllowedTools.Contains(tool) || !connected.ToolNamespaces.TryGetValue(tool, out string? toolNamespace))
            throw new InvalidOperationException(L.T("Codex 未提供兼容的只读接口：", "Codex did not provide a compatible read-only interface: ") + tool);
        if (tool == "read_thread" && !messageAccess) throw new InvalidOperationException(L.T("对话内容读取已关闭。", "Conversation reading is off."));
        return await RequestAsync(connected.Pipe, "tools/call", new
        {
            arguments,
            callerSource = "codex",
            callId = "ai-pet-read-" + Guid.NewGuid().ToString("N"),
            @namespace = toolNamespace,
            threadId = connected.Context.ThreadId,
            tool,
            turnId = connected.Context.TurnId
        }, token).ConfigureAwait(false);
    }

    private async Task<JsonElement> RequestAsync(NamedPipeClientStream pipe, string method, object parameters, CancellationToken token)
    {
        if (method != "tools/list" && method != "tools/call") throw new InvalidOperationException("Unsupported RPC method.");
        long id = Interlocked.Increment(ref nextRequestId);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { id, jsonrpc = "2.0", method, @params = parameters });
        if (body.Length > MaxFrameBytes) throw new InvalidDataException(L.T("Codex 请求超过帧大小限制。", "The Codex request exceeds the frame-size limit."));
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);
        await pipe.WriteAsync(frame.AsMemory(), token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
        for (int skipped = 0; skipped < 32; skipped++)
        {
            byte[] header = new byte[4];
            await pipe.ReadExactlyAsync(header.AsMemory(), token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 1 || length > MaxFrameBytes) throw new InvalidDataException(L.T("Codex 返回了无效的帧长度。", "Codex returned an invalid frame length."));
            byte[] response = new byte[length];
            await pipe.ReadExactlyAsync(response.AsMemory(), token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            var message = doc.RootElement;
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0")
                throw new InvalidDataException(L.T("Codex JSON-RPC 协议不兼容。", "The Codex JSON-RPC protocol is incompatible."));
            if (!message.TryGetProperty("id", out var responseId)) continue;
            if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt64(out long actualId) || actualId != id)
                throw new InvalidDataException(L.T("Codex 返回了不匹配的请求 ID。", "Codex returned a mismatched request ID."));
            if (message.TryGetProperty("error", out var error))
            {
                string code = error.TryGetProperty("code", out var errorCode) ? errorCode.ToString() : "unknown";
                // Do not put peer-controlled error text or identifiers into logs/UI.
                if (!int.TryParse(code, out int numericCode)) numericCode = 0;
                throw new RpcException(L.T("Codex 本地接口拒绝请求（", "The local Codex interface rejected the request (") + numericCode + L.T("），请检查配对或版本兼容性。", "). Check pairing and version compatibility."));
            }
            if (!message.TryGetProperty("result", out var result)) throw new InvalidDataException(L.T("Codex 响应缺少 result。", "The Codex response is missing result."));
            return result.Clone();
        }
        throw new InvalidDataException(L.T("Codex 未返回与请求匹配的响应。", "Codex did not return a response matching the request."));
    }

    private void RememberDiscoveryFailure(string stage, Exception error)
    {
        int priority = error is RpcException ? 3 : stage == L.T("连接本机管道", "Connect to local pipe") ? 1 : 2;
        lock (stateLock)
        {
            if (priority < discoveryFailurePriority) return;
            discoveryFailurePriority = priority;
            discoveryFailure = stage + L.T("失败；", " failed; ") + (stage == L.T("验证 Codex 进程身份", "Verify Codex process identity") ? L.T("签名、安装来源或用户身份不匹配，已拒绝连接。", "Signature, installation source, or user identity does not match. Connection refused.") : L.T("请确认 Codex 正常运行、连接记录有效且版本兼容。", "Check that Codex is running, the connection record is valid, and versions are compatible."));
        }
    }

    private static string BoundedMessage(string message)
    {
        string singleLine = new(message.Take(180).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return singleLine.Trim();
    }

    private static JsonElement Unpack(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new RpcException(L.T("Codex 只读工具未成功完成查询。", "The read-only Codex tool did not complete the query successfully."));
        if (!result.TryGetProperty("contentItems", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(L.T("Codex 工具响应缺少 contentItems。", "The Codex tool response is missing contentItems."));
        var text = new StringBuilder();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var type) && type.GetString() == "inputText" && item.TryGetProperty("text", out var part) && part.ValueKind == JsonValueKind.String)
            { if (text.Length > 0) text.Append('\n'); text.Append(part.GetString()); }
        }
        if (text.Length == 0) throw new InvalidDataException(L.T("Codex 工具响应缺少 JSON 文本。", "The Codex tool response is missing JSON text."));
        using var parsed = JsonDocument.Parse(text.ToString());
        return parsed.RootElement.Clone();
    }

    private static Dictionary<string, string> ValidateCatalog(JsonElement catalog)
    {
        if (catalog.ValueKind != JsonValueKind.Object || !catalog.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(L.T("该本机管道不是兼容的 Codex AppTools 接口。", "This local pipe is not a compatible Codex AppTools interface."));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools.EnumerateArray())
        {
            if (!tool.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String) continue;
            string name = nameValue.GetString()!;
            if (!AllowedTools.Contains(name)) continue;
            if (!tool.TryGetProperty("namespace", out var ns) || ns.GetString() != "codex_app" || !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) || type.GetString() != "object")
                throw new InvalidDataException(L.T("Codex 只读工具的命名空间或参数协议不兼容。", "The Codex read-only tool namespace or argument protocol is incompatible."));
            if (!result.TryAdd(name, ns.GetString()!)) throw new InvalidDataException(L.T("Codex 返回了重复的只读工具名称。", "Codex returned duplicate read-only tool names."));
        }
        if (AllowedTools.Any(name => !result.ContainsKey(name))) throw new InvalidDataException(L.T("Codex 缺少额度、任务或消息读取接口。", "Codex lacks the usage, task, or message-reading interfaces."));
        return result;
    }

    private CodexPairingLoad LoadSavedPairing()
    {
        if (migration != null) return migration.Load();
        var hint = store.Load();
        return new(hint, store.LastState);
    }

    private CallerContext ResolveContext(CodexConnectionHint? hint)
    {
        if (explicitPairing)
        {
            string? thread = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            string? turn = Environment.GetEnvironmentVariable("CODEX_TURN_ID");
            if (IsContextId(thread) && IsContextId(turn)) return new CallerContext(thread!, turn!);
            throw new InvalidOperationException(L.T("首次配对需要 Codex 提供的真实任务和轮次，请按 Codex首次连接.txt 操作。", "Initial pairing requires a real task and turn supplied by Codex. Follow Codex首次连接.txt."));
        }
        if (hint != null) return new CallerContext(hint.CallerThreadId, hint.CallerTurnId);
        throw new InvalidOperationException(L.T("没有有效连接记录，或记录已过期。请在 Codex 中完成首次配对。", "No valid connection record, or the record has expired. Complete initial pairing in Codex."));
    }

    private static Exception PairingFailure(CodexPairingState state) => state switch
    {
        CodexPairingState.Unavailable => new IOException(L.T("连接记录暂时无法读取；稍后自动重试，无需重新配对。", "The connection record is temporarily unreadable. Retrying automatically shortly; no new pairing is needed.")),
        CodexPairingState.Invalid => new UnauthorizedAccessException(L.T("连接记录未通过完整性、权限或当前用户解密检查；已停止访问。", "The connection record failed integrity, permissions, or current-user decryption checks. Access stopped.")),
        CodexPairingState.Revoked => new InvalidOperationException(L.T("连接已清除，请在 Codex 中重新配对。", "Connection cleared. Pair again in Codex.")),
        CodexPairingState.Expired => new InvalidOperationException(L.T("连接记录已过期，请在 Codex 中主动重新配对。", "The connection record has expired. Pair again explicitly in Codex.")),
        CodexPairingState.Missing => new InvalidOperationException(L.T("未找到连接记录，请在 Codex 中完成首次配对。", "No connection record found. Complete initial pairing in Codex.")),
        _ => new IOException(L.T("连接记录尚未验证；稍后自动重试。", "The connection record has not been verified. Retrying automatically shortly."))
    };

    private void ResetConnection(string reason)
    {
        Session? old;
        lock (stateLock) { old = session; session = null; if (!disposed) description = reason; }
        old?.Dispose();
    }
    private static bool IsContextId(string? value) => Guid.TryParseExact(value, "D", out _);
    private static string SafeError(Exception ex) => ex is OperationCanceledException ? L.T("查询已取消", "Query canceled") : ex is IOException ? L.T("本机连接不可用，下次刷新将重试", "Local connection unavailable; retrying on the next refresh") : ex is ObjectDisposedException ? L.T("连接已关闭", "Connection off") : ex.Message.Length <= 150 ? ex.Message : ex.Message.Substring(0, 150);
    private sealed record Candidate(string Endpoint, string Source);
    private sealed record CallerContext(string ThreadId, string TurnId);
    private sealed class Session : IDisposable
    {
        public NamedPipeClientStream Pipe { get; }
        public string Endpoint { get; }
        public string Source { get; }
        public CallerContext Context { get; set; }
        public Dictionary<string, string> ToolNamespaces { get; }
        public Session(NamedPipeClientStream pipe, string endpoint, string source, CallerContext context, Dictionary<string, string> namespaces)
        { Pipe = pipe; Endpoint = endpoint; Source = source; Context = context; ToolNamespaces = namespaces; }
        public void Dispose() => Pipe.Dispose();
    }
    private sealed class RpcException : IOException { public RpcException(string message) : base(message) { } }
}

