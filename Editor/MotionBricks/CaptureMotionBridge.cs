#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Motion;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Converts capture poses to and from MotionBricks' G1 skeleton with the Humanoid Retargeter:
/// <c>capture → retargeter → G1 (projected onto its hinges) → MotionBricks → G1 → retargeter →
/// capture</c>. A capture already uses the retargeter's shared space (metres here, centimetres
/// there; Y up, facing +Z, left +X), so only the units change. Adapted from AI Animator's
/// <c>CharacterMotionBridge</c>.
/// </summary>
public sealed class CaptureMotionBridge
{
    const float MetresToCm = 100f;
    /// <summary>Standing pelvis height of the G1 (MJCF <c>pelvis pos</c>), metres.</summary>
    public const float G1PelvisHeight = 0.793f;

    readonly G1Skeleton _g1;
    readonly PoseRetargeter _toG1;
    readonly PoseRetargeter _toCapture;
    readonly float[] _angles = new float[G1Skeleton.JointCount];

    public int BoneCount { get; }

    public CaptureMotionBridge( RetargeterLibrary library, IReadOnlyList<MotionBone> bones, G1Skeleton g1 )
    {
        _g1 = g1;
        BoneCount = bones.Count;
        var capture = library.CreateSkeleton( bones.Select( b => new RetargetBone( b.Name, b.Parent < 0 ? null : bones[b.Parent].Name,
            MotionDocument.V( b.RestPosition ) * MetresToCm, Quaternion.Normalize( MotionDocument.Q( b.RestRotation ) ) ) ).ToList() );
        var roles = new Dictionary<BoneRole, int>();
        for ( var i = 0; i < bones.Count; i++ )
            if ( bones[i].Role is { } role )
                roles.TryAdd( role, i );
        var captureMap = library.CreateMapping( "humanoid_mocap_capture", roles );
        var g1Skeleton = library.CreateSkeleton( G1Bones( g1 ) );
        var g1Map = library.CreateMapping( "motionbricks_g1", G1Roles( g1 ) );
        _toG1 = library.CreatePoseRetargeter( capture, captureMap, g1Skeleton, g1Map );
        _toCapture = library.CreatePoseRetargeter( g1Skeleton, g1Map, capture, captureMap );
    }

    static List<RetargetBone> G1Bones( G1Skeleton g1 )
    {
        var neutral = G1JointSpace.NeutralLocals();
        var bones = new List<RetargetBone>();
        for ( var j = 0; j < G1Skeleton.JointCount; j++ )
        {
            var offset = j == 0 ? new Vector3( 0, G1PelvisHeight, 0 ) : g1.RestOffset( j );
            bones.Add( new RetargetBone( g1.Names[j], g1.Parents[j] < 0 ? null : g1.Names[g1.Parents[j]], offset * MetresToCm, neutral[j] ) );
        }
        return bones;
    }

    /// <summary>Humanoid roles of the G1: the last joint of each hinge chain, whose world rotation is the whole limb's.</summary>
    static Dictionary<BoneRole, int> G1Roles( G1Skeleton g1 )
    {
        var names = new Dictionary<BoneRole, string>
        {
            [BoneRole.Hips] = "pelvis_skel",
            [BoneRole.Spine0] = "waist_pitch_skel",
            [BoneRole.UpperLegL] = "left_hip_yaw_skel",
            [BoneRole.LowerLegL] = "left_knee_skel",
            [BoneRole.FootL] = "left_ankle_roll_skel",
            [BoneRole.ToeL] = "left_toe_base",
            [BoneRole.UpperLegR] = "right_hip_yaw_skel",
            [BoneRole.LowerLegR] = "right_knee_skel",
            [BoneRole.FootR] = "right_ankle_roll_skel",
            [BoneRole.ToeR] = "right_toe_base",
            [BoneRole.UpperArmL] = "left_shoulder_yaw_skel",
            [BoneRole.LowerArmL] = "left_elbow_skel",
            [BoneRole.HandL] = "left_wrist_yaw_skel",
            [BoneRole.UpperArmR] = "right_shoulder_yaw_skel",
            [BoneRole.LowerArmR] = "right_elbow_skel",
            [BoneRole.HandR] = "right_wrist_yaw_skel",
        };
        var roles = new Dictionary<BoneRole, int>();
        foreach ( var (role, name) in names )
        {
            var index = g1.IndexOf( name );
            if ( index < 0 )
                throw new InvalidDataException( $"The MotionBricks skeleton has no joint '{name}'." );
            roles[role] = index;
        }
        return roles;
    }

    /// <summary>Capture poses (parent-local, metres) to G1 frames; consecutive frames warm-start the hinge projection.</summary>
    public G1Motion ToG1( IReadOnlyList<XForm[]> poses )
    {
        var motion = new G1Motion( poses.Count );
        var srcPos = new Vector3[BoneCount];
        var srcRot = new Quaternion[BoneCount];
        var dstPos = new Vector3[G1Skeleton.JointCount];
        var dstRot = new Quaternion[G1Skeleton.JointCount];
        for ( var f = 0; f < poses.Count; f++ )
        {
            for ( var i = 0; i < BoneCount; i++ )
            {
                srcPos[i] = poses[f][i].Pos * MetresToCm;
                srcRot[i] = poses[f][i].Rot;
            }
            _toG1.Retarget( srcPos, srcRot, dstPos, dstRot );
            motion.Root[f] = dstPos[0] / MetresToCm;
            motion.Local[f] = G1JointSpace.Project( _g1, dstRot, _angles );
        }
        return motion;
    }

    /// <summary>G1 frames back to capture poses (parent-local, metres).</summary>
    public XForm[][] ToCapture( G1Motion motion )
    {
        var result = new XForm[motion.FrameCount][];
        var srcPos = new Vector3[G1Skeleton.JointCount];
        var dstPos = new Vector3[BoneCount];
        var dstRot = new Quaternion[BoneCount];
        for ( var j = 1; j < G1Skeleton.JointCount; j++ )
            srcPos[j] = _g1.RestOffset( j ) * MetresToCm;
        for ( var f = 0; f < motion.FrameCount; f++ )
        {
            srcPos[0] = motion.Root[f] * MetresToCm;
            _toCapture.Retarget( srcPos, motion.Local[f], dstPos, dstRot );
            var pose = new XForm[BoneCount];
            for ( var i = 0; i < BoneCount; i++ )
                pose[i] = new XForm( dstPos[i] / MetresToCm, Quaternion.Normalize( dstRot[i] ) );
            result[f] = pose;
        }
        return result;
    }

    /// <summary>Resets the warm start of the hinge projection (between unrelated segments).</summary>
    public void ResetProjection() => Array.Clear( _angles );
}
