using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace DesktopPet;

// The artwork keeps its original 512 square viewport. These are the authored
// contact points after the common scale/offset in the source asset manifest;
// never normalize each pose to its nontransparent bounding box.
internal static class DeepSeekMotionTimeline
{
    internal const string RedrawnPackId = "builtin-deepseek-small-redrawn-24fps";
    private static readonly int[] Jump = [170, 90, 130, 160, 120, 180];
    private static readonly int[] Climb = [150, 120, 140, 150, 120, 140];
    private static readonly int[] Top = [220, 180, 220, 220, 200, 420];
    private static readonly Dictionary<string, Point[]> Contacts = new(StringComparer.Ordinal)
    {
        ["climb-right"] = [new(420,178), new(376,218), new(397,163), new(400,99), new(387,199), new(370,108)],
        ["climb-left"] = [new(132,186), new(140,223), new(137,161), new(155,141), new(148,210), new(129,188)],
        ["climb-top-right"] = [new(400,128), new(406,171), new(350,358), new(345,393), new(332,408), new(333,446)],
        ["climb-top-left"] = [new(141,121), new(98,150), new(149,327), new(165,334), new(204,398), new(225,435)]
    };
    public static readonly DeepSeekMotionProfile Legacy = new(true, 512, null,
        new[] { "jump-left", "jump-right", "climb-left", "climb-right", "climb-top-left", "climb-top-right" }
        .ToDictionary(action => action, action => new DeepSeekMotionClip(Durations(action),
            Contacts.GetValueOrDefault(action), 1, 5, .4, 1.04), StringComparer.Ordinal));
    public static readonly DeepSeekMotionProfile Unavailable = new(false, 512, null, new());

