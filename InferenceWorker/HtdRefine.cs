// Motion fitting adapted from HTD-Refine (ant-research/HTD-Refine, optimization/), AGPL-3.0.
using System.IO.Compression;
using System.Numerics;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>HTD-Refine's fit (Wei, Shen et al., CVPR 2026): moves GVHMR's per-frame body parameters so the COCO joints follow the
/// 2D positions, velocities and accelerations <see cref="PvaNet"/> saw, penalizing jerk and staying close to where GVHMR put
/// them. GVHMR's motion tends to be over-smoothed and slightly off the picture; this pulls it back onto the video and
/// removes shake without flattening fast moves. Camera space throughout (the network's velocities are camera-relative),
/// so it needs no camera path. The reference's loss, weights, Adam schedule and 1500 steps are kept; its gradients come
/// from PyTorch, here they are written out: each COCO joint is a fixed blend of points carried rigidly by the 22 body
/// joints (the SMPL-X vertices it is regressed from, at their skinning weights, hands at the model's mean pose), so a
/// joint's rotation moves the points below it about that joint. Pose-dependent corrective shapes, a few millimetres, are
/// left out. Betas stay fixed at the clip's mean.</summary>
public static class HtdRefine
{
    public const string Version="htd-refine-v1";
    const int Joints=22,Points=17,Steps=1500,Warmup=10,Milestone=1000;
    const double LearningRate=1e-2,Gamma=.1;
    static readonly int[] parents={-1,0,0,0,1,2,3,4,5,6,7,8,9,9,9,12,13,14,16,17,18,19};
    static readonly double[] vertexBase={200,200,200,200,200,1000,1000,500,500,250,250,1000,1000,500,500,250,250};
    static readonly double[] velocityLimit={2.9097,2.9159,2.9111,2.8630,2.8225,2.8298,2.8383,4.0343,4.1615,5.7119,6.0373,2.7371,2.7457,3.9189,4.2014,5.8695,6.4464};
    static readonly double[] accelerationLimit={76.2970,75.3610,76.1500,73.3050,73.9110,64.3060,64.8900,86.8350,97.0060,115.8650,150.0760,61.1690,58.9190,84.2080,85.6460,126.3640,150.7630};

    /// <summary>The refined per-frame parameters: 21 body joint rotations per frame, the body's orientation and SMPL-X
    /// translation in camera space.</summary>
    public sealed record Result(Quaternion[] BodyRotations,Quaternion[] Orientation,Vector3[] Translation,double StartLoss,double EndLoss,double Seconds,double ModelSeconds);

