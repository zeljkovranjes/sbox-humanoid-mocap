// PVA-Net adapted from HTD-Refine (ant-research/HTD-Refine, pvanet/network), AGPL-3.0.
using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using Microsoft.ML.OnnxRuntime;

namespace HumanoidMocap.Worker;

/// <summary>PVA-Net, the motion half of HTD-Refine (Wei, Shen et al., CVPR 2026): from ViTPose's image tokens for a
/// stretch of video, each COCO joint's 2D position and its 3D velocity and acceleration in camera space, which
/// <see cref="HtdRefine"/> then fits the body motion to. Its image encoder is ViTPose-H itself, bit for bit, so the
/// tokens come from the ViTPose graph the capture already has (<see cref="VisionModel.Tokens"/>) and only its twelve
/// temporal/spatial transformer layers and three heads are extra. Graphics card only: the graph is assembled once
/// from the downloaded checkpoint into the cache folder, with float16 matrix products and a float32 residual stream,
/// as <see cref="GpuBackbone"/> does. The video is read in windows of 120 frames overlapping by 30, the reference's.</summary>
public sealed class PvaNet : IDisposable
{
    public const string CheckpointSha256="e760b788d354540466788606bfe3d5d4dc5c4f9a0cce4bac81a93672fd9bd39e";
    public const string BuilderVersion="pva-head-v1";
    public const int Window=120,Overlap=30;
    /// <summary>Where the downloaded checkpoint lives in the models folder.</summary>
    public const string ModelFile="pvanet/pvanet-head.pt";
    const int Width=1280,Tokens=192,Heads=8,HeadWidth=Width/Heads,DecoderWidth=512,DecoderHeadWidth=DecoderWidth/Heads;
    readonly InferenceSession session;
    readonly float[] velocityMean,velocityStd,accelerationMean,accelerationStd;
    public string Adapter { get; }

    public const long CheckpointBytes=495789927;
    /// <summary>PVA-Net's temporal layers and heads, cut from the released pvanet.pt (whose other 631M weights are ViTPose-H)
    /// and stored as float16 matrices: a 3.6 GB Google Drive file is not a download people can rely on.</summary>
    public const string CheckpointUrl="https://github.com/zeljkovranjes/sbox-humanoid-mocap/releases/download/pvanet-1/pvanet-head.pt";
    /// <summary>Fetches the checkpoint when this PC has a graphics card that can run it; without one, refinement is skipped
    /// and nothing is downloaded.</summary>
    public static async Task EnsureDownloaded(string models,CancellationToken cancellation)
    {
        if(GpuBackbone.Device is null){Console.WriteLine("No suitable graphics card; motion refinement is not used");return;}
        var path=Path.Combine(models,ModelFile);
        if(File.Exists(path)&&FileChecksum.Matches(path,CheckpointSha256)){Console.WriteLine("Verified pvanet-head.pt");return;}
        using var client=new HttpClient{Timeout=TimeSpan.FromHours(1)};
        await ModelDownload.Fetch(client,CheckpointUrl,path,CheckpointBytes,CheckpointSha256,Console.WriteLine,cancellation);
        Console.WriteLine("Verified pvanet-head.pt");
    }
    /// <summary>The network on the graphics card, or null without one (or when it cannot be prepared; reported once).</summary>
    public static PvaNet? TryCreate(string checkpointPath,string cache,Action<string>? report,CancellationToken cancellation)
    {
        if(GpuBackbone.Device is not { } device)return null;
        try{return new PvaNet(checkpointPath,cache,device,cancellation);}
        catch(OperationCanceledException){throw;}
        catch(Exception error){report?.Invoke($"Motion refinement could not use the graphics card ({error.Message.Split('\n')[0]}); skipped");return null;}
    }
    PvaNet(string checkpointPath,string cache,(int Index,string Name) device,CancellationToken cancellation)
    {
        if(!FileChecksum.Matches(checkpointPath,CheckpointSha256))throw new InvalidDataException("PVA-Net checkpoint checksum mismatch.");
        Adapter=device.Name;Directory.CreateDirectory(cache);
        foreach(var file in Directory.EnumerateFiles(cache,"pva-head-*"))
            if(!Path.GetFileName(file).Contains("-"+BuilderVersion+".",StringComparison.Ordinal))try{File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}
        var stem=$"pva-head-{CheckpointSha256[..16]}-fp16-{BuilderVersion}";var graphPath=Path.Combine(cache,stem+".onnx");
        using(var checkpoint=new TorchCheckpoint(checkpointPath))
        {
            velocityMean=checkpoint.ReadFloat("velocity_mean",cancellation);velocityStd=checkpoint.ReadFloat("velocity_std",cancellation);
            accelerationMean=checkpoint.ReadFloat("acceleration_mean",cancellation);accelerationStd=checkpoint.ReadFloat("acceleration_std",cancellation);
            if(!File.Exists(graphPath))Build(checkpoint,cache,stem,cancellation);
        }
        using var options=new SessionOptions{GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL,EnableMemoryPattern=false,ExecutionMode=ExecutionMode.ORT_SEQUENTIAL,LogSeverityLevel=OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR};
        options.AppendExecutionProvider_DML(device.Index);
        session=new InferenceSession(graphPath,options);
    }
    public void Dispose()=>session.Dispose();

