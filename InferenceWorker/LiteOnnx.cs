using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using Microsoft.ML.OnnxRuntime;

namespace HumanoidMocap.Worker;

/// <summary>Runs the pinned MediaPipe hand models through ONNX Runtime. The TFLite operator list that
/// <see cref="LiteInterpreter"/> interprets is translated one operator at a time into an ONNX graph in
/// channel-first layout, with the same padding, resize and activation rules, so the result matches the
/// managed interpreter while running on native kernels: the graphics card when there is one, otherwise the
/// processor. Models too small to need a file keep their weights inside the graph.</summary>
public sealed class LiteOnnx : IDisposable
{
    readonly InferenceSession session;readonly string inputName;readonly long[] inputShape;readonly string[] outputNames;
    public string Device { get; }
    public LiteOnnx(LiteModel model,bool gpu)
    {
        var bytes=Translate(model,out inputShape,out outputNames);inputName="input";
        using var options=new SessionOptions{GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL,LogSeverityLevel=OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR};
        if(gpu&&GpuBackbone.Device is { } device){options.EnableMemoryPattern=false;options.ExecutionMode=ExecutionMode.ORT_SEQUENTIAL;options.AppendExecutionProvider_DML(device.Index);Device=device.Name;}
        else{options.IntraOpNumThreads=Math.Clamp(Environment.ProcessorCount/2,1,8);Device="CPU";}
        session=new InferenceSession(bytes,options);
    }
    public float[][] Run(float[] input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var value=OrtValue.CreateTensorValueFromMemory(input,inputShape);using var run=new RunOptions();
        using var outputs=session.Run(run,new[]{inputName},new[]{value},outputNames);
        return outputs.Select(o=>o.GetTensorDataAsSpan<float>().ToArray()).ToArray();
    }
    public void Dispose()=>session.Dispose();

    /// <summary>Selects ONNX Runtime for <see cref="ManagedHands"/> in this process. Each model is checked against the
    /// managed interpreter on one input first; a model that disagrees keeps the interpreter.</summary>
    /// <summary>Joins cache keys: native kernels agree with the interpreter to about four digits, not bit for bit.</summary>
    public const string KeySuffix="lite-onnx-v1";
    public static void Install(Action<string>? report=null)
    {
        ManagedHands.RunnerFactory=model=>
        {
            foreach(var gpu in new[]{true,false})
            {
                if(gpu&&GpuBackbone.Device is null)continue;
                try
                {
                    var runner=new LiteOnnx(model,gpu);
                    if(Agrees(model,runner))return runner.Run;
                    runner.Dispose();report?.Invoke($"Native hand detector on {(gpu?"the graphics card":"the processor")} disagreed with the reference; not used");
                }
                catch(Exception error){report?.Invoke($"Native hand detector unavailable ({error.Message.Split('\n')[0]})");}
            }
            return new LiteInterpreter(model).Run;
        };
    }
    static bool Agrees(LiteModel model,LiteOnnx runner)
    {
        var random=new Random(3);var length=model.Tensors[model.Inputs[0]].Shape.Aggregate(1,(a,b)=>a*b);
        var input=new float[length];for(var i=0;i<length;i++)input[i]=(float)random.NextDouble();
        var expected=new LiteInterpreter(model).Run(input);var actual=runner.Run(input,default);
        for(var o=0;o<expected.Length;o++)
        {
            var scale=Math.Max(1,expected[o].Max(v=>Math.Abs(v)));
            for(var i=0;i<expected[o].Length;i++)if(!(Math.Abs(expected[o][i]-actual[o][i])<=2e-3*scale))return false;
        }
        return true;
    }

