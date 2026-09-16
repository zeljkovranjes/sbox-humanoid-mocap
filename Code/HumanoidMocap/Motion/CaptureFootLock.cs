using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;
using HumanoidMocap.Solve;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Final target-space ankle/toe constraints from backend stationary predictions.
/// Applied after proportion/height correction, before in-place conversion. Does not infer
/// a stationary camera or relabel contact predictions as observations.</summary>
public static class CaptureFootLock
{
    public sealed record Result(int ConstrainedSamples,float MaximumReachResidual,float MaximumRootShift);
    readonly record struct Anchor(Vector3 Position,float Weight);
    sealed record Leg(int Hip,int Knee,int Ankle,int Toe,Anchor[] AnkleAnchors,Anchor[] ToeAnchors);
    readonly record struct Goal(Leg Leg,Vector3 Position,Quaternion Rotation,float Weight);

    public static Result Apply(List<XForm[]> frames,SourceScene source,MappingResult mapping,TargetRig target,TargetUpAxis axis)
    {
        if(source.CaptureSpace!=MotionSpace.WorldRelative||source.CaptureStationaryJoints is not {Count:>0} probabilities||frames.Count==0)
            return new(0,0,0);
        var scale=axis==TargetUpAxis.ZUpEngine?39.3700787f:100f;var rig=target.Skeleton;var fps=source.Clips[0].Fps;
        var trajectories=new Dictionary<int,Vector3[]>();
        foreach(var role in new[]{BoneRole.FootL,BoneRole.FootR,BoneRole.ToeL,BoneRole.ToeR})
            if(target.BoneForRole(role) is int bone)trajectories.TryAdd(bone,new Vector3[frames.Count]);
        var initial=new XForm[rig.Count];
        for(var f=0;f<frames.Count;f++)
        {FkUtil.ToWorld(frames[f],rig,initial);foreach(var track in trajectories)track.Value[f]=initial[track.Key].Pos;}
        var legs=new List<Leg>();
        foreach(var left in new[]{true,false})
        {
            var roles=left?new[]{BoneRole.UpperLegL,BoneRole.LowerLegL,BoneRole.FootL,BoneRole.ToeL}:
                new[]{BoneRole.UpperLegR,BoneRole.LowerLegR,BoneRole.FootR,BoneRole.ToeR};
            var indices=roles.Select(target.BoneForRole).ToArray();if(indices.Any(i=>i is null))continue;
            Anchor[] Anchors(BoneRole role,int targetBone)
            {
                if(!mapping.RoleToBone.TryGetValue(role,out var sourceBone)||!probabilities.TryGetValue(source.Skeleton[sourceBone].Name,out var p))return new Anchor[frames.Count];
                if(p.Length!=frames.Count)throw new ArgumentException("Stationary predictions do not match the target sample grid.");
                return BuildAnchors(trajectories[targetBone],p,fps,scale);
            }
            legs.Add(new(indices[0]!.Value,indices[1]!.Value,indices[2]!.Value,indices[3]!.Value,
                Anchors(roles[2],indices[2]!.Value),Anchors(roles[3],indices[3]!.Value)));
        }
        int samples=0;float residual=0,rootShift=0;var world=new XForm[rig.Count];
        for(var f=0;f<frames.Count;f++)
        {
            var frame=frames[f];FkUtil.ToWorld(frame,rig,world);var goals=new List<Goal>();
            foreach(var leg in legs)
            {
                var ankle=leg.AnkleAnchors[f];var toe=leg.ToeAnchors[f];var weight=Math.Max(ankle.Weight,toe.Weight);
                if(weight<=0)continue;
                var a=world[leg.Ankle];var toeOffset=world[leg.Toe].Pos-a.Pos;var rotation=a.Rot;
                if(ankle.Weight>0&&toe.Weight>0)
                {
                    var direction=toe.Position-ankle.Position;
                    if(direction.LengthSquared()>1e-8f&&toeOffset.LengthSquared()>1e-8f)
                    {
                        var swing=Quaternion.Slerp(Quaternion.Identity,MathQ.FromTo(toeOffset,direction),Math.Min(ankle.Weight,toe.Weight));
                        rotation=Quaternion.Normalize(swing*rotation);toeOffset=Vector3.Transform(toeOffset,swing);
                    }
                }
                // Toe-only contact permits heel lift instead of flattening the foot.
                var targetPosition=(ankle.Position*ankle.Weight+(toe.Position-toeOffset)*toe.Weight)/(ankle.Weight+toe.Weight);
                goals.Add(new(leg,Vector3.Lerp(a.Pos,targetPosition,weight),rotation,weight));
            }
            if(goals.Count==0)continue;
            // Minimal shared root displacement when target proportions cannot reach
            // both anchors. Bounded to 3 cm; never stretch a bone to match a prediction.
            var shift=Vector3.Zero;
            for(var iteration=0;iteration<6;iteration++)foreach(var goal in goals)
            {
                var leg=goal.Leg;var hip=world[leg.Hip].Pos;
                var reach=Vector3.Distance(hip,world[leg.Knee].Pos)+Vector3.Distance(world[leg.Knee].Pos,world[leg.Ankle].Pos);
                var delta=goal.Position-hip-shift;var distance=delta.Length();
                if(distance>reach*.9999f&&distance>1e-6f)shift+=delta*((distance-reach*.9999f)/distance)*goal.Weight;
                if(shift.Length()>.03f*scale)shift=Vector3.Normalize(shift)*(.03f*scale);
            }
            rootShift=Math.Max(rootShift,shift.Length());
            if(shift.LengthSquared()>0)for(var b=0;b<rig.Count;b++)if(rig[b].ParentIndex<0)frame[b].Pos+=shift;
            foreach(var goal in goals)
            {
                var leg=goal.Leg;FkUtil.ToWorld(frame,rig,world);
                var upper=world[leg.Hip];var knee=world[leg.Knee];var ankle=world[leg.Ankle];
                var bend=Vector3.Cross(knee.Pos-upper.Pos,ankle.Pos-knee.Pos);
                if(bend.LengthSquared()<1e-8f)bend=Vector3.Transform(Vector3.UnitX,upper.Rot);
                var ik=TwoBoneIk.Solve(upper.Pos,knee.Pos,ankle.Pos,goal.Position,soften:0,stableBendAxis:bend);
                EffectorIk.ApplyWorldDeltas(frame,rig,leg.Hip,leg.Knee,leg.Ankle,ik.UpperWorldDelta,ik.LowerWorldDelta,world);
                FkUtil.ToWorld(frame,rig,world);
                var parent=rig[leg.Ankle].ParentIndex;
                frame[leg.Ankle].Rot=Quaternion.Normalize((parent<0?Quaternion.Identity:Quaternion.Inverse(world[parent].Rot))*goal.Rotation);
                samples++;residual=Math.Max(residual,Vector3.Distance(world[leg.Ankle].Pos,goal.Position));
            }
        }
        return new(samples,residual,rootShift);
    }

