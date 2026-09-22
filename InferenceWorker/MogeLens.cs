// Lens estimation adapted from MoGe-2 (github.com/microsoft/MoGe, moge/model/v2.py and
// moge/utils/geometry_torch.py) with its DINOv2 ViT-S/14 encoder.
using System.Diagnostics;
using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>Estimates the recording lens from the picture with MoGe-2 (ViT-S): the network predicts an
/// affine-invariant point map, and the focal length that best projects it back onto the image plane is the
/// lens. A lens guessed from the picture size alone was wrong by a factor of two on wide phone footage and
/// changed with every crop; MoGe's estimate of the same camera differed by 2.5% between a clip and a 20%
/// crop of it. Runs on a handful of frames, on the graphics card when there is one.</summary>
public sealed class MogeLens : IDisposable
{
    public const string Version="moge2-vits-lens-v1";
    public const string CheckpointSha256="79a16621928c2bf0ed04659218c55c01075e950507f40bb3332fb4c873d3e1dc";
    /// <summary>MoGe's default resolution level: 3,600 base tokens.</summary>
    public const int BaseTokens=3600;
    const int Width=384,Heads=6,HeadWidth=64,Blocks=12,Patch=14;
    static readonly int[] Levels={384,256,128,64,32};
    readonly InferenceSession session;readonly int rows,cols;readonly double aspect;
    public string Device { get; }
    public int InputWidth=>cols*Patch;public int InputHeight=>rows*Patch;

    public MogeLens(string checkpointPath,int imageWidth,int imageHeight,bool gpu=true)
    {
        if(!FileChecksum.Matches(checkpointPath,CheckpointSha256))throw new InvalidDataException("MoGe checkpoint checksum mismatch.");
        aspect=(double)imageWidth/imageHeight;rows=(int)Math.Round(Math.Sqrt(BaseTokens/aspect));cols=(int)Math.Round(Math.Sqrt(BaseTokens*aspect));
        var model=Build(checkpointPath,rows,cols,aspect);
        using var options=new SessionOptions{GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL,LogSeverityLevel=OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR};
        if(gpu&&GpuBackbone.Device is { } device){options.EnableMemoryPattern=false;options.ExecutionMode=ExecutionMode.ORT_SEQUENTIAL;options.AppendExecutionProvider_DML(device.Index);Device=device.Name;}
        else{options.IntraOpNumThreads=Math.Clamp(Environment.ProcessorCount/2,1,8);Device="CPU";}
        session=new InferenceSession(model,options);
    }
    public void Dispose()=>session.Dispose();

    /// <summary>Horizontal focal length in pixels of <paramref name="frame"/>, or null when the picture gives too little to go on.</summary>
    public double? FocalPixels(DecodedVideoFrame frame)
    {
        // RGB in [0,1], resized to the token grid; MoGe antialiases when shrinking, which area resampling approximates.
        using var rgba=Mat.FromPixelData(frame.Height,frame.Width,MatType.CV_8UC4,frame.Rgba);using var resized=new Mat();
        Cv2.Resize(rgba,resized,new Size(InputWidth,InputHeight),0,0,InputWidth<frame.Width?InterpolationFlags.Area:InterpolationFlags.Linear);
        float[] mean={.485f,.456f,.406f},std={.229f,.224f,.225f};var plane=InputWidth*InputHeight;var input=new float[3*plane];
        var pixels=new byte[plane*4];Marshal.Copy(resized.Data,pixels,0,pixels.Length);
        for(var i=0;i<plane;i++)for(var c=0;c<3;c++)input[c*plane+i]=(pixels[i*4+c]/255f-mean[c])/std[c];
        using var value=OrtValue.CreateTensorValueFromMemory(input,new long[]{1,3,InputHeight,InputWidth});using var run=new RunOptions();
        using var outputs=session.Run(run,new[]{"image"},new[]{value},new[]{"points","mask"});
        var points=outputs[0].GetTensorDataAsSpan<float>().ToArray();var mask=outputs[1].GetTensorDataAsSpan<float>().ToArray();
        return Recover(points,mask,rows*16,cols*16,frame.Width,frame.Height);
    }