    /// <summary>One window's raw outputs: per frame, the 17 joints' velocity and acceleration (metres per second and per
    /// second squared, camera axes; the last one and two frames have none) and their 64x48 heatmaps.</summary>
    public sealed record Result(float[] Velocity,float[] Acceleration,float[] Heatmaps);
    /// <param name="tokens">Per frame, ViTPose's [192,1280] tokens of the crop <see cref="VideoCrop.Prepare"/> makes from <paramref name="boxes"/>.</param>
    public Result Run(IReadOnlyList<float[]> tokens,IReadOnlyList<GvhmrDecoder.Box> boxes,GvhmrDecoder.Camera camera)
    {
        var length=tokens.Count;if(length<3||boxes.Count!=length)throw new ArgumentException("PVA-Net needs at least three frames, each with its crop.");
        var flat=new float[length*Tokens*Width];for(var t=0;t<length;t++){if(tokens[t].Length!=Tokens*Width)throw new ArgumentException("Unexpected token count.");tokens[t].CopyTo(flat,t*Tokens*Width);}
        // CLIFF-style position of every token in the picture, relative to the principal point in focal lengths. The
        // square crop's middle three quarters is the 192x256 image ViTPose saw.
        var cliff=new float[length*Tokens*2];
        for(var t=0;t<length;t++)
        {
            var b=boxes[t];float x1=b.CenterX-.375f*b.Size,x2=b.CenterX+.375f*b.Size,y1=b.CenterY-.5f*b.Size,y2=b.CenterY+.5f*b.Size;
            for(var h=0;h<16;h++)for(var w=0;w<12;w++)
            {var i=(t*Tokens+h*12+w)*2;cliff[i]=(x1+(x2-x1)*w/11f-camera.CenterX)/camera.FocalLength;cliff[i+1]=(y1+(y2-y1)*h/15f-camera.CenterY)/camera.FocalLength;}
        }
        var (cos,sin)=Rope(length,HeadWidth);var (decoderCos,decoderSin)=Rope(length,DecoderHeadWidth);
        var position=new float[length*DecoderWidth];
        for(var p=0;p<length;p++)for(var i=0;i<DecoderWidth/2;i++)
        {var angle=p*Math.Exp(2*i*(-Math.Log(10000.0)/DecoderWidth));position[p*DecoderWidth+2*i]=(float)Math.Sin(angle);position[p*DecoderWidth+2*i+1]=(float)Math.Cos(angle);}
        OrtValue Value(float[] data,params long[] shape)=>OrtValue.CreateTensorValueFromMemory(data,shape);
        using var v0=Value(flat,length,Tokens,Width);using var v1=Value(cliff,length,Tokens,2);using var v2=Value(cos,length,HeadWidth);using var v3=Value(sin,length,HeadWidth);
        using var v4=Value(decoderCos,length,DecoderHeadWidth);using var v5=Value(decoderSin,length,DecoderHeadWidth);using var v6=Value(position,length,DecoderWidth);
        using var run=new RunOptions();
        using var outputs=session.Run(run,new[]{"tokens","cliff","rope_cos","rope_sin","decoder_cos","decoder_sin","position"},new[]{v0,v1,v2,v3,v4,v5,v6},new[]{"velocity","acceleration","heatmaps"});
        var velocity=outputs[0].GetTensorDataAsSpan<float>()[..((length-1)*51)].ToArray();var acceleration=outputs[1].GetTensorDataAsSpan<float>()[..((length-2)*51)].ToArray();
        for(var i=0;i<velocity.Length;i++)velocity[i]=velocity[i]*velocityStd[i%3]+velocityMean[i%3];
        for(var i=0;i<acceleration.Length;i++)acceleration[i]=acceleration[i]*accelerationStd[i%51]+accelerationMean[i%51];
        var heatmaps=outputs[2].GetTensorDataAsSpan<float>().ToArray();
        if(velocity.Concat(acceleration).Concat(heatmaps).Any(v=>!float.IsFinite(v)))throw new ArithmeticException("Non-finite motion prediction.");
        return new(velocity,acceleration,heatmaps);
    }
    /// <summary>One-dimensional rotary position tables over frames, pairs of adjacent channels sharing a frequency.</summary>
    static (float[] Cos,float[] Sin) Rope(int length,int width)
    {
        var cos=new float[length*width];var sin=new float[length*width];
        for(var p=0;p<length;p++)for(var i=0;i<width/2;i++)
        {
            var angle=p/Math.Pow(10000,2.0*i/width);
            cos[p*width+2*i]=cos[p*width+2*i+1]=(float)Math.Cos(angle);sin[p*width+2*i]=sin[p*width+2*i+1]=(float)Math.Sin(angle);
        }
        return (cos,sin);
    }

