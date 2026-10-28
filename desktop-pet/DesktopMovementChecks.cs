using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DesktopPet;

internal static class DesktopMovementChecks
{
    // No HWND enumeration, mouse input, timers, or settings writes in these tests.
    public static object Run()
    {
        var checks = new List<string>();
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Desktop movement check failed: " + label);
            checks.Add(label);
        }
        IntPtr rear = new(11), front = new(12);
        var edges = DesktopWindowPlatforms.VisibleEdges(new[]
        {
            new DesktopWindowSurface(front, new Rect(200, 200, 150, 400), false),
            new DesktopWindowSurface(rear, new Rect(100, 400, 500, 300))
        });
        Require(edges.Count == 2 && edges.All(p => p.Handle == rear) && edges[0].Right == 200 && edges[1].Left == 350,
            "front occluder splits rear platform; non-landable overlays still occlude");
        Require(!edges.Any(p => p.Contains(275)) && edges.Any(p => p.Contains(450)), "covered foot cannot land; exposed edge remains usable");
        Require(DesktopWindowPlatforms.VisibleEdges(new[]
        {
            new DesktopWindowSurface(front, new Rect(0, 100, 800, 800), false),
            new DesktopWindowSurface(rear, new Rect(100, 400, 500, 300))
        }).Count == 0, "fully covered window has no platform");

        var work = new Rect(0, 0, 1200, 1000);
        var platform = new DesktopPlatform(rear, 100, 700, 450, new Rect(100, 450, 600, 300));
        var snapshot = new DesktopPlatformSnapshot(new[] { work }, new[] { platform });
        var physics = new DesktopMovementPhysics();
        var anchor = new Point(50, 90); // 10px of UI beneath character foot
        physics.Reset(new Rect(300, 80, 100, 100), anchor);
        for (int i = 0; i < 80 && !physics.Grounded; i++) physics.Step(1.0 / 30, snapshot);
        Require(physics.Support?.Handle == rear && Close(physics.Foot.Y, 450) && Close(physics.Bounds.Top, 360),
            "gravity lands with visual foot on window top instead of HWND bottom");
        Require(physics.LandingSequence == 1, "a descending air-to-window contact produces one physical landing");
        var translated = new DesktopPlatform(rear, 180, 780, 410, new Rect(180, 410, 600, 300));
        double oldX = physics.Foot.X;
        physics.Step(1.0 / 30, new DesktopPlatformSnapshot(new[] { work }, new[] { translated }));
        Require(Close(physics.Foot.X, oldX + 80) && Close(physics.Foot.Y, 410), "standing pet follows carrier movement");
        Require(physics.LandingSequence == 1, "upward carrier motion does not manufacture another landing");
        var resized = new DesktopPlatform(rear, 180, 1080, 410, new Rect(180, 410, 900, 300));
        double relative = (physics.Foot.X - 180) / 600;
        physics.Step(1.0 / 30, new DesktopPlatformSnapshot(new[] { work }, new[] { resized }));
        Require(Close(physics.Foot.X, 180 + relative * 900), "carrier resize preserves proportional foot location");
        var noWindows = new DesktopPlatformSnapshot(new[] { work }, Array.Empty<DesktopPlatform>());
        double beforeFall = physics.Foot.Y;
        physics.Step(1.0 / 30, noWindows);
        Require(physics.Support == null && physics.Foot.Y > beforeFall, "closed/minimized/occluded carrier releases to gravity");
        for (int i = 0; i < 180; i++) physics.Step(1.0 / 30, noWindows);
        Require(physics.OnFloor && Close(physics.Foot.Y, work.Bottom) && Close(physics.Bounds.Bottom, work.Bottom + 10),
            "visible feet rest on taskbar boundary while transparent HWND footer may extend below it");
        Require(physics.LandingSequence == 2, "window removal produces exactly one later floor landing despite continued grounded frames");

