#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace HumanoidMocap.Inference;

public sealed partial class TorchCheckpoint
{
    /// <summary>Repackages a legacy PyTorch tensor dictionary into the ZIP format
    /// using inert metadata and storage bytes only. No pickle callable executes.
    /// The source is preserved and an existing destination is never overwritten.</summary>
    public static void ConvertLegacy(string source,string destination,CancellationToken cancellation=default)
    {
        using var input=File.OpenRead(source);
        var header=new byte[checked((int)Math.Min(input.Length,MaximumMetadataBytes))];input.ReadExactly(header);
        // torch.serialization MAGIC_NUMBER in its protocol-2 LONG1 envelope.
        var magic=Convert.FromHexString("80028A0A6CFC9C46F9206AA850192E");
        if(header.Length<magic.Length||!header.AsSpan(0,magic.Length).SequenceEqual(magic))
            throw new InvalidDataException("Not a supported legacy PyTorch checkpoint.");
        var position=magic.Length;
        object? Read(){var reader=new DataReader(header,position);var value=reader.Read(requireEnd:false);position=reader.Position;return value;}
        if(Read() is not int protocol||protocol!=1001)throw new InvalidDataException("Unsupported legacy checkpoint protocol.");
        if(Read() is not Dictionary<object,object?> system||!system.TryGetValue("little_endian",out var endian)||!Equals(endian,true))
            throw new InvalidDataException("Only little-endian legacy checkpoints are supported.");
        var metadataStart=position;
        if(Read() is not Dictionary<object,object?> state)throw new InvalidDataException("Expected a flat tensor dictionary.");
        var metadataLength=position-metadataStart;
        if(state.Values.Any(v=>v is not TensorRef))throw new InvalidDataException("Legacy import supports tensor dictionaries only.");
        var storages=new Dictionary<string,StorageRef>(StringComparer.Ordinal);
        foreach(var tensor in state.Values.Cast<TensorRef>())
        {
            var storage=tensor.Storage;
            if(storage.Key.Length==0||storage.Key.Any(c=>!char.IsAsciiDigit(c))||storage.Count<0||storage.Count>MaximumTensorElements)
                throw new InvalidDataException("Invalid legacy storage.");
            _=ElementSize(storage.Dtype);
            if(storages.TryGetValue(storage.Key,out var existing)&&existing!=storage)throw new InvalidDataException("Conflicting legacy storage metadata.");
            storages[storage.Key]=storage;
        }
        if(Read() is not List<object?> order||order.Count!=storages.Count||order.Any(k=>k is not string s||!storages.ContainsKey(s))
            ||order.Distinct().Count()!=order.Count)throw new InvalidDataException("Legacy storage index does not match tensor data.");
        input.Position=position;
        var locations=new List<(StorageRef Storage,long Offset,long Bytes)>();
        using var binary=new BinaryReader(input,Encoding.UTF8,leaveOpen:true);
        foreach(var key in order.Cast<string>())
        {
            cancellation.ThrowIfCancellationRequested();var storage=storages[key];
            if(binary.ReadInt64()!=storage.Count)throw new InvalidDataException("Legacy storage length differs from metadata.");
            var bytes=checked(storage.Count*ElementSize(storage.Dtype));
            if(bytes>input.Length-input.Position)throw new EndOfStreamException("Truncated legacy tensor data.");
            locations.Add((storage,input.Position,bytes));input.Position+=bytes;
        }
        if(input.Position!=input.Length)throw new InvalidDataException("Trailing legacy checkpoint data.");
        var temporary=Path.GetFullPath(destination)+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var archive=ZipFile.Open(temporary,ZipArchiveMode.Create))
            {
                using(var entry=archive.CreateEntry("legacy/data.pkl",CompressionLevel.NoCompression).Open())entry.Write(header,metadataStart,metadataLength);
                using(var entry=archive.CreateEntry("legacy/byteorder").Open())entry.Write(Encoding.ASCII.GetBytes("little"));
                var buffer=new byte[65536];
                foreach(var location in locations)
                {
                    input.Position=location.Offset;
                    using var entry=archive.CreateEntry("legacy/data/"+location.Storage.Key,CompressionLevel.NoCompression).Open();
                    for(long remaining=location.Bytes;remaining>0;)
                    {
                        cancellation.ThrowIfCancellationRequested();var count=(int)Math.Min(buffer.Length,remaining);
                        input.ReadExactly(buffer.AsSpan(0,count));entry.Write(buffer,0,count);remaining-=count;
                    }
                }
            }
            using(var validated=new TorchCheckpoint(temporary)){}
            cancellation.ThrowIfCancellationRequested();File.Move(temporary,destination);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
