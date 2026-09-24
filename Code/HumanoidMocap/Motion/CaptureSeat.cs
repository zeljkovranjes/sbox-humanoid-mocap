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
/// floor stay on it, both re-solved with two-bone IK. A hand the lowering would push through the floor stops
/// on it. Sitting on the floor rests on both sides of the pelvis, so the hips are also rolled level; a clip
/// filmed with a handheld phone sat 6 to 16 degrees askew. Nothing else about the pose changes.</summary>
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
        (int Left, int Right)? level = target.BoneForRole( BoneRole.UpperLegL ) is int legL && target.BoneForRole( BoneRole.UpperLegR ) is int legR ? (legL, legR) : null;
        var handParts = arms.ToDictionary( a => a.C, a => Enumerable.Range( 0, rig.Count ).Where( b => IsWithin( rig, b, a.C ) ).ToArray() );
        var world = new XForm[rig.Count]; var lowered = 0;
        // Sitting on the floor the hips are a contact: they stay where the sit put them. A seated tumbler's hips
        // otherwise slid 28 cm across the floor while he sat still. Each seated stretch holds the hips at its
        // middle frame's place across the floor, blended in by the seated weight; feet and resting hands are
        // held by the IK below, and the sit's travel is carried on after it.
        var anchor = new Vector3?[frames.Count]; var feetAnchor = new Vector3[frames.Count][];
        for ( var f = 0; f < frames.Count; )
        {
            if ( !(weights[f] >= 1) ) { f++; continue; }
            var e = f; while ( e < frames.Count && weights[e] >= 1 ) e++;
            FkUtil.ToWorld( frames[(f + e) / 2], rig, world ); var middle = world[hips].Pos;
            // The feet stay put too (a seated tumbler's planted feet otherwise slid about 50 cm/s).
            var feetMiddle = legs.Select( l => world[l.C].Pos ).ToArray();
            var a = f; while ( a > 0 && weights[a - 1] > 0 ) a--;
            var b = e; while ( b < frames.Count && weights[b] > 0 ) b++;
            for ( var k = a; k < b; k++ ) { anchor[k] = middle; feetAnchor[k] = feetMiddle; }
            f = e;
        }
        var carried = Vector3.Zero; var afterFull = false;
        for ( var f = 0; f < frames.Count; f++ )
        {
            var w = Math.Clamp( weights[f], 0, 1 );
            var frame = frames[f];
            // Offset across the floor: the held spot while seated, the running offset otherwise (kept after a sit so
            // the body does not slide back), blended while easing into a sit.
            FkUtil.ToWorld( frame, rig, world );
            var offset = carried;
            if ( w <= 0 ) afterFull = false;
            else if ( anchor[f] is { } held )
            {
                var hold = held - world[hips].Pos; hold -= up * Vector3.Dot( hold, up );
                if ( w >= 1 ) { offset = hold; carried = hold; afterFull = true; }
                else if ( !afterFull ) offset = Vector3.Lerp( carried, hold, w );
            }
            if ( offset != Vector3.Zero )
            {
                for ( var b = 0; b < rig.Count; b++ ) if ( rig[b].ParentIndex < 0 ) frame[b].Pos += offset;
                FkUtil.ToWorld( frame, rig, world );
            }
            if ( w <= 0 ) continue;
            var drop = (Vector3.Dot( world[hips].Pos, up ) - seat) * w; if ( drop <= 0 ) continue;
            // Held goals: every foot, hands resting on or near the floor, and hands that would sink into it.
            var goals = legs.Select( ( l, i ) => (Chain: l, Goal: feetAnchor[f] is { } held ? new XForm( Vector3.Lerp( world[l.C].Pos, held[i], w ), world[l.C].Rot ) : world[l.C]) ).ToList();
            foreach ( var a in arms )
            {
                // The fingers reach below the wrist; the lowest of them is the hand's height.
                var hand = world[a.C]; var height = handParts[a.C].Min( b => Vector3.Dot( world[b].Pos, up ) );
                if ( height < RestingHandCm * cm ) goals.Add( (a, hand) );
                else if ( height < drop ) goals.Add( (a, new XForm( hand.Pos - up * height, hand.Rot )) );
            }
            for ( var b = 0; b < rig.Count; b++ ) if ( rig[b].ParentIndex < 0 ) frame[b].Pos -= up * drop;
            if ( level is { } pelvis )
            {
                FkUtil.ToWorld( frame, rig, world );
                var across = world[pelvis.Left].Pos - world[pelvis.Right].Pos;
                var flat = across - up * Vector3.Dot( across, up );
                if ( across.LengthSquared() > 1e-8f && flat.LengthSquared() > 1e-8f )
                {
                    var roll = Quaternion.Slerp( Quaternion.Identity, FromTo( Vector3.Normalize( across ), Vector3.Normalize( flat ) ), w );
                    var parent = rig[hips].ParentIndex;
                    var hipsWorld = Quaternion.Normalize( roll * world[hips].Rot );
                    frame[hips].Rot = Quaternion.Normalize( (parent < 0 ? Quaternion.Identity : Quaternion.Inverse( world[parent].Rot )) * hipsWorld );
                }
            }
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
    static bool IsWithin( HumanoidMocap.Skeleton.Skeleton rig, int bone, int ancestor )
    {
        for ( var b = bone; b >= 0; b = rig[b].ParentIndex ) if ( b == ancestor ) return true;
        return false;
    }
    static Quaternion FromTo( Vector3 from, Vector3 to )
    {
        var dot = Math.Clamp( Vector3.Dot( from, to ), -1f, 1f ); var axis = Vector3.Cross( from, to );
        if ( axis.LengthSquared() < 1e-12f ) return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle( Vector3.Normalize( axis ), MathF.Acos( dot ) );
    }
}
