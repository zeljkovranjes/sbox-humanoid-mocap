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
    /// <summary>Capture keeps every frame up to this rate. Faster footage (60 fps phones, slow motion) is
    /// sampled down to about 30 per second: the body network was trained at 30, game animation needs no
    /// more, and the work and the 1,800-frame limit then cover the same seconds as ordinary footage.</summary>
    public const double MaximumCaptureRate=40;
    /// <summary>Every how many frames capture reads one, from the median frame interval so gaps do not skew it.</summary>
    public int CaptureStride
    {
        get
        {
            if(Times.Length<3)return 1;
            var intervals=new double[Times.Length-1];for(var i=1;i<Times.Length;i++)intervals[i-1]=Times[i]-Times[i-1];
            Array.Sort(intervals);var interval=intervals[intervals.Length/2];if(!(interval>0))return 1;
            var rate=1/interval;return rate>MaximumCaptureRate?Math.Max(1,(int)Math.Round(rate/30)):1;
        }
    }
    /// <summary>Presentation times of the frames capture reads.</summary>
    public double[] CaptureTimes
    {
        get{var stride=CaptureStride;return stride==1?Times:Times.Where((_,i)=>i%stride==0).ToArray();}
    }
    /// <summary>Frames per second of the captured samples.</summary>
    public double CaptureFrameRate=>FrameRate/CaptureStride;
    public const string SamplingPrefix="High frame rate footage";
    /// <summary>Note for the motion's diagnostics, or null when every frame is read.</summary>
    public string SamplingNote=>CaptureStride is var stride&&stride>1
        ?FormattableString.Invariant($"{SamplingPrefix}: recorded at about {1/((Times[^1]-Times[0])/(Times.Length-1)):F0} frames per second; every {Ordinal(stride)} frame was captured.")
        :null;
    static string Ordinal(int n)=>n==2?"second":n==3?"third":n==4?"fourth":n+"th";
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
        const string foreign="This file is not an MP4 or MOV video. Convert it to an H.264 MP4 and upload it again.";
        List<Box> top;
        try{top=Boxes(r,0,stream.Length);}
        catch(Exception error) when(error is FormatException or EndOfStreamException){throw new FormatException(foreign,error);}
        if(!top.Any(b=>b.Type=="ftyp")||!top.Any(b=>b.Type=="mdat"))throw new FormatException(foreign);
        // Fragmented MP4 (some screen recorders and streaming downloads) keeps its frame table in moof boxes this reader does not follow.
        if(top.Any(b=>b.Type=="moof"))throw new FormatException("This MP4 is fragmented (a streaming or screen-recorder format). Re-export it as a regular H.264 MP4 and upload it again.");
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
            // An edit list may open with an empty edit that delays the picture (dropped first frames,
            // audio priming). Media Foundation presents frames that much later; the table must agree.
            double delay=0;
            if(children.FirstOrDefault(c=>c.Type=="edts") is { } edts&&Boxes(r,edts.Start,edts.End).FirstOrDefault(c=>c.Type=="elst") is { } elst
                &&moov.Start<moov.End&&Boxes(r,moov.Start,moov.End).FirstOrDefault(c=>c.Type=="mvhd") is { } mvhd)
            {
                stream.Position=mvhd.Start;var mv=r.ReadByte();stream.Position=mvhd.Start+(mv==1?20:12);var movieScale=U32(r);
                stream.Position=elst.Start;var ev=r.ReadByte();stream.Position=elst.Start+4;var edits=U32(r);
                for(var i=0;i<edits&&movieScale>0;i++)
                {
                    var length=ev==1?U64(r):U32(r);var mediaTime=ev==1?unchecked((long)U64(r)):unchecked((int)U32(r));stream.Position+=4;
                    if(mediaTime!=-1)break;
                    delay+=(double)length/movieScale;
                }
            }
            Array.Sort(times);var first=times[0];for(var i=0;i<times.Length;i++)times[i]+=delay-first;
            var seconds=(double)duration/scale;return new(seconds,width,height,count/seconds){Times=times,RotationDegrees=rotation};
        }
        throw new FormatException("No video track.");
    }
}
