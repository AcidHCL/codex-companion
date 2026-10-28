using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal interface IPetSpeechSynthesizer : IDisposable
{
    Task<byte[]> SynthesizeAsync(string text, string requestId, CancellationToken cancellationToken);
    Task CancelAsync(string requestId);
    Task ReleaseAsync();
}

// Owns a local worker and an ephemeral authentication token. No cloud service,
// existing user's server, environment credential, or shell command is involved.
internal sealed class PetVoiceClient : IPetSpeechSynthesizer, IPetStreamingSpeechSynthesizer, IPetVoiceLifecycle
{
    private readonly SemaphoreSlim _startup = new(1, 1);
    private readonly object _workerLock = new();
    private readonly object _workerStopLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _http;
    private readonly Func<(string Python, string Script)> _runtimeResolver;
    private readonly int _port;
    private readonly Action? _beforeOwnedWorkerStop;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private Process? _worker;
    private long _workerRevision;
    private bool _disposed;
    internal const int MaxWaveBytes = 32 * 1024 * 1024;

    internal PetVoiceClient(Func<(string Python, string Script)>? runtimeResolver = null, int port = 18766,
        Action? beforeOwnedWorkerStop = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port; _runtimeResolver = runtimeResolver ?? (() => FindRuntime(AppContext.BaseDirectory));
        _beforeOwnedWorkerStop = beforeOwnedWorkerStop;
        _http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(9) };
        _http.DefaultRequestHeaders.Add("X-AIPet-Voice-Token", _token);
    }

    internal int? OwnedWorkerProcessId
    {
        get { lock (_workerLock) return _worker is { HasExited: false } worker ? worker.Id : null; }
    }

    // Authenticated read-only state for diagnostics; never starts a worker.
    internal async Task<PetVoiceHealth?> ReadHealthAsync(CancellationToken cancellationToken)
    {
        if (!WorkerRunning) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await _http.GetAsync("health", deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
        var root = data.RootElement;
        bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return new PetVoiceHealth(Text("status"), Text("backend"), Flag("loaded"), Flag("retained"), Flag("retiring"));
    }

    public bool SupportsStreaming
    {
        get
        {
            try
            {
                var runtime = _runtimeResolver();
                using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(runtime.Script)!, "config.json")));
                return config.RootElement.TryGetProperty("backend", out var backend) && backend.GetString() == "faster";
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return false; }
        }
    }

    internal static (string Python, string Script) FindRuntime(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            foreach (string script in new[]
            {
                Path.Combine(start, "voice-service", "server.py"),
                Path.Combine(directory.FullName, "desktop-pet", "voice-service", "server.py"),
                Path.Combine(directory.FullName, "voice-service", "server.py")
            })
            {
                if (!File.Exists(script)) continue;
                bool fast = false;
                string config = Path.Combine(Path.GetDirectoryName(script)!, "config.json");
                if (File.Exists(config))
                {
                    using var data = JsonDocument.Parse(File.ReadAllText(config));
                    fast = data.RootElement.TryGetProperty("backend", out var backend) && backend.GetString() == "faster";
                }
                string python = Path.Combine(directory.FullName, ".tools", fast ? "deepseek-voice-fast-venv" : "deepseek-voice-venv", "Scripts", "python.exe");
                if (File.Exists(python)) return (python, script);
            }
        }
        throw new InvalidOperationException("本地音色引擎尚未安装，请先运行 voice-service 中的安装脚本。");
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        Process? createdThisAttempt = null;
        try
        {
            Process? existing;
            lock (_workerLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                existing = _worker is { HasExited: false } ? _worker : null;
            }
            if (existing != null && await WorkerAcceptsRequestsAsync(existing, cancellationToken).ConfigureAwait(false)) return;
            // A fast worker retires after dropping its CUDA graphs. HasExited
            // may still be false for a short grace period; never reuse it then.
            await Task.Run(() => StopOwnedWorker(throwOnFailure: true), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = _runtimeResolver();
            var start = new ProcessStartInfo(runtime.Python)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(runtime.Script)!
            };
            start.ArgumentList.Add("-u"); start.ArgumentList.Add(runtime.Script);
            start.ArgumentList.Add("--port"); start.ArgumentList.Add(_port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["TRANSFORMERS_OFFLINE"] = "1";
            start.Environment["TOKENIZERS_PARALLELISM"] = "false";
            var process = new Process { StartInfo = start };
            // Drain output without retaining any spoken text or credentials.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            lock (_workerLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!process.Start()) throw new InvalidOperationException("本地音色服务未能启动。");
                _worker = process; createdThisAttempt = process; _workerRevision++;
            }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync(_token.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            var until = Stopwatch.StartNew();
            string lastFailure = "no_response";
            while (until.Elapsed < TimeSpan.FromSeconds(50))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("本地音色服务已退出，请检查模型安装或端口占用。");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await _http.GetAsync("health", timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        throw new InvalidOperationException("语音端口被其他服务占用，无法验证本次语音会话。");
                    if (response.IsSuccessStatusCode) return;
                    lastFailure = "HTTP " + (int)response.StatusCode;
                }
                catch (HttpRequestException ex) { lastFailure = ex.HttpRequestError + (ex.InnerException is System.Net.Sockets.SocketException socket ? ":" + socket.SocketErrorCode : ""); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { lastFailure = "connection_timeout"; }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("本地音色服务连接超时（" + lastFailure + "）。");
        }
        catch
        {
            // Cancelling a health check while reusing a warm worker must not
            // destroy that worker or a different ongoing preload/speech call.
            if (createdThisAttempt != null)
                await Task.Run(() => StopOwnedWorker(throwOnFailure: false, expected: createdThisAttempt)).ConfigureAwait(false);
            throw;
        }
        finally { _startup.Release(); }
    }

    private async Task<bool> WorkerAcceptsRequestsAsync(Process worker, CancellationToken cancellationToken)
    {
        try
        {
            if (worker.HasExited) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync("health", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return !worker.HasExited;
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return !(data.RootElement.TryGetProperty("retiring", out var retiring) && retiring.ValueKind == JsonValueKind.True) &&
                !(data.RootElement.TryGetProperty("status", out var status) && status.GetString() == "retiring") && !worker.HasExited;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { return !worker.HasExited; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return !worker.HasExited; }
    }

    private async Task<HttpResponseMessage> SendVoiceRequestAsync(string path, object body, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            await EnsureStartedAsync(token).ConfigureAwait(false);
            (int Id, long Revision) owner;
            lock (_workerLock) owner = (_worker?.Id ?? -1, _workerRevision);
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonBody(body) };
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                if (!await OwnedWorkerRetiredAsync(owner, token).ConfigureAwait(false) ||
                    !await RestartRetiredWorkerAsync(owner, token).ConfigureAwait(false)) throw;
                continue;
            }
            if (attempt == 0 && await IsRetiringResponseAsync(response, token).ConfigureAwait(false))
            {
                response.Dispose();
                if (await RestartRetiredWorkerAsync(owner, token).ConfigureAwait(false)) continue;
                throw new InvalidOperationException("语音会话已改变，请重新发起朗读。");
            }
            return response;
        }
    }

    private static async Task<bool> IsRetiringResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable) ||
            response.Content.Headers.ContentLength is not (>= 2 and <= 4096)) return false;
        try
        {
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            return data.RootElement.TryGetProperty("error", out var error) && error.GetString() == "retiring";
        }
        catch (JsonException) { return false; }
    }

    private async Task<bool> OwnedWorkerRetiredAsync((int Id, long Revision) owner, CancellationToken token)
    {
        Process worker;
        lock (_workerLock)
        {
            if (_disposed || _worker == null || _worker.Id != owner.Id || _workerRevision != owner.Revision) return false;
            worker = _worker;
            if (worker.HasExited) return true;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            using var response = await _http.GetAsync("health", timeout.Token).ConfigureAwait(false);
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return data.RootElement.TryGetProperty("retiring", out var retiring) && retiring.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            lock (_workerLock) return _worker == worker && _workerRevision == owner.Revision && worker.HasExited;
        }
    }

    private async Task<bool> RestartRetiredWorkerAsync((int Id, long Revision) owner, CancellationToken token)
    {
        await _startup.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_workerLock)
                if (_disposed || _worker == null || _worker.Id != owner.Id || _workerRevision != owner.Revision) return false;
            await Task.Run(() => StopOwnedWorker(throwOnFailure: true)).ConfigureAwait(false);
            return true;
        }
        finally { _startup.Release(); }
    }

    public async Task<byte[]> SynthesizeAsync(string text, string requestId, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = lifetime.Token;
        using var response = await SendVoiceRequestAsync("v1/audio/speech", new { input = text, voice = "deepseek-let-me-go", request_id = requestId }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string hint = response.StatusCode switch
            {
                HttpStatusCode.Conflict => "语音引擎正在处理另一段声音，请稍后重试。",
                HttpStatusCode.ServiceUnavailable => "本地音色引擎暂不可用，可能正在等待空闲显存或尚未安装完整。",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "本地语音会话认证失败。",
                _ => "本地音色合成失败，可点击试听重试。"
            };
            throw new InvalidOperationException(hint);
        }
        if (response.Content.Headers.ContentLength is long length && length > MaxWaveBytes)
            throw new InvalidOperationException("语音结果超过允许大小。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaxWaveBytes) throw new InvalidOperationException("语音结果超过允许大小。");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        ValidateWave(bytes);
        return bytes;
    }

    public async IAsyncEnumerable<PetPcmChunk> StreamAsync(string text, string requestId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = lifetime.Token;
        using var response = await SendVoiceRequestAsync("v1/audio/stream", new { input = text, voice = "deepseek-let-me-go", request_id = requestId }, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("本地流式语音暂不可用，请检查语音模型状态后重试。");
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new InvalidOperationException("语音服务未返回有效的音频流。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await foreach (var chunk in PetPcmProtocol.ReadAsync(stream, token).ConfigureAwait(false)) yield return chunk;
    }

    public async Task PreloadAsync(string requestId, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var response = await SendVoiceRequestAsync("preload", new { request_id = requestId }, lifetime.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("语音模型预加载失败，请检查显存和本地引擎安装。");
    }

    public async Task SetRetentionAsync(bool active, CancellationToken cancellationToken)
    {
        // A heartbeat must never resurrect an explicitly unloaded/stopped worker.
        if (!WorkerRunning) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(3));
        using var content = JsonBody(new { active });
        using var response = await _http.PostAsync("retention", content, lifetime.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("无法更新语音模型保留状态。");
    }

    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _startup.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            // Releasing Python references does not necessarily destroy captured
            // CUDA graphs. Exit and wait for this client's exact child instead.
            await RequestUnloadBestEffortAsync().ConfigureAwait(false);
            await Task.Run(() => StopOwnedWorker(throwOnFailure: true)).ConfigureAwait(false);
        }
        finally { _startup.Release(); }
    }

    internal static void ValidateWave(byte[] bytes)
    {
        if (bytes.Length < 44 || bytes.Length > MaxWaveBytes ||
            System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidOperationException("语音服务未返回有效的 WAV 音频。");
    }

    public async Task CancelAsync(string requestId)
    {
        if (!WorkerRunning) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var content = JsonBody(new { request_id = requestId });
            using var response = await _http.PostAsync("cancel", content, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async Task ReleaseAsync()
    {
        if (_disposed) return;
        await _startup.WaitAsync().ConfigureAwait(false);
        try
        {
            await RequestUnloadBestEffortAsync().ConfigureAwait(false);
            await Task.Run(() => StopOwnedWorker(throwOnFailure: true)).ConfigureAwait(false);
        }
        finally { _startup.Release(); }
    }

    private async Task RequestUnloadBestEffortAsync()
    {
        if (!WorkerRunning) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var content = JsonBody(new { });
            using var response = await _http.PostAsync("unload", content, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void StopOwnedWorker(bool throwOnFailure = false, Process? expected = null)
    {
        // Dispose may race an async release after _worker was detached. Join
        // that stop operation before deciding that no owned process remains.
        lock (_workerStopLock) StopOwnedWorkerCore(throwOnFailure, expected);
    }

    private void StopOwnedWorkerCore(bool throwOnFailure, Process? expected)
    {
        Process? worker;
        lock (_workerLock)
        {
            if (expected != null && !ReferenceEquals(_worker, expected)) return;
            worker = _worker; _worker = null;
            if (worker != null) _workerRevision++;
        }
        if (worker == null) return;
        bool exited = false;
        try
        {
            // Optional in-assembly fixture probe; production has no callback.
            _beforeOwnedWorkerStop?.Invoke();
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            exited = worker.WaitForExit(5000);
            if (!exited && throwOnFailure) throw new InvalidOperationException("语音进程尚未退出，显存释放未完成，请重试卸载。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { exited = worker.HasExited; } catch (InvalidOperationException) { }
            if (!exited && throwOnFailure) throw new InvalidOperationException("语音进程未能退出，显存释放未完成，请重试卸载。", ex);
        }
        finally
        {
            if (exited) worker.Dispose();
            else lock (_workerLock)
            {
                // Preserve ownership so another request cannot silently create
                // a second worker while the previous one still owns its GPU.
                if (_worker == null) _worker = worker;
            }
        }
    }

    private bool WorkerRunning { get { lock (_workerLock) return !_disposed && _worker is { HasExited: false }; } }

    // The bounded local server deliberately rejects chunked requests.
    internal static StringContent JsonBody<T>(T body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    public void Dispose()
    {
        lock (_workerLock) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        StopOwnedWorker(); _http.Dispose();
    }
}

internal sealed record PetVoiceHealth(string Status, string Backend, bool Loaded, bool Retained, bool Retiring);
