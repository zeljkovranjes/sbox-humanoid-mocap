#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace HumanoidMocap.Inference;
using FloatVector=System.Numerics.Vector<float>;

/// <summary>C# inference port of GVHMR NetworkEncoderRoPE at ee960bb6e2ea2d381aa97f08e9b71ef320b624b1.
/// This is the temporal network, not a complete video reconstruction backend. Callers must supply
/// real normalized COCO observations, CLIFF camera features, normalized camera angular velocity,
/// and HMR2 image features. Output still requires the upstream decoder and contact/IK processing.
/// Upstream copyright and terms: Gvhmr.LICENSE.</summary>
public sealed class GvhmrTemporalNetwork
{
    public const string CheckpointSha256="4fae7da2de388d5da3514cb27a2d003f364dacb280e9cf88972b710e589c6b91";
    public const int MaximumFrames=1800;
    const int Width=512,Heads=8,HeadWidth=64,AttentionLength=120,Layers=12;
    const string Prefix="pipeline.denoiser3d.";
    sealed record Weight(int[] Shape,float[] Values);
    readonly Dictionary<string,Weight> weights=new(StringComparer.Ordinal);
    int running;
    public int Threads { get; set; } = 4;
    public sealed record Output(int Frames,float[] PredX,float[] PredCam,float[] StaticConfidenceLogits,float[] Context);

    public GvhmrTemporalNetwork(string checkpointPath,CancellationToken cancellation=default)
    {
        using(var file=File.OpenRead(checkpointPath))
            if(!string.Equals(Convert.ToHexString(SHA256.HashData(file)),CheckpointSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GVHMR checkpoint does not match the pinned SIGA24 release.");
        using var checkpoint=new TorchCheckpoint(checkpointPath);
        foreach(var pair in checkpoint.Tensors)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!pair.Key.StartsWith(Prefix,StringComparison.Ordinal))continue;
            var data=checkpoint.ReadFloat(pair.Key,cancellation);
            if(data.Any(x=>!float.IsFinite(x)))throw new InvalidDataException("Non-finite GVHMR weights.");
            weights.Add(pair.Key.Substring(Prefix.Length),new(pair.Value.Shape,data));
        }
        if(weights.Count!=247)throw new InvalidDataException("Unexpected GVHMR architecture/tensor count.");
    }
    float[] Get(string name,params int[] expected)
    {
        if(!weights.TryGetValue(name,out var weight)||!weight.Shape.SequenceEqual(expected))
            throw new InvalidDataException("GVHMR weight shape mismatch: "+name);
        return weight.Values;
    }
    static void Input(float[] values,int expected,string name)
    {if(values is null||values.Length!=expected||values.Any(x=>!float.IsFinite(x)))throw new ArgumentException("Invalid GVHMR input: "+name);}

