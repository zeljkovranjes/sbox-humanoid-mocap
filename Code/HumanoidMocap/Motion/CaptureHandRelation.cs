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

/// <summary>Keeps hands that are together in the capture together on the character. Each arm is copied on its own,
/// so on a differently built character hands that cross, clap or hold each other drift apart: a dancer crossing
/// the wrists above the head crossed by 2 cm in the capture and stood 4 cm uncrossed on the character. Where the
/// performer's hands are within <see cref="TogetherArms"/> arm-lengths of each other (fading out by
/// <see cref="ApartArms"/>), the character's hands are moved, half each, until they sit the same way from one another
/// in the body's own frame, scaled by arm length; the arms follow by two-bone IK and the hands keep their orientation.</summary>
public static class CaptureHandRelation
{
    public const float TogetherArms = .25f, ApartArms = .45f;
    sealed record Arm(int Shoulder,int Elbow,int Wrist);

    /// <returns>The number of frames adjusted.</returns>
    public static int Apply( List<XForm[]> frames, IReadOnlyList<XForm[]> sourceFrames, Skeleton.Skeleton source, MappingResult mapping, TargetRig target )
    {
        if ( frames.Count == 0 || sourceFrames.Count != frames.Count ) return 0;
        static Arm? Find( Func<BoneRole, int?> bone, bool left ) => bone( left ? BoneRole.UpperArmL : BoneRole.UpperArmR ) is int s && bone( left ? BoneRole.LowerArmL : BoneRole.LowerArmR ) is int e
            && bone( left ? BoneRole.HandL : BoneRole.HandR ) is int w ? new( s, e, w ) : null;
        int? Source( BoneRole role ) => mapping.RoleToBone.TryGetValue( role, out var b ) ? b : null;
        if ( Find( Source, true ) is not { } sourceL || Find( Source, false ) is not { } sourceR || Find( target.BoneForRole, true ) is not { } left || Find( target.BoneForRole, false ) is not { } right ) return 0;
        if ( Source( BoneRole.Hips ) is not int sourceHips || target.BoneForRole( BoneRole.Hips ) is not int hips ) return 0;
        var rig = target.Skeleton;
        static float Length( IReadOnlyList<XForm> rest, Arm arm ) => Vector3.Distance( rest[arm.Shoulder].Pos, rest[arm.Elbow].Pos ) + Vector3.Distance( rest[arm.Elbow].Pos, rest[arm.Wrist].Pos );
        var sourceArm = Length( source.RestWorld, sourceL ); var targetArm = Length( rig.RestWorld, left );
        if ( !(sourceArm > 1e-4f) || !(targetArm > 1e-4f) ) return 0;
        // The body's own frame: across the shoulders, up from the hips to between them, and forward.
        static (Vector3 Across, Vector3 Up, Vector3 Forward)? Frame( Vector3 hips, Vector3 shoulderL, Vector3 shoulderR )
        {
            var across = shoulderL - shoulderR; var up = (shoulderL + shoulderR) / 2 - hips;
            if ( across.LengthSquared() < 1e-10f || up.LengthSquared() < 1e-10f ) return null;
            across = Vector3.Normalize( across ); up -= across * Vector3.Dot( up, across ); if ( up.LengthSquared() < 1e-10f ) return null;
            up = Vector3.Normalize( up ); return (across, up, Vector3.Cross( across, up ));
        }
        var world = new XForm[rig.Count]; var adjusted = 0;
        for ( var f = 0; f < frames.Count; f++ )
        {
            var sourceWorld = new Pose( sourceFrames[f] ).ToWorld( source );
            var gap = sourceWorld[sourceR.Wrist].Pos - sourceWorld[sourceL.Wrist].Pos;
            var weight = Math.Clamp( (ApartArms - gap.Length() / sourceArm) / (ApartArms - TogetherArms), 0, 1 ); weight = weight * weight * (3 - 2 * weight);
            if ( weight <= 0 ) continue;
            if ( Frame( sourceWorld[sourceHips].Pos, sourceWorld[sourceL.Shoulder].Pos, sourceWorld[sourceR.Shoulder].Pos ) is not { } s ) continue;
            var frame = frames[f]; FkUtil.ToWorld( frame, rig, world );
            if ( Frame( world[hips].Pos, world[left.Shoulder].Pos, world[right.Shoulder].Pos ) is not { } t ) continue;
            var scale = targetArm / sourceArm;
            var wanted = (t.Across * Vector3.Dot( gap, s.Across ) + t.Up * Vector3.Dot( gap, s.Up ) + t.Forward * Vector3.Dot( gap, s.Forward )) * scale;
            var change = (wanted - (world[right.Wrist].Pos - world[left.Wrist].Pos)) * weight;
            if ( change.LengthSquared() < 1e-6f ) continue;
            foreach ( var (arm, move) in new[] { (left, -change / 2), (right, change / 2) } )
            {
                FkUtil.ToWorld( frame, rig, world );
                var shoulder = world[arm.Shoulder]; var elbow = world[arm.Elbow]; var wrist = world[arm.Wrist]; var rotation = wrist.Rot;
                var goal = wrist.Pos + move;
                var reach = (Vector3.Distance( shoulder.Pos, elbow.Pos ) + Vector3.Distance( elbow.Pos, wrist.Pos )) * .9995f;
                var fromShoulder = goal - shoulder.Pos; if ( fromShoulder.Length() > reach ) goal = shoulder.Pos + Vector3.Normalize( fromShoulder ) * reach;
                var bend = Vector3.Cross( elbow.Pos - shoulder.Pos, wrist.Pos - elbow.Pos );
                if ( bend.LengthSquared() < 1e-8f ) bend = Vector3.Transform( Vector3.UnitY, shoulder.Rot );
                var ik = TwoBoneIk.Solve( shoulder.Pos, elbow.Pos, wrist.Pos, goal, soften: 0, stableBendAxis: bend );
                EffectorIk.ApplyWorldDeltas( frame, rig, arm.Shoulder, arm.Elbow, arm.Wrist, ik.UpperWorldDelta, ik.LowerWorldDelta, world );
                FkUtil.ToWorld( frame, rig, world );
                var parent = rig[arm.Wrist].ParentIndex;
                frame[arm.Wrist].Rot = Quaternion.Normalize( (parent < 0 ? Quaternion.Identity : Quaternion.Inverse( world[parent].Rot )) * rotation );
            }
            adjusted++;
        }
        return adjusted;
    }
}
