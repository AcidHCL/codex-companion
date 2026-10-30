using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal interface IPetSpeechRecognizer : IDisposable
{
    Task<string> TranscribeAsync(byte[] wave, CancellationToken cancellationToken);
}

// Audio travels only through a private child-process pipe, never a URL or temporary recording.
internal sealed class PetSpeechRecognizer : IPetSpeechRecognizer
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _active;
    private bool _disposed;
    internal const int MaximumWaveBytes = 16_000 * 2 * 60 + 4096;

    internal static (string Python, string Script) FindRuntime(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            string python = Path.Combine(directory.FullName, ".tools", "deepseek-asr-venv", "Scripts", "python.exe");
            if (!File.Exists(python)) continue;
            foreach (string script in new[] {
                Path.Combine(start, "asr-service", "transcribe.py"),
                Path.Combine(directory.FullName, "desktop-pet", "asr-service", "transcribe.py"),
                Path.Combine(directory.FullName, "asr-service", "transcribe.py") })
                if (File.Exists(script)) return (python, script);
        }
        throw new InvalidOperationException(L.T("本地语音识别尚未安装，请按 asr-service 中的说明安装。", "Local speech recognition is not installed. Follow the instructions in asr-service."));
    }

    public async Task<string> TranscribeAsync(byte[] wave, CancellationToken cancellationToken)
    {
        ValidateWave(wave);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(2));
        var token = operation.Token;
        await _serial.WaitAsync(token).ConfigureAwait(false);
        Process? process = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var runtime = FindRuntime(AppContext.BaseDirectory);
            var start = new ProcessStartInfo(runtime.Python)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(runtime.Script)!
            };
            start.ArgumentList.Add("-u"); start.ArgumentList.Add(runtime.Script);
            start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--ui-language"); start.ArgumentList.Add(L.Language);
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            start.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1";
            start.Environment["TOKENIZERS_PARALLELISM"] = "false";
            process = new Process { StartInfo = start };
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException(L.T("本地语音识别进程未能启动。", "The local speech recognition process could not start."));
                _active = process;
            }
            using var cancel = token.Register(() => Kill(process));
            Task<string> stdout = ReadBoundedAsync(process.StandardOutput, token);
            Task<string> stderr = ReadBoundedAsync(process.StandardError, token);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(wave, token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                string output = await stdout.ConfigureAwait(false);
                string error = await stderr.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException(SafeError(error));
                return ParseResult(output);
            }
            finally
            {
                Kill(process);
                // Always observe pipe tasks when cancellation closes the child abruptly.
                try { await stdout.ConfigureAwait(false); } catch (Exception) { }
                try { await stderr.ConfigureAwait(false); } catch (Exception) { }
            }
        }
        catch (Exception) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new InvalidOperationException(L.T("语音识别超时，请缩短录音后重试。", "Speech recognition timed out. Try a shorter recording.")); }
        catch (Exception) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        finally
        {
            if (process != null)
            {
                Kill(process);
                lock (_gate) { if (ReferenceEquals(_active, process)) _active = null; }
                process.Dispose();
            }
            _serial.Release();
        }
    }

    internal static void ValidateWave(byte[] bytes)
    {
        if (bytes.Length < 44 || bytes.Length > MaximumWaveBytes || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidOperationException(L.T("录音数据无效或超过 60 秒。", "The recording is invalid or longer than 60 seconds."));
    }

    internal static string ParseResult(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            if (!json.RootElement.TryGetProperty("text", out var field) || field.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException(L.T("语音识别未返回有效文字。", "Speech recognition did not return valid text."));
            string text = field.GetString()!.Trim();
            if (text.Length > 12000 || text.Contains('\0')) throw new InvalidOperationException(L.T("语音识别结果无效或过长。", "The transcription is invalid or too long."));
            return text;
        }
        catch (JsonException) { throw new InvalidOperationException(L.T("语音识别返回的数据无效，请重试。", "Speech recognition returned invalid data. Please try again.")); }
    }

    private static string SafeError(string error)
    {
        // Never expose arbitrary library output or transcribed content as an error.
        string code = "";
        try
        {
            using var json = JsonDocument.Parse(error.Trim());
            if (json.RootElement.TryGetProperty("error", out var field) && field.ValueKind == JsonValueKind.String) code = field.GetString() ?? "";
            if (json.RootElement.TryGetProperty("code", out field) && field.ValueKind == JsonValueKind.String) code = field.GetString() ?? code;
        }
        catch (JsonException) { }
        return code switch
        {
            "model_missing" => L.T("本地语音识别模型缺失，请按 asr-service 中的说明安装。", "The local speech recognition model is missing. Follow the instructions in asr-service."),
            "invalid_audio" or "invalid_wav" or "audio_too_long" => L.T("录音格式无效或超过 60 秒，请重新录音。", "The recording format is invalid or exceeds 60 seconds. Record again."),
            "dependency_missing" => L.T("语音识别环境不完整，请运行本地识别安装脚本。", "The speech recognition environment is incomplete. Run its local setup script."),
            _ => L.T("本地语音识别失败，请稍后重试；原有输入已保留。", "Local speech recognition failed. Try again shortly; your existing input has been kept.")
        };
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var value = new StringBuilder(); var buffer = new char[2048]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (value.Length + read > 32768) throw new InvalidOperationException(L.T("语音识别进程输出超过允许大小。", "The speech recognition process exceeded the output size limit."));
            value.Append(buffer, 0, read);
        }
        return value.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
            if (_active != null) Kill(_active);
        }
    }
}
