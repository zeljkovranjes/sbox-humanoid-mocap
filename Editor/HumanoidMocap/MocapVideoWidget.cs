using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Editor;

/// <summary>Timestamped C# video preview. One decoder and a bounded frame handoff;
/// decoding never runs on the editor thread.</summary>
sealed class MocapVideoWidget : Widget
{
    public Action TogglePlayback { get; set; }
    public double Duration { get; private set; }
    public double? FrameTime { get; private set; }
    public string Error { get; private set; }
    readonly CancellationTokenSource stop=new();
    readonly object sync=new();
    double requested,decodedRequest=double.NaN;
    DecodedVideoFrame pending;
    string failure;
    double duration;
    Pixmap frame;

    /// <summary>Depth found in the recording: the preview gets a border that cycles through shades of one colour (green for
    /// full LiDAR depth, amber for partial depth) and a badge naming it.</summary>
    public DepthSourceInfo DepthSource{get=>depthSource;set{depthSource=value??DepthSourceInfo.None;Update();}}
    DepthSourceInfo depthSource=DepthSourceInfo.None;
    DepthTrack depthTrack;Pixmap depthImage;int depthImageFrame=-1;bool showDepth;
    public bool HasDepthTrack=>depthTrack is not null;
    /// <summary>Shows the recording's depth (colour-coded, near warm and far cool) in place of the picture, in time with it.</summary>
    public bool ShowDepth{get=>showDepth;set{showDepth=value&&depthTrack is not null;depthImageFrame=-1;Update();}}
    /// <summary>Loads a depth track for the LiDAR view off the editor thread; <paramref name="ready"/> runs once it is loaded.</summary>
    public void SetDepthTrack(string path,Action ready)
    {
        _=Task.Run(async()=>
        {
            DepthTrack loaded;
            try{loaded=DepthTrack.Load(path);}catch(Exception){return;}
            await EditorPipeline.SwitchToMainThread();
            if(!this.IsValid())return;
            depthTrack=loaded;ready?.Invoke();
        });
    }

    public MocapVideoWidget(Widget parent,string path):base(parent)
    {
        MinimumSize=50;
        // A Record3D .r3d is a bundle of pictures, not a video; its picture-only copy replaces this preview once read.
        if(System.IO.Path.GetExtension(path).Equals(".r3d",StringComparison.OrdinalIgnoreCase)){failure="Reading the LiDAR recording…";return;}
        _=Task.Run(()=>Decode(path));
    }
    public void Request(double seconds)
    {lock(sync)requested=Math.Max(0,seconds);}

