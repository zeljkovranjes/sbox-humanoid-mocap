using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HumanoidMocap.Inference;

/// <summary>How much depth a recording carries beside its picture.</summary>
public enum DepthKind { None, Partial, Full }

/// <summary>What <see cref="DepthDetection"/> found in a file.</summary>
public sealed record DepthSourceInfo(DepthKind Kind,string Format,string Detail)
{
    public static readonly DepthSourceInfo None=new(DepthKind.None,"","");
    /// <summary>The badge shown over the preview.</summary>
    public string Label=>Kind switch{DepthKind.Full=>"Full LiDAR detected",DepthKind.Partial=>"Partial depth detected",_=>""};
}

/// <summary>Recognises recordings that carry depth, from the file alone (no decoding):
///  - Record3D's .r3d export (a zip of per-frame JPEG pictures and LZFSE-compressed LiDAR depth, with a metadata
///    entry holding the lens and the phone's pose per frame): full LiDAR depth;
///  - Record3D's RGBD video export (an MP4 whose left half is depth coded as hue, right half the picture, with the lens
///    appended after the MP4 as <c>{"intrinsicMatrix":...}</c>): full sensor depth;
///  - an iPhone Cinematic mode .mov (a second, auxiliary HEVC track of type <c>dish</c> tagged
///    <c>com.apple.quicktime.cinematic-video-map.depth</c>): partial depth, estimated by the phone mostly from its
///    cameras, low resolution and not in metres.
/// An RGBD video re-saved by an editor loses the appended lens; <see cref="LooksLikeRecord3DFrame"/> recognises its
/// frames from their hue-coded half instead. Everything else is ordinary video.</summary>
public static class DepthDetection
{
    public const string Record3DBundle="record3d-r3d",Record3DVideo="record3d-rgbd-video",Cinematic="cinematic";
    const string CinematicTag="cinematic-video-map.depth";
    /// <summary>Shown for a Cinematic recording that arrived without its depth.</summary>
    public const string StrippedCinematic="Recorded in Cinematic mode, but its depth was removed when it was copied off the iPhone. Copy the original instead: set Settings > Apps > Photos > Transfer to Mac or PC to Keep Originals, or share with Options > All Photos Data.";

    public static DepthSourceInfo Detect(string path)
    {
        try
        {
            using var stream=File.OpenRead(path);
            if(stream.Length<16)return DepthSourceInfo.None;
            var head=new byte[4];stream.ReadExactly(head);
            if(head[0]=='P'&&head[1]=='K'&&head[2]==3&&head[3]==4)return Bundle(stream);
            stream.Position=0;
            if(TrailingLens(stream) is { } lens)return new(DepthKind.Full,Record3DVideo,lens);
            var (present,stripped)=CinematicDepth(stream);
            if(present)return new(DepthKind.Partial,Cinematic,"Cinematic mode depth track");
            if(stripped)return new(DepthKind.None,Cinematic,StrippedCinematic);
        }
        catch(IOException){}
        catch(UnauthorizedAccessException){}
        return DepthSourceInfo.None;
    }