    /// <summary>MoGe's recover_focal_shift on the image-size maps, sampled at its 64x64 nearest-neighbour grid:
    /// the point map is bilinearly resized from the head's resolution, remapped (xy e^z, e^z) and fitted.</summary>
    public static double? Recover(float[] points,float[] mask,int headHeight,int headWidth,int width,int height)
    {
        var aspect=(double)width/height;var diagonal=Math.Sqrt(1+aspect*aspect);var spanX=aspect/diagonal;var spanY=1/diagonal;
        float Sample(float[] map,int channel,int px,int py)
        {
            // Bilinear, align_corners false, source clamped at zero as PyTorch does.
            var sx=Math.Max(0,(px+.5)*headWidth/width-.5);var sy=Math.Max(0,(py+.5)*headHeight/height-.5);
            int x0=Math.Min((int)sx,headWidth-1),y0=Math.Min((int)sy,headHeight-1),x1=Math.Min(x0+1,headWidth-1),y1=Math.Min(y0+1,headHeight-1);
            var fx=(float)(sx-x0);var fy=(float)(sy-y0);var o=channel*headWidth*headHeight;
            float At(int x,int y)=>map[o+y*headWidth+x];
            return (At(x0,y0)*(1-fx)+At(x1,y0)*fx)*(1-fy)+(At(x0,y1)*(1-fx)+At(x1,y1)*fx)*fy;
        }
        var uv=new List<(double U,double V)>();var xyz=new List<(double X,double Y,double Z)>();
        for(var i=0;i<64;i++)for(var j=0;j<64;j++)
        {
            int py=Math.Min(i*height/64,height-1),px=Math.Min(j*width/64,width-1);
            if(!(Sample(mask,0,px,py)>0))continue; // sigmoid > 0.5
            var z=Math.Exp(Sample(points,2,px,py));
            var u=-spanX*(width-1)/width+2*spanX*(width-1)/width*px/Math.Max(1,width-1);
            var v=-spanY*(height-1)/height+2*spanY*(height-1)/height*py/Math.Max(1,height-1);
            uv.Add((u,v));xyz.Add((Sample(points,0,px,py)*z,Sample(points,1,px,py)*z,z));
        }
        if(uv.Count<16)return null;
        double Focal(double shift)
        {
            double num=0,den=0;
            for(var k=0;k<uv.Count;k++){var d=xyz[k].Z+shift;var px_=xyz[k].X/d;var py_=xyz[k].Y/d;num+=px_*uv[k].U+py_*uv[k].V;den+=px_*px_+py_*py_;}
            return num/den;
        }
        double Cost(double shift)
        {
            var f=Focal(shift);double c=0;
            for(var k=0;k<uv.Count;k++){var d=xyz[k].Z+shift;var ex=f*xyz[k].X/d-uv[k].U;var ey=f*xyz[k].Y/d-uv[k].V;c+=ex*ex+ey*ey;}
            return double.IsFinite(c)?c:double.PositiveInfinity;
        }
        // One-parameter Levenberg-Marquardt from zero, as scipy's least_squares(method='lm', ftol=1e-3).
        var minZ=xyz.Min(p=>p.Z);double s=0,cost=Cost(0),lambda=1e-3;
        for(var iteration=0;iteration<100;iteration++)
        {
            var h=Math.Max(1e-6,Math.Abs(s)*1e-6);var g=(Cost(s+h)-Cost(s-h))/(2*h);var curvature=(Cost(s+h)-2*cost+Cost(s-h))/(h*h);
            if(!double.IsFinite(g)||!double.IsFinite(curvature))break;
            var step=-g/(Math.Max(curvature,1e-12)*(1+lambda));var next=s+step;
            if(next<=-minZ*.99)next=(s-minZ*.99)/2;
            var nextCost=Cost(next);
            if(nextCost<cost)
            {
                var improvement=(cost-nextCost)/Math.Max(cost,1e-30);s=next;cost=nextCost;lambda/=10;
                if(improvement<1e-3)break;
            }
            else{lambda*=10;if(lambda>1e8)break;}
        }
        var focal=Focal(s);
        // Focal relative to half the diagonal, to pixels across the width.
        var fxNormalized=focal/2*diagonal/aspect;var pixelsAcross=fxNormalized*width;
        return double.IsFinite(pixelsAcross)&&pixelsAcross>0?pixelsAcross:null;
    }