    void Decode(string path)
    {
        try
        {
            var metadata=Mp4Metadata.Read(path);
            lock(sync)duration=metadata.Duration;
            using var decoder=new WindowsVideoDecoder(path);
            var cache=new Dictionary<long,DecodedVideoFrame>();var order=new Queue<long>();
            static long Key(double time)=>(long)Math.Round(time*10000);
            DecodedVideoFrame Read()
            {
                var decoded=decoder.Read(stop.Token);if(decoded is null)return null;
                var visible=decoded.Height;
                var scale=Math.Min(1,Math.Min(960d/decoded.Width,540d/visible));
                var width=Math.Max(1,(int)(decoded.Width*scale));var height=Math.Max(1,(int)(visible*scale));
                var rgba=new byte[checked(width*height*4)];
                for(var y=0;y<height;y++)
                {
                    stop.Token.ThrowIfCancellationRequested();var sourceY=y*visible/height;
                    for(var x=0;x<width;x++)
                    {
                        var source=(sourceY*decoded.Width+x*decoded.Width/width)*4;var destination=(y*width+x)*4;
                        rgba[destination]=decoded.Rgba[source];rgba[destination+1]=decoded.Rgba[source+1];
                        rgba[destination+2]=decoded.Rgba[source+2];rgba[destination+3]=255;
                    }
                }
                var preview=new DecodedVideoFrame(rgba,width,height,decoded.Time);var key=Key(preview.Time);
                if(!cache.ContainsKey(key))
                {
                    // At most 32 preview frames and 64 MiB; raw inference frames are never changed.
                    var limit=Math.Max(1,Math.Min(32,64*1024*1024/rgba.Length));
                    while(cache.Count>=limit)cache.Remove(order.Dequeue());
                    order.Enqueue(key);cache[key]=preview;
                }
                return preview;
            }
            DecodedVideoFrame current=null,next=null;
            while(!stop.IsCancellationRequested)
            {
                double target;lock(sync)target=requested;
                if(target==decodedRequest){stop.Token.WaitHandle.WaitOne(8);continue;}
                var index=Array.BinarySearch(metadata.Times,target+.00001);
                if(index<0)index=~index-1;index=Math.Clamp(index,0,metadata.Times.Length-1);
                if(cache.TryGetValue(Key(metadata.Times[index]),out var cached))
                {lock(sync){pending=cached;decodedRequest=target;}continue;}
                if(current is not null&&(target<current.Time-.00001||target>current.Time+1))
                {decoder.Seek(target);current=null;next=null;}
                current??=Read();
                if(current is null)throw new InvalidOperationException("The video contains no decodable frames.");
                while(true)
                {
                    next??=Read();
                    if(next is null||next.Time>target+.00001)break;
                    current=next;next=null;
                    // A new scrub request supersedes this one; no queue of seeks builds up.
                    lock(sync)if(Math.Abs(requested-target)>.1)break;
                }
                lock(sync)
                {
                    if(Math.Abs(requested-target)<=.1)pending=current;
                    decodedRequest=target;
                }
            }
        }
        catch(OperationCanceledException) { }
        // An iPhone HEVC video on a PC without Microsoft's HEVC Video Extensions is converted before capture; the converted
        // copy replaces this preview then.
        catch(NotSupportedException){lock(sync)failure="Preparing a preview of this video… It is converted to a format this PC can play when it is processed.";}
        catch(Exception e){lock(sync)failure="Video preview: "+e.Message;}
        finally{stop.Dispose();}
    }
    public void Present()
    {
        DecodedVideoFrame decoded;var previousError=Error;
        lock(sync){decoded=pending;pending=null;Duration=duration;Error=failure;}
        if(Error!=previousError)Update();
        // The depth border keeps moving.
        if(depthSource.Kind!=DepthKind.None)Update();
        if(decoded is null||FrameTime==decoded.Time)return;
        // The decoder supplies the same visible, oriented image used by inference.
        var size=new Vector2(decoded.Width,decoded.Height);
        if(frame is null||frame.Size!=size)frame=new Pixmap(size);
        frame.UpdateFromPixels(decoded.Rgba,size,ImageFormat.RGBA8888);
        FrameTime=decoded.Time;Update();
    }
    internal byte[] FramePng()=>frame?.GetPng();
    protected override void OnPaint()
    {
        Paint.ClearPen();Paint.SetBrush(Theme.ControlBackground);Paint.DrawRect(LocalRect);
        if(frame is null)
        {
            if(Error is not null){Paint.SetPen(Theme.TextLight);Paint.DrawText(LocalRect.Shrink(12),Error,TextFlag.Center|TextFlag.WordWrap);}
            return;
        }
        var scale=Math.Min(Width/frame.Size.x,Height/frame.Size.y);
        var size=frame.Size*scale;
        var picture=new Rect((Width-size.x)*.5f,(Height-size.y)*.5f,size.x,size.y);
        if(showDepth&&DepthImage() is { } depth)Paint.Draw(picture,depth);
        else Paint.Draw(picture,frame);
        if(depthSource.Kind!=DepthKind.None)DrawDepthMark(picture);
    }
    /// <summary>The depth frame taken with the picture on screen, coloured: turbo-like from red (near) to blue (far) for
    /// measured depth, over the clip's own range; partial (Cinematic) depth is relative, so coloured by its own values.</summary>
    Pixmap DepthImage()
    {
        if(depthTrack is null||FrameTime is not double time)return null;
        var index=depthTrack.FrameAt(time);if(index<0)index=depthTrack.Times.Count==0?-1:Math.Clamp(depthTrack.Times.BinarySearch(time) is var i&&i<0?~i:i,0,depthTrack.Times.Count-1);
        if(index<0)return null;
        if(index==depthImageFrame&&depthImage is not null)return depthImage;
        var pixels=depthTrack.Frames[index];int w=depthTrack.Width,h=depthTrack.Height;
        var valid=pixels.Where(v=>v!=0).ToArray();if(valid.Length==0)return null;
        Array.Sort(valid);float low=valid[valid.Length/50],high=valid[Math.Max(0,valid.Length-1-valid.Length/50)];if(high<=low)high=low+1;
        var rgba=new byte[w*h*4];var near=depthTrack.Kind==DepthKind.Full;
        for(var k=0;k<pixels.Length;k++)
        {
            var o=k*4;rgba[o+3]=255;var v=pixels[k];
            if(v==0){rgba[o]=rgba[o+1]=rgba[o+2]=18;continue;}
            var t=Math.Clamp((v-low)/(high-low),0,1);if(!near)t=1-t; // disparity: larger is nearer
            // Near red, then yellow and green, far blue.
            var (r,g,b)=t<.33f?(1f,t/.33f,0f):t<.66f?(1-(t-.33f)/.33f,1f,(t-.33f)/.33f*.5f):(0f,1-(t-.66f)/.34f,.5f+(t-.66f)/.34f*.5f);
            rgba[o]=(byte)(r*255);rgba[o+1]=(byte)(g*255);rgba[o+2]=(byte)(b*255);
        }
        var size=new Vector2(w,h);if(depthImage is null||depthImage.Size!=size)depthImage=new Pixmap(size);
        depthImage.UpdateFromPixels(rgba,size,ImageFormat.RGBA8888);depthImageFrame=index;return depthImage;
    }
    void DrawDepthMark(Rect picture)
    {
        // One colour per kind, its shade swinging between darker and lighter every two seconds or so.
        var colour=depthSource.Kind==DepthKind.Full?new Color(.2f,.85f,.45f):new Color(1f,.7f,.15f);
        var swing=.5f+.5f*MathF.Sin((float)RealTime.Now*3f);
        var shade=Color.Lerp(colour.Darken(.35f),colour.Lighten(.45f),swing);
        Paint.Antialiasing=true;Paint.ClearBrush();Paint.SetPen(shade,2);
        Paint.DrawRect(picture.Shrink(1),3);
        // The badge is just the words, in the border's colour: capitals, monospaced, no box behind them.
        Paint.SetFont("Consolas",10,700);
        var label=depthSource.Label.ToUpperInvariant();var textSize=Paint.MeasureText(label);
        var badge=new Rect(picture.Left+12,picture.Top+10,textSize.x+4,textSize.y+4);
        Paint.SetPen(Color.Black.WithAlpha(.6f));Paint.DrawText(new Rect(badge.Left+1,badge.Top+1,badge.Width,badge.Height),label,TextFlag.LeftCenter);
        Paint.SetPen(shade);Paint.DrawText(badge,label,TextFlag.LeftCenter);
        Paint.SetDefaultFont();
    }
    protected override void OnMouseClick(MouseEvent e)
    {base.OnMouseClick(e);if(e.LeftMouseButton)TogglePlayback?.Invoke();}
    public override void OnDestroyed()
    {try{stop.Cancel();}catch(ObjectDisposedException){}base.OnDestroyed();}
}
