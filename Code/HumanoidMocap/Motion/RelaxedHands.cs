using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>A body capture has no finger tracks, which leaves a target's fingers frozen in its
/// bind pose: on most rigs a flat, splayed hand that reads as a claw on a moving character.
/// This gives such hands one constant, slightly curled resting shape. It is an authored pose,
/// not captured finger motion, and is never applied when the source has any finger track.</summary>
public static class RelaxedHands
{
    // Flexion in degrees at the knuckle, middle and end joint; curl deepens from index to pinky.
    static readonly (string Finger,float Prox,float Mid,float Dist)[] Curl=
    {("Thumb",8,14,10),("Index",24,30,12),("Middle",30,34,15),("Ring",35,38,17),("Pinky",40,42,20)};
    public static int Apply(List<XForm[]> frames,MappingResult sourceMapping,TargetRig target)
    {
        if(frames.Count==0)return 0;
        foreach(var role in sourceMapping.RoleToBone.Keys)if(FingerSolver.IsFingerRole(role))return 0;
        var rig=target.Skeleton;var rest=rig.RestWorld;
        var map=new MappingResult("Target hand anatomy",MappingSource.Authored);
        foreach(var bone in rig.Bones)if(target.RoleOf(bone.Index) is { } role)map.RoleToBone[role]=bone.Index;
        var posed=new List<(int Bone,Quaternion Local)>();
        foreach(var left in new[]{true,false})
        {
            if(HandGeometry.Dorsal(map,rest,left) is not { } dorsal)continue;
            var side=left?"L":"R";
            foreach(var (finger,prox,mid,dist) in Curl)
            {
                var chain=new[]{("Prox",prox),("Mid",mid),("Dist",dist)};
                for(var j=0;j<chain.Length;j++)
                {
                    if(!Enum.TryParse<BoneRole>(finger+chain[j].Item1+side,out var role)||!map.RoleToBone.TryGetValue(role,out var bone))continue;
                    // Direction of this phalanx at rest: toward the next joint, else away from the previous one.
                    Vector3 along;
                    if(j+1<chain.Length&&Enum.TryParse<BoneRole>(finger+chain[j+1].Item1+side,out var nextRole)&&map.RoleToBone.TryGetValue(nextRole,out var next))along=rest[next].Pos-rest[bone].Pos;
                    else if(j>0&&Enum.TryParse<BoneRole>(finger+chain[j-1].Item1+side,out var previousRole)&&map.RoleToBone.TryGetValue(previousRole,out var previous))along=rest[bone].Pos-rest[previous].Pos;
                    else continue;
                    if(along.LengthSquared()<1e-10f)continue;
                    // Curling turns the phalanx toward the palm, about the axis perpendicular to both.
                    var axis=Vector3.Cross(Vector3.Normalize(along),-dorsal);
                    if(axis.LengthSquared()<1e-8f)continue;
                    var localAxis=Vector3.Transform(Vector3.Normalize(axis),Quaternion.Inverse(rest[bone].Rot));
                    var curl=Quaternion.CreateFromAxisAngle(Vector3.Normalize(localAxis),chain[j].Item2*MathF.PI/180);
                    posed.Add((bone,Quaternion.Normalize(rig[bone].RestLocal.Rot*curl)));
                }
            }
        }
        foreach(var frame in frames)foreach(var (bone,local) in posed)frame[bone].Rot=local;
        return posed.Count;
    }
}
