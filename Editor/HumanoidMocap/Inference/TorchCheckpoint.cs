#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace HumanoidMocap.Inference;

/// <summary>Reads tensor data from the pinned PyTorch ZIP checkpoint format without Python,
/// importing modules, invoking pickle callables, or deserializing executable objects.</summary>
public sealed class TorchCheckpoint : IDisposable
{
    public sealed record TensorInfo(string Name,string Storage,string Dtype,long StorageLength,long Offset,int[] Shape,long[] Stride);
    sealed record Symbol(string Module,string Name);
    sealed record StorageRef(string Key,string Dtype,long Count);
    sealed record TensorRef(StorageRef Storage,long Offset,int[] Shape,long[] Stride);
    readonly ZipArchive archive;
    readonly string prefix;
    readonly Dictionary<string,TensorInfo> tensors=new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string,TensorInfo> Tensors=>tensors;
    const int MaximumMetadataBytes=32*1024*1024;
    const long MaximumTensorElements=1_000_000_000;

    public TorchCheckpoint(string path)
    {
        archive=ZipFile.OpenRead(path);
        try
        {
            var entries=archive.Entries.Where(e=>e.FullName.EndsWith("/data.pkl",StringComparison.Ordinal)).ToArray();
            if(entries.Length!=1)throw new InvalidDataException("Expected exactly one PyTorch data.pkl entry.");
            var entry=entries[0];prefix=entry.FullName.Substring(0,entry.FullName.Length-8);
            if(entry.Length>MaximumMetadataBytes)throw new InvalidDataException("Checkpoint metadata exceeds the 32 MiB limit.");
            if(archive.GetEntry(prefix+"byteorder") is { } byteorder)
            {using var r=new StreamReader(byteorder.Open());if(r.ReadToEnd().Trim()!="little")throw new NotSupportedException("Only little-endian checkpoints are supported.");}
            using var source=entry.Open();using var memory=new MemoryStream();source.CopyTo(memory);
            var root=new DataReader(memory.ToArray()).Read() as Dictionary<string,object?> ?? throw new InvalidDataException("Checkpoint root must be a dictionary.");
            var state=root.TryGetValue("state_dict",out var value)?value as Dictionary<string,object?>:root;
            if(state is null)throw new InvalidDataException("Checkpoint state_dict must be a dictionary.");
            foreach(var pair in state)
            {
                if(pair.Value is not TensorRef t)continue;
                var info=new TensorInfo(pair.Key,t.Storage.Key,t.Storage.Dtype,t.Storage.Count,t.Offset,t.Shape,t.Stride);
                Validate(info);tensors.Add(pair.Key,info);
            }
            if(tensors.Count==0)throw new InvalidDataException("No tensor weights in checkpoint.");
        }
        catch{archive.Dispose();throw;}
    }
    static long Count(TensorInfo tensor)
    {long count=1;foreach(var d in tensor.Shape){if(d<0)throw new InvalidDataException("Negative tensor dimension.");count=checked(count*d);if(count>MaximumTensorElements)throw new InvalidDataException("Tensor exceeds element limit.");}return count;}
    void Validate(TensorInfo tensor)
    {
        if(tensor.Shape.Length>8||tensor.Shape.Length!=tensor.Stride.Length||tensor.Offset<0||tensor.StorageLength<0||tensor.StorageLength>MaximumTensorElements)
            throw new InvalidDataException("Invalid tensor dimensions/storage.");
        if(tensor.Storage.Length==0||tensor.Storage.Any(c=>!char.IsAsciiDigit(c)))throw new InvalidDataException("Unexpected tensor storage key.");
        var count=Count(tensor);long last=tensor.Offset;
        for(var i=0;i<tensor.Shape.Length;i++)
        {if(tensor.Stride[i]<0)throw new InvalidDataException("Negative tensor stride.");last=checked(last+Math.Max(0,tensor.Shape[i]-1)*tensor.Stride[i]);}
        if(count>0&&last>=tensor.StorageLength)throw new InvalidDataException("Tensor view exceeds its storage.");
        var storage=archive.GetEntry(prefix+"data/"+tensor.Storage)??throw new InvalidDataException("Missing tensor storage.");
        if(storage.Length!=checked(tensor.StorageLength*ElementSize(tensor.Dtype)))throw new InvalidDataException("Tensor storage byte length mismatch.");
    }
    static int ElementSize(string dtype)=>dtype switch
    {"FloatStorage" or "IntStorage"=>4,"DoubleStorage" or "LongStorage"=>8,"HalfStorage" or "BFloat16Storage"=>2,"ByteStorage" or "BoolStorage"=>1,_=>throw new NotSupportedException("Unsupported checkpoint dtype: "+dtype)};