    static byte[] Translate(LiteModel model,out long[] inputShape,out string[] outputNames)
    {
        var graph=new OnnxGraph(null);
        // Each TFLite tensor maps to an ONNX value; four-dimensional activations are held channel-first.
        var values=new Dictionary<int,(string Name,bool ChannelFirst)>();int counter=0;
        int[] Shape(int tensor)=>model.Tensors[tensor].Shape;
        // DEQUANTIZE outputs alias their float16 source, as in the managed interpreter.
        var alias=model.Operators.Where(o=>o.Code==6).ToDictionary(o=>o.Outputs[0],o=>o.Inputs[0]);
        int Source(int tensor){while(alias.TryGetValue(tensor,out var from))tensor=from;return tensor;}
        float[] Data(int tensor)=>model.Tensors[Source(tensor)].Values;
        string Floats(float[] data,params long[] shape)=>graph.Weight("w"+counter++,OnnxGraph.Float,shape,MemoryMarshal.AsBytes(data.AsSpan()));
        string ToFirst(string x)=>graph.Node("Transpose",new[]{x},a=>a.Ints("perm",0,3,1,2));
        string ToLast(string x)=>graph.Node("Transpose",new[]{x},a=>a.Ints("perm",0,2,3,1));
        (string Name,bool ChannelFirst) Value(int tensor)
        {
            tensor=Source(tensor);if(values.TryGetValue(tensor,out var known))return known;
            var t=model.Tensors[tensor];if(!t.Constant)throw new NotSupportedException("Tensor used before it is produced.");
            var constant=(Floats(t.Values,t.Shape.Select(v=>(long)v).ToArray()),false);values[tensor]=constant;return constant;
        }
        string Last(int tensor){var v=Value(tensor);return v.ChannelFirst?ToLast(v.Name):v.Name;}
        string First(int tensor){var v=Value(tensor);return v.ChannelFirst||Shape(tensor).Length!=4?v.Name:ToFirst(v.Name);}
        string Activate(string x,int kind)=>kind switch
        {
            0=>x,1=>graph.Node("Relu",new[]{x}),
            2=>graph.Node("Clip",new[]{x,graph.Scalar(-1,OnnxGraph.Float),graph.Scalar(1,OnnxGraph.Float)}),
            3=>graph.Node("Clip",new[]{x,graph.Scalar(0,OnnxGraph.Float),graph.Scalar(6,OnnxGraph.Float)}),
            _=>throw new NotSupportedException($"Activation {kind}")
        };
        // TensorFlow SAME padding puts the odd pixel after; the managed interpreter uses the same split.
        long[] Pads(int[] input,int[] output,int kh,int kw,int sh,int sw,int dh,int dw,bool same)
        {
            if(!same)return new long[]{0,0,0,0};
            var th=Math.Max(0,(output[1]-1)*sh+(kh-1)*dh+1-input[1]);var tw=Math.Max(0,(output[2]-1)*sw+(kw-1)*dw+1-input[2]);
            return new long[]{th/2,tw/2,th-th/2,tw-tw/2};
        }

        var input=model.Inputs.Single();var inShape=Shape(input);inputShape=inShape.Select(v=>(long)v).ToArray();
        graph.Input("input",OnnxGraph.Float,inputShape);
        values[input]=inShape.Length==4?(ToFirst("input"),true):("input",false);

        foreach(var op in model.Operators)
        {
            var output=op.Outputs[0];var target=Shape(output);string result;bool first=target.Length==4;
            switch(op.Code)
            {
                case 6:continue; // DEQUANTIZE: resolved through Source
                case 0: // ADD
                {
                    var a=Value(op.Inputs[0]);var b=Value(op.Inputs[1]);
                    var an=first?First(op.Inputs[0]):a.Name;var bn=first?First(op.Inputs[1]):b.Name;
                    result=Activate(graph.Node("Add",new[]{an,bn}),model.Byte(op.Options,0));break;
                }
                case 54: // PRELU, alpha per channel
                {
                    var alpha=Data(op.Inputs[1]);var channels=target[^1];
                    if(alpha.Length!=channels)throw new NotSupportedException("PReLU alpha must be per channel.");
                    result=first?graph.Node("PRelu",new[]{First(op.Inputs[0]),Floats(alpha,channels,1,1)}):graph.Node("PRelu",new[]{Value(op.Inputs[0]).Name,Floats(alpha,channels)});break;
                }
                case 14:result=graph.Node("Sigmoid",new[]{first?First(op.Inputs[0]):Value(op.Inputs[0]).Name});break;
                case 3: case 4: // CONV_2D, DEPTHWISE_CONV_2D
                {
                    var depthwise=op.Code==4;var w=Shape(op.Inputs[1]);var s=Shape(op.Inputs[0]);var weights=Data(op.Inputs[1]);
                    int o=w[0],kh=w[1],kw=w[2],i=w[3];
                    var sw=model.Int(op.Options,1);var sh=model.Int(op.Options,2);
                    var dw=model.Int(op.Options,depthwise?5:4,1);var dh=model.Int(op.Options,depthwise?6:5,1);
                    var activation=model.Byte(op.Options,depthwise?4:3);var same=model.Byte(op.Options,0)==0;
                    float[] kernel;long[] kernelShape;
                    if(depthwise)
                    {
                        if(model.Int(op.Options,3)!=1||s[3]!=target[3])throw new NotSupportedException("Depth multiplier must be one.");
                        var c=i;kernel=new float[c*kh*kw];for(var ch=0;ch<c;ch++)for(var y=0;y<kh;y++)for(var x=0;x<kw;x++)kernel[(ch*kh+y)*kw+x]=weights[(y*kw+x)*c+ch];
                        kernelShape=new long[]{c,1,kh,kw};
                    }
                    else
                    {
                        kernel=new float[o*i*kh*kw];for(var oc=0;oc<o;oc++)for(var y=0;y<kh;y++)for(var x=0;x<kw;x++)for(var ic=0;ic<i;ic++)kernel[((oc*i+ic)*kh+y)*kw+x]=weights[((oc*kh+y)*kw+x)*i+ic];
                        kernelShape=new long[]{o,i,kh,kw};
                    }
                    var pads=Pads(s,target,kh,kw,sh,sw,dh,dw,same);var groups=depthwise?i:1;
                    var conv=graph.Node("Conv",new[]{First(op.Inputs[0]),Floats(kernel,kernelShape),Floats(Data(op.Inputs[2]),target[3])},
                        a=>a.Ints("kernel_shape",kh,kw).Ints("strides",sh,sw).Ints("dilations",dh,dw).Ints("pads",pads).Int("group",groups));
                    result=Activate(conv,activation);break;
                }
                case 17: // MAX_POOL_2D
                {
                    var s=Shape(op.Inputs[0]);var sw=model.Int(op.Options,1);var sh=model.Int(op.Options,2);var kw=model.Int(op.Options,3);var kh=model.Int(op.Options,4);
                    var pads=Pads(s,target,kh,kw,sh,sw,1,1,model.Byte(op.Options,0)==0);
                    result=Activate(graph.Node("MaxPool",new[]{First(op.Inputs[0])},a=>a.Ints("kernel_shape",kh,kw).Ints("strides",sh,sw).Ints("pads",pads)),model.Byte(op.Options,5));break;
                }
                case 34: // PAD, NHWC pairs (before, after)
                {
                    var p=Data(op.Inputs[1]).Select(v=>(long)v).ToArray();
                    if(p.Length!=8)throw new NotSupportedException("Only NHWC padding supported.");
                    result=graph.Node("Pad",new[]{First(op.Inputs[0]),graph.Constant(p[0],p[6],p[2],p[4],p[1],p[7],p[3],p[5])});break;
                }
                case 23: // RESIZE_BILINEAR
                {
                    var align=model.Byte(op.Options,2)!=0;var half=model.Byte(op.Options,3)!=0;
                    var mode=align?"align_corners":half?"half_pixel":"asymmetric";
                    result=graph.Node("Resize",new[]{First(op.Inputs[0]),"","",graph.Constant(target[0],target[3],target[1],target[2])},a=>a.String("mode","linear").String("coordinate_transformation_mode",mode));break;
                }
                case 2: // CONCATENATION
                {
                    var axis=model.Int(op.Options,0);if(axis<0)axis+=target.Length;
                    var onnxAxis=first?new[]{0,2,3,1}[axis]:axis;
                    result=Activate(graph.Node("Concat",op.Inputs.Select(i=>first?First(i):Last(i)).ToArray(),a=>a.Int("axis",onnxAxis)),model.Byte(op.Options,1));break;
                }
                case 22: // RESHAPE, in the channel-last element order TFLite uses
                {
                    var reshaped=graph.Node("Reshape",new[]{Last(op.Inputs[0]),graph.Constant(target.Select(v=>(long)v).ToArray())});
                    result=first?ToFirst(reshaped):reshaped;break;
                }
                case 40: // MEAN over height and width
                {
                    var axes=Data(op.Inputs[1]);
                    if(Shape(op.Inputs[0]).Length!=4||axes.Length!=2||axes[0]!=1||axes[1]!=2)throw new NotSupportedException("Only global spatial mean supported.");
                    var mean=graph.Node("ReduceMean",new[]{First(op.Inputs[0])},a=>a.Ints("axes",2,3).Int("keepdims",0));
                    result=first?graph.Node("Reshape",new[]{mean,graph.Constant(target[0],target[3],1,1)}):graph.Node("Reshape",new[]{mean,graph.Constant(target.Select(v=>(long)v).ToArray())});break;
                }
                case 9: // FULLY_CONNECTED on the flattened channel-last input
                {
                    var w=Shape(op.Inputs[1]);var weights=Data(op.Inputs[1]);int rows=w[0],cols=w[1];
                    var transposed=new float[weights.Length];for(var r=0;r<rows;r++)for(var c=0;c<cols;c++)transposed[c*rows+r]=weights[r*cols+c];
                    var flat=graph.Node("Reshape",new[]{Last(op.Inputs[0]),graph.Constant(1,cols)});
                    var product=graph.Node("MatMul",new[]{flat,Floats(transposed,cols,rows)});
                    if(op.Inputs.Length>2&&op.Inputs[2]>=0)product=graph.Node("Add",new[]{product,Floats(Data(op.Inputs[2]),rows)});
                    result=graph.Node("Reshape",new[]{Activate(product,model.Byte(op.Options,0)),graph.Constant(target.Select(v=>(long)v).ToArray())});
                    first=false;break;
                }
                default:throw new NotSupportedException($"Operator {op.Code}");
            }
            values[output]=(result,first);
        }
        outputNames=model.Outputs.Select((_,i)=>"output"+i).ToArray();
        for(var i=0;i<model.Outputs.Length;i++)
        {
            var tensor=model.Outputs[i];graph.Output(outputNames[i],OnnxGraph.Float,Shape(tensor).Select(v=>(long)v).ToArray());
            graph.Identity(Shape(tensor).Length==4?Last(tensor):Value(tensor).Name,outputNames[i]);
        }
        return graph.Build("mediapipe-hands");
    }

