using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet;

internal interface IPetSpeechPlayer : IDisposable
{
    Task PlayAsync(byte[] wave, double volume, CancellationToken cancellationToken);
    void SetVolume(double volume);
    void Stop();
}

internal sealed class PetSpeechPlayer(Dispatcher dispatcher) : IPetSpeechPlayer
{
    private MediaPlayer? _media;
    private TaskCompletionSource? _completion;
    private bool _disposed;
    private double _volume = .6;
    private string? _file;
    private bool _cleaned;

    public async Task PlayAsync(byte[] wave, double volume, CancellationToken cancellationToken)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        TimeSpan playbackTimeout = PetSpeechWave.Duration(wave) + TimeSpan.FromSeconds(30);
        cancellationToken.ThrowIfCancellationRequested();
        // Only the currently playing utterance exists on disk, with an unguessable name.
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "voice-temp");
        Directory.CreateDirectory(folder);
        if (!_cleaned) { _cleaned = true; CleanAbandonedFiles(folder); }
        string file = Path.Combine(folder, "speech-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".wav");
        MediaPlayer? media = null;
        try
        {
            _file = file;
            // Buffered audio is bounded; complete the write before yielding so
            // shutdown can synchronously close and remove the current file.
            File.WriteAllBytes(file, wave);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            media = new MediaPlayer();
            _media = media; _completion = completion;
            SetVolume(volume);
            media.MediaOpened += (_, _) => { if (ReferenceEquals(_media, media) && !cancellationToken.IsCancellationRequested) media.Play(); };
            media.MediaEnded += (_, _) => completion.TrySetResult();
            media.MediaFailed += (_, _) => completion.TrySetException(new InvalidOperationException("音频播放失败，请检查当前音频设备。"));
            using var registration = cancellationToken.Register(() => dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_completion, completion)) Stop();
            }));
            media.Open(new Uri(file));
            // A complete answer can exceed five minutes; keep a bounded startup/
            // device margin without cutting off otherwise valid long playback.
            await completion.Task.WaitAsync(playbackTimeout, cancellationToken);
        }
        finally
        {
            if (media != null) { media.Close(); if (ReferenceEquals(_media, media)) { _media = null; _completion = null; } }
            DeleteFile(file);
            if (_file == file) _file = null;
        }
    }

    public void SetVolume(double volume)
    {
        dispatcher.VerifyAccess();
        _volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : .6;
        if (_media != null) _media.Volume = _volume;
    }

    public void Stop()
    {
        dispatcher.VerifyAccess();
        _media?.Close(); _media = null;
        _completion?.TrySetCanceled(); _completion = null;
        if (_file != null) { DeleteFile(_file); _file = null; }
    }

    private static void DeleteFile(string file)
    { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static void CleanAbandonedFiles(string folder)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "speech-*-*.wav"))
            {
                string[] pieces = Path.GetFileNameWithoutExtension(file).Split('-');
                if (pieces.Length != 3 || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || !Guid.TryParseExact(pieces[2], "N", out _)) continue;
                bool alive;
                try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; }
                catch (ArgumentException) { alive = false; }
                if (!alive) DeleteFile(file);
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }
}
