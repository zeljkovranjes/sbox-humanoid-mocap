using System.Diagnostics;
using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using Microsoft.ML.OnnxRuntime;

namespace HumanoidMocap.Worker;

/// <summary>The 32 transformer blocks and final norm shared by ViTPose-H, HMR2 and WiLoR, run on the
/// graphics card through ONNX Runtime and DirectML (any DirectX 12 GPU: AMD, NVIDIA or Intel). They are
/// over 90% of the capture's work: about 0.7 s per image on a desktop CPU, about 12 ms here on a
/// Radeon RX 9070. The patch embedding, extra tokens and every model head stay in TorchSharp.
/// The graph is assembled once from the downloaded checkpoint into a cache folder (see
/// <see cref="OnnxGraph"/>); nothing converted is ever distributed. Matrix products run in float16;
/// the residual stream, layer norms, softmax and GELU stay float32.</summary>
public sealed class GpuBackbone : IDisposable
{
    public const string BuilderVersion="vit-backbone-v3";
    /// <summary>What the graph takes and returns. <see cref="Tokens"/>: embedded tokens in, normalized tokens out (WiLoR,
    /// whose extra hand tokens are made in TorchSharp). <see cref="Hmr2Features"/>: a 256x192 image in, the 1024 HMR2 head
    /// features out. <see cref="Heatmaps"/>: a 256x192 image in, the 17 ViTPose joint heatmaps out.</summary>
    public enum Variant{Tokens,Hmr2Features,Heatmaps}
    /// <summary>A card needs this much dedicated memory; smaller or shared-memory GPUs are slower than the CPU path or run out.</summary>
    public const long MinimumDedicatedBytes=3L<<30;
    const int Width=1280,Blocks=32,Heads=16,HeadWidth=80;
    readonly InferenceSession session;readonly int inputLength;readonly long[] inputShape;readonly string inputName,outputName;
    public string Adapter { get; }

