using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopPet;

internal sealed record DesktopJumpPlan(DesktopPlatform Target, Point Destination, Vector Velocity, double Duration);

// Pure geometry/physics: the native adapter below is the only layer that moves a
// HWND. Keeping this separate allows occlusion, drag, and landing tests without
// controlling any user's windows.
internal sealed class DesktopMovementPhysics
{
    public Rect Bounds { get; private set; } = Rect.Empty;
    public Point Anchor { get; private set; }
    public Rect VisibleBounds { get; private set; }
    // Contact geometry deliberately ends at the feet. Transparent HWND chrome,
    // the status badge, and bubble padding do not keep a character above a floor.
    public Rect Body => new(Math.Min(VisibleBounds.Left, Anchor.X), Math.Min(VisibleBounds.Top, Anchor.Y),
        Math.Max(VisibleBounds.Right, Anchor.X) - Math.Min(VisibleBounds.Left, Anchor.X),
        Anchor.Y - Math.Min(VisibleBounds.Top, Anchor.Y));
    public Vector Velocity { get; private set; }
    public DesktopPlatform? Support { get; private set; }
    public bool OnFloor { get; private set; }
    public bool Grounded => Support != null || OnFloor;
    public double Gravity { get; set; } = 1700;
    public double WalkSpeed { get; set; }
    public string State { get; private set; } = "idle";
    // Monotonic contacts produced by integration, never by placement/snapping.
    public long LandingSequence { get; private set; }
    private bool _hasDescended;
    public Point Foot => Bounds.IsEmpty ? new Point() : new(Bounds.Left + Anchor.X, Bounds.Top + Anchor.Y);

    public void Reset(Rect bounds, Point anchor, Rect? localVisibleBounds = null)
    {
        Bounds = bounds; Anchor = anchor; VisibleBounds = NormalizeVisible(bounds, localVisibleBounds);
        Velocity = new Vector(); Support = null; OnFloor = false; State = "idle";
        _hasDescended = false;
    }

    public void ReconfigureGeometry(Rect bounds, Point anchor, Rect? localVisibleBounds = null)
    {
        if (Bounds.IsEmpty) { Reset(bounds, anchor, localVisibleBounds); return; }
        Point foot = Foot;
        Anchor = anchor; VisibleBounds = NormalizeVisible(bounds, localVisibleBounds);
        Bounds = new Rect(foot.X - anchor.X, foot.Y - anchor.Y, bounds.Width, bounds.Height);
    }

    private static Rect NormalizeVisible(Rect bounds, Rect? visible)
    {
        var full = new Rect(0, 0, bounds.Width, bounds.Height);
        if (!visible.HasValue || visible.Value.IsEmpty || visible.Value.Width <= 0 || visible.Value.Height <= 0) return full;
        var clipped = Rect.Intersect(full, visible.Value);
        return clipped.IsEmpty || clipped.Width <= 0 || clipped.Height <= 0 ? full : clipped;
    }

    public void Release(Vector velocity)
    {
        Support = null; OnFloor = false;
        _hasDescended = false;
        Velocity = new Vector(Math.Clamp(velocity.X, -1100, 1100), Math.Clamp(velocity.Y, -1300, 1300));
        State = Velocity.Y < 0 ? "jump" : "fall";
    }

    internal void PlaceKinematic(Rect bounds, string state)
    {
        Bounds = bounds; Velocity = new Vector(); Support = null; OnFloor = false;
        WalkSpeed = 0; State = state; _hasDescended = false;
    }

    internal void StopWalking()
    {
        WalkSpeed = 0;
        if (Grounded) State = "idle";
    }

    public bool TrySnapToSurface(DesktopPlatformSnapshot snapshot, double tolerance)
    {
        if (snapshot.WorkAreas.Count == 0) return false;
        Point foot = Foot;
        var surface = snapshot.Platforms.Where(p => p.Contains(foot.X) && Math.Abs(p.Top - foot.Y) <= tolerance)
            .OrderBy(p => Math.Abs(p.Top - foot.Y)).FirstOrDefault(p => FitsWorkArea(
                new Rect(Bounds.X, p.Top - Anchor.Y, Bounds.Width, Bounds.Height), snapshot.WorkAreas));
        if (surface != null)
        {
            Bounds = new Rect(Bounds.X, surface.Top - Anchor.Y, Bounds.Width, Bounds.Height);
            Support = surface; OnFloor = false; Velocity = new Vector(); State = "idle"; _hasDescended = false; return true;
        }
        var work = DesktopWindowPlatforms.NearestWorkArea(WorldBody(Bounds), snapshot.WorkAreas);
        if (Math.Abs(work.Bottom - foot.Y) > tolerance) return false;
        Bounds = DesktopWindowPlatforms.ClampBodyToWorkAreas(new Rect(Bounds.X, work.Bottom - Anchor.Y, Bounds.Width, Bounds.Height), Body, snapshot.WorkAreas);
        Support = null; OnFloor = true; Velocity = new Vector(); State = "idle"; _hasDescended = false; return true;
    }

    public void Step(double seconds, DesktopPlatformSnapshot snapshot)
    {
        if (snapshot.WorkAreas.Count == 0 || Bounds.IsEmpty) return;
        double dt = Math.Clamp(seconds, 0, .05);
        if (dt <= 0) return;
        FollowSupport(snapshot);
        int steps = Math.Max(1, (int)Math.Ceiling(dt * 120));
        for (int step = 0; step < steps; step++) Advance(dt / steps, snapshot);
    }

