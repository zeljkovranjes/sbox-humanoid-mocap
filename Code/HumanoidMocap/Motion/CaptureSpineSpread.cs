using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Spreads a reconstructed body's spine bend along the target's spine. The body network folds a seated
/// or bent-over back almost entirely at its lowest spine joint (a seated tumbler: 52 degrees there, 16 and 17
/// above), which on a character is a crease at the base of the back. For each frame the rotation from the hips
/// to the top of the spine is kept exactly, so the chest, head and arms do not move; only how it is shared
/// between the spine bones changes, each taking <see cref="Falloff"/> of the share of the one below it and the
/// last absorbing any remainder.</summary>
public static class CaptureSpineSpread
{
    public const float Falloff = .6f;

    /// <returns>The number of frames changed.</returns>
    public static int Apply( List<XForm[]> frames, TargetRig target )
    {
        var rig = target.Skeleton;
        if ( target.BoneForRole( BoneRole.Hips ) is not int hips ) return 0;
        var chain = new List<int>(); var parent = hips;
        foreach ( var role in new[] { BoneRole.Spine0, BoneRole.Spine1, BoneRole.Spine2, BoneRole.Spine3, BoneRole.Spine4 } )
        {
            if ( target.BoneForRole( role ) is not int bone ) continue;
            if ( rig[bone].ParentIndex != parent ) break;
            chain.Add( bone ); parent = bone;
        }
        if ( chain.Count < 2 ) return 0;
        var rest = chain.Select( b => rig[b].RestLocal.Rot ).ToArray();
        var raw = Enumerable.Range( 0, chain.Count ).Select( i => (float)Math.Pow( Falloff, i ) ).ToArray(); var sum = raw.Sum();
        var weights = raw.Select( v => v / sum ).ToArray();
        var changed = 0;
        foreach ( var frame in frames )
        {
            var total = Quaternion.Identity; var delta = Quaternion.Identity;
            for ( var i = 0; i < chain.Count; i++ )
            {
                var local = frame[chain[i]].Rot;
                total = Quaternion.Normalize( total * local );
                delta = Quaternion.Normalize( delta * (Quaternion.Inverse( rest[i] ) * local) );
            }
            var sofar = Quaternion.Identity;
            for ( var i = 0; i < chain.Count - 1; i++ )
            {
                var local = Quaternion.Normalize( rest[i] * Quaternion.Slerp( Quaternion.Identity, delta, weights[i] ) );
                frame[chain[i]].Rot = local; sofar = Quaternion.Normalize( sofar * local );
            }
            frame[chain[^1]].Rot = Quaternion.Normalize( Quaternion.Inverse( sofar ) * total );
            changed++;
        }
        return changed;
    }
}