        physics.Reset(new Rect(300, 365, 100, 100), anchor);
        Require(physics.TrySnapToSurface(snapshot, 8) && physics.Support?.Handle == rear && Close(physics.Foot.Y, 450),
            "slow placement five pixels below a visible edge snaps onto it");
        physics.Reset(new Rect(300, 370, 100, 100), anchor);
        Require(!physics.TrySnapToSurface(snapshot, 8), "placement outside tolerance does not teleport onto a window");
        physics.Reset(new Rect(300, 910, 100, 100), anchor);
        Require(physics.TrySnapToSurface(noWindows, 8) && physics.OnFloor && Close(physics.Foot.Y, 1000),
            "enabling movement with feet already on taskbar immediately establishes ground");
        Require(physics.LandingSequence == 2, "reset and slow drag surface/floor snapping do not publish physical landings");
        physics.Reset(new Rect(300, 910, 100, 100), anchor);
        for (int i = 0; i < 10; i++) physics.Step(1.0 / 60, noWindows);
        Require(physics.OnFloor && physics.LandingSequence == 2, "starting at the floor without a prior snap does not fabricate a fall-and-land impact");

        // Actual artwork may leave substantial transparent padding inside the HWND.
        var art = new Rect(20, 50, 60, 40);
        var artAnchor = new Point(50, 90);
        var highPlatform = new DesktopPlatform(rear, 0, 1200, 60, new Rect(0, 60, 1200, 600));
        var highSnapshot = new DesktopPlatformSnapshot(new[] { work }, new[] { highPlatform });
        physics.Reset(new Rect(300, -30, 100, 120), artAnchor, art);
        Require(physics.TrySnapToSurface(highSnapshot, 8) && physics.Support?.Handle == rear && physics.Bounds.Top < work.Top,
            "transparent head padding may extend outside screen when visible character fits above platform");
        var footBeforeResize = physics.Foot;
        physics.ReconfigureGeometry(new Rect(0, 0, 140, 160), new Point(70, 125), new Rect(25, 85, 90, 40));
        Require(physics.Support?.Handle == rear && (physics.Foot - footBeforeResize).Length < .01,
            "geometry and DPI reconfiguration preserve established world foot and support");
        for (int i = 0; i < 600; i++) physics.Step(1.0 / 60, highSnapshot);
        Require(physics.Support?.Handle == rear && (physics.Foot - footBeforeResize).Length < .01,
            "stationary carrier retains exact grounding for ten seconds at display cadence");
        Require(physics.LandingSequence == 2, "DPI geometry refresh and stationary support do not replay landing");

        var bodyClamp = DesktopWindowPlatforms.ClampBodyToWorkAreas(new Rect(-100, 950, 100, 120), new Rect(20, 50, 60, 40), new[] { work });
        Require(Close(bodyClamp.Left + 20, work.Left) && Close(bodyClamp.Top + 90, work.Bottom),
            "work area clamp uses opaque body sides and feet rather than transparent HWND margins");
        physics.Reset(new Rect(300, 360, 100, 100), anchor);
        Require(physics.TrySnapToSurface(snapshot, 8), "moving carrier fixture begins grounded");
        for (int i = 1; i <= 240; i++)
        {
            // Horizontal translation and upward carrying preserve contact.
            // A platform moving down now intentionally leaves the pet to fall.
            double dx = i * .75, dy = -i * .1;
            var moving = new DesktopPlatform(rear, 100 + dx, 700 + dx, 450 + dy, new Rect(100 + dx, 450 + dy, 600, 300));
            physics.Step(1.0 / 60, new DesktopPlatformSnapshot(new[] { work }, new[] { moving }));
            if (physics.Support?.Handle != rear || !Close(physics.Foot.X, 350 + dx) || !Close(physics.Foot.Y, 450 + dy))
                throw new InvalidOperationException("Continuous carrier tracking lost support at frame " + i);
        }
        Require(true, "continuous horizontal and upward carrier movement stays attached without accumulated drift");