    /// <summary>The median lens over up to <paramref name="samples"/> frames spread across the range, as a horizontal FOV in degrees.</summary>
    public static float? EstimateHorizontalFov(string checkpoint,string video,IReadOnlyList<double> times,int width,int height,int samples,CancellationToken cancellation,Action<string>? progress=null)
    {
        var wanted=Enumerable.Range(0,Math.Min(samples,times.Count)).Select(i=>times[(int)Math.Round(i*(times.Count-1)/Math.Max(1d,Math.Min(samples,times.Count)-1))]).Distinct().ToList();
        using var lens=new MogeLens(checkpoint,width,height);using var decoder=new WindowsVideoDecoder(video);var focals=new List<double>();
        foreach(var time in wanted)
        {
            cancellation.ThrowIfCancellationRequested();DecodedVideoFrame? frame;
            do{frame=decoder.Read(cancellation);if(frame is null)break;}while(frame.Time<time-.00001);
            if(frame is null)break;
            progress?.Invoke($"Estimating the camera lens · {focals.Count+1}/{wanted.Count}");
            if(lens.FocalPixels(frame) is double f)focals.Add(f);
        }
        if(focals.Count==0)return null;
        focals.Sort();var median=focals[focals.Count/2];
        var fov=(float)(2*Math.Atan(width/2d/median)*180/Math.PI);
        return fov is >=20 and <=150?fov:null;
    }

