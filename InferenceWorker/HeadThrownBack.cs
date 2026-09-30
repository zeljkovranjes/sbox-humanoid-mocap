using System.Numerics;
using System.Text.Json;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

/// <summary>Puts back a head thrown back that the body network bowed forward instead. Seen from the front, a head snapped back
/// shows the throat and a head bowed shows the top of the head: either way the face is gone, and the network picked the bow
/// (tiki.mov: tilted back 20-30 degrees just before and after, bent 14-21 degrees forward in between; sped up, it left the head
/// roughly upright instead). Where the face is hidden (nose and eyes barely seen, ears seen) while the body faces the camera and the
/// head was tilted back on both sides of that stretch, the head is thrown back to <see cref="HiddenFaceDegrees"/>, neck and head
/// sharing the turn, eased in over <see cref="EaseFrames"/> frames. A face gone from a camera it faces means the head is right
/// back: 60 degrees looked too little against tiki (the user preferred the first, fully thrown-back version at 135-140).</summary>
public static class HeadThrownBack
{
    public const string File="head-thrown-back.json";
    public const float HiddenFaceDegrees=130,FaceScore=.6f,EyeScore=.65f,EarScore=.7f,BackBeforeDegrees=10,NeckShare=.4f;
    public const int EaseFrames=3,Around=3,Stretch=6;
    /// <summary>Only for a performer at least this tall in the picture (a vanishing face means little on a small, blurred one), with the
    /// torso within <see cref="UprightDegrees"/> of upright (not mid-tumble: a kip-up read as a snapped-back head).</summary>
    public const float BodyPixels=200,UprightDegrees=35;
    /// <summary>A head leaning back less than this (degrees) next to a hidden-face stretch is still the network's bow.</summary>
    public const float StillBowedDegrees=5;
    /// <summary>The corrected local rotations of the neck and head in one frame.</summary>
    public sealed record Correction(int Frame,float[] Neck,float[] Head);

    /// <returns>The corrections made, already applied to <paramref name="motion"/>.</returns>
    public static List<Correction> Apply(MotionDocument motion,IReadOnlyList<float[]?> observations)
    {
        var result=new List<Correction>();var bones=motion.Bones;var count=motion.Frames.Count;
        int Role(BoneRole r)=>bones.FindIndex(b=>b.Role==r);
        int chest=Role(BoneRole.Spine2),neck=Role(BoneRole.Neck),head=Role(BoneRole.Head);
        if(chest<0||neck<0||head<0||bones[head].Parent!=neck||observations.Count!=count||count<8)return result;
        var widths=observations.Where(o=>o is {Length:>=51}&&o[5*3+2]>=.5f&&o[6*3+2]>=.5f).Select(o=>MathF.Abs(o![5*3]-o[6*3])).OrderBy(w=>w).ToArray();
        if(widths.Length==0)return result;var usual=widths[widths.Length/2];
        var heights=observations.Where(o=>o is {Length:>=51}&&o[2]>.5f&&(o[15*3+2]>.5f||o[16*3+2]>.5f))
            .Select(o=>Math.Max(o![15*3+2]>.5f?o[15*3+1]:0,o[16*3+2]>.5f?o[16*3+1]:0)-o[1]).Where(h=>h>0).OrderBy(h=>h).ToArray();
        if(heights.Length==0||heights[heights.Length/2]<BodyPixels)return result;
        bool Hidden(float[]? o)=>o is {Length:>=51}&&o[2]<FaceScore&&o[1*3+2]<EyeScore&&o[2*3+2]<EyeScore&&Math.Max(o[3*3+2],o[4*3+2])>=EarScore
            // Facing the camera: the performer's left shoulder on the right of the picture, at close to its usual width.
            &&o[5*3+2]>=.5f&&o[6*3+2]>=.5f&&o[5*3]-o[6*3]>=.6f*usual;
        var hidden=observations.Select(Hidden).ToArray();
        for(var t=1;t+1<count;t++)if(!hidden[t]&&hidden[t-1]&&hidden[t+1])hidden[t]=true;
        var pitch=Enumerable.Range(0,count).Select(t=>Pitch(motion,t,chest,head)).ToArray();
        var handled=-1;
        for(var t=0;t<count;)
        {
            if(!hidden[t]){t++;continue;}
            var end=t;while(end+1<count&&hidden[end+1])end++;
            var next=end+1;
            // The network's bow can outlast the hidden face by a few frames: those belong to the same stretch.
            var start=t;
            for(var k=0;k<Stretch&&start-1>=0&&pitch[start-1]<StillBowedDegrees;k++)start--;
            for(var k=0;k<Stretch&&end+1<count&&pitch[end+1]<StillBowedDegrees;k++)end++;
            t=start;
            // Two hidden stretches can grow over the same bowed frames: each frame is turned once.
            if(start<=handled){t=Math.Max(next,end+1);continue;}
            var before=Enumerable.Range(t-Around,Around).Where(k=>k>=0).Select(k=>pitch[k]).DefaultIfEmpty(float.NaN).Average();
            var after=Enumerable.Range(end+1,Around).Where(k=>k<count).Select(k=>pitch[k]).DefaultIfEmpty(float.NaN).Average();
            var inside=Enumerable.Range(t,end-t+1).Average(k=>pitch[k]);
            var upright=Enumerable.Range(t,end-t+1).All(k=>Upright(motion,k,chest)<=UprightDegrees);
            // Tilting back on both sides of a face that vanishes means thrown back, whatever the network made of it in between (bowed
            // forward on tiki; left roughly upright on the same clip sped up). Looking down tilts forward on both sides instead.
            if(upright&&before>=BackBeforeDegrees&&after>=BackBeforeDegrees&&inside<HiddenFaceDegrees)
            {
                var goal=Math.Max(HiddenFaceDegrees,Math.Max(before,after));
                for(var k=Math.Max(0,t-EaseFrames);k<=Math.Min(count-1,end+EaseFrames);k++)
                {
                    var outside=k<t?t-k:k>end?k-end:0;
                    var w=1f-outside/(EaseFrames+1f);w=w*w*(3-2*w);
                    // Measured again now, so a frame an earlier stretch's easing already turned is not turned twice.
                    var turn=(goal-Pitch(motion,k,chest,head))*w;if(turn<=0)continue;
                    result.Add(Tilt(motion,k,chest,neck,head,turn));
                }
            }
            handled=Math.Max(handled,end+EaseFrames);
            t=Math.Max(next,end+1);
        }
        return result;
    }

