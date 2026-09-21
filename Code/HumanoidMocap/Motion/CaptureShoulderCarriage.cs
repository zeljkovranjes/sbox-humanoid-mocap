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

/// <summary>Carries a captured body's shoulder movement onto a target whose clavicles are built
/// differently. A reconstructed SMPL clavicle starts low in the chest and points upward even at
/// rest, while a game rig's is nearly level, so copying its direction outright shrugged the
/// target: on the kata sample the performer's shoulders sat 3-6 degrees below her own rest
/// while Human's were 12 and 30 degrees above its rest. Here the change of the clavicle from the
/// performer's own rest is measured in an anatomical chest frame (up, left-right, forward)
/// carried by each skeleton's chest bone, and that change is applied to the target's own rest
/// clavicle. Arms keep their solved world orientation. Depression below rest is limited, since
/// a T-pose rest already holds the shoulders higher than a standing body does.</summary>
public static class CaptureShoulderCarriage
{
    public const float MaximumDepressionDegrees=4,MaximumElevationDegrees=45;
    sealed record Side(int Chest,int Clavicle,int Arm,Quaternion ChestRestInverse,Matrix4x4 RestAxes,Vector3 RestDirection);
    /// <returns>Number of clavicle samples rewritten.</returns>
    public static int Apply(List<XForm[]> frames,IReadOnlyList<XForm[]> sourceFrames,Skeleton.Skeleton source,MappingResult mapping,TargetRig target)
    {
        if(frames.Count==0||sourceFrames.Count!=frames.Count)return 0;
        var rig=target.Skeleton;var targetMap=new MappingResult("Target body anatomy",MappingSource.Authored);
        foreach(var bone in rig.Bones)if(target.RoleOf(bone.Index) is { } role)targetMap.RoleToBone[role]=bone.Index;
        var samples=0;var world=new XForm[rig.Count];
        foreach(var left in new[]{true,false})
        {
            if(Build(source,mapping,left) is not { } from||Build(rig,targetMap,left) is not { } to)continue;
            for(var f=0;f<frames.Count;f++)
            {
                var sourceWorld=new Pose(sourceFrames[f]).ToWorld(source);
                var posed=Components(sourceWorld[from.Arm].Pos-sourceWorld[from.Clavicle].Pos,sourceWorld[from.Chest].Rot,from);
                if(posed is not { } pose)continue;
                // The performer's change from her own rest, limited to what a shoulder girdle does.
                var change=MathQ.FromTo(from.RestDirection,pose);
                var desired=Vector3.Normalize(Vector3.Transform(to.RestDirection,change));
                var rise=MathF.Asin(Math.Clamp(desired.Y,-1,1));var restRise=MathF.Asin(Math.Clamp(to.RestDirection.Y,-1,1));
                var limited=Math.Clamp(rise,restRise-MaximumDepressionDegrees*MathF.PI/180,restRise+MaximumElevationDegrees*MathF.PI/180);
                if(limited!=rise)
                {
                    var flat=new Vector3(desired.X,0,desired.Z);if(flat.LengthSquared()<1e-10f)continue;
                    desired=Vector3.Normalize(Vector3.Normalize(flat)*MathF.Cos(limited)+Vector3.UnitY*MathF.Sin(limited));
                }
                var frame=frames[f];FkUtil.ToWorld(frame,rig,world);
                var axes=Axes(world[to.Chest].Rot,to);
                var goal=Vector3.Normalize(axes.Lateral*desired.X+axes.Up*desired.Y+axes.Forward*desired.Z);
                var current=world[to.Arm].Pos-world[to.Clavicle].Pos;if(current.LengthSquared()<1e-10f)continue;
                var armRotation=world[to.Arm].Rot;
                var clavicle=Quaternion.Normalize(MathQ.FromTo(Vector3.Normalize(current),goal)*world[to.Clavicle].Rot);
                var parent=rig[to.Clavicle].ParentIndex;
                frame[to.Clavicle].Rot=Quaternion.Normalize((parent<0?Quaternion.Identity:Quaternion.Inverse(world[parent].Rot))*clavicle);
                // The arm was solved by its own direction; keep that in the world while its shoulder moves.
                if(rig[to.Arm].ParentIndex==to.Clavicle)frame[to.Arm].Rot=Quaternion.Normalize(Quaternion.Inverse(clavicle)*armRotation);
                samples++;
            }
        }
        return samples;
    }
    static Side? Build(Skeleton.Skeleton skeleton,MappingResult map,bool left)
    {
        if(!map.RoleToBone.TryGetValue(left?BoneRole.ClavicleL:BoneRole.ClavicleR,out var clavicle)||!map.RoleToBone.TryGetValue(left?BoneRole.UpperArmL:BoneRole.UpperArmR,out var arm)||
            !map.RoleToBone.TryGetValue(BoneRole.UpperArmL,out var armL)||!map.RoleToBone.TryGetValue(BoneRole.UpperArmR,out var armR)||
            !map.RoleToBone.TryGetValue(BoneRole.Neck,out var neck)||!map.RoleToBone.TryGetValue(BoneRole.Spine1,out var spine))return null;
        var chest=skeleton[clavicle].ParentIndex;if(chest<0)return null;
        var rest=skeleton.RestWorld;
        var up=rest[neck].Pos-rest[spine].Pos;var lateral=rest[armL].Pos-rest[armR].Pos;
        if(up.LengthSquared()<1e-10f||lateral.LengthSquared()<1e-10f)return null;
        up=Vector3.Normalize(up);lateral-=up*Vector3.Dot(lateral,up);if(lateral.LengthSquared()<1e-10f)return null;
        lateral=Vector3.Normalize(lateral);var forward=Vector3.Cross(lateral,up);
        var side=new Side(chest,clavicle,arm,Quaternion.Inverse(rest[chest].Rot),
            new Matrix4x4(lateral.X,lateral.Y,lateral.Z,0,up.X,up.Y,up.Z,0,forward.X,forward.Y,forward.Z,0,0,0,0,1),Vector3.Zero);
        return Components(rest[arm].Pos-rest[clavicle].Pos,rest[chest].Rot,side) is { } direction?side with{RestDirection=direction}:null;
    }
    static (Vector3 Lateral,Vector3 Up,Vector3 Forward) Axes(Quaternion chest,Side side)
    {
        // Anatomical axes fixed to the chest bone: its rest axes, turned by the chest's change from rest.
        var carried=Quaternion.Normalize(chest*side.ChestRestInverse);var m=side.RestAxes;
        return(Vector3.Transform(new Vector3(m.M11,m.M12,m.M13),carried),Vector3.Transform(new Vector3(m.M21,m.M22,m.M23),carried),Vector3.Transform(new Vector3(m.M31,m.M32,m.M33),carried));
    }
    /// <summary>Unit direction as (left-right, up, forward) components in the chest's anatomical frame.</summary>
    static Vector3? Components(Vector3 direction,Quaternion chest,Side side)
    {
        if(direction.LengthSquared()<1e-10f)return null;
        var axes=Axes(chest,side);direction=Vector3.Normalize(direction);
        return new(Vector3.Dot(direction,axes.Lateral),Vector3.Dot(direction,axes.Up),Vector3.Dot(direction,axes.Forward));
    }
}
