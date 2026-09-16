using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Motion;
using Vector3=System.Numerics.Vector3;

public sealed record MissingObservationInterval(double Start,double End);
public sealed record JointTrackDiagnostics(string Bone,BoneRole? Role,int Reconstructed,int Inferred,int GeneratedIk,int Unobserved,
    double LongestMissingSeconds,IReadOnlyList<MissingObservationInterval> MissingIntervals);
public sealed record MotionDiagnosticsReport(int Frames,double Start,double End,double MinimumFrameInterval,double MaximumFrameInterval,
    float MaximumObservedBoneLengthRangeMetres,float MaximumObservedRotationStepDegrees,double MaximumObservedRootSpeedMetresPerSecond,
    IReadOnlyList<JointTrackDiagnostics> Tracks)
{
    public string Interpretation=>"Derived motion checks, not backend confidence or ground-truth accuracy. Large motion changes can be intentional.";
    public string HandSummary=>string.Join(" ",Tracks.Where(t=>t.Role is BoneRole.HandL or BoneRole.HandR)
        .Select(t=>t.GeneratedIk>0
            ?$"{(t.Role==BoneRole.HandL?"Left":"Right")} hand: reconstructed {t.Reconstructed}/{Frames}, generated IK {t.GeneratedIk}/{Frames}; longest unresolved gap {t.LongestMissingSeconds:F2}s."
            :$"{(t.Role==BoneRole.HandL?"Left":"Right")} hand: observed {t.Reconstructed}/{Frames} frames; longest unresolved gap {t.LongestMissingSeconds:F2}s."));
}

/// <summary>Measures available observations and transform consistency without assigning confidence.</summary>
public static class MotionDiagnostics
{
    public static MotionDiagnosticsReport Analyze(MotionDocument motion)
    {
        motion.Validate();var frames=motion.Frames;
        var tracks=new List<JointTrackDiagnostics>();float lengthRange=0,rotationStep=0;double rootSpeed=0;
        var intervals=frames.Zip(frames.Skip(1),(a,b)=>b.Time-a.Time).ToArray();
        for(var bone=0;bone<motion.Bones.Count;bone++)
        {
            int reconstructed=0,inferred=0,ik=0,unobserved=0;double? missingStart=null;
            var missing=new List<MissingObservationInterval>();
            float shortest=float.PositiveInfinity,longest=0;
            for(var i=0;i<frames.Count;i++)
            {
                var frame=frames[i];var evidence=frame.Evidence[bone];
                if(evidence==JointEvidence.Unobserved){unobserved++;missingStart??=frame.Time;}
                else
                {
                    if(missingStart is double start){missing.Add(new(start,frame.Time));missingStart=null;}
                    if(evidence==JointEvidence.Reconstructed)reconstructed++;
                    else if(evidence==JointEvidence.InferredGap)inferred++;
                    else if(evidence==JointEvidence.GeneratedIk)ik++;
                }
                if(evidence!=JointEvidence.Reconstructed)continue;
                if(motion.Bones[bone].Parent>=0)
                {
                    var length=MotionDocument.V(frame.Positions[bone]).Length();
                    shortest=Math.Min(shortest,length);longest=Math.Max(longest,length);
                }
                if(i==0||frames[i-1].Evidence[bone]!=JointEvidence.Reconstructed)continue;
                var previous=frames[i-1];
                var a=Quaternion.Normalize(MotionDocument.Q(previous.Rotations[bone]));var b=Quaternion.Normalize(MotionDocument.Q(frame.Rotations[bone]));
                rotationStep=Math.Max(rotationStep,2*MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a,b)),0,1))*180/MathF.PI);
                if(motion.Bones[bone].Parent<0)
                    rootSpeed=Math.Max(rootSpeed,Vector3.Distance(MotionDocument.V(frame.Positions[bone]),MotionDocument.V(previous.Positions[bone]))/(frame.Time-previous.Time));
            }
            if(missingStart is double finalStart)missing.Add(new(finalStart,frames[^1].Time+1/motion.SourceFps));
            if(float.IsFinite(shortest))lengthRange=Math.Max(lengthRange,longest-shortest);
            tracks.Add(new(motion.Bones[bone].Name,motion.Bones[bone].Role,reconstructed,inferred,ik,unobserved,
                missing.Count==0?0:missing.Max(m=>m.End-m.Start),missing));
        }
        return new(frames.Count,frames[0].Time,frames[^1].Time,intervals.Length==0?0:intervals.Min(),intervals.Length==0?0:intervals.Max(),
            lengthRange,rotationStep,rootSpeed,tracks);
    }
}
