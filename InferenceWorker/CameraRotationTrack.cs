using System.Numerics;
using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>Frame-to-frame rotation of a moving recording camera, in the role of GVHMR's
/// SimpleVO: background features outside the followed person are tracked between sampled
/// frames, a rotation is solved for each pair with the job's pinhole assumption, and the
/// chain is interpolated to every frame. Only rotation is estimated. Camera translation and
/// scene scale are not, so this is not camera tracking or world reconstruction.</summary>
public sealed class CameraRotationTrack : IDisposable
{
    public const string Version="background-rotation-v3";
    public const string FollowedPrefix="Moving recording camera followed:";
    const int WorkingWidth=640,Step=6,MinimumInliers=40,MaximumFilledRun=3;
    public sealed record Result(float[] AngularVelocity6d,int Pairs,int UsablePairs,float TotalDegrees,float LargestPairDegrees,float MeanInlierRatio=0,int FilledPairs=0)
    {
        /// <summary>Background that fits one rotation homography this well shows little parallax, so the
        /// camera turned about a nearly fixed point, as a standing operator's does. A camera that also
        /// travels leaves parallax, and its position is then unknown.</summary>
        public bool RotationOnly=>Usable&&MeanInlierRatio>=.8f;
        /// <summary>Usable when most sampled pairs were solved and nearly all are covered, short failed runs (a fast,
        /// blurred pan) being filled at the speed of the solved pairs around them. A phone following a tumbler
        /// solved 27 of 37 pairs; left unfollowed, each pan turned the direction he travelled.</summary>
        public bool Usable=>Pairs>0&&UsablePairs>=Pairs*.6f&&UsablePairs+FilledPairs>=Pairs*.8f;
        public string Diagnostic=>Usable
            ?FormattableString.Invariant($"{FollowedPrefix} camera rotation solved from background features for {UsablePairs}/{Pairs} sampled frame pairs ({FilledPairs} more filled from their neighbours), {TotalDegrees:F1} degrees in total and at most {LargestPairDegrees:F1} degrees per pair, and supplied to GVHMR in place of a still-camera assumption. {MeanInlierRatio*100:F0}% of background features fit a pure rotation, so the camera is treated as {(RotationOnly?"turning in place":"also travelling")}. Rotation only, from an assumed lens; camera translation and scale are not recovered.")
            :FormattableString.Invariant($"Moving recording camera could not be followed: only {UsablePairs}/{Pairs} sampled frame pairs had enough background features. The capture stays camera-relative.");
    }
    readonly float focal;Mat? previous;Rect2f previousBody;int frames;
    readonly List<(int Frame,Quaternion WorldToCamera)> samples=new();int pairs,usable;float largest,inlierRatios;
    /// <summary>Per sampled pair, its rotation (world to camera, later relative to earlier), or null when unsolved.</summary>
    readonly List<Quaternion?> deltas=new();
    /// <param name="focalLength">The job's pinhole focal length in source pixels.</param>
    public CameraRotationTrack(float focalLength){if(!(focalLength>0))throw new ArgumentOutOfRangeException(nameof(focalLength));focal=focalLength;}
    public void Add(DecodedVideoFrame frame,GvhmrDecoder.Box person,bool last)
    {
        var index=frames++;if(index%Step!=0&&!last)return;
        var scale=WorkingWidth/(float)frame.Width;
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var full=new Mat();Cv2.CvtColor(rgba,full,ColorConversionCodes.RGBA2GRAY);
        var gray=new Mat();Cv2.Resize(full,gray,new Size(WorkingWidth,Math.Max(1,(int)MathF.Round(frame.Height*scale))),0,0,InterpolationFlags.Area);
        var body=new Rect2f((person.CenterX-person.Size*.3f)*scale,(person.CenterY-person.Size*.55f)*scale,person.Size*.6f*scale,person.Size*1.1f*scale);
        if(previous is null){previous=gray;previousBody=body;samples.Add((index,Quaternion.Identity));return;}
        pairs++;var rotation=Solve(previous,gray,previousBody,body,focal*scale,out var inlierRatio);
        if(rotation is { } solved)
        {
            usable++;largest=Math.Max(largest,Degrees(solved));inlierRatios+=inlierRatio;
            samples.Add((index,Quaternion.Normalize(solved*samples[^1].WorldToCamera)));deltas.Add(solved);
        }
        else{samples.Add((index,samples[^1].WorldToCamera));deltas.Add(null);} // unsolved pair: filled in Finish if short
        previous.Dispose();previous=gray;previousBody=body;
    }
    static float Degrees(Quaternion q)=>2*MathF.Acos(Math.Clamp(MathF.Abs(q.W),0,1))*180/MathF.PI;
    static Quaternion? Solve(Mat a,Mat b,Rect2f bodyA,Rect2f bodyB,float f,out float inlierRatio)
    {
        inlierRatio=0;
        using var mask=new Mat(a.Size(),MatType.CV_8UC1,Scalar.White);
        Cv2.Rectangle(mask,new Rect((int)bodyA.X,(int)bodyA.Y,(int)bodyA.Width,(int)bodyA.Height),Scalar.Black,-1);
        var points=Cv2.GoodFeaturesToTrack(a,600,.01,7,mask,3,false,.04);
        if(points.Length<MinimumInliers)return null;
        var tracked=new Point2f[points.Length];Cv2.CalcOpticalFlowPyrLK(a,b,points,ref tracked,out var status,out _,new Size(21,21),4);
        var returned=new Point2f[points.Length];Cv2.CalcOpticalFlowPyrLK(b,a,tracked,ref returned,out var back,out _,new Size(21,21),4);
        var from=new List<Point2d>();var to=new List<Point2d>();
        for(var i=0;i<points.Length;i++)
        {
            if(status[i]==0||back[i]==0||bodyB.Contains(tracked[i])||points[i].DistanceTo(returned[i])>1)continue;
            from.Add(new(points[i].X,points[i].Y));to.Add(new(tracked[i].X,tracked[i].Y));
        }
        if(from.Count<MinimumInliers)return null;
        // Distant background under camera rotation moves by the homography K R K^-1.
        using var inliers=new Mat();
        using var homography=Cv2.FindHomography(from,to,HomographyMethods.Ransac,1.5,inliers);
        if(homography.Empty()||Cv2.CountNonZero(inliers)<Math.Max(MinimumInliers,from.Count*.5))return null;
        inlierRatio=Cv2.CountNonZero(inliers)/(float)from.Count;
        double cx=(a.Width-1)*.5,cy=(a.Height-1)*.5;
        var h=new double[3,3];for(var r=0;r<3;r++)for(var c=0;c<3;c++)h[r,c]=homography.At<double>(r,c);
        double[,] k={{f,0,cx},{0,f,cy},{0,0,1}},inverse={{1/f,0,-cx/f},{0,1/f,-cy/f},{0,0,1}};
        var m=Multiply(inverse,Multiply(h,k));
        // Nearest rotation: orthonormalise with SVD and fix the sign.
        using var matrix=new Mat(3,3,MatType.CV_64FC1);for(var r=0;r<3;r++)for(var c=0;c<3;c++)matrix.Set(r,c,m[r,c]);
        using var w=new Mat();using var u=new Mat();using var vt=new Mat();Cv2.SVDecomp(matrix,w,u,vt);
        using var product=(u*vt).ToMat();var rotation=new double[3,3];for(var r=0;r<3;r++)for(var c=0;c<3;c++)rotation[r,c]=product.At<double>(r,c);
        var determinant=rotation[0,0]*(rotation[1,1]*rotation[2,2]-rotation[1,2]*rotation[2,1])-rotation[0,1]*(rotation[1,0]*rotation[2,2]-rotation[1,2]*rotation[2,0])+rotation[0,2]*(rotation[1,0]*rotation[2,1]-rotation[1,1]*rotation[2,0]);
        if(determinant<0)for(var r=0;r<3;r++)for(var c=0;c<3;c++)rotation[r,c]=-rotation[r,c];
        // System.Numerics uses row vectors: its matrix is the transpose of this column-vector rotation.
        var q=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            (float)rotation[0,0],(float)rotation[1,0],(float)rotation[2,0],0,(float)rotation[0,1],(float)rotation[1,1],(float)rotation[2,1],0,
            (float)rotation[0,2],(float)rotation[1,2],(float)rotation[2,2],0,0,0,0,1)));
        // A sampled pair a fifth of a second apart cannot plausibly turn this far; treat it as a failed solve.
        return float.IsFinite(q.W)&&Degrees(q)<=25?q:null;
    }
    static double[,] Multiply(double[,] a,double[,] b)
    {var result=new double[3,3];for(var r=0;r<3;r++)for(var c=0;c<3;c++)for(var i=0;i<3;i++)result[r,c]+=a[r,i]*b[i,c];return result;}
    public Result Finish()
    {
        var count=frames;var result=new float[count*6];if(count==0)return new(result,0,0,0,0);
        // Fill runs of up to MaximumFilledRun unsolved pairs with the average turn of the solved pairs either side.
        var filled=0;
        for(var i=0;i<deltas.Count;)
        {
            if(deltas[i] is not null){i++;continue;}
            var e=i;while(e<deltas.Count&&deltas[e] is null)e++;
            var before=i>0?deltas[i-1]:null;var after=e<deltas.Count?deltas[e]:null;
            if(e-i<=MaximumFilledRun&&(before is not null||after is not null))
            {
                var fill=before is {} b&&after is {} a?Quaternion.Slerp(b,a,.5f):(before??after)!.Value;
                for(var k=i;k<e;k++){deltas[k]=fill;filled++;}
            }
            i=e;
        }
        if(filled>0)
        {
            // Rebuild the sampled orientations from the pair rotations.
            var orientationSoFar=Quaternion.Identity;
            for(var k=0;k<deltas.Count&&k+1<samples.Count;k++)
            {
                if(deltas[k] is {} d)orientationSoFar=Quaternion.Normalize(d*orientationSoFar);
                samples[k+1]=(samples[k+1].Frame,orientationSoFar);
            }
        }
        var orientation=new Quaternion[count];var next=0;
        for(var t=0;t<count;t++)
        {
            while(next<samples.Count-1&&samples[next+1].Frame<=t)next++;
            var a=samples[next];var b=samples[Math.Min(next+1,samples.Count-1)];
            orientation[t]=b.Frame==a.Frame?a.WorldToCamera:Quaternion.Slerp(a.WorldToCamera,b.WorldToCamera,Math.Clamp((t-a.Frame)/(float)(b.Frame-a.Frame),0,1));
        }
        for(var t=0;t<count;t++)
        {
            // GVHMR compute_cam_angvel: R[t+1] R[t]^T, with the final value repeated.
            var s=Math.Min(t,count-2);var relative=count<2?Quaternion.Identity:Quaternion.Normalize(orientation[s+1]*Quaternion.Conjugate(orientation[s]));
            var m=Matrix4x4.CreateFromQuaternion(relative);
            // First two rows of the column-vector rotation matrix (PyTorch3D 6D layout).
            result[t*6]=m.M11;result[t*6+1]=m.M21;result[t*6+2]=m.M31;result[t*6+3]=m.M12;result[t*6+4]=m.M22;result[t*6+5]=m.M32;
        }
        return new(result,pairs,usable,Degrees(orientation[^1]),largest,usable==0?0:inlierRatios/usable,filled);
    }
    public void Dispose(){previous?.Dispose();previous=null;}
}