    static Anchor[] BuildAnchors(Vector3[] positions,float[] probability,float fps,float scale)
    {
        var anchors=new Anchor[positions.Length];var intervals=new List<(int Start,int End)>();int start=-1;
        var minimum=Math.Max(3,(int)Math.Ceiling(fps*.1));var blend=Math.Max(1,(int)Math.Round(fps*.08));
        for(var i=0;i<positions.Length;i++)
        {
            var speed=i==0?0:Vector3.Distance(positions[i],positions[i-1])*fps/scale;
            if(start<0&&probability[i]>=.8f&&speed<1)start=i;
            else if(start>=0&&(probability[i]<.6f||speed>=1))
            {if(i-start>=minimum)intervals.Add((start,i-1));start=-1;}
        }
        if(start>=0&&positions.Length-start>=minimum)intervals.Add((start,positions.Length-1));
        foreach(var interval in intervals)
        {
            var points=positions.Skip(interval.Start).Take(interval.End-interval.Start+1).ToArray();
            float Median(Func<Vector3,float> component){var values=points.Select(component).OrderBy(v=>v).ToArray();return values[values.Length/2];}
            var anchor=new Vector3(Median(p=>p.X),Median(p=>p.Y),Median(p=>p.Z));
            // Reject large excursions even when predicted static; protect fast steps
            // and obvious false-positive intervals rather than flattening them.
            if(points.Any(p=>Vector3.Distance(p,anchor)>.12f*scale))continue;
            for(var i=Math.Max(0,interval.Start-blend);i<=Math.Min(positions.Length-1,interval.End+blend);i++)
            {
                float weight=i<interval.Start?1-(interval.Start-i)/(float)(blend+1):i>interval.End?1-(i-interval.End)/(float)(blend+1):1;
                weight=weight*weight*(3-2*weight);
                if(weight>anchors[i].Weight)anchors[i]=new(anchor,weight);
            }
        }
        return anchors;
    }
}
