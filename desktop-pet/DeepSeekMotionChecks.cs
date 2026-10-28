using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace DesktopPet;

internal static class DeepSeekMotionChecks
{
    public static object Run()
    {
        var checks = new List<string>();
        void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("DeepSeek motion check failed: " + label);
            checks.Add(label);
        }
        var front = new DesktopWindowSurface(new IntPtr(1), new Rect(280, 300, 100, 120), false);
        var rear = new DesktopWindowSurface(new IntPtr(2), new Rect(300, 200, 400, 500));
        var sides = DesktopWindowPlatforms.VisibleSides(new[] { front, rear });
        var left = sides.Where(s => s.Handle == rear.Handle && s.Side == -1).ToArray();
        Require(left.Length == 2 && left[0].Top == 200 && left[0].Bottom == 300 && left[1].Top == 420,
            "front_nonlandable_overlay_splits_rear_vertical_grip_edge");
        Require(sides.Count(s => s.Handle == rear.Handle && s.Side == 1) == 1,
            "occlusion_of_one_side_does_not_hide_other_side");
        Require(DesktopWindowPlatforms.VisibleSides(new[] { front with { Bounds = new Rect(200, 100, 700, 800) }, rear }).Count == 0,
            "fully_occluded_window_has_no_climb_route");
        var work = new Rect(0, 0, 1400, 1000);
        var window = new Rect(235, 620, 120, 160);
        var anchor = new Point(60, 145);
        var body = new Rect(10, 10, 100, 135);
        var grip = new Point(105, 45);
        var surfaces = new[] { new DesktopWindowSurface(new IntPtr(3), new Rect(350, 350, 500, 550)) };
        var snapshot = new DesktopPlatformSnapshot(new[] { work }, DesktopWindowPlatforms.VisibleEdges(surfaces), DesktopWindowPlatforms.VisibleSides(surfaces));
        var plan = DesktopClimbPlanner.Plan(snapshot, window, anchor, body, grip, 1, 1);
        Require(plan != null && plan.Direction == 1 && plan.Grip == new Point(350, 665), "nearby_exposed_side_leads_to_visible_top");
        Require(DesktopClimbPlanner.Plan(snapshot, window, anchor, body, grip, -1, 1) == null,
            "climb_direction_does_not_select_opposite_facing_side");
        Require(DesktopClimbPlanner.Plan(snapshot, window with { X = 100 }, anchor, body, grip, 1, 1) == null,
            "distant_window_does_not_teleport_pet_to_side");
        var blockedSides = snapshot with { ClimbEdges = new[] { snapshot.ClimbEdges![0] with { Top = 500 } } };
        Require(DesktopClimbPlanner.Plan(blockedSides, window, anchor, body, grip, 1, 1) == null,
            "lower_exposed_fragment_is_not_a_continuous_route_to_top");
        Require(DesktopClimbPlanner.Plan(snapshot with { Platforms = Array.Empty<DesktopPlatform>() }, window, anchor, body, grip, 1, 1) == null,
            "climb_requires_exposed_landing_at_top");
        Require(DesktopClimbPlanner.Plan(snapshot, window, anchor, body, grip, 0, 1) == null,
            "invalid_climb_direction_rejected");
        var physics = new DesktopMovementPhysics();
        physics.Reset(new Rect(580, 850, 100, 160), new Point(50, 150), new Rect(0, 0, 100, 150));
        var jumpPlatforms = new[]
        {
            new DesktopPlatform(new IntPtr(4), 210, 500, 820, new Rect(210,820,290,100)),
            new DesktopPlatform(new IntPtr(5), 760, 1060, 800, new Rect(760,800,300,100))
        };
        var jumpSnapshot = new DesktopPlatformSnapshot(new[] { work }, jumpPlatforms);
        Require(physics.TrySnapToSurface(jumpSnapshot, 1), "jump_fixture_starts_grounded_on_floor");
        var right = physics.PlanJump(jumpSnapshot, 1);
        var leftJump = physics.PlanJump(jumpSnapshot, -1);
        Require(right?.Target.Handle == new IntPtr(5) && right.Velocity.X > 0, "right_jump_plans_actual_right_platform");
        Require(leftJump?.Target.Handle == new IntPtr(4) && leftJump.Velocity.X < 0, "left_jump_plans_actual_left_platform");
        Require(physics.Grounded && physics.Velocity.Length == 0, "planning_does_not_release_before_crouch");
        Require(physics.TryJump(jumpSnapshot, 1), "directed_jump_launches_ballistic_path");
        for (int i = 0; i < 240 && !physics.Grounded; i++) physics.Step(1d / 120, jumpSnapshot);
        Require(physics.Support?.Handle == new IntPtr(5) && Math.Abs(physics.Foot.Y - 800) < .01,
            "ballistic_jump_lands_on_requested_visible_top");
        Require(DeepSeekMotionTimeline.FrameAt("climb-left", 0) == 0 && DeepSeekMotionTimeline.FrameAt("climb-left", .151) == 1
            && DeepSeekMotionTimeline.FrameAt("climb-left", .821) == 0, "climb_cycle_uses_asset_timing_and_loops");
        Require(DeepSeekMotionTimeline.FrameAt("climb-top-right", 5) == 5
            && Math.Abs(DeepSeekMotionTimeline.TotalSeconds("climb-top-right") - 1.46) < .001,
            "topout_once_holds_last_pose_with_authored_duration");
        Require(DeepSeekMotionTimeline.ContactPoint("climb-top-right", 0) == new Point(400,128)
            && DeepSeekMotionTimeline.ContactPoint("climb-top-left", 5) == new Point(225,435)
            && DeepSeekMotionTimeline.ContactPoint("jump-left", 0) == null,
            "contacts_match_transformed_512_asset_coordinates");
        var redrawn = BundledSceneAnimations.FindForSkin("deepseek", DeepSeekMotionTimeline.RedrawnPackId);
        if (redrawn != null)
        {
            var timeline = DeepSeekMotionTimeline.ForPack(redrawn);
            Require(!timeline.IsLegacy && timeline.StandingFoot == new Point(256,464), "redrawn_profile_uses_new_fixed_viewport_support_line");
            foreach (var item in timeline.Clips)
            {
                string action = item.Key; var clip = item.Value;
                bool topout = action.StartsWith("climb-top-", StringComparison.Ordinal);
                Require(clip.FrameCount == (topout ? 36 : 20), "full_new_frame_count_" + action);
                Require(Enumerable.Range(0, clip.FrameCount).All(i => clip.FrameAt(clip.StartSeconds(i) + .001) == i),
                    "all_authored_frame_boundaries_reachable_" + action);
                if (action.StartsWith("climb", StringComparison.Ordinal))
                {
                    Require(clip.Contacts?.Length == clip.FrameCount && clip.Contacts.All(p => p.X > 0 && p.X < 512 && p.Y > 0 && p.Y <= 512),
                        "every_new_grip_has_own_calibration_" + action);
                    Require(clip.Contacts![0] != DeepSeekMotionTimeline.ContactPoint(action, 0), "new_grips_do_not_reuse_legacy_coordinates_" + action);
                    if (!topout) Require(timeline.FrameAt(action, clip.TotalSeconds + .001) == 0, "new_climb_loops_full_cycle_" + action);
                    else Require(clip.FrameAt(10) == 35 && clip.TransferEndSeconds < clip.TotalSeconds,
                        "new_topout_transfers_before_last_standing_pose_" + action);
                }
                else
                {
                    Require(clip.PrepareSeconds > 0 && clip.LandingSeconds > 0 && clip.TakeoffFrame < clip.LandingFrame,
                        "new_jump_has_crouch_air_and_recovery_" + action);
                    Require(clip.GroundFootY?.Length == clip.FrameCount && clip.GroundFootY.Max() > 464,
                        "grounded_jump_uses_actual_shoes_instead_of_static_support_line_" + action);
                    Require(Enumerable.Range(0,1000).Select(i => clip.AirFrame(i / 1000d, 1)).Distinct().Count() == clip.LandingFrame - clip.TakeoffFrame,
                        "new_jump_uses_every_airborne_pose_" + action);
                }
            }
            Require(ReferenceEquals(DeepSeekMotionTimeline.ForPack(redrawn with { Id = "builtin-deepseek-small" }), DeepSeekMotionTimeline.Legacy),
                "switching_back_restores_legacy_motion_profile");
        }
        return new { passed = true, count = checks.Count, checks, nativeWindowsTouched = false };
    }
}
