using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

public readonly record struct ContactSample(double Time,Vector3 Hand, XForm Object,
    Vector3 ClosestSurfacePoint, float FingerClosure, bool Observed,Vector3? Wrist=null);

public sealed class ContactSettings
{
    public float EnterDistance { get; set; } = .025f;
    public float ExitDistance { get; set; } = .05f;
    public float MaximumRelativeSpeed { get; set; } = .15f;
    public double MinimumPersistence { get; set; } = .12;
    public double ReleasePersistence { get; set; } = .08;
    public double BlendSeconds { get; set; } = .08;
    public double MaximumSampleGap { get; set; } = .1;
}

/// <summary>Suggestions require an explicit object track and actual surface samples.
/// The object is authoritative; solving never changes its transform in response to a hand.</summary>
public static class ContactSolver
{
    public static List<ContactInterval> Suggest(IReadOnlyList<ContactSample> samples,string hand,string prop,ContactSettings s)
    {
        if(!float.IsFinite(s.EnterDistance+s.ExitDistance+s.MaximumRelativeSpeed)||!double.IsFinite(s.MinimumPersistence+s.ReleasePersistence+s.MaximumSampleGap)
            ||s.EnterDistance<=0 || s.ExitDistance<=s.EnterDistance || s.MaximumRelativeSpeed<=0||s.MinimumPersistence<0 || s.ReleasePersistence<0||s.MaximumSampleGap<=0)
            throw new ArgumentException("Invalid contact hysteresis settings.");
        var result=new List<ContactInterval>();int candidate=-1,active=-1,release=-1;Vector3 local=default;
        for(var i=0;i<samples.Count;i++)
        {
            var x=samples[i];
            if(i>0 && x.Time<=samples[i-1].Time)throw new ArgumentException("Contact times must increase.");
            // Loss of observations is not evidence of a continuing grip. Never bridge
            // an occlusion or unsampled interval using temporal hysteresis.
            if(!x.Observed||(i>0&&x.Time-samples[i-1].Time>s.MaximumSampleGap))
            {
                if(active>=0)Add(Math.Max(active,release>=0?release-1:i-1));
                active=candidate=release=-1;
                if(!x.Observed)continue;
            }
            var inv=x.Object.Inverse();var p=XForm.Compose(inv,new XForm(x.Hand,Quaternion.Identity)).Pos;
            var speed=i==0||!samples[i-1].Observed||x.Time-samples[i-1].Time>s.MaximumSampleGap?float.PositiveInfinity:(p-XForm.Compose(samples[i-1].Object.Inverse(),new XForm(samples[i-1].Hand,Quaternion.Identity)).Pos).Length()/(float)(x.Time-samples[i-1].Time);
            var distance=Vector3.Distance(x.Hand,x.ClosestSurfacePoint);
            bool enter=x.Observed && distance<s.EnterDistance && speed<s.MaximumRelativeSpeed && x.FingerClosure>.25f;
            bool stay=x.Observed && distance<s.ExitDistance && speed<s.MaximumRelativeSpeed*2&&x.FingerClosure>.15f;
            if(active<0)
            {
                if(!enter){candidate=-1;continue;}
                if(candidate<0)candidate=i;
                if(x.Time-samples[candidate].Time>=s.MinimumPersistence)
                {
                    active=candidate;local=XForm.Compose(samples[active].Object.Inverse(),new XForm(samples[active].Wrist??samples[active].ClosestSurfacePoint,Quaternion.Identity)).Pos;
                }
            }
            else if(stay)release=-1;
            else
            {
                if(release<0)release=i;
                if(x.Time-samples[release].Time>=s.ReleasePersistence)
                {
                    Add(Math.Max(active,release-1));active=candidate=release=-1;
                }
            }
        }
        if(active>=0)Add(release>=0?Math.Max(active,release-1):samples.Count-1);
        return result;
        void Add(int end) => result.Add(new ContactInterval { Bone=hand,Object=prop,Start=samples[active].Time,
            End=samples[end].Time,LocalTarget=MotionDocument.A(local),Review=ContactReview.Suggested,
            Reason="Proximity, relative speed, finger closure and persistence; requires review. Not a calibrated probability." });
    }

    public static Vector3 Apply(Vector3 freeHand,XForm authoritativeObject,ContactInterval contact,double time,ContactSettings settings,
        Vector3? movingLocalTarget=null)
    {
        if(contact.Review!=ContactReview.Confirmed || time<contact.Start || time>contact.End)return freeHand;
        var local=contact.Sliding && movingLocalTarget is { } moving?moving:LocalTarget(contact,time);
        var target=XForm.Compose(authoritativeObject,new XForm(local,Quaternion.Identity)).Pos;
        return Vector3.Lerp(freeHand,target,Weight(contact,time,settings));
    }

    public static float Weight(ContactInterval contact,double time,ContactSettings settings)
    {
        if(contact.Review!=ContactReview.Confirmed||time<contact.Start||time>contact.End)return 0;
        var fade=Math.Max(settings.BlendSeconds,1e-5);
        var t=(float)Math.Clamp(Math.Min((time-contact.Start)/fade,(contact.End-time)/fade),0,1);
        return t*t*(3-2*t);
    }

    public static Vector3 LocalTarget(ContactInterval contact,double time)
    {
        if(!contact.Sliding)return MotionDocument.V(contact.LocalTarget);
        var keys=contact.TargetKeys;
        if(keys.Count<2)throw new ArgumentException("Sliding contacts need object-local target keys.");
        if(time<=keys[0].Time)return MotionDocument.V(keys[0].Position);
        for(var i=1;i<keys.Count;i++)if(time<=keys[i].Time)
            return Vector3.Lerp(MotionDocument.V(keys[i-1].Position),MotionDocument.V(keys[i].Position),
                (float)((time-keys[i-1].Time)/(keys[i].Time-keys[i-1].Time)));
        return MotionDocument.V(keys[^1].Position);
    }
}
