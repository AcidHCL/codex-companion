using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

// App-owned, not chat-window-owned: closing the composer does not detach the
// companion's senses. The queue and client permit only one reaction at a time.
internal sealed class PetBehaviorCoordinator : IDisposable
{
    private readonly PetWindow _pet;
    private readonly Func<OllamaChatWindow?> _getSession;
    private readonly Func<OllamaChatWindow?> _ensureSession;
    private readonly PetInteractionQueue _queue = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reaction;
    private OllamaChatWindow? _attached;
    private bool _disposed, _ticking, _refreshing;
    private DateTimeOffset _nextProbe;
    private long _epoch;

    internal PetBehaviorCoordinator(PetWindow pet, Func<OllamaChatWindow?> getSession, Func<OllamaChatWindow?> ensureSession)
    {
        _pet = pet; _getSession = getSession; _ensureSession = ensureSession;
        _timer = new DispatcherTimer(DispatcherPriority.Background, pet.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Tick;
        _pet.InteractionOccurred += OnInteraction;
        _pet.BehaviorInvalidated += OnHostInvalidated;
        RefreshMode();
    }

    internal void RefreshMode()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            var session = _getSession(); Attach(session);
            session?.RefreshBehaviorConfiguration();
            bool requested = _pet.Settings.LlmBehaviorEnabled;
            _pet.SetExplicitBehaviorCommandsActive(requested && session?.BehaviorCommandsReady == true);
            _pet.SetLlmBehaviorActive(requested && session?.BehaviorServiceReady == true);
            if (requested) _timer.Start();
            else { _timer.Stop(); CancelPending(); session?.CancelBehaviorReaction(); }
        }
        finally { _refreshing = false; }
    }

    private void Attach(OllamaChatWindow? session)
    {
        if (ReferenceEquals(session, _attached)) return;
        if (_attached != null) { _attached.BehaviorAvailabilityChanged -= OnAvailability; _attached.BehaviorContextInvalidated -= OnContextInvalidated; }
        _attached = session;
        if (session != null) { session.BehaviorAvailabilityChanged += OnAvailability; session.BehaviorContextInvalidated += OnContextInvalidated; }
    }
    private void OnAvailability() => RefreshMode();
    private void OnContextInvalidated()
    { CancelPending(); _pet.InvalidateLlmBehavior(); _nextProbe = DateTimeOffset.MinValue; }
    private void OnHostInvalidated()
    { _epoch++; _queue.InvalidatePending(); CancelReaction(); _getSession()?.CancelBehaviorReaction(); }
    private void CancelPending()
    { _epoch++; _queue.Clear(); CancelReaction(); }
    private void CancelReaction()
    { try { _reaction?.Cancel(); } catch (ObjectDisposedException) { } }
    private void OnInteraction(PetInteractionEvent item)
    {
        if (_disposed || !_pet.Settings.LlmBehaviorEnabled || !_pet.CompanionSurfacesAvailable) return;
        _epoch++; CancelReaction(); _getSession()?.CancelBehaviorReaction();
        _queue.Offer(item);
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_disposed || _ticking || !_pet.Settings.LlmBehaviorEnabled) return;
        if (!_pet.CompanionSurfacesAvailable) { CancelPending(); _getSession()?.CancelBehaviorReaction(); return; }
        _ticking = true;
        try
        {
            var session = _getSession();
            var now = DateTimeOffset.UtcNow;
            if (session == null && now >= _nextProbe)
            {
                _nextProbe = now.AddSeconds(45);
                session = _ensureSession(); Attach(session);
                if (session != null) await session.CheckConnectionAsync();
                RefreshMode();
            }
            if (session == null || _disposed) return;
            // Metadata-only health check, never an idle inference loop. The
            // chat method declines checks while user work/configuration is busy.
            if (now >= _nextProbe)
            {
                _nextProbe = now.AddSeconds(45);
                await session.CheckConnectionAsync(); RefreshMode();
            }
            if (!_pet.IsLlmBehaviorActive || !session.BehaviorServiceReady) { _queue.Clear(); return; }
            if (!_queue.TryTake(DateTimeOffset.UtcNow, session.CanReactToInteraction, out var interaction) || interaction == null) return;
            long epoch = _epoch;
            using var reaction = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            reaction.CancelAfter(TimeSpan.FromSeconds(25)); _reaction = reaction;
            string? answer = await session.ReactToInteractionAsync(interaction, reaction.Token);
            if (!reaction.IsCancellationRequested && !_disposed && epoch == _epoch && _pet.Settings.LlmBehaviorEnabled && _pet.CompanionSurfacesAvailable && !string.IsNullOrWhiteSpace(answer))
                _pet.SayChatAnswer(answer, answer.Length <= 120 ? answer : answer[..120] + "…");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _pet.SetLlmBehaviorActive(false); }
        finally { _reaction = null; _ticking = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= Tick;
        _pet.InteractionOccurred -= OnInteraction; _pet.BehaviorInvalidated -= OnHostInvalidated;
        Attach(null); CancelPending(); _lifetime.Cancel(); _lifetime.Dispose();
        _pet.SetExplicitBehaviorCommandsActive(false);
        _pet.SetLlmBehaviorActive(false);
    }
}