    static byte[] Build(string checkpointPath,int rows,int cols,double aspect)
    {
        using var checkpoint=new TorchCheckpoint(checkpointPath,"model");
        var graph=new OnnxGraph(null);var n=0;
        float[] Read(string name)=>checkpoint.ReadFloat(name);
        long[] ShapeOf(string name)=>checkpoint.Tensors[name].Shape.Select(v=>(long)v).ToArray();
        string Const(float[] data,params long[] shape)=>graph.Weight("c"+n++,OnnxGraph.Float,shape,MemoryMarshal.AsBytes(data.AsSpan()));
        string Load(string name)=>graph.Weight(name,OnnxGraph.Float,ShapeOf(name),MemoryMarshal.AsBytes(Read(name).AsSpan()));
        string Linear(string x,string name)
        {
            var shape=ShapeOf(name+".weight");int r=(int)shape[0],c=(int)shape[1];var w=Read(name+".weight");var t=new float[w.Length];
            for(var i=0;i<r;i++)for(var j=0;j<c;j++)t[j*r+i]=w[i*c+j];
            return graph.Node("Add",new[]{graph.Node("MatMul",new[]{x,Const(t,c,r)}),Load(name+".bias")});
        }
        string Norm(string x,string name)=>graph.Node("LayerNormalization",new[]{x,Load(name+".weight"),Load(name+".bias")},a=>a.Int("axis",-1).Float("epsilon",1e-6f));
        string Conv1(string x,string name)=>graph.Node("Conv",new[]{x,Load(name+".weight"),Load(name+".bias")},a=>a.Ints("kernel_shape",1,1));
        // 3x3 convolution with replicate padding.
        string Conv3(string x,string name)=>graph.Node("Conv",new[]{graph.Node("Pad",new[]{x,graph.Constant(0,0,1,1,0,0,1,1)},a=>a.String("mode","edge")),Load(name+".weight"),Load(name+".bias")},a=>a.Ints("kernel_shape",3,3));
        string Relu(string x)=>graph.Node("Relu",new[]{x});

        graph.Input("image",OnnxGraph.Float,1,3,rows*Patch,cols*Patch);
        const string backbone="encoder.backbone.";var tokens=rows*cols;
        var x=graph.Node("Conv",new[]{"image",Load(backbone+"patch_embed.proj.weight"),Load(backbone+"patch_embed.proj.bias")},a=>a.Ints("kernel_shape",Patch,Patch).Ints("strides",Patch,Patch));
        x=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{x,graph.Constant(1,Width,tokens)})},a=>a.Ints("perm",0,2,1));
        x=graph.Node("Concat",new[]{Load(backbone+"cls_token"),x},a=>a.Int("axis",1));
        x=graph.Node("Add",new[]{x,Const(Positions(Read(backbone+"pos_embed"),rows,cols),1,tokens+1,Width)});
        var scale=graph.Scalar(1f/MathF.Sqrt(HeadWidth),OnnxGraph.Float);var rootHalf=graph.Scalar((float)(1/Math.Sqrt(2)),OnnxGraph.Float);
        var half=graph.Scalar(.5f,OnnxGraph.Float);var one=graph.Scalar(1f,OnnxGraph.Float);
        var heads=graph.Constant(1,tokens+1,Heads,HeadWidth);var flat=graph.Constant(1,tokens+1,Width);
        var intermediate=new List<string>();
        for(var block=0;block<Blocks;block++)
        {
            var name=backbone+"blocks."+block;
            var parts=graph.Node("Split",new[]{Linear(Norm(x,name+".norm1"),name+".attn.qkv")},3,a=>a.Int("axis",-1));
            var q=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{parts[0],heads})},a=>a.Ints("perm",0,2,1,3));
            var k=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{parts[1],heads})},a=>a.Ints("perm",0,2,3,1));
            var v=graph.Node("Transpose",new[]{graph.Node("Reshape",new[]{parts[2],heads})},a=>a.Ints("perm",0,2,1,3));
            var attention=graph.Node("MatMul",new[]{graph.Node("Softmax",new[]{graph.Node("Mul",new[]{graph.Node("MatMul",new[]{q,k}),scale})},a=>a.Int("axis",-1)),v});
            attention=graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{attention},a=>a.Ints("perm",0,2,1,3)),flat});
            x=graph.Node("Add",new[]{x,graph.Node("Mul",new[]{Linear(attention,name+".attn.proj"),Load(name+".ls1.gamma")})});
            var hidden=Linear(Norm(x,name+".norm2"),name+".mlp.fc1");
            var gelu=graph.Node("Mul",new[]{graph.Node("Mul",new[]{hidden,half}),graph.Node("Add",new[]{graph.Node("Erf",new[]{graph.Node("Mul",new[]{hidden,rootHalf})}),one})});
            x=graph.Node("Add",new[]{x,graph.Node("Mul",new[]{Linear(gelu,name+".mlp.fc2"),Load(name+".ls2.gamma")})});
            if(block is 5 or 11)intermediate.Add(x);
        }
        // Intermediate layers 5 and 11, normalized, class token dropped, projected and summed.
        string? features=null;
        for(var i=0;i<intermediate.Count;i++)
        {
            var normed=Norm(intermediate[i],backbone+"norm");
            var patches=graph.Node("Slice",new[]{normed,graph.Constant(1),graph.Constant(tokens+1),graph.Constant(1)});
            var map=graph.Node("Reshape",new[]{graph.Node("Transpose",new[]{patches},a=>a.Ints("perm",0,2,1)),graph.Constant(1,Width,rows,cols)});
            var projected=Conv1(map,"encoder.output_projections."+i);
            features=features is null?projected:graph.Node("Add",new[]{features,projected});
        }
        var uv=Enumerable.Range(0,5).Select(level=>Const(ViewPlaneUv(cols<<level,rows<<level,aspect),1,2,rows<<level,cols<<level)).ToArray();
        string[] Stack(string prefix,string[] inputs,bool neck)
        {
            var outputs=new string[5];string? current=null;
            for(var level=0;level<5;level++)
            {
                var feature=Conv1(inputs[level],prefix+".input_blocks."+level);
                current=current is null?feature:graph.Node("Add",new[]{current,feature});
                if(level is 1 or 2 or 3)
                {
                    // Residual block without norms: relu, 3x3, relu, 3x3, plus the input.
                    var block=prefix+".res_blocks."+level+".0.layers.";
                    current=graph.Node("Add",new[]{Conv3(Relu(Conv3(Relu(current),block+"2")),block+"5"),current});
                }
                outputs[level]=current;
                if(level<4)
                {
                    var resampler=prefix+".resamplers."+level;
                    current=level<3
                        ?graph.Node("ConvTranspose",new[]{current,Load(resampler+".0.weight"),Load(resampler+".0.bias")},a=>a.Ints("kernel_shape",2,2).Ints("strides",2,2))
                        :graph.Node("Resize",new[]{current,"",graph.Floats(1,1,2,2)},a=>a.String("mode","linear").String("coordinate_transformation_mode","half_pixel"));
                    current=Conv3(current,resampler+".1");
                }
            }
            return outputs;
        }
        var levels=Stack("neck",new[]{graph.Node("Concat",new[]{features!,uv[0]},a=>a.Int("axis",1)),uv[1],uv[2],uv[3],uv[4]},true);
        var pointsOut=Conv1(Stack("points_head",levels,false)[4],"points_head.output_blocks.4");
        var maskOut=Conv1(Stack("mask_head",levels,false)[4],"mask_head.output_blocks.4");
        graph.Output("points",OnnxGraph.Float,1,3,rows*16,cols*16);graph.Output("mask",OnnxGraph.Float,1,1,rows*16,cols*16);
        graph.Identity(pointsOut,"points");graph.Identity(maskOut,"mask");
        return graph.Build("moge2-lens");
    }

    /// <summary>DINOv2's position embedding resized to the token grid: bicubic (a = -0.75), align_corners false, with its
    /// 0.1 interpolation offset folded into the scale factor, then the class position prepended.</summary>
    static float[] Positions(float[] embedding,int rows,int cols)
    {
        var grid=(int)Math.Sqrt(embedding.Length/Width-1);
        double sy=(rows+.1)/grid,sx=(cols+.1)/grid;var result=new float[(rows*cols+1)*Width];
        Array.Copy(embedding,0,result,0,Width);
        static double Cubic(double t){t=Math.Abs(t);const double a=-.75;return t<=1?((a+2)*t-(a+3))*t*t+1:t<2?((a*t-5*a)*t+8*a)*t-4*a:0;}
        for(var r=0;r<rows;r++)for(var c=0;c<cols;c++)
        {
            var y=(r+.5)/sy-.5;var x=(c+.5)/sx-.5;int y0=(int)Math.Floor(y),x0=(int)Math.Floor(x);
            for(var ch=0;ch<Width;ch++)
            {
                double sum=0;
                for(var m=-1;m<=2;m++)for(var k=-1;k<=2;k++)
                {
                    var yy=Math.Clamp(y0+m,0,grid-1);var xx=Math.Clamp(x0+k,0,grid-1);
                    sum+=Cubic(y-(y0+m))*Cubic(x-(x0+k))*embedding[(1+yy*grid+xx)*Width+ch];
                }
                result[(1+r*cols+c)*Width+ch]=(float)sum;
            }
        }
        return result;
    }
    /// <summary>MoGe's normalized_view_plane_uv as a 2xHxW map (u then v).</summary>
    static float[] ViewPlaneUv(int width,int height,double aspect)
    {
        var diagonal=Math.Sqrt(1+aspect*aspect);var spanX=aspect/diagonal*(width-1)/width;var spanY=1/diagonal*(height-1)/height;var map=new float[2*width*height];
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            map[y*width+x]=(float)(-spanX+2*spanX*x/Math.Max(1,width-1));
            map[width*height+y*width+x]=(float)(-spanY+2*spanY*y/Math.Max(1,height-1));
        }
        return map;
    }
}
