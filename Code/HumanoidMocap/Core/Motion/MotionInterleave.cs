using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Joins two captures of the same video made from alternate frames (the body network works at 30 frames per second,
/// so 60 fps footage is captured as its even frames, and on request once more as its odd frames) into one capture of every
/// frame. The first capture's path is kept: the second's frames take its root interpolated at their time, since two separate
/// passes place the body slightly apart and alternating them would shake it. Each frame keeps its own pass's pose, and a light
/// three-frame smoothing removes what differs between the passes from frame to frame, well above any human movement.</summary>
public static class MotionInterleave
{
    public const string NotePrefix="Every frame captured";

    public static MotionDocument Merge(MotionDocument first,MotionDocument second)
    {
        if(first.Bones.Count!=second.Bones.Count||first.Bones.Where((b,i)=>b.Name!=second.Bones[i].Name).Any())
            throw new ArgumentException("The two passes do not share a skeleton.");
        var root=first.Bones.FindIndex(b=>b.Parent<0);
        var merged=first.Copy();
        var frames=new List<(MotionFrame Frame,bool Second)>();
        frames.AddRange(first.Frames.Select(f=>(f,false)));
        foreach(var f in second.Frames)
        {
            if(f.Time<=first.Frames[0].Time||f.Time>=first.Frames[^1].Time)continue;
            var copy=Clone(f);
            var (position,rotation)=RootAt(first,root,f.Time);
            copy.Positions[root]=MotionDocument.A(position);copy.Rotations[root]=MotionDocument.A(rotation);
            frames.Add((copy,true));
        }
        frames.Sort((a,b)=>a.Frame.Time.CompareTo(b.Frame.Time));
        merged.Frames=Smooth(frames.Select(f=>f.Frame).ToList(),root);
        merged.SourceFps=first.SourceFps*2;
        // Per-frame tracks follow the frames: each of the second pass's samples taken at its own time.
        var times=merged.Frames.Select(f=>f.Time).ToArray();
        merged.StationaryJoints=first.StationaryJoints.Select(track=>new StationaryJointTrack{Bone=track.Bone,Source=track.Source,
            Probability=times.Select(t=>Sample(first,track.Probability,t)).ToArray()}).ToList();
        // Camera samples (time-stamped) stay the first pass's.
        merged.Diagnostics.Add(FormattableString.Invariant($"{NotePrefix}: the clip was captured twice, from its even and its odd frames, and the two joined into {merged.Frames.Count} samples at {merged.SourceFps:0} per second, for fast movement."));
        return merged;
    }

    static MotionFrame Clone(MotionFrame f)=>new(){Time=f.Time,Positions=f.Positions.Select(p=>p.ToArray()).ToArray(),
        Rotations=f.Rotations.Select(r=>r.ToArray()).ToArray(),Evidence=f.Evidence.ToArray(),Confidence=f.Confidence?.ToArray()};

    static (Vector3,Quaternion) RootAt(MotionDocument doc,int root,double time)
    {
        var frames=doc.Frames;var hi=frames.FindIndex(f=>f.Time>=time);if(hi<=0)hi=1;var lo=hi-1;
        var a=frames[lo];var b=frames[hi];var t=(float)Math.Clamp((time-a.Time)/Math.Max(1e-9,b.Time-a.Time),0,1);
        return(Vector3.Lerp(MotionDocument.V(a.Positions[root]),MotionDocument.V(b.Positions[root]),t),
            Quaternion.Normalize(Quaternion.Slerp(MotionDocument.Q(a.Rotations[root]),MotionDocument.Q(b.Rotations[root]),t)));
    }

    static float Sample(MotionDocument doc,float[] values,double time)
    {
        var frames=doc.Frames;var hi=frames.FindIndex(f=>f.Time>=time);if(hi<0)return values[^1];if(hi==0)return values[0];
        var t=(time-frames[hi-1].Time)/Math.Max(1e-9,frames[hi].Time-frames[hi-1].Time);
        return (float)(values[hi-1]+(values[hi]-values[hi-1])*t);
    }

    /// <summary>[1 2 1]/4 across neighbouring samples on every joint rotation and the root position.</summary>
    static List<MotionFrame> Smooth(List<MotionFrame> frames,int root)
    {
        var result=frames.Select(Clone).ToList();
        for(var i=1;i+1<frames.Count;i++)
            for(var j=0;j<frames[i].Rotations.Length;j++)
            {
                var q=MotionDocument.Q(frames[i].Rotations[j]);var sum=q*2;
                foreach(var k in new[]{i-1,i+1}){var n=MotionDocument.Q(frames[k].Rotations[j]);if(Quaternion.Dot(n,q)<0)n=Quaternion.Negate(n);sum+=n;}
                result[i].Rotations[j]=MotionDocument.A(Quaternion.Normalize(sum));
                if(j==root)result[i].Positions[j]=MotionDocument.A((MotionDocument.V(frames[i-1].Positions[j])+2*MotionDocument.V(frames[i].Positions[j])+MotionDocument.V(frames[i+1].Positions[j]))/4);
            }
        return result;
    }
}