        foreach (int hz in new[] { 30, 60, 120 })
        {
            double dt = 1.0 / hz;
            DesktopPlatformSnapshot CarrierAt(double dx, double down) => new(new[] { work }, new[]
            {
                new DesktopPlatform(rear, 100 + dx, 700 + dx, 450 + down, new Rect(100 + dx, 450 + down, 600, 300))
            });
            DesktopMovementPhysics GroundedPet()
            {
                var pet = new DesktopMovementPhysics();
                pet.Reset(new Rect(300, 360, 100, 100), anchor);
                if (!pet.TrySnapToSurface(snapshot, 8)) throw new InvalidOperationException("Downward carrier fixture could not establish contact.");
                return pet;
            }

            var fallingPet = GroundedPet();
            double oneFrameDrop = .5 * fallingPet.Gravity * dt * dt;
            fallingPet.Step(dt, CarrierAt(0, 1));
            Require(fallingPet.Support == null && fallingPet.State == "fall" &&
                Math.Abs(fallingPet.Foot.Y - 450 - oneFrameDrop) < .0001 && fallingPet.Foot.Y < 451,
                $"{hz}Hz: even a one-pixel downward carrier move releases support and starts gravity on the first frame");

            fallingPet = GroundedPet();
            fallingPet.Step(dt, CarrierAt(0, 120));
            Require(fallingPet.Support == null && Math.Abs(fallingPet.Foot.Y - 450 - oneFrameDrop) < .0001 && fallingPet.Foot.Y < 452,
                $"{hz}Hz: a large downward carrier move does not teleport the pet to its new top");

            fallingPet = GroundedPet();
            double previousSpeed = 0;
            for (int i = 1; i <= hz / 2; i++)
            {
                double elapsed = i * dt;
                fallingPet.Step(dt, CarrierAt(0, 600 * elapsed));
                double expectedY = 450 + .5 * fallingPet.Gravity * elapsed * elapsed;
                if (fallingPet.Support != null || fallingPet.State != "fall" ||
                    Math.Abs(fallingPet.Foot.Y - expectedY) > .0001 || fallingPet.Velocity.Y <= previousSpeed)
                    throw new InvalidOperationException($"{hz}Hz: repeated downward carrier motion reset or bypassed gravity at frame {i}.");
                previousSpeed = fallingPet.Velocity.Y;
            }
            Require(Math.Abs(fallingPet.Velocity.Y - 850) < .0001 && Close(fallingPet.Foot.Y, 662.5),
                $"{hz}Hz: continuous fast downward dragging preserves accumulating gravity and frame-rate-independent free fall");

            var stoppedCarrier = CarrierAt(0, 300);
            for (int i = 0; i < hz; i++) fallingPet.Step(dt, stoppedCarrier);
            Require(fallingPet.Support?.Handle == rear && Close(fallingPet.Foot.Y, 750) && fallingPet.Velocity.Length == 0,
                $"{hz}Hz: falling pet can naturally catch the same window and remain grounded after it stops moving down");
            Require(fallingPet.LandingSequence == 1, $"{hz}Hz: downward carrier release triggers exactly one landing when the pet catches it again");
            fallingPet.Step(dt, CarrierAt(0, 360));
            for (int i = 0; i < hz; i++) fallingPet.Step(dt, CarrierAt(0, 360));
            Require(fallingPet.LandingSequence == 2 && fallingPet.Grounded, $"{hz}Hz: losing support after landing arms a fresh one-shot landing");

            fallingPet = GroundedPet();
            fallingPet.Step(dt, CarrierAt(80, 60));
            Require(fallingPet.Support == null && Close(fallingPet.Foot.X, 350) &&
                Math.Abs(fallingPet.Foot.Y - 450 - oneFrameDrop) < .0001,
                $"{hz}Hz: diagonal downward window movement does not transfer its horizontal displacement to the released pet");

            fallingPet = GroundedPet();
            fallingPet.Step(dt, CarrierAt(0, .0005));
            for (int i = 0; i < hz; i++) fallingPet.Step(dt, CarrierAt(0, .0005));
            Require(fallingPet.Support?.Handle == rear && Math.Abs(fallingPet.Foot.Y - 450.0005) < .000001 && fallingPet.Velocity.Length == 0,
                $"{hz}Hz: sub-tolerance numerical changes retain stationary support without intermittent falling");
        }