    /// <summary>Every frame's targets for <see cref="HtdRefine"/>, from overlapping windows; each frame keeps the first window's answer.</summary>
    public sealed record Targets(float[] Keypoints,float[] Velocity,float[] Acceleration);
    /// <summary>Feeds frames in order and runs each window as soon as its last frame arrives, so at most one window of
    /// tokens is held.</summary>
    public sealed class Collector
    {
        readonly PvaNet network;readonly GvhmrDecoder.Camera camera;readonly int count,length;readonly int[] starts;int next;
        readonly Dictionary<int,(float[] Tokens,GvhmrDecoder.Box Box)> held=new();
        readonly float[] keypoints,velocity,acceleration;readonly bool[] haveKeypoints,haveVelocity,haveAcceleration;
        public Collector(PvaNet network,int count,GvhmrDecoder.Camera camera)
        {
            if(count<3)throw new ArgumentException("Motion refinement needs at least three frames.");
            this.network=network;this.camera=camera;this.count=count;length=Math.Min(Window,count);
            var list=new List<int>();for(var s=0;s<=count-length;s+=Window-Overlap)list.Add(s);if(list[^1]!=count-length)list.Add(count-length);starts=list.ToArray();
            keypoints=new float[count*51];velocity=new float[(count-1)*51];acceleration=new float[(count-2)*51];
            haveKeypoints=new bool[count];haveVelocity=new bool[count-1];haveAcceleration=new bool[count-2];
        }
        public void Add(int index,float[] tokens,GvhmrDecoder.Box box)
        {
            held[index]=(tokens,box);
            while(next<starts.Length&&starts[next]+length-1<=index)
            {
                var start=starts[next++];var frames=Enumerable.Range(start,length).Select(i=>held.TryGetValue(i,out var f)?f:throw new InvalidOperationException("Motion refinement frames arrived out of order.")).ToArray();
                var result=network.Run(frames.Select(f=>f.Tokens).ToArray(),frames.Select(f=>f.Box).ToArray(),camera);
                for(var i=0;i<length;i++)
                {
                    var g=start+i;
                    if(!haveKeypoints[g]){VideoCrop.DecodeHeatmaps(result.Heatmaps.AsSpan(i*17*64*48,17*64*48).ToArray(),frames[i].Box).CopyTo(keypoints,g*51);haveKeypoints[g]=true;}
                    if(i<length-1&&!haveVelocity[g]){Array.Copy(result.Velocity,i*51,velocity,g*51,51);haveVelocity[g]=true;}
                    if(i<length-2&&!haveAcceleration[g]){Array.Copy(result.Acceleration,i*51,acceleration,g*51,51);haveAcceleration[g]=true;}
                }
                var keep=next<starts.Length?starts[next]:count;foreach(var old in held.Keys.Where(k=>k<keep).ToArray())held.Remove(old);
            }
        }
        public Targets Finish()
        {
            if(next<starts.Length||haveKeypoints.Contains(false)||haveVelocity.Contains(false)||haveAcceleration.Contains(false))throw new InvalidOperationException("Motion refinement did not see every frame.");
            return new(keypoints,velocity,acceleration);
        }
    }

