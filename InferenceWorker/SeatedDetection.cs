using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

/// <summary>Finds moments when the performer sits on the floor. Seen from a low camera, sitting with the
/// hips on the floor further back and crouching with them raised project almost the same, and the body
/// network reads floor sits as crouches: on a breakdance clip the pelvis sat 25 cm above the floor while the
/// dancer was seated. The floor is placed in camera space under the feet wherever the network predicts them
/// planted, level with gravity as the network estimates it (a plane fitted to the feet themselves tilts freely
/// when they travel along one line, as on a skateboard, and stood a crouching rider on a floor through his
/// knees); the pelvis is then placed on its own image ray at seated height above that floor. When the
/// pelvis is low and that seated placement lies within leg reach of the feet, the frame is taken as seated.
/// A crouch fails the test by metres, since its ray meets seated height far behind the feet.</summary>
public static class SeatedDetection
{
    public const string Source="Seated on the floor: pelvis image ray meets seated height within leg reach of the planted feet";
    /// <summary>Height of the hip joint above the floor when seated, in metres.</summary>
    public const float SeatHeight=.11f;
    /// <summary>A pelvis this high above the floor is standing or crouching, whatever the geometry allows.</summary>
    public const float MaximumPelvisHeight=.42f;
    /// <summary>Hips this far above the lowest foot joint, in the pose itself, are not sitting on the floor. Sits the
    /// network read as crouches measured 10 to 32 cm; a landing crouch 68 to 82.</summary>
    public const float MaximumHipsAboveFeet=.5f;
    /// <summary>Seated with the legs out straight, the hips sit about one leg length from the feet; a crouch misses by metres.</summary>
    public const float ReachFraction=1.3f;
    public const double MinimumSeconds=.1,RampSeconds=.25,GapSeconds=.6;

