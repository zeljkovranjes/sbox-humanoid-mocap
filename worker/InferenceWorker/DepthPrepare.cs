using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Core.Inference;
using HumanoidMocap.EditorTools.Inference;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>Turns a recording with depth into what capture reads: an ordinary picture-only video and a
/// <see cref="DepthTrack"/> beside it. Record3D's RGBD video is split down the middle (depth left as hue, picture right);
/// its .r3d export is a zip of JPEG pictures and LZFSE-compressed float depth with the phone's poses; a Cinematic mode
/// .mov keeps its picture and gives up its depth track, decoded after relabelling it as the file's only video track.
/// Anything else is left as it is and gets no depth.</summary>
public static class DepthPrepare
{
    /// <param name="HorizontalFov">The recording's lens (degrees across the picture), when the file states it.</param>
    public sealed record Result(DepthKind Kind,string Format,string Label,string Video,string? Depth,string Note,float? HorizontalFov=null);
    /// <summary>Longest side of the stored depth images, pixels: enough to sample joints, small enough to keep a minute in memory.</summary>
    public const int StoredSize=160;
    /// <summary>Record3D's older RGBD videos code 0 to 3 m as hue 0 to 1; newer ones state their range after the video.</summary>
    const float DefaultRange=3;

    public static Result Run(string input,string folder,CancellationToken cancellation,Action<string>? progress)
    {
        var info=DepthDetection.DetectWithPicture(input);Directory.CreateDirectory(folder);
        var depthPath=Path.Combine(folder,DepthTrack.FileName);
        switch(info.Kind,info.Format)
        {
            case (DepthKind.Full,DepthDetection.Record3DVideo):
            {
                var video=Path.Combine(folder,"picture.mp4");
                var (note,fov)=SplitRgbdVideo(input,video,depthPath,info.Detail,cancellation,progress);
                return new(info.Kind,info.Format,info.Label,video,depthPath,note,fov);
            }
            case (DepthKind.Full,DepthDetection.Record3DBundle):
            {
                var video=Path.Combine(folder,"picture.mp4");
                var (note,fov)=UnpackBundle(input,video,depthPath,cancellation,progress);
                return new(info.Kind,info.Format,info.Label,video,depthPath,note,fov);
            }
            case (DepthKind.Partial,DepthDetection.Cinematic):
            {
                var note=ExtractCinematicDepth(input,folder,depthPath,cancellation,progress);
                return new(info.Kind,info.Format,info.Label,input,depthPath,note);
            }
            default:return new(DepthKind.None,info.Format,"",input,null,info.Detail);
        }
    }

    /// <summary>OpenCV opens files by narrow name; a copy in a plain-ASCII folder when the path is not.</summary>
    static string Openable(string path,string folder,List<string> cleanup)
    {
        if(path.All(c=>c<128))return path;
        var scratch=new[]{folder,Path.GetTempPath()}.FirstOrDefault(p=>p.All(c=>c<128))??Path.GetTempPath();
        var copy=Path.Combine(scratch,"hm-depth-"+Guid.NewGuid().ToString("N")+Path.GetExtension(path));File.Copy(path,copy,true);cleanup.Add(copy);return copy;
    }
    static VideoWriter Writer(string path,double fps,Size size)
    {
        var writer=new VideoWriter(path,VideoCaptureAPIs.MSMF,FourCC.H264,fps,size);
        if(!writer.IsOpened())throw new NotSupportedException("Windows' H.264 encoder is unavailable.");
        return writer;
    }
    static (int Width,int Height) StoredDimensions(int width,int height)
    {
        var scale=Math.Min(1f,(float)StoredSize/Math.Max(width,height));
        return (Math.Max(1,(int)MathF.Round(width*scale)),Math.Max(1,(int)MathF.Round(height*scale)));
    }