    public static Result Refine(string smplxPath,GvhmrDecoder.Pose pose,Vector3[] translation,PvaNet.Targets targets,GvhmrDecoder.Camera camera,double fps,CancellationToken cancellation,Action<string>? progress=null)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        var frames=pose.Frames;if(frames<4||translation.Length!=frames||targets.Keypoints.Length!=frames*51)throw new ArgumentException("Motion refinement inputs disagree.");
        var betas=new double[10];for(var t=0;t<frames;t++)for(var b=0;b<10;b++)betas[b]+=pose.Betas[t*10+b]/(double)frames;
        var model=Attachments.Load(smplxPath,betas,cancellation);var loadSeconds=clock.Elapsed.TotalSeconds;
        var dt=1/fps;
        // Parameters, as the reference optimizes them: axis-angle root orientation, 21 axis-angle body rotations, translation.
        var p=new double[frames*69];
        for(var t=0;t<frames;t++)
        {
            Put(p,t*69,AxisAngle(pose.CameraOrientation[t]));
            for(var j=0;j<21;j++)Put(p,t*69+3+j*3,AxisAngle(pose.BodyRotations[t*21+j]));
            p[t*69+66]=translation[t].X;p[t*69+67]=translation[t].Y;p[t*69+68]=translation[t].Z;
        }
        var reference=(double[])p.Clone();
        // Keypoint confidences through the reference's sigmoid; velocity and acceleration take the least of their frames'.
        var confidence=new double[frames*Points];for(var i=0;i<confidence.Length;i++)confidence[i]=1/(1+Math.Exp(-20*(targets.Keypoints[i*3+2]-.6)));
        var width=camera.CenterX*2;var height=camera.CenterY*2;var resolution=1280.0*960/(width*height);
        var gradient=new double[p.Length];var m=new double[p.Length];var v=new double[p.Length];
        var keypoints=new Vector3d[frames*Points];var pointGradient=new Vector3d[frames*Points];
        var world=new Frame[frames];for(var t=0;t<frames;t++)world[t]=new Frame();
        double startLoss=0,loss=0;
        var chunks=System.Collections.Concurrent.Partitioner.Create(0,frames,Math.Max(8,frames/(2*Environment.ProcessorCount)));
        for(var step=0;step<Steps;step++)
        {
            cancellation.ThrowIfCancellationRequested();
            Parallel.ForEach(chunks,range=>{for(var t=range.Item1;t<range.Item2;t++)world[t].Forward(p,t*69,model,keypoints.AsSpan(t*Points,Points));});
            Array.Clear(pointGradient);
            loss=Loss(keypoints,pointGradient,targets,confidence,frames,dt,camera,resolution);
            // Regularization toward GVHMR's own values: body pose x10, orientation x1, translation x0.1, all x1e4.
            Array.Clear(gradient);
            for(var t=0;t<frames;t++)for(var i=0;i<69;i++)
            {
                var d=p[t*69+i]-reference[t*69+i];
                var (weight,count)=i<3?(1.0,frames*3.0):i<66?(10.0,frames*63.0):(.1,frames*3.0);
                loss+=1e4*weight*d*d/count;gradient[t*69+i]=1e4*weight*2*d/count;
            }
            if(step==0)startLoss=loss;
            Parallel.ForEach(chunks,range=>{for(var t=range.Item1;t<range.Item2;t++)world[t].Backward(p,t*69,model,pointGradient.AsSpan(t*Points,Points),gradient.AsSpan(t*69,69));});
            // Adam, with the reference's ten-step warmup and one tenfold decay.
            var rate=step<Warmup?LearningRate*(step+1)/(Warmup+1):step>=Milestone?LearningRate*Gamma:LearningRate;
            double c1=1-Math.Pow(.9,step+1),c2=1-Math.Pow(.999,step+1);
            for(var i=0;i<p.Length;i++)
            {
                m[i]=.9*m[i]+.1*gradient[i];v[i]=.999*v[i]+.001*gradient[i]*gradient[i];
                p[i]-=rate*(m[i]/c1)/(Math.Sqrt(v[i]/c2)+1e-8);
            }
            if(step%300==0)progress?.Invoke($"Refining the motion against the video {step*100/Steps}%");
        }
        var body=new Quaternion[frames*21];var orientation=new Quaternion[frames];var moved=new Vector3[frames];
        for(var t=0;t<frames;t++)
        {
            orientation[t]=FromAxisAngle(p,t*69);
            for(var j=0;j<21;j++)body[t*21+j]=FromAxisAngle(p,t*69+3+j*3);
            moved[t]=new((float)p[t*69+66],(float)p[t*69+67],(float)p[t*69+68]);
        }
        for(var t=0;t<frames;t++)if(!float.IsFinite(moved[t].X+moved[t].Y+moved[t].Z)||!float.IsFinite(orientation[t].W))throw new ArithmeticException("Motion refinement diverged.");
        return new(body,orientation,moved,startLoss,loss,clock.Elapsed.TotalSeconds,loadSeconds);
    }

    /// <summary>The capture's refined body, saved beside its GVHMR predictions so the contact refinement starts from it.</summary>
    public const string RefinedFile="htd-refined.json";
    public sealed record Saved(string Version,float[] BodyRotations,float[] CameraOrientation,float[] GravityOrientation);
    /// <summary>The decoded GVHMR output with the refined body. The body's orientation relative to gravity turns with its
    /// refined orientation to the camera, so the camera's own tilt against gravity is unchanged.</summary>
    public static GvhmrDecoder.Pose Apply(GvhmrDecoder.Pose pose,Result refined)
    {
        var gravity=Enumerable.Range(0,pose.Frames).Select(t=>Quaternion.Normalize(pose.GravityOrientation[t]*Quaternion.Conjugate(pose.CameraOrientation[t])*refined.Orientation[t])).ToArray();
        return pose with{BodyRotations=refined.BodyRotations,CameraOrientation=refined.Orientation,GravityOrientation=gravity};
    }
    static float[] Flat(Quaternion[] q)=>q.SelectMany(x=>new[]{x.X,x.Y,x.Z,x.W}).ToArray();
    static Quaternion[] Unflat(float[] v)=>Enumerable.Range(0,v.Length/4).Select(i=>new Quaternion(v[i*4],v[i*4+1],v[i*4+2],v[i*4+3])).ToArray();
    public static Saved Save(GvhmrDecoder.Pose pose,Vector3[] translation)=>new(Version,Flat(pose.BodyRotations),Flat(pose.CameraOrientation),Flat(pose.GravityOrientation));
    /// <summary>The saved refined body applied to freshly decoded predictions, or the predictions unchanged when there is none.</summary>
    public static GvhmrDecoder.Pose Load(GvhmrDecoder.Pose pose,Saved? saved)
    {
        if(saved is null)return pose;
        if(saved.Version!=Version||saved.BodyRotations.Length!=pose.Frames*84||saved.CameraOrientation.Length!=pose.Frames*4||saved.GravityOrientation.Length!=pose.Frames*4)
            throw new InvalidDataException("The saved motion refinement does not match these predictions.");
        return pose with{BodyRotations=Unflat(saved.BodyRotations),CameraOrientation=Unflat(saved.CameraOrientation),GravityOrientation=Unflat(saved.GravityOrientation)};
    }
    public static string Note(Result refined)=>FormattableString.Invariant(
        $"Motion refined against the video (HTD-Refine): PVA-Net read each joint's position, speed and acceleration from the picture on the graphics card, and the body was fitted to them in {refined.Seconds:F1} s (fit error {refined.StartLoss:F0} to {refined.EndLoss:F0}). This follows the video more closely and removes shake without smoothing away fast moves.");

    /// <summary>Dev check against reference joint files: the body model, then the whole fit, on a capture folder's decoded GVHMR output.</summary>
    public static void Check(string models,string folder,string targetsPath,string reference,Action<string> report)
    {
        using var predictionJson=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"raw-predictions.json")));var pr=predictionJson.RootElement;
        var frames=pr.GetProperty("Frames").GetInt32();
        var pose=GvhmrDecoder.Decode(pr.GetProperty("PredX").EnumerateArray().Select(x=>x.GetSingle()).ToArray(),frames);
        using var reconstructionJson=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"reconstruction.json")));
        var boxes=reconstructionJson.RootElement.GetProperty("Frames").EnumerateArray().Select(f=>{var c=f.GetProperty("Person").GetProperty("Crop");return new GvhmrDecoder.Box(c.GetProperty("CenterX").GetSingle(),c.GetProperty("CenterY").GetSingle(),c.GetProperty("Size").GetSingle());}).ToArray();
        using var motionJson=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"raw-body.hmotion")));
        var k=motionJson.RootElement.GetProperty("cameras")[0].GetProperty("intrinsics").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        var camera=new GvhmrDecoder.Camera(k[0],k[2],k[5]);
        var translation=GvhmrDecoder.CameraTranslation(pr.GetProperty("PredCam").EnumerateArray().Select(x=>x.GetSingle()).ToArray(),boxes,Enumerable.Repeat(camera,frames).ToArray());
        using var targetJson=System.Text.Json.JsonDocument.Parse(File.ReadAllText(targetsPath));float[] Array(string n)=>targetJson.RootElement.GetProperty(n).EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        var targets=new PvaNet.Targets(Array("keypoints"),Array("velocity"),Array("acceleration"));
        var smplx=Path.Combine(models,"smplx/SMPLX_NEUTRAL.npz");
        float[] ReadBin(string name){var bytes=File.ReadAllBytes(Path.Combine(reference,name));var f=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,f,0,bytes.Length);return f;}
        string Compare(float[] a,float[] b){var d=new List<double>();for(var i=0;i<a.Length;i+=3)d.Add(Math.Sqrt(Math.Pow(a[i]-b[i],2)+Math.Pow(a[i+1]-b[i+1],2)+Math.Pow(a[i+2]-b[i+2],2))*1000);d.Sort();return $"median {d[d.Count/2]:F1} mm, p95 {d[(int)(d.Count*.95)]:F1} mm, max {d[^1]:F1} mm";}
        report("Body model vs reference, GVHMR input: "+Compare(CocoJoints(smplx,pose,pose.BodyRotations,pose.CameraOrientation,translation),ReadBin("htd_ref_joints0.bin")));
        var result=Refine(smplx,pose,translation,targets,camera,30,CancellationToken.None);
        report($"Fit: loss {result.StartLoss:F1} -> {result.EndLoss:F1} in {result.Seconds:F1} s ({result.ModelSeconds:F1} s loading the body model)");
        var refined=CocoJoints(smplx,pose,result.BodyRotations,result.Orientation,result.Translation);
        report("Refined vs reference refined: "+Compare(refined,ReadBin("htd_ref_joints1.bin")));
        report("Reference refined vs its input: "+Compare(ReadBin("htd_ref_joints1.bin"),ReadBin("htd_ref_joints0.bin")));
    }
    /// <summary>Dev check: the COCO joints (camera space, metres, [frames,17,3]) this model gives for a pose.</summary>
    public static float[] CocoJoints(string smplxPath,GvhmrDecoder.Pose pose,Quaternion[] body,Quaternion[] orientation,Vector3[] translation)
    {
        var frames=orientation.Length;var betas=new double[10];for(var t=0;t<frames;t++)for(var b=0;b<10;b++)betas[b]+=pose.Betas[t*10+b]/(double)frames;
        var model=Attachments.Load(smplxPath,betas,CancellationToken.None);var result=new float[frames*Points*3];var frame=new Frame();var points=new Vector3d[Points];
        for(var t=0;t<frames;t++)
        {
            var p=new double[69];Put(p,0,AxisAngle(orientation[t]));for(var j=0;j<21;j++)Put(p,3+j*3,AxisAngle(body[t*21+j]));p[66]=translation[t].X;p[67]=translation[t].Y;p[68]=translation[t].Z;
            frame.Forward(p,0,model,points);for(var k=0;k<Points;k++){result[(t*Points+k)*3]=(float)points[k].X;result[(t*Points+k)*3+1]=(float)points[k].Y;result[(t*Points+k)*3+2]=(float)points[k].Z;}
        }
        return result;
    }

    /// <summary>The fit's data terms and their gradients with respect to every COCO joint position (camera space, metres).</summary>
    static double Loss(Vector3d[] k,Vector3d[] g,PvaNet.Targets targets,double[] confidence,int frames,double dt,GvhmrDecoder.Camera camera,double resolution)
    {
        double loss=0;
        Vector3d Target(float[] source,int index)=>new(source[index],source[index+1],source[index+2]);
        // Velocity (x1) and acceleration (x0.1) of each joint against the network's, weighted per joint and by confidence,
        // averaged over the residuals within the reference's outlier limits.
        foreach(var acceleration in new[]{false,true})
        {
            var span=acceleration?frames-2:frames-1;var scale=acceleration?.15:.5;var limits=acceleration?accelerationLimit:velocityLimit;var weight=acceleration?.1:1;
            var residual=new Vector3d[span*Points];var valid=0;
            for(var t=0;t<span;t++)for(var j=0;j<Points;j++)
            {
                var r=acceleration?(k[(t+2)*Points+j]-2*k[(t+1)*Points+j]+k[t*Points+j])/(dt*dt):(k[(t+1)*Points+j]-k[t*Points+j])/dt;
                r-=Target(acceleration?targets.Acceleration:targets.Velocity,(t*Points+j)*3);
                if(r.Length()<=limits[j]){residual[t*Points+j]=r;valid++;}else residual[t*Points+j]=new(double.NaN,0,0);
            }
            var count=Math.Max(1,valid);
            for(var t=0;t<span;t++)for(var j=0;j<Points;j++)
            {
                var r=residual[t*Points+j];if(double.IsNaN(r.X))continue;
                var c=Math.Min(confidence[t*Points+j],confidence[(t+1)*Points+j]);if(acceleration)c=Math.Min(c,confidence[(t+2)*Points+j]);
                var w=vertexBase[j]*scale*c;loss+=weight*w*r.Dot(r)/count;
                var d=r*(weight*2*w/count);
                if(acceleration){d/=dt*dt;g[(t+2)*Points+j]+=d;g[(t+1)*Points+j]-=2*d;g[t*Points+j]+=d;}
                else{d/=dt;g[(t+1)*Points+j]+=d;g[t*Points+j]-=d;}
            }
        }
        // Projection onto the picture against the network's 2D joints (x1), scaled to a 1280x960 picture.
        var elements=frames*Points*2.0;
        for(var t=0;t<frames;t++)for(var j=0;j<Points;j++)
        {
            var x=k[t*Points+j];var z=Math.Max(x.Z,1e-4);var i=(t*Points+j)*3;
            var u=camera.FocalLength*x.X/z+camera.CenterX-targets.Keypoints[i];var w=camera.FocalLength*x.Y/z+camera.CenterY-targets.Keypoints[i+1];
            var c=confidence[t*Points+j]*resolution/elements;loss+=c*(u*u+w*w);
            var du=2*c*u;var dw=2*c*w;
            g[t*Points+j]+=new Vector3d(du*camera.FocalLength/z,dw*camera.FocalLength/z,x.Z>1e-4?-(du*camera.FocalLength*x.X+dw*camera.FocalLength*x.Y)/(z*z):0);
        }
        // Jerk (x1e4): the length of each joint's third difference, averaged.
        var jerks=(frames-3)*Points;
        for(var t=0;t+3<frames;t++)for(var j=0;j<Points;j++)
        {
            var jerk=k[(t+3)*Points+j]-3*k[(t+2)*Points+j]+3*k[(t+1)*Points+j]-k[t*Points+j];var length=jerk.Length();
            loss+=1e4*length/jerks;if(length<1e-12)continue;
            var d=jerk*(1e4/(jerks*length));
            g[(t+3)*Points+j]+=d;g[(t+2)*Points+j]-=3*d;g[(t+1)*Points+j]+=3*d;g[t*Points+j]-=d;
        }
        return loss;
    }

    /// <summary>Each COCO joint as Σ_j (share_j · P_j + W_j · offset_j) over the 22 body joints: the posed joint positions P
    /// and rotations W, with shares summing to one.</summary>
    sealed class Attachments
    {
        public readonly Vector3d[] Rest=new Vector3d[Joints];
        public readonly double[] Share=new double[Points*Joints];public readonly Vector3d[] Offset=new Vector3d[Points*Joints];
        /// <summary>Per COCO joint, the body joints that carry some of it.</summary>
        public int[][] Carriers=System.Array.Empty<int[]>();
        // Joints in a child-before-parent order, for summing subtrees.
        public static Attachments Load(string path,double[] betas,CancellationToken cancellation)
        {
            if(!FileChecksum.Matches(path,SmplxSkeleton.NeutralSha256))throw new InvalidDataException("SMPL-X neutral model does not match the pinned version.");
            using var archive=ZipFile.OpenRead(path);
            var template=NumpyArray.Read(archive,"v_template.npy",cancellation).Values;var directions=NumpyArray.Read(archive,"shapedirs.npy",cancellation).Values;
            var regressor=NumpyArray.Read(archive,"J_regressor.npy",cancellation).Values;var weights=NumpyArray.Read(archive,"weights.npy",cancellation).Values;
            var tree=NumpyArray.Read(archive,"kintree_table.npy",cancellation).Values;var posedirs=NumpyArray.Read(archive,"posedirs.npy",cancellation).Values;
            var meanLeft=NumpyArray.Read(archive,"hands_meanl.npy",cancellation).Values;var meanRight=NumpyArray.Read(archive,"hands_meanr.npy",cancellation).Values;
            const int vertices=10475,all=55;
            Vector3d Shaped(int v)
            {
                var s=new Vector3d(template[v*3],template[v*3+1],template[v*3+2]);
                for(var b=0;b<10;b++)s+=new Vector3d(directions[(v*3)*400+b],directions[(v*3+1)*400+b],directions[(v*3+2)*400+b])*betas[b];
                return s;
            }
            var shaped=new Vector3d[vertices];for(var v=0;v<vertices;v++)shaped[v]=Shaped(v);
            var joints=new Vector3d[all];
            for(var j=0;j<all;j++)for(var v=0;v<vertices;v++){var c=regressor[j*vertices+v];if(c!=0)joints[j]+=shaped[v]*c;}
            var parent=new int[all];for(var j=0;j<all;j++)parent[j]=j==0?-1:(int)tree[j];
            // The reference's default pose: body, jaw and eyes at rest, both hands at the model's mean hand pose.
            var local=new Matrix3d[all];for(var j=0;j<all;j++)local[j]=Matrix3d.Identity;
            for(var j=0;j<15;j++){local[25+j]=Matrix3d.Exp(new(meanLeft[j*3],meanLeft[j*3+1],meanLeft[j*3+2]));local[40+j]=Matrix3d.Exp(new(meanRight[j*3],meanRight[j*3+1],meanRight[j*3+2]));}
            var rotation=new Matrix3d[all];var position=new Vector3d[all];
            for(var j=0;j<all;j++)
            {
                if(parent[j]<0){rotation[j]=local[j];position[j]=joints[j];continue;}
                rotation[j]=rotation[parent[j]]*local[j];position[j]=position[parent[j]]+rotation[parent[j]]*(joints[j]-joints[parent[j]]);
            }
            // The body joint each of the 55 moves with: itself, or its nearest body ancestor.
            var carrier=new int[all];for(var j=0;j<all;j++){var a=j;while(a>=Joints)a=parent[a];carrier[j]=a;}
            var result=new Attachments();for(var j=0;j<Joints;j++)result.Rest[j]=joints[j];
            foreach(var (point,vertex,coefficient) in Coco17)
            {
                // Pose correctives of the mean hands; the body's own are left out.
                var rest=shaped[vertex];
                for(var j=1;j<all;j++)
                {
                    if(j<Joints||j is 22 or 23 or 24)continue;
                    var r=local[j];var feature=new[]{r.M11-1,r.M12,r.M13,r.M21,r.M22-1,r.M23,r.M31,r.M32,r.M33-1};
                    for(var n=0;n<9;n++){var index=(j-1)*9+n;if(feature[n]==0)continue;rest+=new Vector3d(posedirs[(vertex*3)*486+index],posedirs[(vertex*3+1)*486+index],posedirs[(vertex*3+2)*486+index])*feature[n];}
                }
                for(var j=0;j<all;j++)
                {
                    var w=weights[vertex*all+j];if(w==0)continue;
                    // The vertex as joint j carries it in the default pose, relative to the body joint that carries j.
                    var carried=rotation[j]*(rest-joints[j])+position[j];var body=carrier[j];
                    result.Share[point*Joints+body]+=coefficient*w;result.Offset[point*Joints+body]+=(carried-joints[body])*(coefficient*w);
                }
            }
            result.Carriers=Enumerable.Range(0,Points).Select(k=>Enumerable.Range(0,Joints).Where(j=>result.Share[k*Joints+j]!=0||result.Offset[k*Joints+j]!=Vector3d.Zero).ToArray()).ToArray();
            return result;
        }
    }

    /// <summary>One frame's posed skeleton and the working values its gradient needs.</summary>
    sealed class Frame
    {
        readonly Matrix3d[] local=new Matrix3d[Joints],rotation=new Matrix3d[Joints];readonly Vector3d[] position=new Vector3d[Joints];
        readonly Vector3d[] torque=new Vector3d[Joints],force=new Vector3d[Joints];
        public void Forward(double[] p,int at,Attachments model,Span<Vector3d> points)
        {
            for(var j=0;j<Joints;j++)local[j]=Matrix3d.Exp(new(p[at+j*3],p[at+j*3+1],p[at+j*3+2]));
            var translation=new Vector3d(p[at+66],p[at+67],p[at+68]);
            for(var j=0;j<Joints;j++)
            {
                if(j==0){rotation[0]=local[0];position[0]=model.Rest[0]+translation;continue;}
                var q=parents[j];rotation[j]=rotation[q]*local[j];position[j]=position[q]+rotation[q]*(model.Rest[j]-model.Rest[q]);
            }
            for(var k=0;k<Points;k++)
            {
                var sum=Vector3d.Zero;
                foreach(var j in model.Carriers[k])sum+=position[j]*model.Share[k*Joints+j]+rotation[j]*model.Offset[k*Joints+j];
                points[k]=sum;
            }
        }
        /// <summary>Adds to <paramref name="gradient"/> (69 values) the loss's gradient through this frame's joints, given its
        /// gradient at each COCO joint. A rotation ω (world axes) at joint i moves everything it carries by ω × (x - P_i).</summary>
        public void Backward(double[] p,int at,Attachments model,ReadOnlySpan<Vector3d> pointGradient,Span<double> gradient)
        {
            var total=Vector3d.Zero;
            for(var j=0;j<Joints;j++){torque[j]=Vector3d.Zero;force[j]=Vector3d.Zero;}
            for(var k=0;k<Points;k++)
            {
                var g=pointGradient[k];total+=g;
                foreach(var j in model.Carriers[k])
                {
                    var s=model.Share[k*Joints+j];
                    var carried=position[j]*s+rotation[j]*model.Offset[k*Joints+j];
                    torque[j]+=carried.Cross(g);force[j]+=g*s;
                }
            }
            // Subtree sums, children before parents (every child has a higher index).
            for(var j=Joints-1;j>0;j--){torque[parents[j]]+=torque[j];force[parents[j]]+=force[j];}
            gradient[66]+=total.X;gradient[67]+=total.Y;gradient[68]+=total.Z;
            for(var j=0;j<Joints;j++)
            {
                var worldTorque=torque[j]-position[j].Cross(force[j]);
                var a=new Vector3d(p[at+j*3],p[at+j*3+1],p[at+j*3+2]);
                var d=Matrix3d.RightJacobian(a).TransposeTimes(rotation[j].TransposeTimes(worldTorque));
                gradient[j*3]+=d.X;gradient[j*3+1]+=d.Y;gradient[j*3+2]+=d.Z;
            }
        }
    }

    static void Put(double[] p,int at,Vector3d v){p[at]=v.X;p[at+1]=v.Y;p[at+2]=v.Z;}
    static Vector3d AxisAngle(Quaternion q)
    {
        q=Quaternion.Normalize(q);if(q.W<0)q=-q;var s=Math.Sqrt(Math.Max(0,1-(double)q.W*q.W));var angle=2*Math.Atan2(s,q.W);
        return s<1e-9?new Vector3d(2*q.X,2*q.Y,2*q.Z):new Vector3d(q.X/s*angle,q.Y/s*angle,q.Z/s*angle);
    }
    static Quaternion FromAxisAngle(double[] p,int at)
    {
        var a=new Vector3d(p[at],p[at+1],p[at+2]);var angle=a.Length();if(angle<1e-12)return Quaternion.Identity;
        var axis=a/angle;var s=Math.Sin(angle/2);
        return Quaternion.Normalize(new Quaternion((float)(axis.X*s),(float)(axis.Y*s),(float)(axis.Z*s),(float)Math.Cos(angle/2)));
    }

    readonly record struct Vector3d(double X,double Y,double Z)
    {
        public static readonly Vector3d Zero=new(0,0,0);
        public static Vector3d operator+(Vector3d a,Vector3d b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Vector3d operator-(Vector3d a,Vector3d b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Vector3d operator*(Vector3d a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
        public static Vector3d operator*(double s,Vector3d a)=>new(a.X*s,a.Y*s,a.Z*s);
        public static Vector3d operator/(Vector3d a,double s)=>new(a.X/s,a.Y/s,a.Z/s);
        public double Dot(Vector3d b)=>X*b.X+Y*b.Y+Z*b.Z;
        public Vector3d Cross(Vector3d b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public double Length()=>Math.Sqrt(Dot(this));
    }
    readonly record struct Matrix3d(double M11,double M12,double M13,double M21,double M22,double M23,double M31,double M32,double M33)
    {
        public static readonly Matrix3d Identity=new(1,0,0,0,1,0,0,0,1);
        public static Matrix3d operator*(Matrix3d a,Matrix3d b)=>new(
            a.M11*b.M11+a.M12*b.M21+a.M13*b.M31,a.M11*b.M12+a.M12*b.M22+a.M13*b.M32,a.M11*b.M13+a.M12*b.M23+a.M13*b.M33,
            a.M21*b.M11+a.M22*b.M21+a.M23*b.M31,a.M21*b.M12+a.M22*b.M22+a.M23*b.M32,a.M21*b.M13+a.M22*b.M23+a.M23*b.M33,
            a.M31*b.M11+a.M32*b.M21+a.M33*b.M31,a.M31*b.M12+a.M32*b.M22+a.M33*b.M32,a.M31*b.M13+a.M32*b.M23+a.M33*b.M33);
        public static Vector3d operator*(Matrix3d a,Vector3d v)=>new(a.M11*v.X+a.M12*v.Y+a.M13*v.Z,a.M21*v.X+a.M22*v.Y+a.M23*v.Z,a.M31*v.X+a.M32*v.Y+a.M33*v.Z);
        public Vector3d TransposeTimes(Vector3d v)=>new(M11*v.X+M21*v.Y+M31*v.Z,M12*v.X+M22*v.Y+M32*v.Z,M13*v.X+M23*v.Y+M33*v.Z);
        static Matrix3d Skew(Vector3d a)=>new(0,-a.Z,a.Y,a.Z,0,-a.X,-a.Y,a.X,0);
        static Matrix3d Add(Matrix3d a,Matrix3d b,double s)=>new(a.M11+s*b.M11,a.M12+s*b.M12,a.M13+s*b.M13,a.M21+s*b.M21,a.M22+s*b.M22,a.M23+s*b.M23,a.M31+s*b.M31,a.M32+s*b.M32,a.M33+s*b.M33);
        /// <summary>Rodrigues: the rotation by |a| about a.</summary>
        public static Matrix3d Exp(Vector3d a)
        {
            var angle=a.Length();var k=Skew(a);var k2=k*k;
            if(angle<1e-8)return Add(Add(Identity,k,1),k2,.5);
            return Add(Add(Identity,k,Math.Sin(angle)/angle),k2,(1-Math.Cos(angle))/(angle*angle));
        }
        /// <summary>exp(a + δ) ≈ exp(a) exp(J_r(a) δ).</summary>
        public static Matrix3d RightJacobian(Vector3d a)
        {
            var angle=a.Length();var k=Skew(a);var k2=k*k;
            if(angle<1e-6)return Add(Add(Identity,k,-.5),k2,1.0/6);
            return Add(Add(Identity,k,-(1-Math.Cos(angle))/(angle*angle)),k2,(angle-Math.Sin(angle))/(angle*angle*angle));
        }
    }

    // (COCO joint, SMPL-X vertex, weight): HTD-Refine's COCO17 regressor for SMPL composed with its SMPL-X-to-SMPL map.
    static readonly (int Point,int Vertex,float Weight)[] Coco17={
        (0,9007,0.0334710553f),(0,9120,0.928839207f),(0,9278,0.0376897603f),(1,878,0.152663052f),(1,879,0.324646771f),(1,884,0.522690177f),(2,2290,0.146285817f),(2,2291,0.564109921f),
        (2,2296,0.289604217f),(3,239,0.505871236f),(3,258,0.293636858f),(3,316,0.20049192f),(4,599,0.428769797f),(4,602,0.0128554832f),(4,1048,0.558374703f),(5,3264,0.0483188331f),
        (5,4477,0.0480864756f),(5,5541,0.061613027f),(5,5544,0.20117566f),(5,5607,0.0788006335f),(5,5626,0.39402923f),(5,8130,0.167956159f),(6,5936,0.149437994f),(6,6027,0.00695912866f),
        (6,7198,0.0023597097f),(6,8254,0.00388734299f),(6,8257,0.391246229f),(6,8309,0.0129988836f),(6,8319,0.433090925f),(7,4196,0.091592595f),(7,4284,0.143284842f),(7,4287,0.119102918f),
        (7,4290,0.244213611f),(7,4293,0.14927116f),(7,4339,0.101916984f),(7,4363,0.15059787f),(8,6995,0.13146016f),(8,7021,0.109889321f),(8,7025,0.103431933f),(8,7030,0.100911632f),
        (8,7032,0.158548787f),(8,7033,0.12992087f),(8,7099,0.11281912f),(8,7254,0.152998149f),(9,4715,0.0824394971f),(9,4718,0.083925955f),(9,4719,0.0835337266f),(9,4720,0.107835755f),
        (9,4723,0.0938445479f),(9,4762,0.0855457559f),(9,4820,0.142879963f),(9,4844,0.150213525f),(9,4855,0.169761285f),(10,7422,0.0942604095f),(10,7439,0.113342047f),(10,7451,0.106954843f),
        (10,7456,0.263576329f),(10,7459,0.0950605348f),(10,7498,0.116865486f),(10,7532,0.0984688997f),(10,7580,0.111451469f),(11,3469,0.00490023755f),(11,3512,-1.2394921e-18f),(11,3513,0.00186069286f),
        (11,3514,3.2478976e-08f),(11,3770,0.0475626811f),(11,3840,0.00793842971f),(11,3841,0.00145554589f),(11,3842,0.00176396093f),(11,3972,0.359629601f),(11,4002,0.000989845605f),(11,4419,0.00200654124f),
        (11,5663,0.516982734f),(11,5666,0.0545736924f),(11,6707,0.00031685381f),(12,3465,0.000763437769f),(12,3482,8.66198058e-10f),(12,3903,1.70754877e-08f),(12,4146,0.000597353792f),(12,6202,0.499139965f),
        (12,6227,0.0017930991f),(12,6273,1.86663107e-18f),(12,6274,0.00280213752f),(12,6275,4.89121845e-08f),(12,6528,0.27056247f),(12,6531,9.67116375e-06f),(12,6720,0.192484006f),(12,6742,0.00402843114f),
        (12,8360,0.0184508413f),(12,8367,0.00934906956f),(13,3629,0.130496278f),(13,3636,0.177628368f),(13,3640,0.0606065094f),(13,3641,0.0580667183f),(13,3642,0.180695027f),(13,3643,0.1278788f),
        (13,3647,0.0472189374f),(13,3649,0.107116282f),(13,3676,0.110273123f),(14,6387,0.0781076476f),(14,6390,0.0852915272f),(14,6397,3.75100558e-06f),(14,6398,0.142134786f),(14,6400,0.145757124f),
        (14,6404,0.0672585368f),(14,6409,2.70091164e-06f),(14,6410,0.0830183923f),(14,6434,0.0844428241f),(14,6436,0.12072219f),(14,6437,0.0979798809f),(14,6453,0.0952588171f),(14,6458,1.84079693e-06f),
        (15,5762,1.16491996e-07f),(15,5773,8.75576386e-18f),(15,5798,0.0262878388f),(15,5808,4.63831157e-07f),(15,5879,0.110076599f),(15,5882,0.0652409121f),(15,5883,0.0954026282f),(15,5926,1.28087663e-06f),
        (15,8839,2.57614715e-06f),(15,8852,0.105695263f),(15,8892,0.24236457f),(15,8893,0.292309523f),(15,8935,0.0625981838f),(16,8462,0.012318911f),(16,8572,0.247041866f),(16,8577,0.12350034f),
        (16,8640,0.182057679f),(16,8664,0.118190862f),(16,8680,0.273270756f),(16,8717,0.0435995311f)
    };
}