    /// <summary>How far the chest leans from upright, degrees (camera-relative documents are upright in +Y, near enough for this).</summary>
    static float Upright(MotionDocument motion,int t,int chest)
    {
        var (_,q)=World(motion,t);var up=Vector3.Transform(Vector3.UnitY,q[chest]);
        return MathF.Acos(Math.Clamp(up.Y,-1f,1f))*180/MathF.PI;
    }

    /// <summary>How far the head leans back from the chest, degrees (negative: bowed forward), in the chest's own frame.</summary>
    static float Pitch(MotionDocument motion,int t,int chest,int head)
    {
        var (_,q)=World(motion,t);
        var up=Vector3.Transform(Vector3.UnitY,q[head]);var chestUp=Vector3.Transform(Vector3.UnitY,q[chest]);var chestForward=Vector3.Transform(Vector3.UnitZ,q[chest]);
        return MathF.Atan2(-Vector3.Dot(up,chestForward),Vector3.Dot(up,chestUp))*180/MathF.PI;
    }

    /// <summary>Tilts the head back by <paramref name="degrees"/> about the chest's left-right axis, the neck taking <see cref="NeckShare"/>.</summary>
    static Correction Tilt(MotionDocument motion,int t,int chest,int neck,int head,float degrees)
    {
        var (_,q)=World(motion,t);
        // The chest's +X is the performer's left; turning about it by a negative angle takes up toward the back.
        var axis=Vector3.Normalize(Vector3.Transform(Vector3.UnitX,q[chest]));var radians=-degrees*MathF.PI/180;
        var neckWorld=Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis,radians*NeckShare)*q[neck]);
        var headWorld=Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis,radians)*q[head]);
        var parent=motion.Bones[neck].Parent;var parentWorld=parent<0?Quaternion.Identity:q[parent];
        var neckLocal=Quaternion.Normalize(Quaternion.Inverse(parentWorld)*neckWorld);var headLocal=Quaternion.Normalize(Quaternion.Inverse(neckWorld)*headWorld);
        var correction=new Correction(t,new[]{neckLocal.X,neckLocal.Y,neckLocal.Z,neckLocal.W},new[]{headLocal.X,headLocal.Y,headLocal.Z,headLocal.W});
        Replay(motion,new[]{correction});return correction;
    }

    /// <summary>Applies saved corrections to another document on the same skeleton (the refinement rebuilds the body from the network).</summary>
    public static int Replay(MotionDocument motion,IEnumerable<Correction> corrections)
    {
        int neck=motion.Bones.FindIndex(b=>b.Role==BoneRole.Neck),head=motion.Bones.FindIndex(b=>b.Role==BoneRole.Head);var applied=0;
        if(neck<0||head<0)return 0;
        foreach(var c in corrections)
        {
            if(c.Frame<0||c.Frame>=motion.Frames.Count)continue;
            motion.Frames[c.Frame].Rotations[neck]=c.Neck.ToArray();motion.Frames[c.Frame].Rotations[head]=c.Head.ToArray();applied++;
        }
        return applied;
    }
    public static void Save(string folder,List<Correction> corrections)
    {
        var path=Path.Combine(folder,File);
        if(corrections.Count==0){System.IO.File.Delete(path);return;}
        System.IO.File.WriteAllText(path,JsonSerializer.Serialize(corrections));
    }
    public static List<Correction> Load(string folder)
    {
        var path=Path.Combine(folder,File);
        return System.IO.File.Exists(path)?JsonSerializer.Deserialize<List<Correction>>(System.IO.File.ReadAllText(path))??new():new();
    }
    static (Vector3[] Positions,Quaternion[] Rotations) World(MotionDocument motion,int t)
    {
        var bones=motion.Bones;var frame=motion.Frames[t];var p=new Vector3[bones.Count];var q=new Quaternion[bones.Count];
        for(var i=0;i<bones.Count;i++)
        {
            var parent=bones[i].Parent;var local=Quaternion.Normalize(MotionDocument.Q(frame.Rotations[i]));var offset=MotionDocument.V(frame.Positions[i]);
            if(parent<0){p[i]=offset;q[i]=local;}else{p[i]=p[parent]+Vector3.Transform(offset,q[parent]);q[i]=Quaternion.Normalize(q[parent]*local);}
        }
        return (p,q);
    }
}
