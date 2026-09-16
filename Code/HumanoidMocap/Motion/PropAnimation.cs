using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Motion;

/// <summary>Combines the character and explicit prop armatures for portable bone-only export.
/// Prop roots remain independent of character bones; contact solving cannot move them.</summary>
public sealed record PropAnimation(SkeletonModel Skeleton,IReadOnlyList<XForm[]> Frames)
{
    public static PropAnimation Append(SkeletonModel character,IReadOnlyList<XForm[]> frames,
        IReadOnlyList<double> sourceTimes,PropContactMotion tracks,CapturePlacement placement)
    {
        if(tracks.Objects.Count==0)return new(character,frames);
        int count=character.Count+tracks.Objects.Sum(p=>p.Bones.Count);
        if(count>1024||(long)count*frames.Count>Formats.Fbx.FbxAnimationWriter.MaximumTransformSamples)
            throw new ArgumentException("Character and prop armatures exceed the export budget.");
        if(sourceTimes.Count!=frames.Count)throw new ArgumentException("Prop and character sample times differ.");
        var definitions=character.Bones.Select(b=>new BoneDefinition(b.Name,b.ParentIndex<0?null:character[b.ParentIndex].Name,b.RestLocal)).ToList();
        var restById=new Dictionary<string,XForm[]>();
        for(var p=0;p<tracks.Objects.Count;p++)
        {
            var prop=tracks.Objects[p];var rest=new XForm[prop.Bones.Count];
            for(var b=0;b<rest.Length;b++)
            {
                var bone=prop.Bones[b];var local=new XForm(MotionDocument.V(bone.RestPosition),MotionDocument.Q(bone.RestRotation));
                var parent=bone.Parent<0?(string.IsNullOrEmpty(prop.ParentObject)?XForm.Identity:restById[prop.ParentObject][0]):rest[bone.Parent];
                rest[b]=XForm.Compose(parent,local);
                var output=bone.Parent<0?placement.Transform(rest[b]):new XForm(local.Pos*placement.Units,local.Rot);
                definitions.Add(new(Name(p,bone.Name),bone.Parent<0?null:Name(p,prop.Bones[bone.Parent].Name),output));
            }
            restById.Add(prop.Id,rest);
        }
        var skeleton=SkeletonModel.Create(definitions);var result=new List<XForm[]>(frames.Count);
        for(var f=0;f<frames.Count;f++)
        {
            var frame=new XForm[count];Array.Copy(frames[f],frame,character.Count);int offset=character.Count;
            foreach(var prop in tracks.Objects)
            {
                if(!tracks.TrySample(prop.Id,sourceTimes[f],out var world,out var available)||available.Any(a=>!a))
                    throw new InvalidOperationException($"Prop '{prop.Id}' has missing motion at {sourceTimes[f]:F3}s. Trim the clip or explicitly fill the object track before export.");
                for(var b=0;b<world.Length;b++)
                {
                    var parent=prop.Bones[b].Parent;
                    var local=parent<0?world[b]:XForm.Compose(world[parent].Inverse(),world[b]);
                    frame[offset++]=parent<0?placement.Transform(local):new XForm(local.Pos*placement.Units,local.Rot);
                }
            }
            result.Add(frame);
        }
        return new(skeleton,result);
    }
    public static string Name(int objectIndex,string bone)=>$"hm_prop_{objectIndex}_{bone}";
}
