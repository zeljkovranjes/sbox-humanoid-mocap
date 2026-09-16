using System;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Motion;

public static class ContactAuthoring
{
    /// <summary>Explicit manual anchor from an observed wrist at a nearby sample.
    /// This is a user placement aid, not an automatically detected grip.</summary>
    public static double PlaceAtWrist(MotionDocument motion,ContactInterval contact,bool includeRotation=false)
    {
        motion.Validate();
        var hand=motion.Bones.FindIndex(b=>b.Name==contact.Bone);
        var prop=motion.Objects.FirstOrDefault(p=>p.Id==contact.Object);
        if(hand<0||prop is null)throw new ArgumentException("Choose a wrist and prop.");
        var bone=string.IsNullOrEmpty(contact.ObjectBone)?0:prop.Bones.FindIndex(b=>b.Name==contact.ObjectBone);
        if(bone<0)throw new ArgumentException("Choose a prop bone.");
        var midpoint=(contact.Start+contact.End)*.5;
        var frame=motion.Frames.Where(f=>f.Time>=contact.Start&&f.Time<=contact.End)
            .OrderBy(f=>Math.Abs(f.Time-midpoint)).FirstOrDefault();
        if(frame is null)throw new ArgumentException("The interval contains no captured samples.");
        var wrist=new XForm(MotionDocument.V(frame.Positions[hand]),MotionDocument.Q(frame.Rotations[hand]));
        for(var b=hand;b>=0;b=motion.Bones[b].Parent)
        {
            if(frame.Evidence[b]!=JointEvidence.Reconstructed)throw new ArgumentException("The wrist was not observed near the midpoint. Choose a visible interval.");
            if(b!=hand)wrist=XForm.Compose(new(MotionDocument.V(frame.Positions[b]),MotionDocument.Q(frame.Rotations[b])),wrist);
        }
        var props=new PropContactMotion(motion);
        if(props.UnsupportedReason(contact) is {} reason)throw new ArgumentException(reason);
        if(!props.TrySample(contact.Object,frame.Time,out var world,out var valid)||!valid[bone])throw new ArgumentException("The prop track is unavailable at this time.");
        var local=XForm.Compose(world[bone].Inverse(),wrist);
        contact.LocalTarget=MotionDocument.A(local.Pos);
        if(includeRotation)contact.LocalRotation=MotionDocument.A(local.Rot);
        return frame.Time;
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
