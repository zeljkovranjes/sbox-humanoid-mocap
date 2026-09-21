using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Whether a hand capture was filmed from the performer's own head or chest, or by a camera
/// facing them. Seen from their own head a person's right hand is on the right of the picture; seen
/// from in front it is on the left. Treating footage of someone facing the camera as head-mounted put
/// the character's right hand on its left and crossed its arms. This reads only the side of the
/// picture each reconstructed wrist occupies; it is a viewpoint guess, not camera recovery.</summary>
public static class CaptureViewpoint
{
    /// <summary>Distance in front of the character's shoulders for a camera that faces them, in metres:
    /// the 0.45 m typical hand distance plus hands held about 0.35 m in front of the body.</summary>
    public const float FacingCameraDistance=.8f;
    /// <summary>Chest height below the shoulder line on a 1.45 m shoulder-height figure, in metres.</summary>
    public const float ChestBelowShoulders=.22f;
    public const float MinimumSeparation=.05f,MinimumOffset=.06f;
    public static bool FacesSubject(MotionDocument document)
    {
        if(document.Space!=MotionSpace.CameraRelative)return false;
        var left=document.Bones.FindIndex(b=>b.Role==BoneRole.HandL);var right=document.Bones.FindIndex(b=>b.Role==BoneRole.HandR);
        var both=new List<float>();var onlyLeft=new List<float>();var onlyRight=new List<float>();
        foreach(var frame in document.Frames)
        {
            var l=left>=0&&frame.Evidence[left]==JointEvidence.Reconstructed;var r=right>=0&&frame.Evidence[right]==JointEvidence.Reconstructed;
            // Document space keeps the camera's left-right axis: +X is the right of the picture.
            if(l&&r)both.Add(frame.Positions[right][0]-frame.Positions[left][0]);
            else if(l)onlyLeft.Add(frame.Positions[left][0]);else if(r)onlyRight.Add(frame.Positions[right][0]);
        }
        static float Median(List<float> values){values.Sort();return values[values.Count/2];}
        if(both.Count>=8)return Median(both)<-MinimumSeparation;
        // One hand: a wearer's right hand works on the right of the picture, a facing subject's on the left.
        if(onlyRight.Count>=8&&onlyRight.Count>=onlyLeft.Count)return Median(onlyRight)<-MinimumOffset;
        if(onlyLeft.Count>=8)return Median(onlyLeft)>MinimumOffset;
        return false;
    }
    /// <summary>Placement for a camera facing the character: in front of its shoulders, looking back at it.</summary>
    public static void PlaceFacingCamera(TargetCorrectionSettings settings)
    {
        var looking=Vector3.Transform(-Vector3.UnitZ,Quaternion.CreateFromYawPitchRoll(settings.CaptureCameraYawDegrees*MathF.PI/180,0,0));
        looking.Y=0;if(looking.LengthSquared()<1e-8f)return;looking=Vector3.Normalize(looking);
        var shoulders=(settings.LeftShoulder+settings.RightShoulder)*.5f;
        // Hands filmed from in front are usually held at chest height and framed near the picture's centre.
        settings.CaptureCameraPosition=shoulders-Vector3.UnitY*(ChestBelowShoulders*shoulders.Y/1.45f)+looking*FacingCameraDistance;
        var yaw=settings.CaptureCameraYawDegrees+180;settings.CaptureCameraYawDegrees=yaw>180?yaw-360:yaw;
        settings.CaptureCameraPitchDegrees=0;settings.CaptureFacesSubject=true;
    }
}
