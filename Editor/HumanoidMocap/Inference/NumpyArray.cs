#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HumanoidMocap.Inference;

/// <summary>Numeric NPY/NPZ input only. Object/pickle arrays are never evaluated.
/// Materializes one bounded array in row-major order, including Fortran-ordered input.</summary>
public sealed record NumpyArray(int[] Shape,float[] Values)
{
    public static NumpyArray Read(ZipArchive archive,string name,CancellationToken cancellation=default)
    {
        var entry=archive.GetEntry(name)??throw new InvalidDataException("Missing model array: "+name);
        if(entry.Length<10||entry.Length>128*1024*1024)throw new InvalidDataException("Model array exceeds the 128 MiB limit.");
        using var stream=entry.Open();using var reader=new BinaryReader(stream,Encoding.ASCII);
        if(!reader.ReadBytes(6).SequenceEqual(new byte[]{0x93,78,85,77,80,89}))throw new InvalidDataException("Invalid NPY signature.");
        var major=reader.ReadByte();var minor=reader.ReadByte();
        if(major is not (1 or 2 or 3)||minor!=0)throw new NotSupportedException("Unsupported NPY version.");
        var headerBytes=major==1?reader.ReadUInt16():reader.ReadUInt32();
        if(headerBytes>65536)throw new InvalidDataException("NPY header exceeds limit.");
        var header=Encoding.UTF8.GetString(reader.ReadBytes((int)headerBytes));
        var dtype=Regex.Match(header,"['\"]descr['\"]\\s*:\\s*['\"]([^'\"]+)['\"]").Groups[1].Value;
        var order=Regex.Match(header,"['\"]fortran_order['\"]\\s*:\\s*(True|False)").Groups[1].Value;
        var dimensions=Regex.Match(header,"['\"]shape['\"]\\s*:\\s*\\(([^)]*)\\)");
        if(!dimensions.Success||order.Length==0)throw new InvalidDataException("Invalid NPY header.");
        var shape=dimensions.Groups[1].Value.Split(',').Where(v=>!string.IsNullOrWhiteSpace(v)).Select(v=>int.Parse(v.Trim(),CultureInfo.InvariantCulture)).ToArray();
        if(shape.Length>8||shape.Any(v=>v<0))throw new InvalidDataException("Invalid NPY dimensions.");
        long count=1;foreach(var d in shape){count=checked(count*d);if(count>32*1024*1024)throw new InvalidDataException("NPY element limit exceeded.");}
        var size=dtype switch{"<f4" or "<i4" or "<u4"=>4,"<f8" or "<i8"=>8,"|u1" or "|b1"=>1,_=>throw new NotSupportedException("Non-numeric or unsupported NPY dtype: "+dtype)};
        if(entry.Length!=6+2+(major==1?2:4)+headerBytes+count*size)throw new InvalidDataException("NPY storage length mismatch.");
        var bytes=new byte[checked((int)(count*size))];
        for(var offset=0;offset<bytes.Length;)
        {cancellation.ThrowIfCancellationRequested();var n=stream.Read(bytes,offset,Math.Min(65536,bytes.Length-offset));if(n==0)throw new EndOfStreamException();offset+=n;}
        var result=new float[checked((int)count)];var fortranStride=new int[shape.Length];var stride=1;
        for(var d=0;d<shape.Length;d++){fortranStride[d]=stride;stride=checked(stride*shape[d]);}
        for(var i=0;i<result.Length;i++)
        {
            if((i&16383)==0)cancellation.ThrowIfCancellationRequested();
            var source=i;
            if(order=="True")
            {source=0;var remainder=i;for(var d=shape.Length-1;d>=0;d--){source+=remainder%shape[d]*fortranStride[d];remainder/=shape[d];}}
            var at=source*size;
            result[i]=dtype switch{"<f4"=>BitConverter.ToSingle(bytes,at),"<f8"=>(float)BitConverter.ToDouble(bytes,at),
                "<i4"=>BitConverter.ToInt32(bytes,at),"<u4"=>BitConverter.ToUInt32(bytes,at),"<i8"=>BitConverter.ToInt64(bytes,at),_=>bytes[at]};
            if(!float.IsFinite(result[i]))throw new InvalidDataException("Non-finite model array: "+name);
        }
        return new(shape,result);
    }
}
