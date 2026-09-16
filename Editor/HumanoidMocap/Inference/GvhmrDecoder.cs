#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Inference;
using Vector3=System.Numerics.Vector3;

/// <summary>GVHMR release decoding and camera conditioning. Coordinates remain in metres:
/// camera space is x-right/y-down/z-forward; global output is the upstream gravity-aligned
/// y-up frame. This is a monocular estimate, not calibrated world-space ground truth.
/// Adapted from GVHMR ee960bb6, endecoder.py and gvhmr_pipeline.py; see Gvhmr.LICENSE.</summary>
public static class GvhmrDecoder
{
    public readonly record struct Camera(float FocalLength,float CenterX,float CenterY);
    public readonly record struct Box(float CenterX,float CenterY,float Size);
    public sealed record Pose(int Frames,Quaternion[] BodyRotations,float[] Betas,
        Quaternion[] CameraOrientation,Quaternion[] GravityOrientation,Vector3[] LocalDisplacement);
    public sealed record Root(Quaternion[] Orientation,Vector3[] Translation);
    public sealed record Conditions(float[] Observations,float[] CliffCamera,float[] NormalizedAngularVelocity);

    public static Conditions Prepare(float[] coco17,Box[] boxes,Camera[] cameras,float[] angularVelocity6d)
    {
        var frames=boxes.Length;CheckFrames(frames);Check(coco17,frames*51);Check(angularVelocity6d,frames*6);
        if(cameras.Length!=frames)throw new ArgumentException("One camera intrinsic record is required per frame.");
        var observations=new float[coco17.Length];var cliff=new float[frames*3];var angular=new float[frames*6];
        for(var t=0;t<frames;t++)
        {
            Validate(boxes[t],cameras[t]);var b=boxes[t];var c=cameras[t];
            cliff[t*3]=(b.CenterX-c.CenterX)/c.FocalLength;cliff[t*3+1]=(b.CenterY-c.CenterY)/c.FocalLength;cliff[t*3+2]=b.Size/c.FocalLength;
            for(var j=0;j<17;j++)
            {
                var i=t*51+j*3;var x=coco17[i];var y=coco17[i+1];
                observations[i]=2*(x-b.CenterX)/b.Size;observations[i+1]=2*(y-b.CenterY)/b.Size;
                observations[i+2]=Math.Abs(x-b.CenterX)>b.Size/2||Math.Abs(y-b.CenterY)>b.Size/2?0:coco17[i+2];
            }
            for(var j=0;j<6;j++)angular[t*6+j]=(angularVelocity6d[t*6+j]-(j is 0 or 4?1f:0f))/(j is 0 or 4?.001f:.1f);
        }
        return new(observations,cliff,angular);
    }
    public static Pose Decode(float[] normalizedPrediction,int frames)
    {
        CheckFrames(frames);Check(normalizedPrediction,frames*151);
        var rotations=new Quaternion[frames*21];var betas=new float[frames*10];
        var camera=new Quaternion[frames];var gravity=new Quaternion[frames];var displacement=new Vector3[frames];
        Span<float> values=stackalloc float[151];
        for(var t=0;t<frames;t++)
        {
            for(var i=0;i<151;i++)values[i]=normalizedPrediction[t*151+i]*GvhmrStatistics.StandardDeviation[i]+GvhmrStatistics.Mean[i];
            for(var j=0;j<21;j++)rotations[t*21+j]=Continuous(Rotation6D(values.Slice(j*6,6)),t>0?rotations[(t-1)*21+j]:Quaternion.Identity);
            for(var i=0;i<10;i++)betas[t*10+i]=values[126+i];
            camera[t]=Continuous(Rotation6D(values.Slice(136,6)),t>0?camera[t-1]:Quaternion.Identity);
            gravity[t]=Continuous(Rotation6D(values.Slice(142,6)),t>0?gravity[t-1]:Quaternion.Identity);
            displacement[t]=new(values[148],values[149],values[150]);
        }
        return new(frames,rotations,betas,camera,gravity,displacement);
    }
    public static Vector3[] CameraTranslation(float[] predictedCamera,Box[] boxes,Camera[] cameras)
    {
        CheckFrames(boxes.Length);Check(predictedCamera,boxes.Length*3);
        if(cameras.Length!=boxes.Length)throw new ArgumentException("Camera and bounding box counts differ.");
        var result=new Vector3[boxes.Length];
        for(var t=0;t<boxes.Length;t++)
        {
            Validate(boxes[t],cameras[t]);var b=boxes[t];var c=cameras[t];
            if(predictedCamera[t*3]<=0)throw new ArgumentException("Camera scale must be positive.");
            var sb=predictedCamera[t*3]*b.Size+1e-9f;
            result[t]=new(predictedCamera[t*3+1]+2*(b.CenterX-c.CenterX)/sb,
                predictedCamera[t*3+2]+2*(b.CenterY-c.CenterY)/sb,2*c.FocalLength/sb);
        }
        return result;
    }
    /// <summary>Upstream heading rollout, including its sigma=3 temporal processing and
    /// per-frame displacement convention. Do not multiply displacements by frame duration.
    /// Camera rotations must come from actual estimates or an explicitly static camera.</summary>
    public static Root WorldRoot(Pose pose,float[] cameraAngularVelocity6d)
    {
        CheckFrames(pose.Frames);Check(cameraAngularVelocity6d,pose.Frames*6);
        if(pose.CameraOrientation.Length!=pose.Frames||pose.GravityOrientation.Length!=pose.Frames||pose.LocalDisplacement.Length!=pose.Frames)
            throw new ArgumentException("Invalid decoded root tracks.");
        var yaw=new Vector3[pose.Frames];
        for(var t=0;t<pose.Frames;t++)
        {
            var relative=AsIdentity(Rotation6D(cameraAngularVelocity6d.AsSpan(t*6,6)));
            var cameraToGravity=pose.GravityOrientation[t]*Quaternion.Conjugate(pose.CameraOrientation[t]);
            var view=Vector3.Transform(Vector3.UnitZ,cameraToGravity);
            var nextView=Vector3.Transform(Vector3.UnitZ,cameraToGravity*Quaternion.Conjugate(relative));
            var a=Normalize(new(view.X,0,view.Z));var b=Normalize(new(nextView.X,0,nextView.Z));
            yaw[t]=Normalize(Vector3.Cross(b,a))*MathF.Acos(Math.Clamp(Vector3.Dot(a,b),-1,1));
        }
        yaw=Gaussian(yaw,3);var orientation=new Quaternion[pose.Frames];var translation=new Vector3[pose.Frames];
        var accumulated=Quaternion.Identity;var position=Vector3.Zero;
        var flip=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI);
        for(var t=0;t<pose.Frames;t++)
        {
            // The reference deliberately starts at relative rotation index one.
            if(t>0)accumulated=Quaternion.Normalize(accumulated*Quaternion.Conjugate(FromAxisAngle(yaw[t])));
            var world=Quaternion.Normalize(AsIdentity(accumulated)*pose.GravityOrientation[t]);
            orientation[t]=Continuous(Quaternion.Normalize(flip*world),t>0?orientation[t-1]:Quaternion.Identity);
            translation[t]=Vector3.Transform(position,flip);
            position+=Vector3.Transform(pose.LocalDisplacement[t],world);
        }
        return new(orientation,translation);
    }
    public static Quaternion Rotation6D(ReadOnlySpan<float> values)
    {
        if(values.Length!=6)throw new ArgumentException("Rotation requires six components.");
        var a=new Vector3(values[0],values[1],values[2]);var b=new Vector3(values[3],values[4],values[5]);
        if(!Finite(a)||!Finite(b)||a.LengthSquared()<1e-16f)throw new ArgumentException("Degenerate/non-finite 6D rotation.");
        var r0=Vector3.Normalize(a);var residual=b-Vector3.Dot(r0,b)*r0;
        if(residual.LengthSquared()<1e-16f)throw new ArgumentException("Collinear 6D rotation axes.");
        var r1=Vector3.Normalize(residual);var r2=Vector3.Cross(r0,r1);
        // PyTorch3D uses column vectors and stores the first two matrix rows. System.Numerics
        // uses row vectors: transpose at this boundary, then use quaternion composition.
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            r0.X,r1.X,r2.X,0,r0.Y,r1.Y,r2.Y,0,r0.Z,r1.Z,r2.Z,0,0,0,0,1)));
    }
    internal static Quaternion FromAxisAngle(Vector3 aa)
    {var angle=aa.Length();return angle<1e-12f?Quaternion.Identity:Quaternion.CreateFromAxisAngle(aa/angle,angle);}
    internal static Quaternion Continuous(Quaternion q,Quaternion previous)=>Quaternion.Dot(q,previous)<0?new(-q.X,-q.Y,-q.Z,-q.W):q;
    static Quaternion AsIdentity(Quaternion q)=>new Vector3(q.X,q.Y,q.Z).Length()<5e-6f?Quaternion.Identity:q;
    static Vector3 Normalize(Vector3 v)=>v/Math.Max(v.Length(),1e-12f);
    internal static Vector3[] Gaussian(Vector3[] data,float sigma)
    {
        if(data.Length==0)return Array.Empty<Vector3>();
        var radius=(int)(4*sigma+.5f);var kernel=new float[radius*2+1];double sum=0;
        for(var i=-radius;i<=radius;i++)sum+=Math.Exp(-.5*i*i/(sigma*sigma));
        for(var i=-radius;i<=radius;i++)kernel[i+radius]=(float)(Math.Exp(-.5*i*i/(sigma*sigma))/sum);
        var result=new Vector3[data.Length];
        for(var t=0;t<data.Length;t++)for(var i=-radius;i<=radius;i++)result[t]+=data[Math.Clamp(t+i,0,data.Length-1)]*kernel[i+radius];
        return result;
    }
    internal static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    static void Check(float[] values,int expected)
    {if(values is null||values.Length!=expected||values.Any(v=>!float.IsFinite(v)))throw new ArgumentException("Non-finite or incorrectly sized GVHMR data.");}
    static void CheckFrames(int frames){if(frames<1||frames>GvhmrTemporalNetwork.MaximumFrames)throw new ArgumentOutOfRangeException(nameof(frames));}
    static void Validate(Box b,Camera c)
    {if(!float.IsFinite(b.CenterX)||!float.IsFinite(b.CenterY)||!float.IsFinite(b.Size)||b.Size<=0||!float.IsFinite(c.FocalLength)||c.FocalLength<=0||!float.IsFinite(c.CenterX)||!float.IsFinite(c.CenterY))throw new ArgumentException("Invalid bounding box or camera intrinsics.");}
}
