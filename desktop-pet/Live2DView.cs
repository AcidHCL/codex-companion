using System;
using System.Collections.Generic;
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

// This browser renders only bundled code and validated model data. It has no Codex bridge.
internal sealed class Live2DView : Grid, IDisposable
{
    private const string Origin = "https://aipet.invalid";
    // Bind the allowed entry URL to the language at construction. Only the
    // bundled page is navigable; model resource URLs retain their strict rules.
    private readonly string Entry = Origin + "/runtime/index.html?lang=" + L.Language;
    private readonly WebView2CompositionControl _browser;
    private readonly string _profileDirectory;
    private CoreWebView2Environment? _environment;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _requestId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _paused;
    private bool _motionEnabled = true;
    private bool _runtimeReady;
    private bool _loadStarted;
    public bool ModelLoaded { get; private set; }
    public bool IsFailed { get; private set; }
    public event Action<string>? Failed;

    public Live2DView(Live2DModelInfo model, string? profileDirectory = null)
    {
        _profileDirectory = profileDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "WebView2");
        IsHitTestVisible = false;
        var catalog = new Live2DModelCatalog();
        foreach (string relative in catalog.GetValidatedFiles(model))
            _files["/model/" + relative] = Path.Combine(model.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        string runtime = Path.Combine(AppContext.BaseDirectory, "assets", "live2d-runtime");
        if (!Directory.Exists(runtime)) throw new FileNotFoundException(L.T("Live2D 运行文件缺失，请重新构建桌宠。", "Live2D runtime files are missing. Rebuild AI Pet."));
        foreach (string file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(runtime, file).Replace('\\', '/');
            if (new[] { ".html", ".js", ".wasm", ".css", ".vert", ".frag" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
                _files["/runtime/" + relative] = file;
        }
        _browser = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            IsHitTestVisible = false,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Children.Add(_browser);
    }

    public async Task LoadAsync(Live2DModelInfo model)
    {
        ThrowIfUnavailable();
        if (_loadStarted) { await _loaded.Task; return; }
        _loadStarted = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _profileDirectory).WaitAsync(TimeSpan.FromSeconds(25));
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
            core.WebMessageReceived += MessageReceived;
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) ReportFailure(L.T("Live2D 页面加载失败：", "The Live2D page failed to load: ") + e.WebErrorStatus);
            };
            core.Navigate(Entry);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
            ThrowIfUnavailable();
            // Apply cached preferences before load, and keep forwarding changes
            // while textures are loading so hidden/reduced-motion pets never flash.
            SetPaused(_paused);
            SetMotionEnabled(_motionEnabled);
            string relative = Path.GetRelativePath(model.RootDirectory, model.ModelPath).Replace('\\', '/');
            string url = Origin + "/model/" + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
            Post(new { type = "load", url, requestId = _requestId });
            await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(45));
            ThrowIfUnavailable();
            ModelLoaded = true;
            SetPaused(_paused);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            var error = new InvalidOperationException(L.T("未找到 Microsoft Edge WebView2 Runtime。安装后即可使用 Live2D；PNG 皮肤仍可使用。", "Microsoft Edge WebView2 Runtime was not found. Install it to use Live2D; PNG skins remain available."), ex);
            ReportFailure(error.Message, error);
            throw error;
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            ReportFailure(L.T("Live2D 模型加载失败。", "The Live2D model failed to load."), ex);
            throw;
        }
    }

    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_disposed || IsFailed) return;
        Stream? content = null;
        try
        {
            var uri = new Uri(e.Request.Uri);
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            bool allowed = !_disposed && e.Request.Method == "GET" && uri.GetLeftPart(UriPartial.Authority) == Origin
                && !path.Contains('\\') && !path.Contains('\0') && _files.TryGetValue(path, out _);
            if (!allowed) { Respond(e, null, 403, "Forbidden", ""); return; }
            string file = _files[path];
            string mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8", ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
                ".vert" or ".frag" => "text/plain; charset=utf-8",
                ".css" => "text/css; charset=utf-8", ".wasm" => "application/wasm", _ => "application/octet-stream"
            };
            content = new MemoryStream(File.ReadAllBytes(file), false);
            string headers = "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
            Respond(e, content, 200, "OK", headers);
            content = null;
        }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex))
        {
            content?.Dispose(); ReportFailure(L.T("Live2D 渲染进程已停止。", "The Live2D renderer has stopped."), ex);
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            content?.Dispose(); Program.Log(ex);
            try { Respond(e, null, 404, "Not Found", ""); }
            catch (Exception responseError) when (WebViewFailurePolicy.IsUnavailable(responseError)) { ReportFailure(L.T("Live2D 渲染进程已停止。", "The Live2D renderer has stopped."), responseError); }
        }
    }

    private void Respond(CoreWebView2WebResourceRequestedEventArgs e, Stream? content, int status, string reason, string headers)
    {
        if (_disposed || IsFailed) { content?.Dispose(); return; }
        var environment = _environment ?? throw new InvalidOperationException(L.T("Live2D 运行环境不可用。", "The Live2D runtime is unavailable."));
        e.Response = environment.CreateWebResourceResponse(content, status, reason, headers);
    }

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
            if (root.TryGetProperty("requestId", out var request) && !string.IsNullOrEmpty(request.GetString()) && request.GetString() != _requestId) return;
            if (type == "loaded") _loaded.TrySetResult(true);
            if (type == "error")
                ReportFailure(root.TryGetProperty("message", out var message) ? message.GetString() ?? L.T("模型加载失败。", "The model failed to load.") : L.T("模型加载失败。", "The model failed to load."));
        }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { ReportFailure(L.T("Live2D 渲染进程已停止。", "The Live2D renderer has stopped."), ex); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
    }

    internal void HandleProcessFailure(CoreWebView2ProcessFailedKind kind)
    {
        if (!_disposed && !IsFailed && WebViewFailurePolicy.RequiresRecreation(kind))
            ReportFailure(L.T("Live2D 渲染进程已停止：", "The Live2D renderer has stopped: ") + kind);
    }

    private void ReportFailure(string message, Exception? error = null)
    {
        if (_disposed || IsFailed) return;
        error ??= new InvalidOperationException(message);
        IsFailed = true; _runtimeReady = false; ModelLoaded = false;
        _ready.TrySetException(error);
        _loaded.TrySetException(error);
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { if (!_disposed) Failed?.Invoke(message); }));
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFailed) throw new InvalidOperationException(L.T("Live2D 渲染进程已停止。", "The Live2D renderer has stopped."));
    }

    private void Post(object message)
    {
        if (_disposed || IsFailed || !_runtimeReady) return;
        try { _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { ReportFailure(L.T("Live2D 渲染进程已停止。", "The Live2D renderer has stopped."), ex); }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Post(new { type = "pause", paused });
    }

    public void SetMotionEnabled(bool enabled)
    {
        _motionEnabled = enabled;
        Post(new { type = "motion", enabled });
    }

    internal async Task<string> GetDiagnosticsAsync()
    {
        ThrowIfUnavailable();
        try { return await _browser.ExecuteScriptAsync("JSON.stringify(window.petDiagnostics())"); }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { ReportFailure(L.T("Live2D 渲染进程已停止。", "The Live2D renderer has stopped."), ex); throw; }
    }

    internal async Task<CharacterGeometry> CaptureContactGeometryAsync()
    {
        ThrowIfUnavailable();
        using var pixels = new MemoryStream();
        await CapturePreviewAsync(pixels);
        pixels.Position = 0;
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(pixels, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
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
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { ReportFailure(L.T("Live2D 渲染进程已停止。", "The Live2D renderer has stopped."), ex); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _runtimeReady = false; ModelLoaded = false;
        _ready.TrySetCanceled();
        _loaded.TrySetCanceled();
        try { _browser.Dispose(); }
        catch (Exception ex) when (WebViewFailurePolicy.IsUnavailable(ex)) { Program.Log(ex); }
        finally { Children.Clear(); _environment = null; }
    }
}
