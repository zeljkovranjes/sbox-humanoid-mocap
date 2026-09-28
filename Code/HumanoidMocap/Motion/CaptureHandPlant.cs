using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Holds hands that rest on the floor in place, the way planted feet are held. Push-ups, crawling,
/// cartwheels and handstands put weight on the hands, but the capture has no contact track for them, so a hand on
/// the floor slid and hovered with the arm's small errors. After the floor is settled (CaptureGround, CaptureHover,
/// CaptureSeat), a hand whose lowest point (fingers included) stays within <see cref="NearFloorCm"/> of the floor
/// and moves slower than <see cref="StillMetresPerSecond"/> for at least <see cref="MinimumSeconds"/> rests on it:
/// through that stretch it is held where it landed, palm on the floor, re-solved with two-bone arm IK; its
/// orientation is kept. The slide the capture built up is let go over <see cref="BlendSeconds"/> after the hand lifts:
/// held at the stretch's middle instead, a hand that slid while resting jumped to it at up to 2 m/s. A hand that travels more than <see cref="MaximumTravelCm"/> while low is wiping or
/// sliding on purpose and is left alone.</summary>
public static class CaptureHandPlant
{
    /// <summary>A hand comes to rest within NearFloorCm of the floor and stays at rest until it rises past LiftCm: the capture
    /// lifted a hand planted behind a seated dancer 9 cm as he got up, and a single threshold let it float.</summary>
    public const float MaximumShoulderDegrees = 25, MaximumLeanDegrees = 15;
    public const float NearFloorCm = 8, LiftCm = 15, StillMetresPerSecond = 1, MinimumSeconds = .15f, MaximumTravelCm = 25, BlendSeconds = .25f;

