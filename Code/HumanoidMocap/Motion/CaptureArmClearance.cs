using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Keeps the character's forearms and hands as clear of its torso as the performer's were. Arms copied
/// onto a differently built body, and hands brought together, pushed a forearm through the chest of a dancer with
/// folded arms. Each body's torso is an upright oval tube from the hips to the neck, <see cref="HalfWidth"/> and
/// <see cref="HalfDepth"/> of its own shoulder half-span across, widened by a forearm <see cref="ArmRadius"/> of it
/// thick; points along the forearm and hand are compared in those proportions. Where the performer's were clear of
/// the tube the character's must be too, and where the performer's were closer in, the character's may come no
/// closer. The hand is moved straight out and the arm re-solved by two-bone IK, keeping the hand's orientation;
/// judging the character against the performer (not on its own) leaves hands resting on the knees of a crouch alone.</summary>
public static class CaptureArmClearance
{
    public const float HalfWidth = .9f, HalfDepth = .7f, ArmRadius = .25f, SmoothSeconds = .1f;
    sealed record Arm(int Shoulder,int Elbow,int Wrist);
    sealed record Tube(Vector3 Bottom,Vector3 Up,float Height,Vector3 Across,Vector3 Forward,float Width,float Depth)
    {
        public (float Clearance, Vector3 Out) Measure( Vector3 p )
        {
            var v = p - Bottom; var along = Vector3.Dot( v, Up ) / Height;
            if ( along < .05f || along > .95f ) return (float.PositiveInfinity, Vector3.Zero);
            float x = Vector3.Dot( v, Across ), z = Vector3.Dot( v, Forward );
            return (MathF.Sqrt( x * x / (Width * Width) + z * z / (Depth * Depth) ), Across * x + Forward * z);
        }
    }
    static Tube? Build( Vector3 hips, Vector3 neck, Vector3 shoulderL, Vector3 shoulderR )
    {
        var axis = neck - hips; var height = axis.Length(); if ( height < 1e-5f ) return null; var up = axis / height;
        var across = shoulderL - shoulderR; var span = across.Length() / 2; across -= up * Vector3.Dot( across, up );
        if ( across.LengthSquared() < 1e-10f || span < 1e-5f ) return null; across = Vector3.Normalize( across );
        return new( hips, up, height, across, Vector3.Cross( across, up ), (HalfWidth + ArmRadius) * span, (HalfDepth + ArmRadius) * span );
    }
    static readonly float[] Along = { .5f, .75f, 1f, 1.17f, 1.35f };
    static Vector3 Point( Vector3 elbow, Vector3 wrist, float t ) => t <= 1 ? Vector3.Lerp( elbow, wrist, t ) : wrist + (wrist - elbow) * (t - 1);