    /// <summary>Materializes one tensor, respecting noncontiguous views. Never loads the whole
    /// checkpoint into memory. Callers choose which model's tensors to retain.</summary>
    public float[] ReadFloat(string name,CancellationToken cancellation=default)
    {
        var tensor=tensors.TryGetValue(name,out var found)?found:throw new KeyNotFoundException(name);
        var count=checked((int)Count(tensor));var bytes=checked((int)(tensor.StorageLength*ElementSize(tensor.Dtype)));
        cancellation.ThrowIfCancellationRequested();var storage=new byte[bytes];
        using(var input=archive.GetEntry(prefix+"data/"+tensor.Storage)!.Open())
        {
            var offset=0;while(offset<storage.Length){cancellation.ThrowIfCancellationRequested();var n=input.Read(storage,offset,Math.Min(65536,storage.Length-offset));if(n==0)throw new EndOfStreamException();offset+=n;}
        }
        var output=new float[count];var elementSize=ElementSize(tensor.Dtype);
        long contiguousStride=1;var contiguous=true;
        for(var dim=tensor.Shape.Length-1;dim>=0;dim--)
        {if(tensor.Shape[dim]>1&&tensor.Stride[dim]!=contiguousStride)contiguous=false;contiguousStride*=tensor.Shape[dim];}
        if(contiguous&&tensor.Dtype=="FloatStorage"&&BitConverter.IsLittleEndian)
        {
            cancellation.ThrowIfCancellationRequested();
            Buffer.BlockCopy(storage,checked((int)(tensor.Offset*4)),output,0,checked(count*4));
            cancellation.ThrowIfCancellationRequested();return output;
        }
        for(var i=0;i<count;i++)
        {
            if((i&16383)==0)cancellation.ThrowIfCancellationRequested();
            long index=tensor.Offset;var remainder=i;
            for(var dim=tensor.Shape.Length-1;dim>=0;dim--){index+=(remainder%tensor.Shape[dim])*tensor.Stride[dim];remainder/=tensor.Shape[dim];}
            var at=checked((int)(index*elementSize));
            output[i]=tensor.Dtype switch
            {
                "FloatStorage"=>BitConverter.ToSingle(storage,at),"DoubleStorage"=>(float)BitConverter.ToDouble(storage,at),
                "HalfStorage"=>(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(storage,at)),
                "BFloat16Storage"=>BitConverter.Int32BitsToSingle(BitConverter.ToUInt16(storage,at)<<16),
                "IntStorage"=>BitConverter.ToInt32(storage,at),"LongStorage"=>BitConverter.ToInt64(storage,at),
                "ByteStorage" or "BoolStorage"=>storage[at],_=>throw new NotSupportedException(tensor.Dtype)
            };
        }
        return output;
    }
    public void Dispose()=>archive.Dispose();