    static (int Index,string Name)? chosen;static bool probed;static int simulated;
    /// <summary>The GPU this process uses, or null for the CPU. HUMANOID_MOCAP_DEVICE=cpu forces the CPU.</summary>
    public static (int Index,string Name)? Device
    {
        get
        {
            if(probed)return chosen;probed=true;
            if(string.Equals(Environment.GetEnvironmentVariable("HUMANOID_MOCAP_DEVICE"),"cpu",StringComparison.OrdinalIgnoreCase))return null;
            try{chosen=Dxgi.LargestAdapter() is { } a&&a.DedicatedBytes>=MinimumDedicatedBytes?(a.Index,a.Name):null;}
            catch(Exception){chosen=null;}
            return chosen;
        }
    }
    static string? failure;
    /// <summary>Stops using the graphics card for the rest of this process after it failed mid-job.</summary>
    public static void Disable(string adapter,Exception error)
    {
        failure=$"{adapter}: {error.Message.Split('\n')[0].Trim()}";probed=true;chosen=null;
    }
    /// <summary>One line for a capture's notes saying where the transformer ran.</summary>
    public static string DeviceNote=>failure is not null?$"Vision transformers started on the graphics card and finished on the processor after it failed ({failure}).":Device is { } d?$"Vision transformers ran on the graphics card ({d.Name}, DirectML, float16 matrix products).":"Vision transformers ran on the processor; no DirectX 12 graphics card with 3 GB or more of its own memory was available, or HUMANOID_MOCAP_DEVICE=cpu was set.";
    /// <summary>Joins cache keys: GPU results differ from the CPU path in the last digits, so each keeps its own cache.</summary>
    public static string? KeySuffix=>Device is null?null:"directml-fp16-"+BuilderVersion;
    /// <summary>A GPU backbone for this checkpoint, or null when no suitable GPU is available or it fails its first run.
    /// A failure is reported once through <paramref name="report"/> and the caller falls back to the CPU.</summary>
    public static GpuBackbone? TryCreate(string checkpointPath,string checkpointSha256,Variant variant,int tokens,string cache,Action<string>? report,CancellationToken cancellation)
    {
        if(Device is not { } device)return null;
        try
        {
            var backbone=new GpuBackbone(checkpointPath,checkpointSha256,variant,tokens,cache,device,cancellation);
            var probe=new float[backbone.inputLength];var random=new Random(1);for(var i=0;i<probe.Length;i++)probe[i]=(float)(random.NextDouble()-.5);
            if(backbone.Run(probe).All(float.IsFinite))return backbone;
            backbone.Dispose();report?.Invoke($"The graphics card ({device.Name}) gave invalid results; using the processor instead");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error){report?.Invoke($"The graphics card ({device.Name}) could not be used ({error.Message.Split('\n')[0]}); using the processor instead");}
        chosen=null;return null;
    }
    GpuBackbone(string checkpointPath,string checkpointSha256,Variant variant,int tokens,string cache,(int Index,string Name) device,CancellationToken cancellation)
    {
        if(variant!=Variant.Tokens&&tokens!=192)throw new ArgumentException("Image input embeds 192 patches.");
        Adapter=device.Name;Directory.CreateDirectory(cache);
        inputShape=variant==Variant.Tokens?new long[]{1,tokens,Width}:new long[]{1,3,256,192};inputLength=(int)inputShape.Aggregate(1L,(a,b)=>a*b);
        inputName=variant==Variant.Tokens?"tokens":"image";outputName=variant==Variant.Heatmaps?"heatmaps":"features";
        var stem=$"vit-h-{checkpointSha256[..16].ToLowerInvariant()}-{variant.ToString().ToLowerInvariant()}-{tokens}t-fp16-{BuilderVersion}";var graphPath=Path.Combine(cache,stem+".onnx");
        if(!File.Exists(graphPath))Build(checkpointPath,cache,stem,variant,tokens,cancellation);
        using var options=new SessionOptions{GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL,EnableMemoryPattern=false,ExecutionMode=ExecutionMode.ORT_SEQUENTIAL,LogSeverityLevel=OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR};
        options.AppendExecutionProvider_DML(device.Index);
        session=new InferenceSession(graphPath,options);
    }
    /// <summary>Embedded tokens [tokens,1280] or a CHW 256x192 image, as the variant takes; returns normalized
    /// tokens [tokens,1280], HMR2 features [1024] or heatmaps [17,64,48].</summary>

    public float[] Run(float[] input)
    {
        if(input.Length!=inputLength)throw new ArgumentException("Unexpected backbone input size.");
        // Test hook: simulates the graphics driver failing after this many calls in the process.
        if(int.TryParse(Environment.GetEnvironmentVariable("HUMANOID_MOCAP_TEST_GPU_FAILURE"),out var failAfter)&&Interlocked.Increment(ref simulated)>failAfter)
            throw new InvalidOperationException("Simulated graphics-card failure.");
        using var value=OrtValue.CreateTensorValueFromMemory(input,inputShape);
        using var run=new RunOptions();
        using var outputs=session.Run(run,new[]{inputName},new[]{value},new[]{outputName});
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }
    public void Dispose()=>session.Dispose();

