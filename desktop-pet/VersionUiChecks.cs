using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// Isolated UI-only entry point: no mutex, live Codex transport, user settings
// load/save, or interaction with existing app windows. Only our own windows open.
internal static class VersionUiChecks
{
    internal static int Run()
    {
        string report = Path.Combine(AppContext.BaseDirectory, "version-ui-validation.json");
        string previews = Path.Combine(AppContext.BaseDirectory, "version-ui-previews");
        Directory.CreateDirectory(previews);
        int assertions = 0, result = 1;
        bool finished = false;
        var windows = new List<Window>();
        var samples = new List<object>();
        var fake = new SyntheticConnection();
        CodexMonitor? monitor = null;
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };

        void Check(bool condition, string reason)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(reason);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    passed, version = AppVersion.Number, assertions, error, samples,
                    syntheticTransport = true, realAccountRequests = 0, userSettingsWrites = 0,
                    fakeRequests = fake.Calls, fakeMessageReads = 0
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                for (int i = windows.Count - 1; i >= 0; i--) windows[i].Close();
                monitor?.Dispose(); result = passed ? 0 : 1; app.Shutdown(result);
            }
        }
        async Task ShowAsync(Window window, bool minimumSize = true)
        {
            windows.Add(window);
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = false;
            if (minimumSize) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
            window.Show();
            await SettleAsync(window);
        }
        void Verify(Window window, string name)
        {
            Check(window.Title.EndsWith(AppVersion.Label, StringComparison.Ordinal), name + ": incorrect title version.");
            var marks = Descendants<TextBlock>(window).Where(x => x.Name == "AppVersionLabel").ToArray();
            Check(marks.Length == 1, name + ": must have exactly one version label.");
            var mark = marks.Single();
            Check(mark.IsVisible && mark.Opacity > 0 && mark.Text.Contains(AppVersion.Label, StringComparison.Ordinal), name + ": version is not visible.");
            var text = new FormattedText(mark.Text, CultureInfo.CurrentUICulture, mark.FlowDirection,
                new Typeface(mark.FontFamily, mark.FontStyle, mark.FontWeight, mark.FontStretch),
                mark.FontSize, mark.Foreground, VisualTreeHelper.GetDpi(mark).PixelsPerDip);
            Check(mark.ActualWidth + 1 >= text.WidthIncludingTrailingWhitespace && mark.ActualHeight + 1 >= text.Height,
                name + ": version text is clipped.");
            var content = (FrameworkElement)window.Content;
            var bounds = mark.TransformToAncestor(content).TransformBounds(new Rect(mark.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1,
                name + ": version lies outside the window content.");
            // Render the common window coordinate space. Rendering a margined
            // Content visual into its own ActualWidth/Height retains its offset
            // but drops the right/bottom margins and crops those edges.
            int captureWidth = (int)Math.Ceiling(window.ActualWidth);
            int captureHeight = (int)Math.Ceiling(window.ActualHeight);
            var contentBounds = content.TransformToAncestor(window).TransformBounds(new Rect(content.RenderSize));
            Check(contentBounds.Left >= -1 && contentBounds.Top >= -1 &&
                contentBounds.Right <= captureWidth + 1 && contentBounds.Bottom <= captureHeight + 1,
                name + ": preview canvas does not contain the full content visual.");
            var png = new PngBitmapEncoder();
            var bitmap = new RenderTargetBitmap(captureWidth, captureHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(previews, name + ".png"))) png.Save(stream);
            samples.Add(new { name, title = window.Title, label = mark.Text, width = window.ActualWidth, height = window.ActualHeight, captureWidth, captureHeight,
                labelBounds = new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height } });
        }

        timeout.Tick += (_, _) => Finish(false, "VERSION_UI_TIMEOUT");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, "VERSION_UI_DISPATCHER_" + e.Exception.GetType().Name); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                // A static skin deliberately avoids starting WebView2/renderers.
                var settings = new DesktopSettings
                {
                    SkinId = skins.First(s => s.Id != "deepseek").Id, Size = 160,
                    Left = 30, Top = 50, Topmost = false, MotionEnabled = false, MovementEnabled = false,
                    NotificationsEnabled = false, CodexMessagesEnabled = false, RefreshSeconds = 120
                };
                var pet = new PetWindow(skins, settings, persist: false);
                await ShowAsync(pet, minimumSize: false);
                Check(pet.Width == 184 && pet.Height == 248, "Version label changed the 160px pet window dimensions.");
                Verify(pet, "pet-160-status");
                var foot = pet.VisibleFootPixels;
                var characterBounds = pet.CharacterVisibleBoundsPixels;
                settings.ShowStatusBadge = false;
                pet.SetCodexStatus(CodexSnapshot.Empty);
                await SettleAsync(pet);
                Verify(pet, "pet-160-status-hidden");
                pet.Say("版本标签应与提示气泡分别显示。");
                await SettleAsync(pet);
                Verify(pet, "pet-160-speech");
                Check(pet.VisibleFootPixels == foot && pet.CharacterVisibleBoundsPixels == characterBounds,
                    "Status/version/speech overlays changed character contact geometry.");
                var petMark = Descendants<TextBlock>(pet).Single(x => x.Name == "AppVersionLabel");
                Check(!petMark.IsHitTestVisible, "The passive pet version label must not accept mouse interaction.");
                var petRoot = (FrameworkElement)pet.Content;
                var versionBounds = petMark.TransformToAncestor(petRoot).TransformBounds(new Rect(petMark.RenderSize));
                var speech = Descendants<TextBlock>(pet).Single(x => x.Text == "版本标签应与提示气泡分别显示。");
                var speechBounds = speech.TransformToAncestor(petRoot).TransformBounds(new Rect(speech.RenderSize));
                Check(!versionBounds.IntersectsWith(speechBounds), "Version label overlaps the speech text.");

                var preferences = new SettingsWindow(pet, skins);
                await ShowAsync(preferences); Verify(preferences, "settings-minimum"); preferences.Hide();
                monitor = new CodexMonitor(settings, fake, persistSettings: false);
                await monitor.RefreshAsync(true);
                var dashboard = new CodexDashboard(monitor, settings, () => { });
                await ShowAsync(dashboard); Verify(dashboard, "dashboard-minimum"); dashboard.Hide();
                var live2d = new Live2DLibraryWindow(pet);
                await ShowAsync(live2d); Verify(live2d, "live2d-minimum"); live2d.Hide();
                var scenes = new SceneAnimationLibraryWindow(pet);
                await ShowAsync(scenes); Verify(scenes, "scenes-minimum"); scenes.Hide();
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.Message); }
        };
        app.Run();
        return result;
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private sealed class SyntheticConnection : ICodexConnection
    {
        public string ConnectionDescription => "版本布局验证 · 合成连接";
        public string? CallerThreadId => null;
        public bool IsPairingRevoked => false;
        public bool HasSavedPairing => true;
        public int Calls { get; private set; }
        public bool ResumeSavedPairing() => true;
        public void SetMessageAccess(bool enabled) { if (enabled) throw new InvalidOperationException("Version UI test cannot read messages."); }
        public Task EnsureConnectedAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            string json = tool switch
            {
                "list_threads" => "{\"threads\":[]}",
                "get_usage_limits" => "{\"rateLimits\":{\"credits\":{\"balance\":\"1200\"},\"primary\":{\"windowDurationMins\":300,\"usedPercent\":20},\"secondary\":{\"windowDurationMins\":10080,\"usedPercent\":35}}}",
                _ => throw new InvalidOperationException("Unexpected synthetic request: " + tool)
            };
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult(doc.RootElement.Clone());
        }
        public void Disconnect() { }
        public Task ForgetAsync() => throw new InvalidOperationException("Version UI test cannot alter pairing.");
        public void Dispose() { }
    }
}
