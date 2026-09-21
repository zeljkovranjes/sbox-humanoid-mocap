using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Keeps a captured body's hand spacing when the target's shoulders are a different width.
/// Arm rotations copied onto a rig whose shoulder joints sit wider, relative to its arms, than the
/// performer's carry both hands outward by the extra half-width: on the kata sample shoulder width
/// was 0.60 arm-lengths on the performer and 0.72 on Human, and hands 0.90 arm-lengths apart became
/// 1.02, so hands that meet in the video would not meet on the character. Each wrist is moved back
/// along the shoulder line by that difference and the arm re-solved, preserving bone lengths and the
/// hand's world orientation. The move fades out as the hand goes out to its own side, where there
/// is nothing to meet and a straight arm must stay straight. A proportion correction, not new capture.</summary>
public static class CaptureArmProportion
{
    public sealed record Result(float HalfWidthCorrection,int Samples);
    sealed record Arm(int Shoulder,int Elbow,int Wrist);
    public static Result Apply(List<XForm[]> frames,Skeleton.Skeleton source,MappingResult mapping,TargetRig target)
    {
        if(frames.Count==0)return new(0,0);
        static Arm? Find(Func<BoneRole,int?> bone,bool left)=>bone(left?BoneRole.UpperArmL:BoneRole.UpperArmR) is int s&&bone(left?BoneRole.LowerArmL:BoneRole.LowerArmR) is int e&&
            bone(left?BoneRole.HandL:BoneRole.HandR) is int w?new(s,e,w):null;
        int? Source(BoneRole role)=>mapping.RoleToBone.TryGetValue(role,out var b)?b:null;
        if(Find(Source,true) is not { } sourceL||Find(Source,false) is not { } sourceR||Find(target.BoneForRole,true) is not { } left||Find(target.BoneForRole,false) is not { } right)return new(0,0);
        var rig=target.Skeleton;
        static float Length(IReadOnlyList<XForm> rest,Arm arm)=>Vector3.Distance(rest[arm.Shoulder].Pos,rest[arm.Elbow].Pos)+Vector3.Distance(rest[arm.Elbow].Pos,rest[arm.Wrist].Pos);
        var sourceArm=Length(source.RestWorld,sourceL);var targetArm=Length(rig.RestWorld,left);
        if(!(sourceArm>1e-4f)||!(targetArm>1e-4f))return new(0,0);
        // Half the shoulder-joint spacing each skeleton has per unit of its own arm, in target units.
        var correction=(Vector3.Distance(rig.RestWorld[left.Shoulder].Pos,rig.RestWorld[right.Shoulder].Pos)/targetArm
            -Vector3.Distance(source.RestWorld[sourceL.Shoulder].Pos,source.RestWorld[sourceR.Shoulder].Pos)/sourceArm)*targetArm*.5f;
        if(!float.IsFinite(correction)||MathF.Abs(correction)<targetArm*.005f)return new(0,0);
        var world=new XForm[rig.Count];var samples=0;
        foreach(var frame in frames)
        {
            foreach(var (arm,other) in new[]{(left,right),(right,left)})
            {
                FkUtil.ToWorld(frame,rig,world);
                var lateral=world[arm.Shoulder].Pos-world[other.Shoulder].Pos;if(lateral.LengthSquared()<1e-8f)continue;
                lateral=Vector3.Normalize(lateral);
                var shoulder=world[arm.Shoulder];var elbow=world[arm.Elbow];var wrist=world[arm.Wrist];var handRotation=wrist.Rot;
                // How far the hand is out to its own side, in arm-lengths from its shoulder: full correction
                // in front of the body or across it, none from 0.6 arm-lengths outward.
                var outward=Vector3.Dot(wrist.Pos-shoulder.Pos,lateral)/targetArm;
                var weight=1-Math.Clamp((outward-.2f)/.4f,0,1);weight=weight*weight*(3-2*weight);
                if(weight<=0)continue;
                var goal=wrist.Pos-lateral*(correction*weight);
                var reach=(Vector3.Distance(shoulder.Pos,elbow.Pos)+Vector3.Distance(elbow.Pos,wrist.Pos))*.9995f;
                var fromShoulder=goal-shoulder.Pos;if(fromShoulder.Length()>reach)goal=shoulder.Pos+Vector3.Normalize(fromShoulder)*reach;
                var bend=Vector3.Cross(elbow.Pos-shoulder.Pos,wrist.Pos-elbow.Pos);
                if(bend.LengthSquared()<1e-8f)bend=Vector3.Transform(Vector3.UnitY,shoulder.Rot);
                var ik=TwoBoneIk.Solve(shoulder.Pos,elbow.Pos,wrist.Pos,goal,soften:0,stableBendAxis:bend);
                EffectorIk.ApplyWorldDeltas(frame,rig,arm.Shoulder,arm.Elbow,arm.Wrist,ik.UpperWorldDelta,ik.LowerWorldDelta,world);
                FkUtil.ToWorld(frame,rig,world);
                var parent=rig[arm.Wrist].ParentIndex;
                frame[arm.Wrist].Rot=Quaternion.Normalize((parent<0?Quaternion.Identity:Quaternion.Inverse(world[parent].Rot))*handRotation);
                samples++;
            }
        }
        return new(correction,samples);
    }
}
