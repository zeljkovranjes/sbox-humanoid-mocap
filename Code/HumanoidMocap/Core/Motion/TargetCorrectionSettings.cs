using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Cleanup;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Target;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;

public sealed class TargetCorrectionSettings
{
    public bool FirstPerson { get; set; }
    /// <summary>Identifies the selected target geometry for explicitly authored finger contacts.</summary>
    public string ContactTargetKey { get; set; } = "";
    public Vector3 LeftShoulder { get; set; } = new(.18f,1.45f,0);
    public Vector3 RightShoulder { get; set; } = new(-.18f,1.45f,0);
    public Vector3 LeftElbow { get; set; } = new(.45f,1.1f,.15f);
    public Vector3 RightElbow { get; set; } = new(-.45f,1.1f,.15f);
    public float Reach { get; set; } = .995f;
    public float GroundOffset { get; set; }
    public float FacingDegrees { get; set; }
    public bool StabilizeFeet { get; set; } = true;
    /// <summary>Manual camera-space wrist position edits, applied before confirmed prop contacts and target IK.</summary>
    public List<WristPositionOffset> WristOffsets { get; set; } = new();
    /// <summary>Editable placement of a camera-relative hand capture in the target's
    /// Y-up metre frame. This is a user assumption, not recovered camera tracking.</summary>
    public Vector3 CaptureCameraPosition { get; set; } = new(0,1.65f,0);
    public float CaptureCameraYawDegrees { get; set; } = 180;
    public float CaptureCameraPitchDegrees { get; set; }
    /// <summary>The capture camera faced the performer instead of being worn by them. The placement
    /// above then describes a camera in front of the character, and a first-person preview should
    /// look from the character's own head rather than from that camera.</summary>
    public bool CaptureFacesSubject { get; set; }
    /// <summary>Shrink a hand capture toward the camera, by at most a quarter, when the
    /// target's arms are too short to reach it. Points keep their viewing rays, so the
    /// first-person picture is unchanged. Skipped while prop contacts share the capture space.</summary>
    public bool FitCaptureToArmReach { get; set; } = true;

    public static TargetCorrectionSettings ForRig(TargetRig rig,TargetUpAxis axis)
    {
        var scale=axis==TargetUpAxis.ZUpEngine?39.3700787f:100f;
        var rotation=axis==TargetUpAxis.YUpCm?Quaternion.Identity:Quaternion.CreateFromAxisAngle(Vector3.UnitX,-MathF.PI/2);
        Vector3 Position(BoneRole role,Vector3 fallback)=>rig.BoneForRole(role) is int b
            ?Vector3.Transform(rig.Skeleton.RestWorld[b].Pos/scale,rotation):fallback;
        var result=new TargetCorrectionSettings();
        result.LeftShoulder=Position(BoneRole.UpperArmL,result.LeftShoulder);
        result.RightShoulder=Position(BoneRole.UpperArmR,result.RightShoulder);
        var height=Math.Max(.2f,(result.LeftShoulder.Y+result.RightShoulder.Y)/2)/1.45f;
        result.LeftElbow=result.LeftShoulder+new Vector3(.27f,-.35f,.15f)*height;
        result.RightElbow=result.RightShoulder+new Vector3(-.27f,-.35f,.15f)*height;
        result.CaptureCameraPosition=Position(BoneRole.Head,new(0,1.55f,0))+Vector3.UnitY*.1f*height;
        // FPS viewmodels without a body use their authored origin as the assumed
        // camera position. A full-body eye-height fallback would lift these wrists
        // above the rig and exhaust arm reach before any captured movement.
        if(rig.BoneForRole(BoneRole.Head) is null&&rig.BoneForRole(BoneRole.Hips) is null)
        {
            result.CaptureCameraPosition=Vector3.Zero;
            // A viewmodel can face a different horizontal axis than a body rig.
            // Its left/right shoulder line supplies lateral direction; detached
            // hands can use their authored wrist spacing. This remains editable.
            var leftRole=rig.BoneForRole(BoneRole.UpperArmL) is not null?BoneRole.UpperArmL:BoneRole.HandL;
            var rightRole=rig.BoneForRole(BoneRole.UpperArmR) is not null?BoneRole.UpperArmR:BoneRole.HandR;
            if(rig.BoneForRole(leftRole) is not null&&rig.BoneForRole(rightRole) is not null)
            {
                var lateral=Position(leftRole,Vector3.Zero)-Position(rightRole,Vector3.Zero);lateral.Y=0;
                if(lateral.LengthSquared()>1e-8f)
                {
                    lateral=Vector3.Normalize(lateral);var forward=Vector3.Cross(lateral,Vector3.UnitY);
                    result.CaptureCameraYawDegrees=MathF.Atan2(-forward.X,-forward.Z)*180/MathF.PI;
                    float ArmLength(BoneRole upper,BoneRole lower,BoneRole hand)=>
                        rig.BoneForRole(upper) is not null&&rig.BoneForRole(lower) is not null&&rig.BoneForRole(hand) is not null
                        ?Vector3.Distance(Position(upper,Vector3.Zero),Position(lower,Vector3.Zero))+
                            Vector3.Distance(Position(lower,Vector3.Zero),Position(hand,Vector3.Zero)):0;
                    var armLength=Math.Max(ArmLength(BoneRole.UpperArmL,BoneRole.LowerArmL,BoneRole.HandL),
                        ArmLength(BoneRole.UpperArmR,BoneRole.LowerArmR,BoneRole.HandR));
                    var proportion=armLength>1e-4f?armLength/.6f:1f;
                    result.LeftElbow=result.LeftShoulder+(lateral*.27f-Vector3.UnitY*.35f+forward*.15f)*proportion;
                    result.RightElbow=result.RightShoulder+(-lateral*.27f-Vector3.UnitY*.35f+forward*.15f)*proportion;
                }
            }
        }
        return result;
    }
}