        var coarse = new DesktopMovementPhysics(); var smooth = new DesktopMovementPhysics();
        coarse.Reset(new Rect(300, 20, 100, 100), anchor); smooth.Reset(new Rect(300, 20, 100, 100), anchor);
        for (int i = 0; i < 15; i++) coarse.Step(1.0 / 30, noWindows);
        for (int i = 0; i < 30; i++) smooth.Step(1.0 / 60, noWindows);
        Require((coarse.Foot - smooth.Foot).Length < .05 && (coarse.Velocity - smooth.Velocity).Length < .05,
            "120Hz substeps preserve gravity timing when display cadence changes from 30 to 60Hz");

        physics.Reset(new Rect(640, 360, 100, 100), anchor);
        physics.Step(1.0 / 30, snapshot);
        physics.WalkSpeed = 240;
        for (int i = 0; i < 100; i++) physics.Step(1.0 / 30, snapshot);
        Require(physics.Support != null && physics.Foot.X >= 106 && physics.Foot.X <= 694, "walking turns at exposed platform boundaries");

        var walkingPet = new DesktopMovementPhysics();
        var animation = new DesktopMovementAnimationState();
        walkingPet.Reset(new Rect(640, 360, 100, 100), anchor);
        Require(walkingPet.TrySnapToSurface(snapshot, 8), "directional animation fixture starts on a window");
        walkingPet.WalkSpeed = 40;
        walkingPet.Step(1.0 / 60, snapshot);
        Require(walkingPet.State == "walk" && walkingPet.Velocity.X == 0 && animation.Update(walkingPet.State, walkingPet.WalkSpeed) && animation.FacingDirection == 1,
            "right walk animation follows grounded walk speed even though ballistic horizontal velocity is zero");
        Require(!animation.Update(walkingPet.State, walkingPet.WalkSpeed), "unchanged walk direction does not restart the animation every frame");
        for (int i = 0; i < 30 && walkingPet.WalkSpeed > 0; i++) walkingPet.Step(1.0 / 60, snapshot);
        Require(walkingPet.State == "walk" && walkingPet.WalkSpeed < 0 && animation.Update(walkingPet.State, walkingPet.WalkSpeed) && animation.FacingDirection == -1,
            "window edge turnaround publishes a direction change while movement remains walking");
        double turnX = walkingPet.Foot.X;
        walkingPet.Step(1.0 / 60, snapshot);
        Require(walkingPet.Foot.X < turnX && !animation.Update(walkingPet.State, walkingPet.WalkSpeed),
            "left animation remains selected as the pet travels away from the window edge");
        walkingPet.WalkSpeed = 0;
        walkingPet.Step(1.0 / 60, snapshot);
        Require(walkingPet.State == "idle" && animation.Update(walkingPet.State, 0) && animation.FacingDirection == -1,
            "stopping movement publishes idle while preserving the last facing direction");
        walkingPet.Release(new Vector(250, -100));
        Require(animation.Update(walkingPet.State, walkingPet.Velocity.X) && animation.State == "jump" && animation.FacingDirection == 1,
            "airborne facing follows throw velocity independently of stored walking speed");
        Require(animation.Update("idle", 0) && !animation.Update("idle", 0) && animation.FacingDirection == 1,
            "pause or disable clears the movement animation once without changing its facing");

        var landingPet = new DesktopMovementPhysics();
        landingPet.Reset(new Rect(300, 300, 100, 100), anchor);
        landingPet.WalkSpeed = 40;
        landingPet.Release(new Vector(0, 1300));
        landingPet.Step(.05, snapshot);
        Require(landingPet.LandingSequence == 1 && landingPet.State == "idle" && landingPet.WalkSpeed == 0 && Close(landingPet.Foot.X, 350),
            "touchdown cancels the old walking decision before remaining substeps so landing pose stays in place");
        long landingBeforeReconfigure = landingPet.LandingSequence;
        landingPet.ReconfigureGeometry(new Rect(0, 0, 120, 140), new Point(60, 125));
        landingPet.Step(1.0 / 60, snapshot);
        Require(landingPet.LandingSequence == landingBeforeReconfigure, "changing animation contact geometry on touchdown does not retrigger the landing event");

