using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Explicit object animation in the capture's metre coordinates. Sampling never
/// extrapolates or treats an unobserved object as tracked. Objects drive hands, never vice versa.</summary>
public sealed class PropContactMotion
{
    public IReadOnlyList<PropTrack> Objects { get; }
    public IReadOnlyList<ContactInterval> Contacts { get; }
    public double StartTime { get; }
    public double EndTime { get; }
    readonly Dictionary<string,PropTrack> _objects;
    readonly Dictionary<string,BoneRole?> _roles;
    readonly MotionSpace _space;
    readonly bool _handOnly;

    public PropContactMotion(MotionDocument motion)
    {
        Objects=motion.Objects;Contacts=motion.Contacts;StartTime=motion.Frames[0].Time;EndTime=motion.Frames[^1].Time;
        _space=motion.Space;_objects=Objects.ToDictionary(p=>p.Id,StringComparer.Ordinal);
        _handOnly=HandCaptureRetargeter.Supports(motion);
        _roles=motion.Bones.ToDictionary(b=>b.Name,b=>b.Role,StringComparer.Ordinal);
    }

    public static void ValidateObjects(MotionDocument motion)
    {
        if(motion.Objects is null||motion.Objects.Count>64)throw new FormatException("At most 64 prop tracks are supported.");
        var objects=new Dictionary<string,PropTrack>(StringComparer.Ordinal);
        long samples=(long)motion.Bones.Count*motion.Frames.Count;
        foreach(var prop in motion.Objects)
        {
            if(prop is null||string.IsNullOrWhiteSpace(prop.Id)||!objects.TryAdd(prop.Id,prop)
                ||prop.Source==ObjectMotionSource.Unspecified||!Enum.IsDefined(typeof(ObjectMotionSource),prop.Source)||!Enum.IsDefined(typeof(MotionSpace),prop.Space))
                throw new FormatException("Prop tracks need unique IDs, an explicit motion source and coordinate space.");
            if(prop.Bones is null||prop.Frames is null||prop.Bones.Count==0||prop.Bones[0].Parent!=-1
                ||prop.Bones.Count(b=>b.Parent<0)!=1)
                throw new FormatException("Each prop needs one root and an articulated skeleton.");
            if(!string.IsNullOrEmpty(prop.ParentObject)&&
                (prop.ParentObject==prop.Id||!objects.TryGetValue(prop.ParentObject,out var parent)||parent.Space!=prop.Space))
                throw new FormatException("A prop parent must precede its child and use the same coordinate space.");
            samples+=(long)prop.Bones.Count*prop.Frames.Count;
            if(samples>Formats.Fbx.FbxAnimationWriter.MaximumTransformSamples)
                throw new FormatException("Motion and props exceed the transform budget. Select a shorter range.");
            // Reuse the exact channel/hierarchy checks; this child document has no objects or contacts.
            new MotionDocument{SourceFps=motion.SourceFps,Bones=prop.Bones,Frames=prop.Frames,Space=prop.Space}.Validate();
        }
        foreach(var contact in motion.Contacts)
        {
            // Older cleanup-only interval annotations have no object binding.
            if(string.IsNullOrEmpty(contact.Object))continue;
            if(!objects.TryGetValue(contact.Object,out var prop)||!motion.Bones.Any(b=>b.Name==contact.Bone)
                ||(!string.IsNullOrEmpty(contact.ObjectBone)&&!prop.Bones.Any(b=>b.Name==contact.ObjectBone)))
                throw new FormatException("Contact references a missing hand, object or object bone.");
            if(prop.Space!=motion.Space)throw new FormatException("Contact and prop coordinate spaces differ; provide an aligned track.");
        }
    }

    public string? UnsupportedReason(ContactInterval contact)
    {
        if(string.IsNullOrEmpty(contact.Object))return "Interval annotation only; no object motion is bound.";
        if(!_handOnly||_space!=MotionSpace.CameraRelative||!_roles.TryGetValue(contact.Bone,out var role)||role is not (BoneRole.HandL or BoneRole.HandR))
            return "Prop correction currently supports camera-relative wrist contacts only.";
        if(contact.Sliding&&contact.TargetKeys.Count<2)return "Sliding contacts need at least two object-local target keys.";
        return null;
    }

    /// <summary>Returns object bone transforms in capture coordinates, with availability
    /// propagated through the hierarchy. Time is the original source-video timestamp.</summary>
    public bool TrySample(string id,double time,out XForm[] world,out bool[] available)
    {
        world=Array.Empty<XForm>();available=Array.Empty<bool>();
        if(!_objects.TryGetValue(id,out var prop)||!double.IsFinite(time)||time<prop.Frames[0].Time||time>prop.Frames[^1].Time)return false;
        var low=0;var high=prop.Frames.Count-1;
        while(low<high){var mid=(low+high)/2;if(prop.Frames[mid].Time<time)low=mid+1;else high=mid;}
        var b=prop.Frames[low];var a=prop.Frames[Math.Max(0,low-1)];
        float t=b.Time>a.Time?(float)((time-a.Time)/(b.Time-a.Time)):0;
        var parentRoot=XForm.Identity;var parentAvailable=true;
        if(!string.IsNullOrEmpty(prop.ParentObject))
        {
            if(!TrySample(prop.ParentObject,time,out var parent,out var parentValid))return false;
            parentRoot=parent[0];parentAvailable=parentValid[0];
        }
        world=new XForm[prop.Bones.Count];available=new bool[world.Length];
        for(var i=0;i<world.Length;i++)
        {
            var local=new XForm(Vector3.Lerp(MotionDocument.V(a.Positions[i]),MotionDocument.V(b.Positions[i]),t),
                Quaternion.Normalize(Quaternion.Slerp(MotionDocument.Q(a.Rotations[i]),MotionDocument.Q(b.Rotations[i]),t)));
            var p=prop.Bones[i].Parent;
            world[i]=XForm.Compose(p<0?parentRoot:world[p],local);
            available[i]=(p<0?parentAvailable:available[p])&&(t>=1||a.Evidence[i]!=JointEvidence.Unobserved)
                &&(t<=0||b.Evidence[i]!=JointEvidence.Unobserved);
        }
        return true;
    }

    public Vector3 ApplyWrist(string bone,Vector3 freeWrist,double time,ContactSettings settings)
    {
        var weighted=Vector3.Zero;var total=0f;
        foreach(var contact in Contacts)
        {
            if(contact.Bone!=bone||contact.Review!=ContactReview.Confirmed||string.IsNullOrEmpty(contact.Object))continue;
            if(UnsupportedReason(contact) is { } reason)throw new InvalidOperationException(reason);
            var weight=ContactSolver.Weight(contact,time,settings);
            if(weight<=0||!TrySample(contact.Object,time,out var world,out var available))continue;
            var prop=_objects[contact.Object];var index=string.IsNullOrEmpty(contact.ObjectBone)?0:prop.Bones.FindIndex(b=>b.Name==contact.ObjectBone);
            if(!available[index])continue;
            var point=XForm.Compose(world[index],new XForm(ContactSolver.LocalTarget(contact,time),Quaternion.Identity)).Pos;
            weighted+=point*weight;total+=weight;
        }
        // Overlapping transitions blend authoritative goals together, independent of list
        // order. Sequential lerps would let the last object arbitrarily win.
        return total>0?Vector3.Lerp(freeWrist,weighted/total,Math.Min(1,total)):freeWrist;
    }
}
