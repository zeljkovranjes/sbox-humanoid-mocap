using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HumanoidMocap.Inference;

/// <summary>Read-only subset of the documented TFLite flatbuffer format. No native runtime.</summary>
public sealed class LiteModel
{
    public sealed record Tensor(string Name,int[] Shape,int Type,float[] Values,bool Constant);
    public sealed record Operator(int Code,int[] Inputs,int[] Outputs,int Options);
    public List<Tensor> Tensors { get; } = new();
    public List<Operator> Operators { get; } = new();
    public int[] Inputs { get; }
    public int[] Outputs { get; }
    readonly byte[] data;
    int I(int p)=>BitConverter.ToInt32(data,p);
    ushort U(int p)=>BitConverter.ToUInt16(data,p);
    public int Field(int table,int field)
    {
        if(table==0)return 0;var vt=table-I(table);var index=4+field*2;
        return index<U(vt) && U(vt+index)!=0?table+U(vt+index):0;
    }
    int Ref(int at)=>at==0?0:at+I(at);
    public int Int(int table,int field,int fallback=0){var p=Field(table,field);return p==0?fallback:I(p);}
    public int Byte(int table,int field,int fallback=0){var p=Field(table,field);return p==0?fallback:data[p];}
    int[] Array(int table,int field,bool refs=false)
    {
        var p=Ref(Field(table,field));if(p==0)return System.Array.Empty<int>();
        var n=I(p);if(n<0||n>1000000)throw new FormatException("Invalid model vector size.");
        return Enumerable.Range(0,n).Select(i=>refs?Ref(p+4+i*4):I(p+4+i*4)).ToArray();
    }
    string String(int table,int field)
    {
        var p=Ref(Field(table,field));return p==0?"":Encoding.UTF8.GetString(data,p+4,I(p));
    }
    public LiteModel(byte[] bytes)
    {
        data=bytes;
        if(bytes.Length<8||Encoding.ASCII.GetString(bytes,4,4)!="TFL3")throw new FormatException("Not a TFLite model.");
        var root=I(0);var codes=Array(root,1,true);var subgraphs=Array(root,2,true);var buffers=Array(root,4,true);
        if(subgraphs.Length!=1)throw new NotSupportedException("Only single-graph inference is supported.");
        var graph=subgraphs[0];Inputs=Array(graph,1);Outputs=Array(graph,2);
        long total=0;
        foreach(var t in Array(graph,0,true))
        {
            var shape=Array(t,0);var count=shape.Aggregate(1,(a,b)=>checked(a*b));total+=count;
            if(count<0||total>200000000)throw new FormatException("Model exceeds managed inference budget.");
            var type=Byte(t,1);var buffer=Int(t,2);var offset=Ref(Field(buffers[buffer],0));var length=offset==0?0:I(offset);
            var values=new float[count];
            if(length>0)
            {
                var stride=type switch{0 or 2=>4,1=>2,_=>throw new NotSupportedException($"Tensor type {type} is not supported.")};
                if(length!=count*stride)throw new FormatException("Tensor size mismatch.");
                for(var i=0;i<count;i++)values[i]=type switch
                {0=>BitConverter.ToSingle(data,offset+4+i*4),1=>HalfToFloat(U(offset+4+i*2)),2=>I(offset+4+i*4),_=>0};
            }
            Tensors.Add(new(String(t,3),shape,type,values,length>0));
        }
        foreach(var op in Array(graph,3,true))
        {
            var code=codes[Int(op,0)];var builtin=Int(code,3,Byte(code,0));
            Operators.Add(new(builtin,Array(op,1),Array(op,2),Ref(Field(op,4))));
        }
    }
    static float HalfToFloat(ushort bits)
    {
        var sign=(bits&0x8000)==0?1f:-1f;var exponent=(bits>>10)&31;var mantissa=bits&1023;
        if(exponent==0)return sign*MathF.Pow(2,-14)*(mantissa/1024f);
        if(exponent==31)return mantissa==0?sign*float.PositiveInfinity:float.NaN;
        return sign*MathF.Pow(2,exponent-15)*(1+mantissa/1024f);
    }
}
