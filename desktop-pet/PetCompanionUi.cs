using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopPet;

// These owned windows never enlarge the pet's collision or transparent canvas.
internal sealed class PetCompanionUi : IDisposable
{
    private readonly PetWindow _pet;
    private readonly Action _openChat, _openSettings, _openDashboard;
    private PetActionRailWindow? _actions;
    private OllamaChatWindow? _chat;
    private readonly Dictionary<Window, TopmostController> _guards = new();
    private double _chatWidth, _chatHeight;
    private bool _disposed, _positionQueued, _positioning;
    internal Window? ActionsWindow => _actions;
    internal bool IsOpen => _actions?.IsVisible == true || _chat?.IsVisible == true;

    internal PetCompanionUi(PetWindow pet, Action openChat, Action openSettings, Action openDashboard)
    {
        _pet = pet; _openChat = openChat; _openSettings = openSettings; _openDashboard = openDashboard;
        _pet.LocationChanged += HostMoved;
        _pet.SizeChanged += HostResized;
        _pet.SettingsChanged += QueuePlacement;
        _pet.IsVisibleChanged += HostVisibilityChanged;
        _pet.CompanionSurfacesUnavailable += HideAll;
        _pet.Closed += HostClosed;
    }

    internal void ToggleActions()
    {
        if (_disposed || !_pet.CompanionSurfacesAvailable) return;
        if (_actions?.IsVisible == true) { _actions.Hide(); return; }
        if (_chat?.IsVisible == true) _chat.Close();
        if (_actions == null)
        {
            _actions = new PetActionRailWindow(new[]
            {
                new PetQuickAction("chat", L.T("对话", "Chat"), "\uE8F2", () => Invoke(_openChat)),
                new PetQuickAction("settings", L.T("设置", "Settings"), "\uE713", () => Invoke(_openSettings)),
                new PetQuickAction("dashboard", L.T("看板", "Dashboard"), "\uE80F", () => Invoke(_openDashboard)),
                new PetQuickAction("more", L.T("更多", "More"), "\uE712", () => Invoke(_pet.OpenMoreActions))
            }) { Owner = _pet, Topmost = _pet.Settings.Topmost };
            ConfigureSurface(_actions);
        }
        PreparePosition(_actions, 78, 270, true);
        _actions.Show();
        RefreshPlacement();
        _actions.Activate();
        UpdateInteraction();
    }

    private void Invoke(Action action)
    {
        _actions?.Hide();
        action();
    }

    internal void ShowChat(OllamaChatWindow chat)
    {
        if (_disposed || !_pet.CompanionSurfacesAvailable) return;
        _actions?.Hide();
        if (_chat != chat)
        {
            if (_chat != null) UnsubscribeSurface(_chat);
            _chat = chat;
            _chatWidth = chat.Width; _chatHeight = chat.Height;
            chat.Owner = _pet;
            chat.WindowStartupLocation = WindowStartupLocation.Manual;
            chat.Topmost = _pet.Settings.Topmost;
            ConfigureSurface(chat);
        }
        PreparePosition(chat, _chatWidth, _chatHeight, false);
        chat.Show();
        RefreshPlacement();
        chat.Activate();
        chat.FocusComposer();
        UpdateInteraction();
    }

    private void ConfigureSurface(Window surface)
    {
        surface.IsVisibleChanged += SurfaceVisibilityChanged;
        surface.SizeChanged += SurfaceResized;
        surface.Closed += SurfaceClosed;
        var handle = new WindowInteropHelper(surface).EnsureHandle();
        NativeMethods.SetToolWindow(handle);
        _guards[surface] = new TopmostController(surface.Dispatcher, handle, () => _pet.Settings.Topmost,
            () => !_disposed && surface.IsVisible && _pet.CompanionSurfacesAvailable, () => false);
        HwndSource.FromHwnd(handle)?.AddHook(SurfaceHook);
    }

