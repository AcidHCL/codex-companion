using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet.ScreenUnderstanding;

internal static class ScreenCaptureChecks
{
    // The only actual UI/image in these checks is a purpose-built test window.
    // Raw captures stay in memory and are never printed, saved, or sent to a model.
    internal static async Task<object> RunAsync()
    {
        if (Application.Current == null || !Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException("Run capture checks on the WPF fixture dispatcher.");
        var checks = new List<string>();
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Screen capture: " + name); checks.Add(name); }

        var valid = new ScreenCaptureRequest("fixture", "123:ABCD", true, true, false, Array.Empty<string>());
        Check(ScreenCapturePolicy.ValidRequest(valid), "bounded_capture_request_is_accepted");
        Check(!ScreenCapturePolicy.ValidRequest(valid with { App = @"C:\private\fixture.exe" }) &&
            !ScreenCapturePolicy.ValidRequest(valid with { WindowKey = "123:ABCD:MORE" }) &&
            !ScreenCapturePolicy.ValidRequest(valid with { WindowKey = "0:ABCD" }) &&
            !ScreenCapturePolicy.ValidRequest(valid with { IncludeText = false, IncludeImage = false }),
            "paths_invalid_window_keys_and_empty_capture_scope_are_rejected_before_native_access");
        Check(!ScreenCapturePolicy.ValidRequest(valid with { ExcludedApps = Enumerable.Repeat("fixture", 65).ToArray() }),
            "exclusion_count_is_bounded");
        Check(ScreenCapturePolicy.BoundText(new string('x', 5000)).Length == 4000 &&
            ScreenCapturePolicy.BoundText("a\0b\r\ntext") == "ab\ntext" && ScreenCapturePolicy.BoundText("x", 0) == "",
            "transient_text_is_bounded_and_control_characters_removed");
        string worstCaseBase64 = new string('+', (ScreenCapturePolicy.MaximumImageBytes + 2) / 3 * 4);
        var maximumWireResult = new ScreenCaptureResult(true, "captured", new string('中', 4000), worstCaseBase64, valid.WindowKey, new string('A', 64));
        Check(JsonSerializer.Serialize(maximumWireResult, ScreenCapturePolicy.WireOptions).Length < WindowsScreenWindowCapture.MaximumReplyCharacters,
            "maximum_image_and_chinese_text_fit_bounded_utf8_pipe_without_html_base64_expansion");
        using (var large = new Bitmap(2000, 1000))
        {
            using (var paint = Graphics.FromImage(large))
            { paint.Clear(System.Drawing.Color.White); paint.FillRectangle(System.Drawing.Brushes.Blue, 0, 0, 900, 900); }
            string? encoded = ScreenCaptureNative.Encode(large);
            Check(encoded != null, "fixture_image_encodes_as_jpeg");
            byte[] jpeg = Convert.FromBase64String(encoded!);
            using var bytes = new MemoryStream(jpeg);
            using var resized = System.Drawing.Image.FromStream(bytes);
            Check(jpeg.Length <= ScreenCapturePolicy.MaximumImageBytes && resized.Width == 1280 && resized.Height == 640,
                "image_is_downscaled_before_transport_and_bounded_to_one_mebibyte");
            var result = new ScreenCaptureResult(true, "captured", "fixture", encoded, valid.WindowKey, new string('A', 64));
            Check(ScreenCapturePolicy.ValidResult(result, valid) &&
                !ScreenCapturePolicy.ValidResult(result with { WindowKey = "321:ABCD" }, valid) &&
                !ScreenCapturePolicy.ValidResult(result with { ApplicationIdentity = "raw-path" }, valid) &&
                !ScreenCapturePolicy.ValidResult(result with { ImageBase64 = "not-base64" }, valid),
                "result_protocol_rejects_wrong_window_raw_identity_and_invalid_image");
        }
        using (var solid = new Bitmap(50, 50))
        {
            using var paint = Graphics.FromImage(solid); paint.Clear(System.Drawing.Color.Black);
            Check(ScreenCaptureNative.Blank(solid), "black_capture_is_not_presented_as_valid_visual_evidence");
        }
        bool bounded = false;
        using (var input = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(new string('x', 101)))))
        {
            try { await WindowsScreenWindowCapture.ReadBoundedAsync(input, 100, CancellationToken.None); }
            catch (InvalidDataException) { bounded = true; }
        }
        Check(bounded, "worker_protocol_stream_rejects_oversized_payloads");
        await CheckWorkerProtocolAsync(Check);
        var capture = new WindowsScreenWindowCapture();
        Check(!(await capture.CaptureAsync(valid with { WindowKey = "bad" }, CancellationToken.None)).Success,
            "invalid_requests_do_not_launch_a_worker");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool cancelled = false;
            try { await capture.CaptureAsync(valid, canceled.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancellation_before_capture_skips_native_work");
        }

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "VISIBLE_CAPTURE_FIXTURE_6427", FontSize = 22, Foreground = System.Windows.Media.Brushes.DarkBlue });
        content.Children.Add(new TextBlock { Text = "HIDDEN_CAPTURE_FIXTURE_9831", Visibility = Visibility.Collapsed });
        content.Children.Add(new TextBox { Text = "Visible fixture editor content", Height = 42, Margin = new Thickness(0, 14, 0, 8) });
        content.Children.Add(new System.Windows.Shapes.Rectangle { Height = 120, Fill = System.Windows.Media.Brushes.CornflowerBlue });
        var password = new PasswordBox { Password = "FIXTURE_PASSWORD_MUST_NEVER_ESCAPE", Height = 32, Visibility = Visibility.Collapsed };
        content.Children.Add(password);
        var window = new Window
        {
            Title = "AI Pet capture test fixture", Width = 650, Height = 380, Content = content,
            Background = System.Windows.Media.Brushes.White, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        try
        {
            window.Show(); window.Activate();
            await Settle(window);
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            string key = Environment.ProcessId + ":" + hwnd.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            string app = Process.GetCurrentProcess().ProcessName;
            var request = new ScreenCaptureRequest(app, key, true, true, false, Array.Empty<string>());
            Check(ScreenCaptureNative.Native.GetForegroundWindow() == hwnd,
                "native_fixture_owns_foreground_before_any_capture");
            ScreenCaptureResult actual = await Task.Run(() => ScreenCaptureNative.Capture(request, fixture: true));
            Check(actual.Success, "own_fixture_window_text_and_image_capture_succeeds_" + actual.Status);
            Check(actual.Text.Contains("VISIBLE_CAPTURE_FIXTURE_6427", StringComparison.Ordinal) &&
                !actual.Text.Contains("HIDDEN_CAPTURE_FIXTURE_9831", StringComparison.Ordinal) &&
                !actual.Text.Contains("FIXTURE_PASSWORD", StringComparison.Ordinal),
                "visible_ui_text_is_read_without_hidden_or_password_content");
            Check(actual.ImageBase64 != null && actual.ApplicationIdentity.Length == 64 &&
                actual.WindowKey == key && !actual.ApplicationIdentity.Contains(app, StringComparison.OrdinalIgnoreCase),
                "only_fixture_client_pixels_and_hashed_identity_are_returned");
            ScreenCaptureResult textOnly = await Task.Run(() => ScreenCaptureNative.Capture(request with { IncludeImage = false }, fixture: true));
            Check(textOnly.Success && textOnly.ImageBase64 == null && textOnly.Text.Length > 0,
                "text_only_scope_never_returns_image_data");
            ScreenCaptureResult imageOnly = await Task.Run(() => ScreenCaptureNative.Capture(request with { IncludeText = false }, fixture: true));
            Check(imageOnly.Success && imageOnly.Text.Length == 0 && imageOnly.ImageBase64 != null,
                "image_only_scope_still_checks_protected_controls_without_returning_text");
            ScreenCaptureResult excluded = await Task.Run(() => ScreenCaptureNative.Capture(request with { ExcludedApps = new[] { app } }, fixture: true));
            Check(!excluded.Success && excluded.Status == "excluded" && excluded.Text.Length == 0 && excluded.ImageBase64 == null,
                "excluded_fixture_is_rejected_before_accessibility_or_pixels");
            password.Visibility = Visibility.Visible; password.Focus(); await Settle(window);
            ScreenCaptureResult protectedResult = await Task.Run(() => ScreenCaptureNative.Capture(request, fixture: true));
            Check(!protectedResult.Success && protectedResult.Status == "protected_content" &&
                protectedResult.Text.Length == 0 && protectedResult.ImageBase64 == null,
                "visible_password_field_refuses_the_entire_capture");
            window.Hide(); await Task.Delay(60);
            ScreenCaptureResult stale = await Task.Run(() => ScreenCaptureNative.Capture(request, fixture: true));
            Check(!stale.Success && stale.ImageBase64 == null && stale.Text.Length == 0,
                "hidden_or_changed_foreground_never_falls_back_to_some_other_window");
        }
        finally { window.Close(); }
        return new { passed = true, assertions = checks.Count, checks, modelRequests = 0,
            capturedOnlyOwnFixture = true, capturedUserDesktop = false, savedImagesOrText = false };
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(120);
    }

    private static async Task CheckWorkerProtocolAsync(Action<bool, string> check)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "DesktopPet.exe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var worker = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
            }
        };
        worker.StartInfo.ArgumentList.Add("--screen-capture-worker");
        check(worker.Start(), "isolated_worker_can_start_without_the_pet_application");
        try
        {
            using var job = ScreenCaptureJob.Attach(worker);
            check(job != null, "worker_is_owned_by_a_kill_on_close_job");
            // Invalid input is rejected before any native window read. This
            // proves actual redirected IPC/early CLI routing without spying on
            // whichever personal application is currently in the foreground.
            await worker.StandardInput.WriteAsync("{\"App\":\"fixture\",\"WindowKey\":\"bad\",\"IncludeText\":true,\"IncludeImage\":true,\"AllowFullscreen\":false,\"ExcludedApps\":[]}".AsMemory(), timeout.Token);
            worker.StandardInput.Close();
            Task<string> output = WindowsScreenWindowCapture.ReadBoundedAsync(worker.StandardOutput, 4096, timeout.Token);
            Task<string> error = WindowsScreenWindowCapture.ReadBoundedAsync(worker.StandardError, 4096, timeout.Token);
            await worker.WaitForExitAsync(timeout.Token);
            var result = JsonSerializer.Deserialize<ScreenCaptureResult>(await output);
            check(worker.ExitCode == 0 && string.IsNullOrEmpty(await error) && result is { Success: false, Status: "invalid_request", Text.Length: 0, ImageBase64: null },
                "worker_stdout_is_only_bounded_protocol_and_invalid_input_never_captures");
        }
        finally
        {
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
        }
    }
}
