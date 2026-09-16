using System;
using System.Collections.Generic;
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
    int visibleHeight;
    Pixmap frame;

    public MocapVideoWidget(Widget parent,string path):base(parent)
    {
        MinimumSize=50;
        _=Task.Run(()=>Decode(path));
    }
    public void Request(double seconds)
    {lock(sync)requested=Math.Max(0,seconds);}

    void Decode(string path)
    {
        try
        {
            var metadata=Mp4Metadata.Read(path);
            lock(sync){duration=metadata.Duration;visibleHeight=metadata.Height;}
            using var decoder=new WindowsVideoDecoder(path);
            var cache=new Dictionary<long,DecodedVideoFrame>();var order=new Queue<long>();
            static long Key(double time)=>(long)Math.Round(time*10000);
            DecodedVideoFrame Read()
            {
                var decoded=decoder.Read(stop.Token);if(decoded is null)return null;
                var visible=Math.Min(metadata.Height,decoded.Height);
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
        catch(Exception e){lock(sync)failure="Video preview: "+e.Message;}
        finally{stop.Dispose();}
    }
    public void Present()
    {
        DecodedVideoFrame decoded;int height;var previousError=Error;
        lock(sync){decoded=pending;pending=null;Duration=duration;Error=failure;height=visibleHeight;}
        if(Error!=previousError)Update();
        if(decoded is null||FrameTime==decoded.Time)return;
        // Media Foundation can return a 1088-row surface for a 1080-row video.
        // Exclude coded padding from both presentation and the aspect ratio.
        height=height>0?Math.Min(height,decoded.Height):decoded.Height;
        var size=new Vector2(decoded.Width,height);
        if(frame is null||frame.Size!=size)frame=new Pixmap(size);
        frame.UpdateFromPixels(decoded.Rgba.AsSpan(0,checked(decoded.Width*height*4)),size,ImageFormat.RGBA8888);
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
        Paint.Draw(new Rect((Width-size.x)*.5f,(Height-size.y)*.5f,size.x,size.y),frame);
    }
    protected override void OnMouseClick(MouseEvent e)
    {base.OnMouseClick(e);if(e.LeftMouseButton)TogglePlayback?.Invoke();}
    public override void OnDestroyed()
    {try{stop.Cancel();}catch(ObjectDisposedException){}base.OnDestroyed();}
}
