using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
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
    /// <summary>Editable placement of a camera-relative hand capture in the target's
    /// Y-up metre frame. This is a user assumption, not recovered camera tracking.</summary>
    public Vector3 CaptureCameraPosition { get; set; } = new(0,1.65f,0);
    public float CaptureCameraYawDegrees { get; set; } = 180;
    public float CaptureCameraPitchDegrees { get; set; }

    public static TargetCorrectionSettings ForRig(TargetRig rig,TargetUpAxis axis)
    {
        var scale=axis==TargetUpAxis.YUpCm?100f:39.3700787f;
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
        return result;
    }
}

public static class TargetCorrections
{
    public static void Apply(List<XForm[]> frames,TargetRig rig,TargetUpAxis axis,TargetCorrectionSettings settings,
        IReadOnlyList<Dictionary<BoneRole,XForm>>? wristTargets=null)
    {
        var skeleton=rig.Skeleton;var world=new XForm[skeleton.Count];
        var left=new ArmConstraintSolver();var right=new ArmConstraintSolver();
        var scale=axis==TargetUpAxis.YUpCm?100f:39.3700787f;
        var conversion=axis==TargetUpAxis.YUpCm?Quaternion.Identity:Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2);
        Vector3 Convert(Vector3 v)=>Vector3.Transform(v*scale,conversion);
        var up=axis==TargetUpAxis.YUpCm?Vector3.UnitY:Vector3.UnitZ;
        var yaw=Quaternion.CreateFromAxisAngle(up,settings.FacingDegrees*MathF.PI/180);
        for(var frameIndex=0;frameIndex<frames.Count;frameIndex++)
        {
            var frame=frames[frameIndex];
            if(!settings.FirstPerson&&wristTargets is null)
            {
                for(var i=0;i<frame.Length;i++)if(skeleton[i].ParentIndex<0)
                    frame[i]=new XForm(Vector3.Transform(frame[i].Pos,yaw)+up*settings.GroundOffset*scale,Quaternion.Normalize(yaw*frame[i].Rot));
                continue;
            }
            Solve(BoneRole.UpperArmL,BoneRole.LowerArmL,BoneRole.HandL,settings.LeftShoulder,settings.LeftElbow,left);
            Solve(BoneRole.UpperArmR,BoneRole.LowerArmR,BoneRole.HandR,settings.RightShoulder,settings.RightElbow,right);
            void Solve(BoneRole upperRole,BoneRole lowerRole,BoneRole handRole,Vector3 shoulder,Vector3 pole,ArmConstraintSolver solver)
            {
                if(rig.BoneForRole(handRole) is not int h)return;
                FkUtil.ToWorld(frame,skeleton,world);
                var originalWrist=world[h];
                if(wristTargets is not null&&!wristTargets[frameIndex].TryGetValue(handRole,out originalWrist))return;
                if(rig.BoneForRole(upperRole) is not int u || rig.BoneForRole(lowerRole) is not int l)
                {
                    if(wristTargets is not null)SetWorld(h,originalWrist);
                    return;
                }
                var upperLength=Vector3.Distance(world[u].Pos,world[l].Pos);var lowerLength=Vector3.Distance(world[l].Pos,world[h].Pos);
                var result=solver.Solve(originalWrist.Pos,originalWrist.Rot,new ArmSettings { Shoulder=Convert(shoulder),ElbowTarget=Convert(pole),UpperLength=upperLength,ForearmLength=lowerLength,MaximumReach=settings.Reach });
                var upperRotation=Quaternion.Normalize(MathQ.FromTo(world[l].Pos-world[u].Pos,result.Elbow-result.Shoulder)*world[u].Rot);
                SetWorld(u,new XForm(result.Shoulder,upperRotation));
                FkUtil.ToWorld(frame,skeleton,world);
                var lowerRotation=Quaternion.Normalize(MathQ.FromTo(world[h].Pos-world[l].Pos,result.Wrist-result.Elbow)*world[l].Rot);
                SetWorld(l,new XForm(world[l].Pos,lowerRotation));
                FkUtil.ToWorld(frame,skeleton,world);
                SetWorld(h,new XForm(world[h].Pos,originalWrist.Rot)); // preserve captured wrist attitude and finger locals
            }
            void SetWorld(int index,XForm value)
            {
                var parent=skeleton[index].ParentIndex;
                frame[index]=parent<0?value:XForm.Compose(world[parent].Inverse(),value);
            }
        }
    }
}
