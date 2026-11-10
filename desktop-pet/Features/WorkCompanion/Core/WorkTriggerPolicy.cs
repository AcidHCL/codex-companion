using System;
using System.Collections.Generic;

namespace DesktopPet.WorkCompanion;

// Pure, deterministic policy. The host supplies observations and owns the global
// cooldown, LLM scheduling, presentation, and configuration. No persistence here.
internal sealed class WorkTriggerPolicy
{
    internal static readonly TimeSpan StableWindowTime = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan AwayTime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan CandidateLifetime = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TopicCooldown = TimeSpan.FromMinutes(10);
    private const int MaximumTopics = 64;

    private readonly Dictionary<string, DateTimeOffset> _handledTopics = new(StringComparer.Ordinal);
    private WorkObservation? _latest;
    private string? _currentKey;
    private string? _settledKey;
    private string? _settledApp;
    private string? _previousApp;
    private WorkActivity? _settledActivity, _previousActivity;
    private DateTimeOffset _stableSince;
    private TimeSpan _workTime;
    private bool _hasTransition;
    private bool _transitionOffered;
    private bool _breakOffered;
    private WorkReactionCandidate? _candidate;
    private string? _candidateWindowKey;
    private string? _candidateTopic;
    private bool _allowFullscreen;
    private string? _offeredContextRequest;
    private TimeSpan _candidateLifetime = CandidateLifetime;

    public int ActiveMinutes => (int)_workTime.TotalMinutes;

    public WorkReactionCandidate? Observe(WorkObservation observation, bool reactAppChanges = true,
        bool breakReminders = true, int breakAfterMinutes = 45, bool allowFullscreen = false,
        string? contextRequestKey = null, TimeSpan? candidateLifetime = null)
    {
        _allowFullscreen = allowFullscreen;
        _candidateLifetime = candidateLifetime is { } duration && duration >= CandidateLifetime && duration <= TimeSpan.FromSeconds(60)
            ? duration : CandidateLifetime;
        if (!CanObserve(observation))
        {
            ClearSession();
            return null;
        }

        if (_latest != null)
        {
            var elapsed = observation.At - _latest.At;
            // Sleep, disconnect, clock changes, and stopped polling cannot turn
            // into imaginary working time or a transition across an unseen gap.
            if (elapsed <= TimeSpan.Zero || elapsed >= MaximumSampleGap)
                ClearSession();
            else if (_latest.Activity == observation.Activity)
                _workTime += elapsed;
            else
            {
                // Minutes from work must not become minutes spent gaming (or
                // vice versa). App changes within one category keep continuity.
                _workTime = TimeSpan.Zero;
                _breakOffered = false;
            }
        }

        string key = ObservationKey(observation);
        _latest = observation;
        if (_currentKey == null)
        {
            _currentKey = _settledKey = key;
            _settledApp = observation.App;
            _settledActivity = observation.Activity;
            _stableSince = observation.At;
            _transitionOffered = true;
        }
        else if (!StringComparer.Ordinal.Equals(_currentKey, key))
        {
            _currentKey = key;
            _stableSince = observation.At;
            _previousApp = _settledApp;
            _previousActivity = _settledActivity;
            _hasTransition = !StringComparer.Ordinal.Equals(_settledKey, key);
            _transitionOffered = false;
            ClearCandidate();
        }

        bool stable = observation.At - _stableSince >= StableWindowTime;
        if (stable)
        {
            _settledKey = key;
            _settledApp = observation.App;
            _settledActivity = observation.Activity;
        }
        if (_candidate != null && !IsCurrent(_candidate, observation.At)) ClearCandidate();
        if (!reactAppChanges && _candidate?.Kind == "app_changed") ClearCandidate();
        if (!breakReminders && _candidate?.Kind.EndsWith("_break", StringComparison.Ordinal) == true) ClearCandidate();

        // A started attempt is emitted once. A host that cannot start yet may
        // release its offer; the next sample must qualify a fresh candidate.
        // Resuming input invalidates any candidate, including an in-flight reply.
        if (_candidate != null || !stable || !IsQuiet(observation)) return null;
        if (!string.IsNullOrEmpty(contextRequestKey) && contextRequestKey != _offeredContextRequest)
        {
            _offeredContextRequest = contextRequestKey;
            var candidate = Offer("context_requested", observation, key, "context_requested:" + key);
            _candidate = candidate with { ContextRequestKey = contextRequestKey };
            return _candidate;
        }
        int breakMinutes = Math.Clamp(breakAfterMinutes, 1, 240);
        if (breakReminders && !_breakOffered && ActiveMinutes >= breakMinutes)
        {
            _breakOffered = true;
            string kind = CompanionActivityClassifier.Key(observation.Activity) + "_break";
            return Offer(kind, observation, key, kind);
        }
        if (!reactAppChanges)
        {
            _transitionOffered = true;
            return null;
        }
        if (_hasTransition && !_transitionOffered)
        {
            _transitionOffered = true;
            string topic = "app_changed:" + key;
            if (_handledTopics.TryGetValue(topic, out var handledAt) && observation.At - handledAt < TopicCooldown)
                return null;
            return Offer("app_changed", observation, key, topic);
        }
        return null;
    }