        physics.Reset(new Rect(300, 300, 100, 100), anchor);
        physics.Release(new Vector(300, 1500));
        physics.Step(.05, snapshot);
        Require(physics.Support?.Handle == rear && Close(physics.Foot.Y, 450), "fast descent uses swept crossing and does not tunnel through platform");
        physics.Reset(new Rect(300, 600, 100, 100), anchor);
        physics.Release(new Vector(0, -1100));
        for (int i = 0; i < 10; i++) physics.Step(1.0 / 30, snapshot);
        Require(physics.Support == null && physics.Foot.Y < 450, "upward jump passes through top edge without sticking beneath it");

        physics.Reset(new Rect(500, 200, 100, 100), anchor);
        physics.Step(20, noWindows);
        Require(physics.Bounds.Y < 203, "stalled clock caps elapsed physics to 50ms");

        var screens = new[] { new Rect(-1920, 0, 1920, 1080), new Rect(0, -240, 2560, 1440) };
        var negative = DesktopWindowPlatforms.ClampToWorkAreas(new Rect(-2000, 1000, 220, 300), screens);
        Require(negative.Left == -1920 && negative.Bottom == 1080, "negative-coordinate physical monitor bounds clamp correctly");
        var gapScreens = new[] { new Rect(0, 0, 1000, 800), new Rect(1300, 0, 1500, 1000) };
        var gap = DesktopWindowPlatforms.ClampToWorkAreas(new Rect(1100, 400, 100, 100), gapScreens);
        Require(gapScreens.Any(s => s.Contains(gap)), "monitor gap never becomes a virtual landing area");
        var scaled = DesktopWindowPlatforms.ClampToWorkAreas(new Rect(2500, 1050, 300, 450), screens);
        Require(scaled.Right == 2560 && scaled.Bottom == 1200, "high-DPI physical size stays within target monitor without global DIP conversion");

        physics.Reset(new Rect(650, 350, 100, 100), anchor);
        physics.Release(new Vector(0, 0));
        for (int i = 0; i < 120; i++) physics.Step(1.0 / 30, noWindows);
        var jumpTarget = new DesktopPlatform(front, 800, 1150, 800, new Rect(800, 800, 350, 180));
        var jumpSnapshot = new DesktopPlatformSnapshot(new[] { work }, new[] { jumpTarget });
        long beforeJumpLanding = physics.LandingSequence;
        Require(physics.TryJump(jumpSnapshot), "reachable target produces a ballistic jump plan");
        bool descendingAfterJump = false;
        for (int i = 0; i < 120 && physics.Support == null; i++)
        {
            physics.Step(1.0 / 30, jumpSnapshot);
            if (physics.State == "jump" && physics.LandingSequence != beforeJumpLanding)
                throw new InvalidOperationException("Ascending jump incorrectly produced a landing.");
            descendingAfterJump |= physics.State == "fall";
        }
        Require(physics.Support?.Handle == front, "ballistic jump actually lands on chosen window");
        Require(descendingAfterJump && physics.LandingSequence == beforeJumpLanding + 1, "ascending jump emits no landing until the real descending part of the arc contacts its target once");
        var unreachable = new DesktopPlatform(new IntPtr(13), 100, 500, 80, new Rect(100, 80, 400, 400));
        Require(!physics.TryJump(new DesktopPlatformSnapshot(new[] { work }, new[] { unreachable })), "unreachable or head-clipping target is rejected");

        var velocity = new DesktopDragVelocity();
        velocity.Add(new Point(0, 0), 0); velocity.Add(new Point(40, -20), .04); velocity.Add(new Point(100, -50), .10);
        var throwSpeed = velocity.Get(.10);
        Require(Close(throwSpeed.X, 1000) && Close(throwSpeed.Y, -500), "throw velocity uses elapsed seconds rather than event count");
        velocity.Add(new Point(100, -50), .4);
        Require(velocity.Get(.4).Length == 0, "holding still before release discards stale throw velocity");
        velocity.Clear(); velocity.Add(new Point(0, 0), 1); velocity.Add(new Point(10000, 10000), 1.02);
        Require(velocity.Get(1.02).X == 1100 && velocity.Get(1.02).Y == 1300, "throw speed is bounded for safety and numerical stability");
        return new { passed = true, count = checks.Count, checks, nativeWindowsTouched = false };
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) < .05;
}