    /// <param name="cameraRelative">The capture in document camera space (x right, y up, camera looking along -z).</param>
    /// <param name="observations">COCO-17 (x, y, score) per frame in source pixels.</param>
    /// <param name="cameraDown">Per frame, the unit direction of gravity in camera coordinates (x right, y down, z forward).</param>
    /// <param name="staticLogits">GVHMR static-joint logits, six per frame: left ankle, left foot, right ankle, right foot, left wrist, right wrist.</param>
    /// <returns>A seated weight per frame from 0 to 1, or null when the floor cannot be found.</returns>
    public static float[]? Detect(MotionDocument cameraRelative,IReadOnlyList<float[]> observations,IReadOnlyList<Vector3> cameraDown,float[] staticLogits,float focal,float centerX,float centerY)
    {
        var frames=cameraRelative.Frames;var n=frames.Count;
        if(n<3||observations.Count!=n||cameraDown.Count!=n||staticLogits.Length!=n*6)return null;
        int Bone(BoneRole role)=>cameraRelative.Bones.FindIndex(b=>b.Role==role);
        int hips=Bone(BoneRole.Hips),upperL=Bone(BoneRole.UpperLegL),kneeL=Bone(BoneRole.LowerLegL),ankleL=Bone(BoneRole.FootL),ankleR=Bone(BoneRole.FootR),toeL=Bone(BoneRole.ToeL),toeR=Bone(BoneRole.ToeR);
        if(new[]{hips,upperL,kneeL,ankleL,ankleR,toeL,toeR}.Any(i=>i<0))return null;
        // Camera coordinates: x right, y down, z forward.
        var world=frames.Select(f=>World(cameraRelative,f).Select(p=>new Vector3(p.X,-p.Y,-p.Z)).ToArray()).ToArray();
        static float Sigmoid(float x)=>1/(1+MathF.Exp(-x));
        var points=new List<Vector3>();
        for(var f=0;f<n;f++)foreach(var (k,joint) in new[]{(0,ankleL),(1,toeL),(2,ankleR),(3,toeR)})
            if(Sigmoid(staticLogits[f*6+k])>.8f)points.Add(world[f][joint]);
        if(points.Count<30)return null;
        // A camera that tilted during the clip, or a network unsure of gravity, has no single floor.
        var sum=cameraDown.Aggregate(Vector3.Zero,(a,b)=>a+b);if(!(sum.Length()>=.97f*n))return null;
        var normal=-Vector3.Normalize(sum);if(!(normal.Y<-.5f))return null;
        var center=points.Aggregate(Vector3.Zero,(a,b)=>a+b)/points.Count;
        float Height(Vector3 p)=>Vector3.Dot(p-center,normal);
        var heights=points.Select(Height).OrderBy(h=>h).ToArray();var floor=heights[heights.Length/10];
        var leg=Vector3.Distance(world[0][upperL],world[0][kneeL])+Vector3.Distance(world[0][kneeL],world[0][ankleL]);
        var seated=new bool[n];
        // Frames that could still be seated: the hips unreadable (blurred) or low. A frame with the hips high cannot.
        var possible=new bool[n];
        for(var f=0;f<n;f++)
        {
            var o=observations[f];var pelvis=world[f][hips];
            if(!(Height(pelvis)-floor<MaximumPelvisHeight))continue;
            // The pose itself, whatever the capture's height: sitting on the floor puts the hips near the feet. A
            // landing crouch whose capture had drifted low was otherwise taken for a sit (hips 70 cm or more above).
            var feetLow=new[]{world[f][ankleL],world[f][ankleR],world[f][toeL],world[f][toeR]}.Min(Height);
            if(!(Height(pelvis)-feetLow<MaximumHipsAboveFeet))continue;
            possible[f]=true;
            if(o.Length!=51||!(o[11*3+2]>=.5f)||!(o[12*3+2]>=.5f))continue;
            var px=(o[11*3]+o[12*3])/2;var py=(o[11*3+1]+o[12*3+1])/2;
            var ray=Vector3.Normalize(new((px-centerX)/focal,(py-centerY)/focal,1));
            var along=Vector3.Dot(ray,normal);if(MathF.Abs(along)<1e-4f)continue;
            // Point on the ray at seated height: dot(t*ray-center, normal) = floor + SeatHeight.
            var t=(floor+SeatHeight+Vector3.Dot(center,normal))/along;if(!(t>0))continue;
            var seat=ray*t;var feet=(world[f][ankleL]+world[f][ankleR])/2;
            var offset=seat-feet;offset-=Vector3.Dot(offset,normal)*normal;
            seated[f]=offset.Length()<=leg*ReachFraction&&seat.Z>=pelvis.Z-.05f;
        }
        var fps=(n-1)/Math.Max(1e-3,frames[^1].Time-frames[0].Time);var minimum=Math.Max(1,(int)Math.Round(MinimumSeconds*fps));
        // Bridge short stretches between seated frames where the test could not run: a performer swinging his
        // arms while sitting blurred the hips for 0.4 s, and the hips then rose out of the sit and back.
        var gap=(int)Math.Ceiling(GapSeconds*fps);
        for(var f=0;f<n;)
        {
            if(seated[f]){f++;continue;}
            var e=f;while(e<n&&!seated[e])e++;
            if(f>0&&e<n&&e-f<=gap&&Enumerable.Range(f,e-f).All(k=>possible[k]))for(var k=f;k<e;k++)seated[k]=true;
            f=e;
        }
        // Drop flickers shorter than MinimumSeconds, then ease in and out.
        for(var start=0;start<n;)
        {
            if(!seated[start]){start++;continue;}
            var end=start;while(end<n&&seated[end])end++;
            if(end-start<minimum)for(var i=start;i<end;i++)seated[i]=false;
            start=end;
        }
        if(!seated.Any(s=>s))return new float[n];
        var ramp=Math.Max(1,(int)Math.Round(RampSeconds*fps));var weight=new float[n];
        for(var f=0;f<n;f++)
        {
            var distance=int.MaxValue;
            for(var k=Math.Max(0,f-ramp);k<=Math.Min(n-1,f+ramp);k++)if(seated[k])distance=Math.Min(distance,Math.Abs(k-f));
            if(distance==int.MaxValue)continue;
            var x=1-distance/(float)(ramp+1);weight[f]=seated[f]?1:x*x*(3-2*x);
        }
        return weight;
    }
    static Vector3[] World(MotionDocument doc,MotionFrame frame)
    {
        var p=new Vector3[doc.Bones.Count];var q=new Quaternion[doc.Bones.Count];
        for(var i=0;i<doc.Bones.Count;i++)
        {
            var local=MotionDocument.V(frame.Positions[i]);var rotation=MotionDocument.Q(frame.Rotations[i]);var parent=doc.Bones[i].Parent;
            if(parent<0){p[i]=local;q[i]=rotation;}
            else{p[i]=p[parent]+Vector3.Transform(local,q[parent]);q[i]=Quaternion.Normalize(q[parent]*rotation);}
        }
        return p;
    }
}
