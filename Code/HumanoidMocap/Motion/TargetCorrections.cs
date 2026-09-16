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
    public Vector3 LeftShoulder { get; set; } = new(.18f,1.45f,0);
    public Vector3 RightShoulder { get; set; } = new(-.18f,1.45f,0);
    public Vector3 LeftElbow { get; set; } = new(.45f,1.1f,.15f);
    public Vector3 RightElbow { get; set; } = new(-.45f,1.1f,.15f);
    public float Reach { get; set; } = .995f;
    public float GroundOffset { get; set; }
    public float FacingDegrees { get; set; }
}

public static class TargetCorrections
{
    public static void Apply(List<XForm[]> frames,TargetRig rig,TargetUpAxis axis,TargetCorrectionSettings settings)
    {
        var skeleton=rig.Skeleton;var world=new XForm[skeleton.Count];
        var left=new ArmConstraintSolver();var right=new ArmConstraintSolver();
        var scale=axis==TargetUpAxis.YUpCm?100f:39.3700787f;
        var conversion=axis==TargetUpAxis.YUpCm?Quaternion.Identity:Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2);
        Vector3 Convert(Vector3 v)=>Vector3.Transform(v*scale,conversion);
        var up=axis==TargetUpAxis.YUpCm?Vector3.UnitY:Vector3.UnitZ;
        var yaw=Quaternion.CreateFromAxisAngle(up,settings.FacingDegrees*MathF.PI/180);
        foreach(var frame in frames)
        {
            if(!settings.FirstPerson)
            {
                for(var i=0;i<frame.Length;i++)if(skeleton[i].ParentIndex<0)
                    frame[i]=new XForm(Vector3.Transform(frame[i].Pos,yaw)+up*settings.GroundOffset*scale,Quaternion.Normalize(yaw*frame[i].Rot));
                continue;
            }
            Solve(BoneRole.UpperArmL,BoneRole.LowerArmL,BoneRole.HandL,settings.LeftShoulder,settings.LeftElbow,left);
            Solve(BoneRole.UpperArmR,BoneRole.LowerArmR,BoneRole.HandR,settings.RightShoulder,settings.RightElbow,right);
            void Solve(BoneRole upperRole,BoneRole lowerRole,BoneRole handRole,Vector3 shoulder,Vector3 pole,ArmConstraintSolver solver)
            {
                if(rig.BoneForRole(upperRole) is not int u || rig.BoneForRole(lowerRole) is not int l || rig.BoneForRole(handRole) is not int h)return;
                FkUtil.ToWorld(frame,skeleton,world);
                var originalWrist=world[h];var upperLength=Vector3.Distance(world[u].Pos,world[l].Pos);var lowerLength=Vector3.Distance(world[l].Pos,world[h].Pos);
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
