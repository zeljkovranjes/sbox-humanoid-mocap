using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

/// <summary>Finds moments when the performer sits on the floor. Seen from a low camera, sitting with the
/// hips on the floor further back and crouching with them raised project almost the same, and the body
/// network reads floor sits as crouches: on a breakdance clip the pelvis sat 25 cm above the floor while the
/// dancer was seated. The floor plane is fitted in camera space to the feet wherever the network predicts
/// them planted; the pelvis is then placed on its own image ray at seated height above that floor. When the
/// pelvis is low and that seated placement lies within leg reach of the feet, the frame is taken as seated.
/// A crouch fails the test by metres, since its ray meets seated height far behind the feet.</summary>
public static class SeatedDetection
{
    public const string Source="Seated on the floor: pelvis image ray meets seated height within leg reach of the planted feet";
    /// <summary>Height of the hip joint above the floor when seated, in metres.</summary>
    public const float SeatHeight=.11f;
    /// <summary>A pelvis this high above the floor is standing or crouching, whatever the geometry allows.</summary>
    public const float MaximumPelvisHeight=.35f;
    /// <summary>Seated with the legs out straight, the hips sit about one leg length from the feet; a crouch misses by metres.</summary>
    public const float ReachFraction=1.1f;
    public const double MinimumSeconds=.3,RampSeconds=.25;

    /// <param name="cameraRelative">The capture in document camera space (x right, y up, camera looking along -z).</param>
    /// <param name="observations">COCO-17 (x, y, score) per frame in source pixels.</param>
    /// <param name="staticLogits">GVHMR static-joint logits, six per frame: left ankle, left foot, right ankle, right foot, left wrist, right wrist.</param>
    /// <returns>A seated weight per frame from 0 to 1, or null when the floor cannot be found.</returns>
    public static float[]? Detect(MotionDocument cameraRelative,IReadOnlyList<float[]> observations,float[] staticLogits,float focal,float centerX,float centerY)
    {
        var frames=cameraRelative.Frames;var n=frames.Count;
        if(n<3||observations.Count!=n||staticLogits.Length!=n*6)return null;
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
        var (center,normal)=FitPlane(points);if(normal.Y>0)normal=-normal; // up is -y in camera coordinates
        float Height(Vector3 p)=>Vector3.Dot(p-center,normal);
        var heights=points.Select(Height).OrderBy(h=>h).ToArray();var floor=heights[heights.Length/10];
        var leg=Vector3.Distance(world[0][upperL],world[0][kneeL])+Vector3.Distance(world[0][kneeL],world[0][ankleL]);
        var seated=new bool[n];
        for(var f=0;f<n;f++)
        {
            var o=observations[f];if(o.Length!=51||!(o[11*3+2]>=.5f)||!(o[12*3+2]>=.5f))continue;
            var pelvis=world[f][hips];if(!(Height(pelvis)-floor<MaximumPelvisHeight))continue;
            var px=(o[11*3]+o[12*3])/2;var py=(o[11*3+1]+o[12*3+1])/2;
            var ray=Vector3.Normalize(new((px-centerX)/focal,(py-centerY)/focal,1));
            var along=Vector3.Dot(ray,normal);if(MathF.Abs(along)<1e-4f)continue;
            // Point on the ray at seated height: dot(t*ray-center, normal) = floor + SeatHeight.
            var t=(floor+SeatHeight+Vector3.Dot(center,normal))/along;if(!(t>0))continue;
            var seat=ray*t;var feet=(world[f][ankleL]+world[f][ankleR])/2;
            var offset=seat-feet;offset-=Vector3.Dot(offset,normal)*normal;
            seated[f]=offset.Length()<=leg*ReachFraction&&seat.Z>=pelvis.Z-.05f;
        }
        // Drop flickers shorter than MinimumSeconds, then ease in and out.
        var fps=(n-1)/Math.Max(1e-3,frames[^1].Time-frames[0].Time);var minimum=(int)Math.Ceiling(MinimumSeconds*fps);
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
    static (Vector3 Center,Vector3 Normal) FitPlane(List<Vector3> points)
    {
        var c=points.Aggregate(Vector3.Zero,(a,b)=>a+b)/points.Count;
        double xx=0,xy=0,xz=0,yy=0,yz=0,zz=0;
        foreach(var p in points){var d=p-c;xx+=d.X*d.X;xy+=d.X*d.Y;xz+=d.X*d.Z;yy+=d.Y*d.Y;yz+=d.Y*d.Z;zz+=d.Z*d.Z;}
        // Normal = eigenvector of the smallest eigenvalue: inverse iteration on the covariance.
        var m=new double[3,3]{{xx,xy,xz},{xy,yy,yz},{xz,yz,zz}};var trace=xx+yy+zz;
        for(var i=0;i<3;i++)m[i,i]+=1e-9*trace;
        var v=new double[]{0,1,0};
        for(var it=0;it<50;it++)
        {
            var s=Solve(m,v);var len=Math.Sqrt(s[0]*s[0]+s[1]*s[1]+s[2]*s[2]);if(!(len>0))break;v=new[]{s[0]/len,s[1]/len,s[2]/len};
        }
        return (c,Vector3.Normalize(new((float)v[0],(float)v[1],(float)v[2])));
    }
    static double[] Solve(double[,] a,double[] b)
    {
        double det(double[,] m)=>m[0,0]*(m[1,1]*m[2,2]-m[1,2]*m[2,1])-m[0,1]*(m[1,0]*m[2,2]-m[1,2]*m[2,0])+m[0,2]*(m[1,0]*m[2,1]-m[1,1]*m[2,0]);
        var d=det(a);var r=new double[3];
        for(var c=0;c<3;c++){var m=(double[,])a.Clone();for(var i=0;i<3;i++)m[i,c]=b[i];r[c]=det(m)/d;}
        return r;
    }
}
