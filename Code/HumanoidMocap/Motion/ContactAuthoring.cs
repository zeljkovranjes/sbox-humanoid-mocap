using System;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Motion;
using Vector3=System.Numerics.Vector3;

public static class ContactAuthoring
{
    /// <summary>Explicit manual anchor from an observed wrist at a nearby sample.
    /// This is a user placement aid, not an automatically detected grip.</summary>
    public static double PlaceAtWrist(MotionDocument motion,ContactInterval contact,bool includeRotation=false)
    {
        var sample=SampleWristAnchor(motion,contact,(contact.Start+contact.End)*.5);
        contact.LocalTarget=MotionDocument.A(sample.Local.Pos);
        if(includeRotation)contact.LocalRotation=MotionDocument.A(sample.Local.Rot);
        return sample.Time;
    }

    /// <summary>Samples observed motion without modifying the capture or contact draft.</summary>
    public static (double Time,XForm Local) SampleWristAnchor(MotionDocument motion,ContactInterval contact,double time)
    {
        motion.Validate();
        if(!double.IsFinite(time)||!double.IsFinite(contact.Start+contact.End)||contact.End<=contact.Start
            ||contact.Start<motion.Frames[0].Time||contact.End>motion.Frames[^1].Time||time<contact.Start||time>contact.End)
            throw new ArgumentException("Choose a sample time inside a nonempty contact interval and capture range.");
        var hand=motion.Bones.FindIndex(b=>b.Name==contact.Bone);
        var prop=motion.Objects.FirstOrDefault(p=>p.Id==contact.Object);
        if(hand<0||prop is null)throw new ArgumentException("Choose a wrist and prop.");
        var bone=string.IsNullOrEmpty(contact.ObjectBone)?0:prop.Bones.FindIndex(b=>b.Name==contact.ObjectBone);
        if(bone<0)throw new ArgumentException("Choose a prop bone.");
        var frame=motion.Frames.Where(f=>f.Time>=contact.Start&&f.Time<=contact.End)
            .OrderBy(f=>Math.Abs(f.Time-time)).FirstOrDefault();
        if(frame is null)throw new ArgumentException("The interval contains no captured samples.");
        var wrist=new XForm(MotionDocument.V(frame.Positions[hand]),MotionDocument.Q(frame.Rotations[hand]));
        for(var b=hand;b>=0;b=motion.Bones[b].Parent)
        {
            if(frame.Evidence[b]!=JointEvidence.Reconstructed)throw new ArgumentException("The wrist was not observed at the nearest sample. Choose a visible time.");
            if(b!=hand)wrist=XForm.Compose(new(MotionDocument.V(frame.Positions[b]),MotionDocument.Q(frame.Rotations[b])),wrist);
        }
        var props=new PropContactMotion(motion);
        // Key authoring must work before a sliding draft has its required two keys.
        if(props.UnsupportedReason(new(){Bone=contact.Bone,Object=contact.Object}) is {} reason)throw new ArgumentException(reason);
        if(!props.TrySample(contact.Object,frame.Time,out var world,out var valid)||!valid[bone])throw new ArgumentException("The prop track is unavailable at this time.");
        var local=XForm.Compose(world[bone].Inverse(),wrist);
        return (frame.Time,local);
    }

    /// <summary>Adds or replaces one draft key, rejecting conflicting times without mutation.</summary>
    public static int SetSlidingKey(ContactInterval contact,int index,double time,Vector3 position)
    {
        if(index < -1||index>=contact.TargetKeys.Count)throw new ArgumentOutOfRangeException(nameof(index));
        if(!double.IsFinite(time)||time<contact.Start||time>contact.End||!double.IsFinite(contact.Start+contact.End)||contact.End<=contact.Start)
            throw new ArgumentException("Key time must be inside the contact interval.");
        if(!float.IsFinite(position.X)||!float.IsFinite(position.Y)||!float.IsFinite(position.Z))throw new ArgumentException("Key position must be finite.");
        if(contact.TargetKeys.Where((_,i)=>i!=index).Any(k=>Math.Abs(k.Time-time)<1e-7))throw new ArgumentException("A key already exists at this time. Select it to edit it.");
        if(index<0&&contact.TargetKeys.Count>=4096)throw new ArgumentException("At most 4,096 manually edited keys per contact are supported.");
        var key=new ContactTargetKey{Time=time,Position=MotionDocument.A(position)};
        if(index<0)contact.TargetKeys.Add(key);else contact.TargetKeys[index]=key;
        contact.TargetKeys.Sort((a,b)=>a.Time.CompareTo(b.Time));
        contact.Review=ContactReview.Suggested;
        return contact.TargetKeys.IndexOf(key);
    }

    public static MotionDocument Replace(MotionDocument motion,int index,ContactInterval replacement)
    {
        if(index < -1||index>=motion.Contacts.Count)throw new ArgumentOutOfRangeException(nameof(index));
        if(replacement.Start<motion.Frames[0].Time||replacement.End>motion.Frames[^1].Time||replacement.End<=replacement.Start)
            throw new ArgumentException("Choose a nonempty contact interval inside the capture range.");
        var result=motion.Copy();
        if(index<0)result.Contacts.Add(replacement);else result.Contacts[index]=replacement;
        result.Validate();return result.Copy();
    }
}
