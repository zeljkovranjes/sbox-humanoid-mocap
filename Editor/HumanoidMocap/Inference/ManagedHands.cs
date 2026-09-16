using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace HumanoidMocap.Inference;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;



/// <summary>Experimental managed port of MediaPipe's palm + landmark inference graphs.
/// Image landmarks are pixels. World landmarks are hand-relative metres, not world tracking.</summary>
public sealed class ManagedHands
{
    public const string ImplementationVersion="managed-hands-v4-resize";
    readonly LiteInterpreter palms,hands;
    readonly List<Vector2> anchors=new();
    public ManagedHands(byte[] task)
    {
        using var zip=new ZipArchive(new MemoryStream(task),ZipArchiveMode.Read);
        LiteInterpreter Load(string name)
        {
            using var input=(zip.GetEntry(name)??throw new FormatException("Missing "+name)).Open();
            using var memory=new MemoryStream();input.CopyTo(memory);return new LiteInterpreter(new LiteModel(memory.ToArray()));
        }
        palms=Load("hand_detector.tflite");hands=Load("hand_landmarks_detector.tflite");
        foreach(var (size,repeats) in new[]{(24,2),(12,6)})
            for(var y=0;y<size;y++)for(var x=0;x<size;x++)for(var a=0;a<repeats;a++)anchors.Add(new((x+.5f)/size,(y+.5f)/size));
    }
    sealed record Palm(float Score,float X,float Y,float Width,float Height,Vector2[] Points);
    public List<HandObservation> Detect(byte[] rgba,int width,int height,CancellationToken token=default)
    {
        if(rgba.Length!=width*height*4)throw new ArgumentException("RGBA buffer size mismatch.");
        var longest=Math.Max(width,height);var input=Crop(rgba,width,height,192,width/2f,height/2f,longest,0);
        var output=palms.Run(input,token);var candidates=new List<Palm>();
        for(var i=0;i<anchors.Count;i++)
        {
            var score=1/(1+MathF.Exp(-Math.Clamp(output[1][i],-100,100)));if(score<.5f)continue;
            var offset=i*18;
            Vector2 Point(int j)=>new((output[0][offset+j]/192+anchors[i].X-.5f)*longest+width/2f,
                (output[0][offset+j+1]/192+anchors[i].Y-.5f)*longest+height/2f);
            var center=Point(0);var points=Enumerable.Range(0,7).Select(j=>Point(4+2*j)).ToArray();
            candidates.Add(new(score,center.X,center.Y,output[0][offset+2]/192*longest,output[0][offset+3]/192*longest,points));
        }
        var selected=new List<Palm>();
        foreach(var candidate in candidates.OrderByDescending(p=>p.Score))
        {
            if(selected.Any(p=>Iou(p,candidate)>.3f))continue;
            selected.Add(candidate);if(selected.Count==2)break;
        }
        var result=new List<HandObservation>();
        foreach(var palm in selected)
        {
            var direction=palm.Points[2]-palm.Points[0];
            var angle=MathF.PI/2+MathF.Atan2(direction.Y,direction.X);
            var cos=MathF.Cos(angle);var sin=MathF.Sin(angle);
            var cx=palm.X+.5f*palm.Height*sin;var cy=palm.Y-.5f*palm.Height*cos;
            var size=Math.Max(palm.Width,palm.Height)*2.6f;
            if(size<2)continue;
            var observation=Landmarks(rgba,width,height,cx,cy,size,angle,token);
            if(observation is not null)result.Add(observation);
        }
        return result;
    }

