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
    public const float ToleranceCm = 2, MaximumHoverCm = 40;
    public const double MinimumHoverSeconds = .25, RampSeconds = .1;

    /// <returns>The number of frames moved.</returns>
    public static int Apply( List<XForm[]> frames, TargetRig target, TargetUpAxis axis, float fps )
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
        for ( var f = 0; f < frames.Count; f++ )
        {
            FkUtil.ToWorld( frames[f], rig, world );
            lowest[f] = (body.Min( b => Vector3.Dot( world[b].Pos, up ) ) - floor) * toCm;
            if ( hips is int h ) hipsHeight[f] = Vector3.Dot( world[h].Pos, up ) * toCm;
        }
        // Wanted change in centimetres per frame: down onto the floor through a hover, up out of the floor.
        var wanted = new float[frames.Count]; var fixedFrame = new bool[frames.Count];
        var minimum = Math.Max( 2, (int)Math.Ceiling( MinimumHoverSeconds * fps ) );
        for ( var start = 0; start < frames.Count; )
        {
            bool Low( int f ) => lowest[f] > ToleranceCm && lowest[f] <= MaximumHoverCm;
            if ( !Low( start ) ) { start++; continue; }
            var end = start; while ( end < frames.Count && Low( end ) ) end++;
            if ( end - start >= minimum )
                for ( var f = start; f < end; f++ ) { wanted[f] = -lowest[f]; fixedFrame[f] = true; }
            start = end;
        }
        for ( var f = 0; f < frames.Count; f++ )
            if ( lowest[f] < 0 ) { wanted[f] = -lowest[f]; fixedFrame[f] = true; }
        // Flights: the hips follow the free-fall arc between takeoff and landing.
        var lift = new float[frames.Count];
        if ( hips is not null )
            for ( var start = 1; start < frames.Count; )
            {
                if ( !(lowest[start] > ToleranceCm) || fixedFrame[start] ) { start++; continue; }
                var end = start; while ( end < frames.Count && lowest[end] > ToleranceCm && !fixedFrame[end] ) end++;
                var peak = 0f; for ( var f = start; f < end; f++ ) peak = Math.Max( peak, lowest[f] );
                if ( end < frames.Count && peak > MaximumHoverCm )
                {
                    var takeoff = start - 1; var duration = (end - takeoff) / (double)fps;
                    for ( var f = start; f < end; f++ )
                    {
                        var t = (f - takeoff) / (double)fps;
                        var arc = hipsHeight[takeoff] + (hipsHeight[end] - hipsHeight[takeoff]) * t / duration + 981 / 2.0 * t * (duration - t);
                        lift[f] = (float)arc - hipsHeight[f];
                    }
                }
                start = end;
            }
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