    private IntPtr SurfaceHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x02E0) QueuePlacement(); // WM_DPICHANGED: run after WPF updates the HWND size.
        foreach (var surface in _guards)
            if (new WindowInteropHelper(surface.Key).Handle == hwnd) surface.Value.OnWindowMessage(message, lParam);
        return IntPtr.Zero;
    }

    private void UnsubscribeSurface(Window surface)
    {
        surface.IsVisibleChanged -= SurfaceVisibilityChanged;
        surface.SizeChanged -= SurfaceResized;
        surface.Closed -= SurfaceClosed;
        if (_guards.Remove(surface, out var guard)) guard.Dispose();
        var handle = new WindowInteropHelper(surface).Handle;
        if (handle != IntPtr.Zero) HwndSource.FromHwnd(handle)?.RemoveHook(SurfaceHook);
    }

    private void SurfaceClosed(object? sender, EventArgs e)
    {
        if (sender is Window surface) UnsubscribeSurface(surface);
        if (sender == _chat) _chat = null;
        if (sender == _actions) _actions = null;
        UpdateInteraction();
    }
    private void SurfaceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Window surface && _guards.TryGetValue(surface, out var guard)) guard.Refresh();
        UpdateInteraction(); QueuePlacement();
    }
    private void SurfaceResized(object sender, SizeChangedEventArgs e) => QueuePlacement();
    private void HostMoved(object? sender, EventArgs e) => QueuePlacement();
    private void HostResized(object sender, SizeChangedEventArgs e) => QueuePlacement();
    private void HostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!_pet.IsVisible) HideAll(); }
    private void HostClosed(object? sender, EventArgs e) => Dispose();
    private void UpdateInteraction() => _pet.SetCompanionOpen(!_disposed && IsOpen);

    private void QueuePlacement()
    {
        if (_disposed || _positionQueued || !IsOpen) return;
        _positionQueued = true;
        _pet.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _positionQueued = false;
            if (_disposed) return;
            RefreshPlacement();
        });
    }

    internal void RefreshPlacement()
    {
        if (_disposed || _positioning) return;
        if (!_pet.CompanionSurfacesAvailable) { HideAll(); return; }
        _positioning = true;
        try
        {
            if (_actions?.IsVisible == true) PreparePosition(_actions, 78, _actions.ActualHeight, true);
            if (_chat?.IsVisible == true) PreparePosition(_chat, _chatWidth, _chatHeight, false);
        }
        finally { _positioning = false; }
    }

    private void PreparePosition(Window surface, double preferredWidth, double preferredHeight, bool rail)
    {
        var hostHandle = new WindowInteropHelper(_pet).Handle;
        if (hostHandle == IntPtr.Zero) return;
        var handle = new WindowInteropHelper(surface).EnsureHandle();
        var work = NativeMethods.GetWindowWorkAreaPixels(hostHandle);
        double margin = 6 * NativeMethods.GetDpiScale(hostHandle);
        if (work.Width > margin * 2 && work.Height > margin * 2) work.Inflate(-margin, -margin);
        double scale = NativeMethods.GetDpiScale(handle);
        if (!rail)
        {
            // Also shrink the actual HWND on small/scaled displays, not just its origin.
            double width = Math.Min(preferredWidth, work.Width / scale), height = Math.Min(preferredHeight, work.Height / scale);
            surface.MinWidth = Math.Min(320, width); surface.MinHeight = Math.Min(360, height);
            if (Math.Abs(surface.Width - width) > .5) surface.Width = width;
            if (Math.Abs(surface.Height - height) > .5) surface.Height = height;
        }
        if (surface.Topmost != _pet.Settings.Topmost)
        {
            surface.Topmost = _pet.Settings.Topmost;
            if (_guards.TryGetValue(surface, out var guard)) guard.Refresh();
        }
        surface.UpdateLayout();
        var hostBounds = NativeMethods.GetWindowBoundsPixels(hostHandle);
        var character = _pet.CharacterVisibleBoundsPixels;
        character.Offset(hostBounds.X, hostBounds.Y);
        var bounds = NativeMethods.GetWindowBoundsPixels(handle);
        var size = bounds.Width > 1 && bounds.Height > 1 ? bounds.Size : new Size(preferredWidth * scale, preferredHeight * scale);
        var target = PetSurfacePlacement.Place(character, work, size, 12 * NativeMethods.GetDpiScale(hostHandle), rail);
        if (Math.Abs(bounds.Left - target.Left) >= 1 || Math.Abs(bounds.Top - target.Top) >= 1)
            NativeMethods.SetWindowPositionPixels(handle, (int)Math.Round(target.Left), (int)Math.Round(target.Top));
    }

    internal void HideAll()
    {
        _actions?.Hide();
        // Close is the chat's existing cancel-and-hide path; drafts/history survive.
        if (_chat?.IsVisible == true) _chat.Close();
        UpdateInteraction();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pet.LocationChanged -= HostMoved;
        _pet.SizeChanged -= HostResized;
        _pet.SettingsChanged -= QueuePlacement;
        _pet.IsVisibleChanged -= HostVisibilityChanged;
        _pet.CompanionSurfacesUnavailable -= HideAll;
        _pet.Closed -= HostClosed;
        if (_actions != null) { UnsubscribeSurface(_actions); _actions.Close(); _actions = null; }
        if (_chat != null) { UnsubscribeSurface(_chat); _chat.Shutdown(); _chat = null; }
        UpdateInteraction();
    }
}