    public static DeepSeekMotionProfile ForPack(SceneAnimationPack pack)
    {
        if (pack.Id != RedrawnPackId) return Legacy;
        string path = Path.Combine(pack.RootDirectory, "motion.json");
        using var stream = SceneFileAccess.OpenChecked(path);
        if (stream.Length > 256 * 1024) throw new InvalidDataException("Motion metadata is too large.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("canvasSize").GetInt32() != 512)
            throw new InvalidDataException("Unsupported motion coordinate system.");
        Point ReadPoint(JsonElement value)
        {
            var xy = value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (xy.Length != 2 || xy.Any(v => !double.IsFinite(v) || v < 0 || v > 512))
                throw new InvalidDataException("Invalid motion contact.");
            return new Point(xy[0], xy[1]);
        }
        var clips = new Dictionary<string, DeepSeekMotionClip>(StringComparer.Ordinal);
        foreach (string action in Legacy.Clips.Keys)
        {
            var value = root.GetProperty("clips").GetProperty(action);
            int[] durations = value.GetProperty("durationsMs").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            int count = action.StartsWith("climb-top-", StringComparison.Ordinal) ? 36 : 20;
            if (!pack.Actions.TryGetValue(action, out var scene) || scene.Loop.Frames.Length != count
                || durations.Length != count || durations.Any(ms => ms is < 17 or > 1000)
                || scene.Loop.DurationsMs == null || !durations.SequenceEqual(scene.Loop.DurationsMs))
                throw new InvalidDataException("Motion frame timing does not match artwork: " + action);
            Point[]? contacts = value.TryGetProperty("contacts", out var points) ? points.EnumerateArray().Select(ReadPoint).ToArray() : null;
            if (action.StartsWith("climb", StringComparison.Ordinal) && contacts?.Length != count)
                throw new InvalidDataException("Missing per-frame contact positions: " + action);
            int takeoff = 0, landing = count - 1;
            double[]? groundFootY = null;
            if (value.TryGetProperty("groundFootY", out var groundPoints))
            {
                groundFootY = groundPoints.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (groundFootY.Length != count || groundFootY.Any(y => !double.IsFinite(y) || y <= 0 || y > 512))
                    throw new InvalidDataException("Invalid grounded foot calibration.");
            }
            if (action.StartsWith("jump-", StringComparison.Ordinal))
            {
                takeoff = value.GetProperty("takeoffFrame").GetInt32(); landing = value.GetProperty("landingFrame").GetInt32();
                if (takeoff < 1 || landing <= takeoff || landing >= count) throw new InvalidDataException("Invalid jump stages.");
                if (groundFootY == null)
                    throw new InvalidDataException("Missing grounded jump foot calibration.");
            }
            double transferStart = 0, transferEnd = 0;
            if (action.StartsWith("climb-top-", StringComparison.Ordinal))
            {
                int first = value.GetProperty("transferStartFrame").GetInt32(), last = value.GetProperty("transferEndFrame").GetInt32();
                if (first < 0 || last <= first || last >= count) throw new InvalidDataException("Invalid top-out stages.");
                transferStart = durations.Take(first).Sum() / 1000d; transferEnd = durations.Take(last).Sum() / 1000d;
            }
            clips.Add(action, new DeepSeekMotionClip(durations, contacts, takeoff, landing, transferStart, transferEnd, groundFootY));
        }
        return new DeepSeekMotionProfile(false, 512, ReadPoint(root.GetProperty("standingFoot")), clips);
    }
    public static Point? ContactPoint(string action, int frame) => Contacts.TryGetValue(action, out var points)
        && frame >= 0 && frame < points.Length ? points[frame] : null;
    private static int[] Durations(string action) => action.StartsWith("climb-top-", StringComparison.Ordinal) ? Top
        : action.StartsWith("climb-", StringComparison.Ordinal) ? Climb : Jump;
    public static double TotalSeconds(string action) => Durations(action).Sum() / 1000d;
    public static int FrameAt(string action, double elapsedSeconds)
    {
        var durations = Durations(action);
        double time = Math.Max(0, elapsedSeconds) * 1000;
        bool loop = action is "climb-right" or "climb-left";
        if (loop) time %= durations.Sum();
        for (int i = 0; i < durations.Length - 1; i++)
        {
            if (time < durations[i]) return i;
            time -= durations[i];
        }
        return durations.Length - 1;
    }
}

internal sealed record DeepSeekMotionClip(int[] DurationsMs, Point[]? Contacts, int TakeoffFrame,
    int LandingFrame, double TransferStartSeconds, double TransferEndSeconds, double[]? GroundFootY = null)
{
    public int FrameCount => DurationsMs.Length;
    public double TotalSeconds => DurationsMs.Sum() / 1000d;
    public double StartSeconds(int frame) => DurationsMs.Take(frame).Sum() / 1000d;
    public double PrepareSeconds => StartSeconds(TakeoffFrame);
    public double LandingSeconds => TotalSeconds - StartSeconds(LandingFrame);
    public int FrameAt(double elapsed, bool loop = false)
    {
        double time = Math.Max(0, elapsed) * 1000;
        if (loop) time %= DurationsMs.Sum();
        for (int i = 0; i < FrameCount - 1; i++)
        {
            if (time < DurationsMs[i]) return i;
            time -= DurationsMs[i];
        }
        return FrameCount - 1;
    }
    public int AirFrame(double elapsed, double physicalFlightSeconds)
    {
        double progress = Math.Clamp(elapsed / Math.Max(.001, physicalFlightSeconds), 0, .999999);
        double authoredAir = StartSeconds(LandingFrame) - PrepareSeconds;
        return Math.Clamp(FrameAt(PrepareSeconds + progress * authoredAir), TakeoffFrame, LandingFrame - 1);
    }
}

internal sealed record DeepSeekMotionProfile(bool IsLegacy, int CanvasSize, Point? StandingFoot,
    Dictionary<string, DeepSeekMotionClip> Clips)
{
    public DeepSeekMotionClip? Clip(string action) => Clips.GetValueOrDefault(action);
    public Point? ContactPoint(string action, int frame) => Clip(action)?.Contacts is Point[] points
        && frame >= 0 && frame < points.Length ? points[frame] : null;
    public int FrameAt(string action, double elapsed) => Clip(action)?.FrameAt(elapsed, action is "climb-left" or "climb-right") ?? 0;
}

internal sealed record DesktopClimbPlan(DesktopClimbEdge Edge, Point Grip, int Direction);

internal static class DesktopClimbPlanner
{
    // Only a nearby, continuous exposed side leading to an exposed top is
    // eligible. A lower segment behind another window is not a climb route.
    public static DesktopClimbPlan? Plan(DesktopPlatformSnapshot snapshot, Rect window, Point footAnchor,
        Rect body, Point localGrip, int direction, double dpi)
    {
        if (direction is not (-1 or 1) || snapshot.ClimbEdges == null) return null;
        Point foot = new(window.X + footAnchor.X, window.Y + footAnchor.Y);
        Point hand = new(window.X + localGrip.X, window.Y + localGrip.Y);
        var candidates = new List<(DesktopClimbPlan Plan, double Score)>();
        foreach (var side in snapshot.ClimbEdges)
        {
            if (side.Side != -direction || side.Top > side.OwnerBounds.Top + 2) continue;
            if ((side.X - foot.X) * direction < -8 * dpi) continue;
            if (Math.Abs(side.X - hand.X) > 64 * dpi) continue;
            if (hand.Y < side.Top + 28 * dpi || hand.Y > side.Bottom - 6 * dpi) continue;
            if (hand.Y - side.Top > 720 * dpi) continue;
            if (!snapshot.Platforms.Any(p => p.Handle == side.Handle && p.Contains(side.X + direction * 14 * dpi))) continue;
            var grabbed = new Rect(side.X - localGrip.X, window.Y, window.Width, window.Height);
            var top = new Rect(side.X + direction * 14 * dpi - footAnchor.X,
                side.OwnerBounds.Top - footAnchor.Y, window.Width, window.Height);
            bool fits = snapshot.WorkAreas.Any(w => w.Contains(WorldBody(grabbed, body)) && w.Contains(WorldBody(top, body)));
            if (!fits) continue;
            candidates.Add((new DesktopClimbPlan(side, new Point(side.X, hand.Y), direction),
                Math.Abs(side.X - hand.X) + (hand.Y - side.Top) * .25));
        }
        return candidates.OrderBy(c => c.Score).Select(c => c.Plan).FirstOrDefault();
    }
    internal static Rect WorldBody(Rect window, Rect body) => new(window.X + body.X, window.Y + body.Y, body.Width, body.Height);
}
