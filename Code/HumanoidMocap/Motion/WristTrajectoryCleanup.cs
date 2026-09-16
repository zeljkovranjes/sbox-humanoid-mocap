using System;
using System.Numerics;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Symmetric local linear position fitting for observed wrists. Constant velocity
/// and sample timing are preserved. This reduces small tracking wander; it cannot recover
/// camera motion or distinguish sustained monocular depth error from actual translation.</summary>
public static class WristTrajectoryCleanup
{
    public static void Apply(MotionDocument raw,MotionDocument output,float amount,Func<int,bool> protectedFrame)
    {
        if(!float.IsFinite(amount)||amount<0||amount>1)throw new ArgumentOutOfRangeException(nameof(amount));
        if(amount<=0)return;
        for(var bone=0;bone<raw.Bones.Count;bone++)
        {
            if(raw.Bones[bone].Parent>=0)continue;
            for(var i=1;i<raw.Frames.Count-1;i++)
            {
                var center=raw.Frames[i];bool usable=true;
                // Sparse detectors often provide only short valid runs. Use three
                // symmetric observations there, never crossing a loss boundary.
                var radius=i>=2&&i+2<raw.Frames.Count&&raw.Frames[i-2].Evidence[bone]==JointEvidence.Reconstructed
                    &&raw.Frames[i+2].Evidence[bone]==JointEvidence.Reconstructed?2:1;
                for(var k=i-radius;k<=i+radius;k++)
                {
                    if(protectedFrame(k)||raw.Frames[k].Evidence[bone]!=JointEvidence.Reconstructed){usable=false;break;}
                    if(k>i-radius)
                    {
                        var dt=raw.Frames[k].Time-raw.Frames[k-1].Time;
                        if(dt>.1||Vector3.Distance(MotionDocument.V(raw.Frames[k].Positions[bone]),MotionDocument.V(raw.Frames[k-1].Positions[bone]))/dt>=1)
                        {usable=false;break;}
                    }
                }
                if(!usable)continue;
                var position=MotionDocument.V(center.Positions[bone]);
                var before=MotionDocument.V(raw.Frames[i-1].Positions[bone]);var after=MotionDocument.V(raw.Frames[i+1].Positions[bone]);
                // Preserve pronounced reversals (recoil/punch peaks). Tiny sub-8mm
                // excursions may be cleaned; that threshold is a heuristic, not confidence.
                if(Vector3.Dot(position-before,after-position)<0&&Math.Min(Vector3.Distance(position,before),Vector3.Distance(position,after))>.008f)continue;
                double weightSum=0,timeSum=0,timeSquared=0;var positionSum=Vector3.Zero;var timePosition=Vector3.Zero;
                for(var k=i-radius;k<=i+radius;k++)
                {
                    var weight=radius+1-Math.Abs(k-i);var dt=raw.Frames[k].Time-center.Time;var p=MotionDocument.V(raw.Frames[k].Positions[bone]);
                    weightSum+=weight;timeSum+=weight*dt;timeSquared+=weight*dt*dt;
                    positionSum+=p*weight;timePosition+=p*(float)(weight*dt);
                }
                var denominator=timeSquared-timeSum*timeSum/weightSum;
                if(denominator<1e-12)continue;
                var velocity=(timePosition-positionSum*(float)(timeSum/weightSum))/(float)denominator;
                var estimate=positionSum/(float)weightSum-velocity*(float)(timeSum/weightSum);
                // Bound free-hand edits independently of the amount: an ambiguous
                // large displacement needs review rather than aggressive filtering.
                var correction=(estimate-position)*amount;
                if(correction.Length()>.003f)correction=Vector3.Normalize(correction)*.003f;
                output.Frames[i].Positions[bone]=MotionDocument.A(position+correction);
            }
        }
    }
}
