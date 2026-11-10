using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPet.WorkCompanion;

// The sole feature composition root. Removing its registration and this folder
// removes observations, configuration UI and model/pet adapters together.
internal sealed class WorkCompanionModule : IDisposable
{
    private readonly PetWindow _pet;
    private readonly CodexMonitor? _monitor;
    private readonly PetWorkCompanionHost _host;
    private readonly WorkCompanionController _controller;
    private readonly DispatcherTimer _timer;
    private string _context;
    private bool _disposed;

    internal WorkCompanionModule(PetWindow pet, CodexMonitor? monitor,
        Func<OllamaChatWindow?> getChat, Func<OllamaChatWindow?> ensureChat, IWorkContextExtension? contextExtension = null)
    {
        CompanionDiagnostics.Initialize();
        CompanionDiagnostics.Record("lifecycle", "start");
        _pet = pet; _monitor = monitor;
        _host = new PetWorkCompanionHost(pet, getChat, ensureChat, contextExtension);
        var store = new WorkCompanionOptionsStore();
        _controller = new WorkCompanionController(new WindowsWorkObserver(), _host, store.Load(), store.Save,
            contextExtension: contextExtension);
        _timer = new DispatcherTimer(DispatcherPriority.Background, pet.Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Tick;
        _controller.Changed += UpdateTimer;
        _pet.BehaviorInvalidated += Invalidate;
        _pet.SettingsChanged += SettingsChanged;
        if (_monitor != null) _monitor.VerifiedTransition += CodexTransition;
        _context = _host.Capture().ContextKey;
        UpdateTimer();
    }

    internal FrameworkElement CreateSettingsView() => new WorkCompanionSettingsView(_controller,
        () => string.Join("\n", CompanionDiagnostics.ReadRecent().Select(entry =>
            $"{entry.Timestamp.LocalDateTime:MM-dd HH:mm:ss} · {(L.IsEnglish ? entry.LayerEn : entry.LayerZh)} · {entry.Display(L.IsEnglish)}")));
    private async void Tick(object? sender, EventArgs args) => await _controller.TickAsync();
    private void UpdateTimer()
    {
        if (_controller.ShouldObserve) { if (!_timer.IsEnabled) _timer.Start(); }
        else { _timer.Stop(); _host.CancelPresentation(); }
    }
    private void Invalidate()
    {
        _context = _host.Capture().ContextKey;
        _controller.Invalidate();
    }
    private void SettingsChanged()
    {
        string context = _host.Capture().ContextKey;
        if (_context == context) return;
        _context = context; _controller.ClearTransientState();
    }
    private void CodexTransition(PetNotice notice)
    {
        if (_monitor?.MessagesEnabled != true || !_pet.Settings.NotificationsEnabled) return;
        _controller.ObserveCodexTransition();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= Tick;
        _controller.Changed -= UpdateTimer;
        _pet.BehaviorInvalidated -= Invalidate;
        _pet.SettingsChanged -= SettingsChanged;
        if (_monitor != null) _monitor.VerifiedTransition -= CodexTransition;
        _controller.Dispose(); _host.Dispose();
    }
}
