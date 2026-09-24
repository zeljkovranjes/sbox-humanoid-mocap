using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Keeps a body capture from hovering over the floor or sinking into it for moments too short for
/// <see cref="CaptureGround"/>, which follows only the floor's slow drift and deliberately leaves anything
/// brief alone, since brief rises are usually jumps. After a backflip the landing hovered 6 to 11 cm up for
/// half a second, and a performer sitting on the floor sat 15 cm above it.
///
/// A hover is told from a jump by how it moves: nothing leaves the floor by a few centimetres and stays
/// there. A jump that peaks at 11 cm is airborne for under 0.3 s and spends only a frame or two that low on
/// its way up and down, so a stretch of at least <see cref="MinimumHoverSeconds"/> in which the body's lowest
/// point stays between <see cref="ToleranceCm"/> and <see cref="MaximumHoverCm"/> is a hover (a real hop that
/// high passes through that band in a tenth of a second on each side), and the body is
/// lowered onto the floor there. Any frame in which part of the body is below the floor is raised to it.
///
/// A stretch in which every part of the body rises above <see cref="MaximumHoverCm"/> is a flight: standing,
/// crouching or a handstand always leaves something lower. A flight is given the height gravity requires. The network underestimates how far the
/// hips travel up: a backflip airborne for 0.55 s rose 14 to 20 cm, where free fall for that long rises 35 to
/// 40 cm, and the flip turned almost in place. Between the last frame on the floor and the first one back, the
/// hips follow the free-fall arc through those two frames instead.
/// Corrections ease in and out over <see cref="RampSeconds"/>. Heights are of joints, the floor is the height
/// of the lowest joint in the target's rest pose, and only the root moves.</summary>
public static class CaptureHover
{
    public const float ToleranceCm = 2, MaximumHoverCm = 40, TouchCm = 20;
    public const double MinimumHoverSeconds = .25, RampSeconds = .1, MaximumFlightSeconds = 1.2;
    /// <summary>A takeoff or landing crosses TouchCm to MaximumHoverCm within this; longer is a hover.</summary>
    public const double EdgeSeconds = .15, SmoothSeconds = .06;
    /// <summary>Cosine of the knee bend below which a leg counts as standing (60 degrees: a relaxed stance on a board
    /// measured 45 to 53, a tucked jump 86 to 116), and how level
    /// the ankles must be, for the standing test.</summary>
    public const float StraightKneeCosine = .5f, LevelAnklesCm = 12;

