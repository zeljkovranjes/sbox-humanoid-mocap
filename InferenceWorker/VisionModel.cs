// ViT backbone adapted from OpenMMLab code in GVHMR.
// Copyright (c) OpenMMLab. All rights reserved. See ../Editor/HumanoidMocap/Inference/Gvhmr.LICENSE.
using System.Security.Cryptography;
using HumanoidMocap.Inference;
using TorchSharp;
using static TorchSharp.torch;
using F=TorchSharp.torch.nn.functional;

namespace HumanoidMocap.Worker;

/// <summary>C# inference for the exact HMR2 feature and ViTPose-H checkpoints used by GVHMR.
/// Direct native LibTorch CPU operators. No Python interpreter, generated script or Python IPC.
/// Source: GVHMR ee960bb6 network/hmr2 and utils/preproc/vitpose_pytorch.</summary>
public sealed class VisionModel : IDisposable
{
    public enum Kind{Hmr2Features,VitPoseHeatmaps}
    readonly Dictionary<string,Tensor> weights=new(StringComparer.Ordinal);
    readonly Kind kind;bool disposed;int running;
    public VisionModel(string checkpointPath,Kind kind,CancellationToken cancellation=default,Action<int,int>? loading=null)
    {
        this.kind=kind;
        var expected=kind==Kind.Hmr2Features?"2dcf79638109781d1ae5f5c44fee5f55bc83291c210653feead9b7f04fa6f20e":"50e33f4077ef2a6bcfd7110c58742b24c5859b7798fb0eedd6d2215e0a8980bc";
        using(var input=File.OpenRead(checkpointPath))if(!Convert.ToHexString(SHA256.HashData(input)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Vision checkpoint checksum mismatch.");
        using var checkpoint=new TorchCheckpoint(checkpointPath);
        var required=checkpoint.Tensors.Where(p=>p.Key.StartsWith("backbone.")||p.Key.StartsWith(kind==Kind.Hmr2Features?"smpl_head.transformer.":"keypoint_head.")).ToArray();
        try
        {
            var count=0;
            foreach(var (name,info) in required)
            {
                cancellation.ThrowIfCancellationRequested();
                var values=checkpoint.ReadFloat(name,cancellation);
                if(values.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Non-finite vision weights.");
                using var original=tensor(values);
                weights.Add(name,original.reshape(info.Shape.Select(v=>(long)v).ToArray()).DetachFromDisposeScope());
                loading?.Invoke(++count,required.Length);
            }
        }
        catch{Dispose();throw;}
    }
    Tensor Weight(string name)=>weights.TryGetValue(name,out var value)?value:throw new InvalidDataException("Missing vision weight: "+name);
    Tensor Linear(Tensor x,string name)=>F.linear(x,Weight(name+".weight"),weights.GetValueOrDefault(name+".bias"));
    Tensor Norm(Tensor x,string name,int width,double epsilon)=>F.layer_norm(x,new long[]{width},Weight(name+".weight"),Weight(name+".bias"),epsilon);
    /// <summary>RGB input normalized with ImageNet mean/std, channel-first 256x192.
    /// Caller owns image crop calibration and must preserve it when decoding observations.</summary>
    public float[] Run(float[] image,CancellationToken cancellation=default,Action<string>? progress=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(image.Length!=3*256*192||image.Any(v=>!float.IsFinite(v)))throw new ArgumentException("Vision input must be finite RGB CHW 256x192.");
        if(Interlocked.Exchange(ref running,1)!=0)throw new InvalidOperationException("Vision model already running.");
        try
        {
            using var noGrad=no_grad();using var scope=NewDisposeScope();
            var pixels=tensor(image).reshape(1,3,256,192);
            var x=F.conv2d(pixels,Weight("backbone.patch_embed.proj.weight"),Weight("backbone.patch_embed.proj.bias"),strides:new long[]{16,16});
            x=x.flatten(2).transpose(1,2);
            x=x+Weight("backbone.pos_embed").slice(1,1,193,1)+Weight("backbone.pos_embed").slice(1,0,1,1);
            for(var block=0;block<32;block++)
            {
                cancellation.ThrowIfCancellationRequested();using var blockScope=NewDisposeScope();
                var name="backbone.blocks."+block;var qkv=Linear(Norm(x,name+".norm1",1280,1e-6),name+".attn.qkv").reshape(1,192,3,16,80).permute(2,0,3,1,4);
                var q=qkv.select(0,0)*(float)(1/Math.Sqrt(80));var k=qkv.select(0,1);var v=qkv.select(0,2);
                var attention=q.matmul(k.transpose(-2,-1)).softmax(-1).matmul(v).transpose(1,2).reshape(1,192,1280);
                var residual=x+Linear(attention,name+".attn.proj");
                var mlp=Linear(F.gelu(Linear(Norm(residual,name+".norm2",1280,1e-6),name+".mlp.fc1")),name+".mlp.fc2");
                var previous=x;x=(residual+mlp).MoveToOuterDisposeScope();previous.Dispose();progress?.Invoke($"Vision transformer {block+1}/32");
            }
            x=Norm(x,"backbone.last_norm",1280,1e-6);
            var output=kind==Kind.Hmr2Features?FeatureHead(x,cancellation):HeatmapHead(x);
            cancellation.ThrowIfCancellationRequested();
            var result=output.contiguous().data<float>().ToArray();
            if(result.Any(v=>!float.IsFinite(v)))throw new ArithmeticException("Non-finite vision prediction.");
            return result;
        }
        finally{Volatile.Write(ref running,0);}
    }
    Tensor FeatureHead(Tensor context,CancellationToken cancellation)
    {
        const string prefix="smpl_head.transformer.";
        var x=Linear(zeros(1,1,1),prefix+"to_token_embedding")+Weight(prefix+"pos_embedding");
        for(var block=0;block<6;block++)
        {
            cancellation.ThrowIfCancellationRequested();using var layer=NewDisposeScope();var name=prefix+"transformer.layers."+block;
            var qkv=Linear(Norm(x,name+".0.norm",1024,1e-5),name+".0.fn.to_qkv");
            Tensor SelfPart(int part)=>qkv.slice(-1,part*512,(part+1)*512,1).reshape(1,1,8,64).transpose(1,2);
            var q=SelfPart(0);var k=SelfPart(1);var v=SelfPart(2);
            var attention=(q.matmul(k.transpose(-2,-1))*.125f).softmax(-1).matmul(v).transpose(1,2).reshape(1,1,512);
            var sa=x+Linear(attention,name+".0.fn.to_out.0");
            q=Linear(Norm(sa,name+".1.norm",1024,1e-5),name+".1.fn.to_q").reshape(1,1,8,64).transpose(1,2);
            var kv=Linear(context,name+".1.fn.to_kv");
            k=kv.slice(-1,0,512,1).reshape(1,192,8,64).transpose(1,2);
            v=kv.slice(-1,512,1024,1).reshape(1,192,8,64).transpose(1,2);
            attention=(q.matmul(k.transpose(-2,-1))*.125f).softmax(-1).matmul(v).transpose(1,2).reshape(1,1,512);
            var ca=sa+Linear(attention,name+".1.fn.to_out.0");
            var mlp=Linear(F.gelu(Linear(Norm(ca,name+".2.norm",1024,1e-5),name+".2.fn.net.0")),name+".2.fn.net.3");
            var previous=x;x=(ca+mlp).MoveToOuterDisposeScope();previous.Dispose();
        }
        return x.reshape(1024);
    }
    Tensor HeatmapHead(Tensor tokens)
    {
        var x=tokens.transpose(1,2).reshape(1,1280,16,12);
        for(var block=0;block<2;block++)
        {
            var name="keypoint_head.deconv_layers.";
            x=F.conv_transpose2d(x,Weight(name+(block*3)+".weight"),strides:new long[]{2,2},padding:new long[]{1,1});
            var bn=name+(block*3+1);
            x=F.batch_norm(x,Weight(bn+".running_mean"),Weight(bn+".running_var"),Weight(bn+".weight"),Weight(bn+".bias"),training:false,eps:1e-5);
            x=F.relu(x);
        }
        return F.conv2d(x,Weight("keypoint_head.final_layer.weight"),Weight("keypoint_head.final_layer.bias"));
    }
    public void Dispose(){if(disposed)return;disposed=true;foreach(var weight in weights.Values)weight.Dispose();weights.Clear();}
}
