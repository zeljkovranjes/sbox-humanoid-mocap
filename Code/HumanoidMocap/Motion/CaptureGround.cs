using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Keeps a body capture on the floor all the way through. The foot lock corrects floor drift only
/// from detected foot contacts, and only for world-relative captures; a camera-relative capture from a camera
/// that moved was grounded once, and dance with few flat-footed moments had nothing to anchor on. On a
/// step-dance clip the feet rose from the floor to 45 cm over 17 seconds.
///
/// Within any stretch of about a second and a half somebody standing, walking or dancing puts a foot down,
/// so the lower envelope of the lowest foot's height is the floor: a rolling minimum followed by a rolling
/// maximum over that window (a morphological opening), which follows a slow rise exactly and drops anything
/// narrower than the window, such as jumps. Only the floor's change over the clip is removed, relative to its
/// lowest level, never by more than would push a foot below the floor: a capture that sits at one height the
/// whole time (a stage, a placed world capture) keeps it.</summary>
public static class CaptureGround
{
    /// <summary>Seconds within which a foot is expected to touch the floor.</summary>
    public const double WindowSeconds = 1.5;
    /// <summary>Corrections smaller than this, in centimetres, leave the clip untouched.</summary>
    public const float MinimumCorrectionCm = 1;

    /// <returns>The largest correction applied, in centimetres.</returns>
    public static float Apply( List<XForm[]> frames, TargetRig target, TargetUpAxis axis, float fps )
    {
        if ( frames.Count < 3 || !(fps > 0) ) return 0;
        var rig = target.Skeleton;
        var up = axis == TargetUpAxis.YUpCm ? Vector3.UnitY : Vector3.UnitZ;
        var toCm = axis == TargetUpAxis.ZUpEngine ? 2.54f : 1f;
        var joints = new[] { BoneRole.FootL, BoneRole.FootR, BoneRole.ToeL, BoneRole.ToeR }
            .Select( target.BoneForRole ).Where( b => b is not null ).Select( b => b.Value ).ToArray();
        if ( joints.Length == 0 ) return 0;
        // Height of the lowest foot joint above its own rest height, per frame.
        var lowest = new float[frames.Count]; var world = new XForm[rig.Count];
        for ( var f = 0; f < frames.Count; f++ )
        {
            FkUtil.ToWorld( frames[f], rig, world );
            var h = float.PositiveInfinity;
            foreach ( var j in joints ) h = Math.Min( h, Vector3.Dot( world[j].Pos, up ) - Vector3.Dot( rig.RestWorld[j].Pos, up ) );
            lowest[f] = h;
        }
        var radius = Math.Max( 1, (int)Math.Round( WindowSeconds * fps / 2 ) );
        var eroded = new float[frames.Count]; var floor = new double[frames.Count];
        for ( var f = 0; f < frames.Count; f++ )
        {
            var m = float.PositiveInfinity;
            for ( var k = Math.Max( 0, f - radius ); k <= Math.Min( frames.Count - 1, f + radius ); k++ ) m = Math.Min( m, lowest[k] );
            eroded[f] = m;
        }
        for ( var f = 0; f < frames.Count; f++ )
        {
            var m = float.NegativeInfinity;
            for ( var k = Math.Max( 0, f - radius ); k <= Math.Min( frames.Count - 1, f + radius ); k++ ) m = Math.Max( m, eroded[k] );
            floor[f] = m;
        }
        // The rolling minimum steps as the window slides; a slow zero-phase filter leaves only the drift.
        if ( frames.Count >= 8 && fps > 2 )
        {
            var (b, a) = MocapSmooth.ButterLowpass( 2, Math.Min( .5, fps * .2 ), fps );
            floor = MocapSmooth.FiltFilt( b, a, floor );
        }
        var reference = floor.Min();
        var largest = 0f;
        var shifts = new float[frames.Count];
        for ( var f = 0; f < frames.Count; f++ )
        {
            // Only the change in floor height is removed, and never below the floor.
            shifts[f] = (float)Math.Min( floor[f] - reference, Math.Max( 0, lowest[f] ) );
            largest = Math.Max( largest, Math.Abs( shifts[f] ) * toCm );
        }
        if ( largest < MinimumCorrectionCm ) return 0;
        for ( var f = 0; f < frames.Count; f++ )
            for ( var bone = 0; bone < rig.Count; bone++ )
                if ( rig[bone].ParentIndex < 0 ) frames[f][bone].Pos -= up * shifts[f];
        return largest;
    }
}
