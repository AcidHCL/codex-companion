using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// A synthetic, non-persistent companion and an in-memory model exercise the
// real attached surfaces without touching an existing pet, Codex, or Ollama.
internal static class PetCompanionUiChecks
{
    public static int Run()
    {
        string evidence = Path.Combine(AppContext.BaseDirectory, "pet-companion-ui-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        var captures = new List<string>();
        var samples = new List<object>();
        PetWindow? pet = null;
        PetCompanionUi? companion = null;
        OllamaChatWindow? chat = null;
        OllamaChatWindow? compactChat = null;
        var client = new SyntheticClient();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        int result = 1, settingsClicks = 0, dashboardClicks = 0, chatClicks = 0;
        bool finished = false;
        string phase = "placement_policy";

        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        void Finish(bool passed, string? error = null)
        {
            if (finished) return;
            finished = true; timeout.Stop();
            try { compactChat?.Shutdown(); companion?.Dispose(); chat?.Shutdown(); pet?.Close(); client.Dispose(); }
            catch (Exception ex) { passed = false; error = "cleanup_" + ex.GetType().Name; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "pet-companion-ui-validation.json"), JsonSerializer.Serialize(new
            {
                passed, version = AppVersion.Number, language = L.Language, assertions = checks.Count, checks, phase, error, evidence, captures, samples,
                syntheticTransport = true, realAccountRequests = 0, realModelRequests = 0, userSettingsWrites = 0,
                userWindowsMoved = 0, operatingSystemInputInjected = false, compositeCapturesContainOnlyTestWindows = true,
                settingsClicks, dashboardClicks, chatClicks, imageRequests = client.Requests.Count(request => request.Any(message => message.Images?.Count > 0)),
                clientDisposed = client.Disposed
            }, new JsonSerializerOptions { WriteIndented = true }));
            result = passed ? 0 : 1;
            app.Shutdown(result);
        }
        timeout.Tick += (_, _) => Finish(false, "companion_ui_timeout");
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Finish(false, e.Exception.GetType().Name + ": " + e.Exception.Message); };
        app.Startup += async (_, _) =>
        {
            timeout.Start();
            try
            {
                CheckPlacement(Require, samples);
                phase = "isolated_pet";
                var skins = SkinCatalog.Load(AppContext.BaseDirectory);
                var settings = new DesktopSettings
                {
                    SkinId = skins.First(skin => skin.Id != "deepseek").Id, Size = 240,
                    Topmost = false, MotionEnabled = false, MovementEnabled = false, ClickThrough = false,
                    CodexEnabled = false, CodexMessagesEnabled = false, NotificationsEnabled = false, WebSearchEnabled = false
                };
                pet = new PetWindow(skins, settings, persist: false,
                    motionPreferences: () => new MotionPreferences(true, false),
                    querySession: () => new WindowsSessionSnapshot(42, false, true, false), subscribeToSystemEvents: false,
                    movementPlatformProvider: handle => new DesktopPlatformSnapshot(new[] { NativeMethods.GetWindowWorkAreaPixels(handle) }, Array.Empty<DesktopPlatform>()));
                pet.Show();
                await UntilAsync(() => pet.IsLoaded && pet.MovementController != null && pet.CompanionSurfacesAvailable, "pet_ready");
                var handle = new WindowInteropHelper(pet).Handle;
                var work = NativeMethods.GetWindowWorkAreaPixels(handle);
                var localBody = pet.CharacterVisibleBoundsPixels;
                double x = work.Left + work.Width * .65 - localBody.Left;
                double y = work.Top + work.Height * .55 - localBody.Top;
                NativeMethods.SetWindowPositionPixels(handle, (int)Math.Round(x), (int)Math.Round(y));
                pet.ClampPosition();
                await SettleAsync(pet);
                chat = new OllamaChatWindow(settings, () => "测试桌宠", pet.SetChatBusy, _ => { }, () => { }, client)
                { ShowActivated = false, ShowInTaskbar = false };
                companion = new PetCompanionUi(pet,
                    () => { chatClicks++; companion!.ShowChat(chat); },
                    () => settingsClicks++, () => dashboardClicks++);

                phase = "action_rail";
                Point initialFoot = WorldFoot(pet);
                Rect initialBody = Body(pet);
                companion.ToggleActions();
                await UntilAsync(() => companion.ActionsWindow?.IsVisible == true, "rail_visible");
                var rail = companion.ActionsWindow!;
                await SettleAsync(rail);
                Require(companion.IsOpen && pet.IsCompanionOpen, "opening_rail_marks_companion_open");
                Require(rail.Owner == pet && rail.WindowStyle == WindowStyle.None && !rail.ShowInTaskbar, "rail_is_owned_borderless_without_taskbar_entry");
                Require((WorldFoot(pet) - initialFoot).Length <= 1.5 && SameRect(Body(pet), initialBody, 1.5), "opening_actions_preserves_character_geometry_and_feet");
                Require(Contains(work, Bounds(rail), 2), "rail_stays_in_current_monitor_work_area");
                foreach (string name in new[] { "chat", "settings", "dashboard", "more" })
                    Require(Named<Button>(rail, "PetAction_" + name).IsVisible, "rail_" + name + "_icon_is_visible");
                CaptureComposite(Path.Combine(evidence, "pet-and-actions.png"), pet, rail); captures.Add("pet-and-actions.png");
                rail.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rail)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Require(!rail.IsVisible && !pet.IsCompanionOpen, "escape_closes_action_rail_and_releases_interaction");
                companion.ToggleActions(); await UntilAsync(() => rail.IsVisible, "rail_reopens_after_escape");
                Click(Named<Button>(rail, "PetAction_settings"));
                Require(settingsClicks == 1 && dashboardClicks == 0, "settings_icon_routes_only_settings_action");
                if (companion.ActionsWindow?.IsVisible != true) companion.ToggleActions();
                await UntilAsync(() => companion.ActionsWindow?.IsVisible == true, "rail_reopen_settings");
                Click(Named<Button>(companion.ActionsWindow!, "PetAction_dashboard"));
                Require(dashboardClicks == 1 && settingsClicks == 1, "dashboard_icon_routes_only_codex_dashboard");
                if (companion.ActionsWindow?.IsVisible != true) companion.ToggleActions();
                await UntilAsync(() => companion.ActionsWindow?.IsVisible == true, "rail_reopen_dashboard");
                Click(Named<Button>(companion.ActionsWindow!, "PetAction_chat"));
                await UntilAsync(() => chat.IsVisible, "attached_chat_visible");
                await SettleAsync(chat);
                Require(chatClicks == 1 && chat.Owner == pet && chat.WindowStyle == WindowStyle.None && !chat.ShowInTaskbar, "chat_icon_opens_owned_borderless_panel");
                Require(companion.IsOpen && pet.IsCompanionOpen, "open_chat_retains_companion_interaction_state");
                long chatStyle = GetWindowLongPtr(new WindowInteropHelper(chat).Handle, -20).ToInt64();
                Require((chatStyle & (0x08000000L | 0x00000020L)) == 0, "chat_accepts_keyboard_and_pointer_input");
                Require((WorldFoot(pet) - initialFoot).Length <= 1.5, "opening_attached_chat_does_not_move_pet_feet");
                Require(Contains(NativeMethods.GetWindowWorkAreaPixels(handle), Bounds(chat), 2), "chat_is_inside_monitor_work_area");

                phase = "chat_content";
                await UntilAsync(() => Named<ComboBox>(chat, "OllamaModelSelector").IsEnabled, "attached_model_discovery_ready");
                Require(Named<ComboBox>(chat, "OllamaModelSelector").Items.Cast<OllamaModelChoice>().Any(choice => choice.Id == SyntheticClient.ThirdModel), "attached_selector_shows_dynamic_server_model");
                Require(Named<Button>(chat, "OllamaConnectionSettings").IsVisible, "attached_panel_exposes_server_connection_settings");
                var input = Named<TextBox>(chat, "OllamaChatInput");
                input.Text = "这是附属聊天面板的合成测试。";
                Require(Named<Button>(chat, "OllamaSend").IsEnabled && !input.IsReadOnly, "attached_composer_accepts_text_and_enables_send");
                await chat.SendAsync();
                Require(client.Requests.Count == 1 && client.Completed == 1 && Named<TextBlock>(chat, "OllamaChatStatus").Text.StartsWith(L.T("回复完成", "Reply complete"), StringComparison.Ordinal), "attached_chat_streams_and_completes_synthetic_answer");
                Require(Descendants<TextBox>(chat).Any(text => text.IsReadOnly && text.Text.Contains("附属面板已收到", StringComparison.Ordinal)), "assistant_output_is_visible_in_attached_panel");
                string imagePath = Path.Combine(evidence, "attachment-fixture.png");
                CreateImage(imagePath);
                await chat.AddImageFilesAsync(new[] { imagePath });
                Require(Named<WrapPanel>(chat, "OllamaImagePreviews").Children.Count == 1 && chat.AllowDrop, "attached_panel_accepts_image_preparation_and_enables_drop");
                input.Text = "请看看这张测试图片。";
                await chat.SendAsync();
                Require(client.Completed == 2 && client.Requests[^1].Count == 4 && client.Requests[^1][^1].Images?.Count == 1, "attached_image_send_preserves_prior_conversation");
                byte[] sentImage = Convert.FromBase64String(client.Requests[^1][^1].Images![0]);
                Require(sentImage.Length > 8 && sentImage[0] == 137 && sentImage[1] == 80, "attached_image_is_sent_as_png_bytes");
                await SettleAsync(chat);
                RequireControlsFit(chat, Require);
                CaptureComposite(Path.Combine(evidence, "pet-and-chat.png"), pet, chat); captures.Add("pet-and-chat.png");

                phase = "follow_position";
                Rect beforePanel = Bounds(chat);
                Rect beforePet = Bounds(pet);
                double moveX = beforePet.Left + 90;
                double moveY = beforePet.Top - 50;
                NativeMethods.SetWindowPositionPixels(handle, (int)Math.Round(moveX), (int)Math.Round(moveY));
                await SettleAsync(chat);
                await Task.Delay(120);
                Rect movedPanel = Bounds(chat);
                Require(Math.Abs(movedPanel.Left - beforePanel.Left) >= 20 || Math.Abs(movedPanel.Top - beforePanel.Top) >= 20, "attached_chat_follows_pet_native_movement");
                Require(Contains(NativeMethods.GetWindowWorkAreaPixels(handle), movedPanel, 2), "moving_pet_keeps_chat_on_screen");
                Point footBeforeResize = WorldFoot(pet);
                pet.SetSize(320);
                await SettleAsync(chat);
                companion.RefreshPlacement();
                Require(Contains(NativeMethods.GetWindowWorkAreaPixels(handle), Bounds(chat), 2), "resizing_pet_repositions_attached_chat_safely");
                Require((WorldFoot(pet) - footBeforeResize).Length <= 1.5, "attached_ui_does_not_change_resize_foot_invariant");
                pet.SetTopmost(true); await SettleAsync(chat);
                Require(chat.Topmost, "attached_chat_tracks_pet_topmost_enabled");
                pet.SetTopmost(false); await SettleAsync(chat);
                Require(!chat.Topmost, "attached_chat_tracks_pet_topmost_disabled");

                phase = "compact_composer";
                companion.HideAll();
                compactChat = new OllamaChatWindow(settings, () => "测试桌宠", _ => { }, _ => { }, () => { }, new SyntheticClient())
                    { Owner = pet, ShowActivated = false, ShowInTaskbar = false, Width = 320, Height = 360 };
                compactChat.Show();
                await UntilAsync(() => Named<ComboBox>(compactChat, "OllamaModelSelector").IsEnabled, "compact_model_discovery_ready");
                await compactChat.AddImageFilesAsync(new[] { imagePath, imagePath });
                await SettleAsync(compactChat);
                Require(Math.Abs(compactChat.ActualWidth - 320) < 1 && Math.Abs(compactChat.ActualHeight - 360) < 1, "compact_fixture_uses_actual_minimum_panel_size");
                RequireControlsFit(compactChat, Require);
                var compactBounds = Bounds(compactChat);
                var compactPosition = PetSurfacePlacement.Place(Body(pet), NativeMethods.GetWindowWorkAreaPixels(handle), compactBounds.Size);
                NativeMethods.SetWindowPositionPixels(new WindowInteropHelper(compactChat).Handle, (int)Math.Round(compactPosition.Left), (int)Math.Round(compactPosition.Top));
                CaptureComposite(Path.Combine(evidence, "pet-and-compact-chat.png"), pet, compactChat); captures.Add("pet-and-compact-chat.png");
                compactChat.Shutdown(); compactChat = null;
                companion.ShowChat(chat); await SettleAsync(chat);

                phase = "lifecycle";
                chat.Close();
                await UntilAsync(() => !chat.IsVisible, "chat_close_hides");
                Require(!client.Disposed, "closing_panel_preserves_reusable_client");
                pet.DisplayLifecycle!.RequestRecovery(rendererFailure: true);
                companion.ShowChat(chat);
                Require(chat.IsVisible && pet.CompanionSurfacesAvailable, "renderer_recovery_does_not_disable_companion_chat");
                await pet.DisplayLifecycle.RestorePendingAsync();
                await SettleAsync(chat);
                input.Text = "重新展开后继续。";
                await chat.SendAsync();
                Require(client.Requests[^1].Count == 6, "reopening_panel_preserves_chat_context");
                pet.Hide();
                await UntilAsync(() => !chat.IsVisible && companion.ActionsWindow?.IsVisible != true && !pet.IsCompanionOpen, "pet_hide_hides_surfaces");
                Require(!companion.IsOpen, "hidden_pet_has_no_floating_orphan_surfaces");
                pet.Show(); await SettleAsync(pet);
                Require(!chat.IsVisible && companion.ActionsWindow?.IsVisible != true, "showing_pet_does_not_reopen_chat_or_steal_focus");
                companion.ShowChat(chat); await SettleAsync(chat);
                pet.DisplayLifecycle!.SimulateSignal(DisplayLifecycleSignal.SessionLocked);
                await UntilAsync(() => !chat.IsVisible && !pet.CompanionSurfacesAvailable, "session_lock_hides_chat");
                Require(!companion.IsOpen && !pet.IsCompanionOpen, "session_lock_releases_companion_interaction_state");
                pet.DisplayLifecycle.SimulateSignal(DisplayLifecycleSignal.SessionUnlocked);
                await pet.DisplayLifecycle.RestorePendingAsync();
                await UntilAsync(() => pet.CompanionSurfacesAvailable, "session_unlock_restores_pet");
                Require(!chat.IsVisible, "session_unlock_does_not_reopen_previous_chat");

                phase = "gravity_not_frozen";
                pet.SetSize(160); pet.SetMovement(false);
                work = NativeMethods.GetWindowWorkAreaPixels(handle);
                var local = pet.CharacterVisibleBoundsPixels;
                NativeMethods.SetWindowPositionPixels(handle,
                    (int)Math.Round(work.Left + work.Width * .55 - local.Left), (int)Math.Round(work.Top + 40 - local.Top));
                pet.MovementController!.NotifyExternalMove();
                companion.ToggleActions();
                await UntilAsync(() => companion.ActionsWindow?.IsVisible == true, "rail_before_fall");
                Point beforeFall = WorldFoot(pet);
                pet.SetMovement(true);
                await UntilAsync(() => WorldFoot(pet).Y > beforeFall.Y + 20, "gravity_continues_with_companion_open", 5);
                Require(pet.IsCompanionOpen && companion.IsOpen, "open_surface_stops_autonomy_without_freezing_gravity");
                pet.SetMovement(false);
                companion.HideAll(); await SettleAsync(pet);
                Require(!pet.IsCompanionOpen && !companion.IsOpen, "hide_all_releases_interaction_state");
                companion.Dispose(); companion = null;
                Require(!pet.IsCompanionOpen, "dispose_releases_pet_and_attached_windows");
                Finish(true);
            }
            catch (Exception ex) { Finish(false, ex.GetType().Name + ": " + ex.Message); }
        };
        app.Run();
        return result;
    }

    private static void CheckPlacement(Action<bool, string> require, List<object> samples)
    {
        var work = new Rect(0, 0, 1920, 1040);
        var body = new Rect(1000, 500, 160, 230);
        var left = PetSurfacePlacement.Place(body, work, new Size(410, 550));
        require(left.Right <= body.Left - 11 && Math.Abs(left.Bottom - body.Bottom) <= 1, "chat_prefers_left_and_aligns_to_character_bottom");
        var right = PetSurfacePlacement.Place(new Rect(5, 500, 160, 230), work, new Size(410, 550));
        require(right.Left >= 176 && Contains(work, right), "left_screen_edge_falls_back_to_right");
        var centered = PetSurfacePlacement.Place(body, work, new Size(52, 204), centerVertically: true);
        require(Math.Abs(centered.Top + centered.Height / 2 - (body.Top + body.Height / 2)) <= 1, "action_rail_centers_on_character_body");
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
        {
            var monitor = new Rect(-2560, -180, 2560, 1400);
            foreach (Point point in new[] { new Point(-2550, -170), new Point(-190, -170), new Point(-2550, 960), new Point(-190, 960) })
            {
                var character = new Rect(point.X, point.Y, 180, 250);
                var requested = new Size(410 * scale, 550 * scale);
                var positioned = PetSurfacePlacement.Place(character, monitor, requested, gap: 12 * scale);
                require(Contains(monitor, positioned) && positioned.Width > 0 && positioned.Height > 0, $"negative_monitor_corner_{scale:0.0}_{point.X}_{point.Y}_contained");
                samples.Add(new { scale, monitor, character, requested, positioned });
            }
        }
        var small = new Rect(-500, 0, 500, 320);
        var compressed = PetSurfacePlacement.Place(new Rect(-250, 120, 160, 180), small, new Size(820, 1100));
        require(Contains(small, compressed) && compressed.Width <= small.Width && compressed.Height <= small.Height, "oversized_panel_size_and_position_fit_small_work_area");
        var above = PetSurfacePlacement.Place(new Rect(220, 540, 180, 180), new Rect(0, 0, 640, 760), new Size(410, 350));
        require(Contains(new Rect(0, 0, 640, 760), above) && !above.IntersectsWith(new Rect(220, 540, 180, 180)), "narrow_monitor_uses_available_vertical_space");
    }

    private static void RequireControlsFit(OllamaChatWindow chat, Action<bool, string> require)
    {
        var content = (FrameworkElement)chat.Content;
        foreach (string name in new[] { "OllamaChatInput", "OllamaSend", "OllamaAttachImage", "OllamaCloseChat", "OllamaConnectionSettings", "AppVersionLabel" })
        {
            var control = Descendants<FrameworkElement>(chat).Single(element => element.Name == name);
            var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
            require(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0 && Contains(new Rect(content.RenderSize), bounds, 1), "compact_panel_keeps_" + name + "_visible_and_inside");
        }
        require(Named<ScrollViewer>(chat, "OllamaMessages").ActualHeight >= 25, "compact_panel_retains_readable_output_area");
    }
    private static Rect Bounds(Window window) => NativeMethods.GetWindowBoundsPixels(new WindowInteropHelper(window).Handle);
    private static Rect Body(PetWindow pet)
    {
        var window = Bounds(pet); var local = pet.CharacterVisibleBoundsPixels;
        return new Rect(window.Left + local.Left, window.Top + local.Top, local.Width, local.Height);
    }
    private static Point WorldFoot(PetWindow pet) { var window = Bounds(pet); var local = pet.VisibleFootPixels; return new Point(window.Left + local.X, window.Top + local.Y); }
    private static bool Contains(Rect outer, Rect inner, double margin = .01) => !inner.IsEmpty && double.IsFinite(inner.X) && double.IsFinite(inner.Y) && inner.Left >= outer.Left - margin && inner.Top >= outer.Top - margin && inner.Right <= outer.Right + margin && inner.Bottom <= outer.Bottom + margin;
    private static bool SameRect(Rect a, Rect b, double tolerance) => Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance && Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(element => element.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        window.UpdateLayout();
    }
    private static async Task UntilAsync(Func<bool> ready, string label, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(label); await Task.Delay(25); }
    }
    private static void CaptureComposite(string path, params Window[] windows)
    {
        var entries = windows.Where(window => window.IsVisible).Select(window => (Window: window, Bounds: Bounds(window))).ToArray();
        var union = Rect.Empty;
        foreach (var entry in entries) union.Union(entry.Bounds);
        union.Inflate(12, 12);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Ui.Brush("#E6ECE4"), null, new Rect(0, 0, union.Width, union.Height));
            foreach (var entry in entries)
            {
                var dpi = VisualTreeHelper.GetDpi(entry.Window);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(entry.Window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(entry.Window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(entry.Window);
                drawing.DrawImage(bitmap, new Rect(entry.Bounds.Left - union.Left, entry.Bounds.Top - union.Top, entry.Bounds.Width, entry.Bounds.Height));
            }
        }
        var output = new RenderTargetBitmap((int)Math.Ceiling(union.Width), (int)Math.Ceiling(union.Height), 96, 96, PixelFormats.Pbgra32); output.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void CreateImage(string path)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) { draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 160, 100)); draw.DrawEllipse(Brushes.Blue, null, new Point(80, 50), 30, 30); }
        var bitmap = new RenderTargetBitmap(160, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class SyntheticClient : IOllamaClient
    {
        internal const string ThirdModel = "synthetic-companion:small";
        internal readonly List<IReadOnlyList<OllamaMessage>> Requests = new();
        internal int Completed;
        internal bool Disposed;
        public Uri Endpoint => OllamaClient.LocalEndpoint;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(OllamaModels.Supported.Select(choice => choice.Id).Append(ThirdModel).ToArray());
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(new OllamaModelCapabilities(true, model != ThirdModel, model != ThirdModel));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(messages.ToArray());
            await Task.Delay(25, ct);
            yield return new OllamaChatDelta("附属面板已收到你的消息", false);
            await Task.Delay(25, ct);
            Completed++;
            yield return new OllamaChatDelta(messages[^1].Images?.Count > 0 ? "，也收到了蓝色圆形测试图片。" : "，可以继续和我聊天。", true, 20, 80);
        }
        public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