    sealed class DataReader
    {
        readonly BinaryReader reader;
        readonly List<object?> stack=new();
        readonly Dictionary<int,object?> memo=new();
        static readonly object Mark=new();
        int operations;
        public DataReader(byte[] bytes){reader=new BinaryReader(new MemoryStream(bytes),Encoding.UTF8);}
        object? Pop(){if(stack.Count==0)throw new InvalidDataException("Empty pickle stack.");var value=stack[^1];stack.RemoveAt(stack.Count-1);return value;}
        object? Peek()=>stack.Count==0?throw new InvalidDataException("Empty pickle stack."):stack[^1];
        object?[] MarkItems()
        {
            var index=stack.LastIndexOf(Mark);if(index<0)throw new InvalidDataException("Missing pickle mark.");
            var values=stack.Skip(index+1).ToArray();stack.RemoveRange(index,stack.Count-index);return values;
        }
        string Line()
        {var bytes=new List<byte>();byte b;while((b=reader.ReadByte())!=10){if(bytes.Count>=4096)throw new InvalidDataException("Pickle identifier too long.");bytes.Add(b);}return Encoding.UTF8.GetString(bytes.ToArray());}
        string Text(int length)
        {if(length<0||length>MaximumMetadataBytes||length>reader.BaseStream.Length-reader.BaseStream.Position)throw new InvalidDataException("Invalid pickle string length.");return Encoding.UTF8.GetString(reader.ReadBytes(length));}
        static long Integer(object? value)=>value switch{int i=>i,long l=>l,_=>throw new InvalidDataException("Expected integer.")};
        static object?[] Tuple(object? value)=>value as object?[]??throw new InvalidDataException("Expected tuple.");
        static Dictionary<string,object?> Dict(object? value)=>value as Dictionary<string,object?>??throw new InvalidDataException("Expected dictionary.");
        static void Set(Dictionary<string,object?> dictionary,object? key,object? value)
        {if(key is not string s)throw new InvalidDataException("Only string dictionary keys are supported.");dictionary[s]=value;}
        public object? Read()
        {
            while(reader.BaseStream.Position<reader.BaseStream.Length)
            {
                if(++operations>2_000_000||stack.Count>100_000||memo.Count>100_000)throw new InvalidDataException("Checkpoint metadata complexity limit.");
                var op=reader.ReadByte();
                switch(op)
                {
                    case 0x80:var protocol=reader.ReadByte();if(protocol>5)throw new NotSupportedException("Pickle protocol "+protocol);break;
                    case (byte)'.':if(stack.Count!=1||reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("Invalid pickle termination.");return Pop();
                    case (byte)'(':stack.Add(Mark);break;
                    case (byte)')':stack.Add(System.Array.Empty<object?>());break;
                    case (byte)'}':stack.Add(new Dictionary<string,object?>(StringComparer.Ordinal));break;
                    case (byte)']':stack.Add(new List<object?>());break;
                    case (byte)'N':stack.Add(null);break;
                    case 0x88:stack.Add(true);break;
                    case 0x89:stack.Add(false);break;
                    case (byte)'K':stack.Add((int)reader.ReadByte());break;
                    case (byte)'M':stack.Add((int)reader.ReadUInt16());break;
                    case (byte)'J':stack.Add(reader.ReadInt32());break;
                    case (byte)'G':var f=reader.ReadBytes(8);if(f.Length!=8)throw new EndOfStreamException();System.Array.Reverse(f);stack.Add(BitConverter.ToDouble(f));break;
                    case (byte)'X':stack.Add(Text(reader.ReadInt32()));break;
                    case 0x8c:stack.Add(Text(reader.ReadByte()));break;
                    case 0x95:var frame=reader.ReadUInt64();if(frame>(ulong)(reader.BaseStream.Length-reader.BaseStream.Position))throw new InvalidDataException("Truncated pickle frame.");break;
                    case (byte)'q':memo[reader.ReadByte()]=Peek();break;
                    case (byte)'r':memo[reader.ReadInt32()]=Peek();break;
                    case 0x94:memo[memo.Count]=Peek();break;
                    case (byte)'h':stack.Add(memo[reader.ReadByte()]);break;
                    case (byte)'j':stack.Add(memo[reader.ReadInt32()]);break;
                    case (byte)'t':stack.Add(MarkItems());break;
                    case 0x85:stack.Add(new[]{Pop()});break;
                    case 0x86:var second=Pop();stack.Add(new[]{Pop(),second});break;
                    case 0x87:var third=Pop();second=Pop();stack.Add(new[]{Pop(),second,third});break;
                    case (byte)'s':var value=Pop();var key=Pop();Set(Dict(Peek()),key,value);break;
                    case (byte)'u':var pairs=MarkItems();if(pairs.Length%2!=0)throw new InvalidDataException("Odd dictionary entries.");var d=Dict(Peek());for(var i=0;i<pairs.Length;i+=2)Set(d,pairs[i],pairs[i+1]);break;
                    case (byte)'a':value=Pop();((List<object?>)Peek()!).Add(value);break;
                    case (byte)'e':var items=MarkItems();((List<object?>)Peek()!).AddRange(items);break;
                    case (byte)'c':stack.Add(AllowedSymbol(Line(),Line()));break;
                    case 0x93:var name=Pop() as string??throw new InvalidDataException();var module=Pop() as string??throw new InvalidDataException();stack.Add(AllowedSymbol(module,name));break;
                    case (byte)'Q':
                        var storage=Tuple(Pop());
                        if(storage.Length<5||storage[0] as string!="storage"||storage[1] is not Symbol type||type.Module!="torch"||storage[2] is not string storageKey)
                            throw new InvalidDataException("Unsupported persistent pickle reference.");
                        stack.Add(new StorageRef(storageKey,type.Name,Integer(storage[4])));break;
                    case (byte)'R':
                        var arguments=Tuple(Pop());var symbol=Pop() as Symbol??throw new InvalidDataException("Unsupported pickle callable.");
                        if(symbol==new Symbol("collections","OrderedDict")&&arguments.Length==0)stack.Add(new Dictionary<string,object?>(StringComparer.Ordinal));
                        else if(symbol.Module=="torch._utils"&&(symbol.Name=="_rebuild_tensor_v2"||symbol.Name=="_rebuild_tensor")&&arguments.Length>=4&&arguments[0] is StorageRef sr)
                            stack.Add(new TensorRef(sr,Integer(arguments[1]),Tuple(arguments[2]).Select(x=>checked((int)Integer(x))).ToArray(),Tuple(arguments[3]).Select(Integer).ToArray()));
                        else throw new InvalidDataException("Unsupported checkpoint construction: "+symbol);
                        break;
                    case (byte)'b':
                        var metadata=Dict(Pop());_ = Dict(Peek());
                        if(metadata.Keys.Any(k=>k!="_metadata"))throw new InvalidDataException("Unsupported checkpoint object state.");
                        break;
                    default:throw new NotSupportedException($"Checkpoint pickle opcode 0x{op:X2} at {reader.BaseStream.Position-1} is unsupported.");
                }
            }
            throw new EndOfStreamException("Missing pickle STOP.");
        }
        static Symbol AllowedSymbol(string module,string name)
        {
            if(module=="collections"&&name=="OrderedDict"||module=="torch._utils"&&(name=="_rebuild_tensor_v2"||name=="_rebuild_tensor"))return new(module,name);
            if(module=="torch"){_ = ElementSize(name);return new(module,name);}
            throw new InvalidDataException("Executable/unsupported checkpoint symbol rejected: "+module+"."+name);
        }
    }
}
