using System.Numerics;
using System.Text.Json;
using HumanoidMocap.Core.Inference;
using HumanoidMocap.EditorTools.Inference;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Motion;

namespace HumanoidMocap.Worker;

/// <summary>Uses a recording's own depth (<see cref="DepthTrack"/>) where the body network can only guess distance.
/// Read at the 2D joints the pose network found, it gives the torso's and each hand's distance from the camera:
///  - full (sensor) depth moves the whole body along its line of sight to the measured torso distance, frame by frame
///    and smoothed, and gives each clearly seen hand its measured distance;
///  - partial (Cinematic mode) depth is relative, so only says how far a hand is in front of or behind the torso; that
///    difference is applied to the network's torso distance, only where it disagrees by more than
///    <see cref="PartialMinimumMetres"/>, and half way.
/// A joint the depth does not see (no reading, or a reading far from the body: the wall behind a hand that left the
/// sensor's view) keeps the network's estimate; so do frames without depth. Works on the camera-relative document.</summary>
public static class DepthCorrection
{
    public const string RootFile="depth-root.json";
    /// <summary>Joint centres sit behind the skin the sensor sees: the torso about this far, the wrist less.</summary>
    const float TorsoBehindSkin=.1f,WristBehindSkin=.03f;
    /// <summary>A hand reading further than this from the torso's is the background, not the hand.</summary>
    const float HandFromTorso=.9f;
    public const float PartialMinimumMetres=.1f;
    const float MinimumScore=.5f,HandScore=.6f;

    public sealed record Result(float[] RootScale,float?[][] WristDepth,string Note);

    public static Result? Measure(MotionDocument motion,IReadOnlyList<float[]?> observations,DepthTrack depth,float fps)
    {
        var count=motion.Frames.Count;if(count==0||observations.Count!=count)return null;
        var bones=motion.Bones;int Role(BoneRole role)=>bones.FindIndex(b=>b.Role==role);
        int[] torsoBones={Role(BoneRole.UpperArmL),Role(BoneRole.UpperArmR),Role(BoneRole.UpperLegL),Role(BoneRole.UpperLegR)};
        int handL=Role(BoneRole.HandL),handR=Role(BoneRole.HandR);
        if(torsoBones.Any(b=>b<0)||handL<0||handR<0)return null;
        var torsoMeasured=new float?[count];var torsoModel=new float[count];var wristMeasured=new[]{new float?[count],new float?[count]};var wristModel=new[]{new float[count],new float[count]};
        var seen=0;
        for(var t=0;t<count;t++)
        {
            var (positions,_)=World(motion,t);
            // Camera-relative documents are the camera's axes turned half a turn about x: distance is -z.
            torsoModel[t]=torsoBones.Average(b=>-positions[b].Z);wristModel[0][t]=-positions[handL].Z;wristModel[1][t]=-positions[handR].Z;
            var o=observations[t];var frame=depth.FrameAt(motion.Frames[t].Time);if(o is null||o.Length<51||frame<0)continue;
            var torso=new List<float>();
            foreach(var k in new[]{5,6,11,12})if(o[k*3+2]>=MinimumScore&&depth.Sample(frame,o[k*3],o[k*3+1],2) is float v)torso.Add(v);
            if(torso.Count<2)continue;
            torso.Sort();var surface=torso[torso.Count/2];torsoMeasured[t]=surface;seen++;
            for(var side=0;side<2;side++)
            {
                var k=side==0?9:10;if(o[k*3+2]<HandScore||depth.Sample(frame,o[k*3],o[k*3+1],1) is not float w)continue;
                if(depth.Kind==DepthKind.Full&&MathF.Abs(w-surface)>HandFromTorso)continue;
                wristMeasured[side][t]=w;
            }
        }
        if(seen<Math.Max(3,count/10))return null;
        var rootScale=Enumerable.Repeat(1f,count).ToArray();var wristDepth=new[]{new float?[count],new float?[count]};string note;
        if(depth.Kind==DepthKind.Full)
        {
            // The measured torso distance, filled across gaps and smoothed over a fifth of a second, over the network's.
            var ratio=new float?[count];
            for(var t=0;t<count;t++)if(torsoMeasured[t] is float m&&torsoModel[t]>.05f){var r=(m+TorsoBehindSkin)/torsoModel[t];if(r is > .4f and < 2.5f)ratio[t]=r;}
            var filled=Fill(ratio);var smooth=Smooth(filled,Math.Max(1,.2f*fps));
            for(var t=0;t<count;t++)rootScale[t]=smooth[t];
            for(var side=0;side<2;side++)for(var t=0;t<count;t++)if(wristMeasured[side][t] is float w)wristDepth[side][t]=w+WristBehindSkin;
            var moved=Enumerable.Range(0,count).Max(t=>MathF.Abs(rootScale[t]-1)*torsoModel[t]);
            note=FormattableString.Invariant($"Depth sensor: the body was placed at its measured distance from the camera (moved by up to {moved*100:F0} cm) and {wristDepth.Sum(s=>s.Count(v=>v is not null))} hand samples took their measured distance.");
        }
        else
        {
            // Relative disparity (larger nearer): a hand's disparity over the torso's is the torso's distance over the hand's.
            var applied=0;
            for(var side=0;side<2;side++)for(var t=0;t<count;t++)
            {
                if(wristMeasured[side][t] is not float w||torsoMeasured[t] is not float torso||w<=1e-3f||torso<=1e-3f)continue;
                var measured=torsoModel[t]*torso/w;var model=wristModel[side][t];
                if(MathF.Abs(measured-model)<PartialMinimumMetres)continue;
                wristDepth[side][t]=model+Math.Clamp((measured-model)*.5f,-.25f,.25f);applied++;
            }
            note=$"Cinematic mode depth: {applied} hand samples moved toward the phone's estimate of how far they were in front of or behind the body.";
        }
        return new(rootScale,wristDepth,note);
    }