    private void FollowSupport(DesktopPlatformSnapshot snapshot)
    {
        if (Support == null) return;
        var previous = Support;
        var candidates = snapshot.Platforms.Where(p => p.Handle == previous.Handle).ToArray();
        if (candidates.Length == 0) { Support = null; State = "fall"; return; }
        // A platform can lift/carry the pet, but cannot pull it downward.
        // Leave the feet at their previous screen position and let gravity
        // close the gap; the normal swept landing may catch this platform again.
        if (candidates[0].Top > previous.Top + .001)
        {
            Support = null; State = "fall";
            return;
        }
        var owner = candidates[0].OwnerBounds;
        double relative = (Foot.X - previous.OwnerBounds.Left) / Math.Max(1, previous.OwnerBounds.Width);
        double nextFoot = owner.Left + relative * owner.Width;
        var current = candidates.FirstOrDefault(p => p.Contains(nextFoot));
        if (current == null) { Support = null; State = "fall"; return; }
        var followed = new Rect(nextFoot - Anchor.X, current.Top - Anchor.Y, Bounds.Width, Bounds.Height);
        if (!FitsWorkArea(followed, snapshot.WorkAreas)) { Support = null; State = "fall"; return; }
        Bounds = followed;
        Support = current;
    }

    private void Advance(double dt, DesktopPlatformSnapshot snapshot)
    {
        var work = DesktopWindowPlatforms.NearestWorkArea(WorldBody(Bounds), snapshot.WorkAreas);
        double floor = work.Bottom;
        if (OnFloor && Math.Abs(Foot.Y - floor) > 1) OnFloor = false;
        Point before = Foot;
        if (Grounded)
        {
            double speed = WalkSpeed;
            double min = work.Left + Anchor.X - Body.Left, max = work.Right + Anchor.X - Body.Right;
            if (Support != null) { min = Math.Max(min, Support.Left + 6); max = Math.Min(max, Support.Right - 6); }
            if (min > max) { Support = null; OnFloor = false; }
            else
            {
                double x = before.X + speed * dt;
                if (x < min || x > max) { WalkSpeed = -speed; x = Math.Clamp(x, min, max); }
                Bounds = new Rect(x - Anchor.X, Bounds.Top, Bounds.Width, Bounds.Height);
                Velocity = new Vector();
                State = Math.Abs(WalkSpeed) > .1 ? "walk" : "idle";
                return;
            }
        }

        Vector previousVelocity = Velocity;
        Velocity = new Vector(previousVelocity.X, Math.Min(previousVelocity.Y + Gravity * dt, 1600));
        Point next = new(before.X + Velocity.X * dt, before.Y + previousVelocity.Y * dt + .5 * Gravity * dt * dt);
        DesktopPlatform? landing = null;
        double earliest = double.PositiveInfinity;
        // Swept foot crossing avoids tunnelling at low frame rates. Upward motion
        // deliberately passes through a platform; landing is only on descent.
        if (next.Y >= before.Y)
        {
            foreach (var platform in snapshot.Platforms)
            {
                if (before.Y > platform.Top + .5 || next.Y < platform.Top) continue;
                double t = (platform.Top - before.Y) / Math.Max(.001, next.Y - before.Y);
                double x = before.X + (next.X - before.X) * t;
                var target = new Rect(x - Anchor.X, platform.Top - Anchor.Y, Bounds.Width, Bounds.Height);
                if (t < earliest && platform.Contains(x) && FitsWorkArea(target, snapshot.WorkAreas)) { landing = platform; earliest = t; }
            }
        }
        if (landing != null && landing.Top <= floor)
        {
            next = new Point(before.X + (next.X - before.X) * earliest, landing.Top);
            Support = landing; OnFloor = false; Velocity = new Vector(); State = "idle";
        }
        else if (next.Y >= floor)
        {
            next.Y = floor; Support = null; OnFloor = true; Velocity = new Vector(); State = "idle";
        }
        else State = Velocity.Y < 0 ? "jump" : "fall";

        var proposed = new Rect(next.X - Anchor.X, next.Y - Anchor.Y, Bounds.Width, Bounds.Height);
        var safe = DesktopWindowPlatforms.ClampBodyToWorkAreas(proposed, Body, snapshot.WorkAreas);
        if (Math.Abs(safe.X - proposed.X) > .01) Velocity = new Vector(0, Velocity.Y);
        if (safe.Top > proposed.Top + .01) Velocity = new Vector(Velocity.X, Math.Max(0, Velocity.Y));
        Bounds = safe;
        // Use actual displacement after collision/clamping: starting exactly on
        // a floor, resetting geometry, or snapping must not manufacture impact.
        if (Foot.Y > before.Y + .001) _hasDescended = true;
        if (Grounded)
        {
            if (_hasDescended) LandingSequence++;
            _hasDescended = false;
            // A touchdown ends this frame's old walking decision. The host can
            // now hold a landing pose while the physics keeps checking support.
            WalkSpeed = 0;
        }
    }

    public bool TryJump(DesktopPlatformSnapshot snapshot, int direction = 0)
    {
        var plan = PlanJump(snapshot, direction);
        if (plan == null) return false;
        Release(plan.Velocity);
        return true;
    }

    internal DesktopJumpPlan? PlanJump(DesktopPlatformSnapshot snapshot, int direction = 0)
    {
        if (!Grounded || direction is < -1 or > 1 || snapshot.WorkAreas.Count == 0) return null;
        Point origin = Foot;
        var work = DesktopWindowPlatforms.NearestWorkArea(WorldBody(Bounds), snapshot.WorkAreas);
        var options = new List<(DesktopPlatform Platform, double X, double Vx, double Vy, double Score)>();
        foreach (var platform in snapshot.Platforms)
        {
            if (platform.Handle == Support?.Handle) continue;
            double min = Math.Max(platform.Left + 12, work.Left + Anchor.X - Body.Left);
            double max = Math.Min(platform.Right - 12, work.Right + Anchor.X - Body.Right);
            if (max < min) continue;
            double x = Math.Clamp(origin.X, min, max), dy = platform.Top - origin.Y;
            if (Math.Abs(x - origin.X) < 24) x = Math.Clamp((platform.Left + platform.Right) / 2, min, max);
            if (direction != 0 && (x - origin.X) * direction < 12)
                x = Math.Clamp(origin.X + direction * 80, min, max);
            if (direction != 0 && (x - origin.X) * direction < 12) continue;
            if (Math.Abs(x - origin.X) > 620 || dy < -250 || dy > 450) continue;
            double arcHeight = Math.Max(70, -dy + 45);
            double vy = -Math.Sqrt(2 * Gravity * arcHeight);
            double discriminant = vy * vy + 2 * Gravity * dy;
            if (discriminant < 0) continue;
            double duration = (-vy + Math.Sqrt(discriminant)) / Gravity;
            double vx = (x - origin.X) / duration;
            var landingBounds = new Rect(x - Anchor.X, platform.Top - Anchor.Y, Bounds.Width, Bounds.Height);
            // A conservative one-monitor arc: no teleport across monitor gaps,
            // and no head clipping at the apex. Replan on the next request if a
            // target moves; the normal landing logic handles missed targets.
            if (Math.Abs(vx) > 600 || origin.Y - arcHeight - Anchor.Y + Body.Top < work.Top || !work.Contains(WorldBody(landingBounds))) continue;
            options.Add((platform, x, vx, vy, Math.Abs(x - origin.X) + Math.Abs(dy) * 1.4));
        }
        if (options.Count == 0) return null;
        var chosen = options.OrderBy(o => o.Score).First();
        double flight = (-chosen.Vy + Math.Sqrt(chosen.Vy * chosen.Vy + 2 * Gravity * (chosen.Platform.Top - origin.Y))) / Gravity;
        return new DesktopJumpPlan(chosen.Platform, new Point(chosen.X, chosen.Platform.Top), new Vector(chosen.Vx, chosen.Vy), flight);
    }

