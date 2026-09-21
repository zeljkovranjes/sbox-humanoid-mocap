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
    public sealed record Result(int ConstrainedSamples,float MaximumReachResidual,float MaximumRootShift,float MaximumFloorDrift=0);
    readonly record struct Anchor(Vector3 Position,float Weight);
    sealed record Leg(int Hip,int Knee,int Ankle,int Toe,Anchor[] AnkleAnchors,Anchor[] ToeAnchors);
    readonly record struct Goal(Leg Leg,Vector3 Position,Quaternion Rotation,float Weight);

    public static Result Apply(List<XForm[]> frames,SourceScene source,MappingResult mapping,TargetRig target,TargetUpAxis axis)
    {
        if(source.CaptureSpace!=MotionSpace.WorldRelative||source.CaptureStationaryJoints is not {Count:>0} probabilities||frames.Count==0)
            return new(0,0,0);
        var scale=axis==TargetUpAxis.ZUpEngine?39.3700787f:100f;var rig=target.Skeleton;var fps=source.Clips[0].Fps;
        var up=axis==TargetUpAxis.YUpCm?Vector3.UnitY:Vector3.UnitZ;
        var trajectories=new Dictionary<int,Vector3[]>();
        foreach(var role in new[]{BoneRole.FootL,BoneRole.FootR,BoneRole.ToeL,BoneRole.ToeR})
            if(target.BoneForRole(role) is int bone)trajectories.TryAdd(bone,new Vector3[frames.Count]);
        var initial=new XForm[rig.Count];
        for(var f=0;f<frames.Count;f++)
        {FkUtil.ToWorld(frames[f],rig,initial);foreach(var track in trajectories)track.Value[f]=initial[track.Key].Pos;}
        var floorDrift=RemoveFloorDrift(frames,source,mapping,target,trajectories,probabilities,fps,scale,up);
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
                return BuildAnchors(trajectories[targetBone],p,fps,scale,up,Vector3.Dot(rig.RestWorld[targetBone].Pos,up));
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
        return new(samples,residual,rootShift,floorDrift);
    }

    /// <summary>Reconstructed world height wanders as a subject moves in depth: on the kata
    /// sample planted ankles sat anywhere from 6 to 20 cm above one another. With a still
    /// camera the floor is level, so each predicted contact's height above the target's
    /// standing rest height is a measurement of that drift. It is interpolated between
    /// contacts and removed from the root, before any foot is anchored. Contacts further
    /// than 30 cm from the floor are treated as real elevation and ignored. Returns the
    /// largest correction in target units.</summary>
    static float RemoveFloorDrift(List<XForm[]> frames,SourceScene source,MappingResult mapping,TargetRig target,Dictionary<int,Vector3[]> trajectories,
        IReadOnlyDictionary<string,float[]> probabilities,float fps,float scale,Vector3 up)
    {
        var rig=target.Skeleton;var lowest=Enumerable.Repeat(float.PositiveInfinity,frames.Count).ToArray();
        // No foot joint may end below its floor height, in or out of contact.
        var clearance=Enumerable.Repeat(float.PositiveInfinity,frames.Count).ToArray();
        foreach(var role in new[]{BoneRole.FootL,BoneRole.FootR,BoneRole.ToeL,BoneRole.ToeR})
        {
            if(target.BoneForRole(role) is not int bone||!mapping.RoleToBone.TryGetValue(role,out var sourceBone)||
                !probabilities.TryGetValue(source.Skeleton[sourceBone].Name,out var p)||p.Length!=frames.Count)continue;
            var floor=Vector3.Dot(rig.RestWorld[bone].Pos,up);var positions=trajectories[bone];
            for(var i=0;i<frames.Count;i++)clearance[i]=Math.Min(clearance[i],Vector3.Dot(positions[i],up)-floor);
            foreach(var interval in Intervals(positions,p,fps,scale))
            {
                var heights=Enumerable.Range(interval.Start,interval.End-interval.Start+1).Select(i=>Vector3.Dot(positions[i],up)).OrderBy(h=>h).ToArray();
                var error=heights[heights.Length/2]-floor;
                if(MathF.Abs(error)>.3f*scale)continue;
                // A planted joint does not really change height, so its height at each frame of the
                // contact measures the drift at that frame. A planted ball of the foot leaves the
                // ankle high; the lowest contact is the floor evidence.
                for(var i=interval.Start;i<=interval.End;i++)lowest[i]=Math.Min(lowest[i],Vector3.Dot(positions[i],up)-floor);
            }
        }
        var known=Enumerable.Range(0,frames.Count).Where(i=>float.IsFinite(lowest[i])).ToArray();
        if(known.Length==0)return 0;
        var drift=new float[frames.Count];var next=0;
        for(var i=0;i<frames.Count;i++)
        {
            while(next<known.Length&&known[next]<i)next++;
            float Value(int k)=>lowest[known[k]];
            if(next==0)drift[i]=Value(0);
            else if(next==known.Length)drift[i]=Value(known.Length-1);
            else if(known[next]==i)drift[i]=Value(next);
            else{var a=known[next-1];var b=known[next];drift[i]=Value(next-1)+(Value(next)-Value(next-1))*(i-a)/(float)(b-a);}
        }
        // Contact boundaries step between two feet's estimates; a quarter-second average
        // keeps the correction slower than any real vertical movement it must not remove.
        var radius=Math.Max(1,(int)Math.Round(fps*.125));var smooth=new float[frames.Count];
        for(var i=0;i<frames.Count;i++)
        {
            float total=0;var count=0;
            for(var j=Math.Max(0,i-radius);j<=Math.Min(frames.Count-1,i+radius);j++){total+=drift[j];count++;}
            smooth[i]=Math.Min(total/count,clearance[i]);
        }
        var largest=0f;
        for(var f=0;f<frames.Count;f++)
        {
            var shift=up*smooth[f];largest=Math.Max(largest,MathF.Abs(smooth[f]));
            for(var b=0;b<rig.Count;b++)if(rig[b].ParentIndex<0)frames[f][b].Pos-=shift;
            foreach(var track in trajectories.Values)track[f]-=shift;
        }
        return largest;
    }
    static List<(int Start,int End)> Intervals(Vector3[] positions,float[] probability,float fps,float scale)
    {
        var intervals=new List<(int Start,int End)>();int start=-1;var minimum=Math.Max(3,(int)Math.Ceiling(fps*.1));
        for(var i=0;i<positions.Length;i++)
        {
            var speed=i==0?0:Vector3.Distance(positions[i],positions[i-1])*fps/scale;
            if(start<0&&probability[i]>=.8f&&speed<1)start=i;
            else if(start>=0&&(probability[i]<.6f||speed>=1))
            {if(i-start>=minimum)intervals.Add((start,i-1));start=-1;}
        }
        if(start>=0&&positions.Length-start>=minimum)intervals.Add((start,positions.Length-1));
        return intervals;
    }

    static Anchor[] BuildAnchors(Vector3[] positions,float[] probability,float fps,float scale,Vector3 up,float floorHeight)
    {
        var anchors=new Anchor[positions.Length];var blend=Math.Max(1,(int)Math.Round(fps*.08));
        foreach(var interval in Intervals(positions,probability,fps,scale))
        {
            var points=positions.Skip(interval.Start).Take(interval.End-interval.Start+1).ToArray();
            float Median(Func<Vector3,float> component){var values=points.Select(component).OrderBy(v=>v).ToArray();return values[values.Length/2];}
            var anchor=new Vector3(Median(p=>p.X),Median(p=>p.Y),Median(p=>p.Z));
            // A planted joint rests at the height it has in the target's standing rest pose.
            // After floor-drift removal the interval median is still a few centimetres off
            // whenever height wavers, which left planted feet hovering on the kata sample.
            // Contacts more than 8 cm from the floor (a raised heel, a step) keep their height.
            var height=Vector3.Dot(anchor,up);
            if(MathF.Abs(height-floorHeight)<=.08f*scale)anchor+=up*(floorHeight-height);
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