    /// <returns>The number of frames moved.</returns>
    /// <param name="contact">Per frame, whether the capture's contact tracks put a foot on the floor, when known (unused).</param>
    public static int Apply( List<XForm[]> frames, TargetRig target, TargetUpAxis axis, float fps, bool[] contact = null )
    {
        if ( frames.Count < 3 || !(fps > 0) ) return 0;
        var rig = target.Skeleton;
        var up = axis == TargetUpAxis.YUpCm ? Vector3.UnitY : Vector3.UnitZ;
        var toCm = axis == TargetUpAxis.ZUpEngine ? 2.54f : 1f;
        var body = Enum.GetValues<BoneRole>().Select( target.BoneForRole ).Where( b => b is not null ).Select( b => b.Value ).Distinct().ToArray();
        if ( body.Length < 8 ) return 0;
        var floor = body.Min( b => Vector3.Dot( rig.RestWorld[b].Pos, up ) );
        var lowest = new float[frames.Count]; var hipsHeight = new float[frames.Count]; var world = new XForm[rig.Count];
        var hips = target.BoneForRole( BoneRole.Hips );
        // Standing: both legs straight and both ankles level. Nobody holds that in the air for long (jumps tuck
        // or swing the legs), so it marks the floor even where a capture's height has drifted: a slowed ollie
        // landed on its board but stayed 40 cm up, and its contact tracks called every frame planted.
        var legs = new[] { (BoneRole.UpperLegL, BoneRole.LowerLegL, BoneRole.FootL), (BoneRole.UpperLegR, BoneRole.LowerLegR, BoneRole.FootR) }
            .Select( l => (A: target.BoneForRole( l.Item1 ), B: target.BoneForRole( l.Item2 ), C: target.BoneForRole( l.Item3 )) )
            .Where( l => l.A is not null && l.B is not null && l.C is not null ).Select( l => (A: l.A!.Value, B: l.B!.Value, C: l.C!.Value) ).ToArray();
        var standing = new bool[frames.Count];
        for ( var f = 0; f < frames.Count; f++ )
        {
            FkUtil.ToWorld( frames[f], rig, world );
            lowest[f] = (body.Min( b => Vector3.Dot( world[b].Pos, up ) ) - floor) * toCm;
            if ( hips is int h ) hipsHeight[f] = Vector3.Dot( world[h].Pos, up ) * toCm;
            if ( legs.Length == 2 )
            {
                bool Straight( (int A, int B, int C) l )
                {
                    var thigh = world[l.B].Pos - world[l.A].Pos; var shin = world[l.C].Pos - world[l.B].Pos;
                    var knee = Vector3.Dot( Vector3.Normalize( thigh ), Vector3.Normalize( shin ) );
                    // Upright too: the leg points down from the hip.
                    return knee > StraightKneeCosine && Vector3.Dot( Vector3.Normalize( world[l.C].Pos - world[l.A].Pos ), -up ) > .9f;
                }
                var level = MathF.Abs( Vector3.Dot( world[legs[0].C].Pos - world[legs[1].C].Pos, up ) ) * toCm < LevelAnklesCm;
                standing[f] = level && Straight( legs[0] ) && Straight( legs[1] );
            }
        }
        // Flights first: where every part of the body is above MaximumHoverCm, widened to where the lowest part
        // comes down to TouchCm. Real takeoffs and landings cross that band in about a tenth of a second; a landing
        // that then hovers 10 to 16 cm up (a backflip did, for up to a second) stays outside the flight.
        var inFlight = new bool[frames.Count]; var lift = new float[frames.Count];
        for ( var f = 0; f < frames.Count; )
        {
            if ( !(lowest[f] > MaximumHoverCm) || standing[f] ) { f++; continue; }
            var core = f; while ( core < frames.Count && lowest[core] > MaximumHoverCm && !standing[core] ) core++;
            var reach = Math.Max( 1, (int)Math.Round( EdgeSeconds * fps ) );
            var a = f; while ( a > 0 && f - a < reach && lowest[a - 1] > TouchCm && !standing[a - 1] ) a--;
            var b = core; while ( b < frames.Count && b - core < reach && lowest[b] > TouchCm && !standing[b] ) b++;
            for ( var k = a; k < b; k++ ) inFlight[k] = true;
            // Nobody stays in the air much over a second; anything longer (slowed footage, a mistake) is left as captured.
            var takeoff = a - 1; var duration = (b - takeoff) / (double)fps;
            if ( hips is not null && takeoff >= 0 && b < frames.Count && duration <= MaximumFlightSeconds )
                for ( var k = a; k < b; k++ )
                {
                    var t = (k - takeoff) / (double)fps;
                    var arc = hipsHeight[takeoff] + (hipsHeight[b] - hipsHeight[takeoff]) * t / duration + 981 / 2.0 * t * (duration - t);
                    lift[k] = (float)arc - hipsHeight[k];
                }
            f = b;
        }
        // Hovers: outside flights, the lowest part staying ToleranceCm to MaximumHoverCm up for MinimumHoverSeconds.
        var wanted = new float[frames.Count]; var fixedFrame = new bool[frames.Count];
        var minimum = Math.Max( 2, (int)Math.Ceiling( MinimumHoverSeconds * fps ) );
        for ( var start = 0; start < frames.Count; )
        {
            bool Low( int k ) => !inFlight[k] && lowest[k] > ToleranceCm && (lowest[k] <= MaximumHoverCm || standing[k]);
            if ( !Low( start ) ) { start++; continue; }
            var end = start; while ( end < frames.Count && Low( end ) ) end++;
            if ( end - start >= minimum )
                for ( var k = start; k < end; k++ ) { wanted[k] = -lowest[k]; fixedFrame[k] = true; }
            start = end;
        }
        for ( var f = 0; f < frames.Count; f++ )
            if ( lowest[f] < 0 && !inFlight[f] ) { wanted[f] = -lowest[f]; fixedFrame[f] = true; }
        if ( !fixedFrame.Any( x => x ) && !lift.Any( x => x != 0 ) ) return 0;
        // Ease each correction in and out over neighbouring uncorrected frames.
        var ramp = Math.Max( 1, (int)Math.Round( RampSeconds * fps ) );
        var shift = wanted.ToArray();
        for ( var f = 0; f < frames.Count; f++ )
        {
            if ( fixedFrame[f] ) continue;
            float best = 0; var weightBest = 0f;
            for ( var k = Math.Max( 0, f - ramp ); k <= Math.Min( frames.Count - 1, f + ramp ); k++ )
            {
                if ( !fixedFrame[k] ) continue;
                var x = 1 - Math.Abs( k - f ) / (float)(ramp + 1); var w = x * x * (3 - 2 * x);
                if ( w > weightBest ) { weightBest = w; best = wanted[k] * w; }
            }
            // Never ease anything below the floor or lift a hover further.
            shift[f] = best < 0 ? Math.Max( best, -Math.Max( 0, lowest[f] ) ) : best;
        }
        for ( var f = 0; f < frames.Count; f++ ) shift[f] += lift[f];
        // Blend corrections into each other over about a tenth of a second either way (zero-phase Gaussian), so a
        // takeoff from a lowered crouch into a flight kept at its height rises instead of stepping; nothing is
        // then left below the floor.
        var sigma = Math.Max( 1, SmoothSeconds * fps ); var radius = (int)Math.Ceiling( 3 * sigma ); var smooth = new float[frames.Count];
        for ( var f = 0; f < frames.Count; f++ )
        {
            double sum = 0, weights = 0;
            for ( var k = Math.Max( 0, f - radius ); k <= Math.Min( frames.Count - 1, f + radius ); k++ ) { var w = Math.Exp( -.5 * (k - f) * (k - f) / (sigma * sigma) ); sum += w * shift[k]; weights += w; }
            smooth[f] = (float)Math.Max( sum / weights, -lowest[f] );
        }
        shift = smooth;
        var moved = 0;
        for ( var f = 0; f < frames.Count; f++ )
        {
            if ( Math.Abs( shift[f] ) < .01f ) continue;
            moved++;
            for ( var b = 0; b < rig.Count; b++ )
                if ( rig[b].ParentIndex < 0 ) frames[f][b].Pos += up * (shift[f] / toCm);
        }
        return moved;
    }
}