    private Rect WorldBody(Rect bounds) => new(bounds.Left + Body.Left, bounds.Top + Body.Top, Body.Width, Body.Height);
    private bool FitsWorkArea(Rect bounds, IReadOnlyList<Rect> workAreas) => workAreas.Any(w => w.Contains(WorldBody(bounds)));
}

internal sealed class DesktopDragVelocity
{
    private readonly List<(Point Position, double Time)> _samples = new();
    public void Clear() => _samples.Clear();
    public void Add(Point position, double seconds)
    {
        if (_samples.Count > 0 && seconds <= _samples[^1].Time) return;
        _samples.Add((position, seconds));
        while (_samples.Count > 1 && _samples[0].Time < seconds - .15) _samples.RemoveAt(0);
    }
    public Vector Get(double seconds)
    {
        if (_samples.Count < 2 || seconds - _samples[^1].Time > .12) return new Vector();
        var first = _samples[0]; var last = _samples[^1];
        double elapsed = last.Time - first.Time;
        if (elapsed < .015 || seconds - first.Time > .22) return new Vector();
        Vector speed = (last.Position - first.Position) / elapsed;
        if (speed.Length < 90) return new Vector();
        return new Vector(Math.Clamp(speed.X, -1100, 1100), Math.Clamp(speed.Y, -1300, 1300));
    }
}

internal sealed record DesktopMovementFrameStats(long FrameCount, long MovedFrames, double MeanIntervalMs,
    double P95IntervalMs, double MaxIntervalMs, long FullScans, long SupportRefreshes, double MaxScanMs, double MaxTickMs);

// Animation observes both the movement kind and facing. Reversing at an edge
// keeps the physics state as "walk", but still needs to select the other clip.
internal sealed class DesktopMovementAnimationState
{
    public string State { get; private set; } = "idle";
    public int FacingDirection { get; private set; } = 1;

    public bool Update(string state, double horizontalSpeed)
    {
        int direction = Math.Abs(horizontalSpeed) > .1 ? Math.Sign(horizontalSpeed) : FacingDirection;
        bool changed = State != state || FacingDirection != direction;
        State = state; FacingDirection = direction;
        return changed;
    }
}

internal sealed class DesktopMovementController : IDisposable
{
    private readonly Window _owner;
    private readonly Func<Point>? _footAnchorPixels;
    private readonly Func<Rect>? _visibleBoundsPixels;
    private readonly Func<string, int, Point?>? _motionContactPixels;
    private readonly Func<string, bool>? _motionSceneReady;
    private readonly Func<IntPtr, DesktopPlatformSnapshot> _platformProvider;
    private readonly bool _nativePlatforms;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DesktopMovementPhysics _physics = new();
    private readonly DesktopMovementAnimationState _animationState = new();
    private readonly DesktopDragVelocity _drag = new();
    private readonly Random _random = new();
    private DesktopPlatformSnapshot _snapshot = new(Array.Empty<Rect>(), Array.Empty<DesktopPlatform>());
    private double _lastTick, _lastScan = -1, _nextDecision, _lastMovement;
    private TimeSpan? _lastRenderTime;
    private bool _paused, _disposed, _needsSave, _rendering, _autonomous = true;
    private double _directedUntil;
    private int _directedDirection;
    private enum MotionPhase { None, JumpPrepare, JumpAir, JumpLand, ClimbApproach, Climb, ClimbTop }
    private MotionPhase _motionPhase;
    private int _motionDirection;
    private double _motionTime;
    private readonly Func<DeepSeekMotionProfile>? _motionProfileProvider;
    private readonly Func<double>? _motionCanvasScale;
    private DeepSeekMotionProfile _motionProfile = DeepSeekMotionTimeline.Legacy;
    private DesktopJumpPlan? _jumpPlan;
    private DesktopClimbPlan? _climbPlan;
    private double _climbGripRelativeY;
    private Rect _climbPreviousOwner;
    private double _motionSceneWaitStarted = -1;
    private readonly Queue<double> _frameIntervals = new();
    private long _frameCount, _movedFrames, _fullScans, _supportRefreshes;
    private double _intervalTotal, _maxInterval, _maxScanMs, _maxTickMs;
    private IntPtr Handle => new WindowInteropHelper(_owner).Handle;
    public bool IsEnabled { get; private set; }
    public bool IsMovingWindow { get; private set; }
    public bool IsDragging { get; private set; }
    public string State => _animationState.State;
    public int FacingDirection => _animationState.FacingDirection;
    public Point FootScreen => _physics.Foot;
    public IntPtr SupportHandle => _physics.Support?.Handle ?? IntPtr.Zero;
    public bool Grounded => _physics.Grounded;
    internal bool IsDirectedWalking => _directedUntil > _clock.Elapsed.TotalSeconds;
    public bool HasScriptedMotion => _motionPhase != MotionPhase.None;
    public string? MotionAction { get; private set; }
    public int MotionFrame { get; private set; } = -1;
    public DesktopMovementFrameStats FrameStats => GetFrameStats();
    public event Action<string>? StateChanged;
    public event Action? Landed;
    public event Action? Settled;
    public event Action? MotionFrameChanged;