    /// <summary>Dev check: agreement with the managed interpreter and time per call for both hand models.</summary>
    public static void Bench(string task,Action<string> report)
    {
        using var zip=new ZipArchive(File.OpenRead(task),ZipArchiveMode.Read);
        foreach(var name in new[]{"hand_detector.tflite","hand_landmarks_detector.tflite"})
        {
            using var stream=zip.GetEntry(name)!.Open();using var memory=new MemoryStream();stream.CopyTo(memory);var model=new LiteModel(memory.ToArray());
            var random=new Random(5);var length=model.Tensors[model.Inputs[0]].Shape.Aggregate(1,(a,b)=>a*b);
            var input=new float[length];for(var i=0;i<length;i++)input[i]=(float)random.NextDouble();
            var managed=new LiteInterpreter(model);var clock=Stopwatch.StartNew();var expected=managed.Run(input);for(var i=0;i<4;i++)expected=managed.Run(input);
            report($"{name}: managed {clock.Elapsed.TotalMilliseconds/5:F1} ms");
            foreach(var gpu in new[]{false,true})
            {
                if(gpu&&GpuBackbone.Device is null)continue;
                using var runner=new LiteOnnx(model,gpu);var actual=runner.Run(input,default);clock.Restart();for(var i=0;i<20;i++)actual=runner.Run(input,default);
                var worst=expected.Select((e,o)=>e.Select((v,i)=>Math.Abs(v-actual[o][i])).Max()).Max();
                report($"{name}: ONNX {runner.Device} {clock.Elapsed.TotalMilliseconds/20:F1} ms, max |diff| {worst:E2}");
            }
        }
    }
}