    public bool IsCurrent(WorkReactionCandidate candidate, DateTimeOffset now) =>
        _candidate?.Id == candidate.Id && now >= candidate.At && now < candidate.ExpiresAt &&
        _latest != null && now >= _latest.At && now - _latest.At < MaximumSampleGap &&
        CanObserve(_latest) && IsQuiet(_latest) &&
        StringComparer.Ordinal.Equals(_candidateWindowKey, ObservationKey(_latest));

    public void MarkHandled(WorkReactionCandidate candidate, DateTimeOffset now)
    {
        if (_candidate?.Id != candidate.Id) return;
        if (_candidateTopic != null)
        {
            if (_handledTopics.Count >= MaximumTopics)
            {
                string? oldestKey = null;
                DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
                foreach (var pair in _handledTopics)
                    if (pair.Value < oldestAt) { oldestKey = pair.Key; oldestAt = pair.Value; }
                if (oldestKey != null) _handledTopics.Remove(oldestKey);
            }
            _handledTopics[_candidateTopic] = now;
        }
        ClearCandidate();
    }

    // Presentation priority and cooldown are temporary gates, not a completed
    // attempt. Release only this offer, never retain its stale observation or
    // payload for later playback. A subsequent Observe rechecks window, input,
    // stability and feature switches before creating a new candidate.
    public void ReleaseCandidate(WorkReactionCandidate candidate)
    {
        if (_candidate?.Id != candidate.Id) return;
        if (candidate.Kind == "context_requested" && _offeredContextRequest == candidate.ContextRequestKey)
            _offeredContextRequest = null;
        else if (candidate.Kind == "app_changed") _transitionOffered = false;
        else if (candidate.Kind.EndsWith("_break", StringComparison.Ordinal)) _breakOffered = false;
        ClearCandidate();
    }

    public void Reset()
    {
        ClearSession();
        _handledTopics.Clear();
    }

    private WorkReactionCandidate Offer(string kind, WorkObservation observation, string key, string topic)
    {
        _candidate = new WorkReactionCandidate(Guid.NewGuid().ToString("N"), kind, observation.At,
            observation.At + _candidateLifetime, observation.App, observation.WindowTitle,
            kind is "app_changed" or "context_requested" ? _previousApp : null, ActiveMinutes)
        {
            Activity = observation.Activity,
            PreviousActivity = kind is "app_changed" or "context_requested" ? _previousActivity : null,
            WindowKey = observation.WindowKey
        };
        _candidateWindowKey = key;
        _candidateTopic = topic;
        return _candidate;
    }

    private bool CanObserve(WorkObservation observation) => observation.IsAvailable &&
        (_allowFullscreen || !observation.IsFullscreen) && !observation.IsOwnApp && !string.IsNullOrWhiteSpace(observation.App) &&
        !string.IsNullOrWhiteSpace(observation.WindowKey) && observation.IdleFor >= TimeSpan.Zero &&
        observation.IdleFor < AwayTime && Enum.IsDefined(observation.Activity);

    private static bool IsQuiet(WorkObservation observation) => observation.IdleFor >= QuietTime && observation.IdleFor < AwayTime;

    private static string ObservationKey(WorkObservation observation) =>
        observation.App.ToLowerInvariant() + "\0" + observation.WindowKey + "\0" + (observation.WindowTitle ?? "") +
        "\0" + CompanionActivityClassifier.Key(observation.Activity);

    private void ClearCandidate()
    {
        _candidate = null;
        _candidateWindowKey = _candidateTopic = null;
    }

    private void ClearSession()
    {
        _offeredContextRequest = null;
        _latest = null;
        _currentKey = _settledKey = _settledApp = _previousApp = null;
        _settledActivity = _previousActivity = null;
        _stableSince = default;
        _workTime = TimeSpan.Zero;
        _hasTransition = _transitionOffered = _breakOffered = false;
        ClearCandidate();
    }
}
