using System;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

public sealed class CleanupSettings
{
    public float Root { get; set; } = .25f;
    public float Arms { get; set; } = .1f;
    public float Fingers { get; set; } = .025f;
    public double MaximumGapSeconds { get; set; } = .1;
    /// <summary>Longer losses between two observations are bridged with an eased glide over
    /// at most this many seconds, ending at the reacquired pose, instead of a frozen pose that
    /// snaps. Zero restores hold-and-snap. Bridged samples are labelled inferred, never observed.</summary>
    public double BridgeSeconds { get; set; } = 1.5;
    public float PreserveAngularSpeed { get; set; } = 6f;
}

public static class MotionCleanup
{
    public static MotionDocument Apply(MotionDocument raw, CleanupSettings settings)
    {
        raw.Validate();var output=raw.Copy();
        var handCapture=HandCaptureRetargeter.Supports(raw);
        if (!float.IsFinite(settings.Root)||!float.IsFinite(settings.Arms)||!float.IsFinite(settings.Fingers)
            ||!float.IsFinite(settings.PreserveAngularSpeed)||settings.PreserveAngularSpeed<0
            ||settings.Root<0 || settings.Root>1 || settings.Arms<0 || settings.Arms>1 || settings.Fingers<0 || settings.Fingers>1
            || !double.IsFinite(settings.MaximumGapSeconds) || settings.MaximumGapSeconds<0
            || !double.IsFinite(settings.BridgeSeconds) || settings.BridgeSeconds<0)
            throw new ArgumentException("Invalid cleanup settings.");
        bool Protected(int i) => raw.Contacts.Any(c => c.Review != ContactReview.Disabled
            && raw.Frames[i].Time >= c.Start-.05 && raw.Frames[i].Time <= c.End+.05);
        // Fill bounded gaps only, preserving the fact that these values are inferred.
        for(var j=0;j<raw.Bones.Count;j++)
        {
            for(var i=1;i<raw.Frames.Count-1;i++)
            {
                if(raw.Frames[i].Evidence[j]!=JointEvidence.Unobserved)continue;
                var first=i;while(i<raw.Frames.Count && raw.Frames[i].Evidence[j]==JointEvidence.Unobserved)i++;
                if(i>=raw.Frames.Count || raw.Frames[first-1].Evidence[j]!=JointEvidence.Reconstructed
                    || raw.Frames[i].Evidence[j]!=JointEvidence.Reconstructed)continue;
                var a=raw.Frames[first-1];var b=raw.Frames[i];
                var bridged=b.Time-a.Time>settings.MaximumGapSeconds;
                if(bridged&&settings.BridgeSeconds<=0)continue;
                // A long loss holds the last pose, then glides into the reacquired one.
                var glideStart=bridged?Math.Max(a.Time,b.Time-settings.BridgeSeconds):a.Time;
                for(var k=first;k<i;k++)
                {
                    if(Protected(k))continue;
                    var t=(float)Math.Clamp((raw.Frames[k].Time-glideStart)/(b.Time-glideStart),0,1);
                    if(bridged)t=t*t*(3-2*t);
                    output.Frames[k].Positions[j]=MotionDocument.A(Vector3.Lerp(MotionDocument.V(a.Positions[j]),MotionDocument.V(b.Positions[j]),t));
                    output.Frames[k].Rotations[j]=MotionDocument.A(Quaternion.Slerp(MotionDocument.Q(a.Rotations[j]),MotionDocument.Q(b.Rotations[j]),t));
                    output.Frames[k].Evidence[j]=JointEvidence.InferredGap;
                    if(output.Frames[k].Confidence is { } confidence)confidence[j]=null;
                }
            }
        }
        // Neighbour-weighted offline filter. No causal delay; do not filter constraints or tracking loss.
        for(var i=1;i<raw.Frames.Count-1;i++)
        {
            if(Protected(i-1)||Protected(i)||Protected(i+1))continue;
            var a=raw.Frames[i-1];var b=raw.Frames[i];var c=raw.Frames[i+1];
            var t=(float)((b.Time-a.Time)/(c.Time-a.Time));
            for(var j=0;j<raw.Bones.Count;j++)
            {
                if(a.Evidence[j]!=JointEvidence.Reconstructed || b.Evidence[j]!=JointEvidence.Reconstructed || c.Evidence[j]!=JointEvidence.Reconstructed)continue;
                var q0=MotionDocument.Q(a.Rotations[j]);var q=MotionDocument.Q(b.Rotations[j]);var q1=MotionDocument.Q(c.Rotations[j]);
                var speed=Math.Max(Angle(q0,q)/(b.Time-a.Time),Angle(q,q1)/(c.Time-b.Time));
                if(speed>settings.PreserveAngularSpeed)continue;
                var amount=raw.Bones[j].Group=="fingers"?settings.Fingers:settings.Arms;
                output.Frames[i].Rotations[j]=MotionDocument.A(Quaternion.Slerp(q,Quaternion.Slerp(q0,q1,t),amount));
                if(raw.Bones[j].Parent<0&&!handCapture)
                {
                    var p0=MotionDocument.V(a.Positions[j]);var p=MotionDocument.V(b.Positions[j]);var p1=MotionDocument.V(c.Positions[j]);
                    // An isolated spike is flagged by metrics, not silently erased as noise.
                    if(Vector3.Distance(p0,p)/(b.Time-a.Time)<1 && Vector3.Distance(p,p1)/(c.Time-b.Time)<1)
                        output.Frames[i].Positions[j]=MotionDocument.A(Vector3.Lerp(p,Vector3.Lerp(p0,p1,t),settings.Root));
                }
            }
        }
        if(handCapture)WristTrajectoryCleanup.Apply(raw,output,settings.Root,Protected);
        for(var j=0;j<raw.Bones.Count;j++)
            for(var i=1;i<output.Frames.Count;i++)
                if(Quaternion.Dot(MotionDocument.Q(output.Frames[i-1].Rotations[j]),MotionDocument.Q(output.Frames[i].Rotations[j]))<0)
                    output.Frames[i].Rotations[j]=output.Frames[i].Rotations[j].Select(v=>-v).ToArray();
        output.Corrections.Add(new MotionCorrection { Type="Conservative symmetric cleanup",Start=raw.Frames[0].Time,End=raw.Frames[^1].Time,
            Settings=new() { ["root"]=settings.Root,["arms"]=settings.Arms,["fingers"]=settings.Fingers } });
        return output;
    }
    public static float Angle(Quaternion a,Quaternion b) => 2*MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a,b)),0,1));
}

/// <summary>Low-cost causal preview filter; not stacked with offline cleanup.</summary>
public sealed class OneEuroFilter
{
    bool initialized;double previousTime;float previousRaw,value,derivative;
    public float MinimumCutoff { get; set; } = 1f;
    public float Beta { get; set; } = .02f;
    public float DerivativeCutoff { get; set; } = 1f;
    public void Reset() => initialized=false;
    public float Step(float raw,double time)
    {
        if(!float.IsFinite(raw)||!double.IsFinite(time))throw new ArgumentException("Non-finite filter input.");
        if(!initialized){initialized=true;previousRaw=value=raw;previousTime=time;derivative=0;return raw;}
        var dt=time-previousTime;if(dt<=0)throw new ArgumentException("Filter timestamps must increase.");
        if(dt>.5){Reset();return Step(raw,time);}
        float Alpha(float cutoff)=>(float)(1/(1+1/(2*Math.PI*Math.Max(cutoff,.0001f)*dt)));
        var d=(raw-previousRaw)/(float)dt;
        derivative+=Alpha(DerivativeCutoff)*(d-derivative);
        value+=Alpha(MinimumCutoff+Beta*Math.Abs(derivative))*(raw-value);
        previousRaw=raw;previousTime=time;return value;
    }
}