    public DesktopMovementController(Window owner, Func<Point>? footAnchorPixels = null, Func<IntPtr, DesktopPlatformSnapshot>? platformProvider = null, Func<Rect>? visibleBoundsPixels = null,
        Func<string, int, Point?>? motionContactPixels = null, Func<string, bool>? motionSceneReady = null,
        Func<DeepSeekMotionProfile>? motionProfile = null, Func<double>? motionCanvasScale = null)
    {
        _owner = owner; _footAnchorPixels = footAnchorPixels; _visibleBoundsPixels = visibleBoundsPixels;
        _motionContactPixels = motionContactPixels;
        _motionSceneReady = motionSceneReady;
        _motionProfileProvider = motionProfile;
        _motionCanvasScale = motionCanvasScale;
        _platformProvider = platformProvider ?? DesktopWindowPlatforms.Capture;
        _nativePlatforms = platformProvider == null;
        _owner.IsVisibleChanged += OwnerVisibilityChanged;
    }

    public DesktopMovementFrameStats GetFrameStats()
    {
        var recent = _frameIntervals.OrderBy(x => x).ToArray();
        double p95 = recent.Length == 0 ? 0 : recent[Math.Max(0, (int)Math.Ceiling(recent.Length * .95) - 1)];
        return new DesktopMovementFrameStats(_frameCount, _movedFrames, _frameCount == 0 ? 0 : _intervalTotal / _frameCount,
            p95, _maxInterval, _fullScans, _supportRefreshes, _maxScanMs, _maxTickMs);
    }
    public void ResetFrameStats()
    {
        _frameIntervals.Clear(); _frameCount = _movedFrames = _fullScans = _supportRefreshes = 0;
        _intervalTotal = _maxInterval = _maxScanMs = _maxTickMs = 0;
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || enabled == IsEnabled) return;
        IsEnabled = enabled;
        if (enabled) { ResetFrameStats(); NotifyExternalMove(); _nextDecision = _clock.Elapsed.TotalSeconds + 2; }
        // Disabling autonomous movement does not release a held mouse drag.
        // Keep drag until EndDrag publishes idle; otherwise that final idle is
        // suppressed as unchanged and the scene never sees the actual release.
        else { StopDirectedWalk(); CancelScriptedMotion(); _physics.WalkSpeed = 0; SetState(IsDragging ? "drag" : "idle"); SaveIfNeeded(); }
        UpdateRendering();
    }
    public void SetPaused(bool paused)
    {
        if (_disposed || _paused == paused) return;
        _paused = paused;
        if (paused) { StopDirectedWalk(); CancelScriptedMotion(); SetState(IsDragging ? "drag" : "idle"); SaveIfNeeded(); }
        UpdateRendering();
    }
    public void SetAutonomous(bool autonomous)
    {
        _autonomous = autonomous;
        if (!autonomous && !IsDirectedWalking)
        {
            _physics.WalkSpeed = 0;
            if (State == "walk") SetState("idle");
        }
    }
    public void NotifyExternalMove()
    {
        if (_disposed || Handle == IntPtr.Zero) return;
        StopDirectedWalk();
        CancelScriptedMotion();
        var bounds = NativeMethods.GetWindowBoundsPixels(Handle);
        _physics.Reset(bounds, GetAnchor(bounds), GetVisibleBounds(bounds));
        if (IsEnabled && !IsDragging)
        {
            ScanPlatforms();
            _physics.TrySnapToSurface(_snapshot, 8 * NativeMethods.GetDpiScale(Handle));
        }
        _lastTick = _clock.Elapsed.TotalSeconds;
        if (!IsDragging) SetState("idle");
    }
    public void ReconfigureGeometry()
    {
        if (_disposed || Handle == IntPtr.Zero) return;
        var bounds = NativeMethods.GetWindowBoundsPixels(Handle);
        _physics.ReconfigureGeometry(bounds, GetAnchor(bounds), GetVisibleBounds(bounds));
    }
    public void BeginDrag()
    {
        if (_disposed) return;
        StopDirectedWalk();
        CancelScriptedMotion();
        IsDragging = true; _drag.Clear(); ObserveDrag(); SetState("drag");
    }
    public void ObserveDrag()
    {
        if (!IsDragging || Handle == IntPtr.Zero) return;
        var b = NativeMethods.GetWindowBoundsPixels(Handle);
        // A thought panel can resize the HWND upward during a drag. Sample
        // the physical feet so that layout changes do not become throw speed.
        Point anchor = GetAnchor(b);
        _drag.Add(new Point(b.Left + anchor.X, b.Top + anchor.Y), _clock.Elapsed.TotalSeconds);
    }
    public void EndDrag()
    {
        if (!IsDragging || _disposed) return;
        ObserveDrag();
        Vector velocity = _drag.Get(_clock.Elapsed.TotalSeconds);
        IsDragging = false;
        var bounds = NativeMethods.GetWindowBoundsPixels(Handle);
        _physics.Reset(bounds, GetAnchor(bounds), GetVisibleBounds(bounds));
        _lastTick = _clock.Elapsed.TotalSeconds;
        if (IsEnabled && !_paused)
        {
            ScanPlatforms();
            _physics.Release(velocity);
            if (velocity.Length <= 160) _physics.TrySnapToSurface(_snapshot, 8 * NativeMethods.GetDpiScale(Handle));
            _nextDecision = _clock.Elapsed.TotalSeconds + 3; SetState(_physics.State);
        }
        else SetState("idle");
        _needsSave = true; _lastMovement = _clock.Elapsed.TotalSeconds;
        if (!IsEnabled) SaveIfNeeded();
        UpdateRendering();
    }
    public bool TryJumpToWindow(int direction = 0)
    {
        if (_disposed || !IsEnabled || _paused || IsDragging || HasScriptedMotion || !_owner.IsVisible || direction is < -1 or > 1) return false;
        StopDirectedWalk();
        ScanPlatforms();
        if (!_physics.Grounded) _physics.TrySnapToSurface(_snapshot, 8 * NativeMethods.GetDpiScale(Handle));
        _jumpPlan = _physics.PlanJump(_snapshot, direction);
        if (_jumpPlan == null) return false;
        _motionDirection = Math.Sign(_jumpPlan.Velocity.X);
        if (_motionDirection == 0) _motionDirection = direction == 0 ? FacingDirection : direction;
        _motionProfile = _motionProfileProvider?.Invoke() ?? DeepSeekMotionTimeline.Legacy;
        if (_motionProfile.Clip(_motionDirection < 0 ? "jump-left" : "jump-right") == null) { _jumpPlan = null; return false; }
        _motionPhase = MotionPhase.JumpPrepare; _motionTime = 0;
        _physics.StopWalking(); _nextDecision = _clock.Elapsed.TotalSeconds + 3;
        SetMotionFrame(_motionDirection < 0 ? "jump-left" : "jump-right", 0);
        SetState("jump");
        return true;
    }

    public bool TryClimbToWindow(int direction)
    {
        if (direction is not (-1 or 1) || _disposed || !IsEnabled || _paused || IsDragging || HasScriptedMotion || !_owner.IsVisible) return false;
        string action = direction < 0 ? "climb-left" : "climb-right";
        _motionProfile = _motionProfileProvider?.Invoke() ?? DeepSeekMotionTimeline.Legacy;
        if (_motionProfile.Clip(action) == null) return false;
        Point? contact = _motionContactPixels?.Invoke(action, 0);
        if (contact == null) return false;
        ScanPlatforms();
        if (!_physics.Grounded) _physics.TrySnapToSurface(_snapshot, 8 * NativeMethods.GetDpiScale(Handle));
        if (!_physics.Grounded) return false;
        var plan = DesktopClimbPlanner.Plan(_snapshot, _physics.Bounds, _physics.Anchor, _physics.Body,
            contact.Value, direction, NativeMethods.GetDpiScale(Handle));
        if (plan == null) return false;
        StopDirectedWalk(); _physics.StopWalking();
        _climbPlan = plan; _climbPreviousOwner = plan.Edge.OwnerBounds;
        _climbGripRelativeY = plan.Grip.Y - plan.Edge.OwnerBounds.Top;
        if (!_motionProfile.IsLegacy && _motionProfile.Clip(action) is { Contacts: { } hands, GroundFootY: { } feet })
        {
            // Reserve enough clearance for the whole grip cycle before it
            // starts. Approach the higher hold smoothly; do not jerk the HWND
            // upward on every pose whose raised hand lowers the body.
            double clearance = Enumerable.Range(0, hands.Length).Max(i => feet[i] - hands[i].Y)
                * (_motionCanvasScale?.Invoke() ?? 1);
            // Collision Body ends at the canonical support line, which can be
            // below a tucked shoe. Reserve both rendered and collision extent.
            clearance = Math.Max(clearance, Enumerable.Range(0, hands.Length)
                .Select(i => _motionContactPixels?.Invoke(action, i))
                .Where(p => p.HasValue).Max(p => _physics.Body.Bottom - p!.Value.Y)) + 1;
            var area = DesktopWindowPlatforms.NearestWorkArea(DesktopClimbPlanner.WorldBody(_physics.Bounds, _physics.Body), _snapshot.WorkAreas);
            _climbGripRelativeY = Math.Max(0, Math.Min(_climbGripRelativeY, area.Bottom - plan.Edge.OwnerBounds.Top - clearance));
        }
        _motionDirection = direction; _motionTime = 0; _motionPhase = MotionPhase.ClimbApproach;
        _nextDecision = _clock.Elapsed.TotalSeconds + 3;
        SetMotionFrame(action, 0); SetState("climb");
        return true;
    }

    public void CancelScriptedMotion()
    {
        if (!HasScriptedMotion) return;
        bool wasClimbing = _climbPlan != null;
        ClearScriptedMotion();
        if (wasClimbing) _physics.Release(new Vector());
        _nextDecision = _clock.Elapsed.TotalSeconds + 2;
        SetState(IsDragging ? "drag" : _physics.State);
    }

    private void ClearScriptedMotion()
    {
        _motionPhase = MotionPhase.None; _jumpPlan = null; _climbPlan = null; _motionTime = 0;
        SetMotionFrame(null, -1);
    }

    private void SetMotionFrame(string? action, int frame)
    {
        if (MotionAction == action && MotionFrame == frame) return;
        if (MotionAction != action) _motionSceneWaitStarted = -1;
        MotionAction = action; MotionFrame = frame; MotionFrameChanged?.Invoke();
    }

    // An explicit direction uses the same physics and rendering-tick path as an
    // autonomous decision. Integration checks can observe real HWND travel and
    // StateChanged without waiting for a random decision or injecting input.
    internal bool TryWalk(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (_disposed || !IsEnabled || _paused || IsDragging || HasScriptedMotion || !_autonomous || !_owner.IsVisible) return false;
        ScanPlatforms();
        if (!_physics.Grounded) _physics.TrySnapToSurface(_snapshot, 8 * NativeMethods.GetDpiScale(Handle));
        if (!_physics.Grounded) return false;
        _physics.WalkSpeed = direction * 40 * NativeMethods.GetDpiScale(Handle);
        _nextDecision = _clock.Elapsed.TotalSeconds + 5;
        return true;
    }

    // Directed movement is a bounded lease, separate from the random idle
    // decision policy. Disabling random walking must never disable gravity.
    internal bool TryDirectedWalk(int direction, double seconds)
    {
        if (direction is not (-1 or 1) || !double.IsFinite(seconds) || seconds is < 1 or > 8) return false;
        if (_disposed || !IsEnabled || _paused || IsDragging || HasScriptedMotion || !_owner.IsVisible) return false;
        ScanPlatforms();
        if (!_physics.Grounded) _physics.TrySnapToSurface(_snapshot, 8 * NativeMethods.GetDpiScale(Handle));
        if (!_physics.Grounded) return false;
        _directedDirection = direction;
        _directedUntil = _clock.Elapsed.TotalSeconds + seconds;
        _physics.WalkSpeed = direction * 40 * NativeMethods.GetDpiScale(Handle);
        return true;
    }

    internal void StopDirectedWalk()
    {
        if (_directedUntil <= 0) return;
        _directedUntil = 0; _directedDirection = 0;
        _physics.StopWalking();
        _nextDecision = _clock.Elapsed.TotalSeconds + 2;
        if (State == "walk") SetState("idle");
    }

    private Point GetAnchor(Rect bounds)
    {
        Point requested = _footAnchorPixels?.Invoke() ?? new Point(bounds.Width / 2, bounds.Height - 36 * NativeMethods.GetDpiScale(Handle));
        return new Point(Math.Clamp(requested.X, 0, bounds.Width), Math.Clamp(requested.Y, 0, bounds.Height));
    }
    private Rect GetVisibleBounds(Rect bounds) => _visibleBoundsPixels?.Invoke() ?? new Rect(0, 0, bounds.Width, bounds.Height);
    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_owner.IsVisible) { StopDirectedWalk(); CancelScriptedMotion(); }
        UpdateRendering();
    }
    private void UpdateRendering()
    {
        if (_disposed) return;
        bool active = IsEnabled && !_paused && _owner.IsVisible && Handle != IntPtr.Zero;
        if (_rendering == active) return;
        _lastTick = _clock.Elapsed.TotalSeconds;
        _lastRenderTime = null;
        _rendering = active;
        if (active) CompositionTarget.Rendering += Tick;
        else CompositionTarget.Rendering -= Tick;
    }
    private void ScanPlatforms()
    {
        var scan = Stopwatch.StartNew();
        _snapshot = _platformProvider(Handle); _lastScan = _clock.Elapsed.TotalSeconds;
        _fullScans++; _maxScanMs = Math.Max(_maxScanMs, scan.Elapsed.TotalMilliseconds);
    }

    private bool AdvanceScriptedMotion(double elapsed)
    {
        double dt = Math.Clamp(elapsed, 0, .05);
        // A skin/pack change invalidates this lease. Never mix old geometry and
        // a new clip during an in-flight hand grip.
        if (_motionProfileProvider != null && !ReferenceEquals(_motionProfile, _motionProfileProvider()))
        { CancelScriptedMotion(); return false; }
        // A new clip can cold-decode asynchronously. Keep the previous physical
        // pose until the renderer acknowledges its first actual frame, rather
        // than moving an idle image along an unseen jump/climb trajectory.
        if (MotionAction is string requested && _motionSceneReady?.Invoke(requested) == false)
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (_motionSceneWaitStarted < 0) _motionSceneWaitStarted = now;
            if (_motionPhase == MotionPhase.JumpPrepare)
            {
                var carrier = _physics.Support;
                var current = carrier == null ? null : _snapshot.Platforms.FirstOrDefault(p => p.Handle == carrier.Handle);
                bool sourceDropped = carrier != null && (current == null || current.Top > carrier.Top + .001);
                // Waiting for artwork must not suspend support physics. Moving
                // upward/sideways still carries the crouching pet; disappearance
                // or downward movement cancels the pending jump immediately.
                _physics.Step(dt, _snapshot);
                if (sourceDropped || !_physics.Grounded) { CancelScriptedMotion(); return false; }
            }
            bool targetLost = _climbPlan != null && !(_snapshot.ClimbEdges?.Any(e => e.Handle == _climbPlan.Edge.Handle
                && e.Side == -_motionDirection && e.Top <= e.OwnerBounds.Top + 2) ?? false);
            if (targetLost || now - _motionSceneWaitStarted >= 8) CancelScriptedMotion();
            return false;
        }
        _motionSceneWaitStarted = -1;
        _motionTime += dt;
        if (_motionPhase == MotionPhase.JumpPrepare)
        {
            // Crouching still rides an upward/horizontal carrier. A platform
            // dropping away during preparation immediately resumes gravity.
            _physics.Step(dt, _snapshot);
            if (!_physics.Grounded) { ClearScriptedMotion(); return false; }
            var clip = _motionProfile.Clip(MotionAction!)!;
            SetMotionFrame(MotionAction, Math.Min(clip.TakeoffFrame - 1, clip.FrameAt(_motionTime)));
            if (_motionTime >= clip.PrepareSeconds)
            {
                var plan = _physics.PlanJump(_snapshot, _motionDirection);
                if (plan == null) { ClearScriptedMotion(); return false; }
                _jumpPlan = plan; _physics.Release(plan.Velocity);
                _motionPhase = MotionPhase.JumpAir; _motionTime = 0;
                SetMotionFrame(MotionAction, clip.TakeoffFrame);
            }
            return false;
        }
        if (_motionPhase == MotionPhase.JumpAir)
        {
            var clip = _motionProfile.Clip(MotionAction!)!;
            _physics.Step(dt, _snapshot);
            if (_physics.Grounded)
            {
                _motionPhase = MotionPhase.JumpLand; _motionTime = 0;
                SetMotionFrame(MotionAction, clip.LandingFrame);
            }
            else
            {
                int pose = _motionProfile.IsLegacy ? (_motionTime < .09 ? 1 : _physics.Velocity.Y < -100 ? 2
                    : Math.Abs(_physics.Velocity.Y) <= 100 ? 3 : 4)
                    : clip.AirFrame(_motionTime, _jumpPlan!.Duration);
                SetMotionFrame(MotionAction, pose);
            }
            return false;
        }
        if (_motionPhase == MotionPhase.JumpLand)
        {
            var clip = _motionProfile.Clip(MotionAction!)!;
            _physics.Step(dt, _snapshot);
            if (!_physics.Grounded) { ClearScriptedMotion(); return false; }
            SetMotionFrame(MotionAction, clip.FrameAt(clip.StartSeconds(clip.LandingFrame) + _motionTime));
            if (_motionTime < clip.LandingSeconds) return false;
            ClearScriptedMotion(); _nextDecision = _clock.Elapsed.TotalSeconds + 2;
            return true;
        }
        if (_climbPlan == null) { CancelScriptedMotion(); return false; }
        var edge = _snapshot.ClimbEdges?.FirstOrDefault(e => e.Handle == _climbPlan.Edge.Handle
            && e.Side == -_motionDirection && e.Top <= e.OwnerBounds.Top + 2
            && e.Bottom >= e.OwnerBounds.Top + _climbGripRelativeY - 2);
        if (edge == null) { CancelScriptedMotion(); return false; }
        Vector displacement = edge.OwnerBounds.TopLeft - _climbPreviousOwner.TopLeft;
        double dpi = NativeMethods.GetDpiScale(Handle);
        if (displacement.Length > Math.Max(48 * dpi, 1200 * dt * dpi)) { CancelScriptedMotion(); return false; }
        _climbPreviousOwner = edge.OwnerBounds;
        var topPlatform = _snapshot.Platforms.FirstOrDefault(p => p.Handle == edge.Handle
            && p.Contains(edge.X + _motionDirection * 14 * dpi));
        if (topPlatform == null) { CancelScriptedMotion(); return false; }
        string sideName = _motionDirection < 0 ? "left" : "right";
        if (_motionPhase == MotionPhase.ClimbApproach)
        {
            Point? contact = _motionContactPixels?.Invoke("climb-" + sideName, 0);
            if (contact == null) { CancelScriptedMotion(); return false; }
            double targetX = edge.X - contact.Value.X;
            double dx = targetX - _physics.Bounds.Left;
            double targetY = edge.OwnerBounds.Top + _climbGripRelativeY - contact.Value.Y;
            double dy = targetY - _physics.Bounds.Top;
            var next = new Rect(_physics.Bounds.X + Math.Clamp(dx, -240 * dpi * dt, 240 * dpi * dt),
                _motionProfile.IsLegacy ? targetY : _physics.Bounds.Top + Math.Clamp(dy, -240 * dpi * dt, 240 * dpi * dt),
                _physics.Bounds.Width, _physics.Bounds.Height);
            if (!TryPlaceClimbing(next, "climb")) return false;
            if (Math.Abs(dx) <= 240 * dpi * dt + .1 && (_motionProfile.IsLegacy || Math.Abs(dy) <= 240 * dpi * dt + .1))
            {
                _motionPhase = MotionPhase.Climb; _motionTime = 0;
            }
            return false;
        }
        if (_motionPhase == MotionPhase.Climb)
        {
            _climbGripRelativeY = Math.Max(0, _climbGripRelativeY - 96 * dpi * dt);
            string action = "climb-" + sideName;
            int frame = _motionProfile.FrameAt(action, _motionTime);
            if (_climbGripRelativeY <= .1)
            {
                _motionPhase = MotionPhase.ClimbTop; _motionTime = 0;
                // Publish the new clip first. Its contact point changes from a
                // vertical grip to the ledge, so defer that HWND change until
                // the following tick can observe first-frame readiness.
                SetMotionFrame("climb-top-" + sideName, 0);
                return false;
            }
            Point? contact = _motionContactPixels?.Invoke(action, frame);
            if (contact == null) { CancelScriptedMotion(); return false; }
            var next = new Rect(edge.X - contact.Value.X, edge.OwnerBounds.Top + _climbGripRelativeY - contact.Value.Y,
                _physics.Bounds.Width, _physics.Bounds.Height);
            if (!TryPlaceClimbing(next, _motionPhase == MotionPhase.ClimbTop ? "climb-top" : "climb")) return false;
            SetMotionFrame(action, frame);
            return false;
        }
        if (_motionPhase == MotionPhase.ClimbTop)
        {
            string action = "climb-top-" + sideName;
            var clip = _motionProfile.Clip(action)!;
            int frame = _motionProfile.FrameAt(action, _motionTime);
            Point? contact = _motionContactPixels?.Invoke(action, frame);
            Point? finalContact = _motionContactPixels?.Invoke(action, clip.FrameCount - 1);
            if (contact == null || finalContact == null) { CancelScriptedMotion(); return false; }
            double total = clip.TotalSeconds;
            // The hand first grips the corner, then the final standing pose
            // travels onto the ledge. This keeps the canonical foot inside the
            // actual supported span when normal idle rendering resumes.
            double inset = Math.Abs(finalContact.Value.X - _physics.Anchor.X) + 14 * dpi;
            double progress = Math.Clamp((_motionTime - clip.TransferStartSeconds) / (clip.TransferEndSeconds - clip.TransferStartSeconds), 0, 1);
            double gripX = edge.X + _motionDirection * inset * progress;
            var next = new Rect(gripX - contact.Value.X, edge.OwnerBounds.Top - contact.Value.Y,
                _physics.Bounds.Width, _physics.Bounds.Height);
            if (!TryPlaceClimbing(next, "climb-top")) return false;
            SetMotionFrame(action, frame);
            if (_motionTime < total) return false;
            double footX = next.Left + _physics.Anchor.X;
            if (!topPlatform.Contains(footX)) { CancelScriptedMotion(); return false; }
            _physics.PlaceKinematic(new Rect(next.X, topPlatform.Top - _physics.Anchor.Y, next.Width, next.Height), "idle");
            if (!_physics.TrySnapToSurface(_snapshot, 1)) { CancelScriptedMotion(); return false; }
            ClearScriptedMotion(); _nextDecision = _clock.Elapsed.TotalSeconds + 2;
            // Top-out is controlled climbing rather than a falling impact.
            return false;
        }
        return false;
    }

    private bool TryPlaceClimbing(Rect next, string state)
    {
        if (!_snapshot.WorkAreas.Any(w => w.Contains(DesktopClimbPlanner.WorldBody(next, _physics.Body))))
        {
            CancelScriptedMotion(); return false;
        }
        _physics.PlaceKinematic(next, state);
        return true;
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed || !IsEnabled || _paused || !_owner.IsVisible) { UpdateRendering(); return; }
        if (e is RenderingEventArgs frame)
        {
            // Layout changes can request another callback for the same display
            // frame even after an expensive native scan. Stopwatch proximity
            // cannot identify those duplicates; WPF's frame timestamp can.
            if (_lastRenderTime == frame.RenderingTime) return;
            _lastRenderTime = frame.RenderingTime;
        }
        double now = _clock.Elapsed.TotalSeconds, elapsed = now - _lastTick;
        if (elapsed < .001) return;
        _lastTick = now;
        var tickClock = Stopwatch.StartNew();
        try
        {
            if (IsDragging) { ObserveDrag(); return; }
            double interval = elapsed * 1000;
            _frameCount++; _intervalTotal += interval; _maxInterval = Math.Max(_maxInterval, interval);
            _frameIntervals.Enqueue(interval); if (_frameIntervals.Count > 600) _frameIntervals.Dequeue();
            if (_lastScan < 0 || now - _lastScan >= .1) ScanPlatforms();
            IntPtr trackedHandle = _climbPlan?.Edge.Handle ?? SupportHandle;
            if (_nativePlatforms && trackedHandle != IntPtr.Zero)
            {
                _snapshot = DesktopWindowPlatforms.RefreshSupport(_snapshot, trackedHandle); _supportRefreshes++;
            }
            var actual = NativeMethods.GetWindowBoundsPixels(Handle);
            Point anchor = GetAnchor(actual);
            Rect visible = GetVisibleBounds(actual);
            if (_physics.Bounds.IsEmpty) _physics.Reset(actual, anchor, visible);
            else if (Math.Abs(actual.Width - _physics.Bounds.Width) > .5 || Math.Abs(actual.Height - _physics.Bounds.Height) > .5 || (_physics.Anchor - anchor).Length > .1 || _physics.VisibleBounds != visible)
                _physics.ReconfigureGeometry(actual, anchor, visible);
            if (_directedUntil > 0 && now >= _directedUntil) StopDirectedWalk();
            if (!HasScriptedMotion && _directedUntil <= 0 && _physics.Grounded && now >= _nextDecision)
            {
                _physics.WalkSpeed = _autonomous && _random.Next(3) != 0 ? (_random.Next(2) == 0 ? -1 : 1) * 40 * NativeMethods.GetDpiScale(Handle) : 0;
                _nextDecision = now + 2.5 + _random.NextDouble() * 4;
            }
            // Advance caps exceptional stalls to 50 ms and splits at 120 Hz;
            // HWND positioning happens once per actual composition frame.
            long landingBefore = _physics.LandingSequence;
            bool scriptedThisFrame = HasScriptedMotion;
            bool scriptedLanded = scriptedThisFrame && AdvanceScriptedMotion(elapsed);
            if (!scriptedThisFrame) _physics.Step(elapsed, _snapshot);
            if (scriptedThisFrame)
            {
                // Advancing a grounded pose can change its measured shoe line.
                // Re-anchor in the same display frame, preserving world feet;
                // waiting for the next tick would briefly penetrate the ledge.
                Point poseAnchor = GetAnchor(_physics.Bounds);
                if ((_physics.Anchor - poseAnchor).Length > .1)
                    _physics.ReconfigureGeometry(_physics.Bounds, poseAnchor, GetVisibleBounds(_physics.Bounds));
            }
            // A lost platform or edge ends an explicit request. Never turn a
            // command to walk left into unbounded rightward wandering.
            if (_directedUntil > 0 && (!_physics.Grounded || Math.Sign(_physics.WalkSpeed) != _directedDirection)) StopDirectedWalk();
            var desired = _physics.Bounds;
            int x = (int)Math.Round(desired.X), y = (int)Math.Round(desired.Y);
            if (Math.Abs(actual.Left - x) >= 1 || Math.Abs(actual.Top - y) >= 1)
            {
                IsMovingWindow = true;
                try { NativeMethods.SetWindowPositionPixels(Handle, x, y); }
                finally { IsMovingWindow = false; }
                _needsSave = true; _lastMovement = now; _movedFrames++;
            }
            string state = _motionPhase is MotionPhase.JumpPrepare or MotionPhase.JumpAir or MotionPhase.JumpLand ? "jump"
                : _motionPhase == MotionPhase.ClimbTop ? "climb-top"
                : HasScriptedMotion ? "climb" : _physics.State;
            SetState(state, landed: scriptedLanded || !scriptedThisFrame && _physics.LandingSequence != landingBefore);
            if (_needsSave && now - _lastMovement > .6) SaveIfNeeded();
        }
        catch (Exception ex)
        {
            // A closed/recreated HWND or monitor transition must never leave a
            // runaway animation timer repeatedly throwing on the UI thread.
            IsEnabled = false;
            bool climbing = _climbPlan != null;
            if (climbing) _physics.Release(new Vector());
            try { ClearScriptedMotion(); } catch (Exception cleanup) { Program.Log(cleanup); }
            UpdateRendering(); SetState("idle"); Program.Log(ex);
        }
        finally { _maxTickMs = Math.Max(_maxTickMs, tickClock.Elapsed.TotalMilliseconds); }
    }
    private void SetState(string state, bool landed = false)
    {
        double horizontalSpeed = HasScriptedMotion ? _motionDirection : state == "walk" ? _physics.WalkSpeed : state is "fall" or "jump" ? _physics.Velocity.X : 0;
        bool changed = _animationState.Update(state, horizontalSpeed);
        // The event sees the new grounded state, and runs before the ordinary
        // state notification could briefly request idle/work instead of land.
        if (landed) Landed?.Invoke();
        if (changed) StateChanged?.Invoke(state);
    }
    private void SaveIfNeeded() { if (_needsSave) { _needsSave = false; Settled?.Invoke(); } }
    public void Dispose()
    {
        if (_disposed) return;
        ClearScriptedMotion();
        if (_rendering) CompositionTarget.Rendering -= Tick;
        _rendering = false; _disposed = true; _owner.IsVisibleChanged -= OwnerVisibilityChanged;
    }
}
