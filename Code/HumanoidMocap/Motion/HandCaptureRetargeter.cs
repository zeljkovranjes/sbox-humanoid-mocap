using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Retargets an observed hand forest without fabricating a source body.
/// Uses the retargeter's anatomical hand geometry and absolute canonical-frame
/// transfer, then solves target arms against the captured wrist positions.</summary>
public static class HandCaptureRetargeter
{
    public static bool Supports(MotionDocument document)=>document.Space==MotionSpace.CameraRelative&&
        document.Bones.Any(b=>b.Role is BoneRole.HandL or BoneRole.HandR)&&
        document.Bones.All(b=>b.Role is BoneRole.HandL or BoneRole.HandR||b.Role is { } role&&FingerSolver.IsFingerRole(role));
    public static bool Supports(SourceScene source,MappingResult mapping)=>
        source.CaptureSpace==MotionSpace.CameraRelative&&source.CaptureEvidence is not null&&
        mapping.RoleToBone.Keys.Any(r=>r is BoneRole.HandL or BoneRole.HandR)&&
        mapping.RoleToBone.Keys.All(r=>r is BoneRole.HandL or BoneRole.HandR||FingerSolver.IsFingerRole(r));

    sealed record Transfer(int Source,int Target,bool Left,Quaternion Offset);

