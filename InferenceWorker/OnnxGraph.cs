using System.Text;

namespace HumanoidMocap.Worker;

/// <summary>Writes the small part of the ONNX format a transformer backbone needs, directly as protobuf
/// wire bytes. The published checkpoints are PyTorch files and their licences do not allow converted
/// copies to be redistributed, so the graph is assembled on the player's machine from the weights the
/// worker has already downloaded and verified. Weights go to an external data file beside the graph,
/// aligned for memory mapping; the serialized graph itself is a few hundred kilobytes.</summary>
public sealed class OnnxGraph
{
    public const int Float=1,Int64=7,Float16=10;
    readonly MemoryStream nodes=new(),initializers=new(),inputs=new(),outputs=new();
    readonly Stream? data;readonly string dataName;int counter;
    /// <param name="data">Receives the weights; <paramref name="dataName"/> is its file name next to the graph.
    /// Null keeps the weights inside the graph, for models well under the 2 GB protobuf limit.</param>
    public OnnxGraph(Stream? data,string dataName=""){this.data=data;this.dataName=dataName;}

    static void Varint(Stream s,ulong value){while(value>=0x80){s.WriteByte((byte)(value|0x80));value>>=7;}s.WriteByte((byte)value);}
    static void Tag(Stream s,int field,int wire)=>Varint(s,(ulong)(field<<3|wire));
    static void Int(Stream s,int field,long value){Tag(s,field,0);Varint(s,unchecked((ulong)value));}
    static void Bytes(Stream s,int field,ReadOnlySpan<byte> value){Tag(s,field,2);Varint(s,(ulong)value.Length);s.Write(value);}
    static void Text(Stream s,int field,string value)=>Bytes(s,field,Encoding.UTF8.GetBytes(value));
    static void Message(Stream s,int field,Action<Stream> write){using var body=new MemoryStream();write(body);Bytes(s,field,body.GetBuffer().AsSpan(0,(int)body.Length));}

    static void ValueInfo(Stream s,string name,int type,long[] shape)
    {
        Text(s,1,name);
        Message(s,2,t=>Message(t,1,tensor=>{Int(tensor,1,type);Message(tensor,2,dims=>{foreach(var d in shape)Message(dims,1,dim=>{if(d<0)Text(dim,2,Symbol);else Int(dim,1,d);});});}));
    }
    /// <summary>A negative dimension in <see cref="Input"/> or <see cref="Output"/> is this named, runtime-chosen length.</summary>
    public const string Symbol="length";
    public void Input(string name,int type,params long[] shape)=>Message(inputs,11,s=>ValueInfo(s,name,type,shape));
    public void Output(string name,int type,params long[] shape)=>Message(outputs,12,s=>ValueInfo(s,name,type,shape));
    /// <summary>A weight stored in the external data file.</summary>
    public string Weight(string name,int type,long[] shape,ReadOnlySpan<byte> bytes)
    {
        if(data is null)
        {
            var raw=bytes.ToArray();
            Message(initializers,5,t=>{foreach(var d in shape)Int(t,1,d);Int(t,2,type);Text(t,8,name);Bytes(t,9,raw);});
            return name;
        }
        var padding=(int)((4096-data.Position%4096)%4096);if(padding>0)data.Write(new byte[padding]);
        var offset=data.Position;var length=bytes.Length;data.Write(bytes);
        Message(initializers,5,t=>
        {
            foreach(var d in shape)Int(t,1,d);Int(t,2,type);Text(t,8,name);
            Message(t,13,e=>{Text(e,1,"location");Text(e,2,dataName);});
            Message(t,13,e=>{Text(e,1,"offset");Text(e,2,offset.ToString(System.Globalization.CultureInfo.InvariantCulture));});
            Message(t,13,e=>{Text(e,1,"length");Text(e,2,length.ToString(System.Globalization.CultureInfo.InvariantCulture));});
            Int(t,14,1); // EXTERNAL
        });
        return name;
    }
    /// <summary>A small integer constant stored in the graph, such as a reshape target.</summary>
    public string Constant(params long[] values)
    {
        var name="const_"+counter++;var raw=new byte[values.Length*8];Buffer.BlockCopy(values,0,raw,0,raw.Length);
        Message(initializers,5,t=>{Int(t,1,values.Length);Int(t,2,Int64);Text(t,8,name);Bytes(t,9,raw);});
        return name;
    }
    /// <summary>A small float vector stored in the graph, such as resize scales.</summary>
    public string Floats(params float[] values)
    {
        var name="floats_"+counter++;var raw=new byte[values.Length*4];Buffer.BlockCopy(values,0,raw,0,raw.Length);
        Message(initializers,5,t=>{Int(t,1,values.Length);Int(t,2,Float);Text(t,8,name);Bytes(t,9,raw);});
        return name;
    }
    /// <summary>A scalar in the graph's floating type.</summary>
    public string Scalar(float value,int type)
    {
        var name="scalar_"+counter++;
        var raw=type==Float16?BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)value)):BitConverter.GetBytes(value);
        Message(initializers,5,t=>{Int(t,2,type);Text(t,8,name);Bytes(t,9,raw);});
        return name;
    }
    public string Node(string op,string[] from,Action<Attributes>? attributes=null)=>Node(op,from,1,attributes)[0];
    public string[] Node(string op,string[] from,int results,Action<Attributes>? attributes=null)
    {
        var id=counter++;var produced=Enumerable.Range(0,results).Select(i=>$"{op}_{id}_{i}").ToArray();
        Message(nodes,1,n=>
        {
            foreach(var name in from)Text(n,1,name);foreach(var name in produced)Text(n,2,name);
            Text(n,3,$"{op}_{id}");Text(n,4,op);attributes?.Invoke(new(n));
        });
        return produced;
    }
    public sealed class Attributes(Stream node)
    {
        public Attributes Int(string name,long value){Message(node,5,a=>{Text(a,1,name);OnnxGraph.Int(a,3,value);OnnxGraph.Int(a,20,2);});return this;}
        public Attributes Float(string name,float value){Message(node,5,a=>{Text(a,1,name);Tag(a,2,5);a.Write(BitConverter.GetBytes(value));OnnxGraph.Int(a,20,1);});return this;}
        public Attributes String(string name,string value){Message(node,5,a=>{OnnxGraph.Text(a,1,name);OnnxGraph.Text(a,4,value);OnnxGraph.Int(a,20,3);});return this;}
        public Attributes Ints(string name,params long[] values){Message(node,5,a=>{Text(a,1,name);foreach(var v in values)OnnxGraph.Int(a,8,v);OnnxGraph.Int(a,20,7);});return this;}
    }
    /// <summary>Rename a produced value to a graph output.</summary>
    public void Identity(string from,string to)=>Message(nodes,1,n=>{Text(n,1,from);Text(n,2,to);Text(n,3,"out_"+to);Text(n,4,"Identity");});
    public byte[] Build(string name,int opset=17)
    {
        using var model=new MemoryStream();
        Int(model,1,8); // ir_version
        Text(model,2,"sbox-humanoid-mocap");
        Message(model,7,g=>
        {
            g.Write(nodes.GetBuffer(),0,(int)nodes.Length);Text(g,2,name);
            g.Write(initializers.GetBuffer(),0,(int)initializers.Length);
            g.Write(inputs.GetBuffer(),0,(int)inputs.Length);g.Write(outputs.GetBuffer(),0,(int)outputs.Length);
        });
        Message(model,8,o=>{Text(o,1,"");Int(o,2,opset);});
        return model.ToArray();
    }
}