    /// <summary>Redetects palms, then attempts missing hands using the prior frame's
    /// landmark ROI. Every accepted track requires fresh landmark-model presence;
    /// no prior pose is returned as a newly observed hand.</summary>
    public List<HandObservation> DetectTracked(byte[] rgba,int width,int height,IReadOnlyList<HandObservation> previous,CancellationToken token=default)
    {
        var current=Detect(rgba,width,height,token);
        foreach(var prior in previous.Take(2))
        {
            if(current.Any(h=>h.Side==prior.Side)||prior.ImageLandmarks.Length!=21)continue;
            var points=prior.ImageLandmarks;
            var direction=(points[5]+points[13]+2*points[9])*.25f-points[0];
            var angle=MathF.PI/2+MathF.Atan2(direction.Y,direction.X);
            var cos=MathF.Cos(angle);var sin=MathF.Sin(angle);
            // MediaPipe's HandLandmarksToRect partial set and ROI graph: the
            // palm/proximal landmarks, square-long scale 2 and local y shift -0.1.
            var subset=new[]{0,1,2,3,5,6,9,10,13,14,17,18};
            var rotated=subset.Select(i=>new Vector2(cos*points[i].X+sin*points[i].Y,-sin*points[i].X+cos*points[i].Y)).ToArray();
            var min=new Vector2(rotated.Min(p=>p.X),rotated.Min(p=>p.Y));
            var max=new Vector2(rotated.Max(p=>p.X),rotated.Max(p=>p.Y));
            var center=(min+max)*.5f;center.Y-=(max.Y-min.Y)*.1f;
            var size=Math.Max(max.X-min.X,max.Y-min.Y)*2;
            if(!float.IsFinite(size)||size<2||size>Math.Max(width,height)*2)continue;
            var tracked=Landmarks(rgba,width,height,cos*center.X-sin*center.Y,sin*center.X+cos*center.Y,size,angle,token);
            if(tracked is not null&&tracked.Side==prior.Side)current.Add(tracked with{Tracked=true});
        }
        return current.GroupBy(h=>h.Side).Select(g=>g.OrderByDescending(h=>h.Presence).First()).ToList();
    }

    HandObservation? Landmarks(byte[] rgba,int width,int height,float cx,float cy,float size,float angle,CancellationToken token)
    {
            var cos=MathF.Cos(angle);var sin=MathF.Sin(angle);
            var predictions=hands.Run(Crop(rgba,width,height,224,cx,cy,size,angle),token);
            if(predictions[1][0]<.5f)return null;
            var screen=new Vector3[21];var world=new Vector3[21];
            for(var i=0;i<21;i++)
            {
                var x=(predictions[0][i*3]/224-.5f)*size;var y=(predictions[0][i*3+1]/224-.5f)*size;
                screen[i]=new(cx+cos*x-sin*y,cy+sin*x+cos*y,predictions[0][i*3+2]/224*size);
                x=predictions[3][i*3];y=predictions[3][i*3+1];world[i]=new(cos*x-sin*y,sin*x+cos*y,predictions[3][i*3+2]);
            }
            // Preserve the task model's handedness label; mirrored footage can be corrected
            // explicitly with Swap hands. Verified against the visible stirring hand sample.
            var right=predictions[2][0];return new(right>.5f?"R":"L",predictions[1][0],Math.Max(right,1-right),screen,world);
    }
    static float Iou(Palm a,Palm b)
    {
        var w=Math.Max(0,Math.Min(a.X+a.Width/2,b.X+b.Width/2)-Math.Max(a.X-a.Width/2,b.X-b.Width/2));
        var h=Math.Max(0,Math.Min(a.Y+a.Height/2,b.Y+b.Height/2)-Math.Max(a.Y-a.Height/2,b.Y-b.Height/2));
        return w*h/Math.Max(1,a.Width*a.Height+b.Width*b.Height-w*h);
    }
    static float[] Crop(byte[] rgba,int width,int height,int size,float cx,float cy,float span,float angle)
    {
        var result=new float[size*size*3];var cos=MathF.Cos(angle);var sin=MathF.Sin(angle);
        for(var y=0;y<size;y++)for(var x=0;x<size;x++)
        {
            var dx=((x+.5f)/size-.5f)*span;var dy=((y+.5f)/size-.5f)*span;
            var px=cx+cos*dx-sin*dy-.5f;var py=cy+sin*dx+cos*dy-.5f;
            var ix=(int)MathF.Floor(px);var iy=(int)MathF.Floor(py);var fx=px-ix;var fy=py-iy;
            for(var c=0;c<3;c++)
            {
                float Sample(int sx,int sy)=>sx<0||sy<0||sx>=width||sy>=height?0:rgba[(sy*width+sx)*4+c]/255f;
                result[(y*size+x)*3+c]=Sample(ix,iy)*(1-fx)*(1-fy)+Sample(ix+1,iy)*fx*(1-fy)+Sample(ix,iy+1)*(1-fx)*fy+Sample(ix+1,iy+1)*fx*fy;
            }
        }
        return result;
    }
}
