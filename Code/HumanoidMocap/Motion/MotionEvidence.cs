using System;

namespace HumanoidMocap.Motion;

/// <summary>Samples evidence on the same timestamp grid used for animation export.
/// These are provenance labels, not confidence or accuracy estimates.</summary>
public static class MotionEvidence
{
    public static JointEvidence At(MotionDocument motion,int bone,double time)
    {
        if(!double.IsFinite(time))throw new ArgumentOutOfRangeException(nameof(time));
        if(bone<0||bone>=motion.Bones.Count)throw new ArgumentOutOfRangeException(nameof(bone));
        var frames=motion.Frames;
        if(frames.Count==0)throw new ArgumentException("Motion has no samples.",nameof(motion));
        if(time<=frames[0].Time)return frames[0].Evidence[bone];
        if(time>=frames[^1].Time)return frames[^1].Evidence[bone];
        var lo=0;var hi=frames.Count-1;
        while(lo<hi){var mid=(lo+hi)/2;if(frames[mid].Time<time)lo=mid+1;else hi=mid;}
        var a=frames[lo-1];var b=frames[lo];
        return Blend(a.Evidence[bone],b.Evidence[bone],Fraction(time,a.Time,b.Time));
    }

    /// <summary>Media Foundation timestamps have 100 ns resolution. Treat only
    /// numerical neighbours of a sample as that sample; retain conservative evidence
    /// blending across genuine intervals. The tolerance is at most one microsecond
    /// and at most 0.01% of this interval.</summary>
    internal static float Fraction(double time,double start,double end)
    {
        if(end<=start)return 0;
        var tolerance=Math.Min(1e-6,(end-start)*1e-4);
        if(time-start<=tolerance)return 0;
        if(end-time<=tolerance)return 1;
        return (float)((time-start)/(end-start));
    }

    internal static JointEvidence Blend(JointEvidence a,JointEvidence b,float amount)
        =>amount<=0?a:amount>=1?b:
            a==JointEvidence.Unobserved||b==JointEvidence.Unobserved?JointEvidence.Unobserved:
            a==b?a:JointEvidence.InferredGap;
}