    public static Clip Solve(SourceScene source,MappingResult mapping,TargetRig target,TargetUpAxis axis,
        TargetCorrectionSettings settings,int take,string name,bool fingers=true)
    {
        if(!Supports(source,mapping)||take!=0)throw new ArgumentException("Expected a camera-relative hand motion document.");
        if(!Finite(settings.CaptureCameraPosition)||!float.IsFinite(settings.CaptureCameraYawDegrees)||!float.IsFinite(settings.CaptureCameraPitchDegrees))
            throw new ArgumentException("Capture camera placement must be finite.");
        var input=source.Clips[take];var evidence=source.CaptureEvidence!;
        if(evidence.Count!=input.Frames.Count)throw new ArgumentException("Hand evidence does not match the clip sample grid.");
        var targetMap=new MappingResult("Target hand anatomy",MappingSource.Authored);
        foreach(var bone in target.Skeleton.Bones)if(target.RoleOf(bone.Index) is { } role)targetMap.RoleToBone[role]=bone.Index;
        var sourceRest=source.Skeleton.RestWorld;var targetRest=target.Skeleton.RestWorld;
        var plans=new List<Transfer>();
        var mappedHands=new Dictionary<bool,(int Source,int Target)>();
        foreach(var left in new[]{true,false})
        {
            var handRole=left?BoneRole.HandL:BoneRole.HandR;
            if(!mapping.RoleToBone.TryGetValue(handRole,out var sourceHand)||target.BoneForRole(handRole) is not int targetHand)continue;
            mappedHands[left]=(sourceHand,targetHand);
            foreach(var (role,sourceBone) in mapping.RoleToBone)
            {
                if(role!=handRole&&(!fingers||!FingerSolver.IsFingerRole(role)||role.ToString().EndsWith("L",StringComparison.Ordinal)!=left))continue;
                if(target.BoneForRole(role) is not int targetBone)continue;
                var sourceFrame=Frame(mapping,sourceRest,role,left);
                var targetFrame=Frame(targetMap,targetRest,role,left);
                var offset=Quaternion.Normalize(Quaternion.Inverse(sourceRest[sourceBone].Rot)*sourceFrame*
                    Quaternion.Inverse(targetFrame)*targetRest[targetBone].Rot);
                plans.Add(new(sourceBone,targetBone,left,offset));
            }
        }
        if(mappedHands.Count==0)throw new ArgumentException("Map at least one target hand and its finger joints.");
        var units=axis==TargetUpAxis.YUpCm?100f:39.3700787f;
        var axisRotation=axis==TargetUpAxis.YUpCm?Quaternion.Identity:Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2);
        var cameraRotation=Quaternion.CreateFromYawPitchRoll(settings.CaptureCameraYawDegrees*MathF.PI/180,
            settings.CaptureCameraPitchDegrees*MathF.PI/180,0);
        var placement=Quaternion.Normalize(axisRotation*cameraRotation);
        var offsetPosition=Vector3.Transform(settings.CaptureCameraPosition*units,axisRotation);
        var sourceWorld=new XForm[source.Skeleton.Count];var targetWorld=new XForm[target.Skeleton.Count];
        var previous=target.Skeleton.Bones.Select(b=>b.RestLocal).ToArray();
        var seen=new HashSet<bool>();var targets=new List<Dictionary<BoneRole,XForm>>();
        var output=new Clip(name,input.Fps,input.Looping);
        for(var f=0;f<input.Frames.Count;f++)
        {
            FkUtil.ToWorld(input.Frames[f],source.Skeleton,sourceWorld);
            foreach(var (left,hand) in mappedHands)
                if(evidence[f][hand.Source]!=JointEvidence.Unobserved)seen.Add(left);
            var desired=new Dictionary<int,Quaternion>();
            foreach(var plan in plans)if(seen.Contains(plan.Left))
                desired[plan.Target]=Quaternion.Normalize(placement*sourceWorld[plan.Source].Rot*plan.Offset);
            var frame=previous.ToArray();var wristTargets=new Dictionary<BoneRole,XForm>();
            foreach(var (left,hand) in mappedHands)if(seen.Contains(left))
            {
                var position=Vector3.Transform(sourceWorld[hand.Source].Pos*(units/100),placement)+offsetPosition;
                wristTargets[left?BoneRole.HandL:BoneRole.HandR]=new(position,desired[hand.Target]);
            }
            for(var i=0;i<frame.Length;i++)
            {
                var parent=target.Skeleton[i].ParentIndex;
                var parentWorld=parent<0?XForm.Identity:targetWorld[parent];
                if(desired.TryGetValue(i,out var rotation))frame[i].Rot=Quaternion.Normalize(Quaternion.Inverse(parentWorld.Rot)*rotation);
                targetWorld[i]=parent<0?frame[i]:XForm.Compose(parentWorld,frame[i]);
            }
            output.Frames.Add(frame);targets.Add(wristTargets);previous=frame;
        }
        TargetCorrections.Apply(output.Frames,target,axis,settings,targets);
        return output;
    }

    static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);

    static Quaternion Frame(MappingResult map,IReadOnlyList<XForm> rest,BoneRole role,bool left)
    {
        var index=map.RoleToBone[role];Vector3 primary;
        if(role is BoneRole.HandL or BoneRole.HandR)
            primary=(HandGeometry.FingerProximalMidpoint(map,rest,left)??throw new ArgumentException("Hand mapping needs finger proximal joints."))-rest[index].Pos;
        else
        {
            var name=role.ToString();var side=left?"L":"R";
            var stem=name[..^1];string next,previous;
            if(stem.EndsWith("Meta",StringComparison.Ordinal)){next=stem[..^4]+"Prox";previous="Hand";}
            else if(stem.EndsWith("Prox",StringComparison.Ordinal)){next=stem[..^4]+"Mid";previous="Hand";}
            else if(stem.EndsWith("Mid",StringComparison.Ordinal)){next=stem[..^3]+"Dist";previous=stem[..^3]+"Prox";}
            else {next="";previous=stem[..^4]+"Mid";}
            if(next.Length>0&&map.RoleToBone.TryGetValue(Enum.Parse<BoneRole>(next+side),out var child))primary=rest[child].Pos-rest[index].Pos;
            else if(map.RoleToBone.TryGetValue(Enum.Parse<BoneRole>(previous+side),out var parent))primary=rest[index].Pos-rest[parent].Pos;
            else throw new ArgumentException("Finger mapping needs adjacent phalanges: "+role);
        }
        if(primary.LengthSquared()<1e-8f)throw new ArgumentException("Degenerate hand segment: "+role);
        var x=Vector3.Normalize(primary);
        var dorsal=HandGeometry.Dorsal(map,rest,left)??throw new ArgumentException("Hand mapping needs a nondegenerate index-to-pinky knuckle line.");
        var z=dorsal-x*Vector3.Dot(dorsal,x);
        z=z.LengthSquared()<1e-8f?MathQ.Perpendicular(x):Vector3.Normalize(z);
        var y=Vector3.Cross(z,x);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1)));
    }
}