    /// <summary>Degrees across a picture <paramref name="width"/> pixels wide seen through focal length <paramref name="fx"/>.</summary>
    static float? Fov(float fx,int width)=>fx>1&&width>1?(float)(2*Math.Atan(width/2.0/fx)*180/Math.PI):null;
    static (string Note,float? Fov) SplitRgbdVideo(string input,string video,string depthPath,string detail,CancellationToken cancellation,Action<string>? progress)
    {
        float low=0,high=DefaultRange;float fx=0;
        if(detail.StartsWith("{",StringComparison.Ordinal))
        {
            try
            {
                using var json=JsonDocument.Parse(detail);
                if(json.RootElement.TryGetProperty("rangeOfEncodedDepth",out var range)&&range.GetArrayLength()==2){low=range[0].GetSingle();high=range[1].GetSingle();}
                if(json.RootElement.TryGetProperty("intrinsicMatrix",out var k)&&k.GetArrayLength()==9)fx=k[0].GetSingle();
            }
            catch(JsonException){}
        }
        var cleanup=new List<string>();
        try
        {
            using var capture=new VideoCapture(Openable(input,Path.GetDirectoryName(video)!,cleanup),VideoCaptureAPIs.FFMPEG);
            if(!capture.IsOpened())throw new NotSupportedException("The RGBD video could not be opened.");
            var fps=capture.Fps;if(!(fps is > 1 and < 1000))fps=30;var total=capture.FrameCount;
            using var frame=new Mat();if(!capture.Read(frame)||frame.Empty())throw new NotSupportedException("The RGBD video has no readable frames.");
            var half=frame.Width/2;var picture=new Size(half&~1,frame.Height&~1);var (dw,dh)=StoredDimensions(half,frame.Height);
            var track=new DepthTrack{Kind=DepthKind.Full,Width=dw,Height=dh,PictureWidth=picture.Width,PictureHeight=picture.Height};
            var partial=video+".partial.mp4";
            using(var writer=Writer(partial,fps,picture))
            {
                var index=0;
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    using(var right=new Mat(frame,new Rect(half,0,picture.Width,picture.Height)))writer.Write(right);
                    using(var left=new Mat(frame,new Rect(0,0,half,frame.Height)))using(var small=new Mat())
                    {
                        // Nearest pixels only: blending two hues would invent a depth between them.
                        Cv2.Resize(left,small,new Size(dw,dh),0,0,InterpolationFlags.Nearest);
                        track.Frames.Add(HueDepth(small,low,high));
                    }
                    track.Times.Add(index/fps);index++;
                    if(index%30==0)progress?.Invoke(total>0?$"Reading LiDAR depth · {Math.Min(100,index*100/total)}%":$"Reading LiDAR depth · {index} frames");
                }
                while(capture.Read(frame)&&!frame.Empty());
            }
            File.Move(partial,video,true);track.Save(depthPath);
            return (FormattableString.Invariant($"Record3D RGBD video: {track.Frames.Count} frames of sensor depth, {low:0.##} to {high:0.##} m."),Fov(fx,picture.Width));
        }
        finally{foreach(var f in cleanup)File.Delete(f);}
    }
    /// <summary>Record3D codes depth as hue from <paramref name="low"/> (hue 0) to <paramref name="high"/> (hue 1), fully
    /// saturated; unsaturated, dark or pure red pixels (its "no reading") are left empty.</summary>
    static ushort[] HueDepth(Mat bgr,float low,float high)
    {
        var result=new ushort[bgr.Width*bgr.Height];var indexer=bgr.GetGenericIndexer<Vec3b>();
        for(var y=0;y<bgr.Height;y++)for(var x=0;x<bgr.Width;x++)
        {
            var p=indexer[y,x];float b=p.Item0/255f,g=p.Item1/255f,r=p.Item2/255f;
            float max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),d=max-min;
            if(max<.5f||d/max<.5f)continue;
            float hue=max==r?(g-b)/d:max==g?2+(b-r)/d:4+(r-g)/d;hue/=6;if(hue<0)hue+=1;
            if(hue<.02f||hue>.98f)continue;
            result[y*bgr.Width+x]=(ushort)Math.Clamp(MathF.Round((low+(high-low)*hue)*1000),1,65535);
        }
        return result;
    }

    sealed record R3dMetadata(int W,int H,int Dw,int Dh,float Fps,float[] K,float[][]? Poses);
    static (string Note,float? Fov) UnpackBundle(string input,string video,string depthPath,CancellationToken cancellation,Action<string>? progress)
    {
        using var zip=ZipFile.OpenRead(input);
        var metaEntry=zip.Entries.FirstOrDefault(e=>e.FullName=="metadata"||e.FullName.EndsWith("/metadata",StringComparison.Ordinal))??throw new InvalidDataException("The .r3d has no metadata.");
        R3dMetadata meta;
        using(var reader=new StreamReader(metaEntry.Open()))
        {
            using var json=JsonDocument.Parse(reader.ReadToEnd());var root=json.RootElement;
            float[] Floats(JsonElement e)=>e.EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            var poses=root.TryGetProperty("poses",out var p)&&p.ValueKind==JsonValueKind.Array?p.EnumerateArray().Select(Floats).ToArray():null;
            meta=new(root.GetProperty("w").GetInt32(),root.GetProperty("h").GetInt32(),root.GetProperty("dw").GetInt32(),root.GetProperty("dh").GetInt32(),
                root.TryGetProperty("fps",out var fps)?fps.GetSingle():30,Floats(root.GetProperty("K")),poses);
        }
        // Frames are numbered from 0: rgbd/0.jpg with rgbd/0.depth, and so on.
        int Number(string name)=>int.TryParse(Path.GetFileNameWithoutExtension(name),out var n)?n:-1;
        var pictures=zip.Entries.Where(e=>e.FullName.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)).ToDictionary(e=>Number(e.Name),e=>e);
        var depths=zip.Entries.Where(e=>e.FullName.EndsWith(".depth",StringComparison.OrdinalIgnoreCase)).ToDictionary(e=>Number(e.Name),e=>e);
        var frames=pictures.Keys.Where(k=>k>=0&&depths.ContainsKey(k)).OrderBy(k=>k).ToArray();
        if(frames.Length<2)throw new InvalidDataException("The .r3d holds no recorded frames.");
        byte[] Read(ZipArchiveEntry e){using var s=e.Open();using var m=new MemoryStream();s.CopyTo(m);return m.ToArray();}
        var fpsValue=meta.Fps is > 1 and < 1000?meta.Fps:30;var (dw,dh)=StoredDimensions(meta.Dw,meta.Dh);
        using var first=Cv2.ImDecode(Read(pictures[frames[0]]),ImreadModes.Color);
        var picture=new Size(first.Width&~1,first.Height&~1);
        var track=new DepthTrack{Kind=DepthKind.Full,Width=dw,Height=dh,PictureWidth=picture.Width,PictureHeight=picture.Height};
        if(meta.Poses is {Length:>0} recorded&&recorded.Length>=frames.Length)track.Poses=frames.Select(k=>k<recorded.Length&&recorded[k].Length>=7?recorded[k][..7]:new float[]{0,0,0,1,0,0,0}).ToList();
        var partial=video+".partial.mp4";
        using(var writer=Writer(partial,fpsValue,picture))
        {
            for(var i=0;i<frames.Length;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                using(var image=Cv2.ImDecode(Read(pictures[frames[i]]),ImreadModes.Color))
                {
                    if(image.Width!=first.Width||image.Height!=first.Height){using var sized=new Mat();Cv2.Resize(image,sized,new Size(first.Width,first.Height));using var c=new Mat(sized,new Rect(0,0,picture.Width,picture.Height));writer.Write(c);}
                    else{using var c=new Mat(image,new Rect(0,0,picture.Width,picture.Height));writer.Write(c);}
                }
                var raw=Lzfse.Decode(Read(depths[frames[i]]));
                if(raw.Length<meta.Dw*meta.Dh*4)throw new InvalidDataException("A depth frame in the .r3d is shorter than its stated size.");
                var stored=new ushort[dw*dh];
                for(var y=0;y<dh;y++)for(var x=0;x<dw;x++)
                {
                    var sx=Math.Min(meta.Dw-1,x*meta.Dw/dw);var sy=Math.Min(meta.Dh-1,y*meta.Dh/dh);
                    var metres=BitConverter.ToSingle(raw,(sy*meta.Dw+sx)*4);
                    if(float.IsFinite(metres)&&metres>.05f&&metres<20)stored[y*dw+x]=(ushort)Math.Clamp(MathF.Round(metres*1000),1,65535);
                }
                track.Frames.Add(stored);track.Times.Add(i/(double)fpsValue);
                if(i%30==0)progress?.Invoke($"Reading LiDAR depth · {i*100/frames.Length}%");
            }
        }
        File.Move(partial,video,true);track.Save(depthPath);
        return ($"Record3D .r3d: {frames.Length} frames of LiDAR depth"+(track.Poses is null?".":" and the phone's camera path."),Fov(meta.K.Length==9?meta.K[0]:0,meta.W));
    }

    /// <summary>Copies the .mov with its picture track hidden (handler auxv, sample type unknown) and its depth track shown as
    /// ordinary HEVC video (handler vide, sample type hvc1), then decodes that. Four bytes change in each place.</summary>
    static string ExtractCinematicDepth(string input,string folder,string depthPath,CancellationToken cancellation,Action<string>? progress)
    {
        var copy=Path.Combine(folder,"depth-track.mov");File.Copy(input,copy,true);
        var cleanup=new List<string>{copy};
        try
        {
            using(var stream=new FileStream(copy,FileMode.Open,FileAccess.ReadWrite))
            {
                var patches=CinematicPatches(stream);
                if(patches.Count==0)throw new InvalidDataException("The Cinematic depth track could not be found.");
                foreach(var (offset,value) in patches){stream.Position=offset;stream.Write(Encoding.ASCII.GetBytes(value));}
            }
            int pictureWidth,pictureHeight;
            try{var metadata=HumanoidMocap.EditorTools.Mp4Metadata.Read(input);pictureWidth=metadata.Width;pictureHeight=metadata.Height;}
            catch(FormatException){pictureWidth=1920;pictureHeight=1080;}
            using var capture=new VideoCapture(Openable(copy,folder,cleanup),VideoCaptureAPIs.FFMPEG);
            if(!capture.IsOpened())throw new NotSupportedException("The Cinematic depth track could not be decoded.");
            using var frame=new Mat();if(!capture.Read(frame)||frame.Empty())throw new NotSupportedException("The Cinematic depth track has no readable frames.");
            var (sourceWidth,sourceHeight)=(frame.Width,frame.Height);var (dw,dh)=StoredDimensions(sourceWidth,sourceHeight);
            var track=new DepthTrack{Kind=DepthKind.Partial,Width=dw,Height=dh,PictureWidth=pictureWidth,PictureHeight=pictureHeight};
            var total=capture.FrameCount;var index=0;
            do
            {
                cancellation.ThrowIfCancellationRequested();
                using var gray=new Mat();Cv2.CvtColor(frame,gray,ColorConversionCodes.BGR2GRAY);
                using var small=new Mat();Cv2.Resize(gray,small,new Size(dw,dh),0,0,InterpolationFlags.Area);
                var stored=new ushort[dw*dh];var indexer=small.GetGenericIndexer<byte>();
                for(var y=0;y<dh;y++)for(var x=0;x<dw;x++){var v=indexer[y,x];if(v>0)stored[y*dw+x]=(ushort)(v*257);}
                track.Frames.Add(stored);track.Times.Add(capture.Get(VideoCaptureProperties.PosMsec)/1000);index++;
                if(index%30==0)progress?.Invoke(total>0?$"Reading Cinematic depth · {Math.Min(100,index*100/total)}%":$"Reading Cinematic depth · {index} frames");
            }
            while(capture.Read(frame)&&!frame.Empty());
            // OpenCV reports each frame's position once read; the first reads as its successor's time on some builds.
            if(track.Times.Count>1&&track.Times[0]>=track.Times[1])track.Times[0]=Math.Max(0,track.Times[1]-(track.Times.Count>2?track.Times[2]-track.Times[1]:1/30.0));
            track.Save(depthPath);
            return $"Cinematic mode: {track.Frames.Count} frames of the phone's estimated depth ({sourceWidth}×{sourceHeight}), relative, not in metres.";
        }
        finally{foreach(var f in cleanup)if(File.Exists(f))File.Delete(f);}
    }
    /// <summary>Byte offsets and new four-character codes that swap which track is the video in a Cinematic .mov.</summary>
    static List<(long Offset,string Value)> CinematicPatches(Stream stream)
    {
        var patches=new List<(long,string)>();var header=new byte[16];long position=0,moovStart=-1,moovEnd=-1;
        while(position+8<=stream.Length)
        {
            stream.Position=position;stream.ReadExactly(header,0,8);
            long size=(uint)(header[0]<<24|header[1]<<16|header[2]<<8|header[3]);var type=Encoding.ASCII.GetString(header,4,4);
            if(size==1){stream.ReadExactly(header,8,8);size=0;for(var i=8;i<16;i++)size=size<<8|header[i];}
            else if(size==0)size=stream.Length-position;
            if(size<8)break;
            if(type=="moov"){moovStart=position;moovEnd=position+size;break;}
            position+=size;
        }
        if(moovStart<0)return patches;
        var bytes=new byte[moovEnd-moovStart];stream.Position=moovStart;stream.ReadExactly(bytes);
        IEnumerable<(int Start,int End)> Children(int start,int end,string type)
        {
            var p=start;
            while(p+8<=end)
            {
                var s=(int)((uint)(bytes[p]<<24|bytes[p+1]<<16|bytes[p+2]<<8|bytes[p+3]));if(s<8||p+s>end)yield break;
                if(Encoding.ASCII.GetString(bytes,p+4,4)==type)yield return (p,p+s);
                p+=s;
            }
        }
        foreach(var trak in Children(8,bytes.Length,"trak"))
        foreach(var mdia in Children(trak.Start+8,trak.End,"mdia"))
        {
            var hdlr=Children(mdia.Start+8,mdia.End,"hdlr").FirstOrDefault();if(hdlr.End==0)continue;
            var handler=Encoding.ASCII.GetString(bytes,hdlr.Start+16,4);
            int entry=-1;
            foreach(var minf in Children(mdia.Start+8,mdia.End,"minf"))
            foreach(var stbl in Children(minf.Start+8,minf.End,"stbl"))
            foreach(var stsd in Children(stbl.Start+8,stbl.End,"stsd"))entry=stsd.Start+20;
            if(entry<0)continue;var sample=Encoding.ASCII.GetString(bytes,entry,4);
            if(handler=="vide"){patches.Add((moovStart+hdlr.Start+16,"auxv"));patches.Add((moovStart+entry,"xpic"));}
            else if(handler=="auxv"&&sample=="dish"){patches.Add((moovStart+hdlr.Start+16,"vide"));patches.Add((moovStart+entry,"hvc1"));}
        }
        return patches.Any(p=>p.Item2=="hvc1")?patches:new();
    }
}
