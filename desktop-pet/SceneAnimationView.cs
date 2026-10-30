using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DesktopPet;

// A local-only media renderer. It has no host objects and no connection to Codex.
internal sealed class SceneAnimationView : Grid, IDisposable
{
    private const string Origin = "https://aipet-scene.invalid";
    private const string Entry = Origin + "/runtime/index.html";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SceneAnimationPack _pack;
    private readonly string _profileDirectory;
    private readonly WebView2CompositionControl _browser;
    private CoreWebView2Environment? _environment;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedVideoSource> _videoSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource<bool> _ready = NewCompletion();
    private readonly TaskCompletionSource<bool> _loaded = NewCompletion();
    private TaskCompletionSource<bool>? _playing;
    private TaskCompletionSource<bool>? _completed;
    private string _requestId = "";
    private string? _action;
    private readonly string _loadId = Guid.NewGuid().ToString("N");
    private bool _disposed, _paused, _runtimeReady, _loadStarted;
    private bool _audioEnabled;
    private bool _audioStopped;
    private double _audioVolume = 0.35;
    private double? _bodyViewportHeight;
    private double _thoughtHeadroom;
    private string? _motionAction;
    private int _motionFrame;
    public bool IsPlaying { get; private set; }
    public bool IsFailed { get; private set; }
    internal int RangeRequests { get; private set; }
    internal int OpenVideoSources => _videoSources.Count;
    public event Action? Completed;
    public event Action<string>? Failed;