    /// <param name="trace">Optional numerical-verification callback. Data is borrowed and must
    /// be copied if retained. It is not a pose/capture confidence estimate.</param>
    public Output Run(int frames,float[] observations,float[] cliffCamera,float[] normalizedCameraAngularVelocity,float[] imageFeatures,
        CancellationToken cancellation=default,Action<string,ReadOnlyMemory<float>>? trace=null)
    {
        if(frames<1||frames>MaximumFrames)throw new ArgumentOutOfRangeException(nameof(frames));
        Input(observations,frames*17*3,nameof(observations));Input(cliffCamera,frames*3,nameof(cliffCamera));
        Input(normalizedCameraAngularVelocity,frames*6,nameof(normalizedCameraAngularVelocity));Input(imageFeatures,frames*1024,nameof(imageFeatures));
        if(Interlocked.Exchange(ref running,1)!=0)throw new InvalidOperationException("This GVHMR model already has an active inference job.");
        try
        {
            var parallel=new ParallelOptions{CancellationToken=cancellation,MaxDegreeOfParallelism=Math.Clamp(Threads,1,8)};
            var observationFeatures=new float[frames*17*32];
            var positionWeight=Get("learned_pos_linear.weight",32,2);var positionBias=Get("learned_pos_linear.bias",32);
            var missing=Get("learned_pos_params",17,32);
            for(var t=0;t<frames;t++)for(var joint=0;joint<17;joint++)
            {
                var input=(t*17+joint)*3;var output=(t*17+joint)*32;var visible=observations[input+2]>.5f;
                for(var feature=0;feature<32;feature++)observationFeatures[output+feature]=visible
                    ?observations[input]*positionWeight[feature*2]+observations[input+1]*positionWeight[feature*2+1]+positionBias[feature]
                    :missing[joint*32+feature];
            }
            var x=Mlp(observationFeatures,frames,17*32,1024,Width,"embed_noisyobs",false,parallel);
            Add(x,Condition(cliffCamera,frames,3,"cliffcam_embedder",parallel));
            Add(x,Condition(normalizedCameraAngularVelocity,frames,6,"cam_angvel_embedder",parallel));
            Add(x,Linear(Normalize(imageFeatures,frames,1024,"imgseq_embedder.0",1e-5f,parallel),frames,1024,Width,"imgseq_embedder.1",parallel));
            trace?.Invoke("embedded",x);
            for(var block=0;block<Layers;block++)
            {
                cancellation.ThrowIfCancellationRequested();var name="blocks."+block;
                var norm=Normalize(x,frames,Width,name+".norm1",1e-6f,parallel);
                var attention=Attention(norm,frames,name+".attn",parallel);
                AddGated(x,attention,Get(name+".gate_msa",1,1,Width));
                norm=Normalize(x,frames,Width,name+".norm2",1e-6f,parallel);
                var mlp=Mlp(norm,frames,Width,Width*4,Width,name+".mlp",true,parallel);
                AddGated(x,mlp,Get(name+".gate_mlp",1,1,Width));trace?.Invoke(name,x);
            }
            var prediction=Mlp(x,frames,Width,Width,151,"final_layer",false,parallel);
            // The published network averages body shape over all valid frames, not per window.
            for(var beta=126;beta<136;beta++)
            {float sum=0;for(var t=0;t<frames;t++)sum+=prediction[t*151+beta];var average=sum/frames;for(var t=0;t<frames;t++)prediction[t*151+beta]=average;}
            var camera=Mlp(x,frames,Width,Width,3,"pred_cam_head",false,parallel);
            for(var t=0;t<frames;t++)
            {camera[t*3]=Math.Max(.25f,camera[t*3]*.1784f+1.0606f);camera[t*3+1]=camera[t*3+1]*.0956f-.0027f;camera[t*3+2]=camera[t*3+2]*.0764f+.2702f;}
            var contacts=Mlp(x,frames,Width,Width,6,"static_conf_head",false,parallel);
            if(prediction.Any(v=>!float.IsFinite(v))||camera.Any(v=>!float.IsFinite(v))||contacts.Any(v=>!float.IsFinite(v)))
                throw new ArithmeticException("Non-finite GVHMR prediction.");
            trace?.Invoke("pred_x",prediction);trace?.Invoke("pred_cam",camera);trace?.Invoke("static_conf_logits",contacts);
            return new(frames,prediction,camera,contacts,x);
        }
        finally{Volatile.Write(ref running,0);}
    }
    float[] Linear(float[] input,int rows,int inputWidth,int outputWidth,string name,ParallelOptions options)
    {
        var weight=Get(name+".weight",outputWidth,inputWidth);var bias=Get(name+".bias",outputWidth);var output=new float[rows*outputWidth];
        Parallel.For(0,rows,options,row=>
        {
            var start=row*inputWidth;
            for(var channel=0;channel<outputWidth;channel++)
                output[row*outputWidth+channel]=Dot(input,start,weight,channel*inputWidth,inputWidth)+bias[channel];
        });
        return output;
    }
    static float Dot(float[] a,int aOffset,float[] b,int bOffset,int count)
    {
        var sum=FloatVector.Zero;var i=0;
        for(;i<=count-FloatVector.Count;i+=FloatVector.Count)sum+=new FloatVector(a,aOffset+i)*new FloatVector(b,bOffset+i);
        var result=System.Numerics.Vector.Sum(sum);for(;i<count;i++)result+=a[aOffset+i]*b[bOffset+i];return result;
    }
    float[] Normalize(float[] input,int rows,int width,string name,float epsilon,ParallelOptions options)
    {
        var weight=Get(name+".weight",width);var bias=Get(name+".bias",width);var output=new float[input.Length];
        Parallel.For(0,rows,options,row=>
        {
            var at=row*width;double sum=0;for(var i=0;i<width;i++)sum+=input[at+i];var mean=sum/width;
            double variance=0;for(var i=0;i<width;i++){var delta=input[at+i]-mean;variance+=delta*delta;}
            var inverse=1/Math.Sqrt(variance/width+epsilon);
            for(var i=0;i<width;i++)output[at+i]=(float)((input[at+i]-mean)*inverse)*weight[i]+bias[i];
        });return output;
    }
    float[] Mlp(float[] input,int rows,int inputWidth,int hiddenWidth,int outputWidth,string name,bool approximate,ParallelOptions options)
    {
        var hidden=Linear(input,rows,inputWidth,hiddenWidth,name+".fc1",options);
        for(var i=0;i<hidden.Length;i++)
        {
            var v=hidden[i];hidden[i]=approximate?.5f*v*(1+MathF.Tanh(.7978845608028654f*(v+.044715f*v*v*v))):.5f*v*(1+Erf(v*.7071067811865475f));
        }
        return Linear(hidden,rows,hiddenWidth,outputWidth,name+".fc2",options);
    }
    float[] Condition(float[] input,int rows,int inputWidth,string name,ParallelOptions options)
    {
        var hidden=Linear(input,rows,inputWidth,Width,name+".0",options);
        for(var i=0;i<hidden.Length;i++)hidden[i]/=1+MathF.Exp(-hidden[i]);
        return Linear(hidden,rows,Width,Width,name+".3",options);
    }
    float[] Attention(float[] input,int frames,string name,ParallelOptions options)
    {
        var query=Linear(input,frames,Width,Width,name+".query",options);
        var key=Linear(input,frames,Width,Width,name+".key",options);
        var value=Linear(input,frames,Width,Width,name+".value",options);
        for(var t=0;t<frames;t++)for(var channel=0;channel<HeadWidth;channel+=2)
        {
            var angle=t/MathF.Pow(10000,channel/(float)HeadWidth);var cos=MathF.Cos(angle);var sin=MathF.Sin(angle);
            for(var head=0;head<Heads;head++)
            {var at=t*Width+head*HeadWidth+channel;Rotate(query,at,cos,sin);Rotate(key,at,cos,sin);}
        }
        var output=new float[input.Length];
        Parallel.For(0,frames,options,t=>
        {
            // This exactly implements the reference attention mask. It is not independent
            // chunk inference: every layer still sees all keys admitted by that frame's mask.
            var first=frames<=AttentionLength?0:Math.Min(frames-AttentionLength,Math.Max(0,t-AttentionLength/2));
            var last=frames<=AttentionLength?frames:Math.Max(AttentionLength,Math.Min(frames,t+AttentionLength/2));
            Span<float> scores=stackalloc float[AttentionLength];
            for(var head=0;head<Heads;head++)
            {
                var q=t*Width+head*HeadWidth;var maximum=float.NegativeInfinity;
                for(var j=first;j<last;j++)
                {var score=Dot(query,q,key,j*Width+head*HeadWidth,HeadWidth)*.125f;scores[j-first]=score;maximum=Math.Max(maximum,score);}
                float total=0;for(var j=first;j<last;j++){var score=MathF.Exp(scores[j-first]-maximum);scores[j-first]=score;total+=score;}
                for(var j=first;j<last;j++)
                {
                    var coefficient=scores[j-first]/total;var at=j*Width+head*HeadWidth;
                    for(var channel=0;channel<HeadWidth;channel++)output[q+channel]+=coefficient*value[at+channel];
                }
            }
        });
        return Linear(output,frames,Width,Width,name+".proj",options);
    }
    static void Rotate(float[] values,int offset,float cos,float sin)
    {var x=values[offset];var y=values[offset+1];values[offset]=x*cos-y*sin;values[offset+1]=y*cos+x*sin;}
    static void Add(float[] target,float[] delta){for(var i=0;i<target.Length;i++)target[i]+=delta[i];}
    static void AddGated(float[] target,float[] delta,float[] gate){for(var i=0;i<target.Length;i++)target[i]+=delta[i]*gate[i%Width];}
    static float Erf(float input)
    {
        // Abramowitz-Stegun 7.1.26; maximum absolute erf error < 1.5e-7. Retain exact-GELU
        // versus tanh-GELU distinction from the upstream heads versus transformer MLPs.
        var x=Math.Abs((double)input);var t=1/(1+.3275911*x);
        var result=1-(((((1.061405429*t-1.453152027)*t)+1.421413741)*t-.284496736)*t+.254829592)*t*Math.Exp(-x*x);
        return (float)(input<0?-result:result);
    }
}
