using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Text;

namespace HumanoidMocap.Editor;
/// <summary>Bounded ISO BMFF metadata inspection. Does not pretend to decode video.</summary>
internal sealed record Mp4Metadata(double Duration,int Width,int Height,double FrameRate)
{
    public double[] Times { get; init; } = Array.Empty<double>();
    public int RotationDegrees { get; init; }
    record Box(string Type,long Start,long End);
    static uint U32(BinaryReader r)=>BinaryPrimitives.ReverseEndianness(r.ReadUInt32());
    static ulong U64(BinaryReader r)=>BinaryPrimitives.ReverseEndianness(r.ReadUInt64());
    static List<Box> Boxes(BinaryReader r,long start,long end)
    {
        var boxes=new List<Box>();r.BaseStream.Position=start;
        while(r.BaseStream.Position+8<=end)
        {
            var offset=r.BaseStream.Position;ulong size=U32(r);var type=Encoding.ASCII.GetString(r.ReadBytes(4));
            var header=8;if(size==1){size=U64(r);header=16;}if(size==0)size=(ulong)(end-offset);
            if(size<(ulong)header || size>(ulong)(end-offset))throw new FormatException("Invalid MP4 box.");
            boxes.Add(new(type,offset+header,offset+(long)size));r.BaseStream.Position=offset+(long)size;
            if(boxes.Count>100000)throw new FormatException("Too many MP4 boxes.");
        }
        return boxes;
    }
    public static Mp4Metadata Read(string path)
    {
        using var stream=File.OpenRead(path);using var r=new BinaryReader(stream);
        var top=Boxes(r,0,stream.Length);
        if(!top.Any(b=>b.Type=="ftyp")||!top.Any(b=>b.Type=="mdat"))throw new FormatException("Not a media MP4 (HTML/LFS pointers are rejected).");
        var moov=top.Single(b=>b.Type=="moov");
        foreach(var track in Boxes(r,moov.Start,moov.End).Where(b=>b.Type=="trak"))
        {
            var children=Boxes(r,track.Start,track.End);var mdia=children.Single(b=>b.Type=="mdia");var media=Boxes(r,mdia.Start,mdia.End);
            var handler=media.Single(b=>b.Type=="hdlr");stream.Position=handler.Start+8;
            if(Encoding.ASCII.GetString(r.ReadBytes(4))!="vide")continue;
            var header=children.Single(b=>b.Type=="tkhd");stream.Position=header.End-8;var width=(int)(U32(r)>>16);var height=(int)(U32(r)>>16);
            // tkhd dimensions precede the display transform. Media Foundation applies
            // this orientation in WindowsVideoDecoder; crops and camera intrinsics must agree.
            if(header.End-header.Start<84)throw new FormatException("Truncated video track header.");
            stream.Position=header.End-44;var matrix=new int[9];for(var i=0;i<9;i++)matrix[i]=unchecked((int)U32(r));
            var rotation=(matrix[0],matrix[1],matrix[3],matrix[4]) switch
            {
                (65536,0,0,65536)=>0,(0,65536,-65536,0)=>90,
                (-65536,0,0,-65536)=>180,(0,-65536,65536,0)=>270,
                _=>throw new FormatException("Unsupported video display transform. Export the video with a standard 0, 90, 180 or 270 degree orientation.")
            };
            if(matrix[2]!=0||matrix[5]!=0||matrix[8]!=1<<30)throw new FormatException("Unsupported perspective video display transform.");
            if(rotation is 90 or 270)(width,height)=(height,width);
            var mdhd=media.Single(b=>b.Type=="mdhd");stream.Position=mdhd.Start;var version=r.ReadByte();stream.Position=mdhd.Start+(version==1?20:12);
            var scale=U32(r);var duration=version==1?U64(r):U32(r);
            var minf=media.Single(b=>b.Type=="minf");var stbl=Boxes(r,minf.Start,minf.End).Single(b=>b.Type=="stbl");
            var tables=Boxes(r,stbl.Start,stbl.End);var stsz=tables.Single(b=>b.Type=="stsz");stream.Position=stsz.Start+8;var count=U32(r);
            if(width<=0||height<=0||scale==0||duration==0||count==0)throw new FormatException("Empty video track.");
            var times=new double[count];var stts=tables.Single(b=>b.Type=="stts");stream.Position=stts.Start+4;
            var entries=U32(r);long ticks=0;int frame=0;
            for(var i=0;i<entries;i++)
            {
                var repeats=U32(r);var delta=U32(r);
                if(frame+repeats>count)throw new FormatException("Invalid timestamp table.");
                for(var j=0;j<repeats;j++){times[frame++]=(double)ticks/scale;ticks+=delta;}
            }
            if(frame!=count)throw new FormatException("Missing timestamps.");
            var ctts=tables.FirstOrDefault(b=>b.Type=="ctts");
            if(ctts is not null)
            {
                stream.Position=ctts.Start;var cv=r.ReadByte();stream.Position=ctts.Start+4;entries=U32(r);frame=0;
                for(var i=0;i<entries;i++)
                {
                    var repeats=U32(r);var rawOffset=U32(r);long offset=cv==1?unchecked((int)rawOffset):rawOffset;
                    if(frame+repeats>count)throw new FormatException("Invalid composition times.");
                    for(var j=0;j<repeats;j++)times[frame++]+=(double)offset/scale;
                }
            }
            Array.Sort(times);var first=times[0];for(var i=0;i<times.Length;i++)times[i]-=first;
            var seconds=(double)duration/scale;return new(seconds,width,height,count/seconds){Times=times,RotationDegrees=rotation};
        }
        throw new FormatException("No video track.");
    }
}
