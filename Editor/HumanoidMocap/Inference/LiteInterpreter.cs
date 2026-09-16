using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace HumanoidMocap.Inference;

/// <summary>Managed float32 kernels for the pinned MediaPipe hand models only.
/// Unsupported operators fail closed. One interpreter instance processes one frame at a time.</summary>
public sealed class LiteInterpreter
{
    readonly LiteModel model;
    readonly float[][] values;
    public int Threads { get; set; }
    public LiteInterpreter(LiteModel model)
    {
        Threads=Math.Min(4,Environment.ProcessorCount);this.model=model;values=model.Tensors.Select(t=>t.Values.ToArray()).ToArray();
        var supported=new[]{0,2,3,4,6,9,14,17,22,23,34,40,54};
        foreach(var op in model.Operators)
            if(!supported.Contains(op.Code))throw new NotSupportedException($"Managed kernel {op.Code} has not been implemented.");
        foreach(var op in model.Operators.Where(o=>o.Code==6))values[op.Outputs[0]]=values[op.Inputs[0]];
    }
    public float[][] Run(float[] input,CancellationToken cancellation=default)
    {
        if(model.Inputs.Length!=1||input.Length!=values[model.Inputs[0]].Length)throw new ArgumentException("Input shape mismatch.");
        Array.Copy(input,values[model.Inputs[0]],input.Length);
        var options=new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(Threads,1,16),CancellationToken=cancellation};
        foreach(var op in model.Operators)
        {
            cancellation.ThrowIfCancellationRequested();
            var a=values[op.Inputs[0]];var output=values[op.Outputs[0]];var shape=model.Tensors[op.Inputs[0]].Shape;var target=model.Tensors[op.Outputs[0]].Shape;
            switch(op.Code)
            {
                case 6:break;
                case 22:Array.Copy(a,output,output.Length);break;
                case 0:
                    var b=values[op.Inputs[1]];
                    if(a.Length!=output.Length || b.Length!=output.Length)throw new NotSupportedException("Broadcast ADD is not used by the pinned model.");
                    for(var i=0;i<output.Length;i++)output[i]=Activate(a[i]+b[i],model.Byte(op.Options,0));break;
                case 54:
                    var alpha=values[op.Inputs[1]];
                    for(var i=0;i<output.Length;i++)output[i]=a[i]>=0?a[i]:a[i]*alpha[i%alpha.Length];break;
                case 14:
                    for(var i=0;i<output.Length;i++)output[i]=1/(1+MathF.Exp(-a[i]));break;
                case 3:Conv(op,false,options);break;
                case 4:Conv(op,true,options);break;
                case 17:Pool(op);break;
                case 34:
                    var padding=values[op.Inputs[1]];Array.Clear(output,0,output.Length);
                    if(shape.Length!=4||padding.Length!=8)throw new NotSupportedException("Only NHWC padding supported.");
                    for(var n=0;n<shape[0];n++)for(var y=0;y<shape[1];y++)for(var x=0;x<shape[2];x++)for(var c=0;c<shape[3];c++)
                        output[(((n+(int)padding[0])*target[1]+y+(int)padding[2])*target[2]+x+(int)padding[4])*target[3]+c+(int)padding[6]]=a[((n*shape[1]+y)*shape[2]+x)*shape[3]+c];
                    break;
                case 23:
                    var align=model.Byte(op.Options,0)!=0;var half=model.Byte(op.Options,1)!=0;
                    var sy=align && target[1]>1?(shape[1]-1f)/(target[1]-1):(float)shape[1]/target[1];
                    var sx=align && target[2]>1?(shape[2]-1f)/(target[2]-1):(float)shape[2]/target[2];
                    for(var y=0;y<target[1];y++)for(var x=0;x<target[2];x++)
                    {
                        var fy=Math.Max(0,(y+(half?.5f:0))*sy-(half?.5f:0));var fx=Math.Max(0,(x+(half?.5f:0))*sx-(half?.5f:0));
                        var y0=Math.Min((int)fy,shape[1]-1);var x0=Math.Min((int)fx,shape[2]-1);var y1=Math.Min(y0+1,shape[1]-1);var x1=Math.Min(x0+1,shape[2]-1);
                        for(var c=0;c<shape[3];c++)
                        {
                            var p00=a[(y0*shape[2]+x0)*shape[3]+c];var p01=a[(y0*shape[2]+x1)*shape[3]+c];
                            var p10=a[(y1*shape[2]+x0)*shape[3]+c];var p11=a[(y1*shape[2]+x1)*shape[3]+c];
                            output[(y*target[2]+x)*shape[3]+c]=(p00+(p01-p00)*(fx-x0))*(1-(fy-y0))+(p10+(p11-p10)*(fx-x0))*(fy-y0);
                        }
                    }
                    break;
                case 2:
                    var axis=model.Int(op.Options,0);if(axis<0)axis+=target.Length;
                    var inner=target.Skip(axis+1).Aggregate(1,(x,y)=>x*y);var outer=target.Take(axis).Aggregate(1,(x,y)=>x*y);
                    var dest=0;for(var n=0;n<outer;n++)foreach(var index in op.Inputs)
                    {var length=model.Tensors[index].Shape[axis]*inner;Array.Copy(values[index],n*length,output,dest,length);dest+=length;}
                    break;
                case 40:
                    var axes=values[op.Inputs[1]];
                    if(shape.Length!=4 || axes.Length!=2 || axes[0]!=1 || axes[1]!=2)throw new NotSupportedException("Only global spatial mean supported.");
                    Array.Clear(output,0,output.Length);for(var i=0;i<a.Length;i++)output[i%shape[3]]+=a[i]/(shape[1]*shape[2]);break;
                case 9:
                    var weights=values[op.Inputs[1]];var bias=op.Inputs.Length>2&&op.Inputs[2]>=0?values[op.Inputs[2]]:null;
                    var wshape=model.Tensors[op.Inputs[1]].Shape;
                    for(var o=0;o<output.Length;o++)output[o]=Activate(Dot(a,0,weights,o*wshape[1],wshape[1])+(bias?[o]??0),model.Byte(op.Options,0));break;
                default:throw new NotSupportedException($"Kernel {op.Code}");
            }
        }
        return model.Outputs.Select(i=>values[i].ToArray()).ToArray();
    }
    static float Activate(float x,int kind)=>kind switch{0=>x,1=>Math.Max(0,x),2=>Math.Clamp(x,-1,1),3=>Math.Clamp(x,0,6),_=>throw new NotSupportedException($"Activation {kind}")};
    static float Dot(float[] a,int ai,float[] b,int bi,int length)
    {
        var sum=Vector<float>.Zero;var i=0;var width=Vector<float>.Count;
        for(;i<=length-width;i+=width)sum+=new Vector<float>(a,ai+i)*new Vector<float>(b,bi+i);
        float total=Vector.Dot(sum,Vector<float>.One);for(;i<length;i++)total+=a[ai+i]*b[bi+i];return total;
    }
    void Conv(LiteModel.Operator op,bool depthwise,ParallelOptions options)
    {
        var input=values[op.Inputs[0]];var weights=values[op.Inputs[1]];var bias=values[op.Inputs[2]];var output=values[op.Outputs[0]];
        var s=model.Tensors[op.Inputs[0]].Shape;var w=model.Tensors[op.Inputs[1]].Shape;var d=model.Tensors[op.Outputs[0]].Shape;
        var ih=s[1];var iw=s[2];var ic=s[3];var oh=d[1];var ow=d[2];var oc=d[3];var kh=w[1];var kw=w[2];
        var sw=model.Int(op.Options,1);var sh=model.Int(op.Options,2);
        var dw=model.Int(op.Options,depthwise?5:4,1);var dh=model.Int(op.Options,depthwise?6:5,1);
        var activation=model.Byte(op.Options,depthwise?4:3);
        var same=model.Byte(op.Options,0)==0;
        var ph=same?Math.Max(0,(oh-1)*sh+(kh-1)*dh+1-ih)/2:0;var pw=same?Math.Max(0,(ow-1)*sw+(kw-1)*dw+1-iw)/2:0;
        if(depthwise && (model.Int(op.Options,3)!=1 || ic!=oc))throw new NotSupportedException("Depth multiplier must be one.");
        Parallel.For(0,oh,options,y=>
        {
            for(var x=0;x<ow;x++)
            {
                var destination=(y*ow+x)*oc;
                if(depthwise)
                {
                    Array.Copy(bias,0,output,destination,oc);
                    for(var ky=0;ky<kh;ky++)
                    {
                        var iy=y*sh-ph+ky*dh;if(iy<0||iy>=ih)continue;
                        for(var kx=0;kx<kw;kx++)
                        {
                            var ix=x*sw-pw+kx*dw;if(ix<0||ix>=iw)continue;
                            var si=(iy*iw+ix)*ic;var wi=(ky*kw+kx)*oc;
                            var c=0;for(;c<=oc-Vector<float>.Count;c+=Vector<float>.Count)
                            {var v=new Vector<float>(output,destination+c)+new Vector<float>(input,si+c)*new Vector<float>(weights,wi+c);v.CopyTo(output,destination+c);}
                            for(;c<oc;c++)output[destination+c]+=input[si+c]*weights[wi+c];
                        }
                    }
                    for(var c=0;c<oc;c++)output[destination+c]=Activate(output[destination+c],activation);
                }
                else for(var c=0;c<oc;c++)
                {
                    var sum=bias[c];for(var ky=0;ky<kh;ky++)
                    {
                        var iy=y*sh-ph+ky*dh;if(iy<0||iy>=ih)continue;
                        for(var kx=0;kx<kw;kx++)
                        {var ix=x*sw-pw+kx*dw;if(ix<0||ix>=iw)continue;sum+=Dot(input,(iy*iw+ix)*ic,weights,((c*kh+ky)*kw+kx)*ic,ic);}
                    }
                    output[destination+c]=Activate(sum,activation);
                }
            }
        });
    }
    void Pool(LiteModel.Operator op)
    {
        var a=values[op.Inputs[0]];var output=values[op.Outputs[0]];var s=model.Tensors[op.Inputs[0]].Shape;var d=model.Tensors[op.Outputs[0]].Shape;
        var sw=model.Int(op.Options,1);var sh=model.Int(op.Options,2);var kw=model.Int(op.Options,3);var kh=model.Int(op.Options,4);
        var same=model.Byte(op.Options,0)==0;var ph=same?Math.Max(0,(d[1]-1)*sh+kh-s[1])/2:0;var pw=same?Math.Max(0,(d[2]-1)*sw+kw-s[2])/2:0;
        for(var y=0;y<d[1];y++)for(var x=0;x<d[2];x++)for(var c=0;c<s[3];c++)
        {
            var max=float.NegativeInfinity;for(var ky=0;ky<kh;ky++)for(var kx=0;kx<kw;kx++)
            {var iy=y*sh-ph+ky;var ix=x*sw-pw+kx;if(iy>=0&&iy<s[1]&&ix>=0&&ix<s[2])max=Math.Max(max,a[(iy*s[2]+ix)*s[3]+c]);}
            output[(y*d[2]+x)*s[3]+c]=Activate(max,model.Byte(op.Options,5));
        }
    }
}