    void Build(TorchCheckpoint checkpoint,string cache,string stem,CancellationToken cancellation)
    {
        var dataName=stem+".data";var dataPartial=Path.Combine(cache,dataName+".partial");
        using(var data=new FileStream(dataPartial,FileMode.Create,FileAccess.Write,FileShare.None,1<<20))
        {
            var graph=new OnnxGraph(data,dataName);
            string Store(string name,float[] values,long[] shape,bool half)
            {
                if(!half)return graph.Weight(name,OnnxGraph.Float,shape,MemoryMarshal.AsBytes(values.AsSpan()));
                var h=new ushort[values.Length];for(var i=0;i<values.Length;i++)h[i]=BitConverter.HalfToUInt16Bits((Half)values[i]);
                return graph.Weight(name,OnnxGraph.Float16,shape,MemoryMarshal.AsBytes(h.AsSpan()));
            }
            float[] Read(string name){cancellation.ThrowIfCancellationRequested();return checkpoint.ReadFloat(name,cancellation);}
            long[] Shape(string name)=>checkpoint.Tensors[name].Shape.Select(v=>(long)v).ToArray();
            string Load(string name,bool half=false)=>Store(name,Read(name),Shape(name),half);
            string Half(string x)=>graph.Node("Cast",new[]{x},a=>a.Int("to",OnnxGraph.Float16));
            string Single(string x)=>graph.Node("Cast",new[]{x},a=>a.Int("to",OnnxGraph.Float));
            // Linear layers are stored [out,in]; MatMul takes [in,out], transposed once here.
            string Linear(string x,string name,bool half)
            {
                var shape=Shape(name+".weight");int rows=(int)shape[0],cols=(int)shape[1];var w=Read(name+".weight");
                var t=new float[w.Length];for(var r=0;r<rows;r++)for(var c=0;c<cols;c++)t[c*rows+r]=w[r*cols+c];
                var product=graph.Node("MatMul",new[]{x,Store(name+".weightT",t,new long[]{cols,rows},half)});
                return graph.Node("Add",new[]{product,Store(name+".bias",Read(name+".bias"),new long[]{rows},half)});
            }
            string Norm(string x,string name,float epsilon)=>graph.Node("LayerNormalization",new[]{x,Load(name+".weight"),Load(name+".bias")},a=>a.Int("axis",-1).Float("epsilon",epsilon));
            string Const(string name,float[] values,params long[] shape)=>Store(name,values,shape,false);
            var rootTwoOverPi=graph.Scalar((float)Math.Sqrt(2/Math.PI),OnnxGraph.Float);var cubic=graph.Scalar(.044715f,OnnxGraph.Float);
            var halfScalar=graph.Scalar(.5f,OnnxGraph.Float);var one=graph.Scalar(1f,OnnxGraph.Float);
            // Swaps each pair of channels (a,b) to (-b,a): rotary position's "rotate half".
            string Swap(int width)
            {var p=new float[width*width];for(var i=0;i<width/2;i++){p[(2*i+1)*width+2*i]=-1;p[2*i*width+2*i+1]=1;}return Const("swap"+width,p,width,width);}
            var swap=Swap(HeadWidth);var swapDecoder=Swap(DecoderHeadWidth);
            // A transformer block with gated residuals and rotary positions on queries and keys, [batch, sequence, width] in and out.
            string Block(string x,string name,int width,int heads,string cos,string sin,string swapping)
            {
                var headWidth=width/heads;var split=graph.Constant(0,0,heads,headWidth);var merge=graph.Constant(0,0,width);
                var h=Half(Norm(x,name+".norm1",1e-6f));
                string Heads(string y)=>graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{y,split})},a=>a.Ints("perm",0,2,1,3));
                string Rotate(string y){var s=Single(y);return Half(graph.Node("Add",new[]{graph.Node("Mul",new[]{s,cos}),graph.Node("Mul",new[]{graph.Node("MatMul",new[]{s,swapping}),sin})}));}
                var q=Rotate(Heads(Linear(h,name+".attn.query",true)));var k=Rotate(Heads(Linear(h,name+".attn.key",true)));var v=Heads(Linear(h,name+".attn.value",true));
                var logits=graph.Node("Mul",new[]{Single(graph.Node("MatMul",new[]{q,graph.Node("Transpose",new[]{k},a=>a.Ints("perm",0,1,3,2))})),graph.Scalar((float)(1/Math.Sqrt(headWidth)),OnnxGraph.Float)});
                var attended=graph.Node("MatMul",new[]{Half(graph.Node("Softmax",new[]{logits},a=>a.Int("axis",-1))),v});
                attended=graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{attended},a=>a.Ints("perm",0,2,1,3)),merge});
                x=graph.Node("Add",new[]{x,graph.Node("Mul",new[]{Single(Linear(attended,name+".attn.proj",true)),Load(name+".gate_msa")})});
                var hidden=Single(Linear(Half(Norm(x,name+".norm2",1e-6f)),name+".mlp.fc1",true));
                // GELU, tanh approximation, as trained.
                var inner=graph.Node("Mul",new[]{graph.Node("Add",new[]{hidden,graph.Node("Mul",new[]{cubic,graph.Node("Mul",new[]{hidden,graph.Node("Mul",new[]{hidden,hidden})})})}),rootTwoOverPi});
                var gelu=graph.Node("Mul",new[]{graph.Node("Mul",new[]{hidden,halfScalar}),graph.Node("Add",new[]{graph.Node("Tanh",new[]{inner}),one})});
                return graph.Node("Add",new[]{x,graph.Node("Mul",new[]{Single(Linear(Half(gelu),name+".mlp.fc2",true)),Load(name+".gate_mlp")})});
            }

            graph.Input("tokens",OnnxGraph.Float,-1,Tokens,Width);graph.Input("cliff",OnnxGraph.Float,-1,Tokens,2);
            graph.Input("rope_cos",OnnxGraph.Float,-1,HeadWidth);graph.Input("rope_sin",OnnxGraph.Float,-1,HeadWidth);
            graph.Input("decoder_cos",OnnxGraph.Float,-1,DecoderHeadWidth);graph.Input("decoder_sin",OnnxGraph.Float,-1,DecoderHeadWidth);
            graph.Input("position",OnnxGraph.Float,-1,DecoderWidth);
            graph.Output("velocity",OnnxGraph.Float,-1,51);graph.Output("acceleration",OnnxGraph.Float,-1,51);graph.Output("heatmaps",OnnxGraph.Float,-1,17,64,48);

            // Image tokens [frames,192,1280] plus their projected picture positions.
            var x=graph.Node("Add",new[]{"tokens",Linear("cliff","spatial_position_projection.0",false)});
            var reads=new List<string>{x};
            // Eight temporal blocks (each token position across the window's frames) with a spatial block (the frame's
            // 192 tokens) after every second one.
            var temporal=0;
            for(var spatial=0;spatial<4;spatial++)
            {
                for(var i=0;i<2;i++)
                {
                    var across=graph.Node("Transpose",new[]{x},a=>a.Ints("perm",1,0,2));
                    x=graph.Node("Transpose",new[]{Block(across,$"temporal_layer.temporal_blocks.{temporal++}",Width,Heads,"rope_cos","rope_sin",swap)},a=>a.Ints("perm",1,0,2));
                }
                // Axial rotary positions over the 16x12 token grid; the time part sees a single frame and stays unrotated.
                var name=$"temporal_layer.spatial_blocks.{spatial}";
                var ft=Read(name+".attn.rope.freqs_t");var fh=Read(name+".attn.rope.freqs_h");var fw=Read(name+".attn.rope.freqs_w");
                var cos=new float[Tokens*HeadWidth];var sin=new float[Tokens*HeadWidth];
                for(var t=0;t<Tokens;t++)
                {
                    int row=t/12,column=t%12,o=0;
                    foreach(var (freqs,at) in new[]{(ft,0),(fh,row),(fw,column)})
                        for(var j=0;j<freqs.Length;j++,o+=2){var angle=at*freqs[j];cos[t*HeadWidth+o]=cos[t*HeadWidth+o+1]=MathF.Cos(angle);sin[t*HeadWidth+o]=sin[t*HeadWidth+o+1]=MathF.Sin(angle);}
                }
                x=Block(x,name,Width,Heads,Const(name+".cos",cos,Tokens,HeadWidth),Const(name+".sin",sin,Tokens,HeadWidth),swap);
                reads.Add(x);
            }
            // Gated mix of the normalized input and spatial-block outputs, for the motion heads. The softmax weights and
            // gate are folded into each layer norm's scale and shift.
            var logits=Read("temporal_feature_aggregation.logits");var gate=Read("temporal_feature_aggregation.gate")[0];
            var total=logits.Sum(l=>Math.Exp(l));var motion=x;
            for(var i=0;i<reads.Count;i++)
            {
                var scale=(float)(Math.Exp(logits[i])/total)*gate;var ln=$"temporal_feature_aggregation.lns.{i}";
                var weight=Read(ln+".weight").Select(v=>v*scale).ToArray();var bias=Read(ln+".bias").Select(v=>v*scale).ToArray();
                motion=graph.Node("Add",new[]{motion,graph.Node("LayerNormalization",new[]{reads[i],Const(ln+".w",weight,Width),Const(ln+".b",bias,Width)},a=>a.Int("axis",-1).Float("epsilon",1e-5f))});
            }
            string Maps(string tokens)=>graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{tokens},a=>a.Ints("perm",0,2,1)),graph.Constant(-1,Width,16,12)});
            string Normalized(string y,string name)=>graph.Node("BatchNormalization",new[]{y,Load(name+".weight"),Load(name+".bias"),Load(name+".running_mean"),Load(name+".running_var")},a=>a.Float("epsilon",1e-5f));
            // Velocity and acceleration heads: a five-frame depthwise filter over time, two 3x3 convolutions, pooling, then
            // eight temporal blocks over the window.
            string Head(string name)
            {
                var channels=graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{motion},a=>a.Ints("perm",2,0,1)),graph.Constant(1,Width,-1,Tokens)});
                var filtered=graph.Node("Conv",new[]{channels,Store(name+".tconv.w",Read(name+".tconv.weight"),new long[]{Width,1,5,1},false),Load(name+".tconv.bias")},
                    a=>a.Ints("kernel_shape",5,1).Ints("pads",2,0,2,0).Int("group",Width));
                var maps=graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{filtered},a=>a.Ints("perm",2,1,0,3)),graph.Constant(-1,Width,16,12)});
                foreach(var layer in new[]{"conv_layers_1","conv_layers_2"})
                {
                    maps=graph.Node("Conv",new[]{maps,Load($"{name}.{layer}.0.weight"),Load($"{name}.{layer}.0.bias")},a=>a.Ints("kernel_shape",3,3).Ints("pads",1,1,1,1));
                    maps=graph.Node("Relu",new[]{Normalized(maps,$"{name}.{layer}.1")});
                }
                var y=graph.Node("Add",new[]{graph.Node("Reshape",new[]{graph.Node("GlobalAveragePool",new[]{maps}),graph.Constant(1,-1,DecoderWidth)}),"position"});
                for(var b=0;b<8;b++)y=Block(y,$"{name}.temporal_blocks_final_L.{b}",DecoderWidth,Heads,"decoder_cos","decoder_sin",swapDecoder);
                var shape=Shape(name+".final_layer.weight");var w=Read(name+".final_layer.weight");var t=new float[w.Length];
                for(var r=0;r<shape[0];r++)for(var c=0;c<shape[1];c++)t[c*shape[0]+r]=w[r*shape[1]+c];
                var output=graph.Node("Add",new[]{graph.Node("MatMul",new[]{y,Const(name+".final.wT",t,shape[1],shape[0])}),Load(name+".final_layer.bias")});
                return graph.Node("Reshape",new[]{output,graph.Constant(-1,51)});
            }
            graph.Identity(Head("velocity_decoder"),"velocity");
            graph.Identity(Head("acceleration_decoder"),"acceleration");
            // 2D keypoint heatmaps from the last temporal features: ViTPose's head layout.
            var heat=Maps(x);
            for(var block=0;block<2;block++)
            {
                const string name="keypoints_decoder.deconv_layers.";
                heat=graph.Node("ConvTranspose",new[]{heat,Load(name+(block*3)+".weight")},a=>a.Ints("kernel_shape",4,4).Ints("strides",2,2).Ints("pads",1,1,1,1));
                heat=graph.Node("Relu",new[]{Normalized(heat,name+(block*3+1))});
            }
            graph.Identity(graph.Node("Conv",new[]{heat,Load("keypoints_decoder.final_layer.weight"),Load("keypoints_decoder.final_layer.bias")},a=>a.Ints("kernel_shape",1,1)),"heatmaps");
            data.Flush();
            File.WriteAllBytes(Path.Combine(cache,stem+".onnx.partial"),graph.Build("pva-net"));
        }
        File.Move(dataPartial,Path.Combine(cache,dataName),true);
        File.Move(Path.Combine(cache,stem+".onnx.partial"),Path.Combine(cache,stem+".onnx"),true);
    }

    /// <summary>Dev check: the network on saved reference tokens, against the reference outputs.</summary>
    public static void Check(string checkpoint,string cache,string reference,int length,Action<string> report)
    {
        using var network=TryCreate(checkpoint,cache,report,CancellationToken.None)??throw new InvalidOperationException("No graphics card.");
        float[] ReadBin(string name){var bytes=File.ReadAllBytes(Path.Combine(reference,$"pva_ref_{name}_{length}.bin"));var f=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,f,0,bytes.Length);return f;}
        var all=ReadBin("tokens");var tokens=Enumerable.Range(0,length).Select(t=>all.AsSpan(t*Tokens*Width,Tokens*Width).ToArray()).ToArray();
        var box=ReadBin("box");var boxes=Enumerable.Range(0,length).Select(t=>{var x1=box[t*4];var y1=box[t*4+1];var x2=box[t*4+2];var y2=box[t*4+3];return new GvhmrDecoder.Box((x1+x2)/2,(y1+y2)/2,y2-y1);}).ToArray();
        var camera=new GvhmrDecoder.Camera(755.5609f,180,320);
        var clock=System.Diagnostics.Stopwatch.StartNew();var result=network.Run(tokens,boxes,camera);var first=clock.Elapsed.TotalMilliseconds;
        clock.Restart();result=network.Run(tokens,boxes,camera);report($"{length} frames: {first:F0} ms first run, {clock.Elapsed.TotalMilliseconds:F0} ms after");
        var refVel=ReadBin("vel");var refAcc=ReadBin("acc");var refHm=ReadBin("hm");
        // The reference files hold the heads' normalized outputs for every frame.
        var vel=new float[(length-1)*51];var acc=new float[(length-2)*51];
        for(var i=0;i<vel.Length;i++)vel[i]=refVel[i]*network.velocityStd[i%3]+network.velocityMean[i%3];
        for(var i=0;i<acc.Length;i++)acc[i]=refAcc[i]*network.accelerationStd[i%51]+network.accelerationMean[i%51];
        static string Compare(float[] a,float[] b){double d=0,m=0,dot=0,na=0,nb=0;for(var i=0;i<a.Length;i++){d=Math.Max(d,Math.Abs(a[i]-b[i]));m=Math.Max(m,Math.Abs(b[i]));dot+=a[i]*b[i];na+=a[i]*a[i];nb+=b[i]*b[i];}return $"max |diff| {d:E2} of max {m:E2}, cosine {dot/Math.Sqrt(na*nb):F6}";}
        report("velocity: "+Compare(result.Velocity,vel));report("acceleration: "+Compare(result.Acceleration,acc));report("heatmaps: "+Compare(result.Heatmaps,refHm));
    }
}
