using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.ScreenUnderstanding;

// UIA providers and PrintWindow can block inside another app. A short-lived
// helper isolates them; cancellation terminates only the Process we started.
internal sealed class WindowsScreenWindowCapture : IScreenWindowCapture
{
    internal const int MaximumReplyCharacters = 1_450_000;
    internal const int MaximumRequestCharacters = 16_384;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public Task<string?> ReadIdentityAsync(string app, string windowKey, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ScreenCaptureNative.TrySnapshot(app, windowKey, true, Array.Empty<string>(), out var snapshot, out _)
                ? snapshot!.Identity : null;
        }, ct);

    public async Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureRequest request, CancellationToken ct)
    {
        if (!ScreenCapturePolicy.ValidRequest(request)) return ScreenCaptureResult.Unavailable("invalid_request");
        ct.ThrowIfCancellationRequested();
        string? identity = await ReadIdentityAsync(request.App, request.WindowKey, ct).ConfigureAwait(false);
        if (identity == null) return ScreenCaptureResult.Unavailable("foreground_unavailable");
        string executable = Path.Combine(AppContext.BaseDirectory, "DesktopPet.exe");
        if (!File.Exists(executable)) return ScreenCaptureResult.Unavailable("worker_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("--screen-capture-worker");
        bool started = false;
        try
        {
            if (!process.Start()) return ScreenCaptureResult.Unavailable("worker_unavailable");
            started = true;
            // The native worker also self-terminates after six seconds. The job
            // covers parent crashes in addition to ordinary cancellation.
            using var job = ScreenCaptureJob.Attach(process);
            if (job == null) return ScreenCaptureResult.Unavailable("worker_isolation_unavailable");
            Task<string> output = ReadBoundedAsync(process.StandardOutput, MaximumReplyCharacters, deadline.Token);
            Task<string> errors = ReadBoundedAsync(process.StandardError, 4096, deadline.Token);
            string json = JsonSerializer.Serialize(request, ScreenCapturePolicy.WireOptions);
            if (json.Length > MaximumRequestCharacters) return ScreenCaptureResult.Unavailable("invalid_request");
            await process.StandardInput.WriteAsync(json.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string reply = await output.ConfigureAwait(false);
            await errors.ConfigureAwait(false); // Drained but deliberately never logged.
            if (process.ExitCode != 0) return ScreenCaptureResult.Unavailable("capture_unavailable");
            var result = JsonSerializer.Deserialize<ScreenCaptureResult>(reply, new JsonSerializerOptions { MaxDepth = 8 });
            if (!ScreenCapturePolicy.ValidResult(result, request))
                return ScreenCaptureResult.Unavailable("capture_unavailable");
            if (!result!.Success) return result;
            if (result.ApplicationIdentity != identity) return ScreenCaptureResult.Unavailable("foreground_changed");
            // Window switches while stdout is being drained revoke even a
            // successful helper result. No output is retained on rejection.
            string? current = await ReadIdentityAsync(request.App, request.WindowKey, deadline.Token).ConfigureAwait(false);
            return current == identity ? result : ScreenCaptureResult.Unavailable("foreground_changed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ScreenCaptureResult.Unavailable("capture_timeout"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ScreenCaptureResult.Unavailable("capture_unavailable"); }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var result = new StringBuilder(Math.Min(limit, 4096));
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (result.Length + read > limit) throw new InvalidDataException("capture_protocol_limit");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