    /// <summary><see cref="Detect"/>, then for video without a file-level sign, the first frame's look: a Record3D RGBD
    /// video re-saved by an editor keeps its hue-coded depth half but loses the lens appended after it.</summary>
    public static DepthSourceInfo DetectWithPicture(string path)
    {
        var info=Detect(path);
        if(info.Kind!=DepthKind.None||info.Format.Length>0)return info;
        try
        {
            using var decoder=new WindowsVideoDecoder(path);
            for(var i=0;i<3;i++)
            {
                var frame=decoder.Read(default);if(frame is null)break;
                if(LooksLikeRecord3DFrame(frame.Rgba,frame.Width,frame.Height))return new(DepthKind.Full,Record3DVideo,"Record3D RGBD video without its lens metadata");
            }
        }
        catch(Exception error) when(error is not OperationCanceledException){}
        return info;
    }
    /// <summary>A Record3D .r3d: a zip whose entries include the metadata and per-frame depth.</summary>
    static DepthSourceInfo Bundle(Stream stream)
    {
        var names=ZipEntryNames(stream,4000);
        var metadata=names.Any(n=>n=="metadata"||n.EndsWith("/metadata",StringComparison.Ordinal));
        var depth=names.Count(n=>n.EndsWith(".depth",StringComparison.OrdinalIgnoreCase));
        var pictures=names.Count(n=>n.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase));
        if(!metadata)return DepthSourceInfo.None;
        return depth>0&&pictures>0?new(DepthKind.Full,Record3DBundle,$"{Math.Min(depth,pictures)} LiDAR frames")
            :new(DepthKind.None,Record3DBundle,"Record3D metadata without frames");
    }
    /// <summary>Entry names from the zip's local headers, reading no entry data.</summary>
    static List<string> ZipEntryNames(Stream stream,int limit)
    {
        var names=new List<string>();var header=new byte[30];stream.Position=0;
        while(names.Count<limit&&stream.Position+30<=stream.Length)
        {
            stream.ReadExactly(header);
            if(!(header[0]=='P'&&header[1]=='K'&&header[2]==3&&header[3]==4))break;
            var flags=BitConverter.ToUInt16(header,6);var compressed=BitConverter.ToUInt32(header,18);
            var nameLength=BitConverter.ToUInt16(header,26);var extraLength=BitConverter.ToUInt16(header,28);
            var name=new byte[nameLength];stream.ReadExactly(name);names.Add(Encoding.UTF8.GetString(name));
            stream.Position+=extraLength;
            // A streamed entry (sizes after the data) cannot be skipped from its header; the names seen so far suffice.
            if((flags&8)!=0&&compressed==0)break;
            stream.Position+=compressed;
        }
        return names;
    }
    /// <summary>The lens Record3D appends after an RGBD video, or null.</summary>
    static string? TrailingLens(Stream stream)
    {
        var length=(int)Math.Min(stream.Length,8192);var tail=new byte[length];
        stream.Position=stream.Length-length;stream.ReadExactly(tail);
        var text=Encoding.ASCII.GetString(tail);var start=text.LastIndexOf("{\"intrinsicMatrix\"",StringComparison.Ordinal);
        if(start<0)return null;
        var end=text.IndexOf('}',start);return end>start?text.Substring(start,end-start+1):null;
    }
    /// <summary>Whether a QuickTime/MP4 file has Apple's Cinematic depth track (Present), is a Cinematic recording whose
    /// depth track is missing (copied as a converted file: transfers set to "Automatic" re-encode to H.264 and drop it),
    /// or neither.</summary>
    static (bool Present,bool Stripped) CinematicDepth(Stream stream)
    {
        var moov=FindTopLevel(stream,"moov");if(moov is not { } box||box.Size>64L*1024*1024)return (false,false);
        var bytes=new byte[box.Size];stream.Position=box.Offset;stream.ReadExactly(bytes);
        foreach(var trak in Children(bytes,8,bytes.Length,"trak"))
        {
            string handler="",sampleType="";
            foreach(var mdia in Children(bytes,trak.Start+8,trak.End,"mdia"))
            {
                // hdlr: header, version and flags, pre-defined, then the handler type.
                foreach(var hdlr in Children(bytes,mdia.Start+8,mdia.End,"hdlr"))if(hdlr.End-hdlr.Start>=20)handler=Encoding.ASCII.GetString(bytes,hdlr.Start+16,4);
                foreach(var minf in Children(bytes,mdia.Start+8,mdia.End,"minf"))
                foreach(var stbl in Children(bytes,minf.Start+8,minf.End,"stbl"))
                foreach(var stsd in Children(bytes,stbl.Start+8,stbl.End,"stsd"))
                    // stsd: header, version and flags, entry count, then the first entry's size and type.
                    if(stsd.End-stsd.Start>=24)sampleType=Encoding.ASCII.GetString(bytes,stsd.Start+20,4);
            }
            var tagged=Encoding.ASCII.GetString(bytes,trak.Start,trak.End-trak.Start).Contains(CinematicTag,StringComparison.Ordinal);
            if(handler=="auxv"&&(sampleType=="dish"||tagged))return (true,false);
        }
        return (false,Encoding.ASCII.GetString(bytes).Contains(CinematicFlag,StringComparison.Ordinal));
    }
    const string CinematicFlag="com.apple.quicktime.cinematic-video";
    readonly record struct Box(long Offset,long Size);
    static Box? FindTopLevel(Stream stream,string type)
    {
        var header=new byte[16];long position=0;
        while(position+8<=stream.Length)
        {
            stream.Position=position;stream.ReadExactly(header,0,8);
            long size=(uint)(header[0]<<24|header[1]<<16|header[2]<<8|header[3]);var name=Encoding.ASCII.GetString(header,4,4);
            if(size==1){stream.ReadExactly(header,8,8);size=0;for(var i=8;i<16;i++)size=size<<8|header[i];}
            else if(size==0)size=stream.Length-position;
            if(size<8)return null;
            if(name==type)return new(position,size);
            position+=size;
        }
        return null;
    }
    readonly record struct Span(int Start,int End);
    /// <summary>Boxes of the given type laid end to end from <paramref name="start"/> (a container's first child) to end.</summary>
    static IEnumerable<Span> Children(byte[] bytes,int start,int end,string type)
    {
        var position=start;
        while(position+8<=end)
        {
            var size=(int)((uint)(bytes[position]<<24|bytes[position+1]<<16|bytes[position+2]<<8|bytes[position+3]));
            if(size<8||position+size>end)yield break;
            if(Encoding.ASCII.GetString(bytes,position+4,4)==type)yield return new(position,position+size);
            position+=size;
        }
    }

    /// <summary>Whether a decoded frame (RGBA) is a Record3D RGBD frame: its left half depth coded as fully saturated
    /// hue (with pure red for no reading), its right half an ordinary picture.</summary>
    public static bool LooksLikeRecord3DFrame(byte[] rgba,int width,int height)
    {
        if(width<16||height<16||rgba.Length<width*height*4)return false;
        int Saturated(int x0,int x1)
        {
            int vivid=0,total=0;
            for(var y=height/8;y<height*7/8;y+=Math.Max(1,height/48))
            for(var x=x0+(x1-x0)/8;x<x1-(x1-x0)/8;x+=Math.Max(1,(x1-x0)/32))
            {
                var i=(y*width+x)*4;int r=rgba[i],g=rgba[i+1],b=rgba[i+2];int max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b));
                if(max>200&&min<60)vivid++;total++;
            }
            return total==0?0:vivid*100/total;
        }
        return Saturated(0,width/2)>=85&&Saturated(width/2,width)<50;
    }
}
