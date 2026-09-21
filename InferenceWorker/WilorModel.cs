// WiLoR architecture: rolpotamias/WiLoR fcb911312a38fa8badd30d9656a167485d61b8f9.
// Full-depth float32 inference; optional upstream depth pruning is deliberately disabled.
using HumanoidMocap.Inference;
using TorchSharp;
using static TorchSharp.torch;
using F=TorchSharp.torch.nn.functional;

namespace HumanoidMocap.Worker;

/// <summary>WiLoR's hand-token ViT-H and vertex-sampled refinement network.
/// The caller owns hand detection, crop transforms and left-hand reflection.</summary>
public sealed class WilorModel : IDisposable
{
    public const string CheckpointSha256="3e97aafc7dd08d883a4cc5a027df61fdb6fda6136dbd1319405413862ada6bb2";
    readonly HandModelWeights weights;
    readonly ManoDecoder mano;
    int running;bool disposed;
    public const string Float32="float32",BFloat16="bfloat16";
    /// <summary>Numeric type of the 32 transformer blocks. Everything else stays float32.</summary>
    public string Precision { get; }
    static string? measured;
    /// <summary>bfloat16 matrix products are more than twice as fast as float32 on processors
    /// with native support (AVX-512 BF16, AMX) and far slower where they are emulated, so the
    /// choice is timed on this machine with one block's MLP shapes. HUMANOID_MOCAP_PRECISION
    /// (float32 or bfloat16) overrides it. bfloat16 must win by 30% to be chosen.</summary>
    public static string ChoosePrecision()
    {
        var requested=Environment.GetEnvironmentVariable("HUMANOID_MOCAP_PRECISION");
        if(requested is Float32 or BFloat16)return requested;
        if(measured is not null)return measured;
        using var noGrad=no_grad();using var scope=NewDisposeScope();
        double Time(ScalarType type)
        {
            try
            {
                var x=randn(1,210,1280).to(type);var up=randn(5120,1280).to(type);var down=randn(1280,5120).to(type);
                for(var i=0;i<2;i++)F.linear(F.linear(x,up),down).Dispose();
                var clock=System.Diagnostics.Stopwatch.StartNew();
                for(var i=0;i<6;i++)F.linear(F.linear(x,up),down).Dispose();
                return clock.Elapsed.TotalSeconds;
            }
            catch(Exception){return double.PositiveInfinity;}
        }
        return measured=Time(ScalarType.BFloat16)<Time(ScalarType.Float32)*.7?BFloat16:Float32;
    }
    public sealed record Prediction(float[] RotationMatrices,float[] Shape,float[] WeakCamera,ManoDecoder.DecodedHand Hand);
    public WilorModel(string checkpointPath,CancellationToken cancellation=default,string? precision=null)
    {
        Precision=precision??ChoosePrecision();
        if(Precision is not (Float32 or BFloat16))throw new ArgumentException("Unsupported WiLoR precision.");
        static bool BlockMatrix(string name)=>name.StartsWith("backbone.blocks.")&&(name.Contains(".attn.qkv.")||name.Contains(".attn.proj.")||name.Contains(".mlp.fc1.")||name.Contains(".mlp.fc2."));
        weights=new(checkpointPath,CheckpointSha256,name=>name.StartsWith("backbone.")||name.StartsWith("refine_net."),cancellation,
            Precision==BFloat16?name=>BlockMatrix(name)?ScalarType.BFloat16:null:null);
        try{using var checkpoint=new TorchCheckpoint(checkpointPath);mano=new(checkpoint,"mano.");}
        catch{Dispose();throw;}
    }
    Tensor BlockLinear(Tensor input,string name)=>weights.Linear(Precision==Float32?input:input.to(ScalarType.BFloat16),name);
    /// <summary>RGB ImageNet-normalized CHW 256x192, from the central columns of
    /// the 256x256 hand crop. Left hands must be horizontally flipped before this call.</summary>
    public Prediction Run(float[] image,CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(image.Length!=3*256*192||image.Any(v=>!float.IsFinite(v)))throw new ArgumentException("Invalid WiLoR image.");
        if(Interlocked.Exchange(ref running,1)!=0)throw new InvalidOperationException("WiLoR is already processing a frame.");
        try
        {
            using var noGrad=no_grad();using var scope=NewDisposeScope();
            var x=weights.Conv(tensor(image).reshape(1,3,256,192),"backbone.patch_embed.proj",16).flatten(2).transpose(1,2);
            x=x+weights["backbone.pos_embed"].slice(1,1,193,1)+weights["backbone.pos_embed"].slice(1,0,1,1);
            var poseToken=weights.Linear(weights["backbone.init_hand_pose"].reshape(1,16,6),"backbone.pose_emb");
            var shapeToken=weights.Linear(weights["backbone.init_betas"],"backbone.shape_emb").unsqueeze(1);
            var cameraToken=weights.Linear(weights["backbone.init_cam"],"backbone.cam_emb").unsqueeze(1);
            x=cat(new[]{poseToken,shapeToken,cameraToken,x},1);
            for(var block=0;block<32;block++)
            {
                cancellation.ThrowIfCancellationRequested();using var layer=NewDisposeScope();
                var name="backbone.blocks."+block;
                // Layer norms, softmax and the residual stream stay float32 at either precision.
                var qkv=BlockLinear(weights.Norm(x,name+".norm1",1280),name+".attn.qkv").reshape(1,210,3,16,80).permute(2,0,3,1,4);
                var q=qkv.select(0,0)*(float)(1/Math.Sqrt(80));var k=qkv.select(0,1);var v=qkv.select(0,2);
                var attended=q.matmul(k.transpose(-2,-1)).to(ScalarType.Float32).softmax(-1).to(v.dtype).matmul(v).transpose(1,2).reshape(1,210,1280);
                var residual=x+BlockLinear(attended,name+".attn.proj").to(ScalarType.Float32);
                var feedforward=BlockLinear(F.gelu(BlockLinear(weights.Norm(residual,name+".norm2",1280),name+".mlp.fc1")),name+".mlp.fc2").to(ScalarType.Float32);
                var previous=x;x=(residual+feedforward).MoveToOuterDisposeScope();previous.Dispose();
            }
            x=weights.Norm(x,"backbone.last_norm",1280);
            var pose=weights.Linear(x.slice(1,0,16,1),"backbone.decpose").reshape(1,96)+weights["backbone.init_hand_pose"];
            var shape=weights.Linear(x.slice(1,16,17,1),"backbone.decshape").reshape(1,10)+weights["backbone.init_betas"];
            var camera=weights.Linear(x.slice(1,17,18,1),"backbone.deccam").reshape(1,3)+weights["backbone.init_cam"];
            var features=x.slice(1,18,210,1).transpose(1,2).reshape(1,1280,16,12);
            var preliminary=mano.Decode(HandModelWeights.Array(RotationMatrices(pose)),HandModelWeights.Array(shape),false,cancellation);
            var vertices=tensor(preliminary.Vertices.SelectMany(v=>new[]{v.X,v.Y,v.Z}).ToArray()).reshape(1,778,3);
            var refinement=Refine(features,vertices,camera,cancellation);
            pose=pose+weights.Linear(refinement,"refine_net.dec_pose");
            shape=shape+weights.Linear(refinement,"refine_net.dec_shape");
            camera=camera+weights.Linear(refinement,"refine_net.dec_cam");
            var rotations=HandModelWeights.Array(RotationMatrices(pose));var betas=HandModelWeights.Array(shape);var weakCamera=HandModelWeights.Array(camera);
            if(rotations.Concat(betas).Concat(weakCamera).Any(v=>!float.IsFinite(v)))throw new ArithmeticException("Non-finite WiLoR prediction.");
            return new(rotations,betas,weakCamera,mano.Decode(rotations,betas,false,cancellation));
        }
        finally{Volatile.Write(ref running,0);}
    }
    Tensor Refine(Tensor features,Tensor vertices,Tensor camera,CancellationToken cancellation)
    {
        var low=weights.Conv(features,"refine_net.deconv.first_conv.0");
        Tensor Upsample(Tensor input,string name)
        {
            var result=F.conv_transpose2d(input,weights[name+".0.weight"],strides:new long[]{2,2},padding:new long[]{1,1});
            return F.relu(weights.BatchNorm(result,name+".1"));
        }
        var middle=Upsample(low,"refine_net.deconv.deconv.0");
        var high=Upsample(low,"refine_net.deconv.deconv.1");
        high=F.conv_transpose2d(high,weights["refine_net.deconv.deconv.1.3.weight"],strides:new long[]{2,2},padding:new long[]{1,1});
        high=F.relu(weights.BatchNorm(high,"refine_net.deconv.deconv.1.4"));
        var samples=new List<Tensor>();
        foreach(var map in new[]{high,middle,low})
        {
            cancellation.ThrowIfCancellationRequested();var height=map.shape[2];var width=map.shape[3];
            const float focal=5000;
            var translation=stack(new[]{camera.select(1,1),camera.select(1,2),2*focal/(height*camera.select(1,0)+1e-9f)},1);
            var points=vertices+translation.unsqueeze(1);
            // Reproduce the released refinement projection and sampling exactly.
            var xy=points.slice(2,0,2,1)/points.slice(2,2,3,1)*(focal/height);
            var grid=stack(new[]{xy.select(2,0)/(width-1)*2-1,xy.select(2,1)/(height-1)*2-1},2).unsqueeze(2);
            var sampled=F.grid_sample(map,grid,align_corners:true);
            samples.Add(sampled.max(2).values.squeeze(2));
        }
        return cat(samples.ToArray(),1);
    }
    static Tensor RotationMatrices(Tensor pose)
    {
        // Released WiLoR geometry.py packs two contiguous columns; WildHands
        // consumes the same six-value grouping as rows instead.
        var six=pose.reshape(16,2,3).transpose(1,2);var a=six.select(2,0);var b=six.select(2,1);
        var first=F.normalize(a,dim:1);var second=F.normalize(b-(first*b).sum(1,keepdim:true)*first,dim:1);
        var third=stack(new[]{first.select(1,1)*second.select(1,2)-first.select(1,2)*second.select(1,1),
            first.select(1,2)*second.select(1,0)-first.select(1,0)*second.select(1,2),
            first.select(1,0)*second.select(1,1)-first.select(1,1)*second.select(1,0)},1);
        return stack(new[]{first,second,third},2);
    }
    public void Dispose(){if(disposed)return;disposed=true;weights.Dispose();}
}