    /// <returns>The number of hand samples held.</returns>
    public static int Apply( List<XForm[]> frames, TargetRig target, TargetUpAxis axis, float fps )
    {
        if ( frames.Count < 3 || fps <= 0 ) return 0;
        var rig = target.Skeleton; var up = axis == TargetUpAxis.YUpCm ? Vector3.UnitY : Vector3.UnitZ;
        var cm = axis == TargetUpAxis.ZUpEngine ? 1 / 2.54f : 1f;
        var arms = new[] { (BoneRole.UpperArmL, BoneRole.LowerArmL, BoneRole.HandL), (BoneRole.UpperArmR, BoneRole.LowerArmR, BoneRole.HandR) }
            .Select( r => target.BoneForRole( r.Item1 ) is int a && target.BoneForRole( r.Item2 ) is int b && target.BoneForRole( r.Item3 ) is int c ? (A: a, B: b, C: c) : ((int A, int B, int C)?)null )
            .Where( c => c is not null ).Select( c => c.Value ).ToArray();
        if ( arms.Length == 0 ) return 0;
        var world = new XForm[rig.Count];
        // Each hand's wrist and the height of its lowest part, per frame, before anything is held.
        var parts = arms.ToDictionary( a => a.C, a => CaptureSeat.HandParts( target, a.C ) );
        var wrist = arms.ToDictionary( a => a.C, _ => new Vector3[frames.Count] );
        var lowest = arms.ToDictionary( a => a.C, _ => new float[frames.Count] );
        for ( var f = 0; f < frames.Count; f++ )
        {
            FkUtil.ToWorld( frames[f], rig, world );
            foreach ( var a in arms ) { wrist[a.C][f] = world[a.C].Pos; lowest[a.C][f] = parts[a.C].Min( b => Vector3.Dot( world[b].Pos, up ) ); }
        }
        var minimum = Math.Max( 3, (int)MathF.Ceiling( MinimumSeconds * fps ) ); var blend = Math.Max( 1, (int)MathF.Round( BlendSeconds * fps ) );
        var still = StillMetresPerSecond * 100 * cm / fps;
        var goals = arms.ToDictionary( a => a.C, _ => new (Vector3 Position, float Weight)[frames.Count] );
        foreach ( var a in arms )
        {
            var p = wrist[a.C]; var low = lowest[a.C];
            bool Resting( int f, float height ) => low[f] < height * cm && (f == 0 || Flat( p[f] - p[f - 1], up ).Length() < still);
            for ( var f = 0; f < frames.Count; )
            {
                if ( !Resting( f, NearFloorCm ) ) { f++; continue; }
                var e = f; while ( e + 1 < frames.Count && Resting( e + 1, LiftCm ) ) e++;
                if ( e - f + 1 >= minimum )
                {
                    // Held where it landed: a hand that touches down stays there. The capture's slide builds up while it rests
                    // and is let go over BlendSeconds after the hand lifts, while it is moving anyway.
                    var length = e - f + 1; var anchor = Flat( p[f], up );
                    if ( Enumerable.Range( f, length ).All( i => (Flat( p[i], up ) - anchor).Length() <= MaximumTravelCm * cm ) )
                    {
                        // The palm settles onto the floor over a few frames rather than dropping in one.
                        var settle = Math.Max( 1, Math.Min( 3, (length - 1) / 3 ) );
                        for ( var i = f; i <= e; i++ )
                        {
                            // Palm on the floor: the wrist at the held place, as high as puts the hand's lowest point on the floor.
                            var w = Math.Clamp( (i - f + 1) / (float)(settle + 1), 0, 1 ); w = w * w * (3 - 2 * w);
                            var goal = anchor + up * (Vector3.Dot( p[i], up ) - low[i]);
                            if ( w > goals[a.C][i].Weight ) goals[a.C][i] = (goal, w);
                        }
                        // Everything held at the last frame, height included, is let go gradually.
                        var carried = anchor + up * (Vector3.Dot( p[e], up ) - low[e]) - p[e];
                        for ( var i = e + 1; i <= Math.Min( frames.Count - 1, e + blend ); i++ )
                        {
                            var w = 1 - (i - e) / (float)(blend + 1); w = w * w * (3 - 2 * w);
                            if ( w > goals[a.C][i].Weight ) goals[a.C][i] = (p[i] + carried, w);
                        }
                    }
                }
                f = e + 1;
            }
        }
        var held = 0;
        for ( var f = 0; f < frames.Count; f++ )
        {
            var frame = frames[f];
            foreach ( var a in arms )
            {
                var (position, weight) = goals[a.C][f]; if ( weight <= 0 ) continue;
                FkUtil.ToWorld( frame, rig, world );
                var rotation = world[a.C].Rot;
                var goal = Vector3.Lerp( world[a.C].Pos, position, weight );
                // Out of the straight arm's reach (a seated dancer rising off a planted hand), the shoulder drops
                // toward the hand as a person's does: the clavicle turns, up to MaximumShoulderDegrees.
                var reach = Vector3.Distance( world[a.A].Pos, world[a.B].Pos ) + Vector3.Distance( world[a.B].Pos, world[a.C].Pos );
                // Turns bone about its own joint, up to limit degrees, just far enough to bring the shoulder within reach.
                void Lean( int bone, float limit )
                {
                    if ( bone < 0 || rig[bone].ParentIndex < 0 || Vector3.Distance( world[a.A].Pos, goal ) <= reach * .999f ) return;
                    var pivot = world[bone].Pos; var from = world[a.A].Pos - pivot; var to = goal - pivot;
                    var hinge = Vector3.Cross( from, to );
                    if ( hinge.LengthSquared() < 1e-10f || from.LengthSquared() < 1e-8f ) return;
                    hinge = Vector3.Normalize( hinge ); var turn = 0f;
                    for ( var step = 1; step <= 25; step++ )
                    {
                        turn = step * limit / 25 * MathF.PI / 180;
                        if ( Vector3.Distance( pivot + Vector3.Transform( from, Quaternion.CreateFromAxisAngle( hinge, turn ) ), goal ) <= reach * .999f ) break;
                    }
                    var turned = Quaternion.Normalize( Quaternion.CreateFromAxisAngle( hinge, turn ) * world[bone].Rot );
                    frame[bone].Rot = Quaternion.Normalize( Quaternion.Inverse( world[rig[bone].ParentIndex].Rot ) * turned );
                    FkUtil.ToWorld( frame, rig, world );
                }
                Lean( rig[a.A].ParentIndex, MaximumShoulderDegrees );
                // Still out of reach (pushing up off the hand), the body leans onto it from the base of the spine.
                if ( target.BoneForRole( BoneRole.Spine0 ) is int spine ) Lean( spine, MaximumLeanDegrees );
                var upper = world[a.A]; var middle = world[a.B]; var end = world[a.C];
                var bend = Vector3.Cross( middle.Pos - upper.Pos, end.Pos - middle.Pos );
                if ( bend.LengthSquared() < 1e-8f ) bend = Vector3.Transform( Vector3.UnitX, upper.Rot );
                var ik = TwoBoneIk.Solve( upper.Pos, middle.Pos, end.Pos, goal, soften: 0, stableBendAxis: bend );
                EffectorIk.ApplyWorldDeltas( frame, rig, a.A, a.B, a.C, ik.UpperWorldDelta, ik.LowerWorldDelta, world );
                FkUtil.ToWorld( frame, rig, world );
                var parent = rig[a.C].ParentIndex;
                frame[a.C].Rot = Quaternion.Normalize( (parent < 0 ? Quaternion.Identity : Quaternion.Inverse( world[parent].Rot )) * rotation );
                held++;
            }
        }
        return held;
    }
    static Vector3 Flat( Vector3 v, Vector3 up ) => v - up * Vector3.Dot( v, up );
    static bool IsWithin( HumanoidMocap.Skeleton.Skeleton rig, int bone, int ancestor )
    {
        for ( var b = bone; b >= 0; b = rig[b].ParentIndex ) if ( b == ancestor ) return true;
        return false;
    }
}