    public const string CameraPathPrefix="Camera path measured by the phone:";
    /// <summary>A phone that moved less than this (metres, degrees) is treated as still: the still-camera refinement
    /// (contacts, floor, in-place detection) then serves better than the measured path.</summary>
    const float MovedMetres=.1f,MovedDegrees=5;

    /// <summary>Places a camera-relative capture in the world along the phone's own camera path (Record3D's .r3d: ARKit
    /// camera-to-world poses, gravity-aligned, metres). The capture's camera axes are ARKit's (x right, y up, z back), so
    /// each frame's root is carried by that frame's pose. Returns false when there are no poses, they do not cover the
    /// capture, or the phone stayed still.</summary>
    public static bool ToWorld(MotionDocument motion,DepthTrack depth,out string note)
    {
        note="";if(depth.Poses is not {Count:>0} poses||poses.Count!=depth.Frames.Count)return false;
        var root=motion.Bones.FindIndex(b=>b.Parent<0);if(root<0||motion.Frames.Count==0)return false;
        var used=new (Quaternion Rotation,Vector3 Position)[motion.Frames.Count];
        for(var t=0;t<motion.Frames.Count;t++)
        {
            var index=depth.FrameAt(motion.Frames[t].Time);if(index<0)return false;
            var p=poses[index];used[t]=(Quaternion.Normalize(new Quaternion(p[0],p[1],p[2],p[3])),new Vector3(p[4],p[5],p[6]));
        }
        var travelled=used.Max(u=>Vector3.Distance(u.Position,used[0].Position));
        var turned=used.Max(u=>2*MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(u.Rotation,used[0].Rotation)),0,1))*180/MathF.PI);
        if(travelled<MovedMetres&&turned<MovedDegrees)return false;
        for(var t=0;t<motion.Frames.Count;t++)
        {
            var frame=motion.Frames[t];var (rotation,position)=used[t];
            frame.Positions[root]=MotionDocument.A(Vector3.Transform(MotionDocument.V(frame.Positions[root]),rotation)+position);
            frame.Rotations[root]=MotionDocument.A(Quaternion.Normalize(rotation*MotionDocument.Q(frame.Rotations[root])));
        }
        motion.Space=MotionSpace.WorldRelative;
        motion.Diagnostics.RemoveAll(d=>d.StartsWith("Stationary recording camera",StringComparison.Ordinal)||d.StartsWith("Moving recording camera",StringComparison.Ordinal)
            ||d.StartsWith("Camera-relative reconstruction",StringComparison.Ordinal));
        note=FormattableString.Invariant($"{CameraPathPrefix} the phone travelled {travelled:F2} m and turned up to {turned:F0} degrees while recording; the body is placed in the world along that path (ARKit tracking recorded by Record3D), not estimated from the picture.");
        motion.Diagnostics.Add(note);
        return true;
    }

    /// <summary>Moves the camera-relative body along its line of sight by <see cref="Result.RootScale"/>.</summary>
    public static void ApplyRoot(MotionDocument motion,float[] scale)
    {
        var root=motion.Bones.FindIndex(b=>b.Parent<0);if(root<0)return;
        for(var t=0;t<motion.Frames.Count&&t<scale.Length;t++){var p=MotionDocument.V(motion.Frames[t].Positions[root])*scale[t];motion.Frames[t].Positions[root]=MotionDocument.A(p);}
    }
    public static void Save(string folder,float[] scale){File.WriteAllText(Path.Combine(folder,RootFile),JsonSerializer.Serialize(scale));}
    public static float[]? Load(string folder)
    {
        var path=Path.Combine(folder,RootFile);
        return File.Exists(path)?JsonSerializer.Deserialize<float[]>(File.ReadAllText(path)):null;
    }

    static float[] Fill(float?[] values)
    {
        var known=Enumerable.Range(0,values.Length).Where(i=>values[i] is not null).ToArray();var result=new float[values.Length];
        if(known.Length==0){Array.Fill(result,1f);return result;}
        for(var i=0;i<values.Length;i++)
        {
            var after=Array.FindIndex(known,k=>k>=i);
            if(after<0)result[i]=values[known[^1]]!.Value;
            else if(after==0||known[after]==i)result[i]=values[known[after]]!.Value;
            else{int a=known[after-1],b=known[after];result[i]=values[a]!.Value+(values[b]!.Value-values[a]!.Value)*(i-a)/(b-a);}
        }
        return result;
    }
    static float[] Smooth(float[] values,float sigma)
    {
        var radius=(int)MathF.Ceiling(3*sigma);var result=new float[values.Length];
        for(var i=0;i<values.Length;i++)
        {
            float sum=0,weights=0;
            for(var k=-radius;k<=radius;k++){var w=MathF.Exp(-k*k/(2*sigma*sigma));sum+=values[Math.Clamp(i+k,0,values.Length-1)]*w;weights+=w;}
            result[i]=sum/weights;
        }
        return result;
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