    static void Build(string checkpointPath,string cache,string stem,Variant variant,int tokens,CancellationToken cancellation)
    {
        using var checkpoint=new TorchCheckpoint(checkpointPath);
        var dataName=stem+".data";var dataPartial=Path.Combine(cache,dataName+".partial");
        var free=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cache))!);
        if(free.IsReady&&free.AvailableFreeSpace<(2L<<30))throw new IOException($"Not enough free space on {free.Name} to prepare the graphics-card model (about 1.3 GB).");
        using(var data=new FileStream(dataPartial,FileMode.Create,FileAccess.Write,FileShare.None,1<<20))
        {
            var graph=new OnnxGraph(data,dataName);
            string Store(string name,float[] values,long[] shape,bool half)
            {
                if(!half)return graph.Weight(name,OnnxGraph.Float,shape,MemoryMarshal.AsBytes(values.AsSpan()));
                var h=new ushort[values.Length];for(var i=0;i<values.Length;i++)h[i]=BitConverter.HalfToUInt16Bits((Half)values[i]);
                return graph.Weight(name,OnnxGraph.Float16,shape,MemoryMarshal.AsBytes(h.AsSpan()));
            }
            string Load(string name)
            {
                cancellation.ThrowIfCancellationRequested();
                return Store(name,checkpoint.ReadFloat(name,cancellation),checkpoint.Tensors[name].Shape.Select(v=>(long)v).ToArray(),false);
            }
            // Linear layers are stored [out,in]; MatMul takes [in,out], transposed once here.
            string Linear(string x,string name)
            {
                cancellation.ThrowIfCancellationRequested();
                var shape=checkpoint.Tensors[name+".weight"].Shape;int rows=shape[0],cols=shape[1];var w=checkpoint.ReadFloat(name+".weight",cancellation);
                var t=new float[w.Length];for(var r=0;r<rows;r++)for(var c=0;c<cols;c++)t[c*rows+r]=w[r*cols+c];
                var product=graph.Node("MatMul",new[]{x,Store(name+".weightT",t,new long[]{cols,rows},true)});
                return graph.Node("Add",new[]{product,Store(name+".bias",checkpoint.ReadFloat(name+".bias",cancellation),new long[]{rows},true)});
            }
            string Norm(string x,string name,float epsilon=1e-6f)=>graph.Node("LayerNormalization",new[]{x,Load(name+".weight"),Load(name+".bias")},a=>a.Int("axis",-1).Float("epsilon",epsilon));
            // Float32 linear layer with an optional bias, for the small heads.
            string LinearSingle(string x,string name)
            {
                cancellation.ThrowIfCancellationRequested();
                var shape=checkpoint.Tensors[name+".weight"].Shape;int rows=shape[0],cols=shape[1];var w=checkpoint.ReadFloat(name+".weight",cancellation);
                var t=new float[w.Length];for(var r=0;r<rows;r++)for(var c=0;c<cols;c++)t[c*rows+r]=w[r*cols+c];
                var product=graph.Node("MatMul",new[]{x,Store(name+".weightT",t,new long[]{cols,rows},false)});
                return checkpoint.Tensors.ContainsKey(name+".bias")?graph.Node("Add",new[]{product,Load(name+".bias")}):product;
            }
            string Half(string x)=>graph.Node("Cast",new[]{x},a=>a.Int("to",OnnxGraph.Float16));
            string Single(string x)=>graph.Node("Cast",new[]{x},a=>a.Int("to",OnnxGraph.Float));

            string x;
            if(variant==Variant.Tokens){graph.Input("tokens",OnnxGraph.Float,1,tokens,Width);x="tokens";}
            else
            {
                graph.Input("image",OnnxGraph.Float,1,3,256,192);
                x=graph.Node("Conv",new[]{"image",Load("backbone.patch_embed.proj.weight"),Load("backbone.patch_embed.proj.bias")},a=>a.Ints("kernel_shape",16,16).Ints("strides",16,16));
                x=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{x,graph.Constant(1,Width,192)})},a=>a.Ints("perm",0,2,1));
                // The class-token position is added to every patch, as the released models do.
                var pos=checkpoint.ReadFloat("backbone.pos_embed",cancellation);var sum=new float[192*Width];
                for(var t=0;t<192;t++)for(var c=0;c<Width;c++)sum[t*Width+c]=pos[(t+1)*Width+c]+pos[c];
                x=graph.Node("Add",new[]{x,Store("pos_sum",sum,new long[]{1,192,Width},false)});
            }
            if(variant==Variant.Heatmaps)graph.Output("heatmaps",OnnxGraph.Float,1,17,64,48);
            else if(variant==Variant.Hmr2Features)graph.Output("features",OnnxGraph.Float,1024);
            else graph.Output("features",OnnxGraph.Float,1,tokens,Width);
            var scale=graph.Scalar((float)(1/Math.Sqrt(HeadWidth)),OnnxGraph.Float);
            var rootHalf=graph.Scalar((float)(1/Math.Sqrt(2)),OnnxGraph.Float);var halfScalar=graph.Scalar(.5f,OnnxGraph.Float);var one=graph.Scalar(1f,OnnxGraph.Float);
            var heads=graph.Constant(1,tokens,Heads,HeadWidth);var flat=graph.Constant(1,tokens,Width);
            for(var block=0;block<Blocks;block++)
            {
                var name="backbone.blocks."+block;
                var parts=graph.Node("Split",new[]{Linear(Half(Norm(x,name+".norm1")),name+".attn.qkv")},3,a=>a.Int("axis",-1));
                var q=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{parts[0],heads})},a=>a.Ints("perm",0,2,1,3));
                var k=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{parts[1],heads})},a=>a.Ints("perm",0,2,3,1));
                var v=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{parts[2],heads})},a=>a.Ints("perm",0,2,1,3));
                var logits=graph.Node("Mul",new[]{Single(graph.Node("MatMul",new[]{q,k})),scale});
                var attention=graph.Node("MatMul",new[]{Half(graph.Node("Softmax",new[]{logits},a=>a.Int("axis",-1))),v});
                attention=graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{attention},a=>a.Ints("perm",0,2,1,3)),flat});
                var residual=graph.Node("Add",new[]{x,Single(Linear(attention,name+".attn.proj"))});
                var hidden=Single(Linear(Half(Norm(residual,name+".norm2")),name+".mlp.fc1"));
                // Exact GELU, x/2 (1 + erf(x/sqrt 2)), as the checkpoints were trained with.
                var erf=graph.Node("Erf",new[]{graph.Node("Mul",new[]{hidden,rootHalf})});
                var gelu=graph.Node("Mul",new[]{graph.Node("Mul",new[]{hidden,halfScalar}),graph.Node("Add",new[]{erf,one})});
                x=graph.Node("Add",new[]{residual,Single(Linear(Half(gelu),name+".mlp.fc2"))});
            }
            x=Norm(x,"backbone.last_norm");
            if(variant==Variant.Tokens)graph.Identity(x,"features");
            else if(variant==Variant.Hmr2Features)
            {
                // The HMR2 head: six layers of self-attention on one query token, cross-attention onto the 192 image
                // tokens and an MLP, as VisionModel.FeatureHead. Its input token is a constant: the embedding of zero.
                const string prefix="smpl_head.transformer.";
                var bias=checkpoint.ReadFloat(prefix+"to_token_embedding.bias",cancellation);var position=checkpoint.ReadFloat(prefix+"pos_embedding",cancellation);
                var query=Store("hmr2_token",bias.Select((b,i)=>b+position[i]).ToArray(),new long[]{1,1,1024},false);
                var headScale=graph.Scalar(.125f,OnnxGraph.Float);var one8=graph.Constant(1,1,8,64);var context8=graph.Constant(1,192,8,64);var flat512=graph.Constant(1,1,512);
                string Attend(string q,string k,string v)
                {
                    var qh=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{q,one8})},a=>a.Ints("perm",0,2,1,3));
                    var weights=graph.Node("Softmax",new[]{graph.Node("Mul",new[]{graph.Node("MatMul",new[]{qh,k}),headScale})},a=>a.Int("axis",-1));
                    return graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{graph.Node("MatMul",new[]{weights,v})},a=>a.Ints("perm",0,2,1,3)),flat512});
                }
                var h=query;
                for(var layer=0;layer<6;layer++)
                {
                    var name=prefix+"transformer.layers."+layer;
                    var own=graph.Node("Split",new[]{LinearSingle(Norm(h,name+".0.norm",1e-5f),name+".0.fn.to_qkv")},3,a=>a.Int("axis",-1));
                    var ownK=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{own[1],one8})},a=>a.Ints("perm",0,2,3,1));
                    var ownV=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{own[2],one8})},a=>a.Ints("perm",0,2,1,3));
                    var selfAttended=graph.Node("Add",new[]{h,LinearSingle(Attend(own[0],ownK,ownV),name+".0.fn.to_out.0")});
                    var crossQ=LinearSingle(Norm(selfAttended,name+".1.norm",1e-5f),name+".1.fn.to_q");
                    var kv=graph.Node("Split",new[]{LinearSingle(x,name+".1.fn.to_kv")},2,a=>a.Int("axis",-1));
                    var crossK=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{kv[0],context8})},a=>a.Ints("perm",0,2,3,1));
                    var crossV=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{kv[1],context8})},a=>a.Ints("perm",0,2,1,3));
                    var crossAttended=graph.Node("Add",new[]{selfAttended,LinearSingle(Attend(crossQ,crossK,crossV),name+".1.fn.to_out.0")});
                    var inner=LinearSingle(Norm(crossAttended,name+".2.norm",1e-5f),name+".2.fn.net.0");
                    var geluHead=graph.Node("Mul",new[]{graph.Node("Mul",new[]{inner,halfScalar}),graph.Node("Add",new[]{graph.Node("Erf",new[]{graph.Node("Mul",new[]{inner,rootHalf})}),one})});
                    h=graph.Node("Add",new[]{crossAttended,LinearSingle(geluHead,name+".2.fn.net.3")});
                }
                graph.Identity(graph.Node("Reshape",new[]{h,graph.Constant(1024)}),"features");
            }
            else
            {
                // The ViTPose head: two 4x4 stride-2 deconvolutions with batch norm and ReLU, then a 1x1 convolution. Float32.
                x=graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{x},a=>a.Ints("perm",0,2,1)),graph.Constant(1,Width,16,12)});
                for(var block=0;block<2;block++)
                {
                    var name="keypoint_head.deconv_layers.";var bn=name+(block*3+1);
                    x=graph.Node("ConvTranspose",new[]{x,Load(name+(block*3)+".weight")},a=>a.Ints("kernel_shape",4,4).Ints("strides",2,2).Ints("pads",1,1,1,1));
                    x=graph.Node("BatchNormalization",new[]{x,Load(bn+".weight"),Load(bn+".bias"),Load(bn+".running_mean"),Load(bn+".running_var")},a=>a.Float("epsilon",1e-5f));
                    x=graph.Node("Relu",new[]{x});
                }
                graph.Identity(graph.Node("Conv",new[]{x,Load("keypoint_head.final_layer.weight"),Load("keypoint_head.final_layer.bias")},a=>a.Ints("kernel_shape",1,1)),"heatmaps");
            }
            data.Flush();
            File.WriteAllBytes(Path.Combine(cache,stem+".onnx.partial"),graph.Build("vit-h-backbone"));
        }
        // The graph file appears last, so a half-written build is never mistaken for a finished one.
        File.Move(dataPartial,Path.Combine(cache,dataName),true);
        File.Move(Path.Combine(cache,stem+".onnx.partial"),Path.Combine(cache,stem+".onnx"),true);
    }

    /// <summary>Dev check against the LibTorch path: agreement and time per image.</summary>
    public static void Bench(string models,Action<string> report)
    {
        var path=Path.Combine(models,"vitpose/vitpose-h-multi-coco.pth");
        var random=new Random(7);var image=new float[3*256*192];for(var i=0;i<image.Length;i++)image[i]=(float)(random.NextDouble()*2-1);
        report("Device: "+(Device?.Name??"CPU"));
        var clock=Stopwatch.StartNew();
        using(var gpu=new VisionModel(path,VisionModel.Kind.VitPoseHeatmaps,gpuCache:Path.Combine(models,"gpu"),report:report))
        {
            report($"GPU model ready in {clock.Elapsed.TotalSeconds:F1}s ({gpu.Device})");
            var g=gpu.Run(image);clock.Restart();for(var i=0;i<10;i++)g=gpu.Run(image);report($"GPU {clock.Elapsed.TotalMilliseconds/10:F0} ms per image");
            using var cpu=new VisionModel(path,VisionModel.Kind.VitPoseHeatmaps,precision:WilorModel.Float32);
            clock.Restart();var c=cpu.Run(image);report($"CPU float32 {clock.Elapsed.TotalMilliseconds:F0} ms per image");
            double maxDiff=0,maxAbs=0;for(var i=0;i<c.Length;i++){maxDiff=Math.Max(maxDiff,Math.Abs(c[i]-g[i]));maxAbs=Math.Max(maxAbs,Math.Abs(c[i]));}
            var moved=0;
            for(var j=0;j<17;j++){int pc=0,pg=0;for(var i=1;i<64*48;i++){if(c[j*3072+i]>c[j*3072+pc])pc=i;if(g[j*3072+i]>g[j*3072+pg])pg=i;}if(pc!=pg)moved++;}
            report($"max |diff| {maxDiff:E2} of max |value| {maxAbs:E2}; heatmap peaks moved for {moved}/17 joints");
        }
        var hmr=Path.Combine(models,"hmr2/hmr2.ckpt");
        using(var gpu=new VisionModel(hmr,VisionModel.Kind.Hmr2Features,gpuCache:Path.Combine(models,"gpu"),report:report))
        {
            var g=gpu.Run(image);clock.Restart();for(var i=0;i<10;i++)g=gpu.Run(image);report($"HMR2 GPU {clock.Elapsed.TotalMilliseconds/10:F0} ms per image");
            using var cpu=new VisionModel(hmr,VisionModel.Kind.Hmr2Features,precision:WilorModel.Float32);
            clock.Restart();var c=cpu.Run(image);report($"HMR2 CPU float32 {clock.Elapsed.TotalMilliseconds:F0} ms per image");
            double dot=0,na=0,nb=0,maxDiff=0;for(var i=0;i<c.Length;i++){dot+=c[i]*g[i];na+=c[i]*c[i];nb+=g[i]*g[i];maxDiff=Math.Max(maxDiff,Math.Abs(c[i]-g[i]));}
            report($"HMR2 features: max |diff| {maxDiff:E2}, cosine {dot/Math.Sqrt(na*nb):F6}, norm {Math.Sqrt(na):F2}");
        }
    }

    /// <summary>Picks the DXGI adapter with the most dedicated video memory, skipping software adapters.
    /// DirectML's device index is the DXGI enumeration index.</summary>
    static class Dxgi
    {
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid,out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1(IntPtr self,int index,out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1(IntPtr self,out Desc1 desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFn(IntPtr self);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct Desc1
        {
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
            public uint VendorId,DeviceId,SubSysId,Revision;public nuint DedicatedVideoMemory,DedicatedSystemMemory,SharedSystemMemory;
            public uint LuidLow;public int LuidHigh;public uint Flags;
        }
        static T Method<T>(IntPtr self,int slot) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self),slot*IntPtr.Size));
        static void Release(IntPtr p){if(p!=IntPtr.Zero)Method<ReleaseFn>(p,2)(p);}
        public static (int Index,string Name,long DedicatedBytes)? LargestAdapter()
        {
            var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if(CreateDXGIFactory1(ref iid,out var factory)<0)return null;
            try
            {
                (int,string,long)? best=null;
                // IDXGIFactory1::EnumAdapters1 is slot 12; IDXGIAdapter1::GetDesc1 is slot 10.
                for(var i=0;Method<EnumAdapters1>(factory,12)(factory,i,out var adapter)>=0;i++)
                {
                    try
                    {
                        if(Method<GetDesc1>(adapter,10)(adapter,out var desc)<0||(desc.Flags&2)!=0)continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                        var bytes=(long)desc.DedicatedVideoMemory;if(best is not { } b||bytes>b.Item3)best=(i,desc.Description.Trim(),bytes);
                    }
                    finally{Release(adapter);}
                }
                return best;
            }
            finally{Release(factory);}
        }
    }
}