    public SceneAnimationView(SceneAnimationPack pack, string? profileDirectory = null)
    {
        _pack = pack;
        // Selection revalidates the manifest, so localize the built-in overlay
        // at the rendering boundary as well. Imported/user text is untouched.
        if (L.IsEnglish && BundledSceneAnimations.IsBuiltIn(pack.Id) &&
            pack.Actions.TryGetValue("sing", out var singing) && singing.Thoughts != null)
            singing.Thoughts.Title = "DeepSeek is thinking…";
        // Scene audio needs application-controlled playback; keep its browser
        // options/profile separate from the always-muted Live2D renderer.
        _profileDirectory = Path.Combine(profileDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "WebView2"), "Scenes");
        IsHitTestVisible = false;
        foreach (string relative in pack.Files)
        {
            string normalized = relative.Replace('\\', '/');
            if (!new[] { ".png", ".apng", ".webm", ".mp4", ".mp3", ".wav", ".ogg" }.Contains(Path.GetExtension(normalized).ToLowerInvariant())) continue;
            _files["/media/" + normalized] = ValidLocalPath(pack.RootDirectory, normalized);
        }
        string runtime = Path.Combine(AppContext.BaseDirectory, "assets", "scene-runtime");
        foreach (string relative in new[] { "index.html", "scene.css", "scene.js", "apng.js" })
            _files["/runtime/" + relative] = ValidLocalPath(runtime, relative);
        _browser = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            IsHitTestVisible = false, Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        Children.Add(_browser);
    }

    public async Task LoadAsync()
    {
        ThrowIfUnavailable();
        if (_loadStarted) { await _loaded.Task; return; }
        _loadStarted = true;
        try
        {
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(null, _profileDirectory, options).WaitAsync(TimeSpan.FromSeconds(25));
            ThrowIfUnavailable();
            _environment = environment;
            await _browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(25));
            ThrowIfUnavailable();
            var core = _browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.IsMuted = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ResourceRequested;
            core.NavigationStarting += (_, e) => { if (e.Uri != Entry) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, e) => HandleProcessFailure(e.ProcessFailedKind);
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) Fail(L.T("场景动画页面加载失败：", "The scene animation page failed to load: ") + e.WebErrorStatus); };
            core.WebMessageReceived += MessageReceived;
            core.Navigate(Entry);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
            ThrowIfUnavailable();
            Post(new { type = "pause", paused = _paused });
            Post(new { type = "audio-settings", enabled = _audioEnabled, volume = _audioVolume });
            SendViewportLayout();
            Post(new { type = "load", requestId = _loadId, pack = new { actions = _pack.Actions, anchorX = _pack.AnchorX, anchorY = _pack.AnchorY } });
            SendMotionFrame();
            await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            Fail(L.T("场景动画加载失败。", "The scene animation failed to load."), ex);
            _ready.TrySetException(ex); _loaded.TrySetException(ex);
            throw;
        }
    }

    // Returns when the first decoded frame is ready, allowing the parent to hide Live2D.
    public Task PlayAsync(string action, bool repeat) => StartAsync(action, repeat, preserveFrame: false);

    // Replace an automatic state without showing the base PNG between decoded frames.
    // The old request is superseded, so it cannot complete or fail the replacement.
    public Task SwitchAsync(string action, bool repeat) => StartAsync(action, repeat, preserveFrame: true);

    private async Task StartAsync(string action, bool repeat, bool preserveFrame)
    {
        ThrowIfUnavailable();
        if (!_pack.Actions.ContainsKey(action)) throw new ArgumentException(L.T("动作包不包含场景：", "The animation pack does not contain this scene: ") + action, nameof(action));
        await LoadAsync();
        ThrowIfUnavailable();
        if (IsPlaying && !preserveFrame) await StopAsync(true);
        ThrowIfUnavailable();
        _playing?.TrySetCanceled(); _completed?.TrySetCanceled();
        _requestId = Guid.NewGuid().ToString("N");
        _action = action;
        _audioStopped = false;
        _playing = NewCompletion(); _completed = NewCompletion();
        var waiting = _playing;
        IsPlaying = true;
        UpdateHostMute();
        Post(new { type = preserveFrame ? "switch" : "play", requestId = _requestId, action, repeat });
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(35)); }
        catch (TimeoutException)
        {
            if (ReferenceEquals(_playing, waiting)) { Post(new { type = "stop", requestId = _requestId, immediate = true }); Fail(L.T("场景动画解码超时。", "Scene animation decoding timed out.")); }
            throw;
        }
    }

    public async Task StopAsync(bool immediate = false)
    {
        if (_disposed || IsFailed || !IsPlaying) return;
        var completion = _completed;
        // Mute immediately on the host side, before an asynchronous stop message
        // or a graceful animation outro has a chance to run.
        SetHostMuted(true);
        _audioStopped = true;
        // Reduced motion or a hidden window must not wait on a frozen loop.
        if (_paused) immediate = true;
        Post(new { type = "stop", requestId = _requestId, immediate });
        ReleaseUnusedVideoSources();
        if (completion != null) await completion.Task.WaitAsync(TimeSpan.FromSeconds(immediate ? 5 : 245));
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        UpdateHostMute();
        if (!_disposed && !IsFailed && _runtimeReady) Post(new { type = "pause", paused });
    }

    // Physical movement owns these frames. Cache the request even while WebView
    // initializes or the next clip decodes, so a late first frame cannot restart
    // the crouch/grasp cycle after the character has already moved.
    public void SetMotionFrame(string? action, int frame)
    {
        if (action != null && (!_pack.Actions.TryGetValue(action, out var clip) || clip.Loop.Type == "video"))
            throw new ArgumentException(L.T("动作包不包含可逐帧控制的场景：", "The animation pack has no frame-controllable scene: ") + action, nameof(action));
        int lastFrame = action != null && _pack.Actions.TryGetValue(action, out var definition) && definition.Loop.Type == "frames"
            ? definition.Loop.Frames.Length - 1 : 299;
        frame = Math.Clamp(frame, 0, Math.Max(0, lastFrame));
        if (_motionAction == action && _motionFrame == frame) return;
        _motionAction = action;
        _motionFrame = frame;
        SendMotionFrame();
    }

    private void SendMotionFrame() => Post(new { type = "motionFrame", action = _motionAction, frame = _motionFrame });

    public void SetAudioSettings(bool enabled, double volume)
    {
        _audioEnabled = enabled;
        _audioVolume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0;
        UpdateHostMute();
        Post(new { type = "audio-settings", enabled = _audioEnabled, volume = _audioVolume });
    }

    internal void SetViewportLayout(double bodyHeight, double thoughtHeadroom)
    {
        _bodyViewportHeight = Math.Clamp(bodyHeight, 1, 2048);
        _thoughtHeadroom = Math.Clamp(thoughtHeadroom, 0, 1024);
        SendViewportLayout();
    }

    private void SendViewportLayout() => Post(new { type = "viewport-layout", bodyHeight = _bodyViewportHeight, thoughtHeadroom = _thoughtHeadroom });

    private void UpdateHostMute() => SetHostMuted(!_audioEnabled || _audioVolume <= 0 || _paused || !IsPlaying);

    private void SetHostMuted(bool muted)
    {
        if (_disposed || !_runtimeReady) return;
        try { _browser.CoreWebView2.IsMuted = muted; }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { Fail(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."), ex); }
    }

    private void Post(object message)
    {
        if (_disposed || IsFailed || !_runtimeReady) return;
        try { _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions)); }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { Fail(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."), ex); }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFailed) throw new InvalidOperationException(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."));
    }

    internal void HandleProcessFailure(CoreWebView2ProcessFailedKind kind)
    {
        if (!_disposed && !IsFailed && WebViewFailurePolicy.RequiresRecreation(kind))
            Fail(L.T("场景动画播放进程已停止：", "The scene animation process has stopped: ") + kind);
    }
    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || IsFailed) return;
        try
        {
            if (e.Source != Entry) return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            string? type = root.GetProperty("type").GetString();
            if (type == "ready") { _runtimeReady = true; _ready.TrySetResult(true); return; }
            string? request = root.TryGetProperty("requestId", out var id) ? id.GetString() : null;
            if (request == _loadId && type == "loaded") { _loaded.TrySetResult(true); return; }
            if (request != _requestId || string.IsNullOrEmpty(request)) return;
            if (type == "playing")
            {
                ReleaseUnusedVideoSources();
                _playing?.TrySetResult(true);
            }
            else if (type == "completed")
            {
                IsPlaying = false;
                UpdateHostMute();
                ReleaseVideoSources();
                // A stop while decoding can finish without ever producing a first frame.
                _playing?.TrySetCanceled(); _completed?.TrySetResult(true);
                Completed?.Invoke();
            }
            else if (type == "error") Fail(root.TryGetProperty("message", out var msg) ? msg.GetString() ?? L.T("场景动画加载失败。", "The scene animation failed to load.") : L.T("场景动画加载失败。", "The scene animation failed to load."));
        }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { Fail(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."), ex); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
    }

    private void Fail(string message, Exception? error = null)
    {
        if (_disposed || IsFailed) return;
        error ??= new InvalidOperationException(message);
        // The renderer may still be alive after a decode error; stop its audio
        // synchronously even if the host recreates the view on a later tick.
        if (_runtimeReady)
        {
            try { _browser.CoreWebView2.IsMuted = true; }
            catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { }
        }
        IsFailed = true; _runtimeReady = false; IsPlaying = false;
        ReleaseVideoSources();
        _ready.TrySetException(error); _loaded.TrySetException(error);
        _playing?.TrySetException(error); _completed?.TrySetException(error);
        // Hosts may dispose/recreate the WebView. Never run that inside a native
        // WebView2 event callback, and never send more than one terminal notice.
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { if (!_disposed) Failed?.Invoke(message); }));
    }

    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_disposed || IsFailed) return;
        Stream? stream = null;
        try
        {
            var uri = new Uri(e.Request.Uri);
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            bool allowed = !_disposed && (e.Request.Method == "GET" || e.Request.Method == "HEAD")
                && uri.GetLeftPart(UriPartial.Authority) == Origin && uri.Query.Length == 0 && uri.Fragment.Length == 0
                && !path.Contains('\\') && !path.Contains('\0') && _files.TryGetValue(path, out _);
            if (!allowed) { Respond(e, null, 403, "Forbidden", ""); return; }
            string file = _files[path];
            string root = path.StartsWith("/media/", StringComparison.Ordinal) ? _pack.RootDirectory : Path.Combine(AppContext.BaseDirectory, "assets", "scene-runtime");
            // Recheck before opening: imported files may have been replaced since validation.
            ValidLocalPath(root, Path.GetRelativePath(root, file).Replace('\\', '/'));
            string mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8", ".png" or ".apng" => "image/png",
                ".webm" => "video/webm", ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", _ => "application/octet-stream"
            };
            // The decoder can deliver a late seek after its source was cleared.
            // Do not let an idle/disposed action recreate a pinned video handle.
            bool streamingMedia = mime.StartsWith("video/", StringComparison.Ordinal) || mime.StartsWith("audio/", StringComparison.Ordinal);
            if (streamingMedia && (!IsPlaying || !CurrentVideoFiles().Contains(file)))
            {
                Respond(e, null, 410, "Gone", ""); return;
            }
            var input = SceneFileAccess.OpenChecked(file);
            stream = input;
            if (input.Length <= 0 || input.Length > 96L * 1024 * 1024) throw new InvalidDataException(L.T("动画资源为空或超过 96 MiB。", "The animation asset is empty or exceeds 96 MiB."));
            if (mime.StartsWith("audio/", StringComparison.Ordinal) && input.Length > 32L * 1024 * 1024) throw new InvalidDataException(L.T("音频资源超过 32 MiB。", "The audio asset exceeds 32 MiB."));
            long length = input.Length, start = 0, end = length - 1;
            string? range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
            if (range != null) RangeRequests++;
            if (range != null && !TryRange(range, length, out start, out end))
            {
                input.Dispose(); stream = null;
                Respond(e, null, 416, "Range Not Satisfiable", "Content-Range: bytes */" + length.ToString(CultureInfo.InvariantCulture)); return;
            }
            long count = end - start + 1;
            string headers = "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nAccept-Ranges: bytes\r\nContent-Length: " + count.ToString(CultureInfo.InvariantCulture);
            if (range != null) headers += $"\r\nContent-Range: bytes {start}-{end}/{length}";
            if (e.Request.Method == "HEAD") { input.Dispose(); stream = null; }
            else if (streamingMedia)
            {
                // One pinned source per file, regardless of how often the decoder
                // seeks or repeats. Each response owns only an independent cursor.
                if (_videoSources.TryGetValue(file, out var source)) input.Dispose();
                else { source = new SharedVideoSource(input); _videoSources.Add(file, source); }
                stream = new BoundedStream(source, start, count);
            }
            else
            {
                // WebView2 may retain its IStream after decoding. Images/scripts must
                // not retain a lock on the user's local source file for that lifetime.
                var bytes = new byte[checked((int)count)];
                input.Position = start; input.ReadExactly(bytes); input.Dispose();
                stream = new MemoryStream(bytes, writable: false);
            }
            Respond(e, stream, range == null ? 200 : 206, range == null ? "OK" : "Partial Content", headers);
            stream = null; // WebView2 owns the response stream until consumed.
        }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex))
        {
            stream?.Dispose(); Fail(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."), ex);
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            stream?.Dispose(); Program.Log(ex);
            // A failed response must not recursively touch an invalid Core.
            try { Respond(e, null, 404, "Not Found", ""); }
            catch (Exception responseError) when (WebViewFailurePolicy.IsUnavailable(responseError)) { Fail(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."), responseError); }
        }
    }

    private void Respond(CoreWebView2WebResourceRequestedEventArgs e, Stream? content, int status, string reason, string headers)
    {
        if (_disposed || IsFailed) { content?.Dispose(); return; }
        var environment = _environment ?? throw new InvalidOperationException(L.T("场景动画运行环境不可用。", "The scene animation runtime is unavailable."));
        e.Response = environment.CreateWebResourceResponse(content, status, reason, headers);
    }

    internal static bool TryRange(string header, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',')) return false;
        string[] fields = header[6..].Split('-');
        if (fields.Length != 2) return false;
        if (fields[0].Length == 0)
        {
            if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix); return true;
        }
        if (!long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length) return false;
        if (fields[1].Length > 0 && !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)) return false;
        end = Math.Min(end, length - 1); return end >= start;
    }

    private static string ValidLocalPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(x => x is "" or "." or "..")) throw new InvalidDataException(L.T("场景资源路径无效。", "Invalid scene asset path."));
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new FileNotFoundException(L.T("场景资源不存在。", "The scene asset does not exist."), full);
        for (string? cursor = full; cursor != null && !string.Equals(cursor, fullRoot, StringComparison.OrdinalIgnoreCase); cursor = Path.GetDirectoryName(cursor))
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(L.T("场景资源不允许链接或重解析点。", "Scene assets cannot use links or reparse points."));
        return full;
    }

    internal async Task<string> GetDiagnosticsAsync()
    {
        ThrowIfUnavailable();
        try { return await _browser.ExecuteScriptAsync("JSON.stringify(window.sceneDiagnostics())"); }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { Fail(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."), ex); throw; }
    }
    internal async Task<CharacterGeometry> CaptureContactGeometryAsync()
    {
        ThrowIfUnavailable();
        using var pixels = new MemoryStream();
        await CapturePreviewAsync(pixels);
        pixels.Position = 0;
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(pixels, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        if (_bodyViewportHeight is double bodyHeight && _thoughtHeadroom > 0 && ActualHeight > bodyHeight)
        {
            // Physical contact belongs to the body, never to its text panel.
            // Return body-normalized geometry just like the unexpanded view.
            int top = Math.Clamp((int)Math.Round(frame.PixelHeight * (ActualHeight - bodyHeight) / ActualHeight), 0, frame.PixelHeight - 1);
            var body = new System.Windows.Media.Imaging.CroppedBitmap(frame,
                new Int32Rect(0, top, frame.PixelWidth, frame.PixelHeight - top));
            return FootGeometry.FromBitmap(body);
        }
        return FootGeometry.FromBitmap(frame);
    }
    internal async Task CaptureAsync(string path)
    {
        ThrowIfUnavailable();
        using var stream = File.Create(path);
        await CapturePreviewAsync(stream);
    }
    private async Task CapturePreviewAsync(Stream stream)
    {
        ThrowIfUnavailable();
        try { await _browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { Fail(L.T("场景动画播放进程已停止。", "The scene animation process has stopped."), ex); throw; }
    }
    private void ReleaseVideoSources()
    {
        foreach (var source in _videoSources.Values) source.Dispose();
        _videoSources.Clear();
    }
    private HashSet<string> CurrentVideoFiles()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_action == null || !_pack.Actions.TryGetValue(_action, out var action)) return paths;
        if (action.Audio != null && !_audioStopped)
            paths.Add(Path.GetFullPath(Path.Combine(_pack.RootDirectory, action.Audio.File.Replace('/', Path.DirectorySeparatorChar))));
        foreach (var clip in new[] { action.Start, action.Loop, action.End })
            if (clip?.Type == "video" && clip.File != null)
                paths.Add(Path.GetFullPath(Path.Combine(_pack.RootDirectory, clip.File.Replace('/', Path.DirectorySeparatorChar))));
        return paths;
    }
    private void ReleaseUnusedVideoSources()
    {
        var keep = CurrentVideoFiles();
        foreach (string file in _videoSources.Keys.Where(file => !keep.Contains(file)).ToArray())
        {
            _videoSources[file].Dispose();
            _videoSources.Remove(file);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        SetHostMuted(true);
        if (_runtimeReady && !IsFailed) Post(new { type = "dispose" });
        _disposed = true; _runtimeReady = false; IsPlaying = false;
        _ready.TrySetCanceled(); _loaded.TrySetCanceled(); _playing?.TrySetCanceled(); _completed?.TrySetCanceled();
        try { _browser.Dispose(); }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { Program.Log(ex); }
        finally { ReleaseVideoSources(); Children.Clear(); _environment = null; }
    }

    private sealed class SharedVideoSource : IDisposable
    {
        private readonly FileStream _file;
        private readonly object _gate = new();
        private bool _disposed;
        public SharedVideoSource(FileStream file) => _file = file;
        public int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _file.Position = position;
                return _file.Read(buffer, offset, count);
            }
        }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _file.Dispose(); }
        }
    }

    private sealed class BoundedStream : Stream
    {
        private readonly SharedVideoSource _source;
        private readonly object _gate = new();
        private readonly long _length, _offset; private long _position; private bool _disposed;
        public BoundedStream(SharedVideoSource source, long offset, long length) { _source = source; _length = length; _offset = offset; }
        public override bool CanRead => !_disposed; public override bool CanSeek => !_disposed; public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get { lock (_gate) return _position; } set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int read = _source.ReadAt(_offset + _position, buffer, offset, (int)Math.Min(count, _length - _position));
                _position += read; return read;
            }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                long position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => _length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
                if (position < 0 || position > _length) throw new IOException("Seek outside media response range.");
                _position = position; return position;
            }
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { lock (_gate) _disposed = true; base.Dispose(disposing); }
    }
}