    /// <returns>The number of hand samples moved and the largest move in target units.</returns>
    public static (int Samples, float Largest) Apply( List<XForm[]> frames, IReadOnlyList<XForm[]> sourceFrames, Skeleton.Skeleton source, MappingResult mapping, TargetRig target, float fps )
    {
        if ( frames.Count == 0 || sourceFrames.Count != frames.Count ) return (0, 0);
        static Arm? Find( Func<BoneRole, int?> bone, bool left ) => bone( left ? BoneRole.UpperArmL : BoneRole.UpperArmR ) is int s && bone( left ? BoneRole.LowerArmL : BoneRole.LowerArmR ) is int e
            && bone( left ? BoneRole.HandL : BoneRole.HandR ) is int w ? new( s, e, w ) : null;
        int? Source( BoneRole role ) => mapping.RoleToBone.TryGetValue( role, out var b ) ? b : null;
        if ( Find( Source, true ) is not { } sourceL || Find( Source, false ) is not { } sourceR || Find( target.BoneForRole, true ) is not { } left || Find( target.BoneForRole, false ) is not { } right ) return (0, 0);
        if ( Source( BoneRole.Hips ) is not int sourceHips || (Source( BoneRole.Neck ) ?? Source( BoneRole.Head )) is not int sourceNeck
            || target.BoneForRole( BoneRole.Hips ) is not int hips || (target.BoneForRole( BoneRole.Neck ) ?? target.BoneForRole( BoneRole.Head )) is not int neck ) return (0, 0);
        var rig = target.Skeleton; var world = new XForm[rig.Count];
        var push = new[] { new Vector3[frames.Count], new Vector3[frames.Count] };
        for ( var f = 0; f < frames.Count; f++ )
        {
            var s = new Pose( sourceFrames[f] ).ToWorld( source ); FkUtil.ToWorld( frames[f], rig, world );
            if ( Build( s[sourceHips].Pos, s[sourceNeck].Pos, s[sourceL.Shoulder].Pos, s[sourceR.Shoulder].Pos ) is not { } sourceTube
                || Build( world[hips].Pos, world[neck].Pos, world[left.Shoulder].Pos, world[right.Shoulder].Pos ) is not { } targetTube ) continue;
            for ( var side = 0; side < 2; side++ )
            {
                var (sa, ta) = side == 0 ? (sourceL, left) : (sourceR, right);
                var needed = Vector3.Zero;
                foreach ( var t in Along )
                {
                    var need = 1f;
                    var (clearance, outward) = targetTube.Measure( Point( world[ta.Elbow].Pos, world[ta.Wrist].Pos, t ) );
                    if ( !(clearance < need) ) continue;
                    // Straight out from the tube's centre line; a point on the line goes forward.
                    var move = clearance < 1e-3f ? targetTube.Forward * targetTube.Depth * need : outward * (need / clearance - 1);
                    if ( move.LengthSquared() > needed.LengthSquared() ) needed = move;
                }
                push[side][f] = needed;
            }
        }
        // The largest push nearby, eased in time, so a hand never snaps out of the body.
        var sigma = Math.Max( 1f, SmoothSeconds * fps ); var reach = (int)MathF.Ceiling( 2 * sigma ); var samples = 0; var largest = 0f;
        var smoothed = push.Select( p => Enumerable.Range( 0, frames.Count ).Select( f =>
        {
            var best = Vector3.Zero;
            for ( var k = -reach; k <= reach; k++ )
            {
                var i = f + k; if ( i < 0 || i >= frames.Count ) continue;
                var v = p[i] * MathF.Exp( -k * k / (2 * sigma * sigma) ); if ( v.LengthSquared() > best.LengthSquared() ) best = v;
            }
            return best;
        } ).ToArray() ).ToArray();
        for ( var f = 0; f < frames.Count; f++ )
            for ( var side = 0; side < 2; side++ )
            {
                var move = smoothed[side][f]; if ( move.LengthSquared() < 1e-8f ) continue;
                var arm = side == 0 ? left : right; var frame = frames[f]; FkUtil.ToWorld( frame, rig, world );
                var shoulder = world[arm.Shoulder]; var elbow = world[arm.Elbow]; var wrist = world[arm.Wrist]; var rotation = wrist.Rot;
                var goal = wrist.Pos + move;
                var armReach = (Vector3.Distance( shoulder.Pos, elbow.Pos ) + Vector3.Distance( elbow.Pos, wrist.Pos )) * .9995f;
                var fromShoulder = goal - shoulder.Pos; if ( fromShoulder.Length() > armReach ) goal = shoulder.Pos + Vector3.Normalize( fromShoulder ) * armReach;
                var bend = Vector3.Cross( elbow.Pos - shoulder.Pos, wrist.Pos - elbow.Pos );
                if ( bend.LengthSquared() < 1e-8f ) bend = Vector3.Transform( Vector3.UnitY, shoulder.Rot );
                var ik = TwoBoneIk.Solve( shoulder.Pos, elbow.Pos, wrist.Pos, goal, soften: 0, stableBendAxis: bend );
                EffectorIk.ApplyWorldDeltas( frame, rig, arm.Shoulder, arm.Elbow, arm.Wrist, ik.UpperWorldDelta, ik.LowerWorldDelta, world );
                FkUtil.ToWorld( frame, rig, world );
                var parent = rig[arm.Wrist].ParentIndex;
                frame[arm.Wrist].Rot = Quaternion.Normalize( (parent < 0 ? Quaternion.Identity : Quaternion.Inverse( world[parent].Rot )) * rotation );
                samples++; largest = Math.Max( largest, move.Length() );
            }
        return (samples, largest);
    }
}
