using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Puts a seated performer's hips on the floor. The capture worker marks frames where the performer
/// sits on the floor with a seated weight on the hips (see the worker's SeatedDetection); the body network
/// itself tends to read floor sits as crouches with the hips well above the floor. Here the hips come down
/// to seated height by that weight while the feet stay exactly where they were and hands resting near the
/// floor stay on it, both re-solved with two-bone IK. Nothing else about the pose changes.</summary>
public static class CaptureSeat
{
    /// <summary>Seated hip-joint height as a fraction of the target's standing hip height.</summary>
    public const float SeatFraction = .13f;
    /// <summary>Hands this close to the floor, in centimetres, are treated as resting on it.</summary>
    public const float RestingHandCm = 15;

    /// <returns>The number of frames lowered.</returns>
    public static int Apply( List<XForm[]> frames, SourceScene source, MappingResult mapping, TargetRig target, TargetUpAxis axis )
    {
        if ( frames.Count == 0 || source.CaptureStationaryJoints is not { Count: > 0 } tracks ) return 0;
        if ( !mapping.RoleToBone.TryGetValue( BoneRole.Hips, out var sourceHips ) ||
             !tracks.TryGetValue( source.Skeleton[sourceHips].Name, out var weights ) || weights.Length != frames.Count || weights.All( w => w <= 0 ) )
            return 0;
        var rig = target.Skeleton; var up = axis == TargetUpAxis.YUpCm ? Vector3.UnitY : Vector3.UnitZ;
        var cm = axis == TargetUpAxis.ZUpEngine ? 1 / 2.54f : 1f;
        if ( target.BoneForRole( BoneRole.Hips ) is not int hips ) return 0;
        var seat = Vector3.Dot( rig.RestWorld[hips].Pos, up ) * SeatFraction;
        (int A, int B, int C)? Chain( BoneRole a, BoneRole b, BoneRole c )
            => target.BoneForRole( a ) is int x && target.BoneForRole( b ) is int y && target.BoneForRole( c ) is int z ? (x, y, z) : null;
        var legs = new[] { Chain( BoneRole.UpperLegL, BoneRole.LowerLegL, BoneRole.FootL ), Chain( BoneRole.UpperLegR, BoneRole.LowerLegR, BoneRole.FootR ) }
            .Where( c => c is not null ).Select( c => c.Value ).ToArray();
        var arms = new[] { Chain( BoneRole.UpperArmL, BoneRole.LowerArmL, BoneRole.HandL ), Chain( BoneRole.UpperArmR, BoneRole.LowerArmR, BoneRole.HandR ) }
            .Where( c => c is not null ).Select( c => c.Value ).ToArray();
        var world = new XForm[rig.Count]; var lowered = 0;
        for ( var f = 0; f < frames.Count; f++ )
        {
            var w = Math.Clamp( weights[f], 0, 1 ); if ( w <= 0 ) continue;
            var frame = frames[f]; FkUtil.ToWorld( frame, rig, world );
            var drop = (Vector3.Dot( world[hips].Pos, up ) - seat) * w; if ( drop <= 0 ) continue;
            // Held goals: every foot, and hands resting on or near the floor.
            var goals = legs.Select( l => (Chain: l, Goal: world[l.C]) ).ToList();
            foreach ( var a in arms )
                if ( Vector3.Dot( world[a.C].Pos, up ) < RestingHandCm * cm ) goals.Add( (a, world[a.C]) );
            for ( var b = 0; b < rig.Count; b++ ) if ( rig[b].ParentIndex < 0 ) frame[b].Pos -= up * drop;
            foreach ( var (chain, goal) in goals )
            {
                FkUtil.ToWorld( frame, rig, world );
                var upper = world[chain.A]; var middle = world[chain.B]; var end = world[chain.C];
                var bend = Vector3.Cross( middle.Pos - upper.Pos, end.Pos - middle.Pos );
                if ( bend.LengthSquared() < 1e-8f ) bend = Vector3.Transform( Vector3.UnitX, upper.Rot );
                var ik = TwoBoneIk.Solve( upper.Pos, middle.Pos, end.Pos, goal.Pos, soften: 0, stableBendAxis: bend );
                EffectorIk.ApplyWorldDeltas( frame, rig, chain.A, chain.B, chain.C, ik.UpperWorldDelta, ik.LowerWorldDelta, world );
                FkUtil.ToWorld( frame, rig, world );
                var parent = rig[chain.C].ParentIndex;
                frame[chain.C].Rot = Quaternion.Normalize( (parent < 0 ? Quaternion.Identity : Quaternion.Inverse( world[parent].Rot )) * goal.Rot );
            }
            lowered++;
        }
        return lowered;
    }
}
