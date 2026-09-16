#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Inference;
using Vector3=System.Numerics.Vector3;

public static class BodyMotionBuilder
{
    static readonly BoneRole[] roles={BoneRole.Hips,BoneRole.UpperLegL,BoneRole.UpperLegR,BoneRole.Spine0,
        BoneRole.LowerLegL,BoneRole.LowerLegR,BoneRole.Spine1,BoneRole.FootL,BoneRole.FootR,BoneRole.Spine2,
        BoneRole.ToeL,BoneRole.ToeR,BoneRole.Neck,BoneRole.ClavicleL,BoneRole.ClavicleR,BoneRole.Head,
        BoneRole.UpperArmL,BoneRole.UpperArmR,BoneRole.LowerArmL,BoneRole.LowerArmR,BoneRole.HandL,BoneRole.HandR};
    /// <summary>Raw camera-relative GVHMR reconstruction. Contact/IK-corrected output is a
    /// separate stage. No detailed fingers, calibrated scale or measured joint confidence.</summary>
    public static MotionDocument CameraRelative(SmplxSkeleton skeleton,GvhmrDecoder.Pose pose,Vector3[] translations,
        IReadOnlyList<double> timestamps,string name,string video,string sha256,double fps,GvhmrDecoder.Camera camera)
    {
        if(translations.Length!=pose.Frames||timestamps.Count!=pose.Frames)throw new ArgumentException("Motion track lengths differ.");
        var rest=skeleton.RestPose(pose.Betas.AsSpan(0,10));var parents=SmplxSkeleton.Parents;
        var document=new MotionDocument{Name=name,SourceVideo=video,SourceSha256=sha256,SourceFps=fps,
            Backend="GVHMR / C# native CPU",ModelVersion="ee960bb6 / "+GvhmrTemporalNetwork.CheckpointSha256,
            Space=MotionSpace.CameraRelative,MetricScaleCalibrated=false};
        for(var j=0;j<22;j++)document.Bones.Add(new(){Name=roles[j].ToString(),Role=roles[j],Parent=parents[j],Group=j is 16 or 17 or 18 or 19 or 20 or 21?"arms":"body",
            RestPosition=MotionDocument.A(j==0?rest[j]:rest[j]-rest[parents[j]])});
        var cameraToDocument=Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI);
        for(var t=0;t<pose.Frames;t++)
        {
            var frame=new MotionFrame{Time=timestamps[t],Positions=new float[22][],Rotations=new float[22][],Evidence=new JointEvidence[22]};
            for(var j=0;j<22;j++)
            {
                frame.Positions[j]=MotionDocument.A(j==0?Vector3.Transform(rest[0]+translations[t],cameraToDocument):rest[j]-rest[parents[j]]);
                frame.Rotations[j]=MotionDocument.A(j==0?Quaternion.Normalize(cameraToDocument*pose.CameraOrientation[t]):pose.BodyRotations[t*21+j-1]);
                frame.Evidence[j]=JointEvidence.Reconstructed;
            }
            document.Frames.Add(frame);
        }
        document.Cameras.Add(new(){Id="video",Source="Input video; focal length estimated from image diagonal",Calibrated=false,Synchronized=true,
            Intrinsics=new[]{camera.FocalLength,0,camera.CenterX,0,camera.FocalLength,camera.CenterY,0,0,1}});
        document.Diagnostics.AddRange(new[]{
            "Real ViTPose-H observations, HMR2 features and GVHMR temporal inference. C# port reference parity is incomplete.",
            "Camera-relative reconstruction with identity camera-angular-velocity conditioning assumption; camera motion has not been recovered.",
            "Focal length is estimated, not calibrated. Monocular body shape and scale are model estimates.",
            "Raw body output: upstream source contact/limb IK has not yet been applied. No world root-motion claim.",
            "GVHMR provides no detailed finger capture or per-joint 3D confidence. Raw 2D heatmap scores remain in reconstruction state.",
            "The selected person crop is manual and constant over this range. Check that the person stays inside it."
        });
        document.Validate();return document;
    }
}
