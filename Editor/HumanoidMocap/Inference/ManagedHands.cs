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
    public const string ImplementationVersion="managed-hands-v8-separated-identities";
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
    public List<HandObservation> Detect(byte[] rgba,int width,int height,CancellationToken token=default)
        =>Observe(rgba,width,height,PalmRects(rgba,width,height,token),token);

    sealed record HandRect(float X,float Y,float Size,float Angle,bool Tracked=false);
    List<HandRect> PalmRects(byte[] rgba,int width,int height,CancellationToken token)
    {
        if(rgba.Length!=width*height*4)throw new ArgumentException("RGBA buffer size mismatch.");
        var longest=Math.Max(width,height);var input=Crop(rgba,width,height,192,width/2f,height/2f,longest,0);
        var output=palms.Run(input,token);var candidates=new List<PalmDetection>();
        for(var i=0;i<anchors.Count;i++)
        {
            var score=1/(1+MathF.Exp(-Math.Clamp(output[1][i],-100,100)));if(score<.5f)continue;
            var offset=i*18;
            Vector2 Point(int j)=>new((output[0][offset+j]/192+anchors[i].X-.5f)*longest+width/2f,
                (output[0][offset+j+1]/192+anchors[i].Y-.5f)*longest+height/2f);
            var center=Point(0);var points=Enumerable.Range(0,7).Select(j=>Point(4+2*j)).ToArray();
            candidates.Add(new(score,center.X,center.Y,output[0][offset+2]/192*longest,output[0][offset+3]/192*longest,points));
        }
        var selected=PalmDetectionFilter.Merge(candidates);
        var result=new List<HandRect>();
        foreach(var palm in selected)
        {
            var direction=palm.Points[2]-palm.Points[0];
            var angle=MathF.PI/2+MathF.Atan2(direction.Y,direction.X);
            var cos=MathF.Cos(angle);var sin=MathF.Sin(angle);
            var cx=palm.X+.5f*palm.Height*sin;var cy=palm.Y-.5f*palm.Height*cos;
            var size=Math.Max(palm.Width,palm.Height)*2.6f;
            if(size<2)continue;
            result.Add(new(cx,cy,size,angle));
        }
        return result;
    }

    /// <summary>Prefer prior landmark ROIs as in MediaPipe's video graph. Detect
    /// new palms only while fewer than two tracks remain. Every output still
    /// requires fresh landmark inference and presence; lost tracks are not poses.</summary>
    public List<HandObservation> DetectTracked(byte[] rgba,int width,int height,IReadOnlyList<HandObservation> previous,CancellationToken token=default)
    {
        if(rgba.Length!=width*height*4)throw new ArgumentException("RGBA buffer size mismatch.");
        var regions=new List<HandRect>();
        foreach(var prior in previous.Take(2))
        {
            if(prior.ImageLandmarks.Length!=21)continue;
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
            regions.Add(new(cos*center.X-sin*center.Y,sin*center.X+cos*center.Y,size,angle,true));
        }
        if(regions.Count<2)
            foreach(var candidate in PalmRects(rgba,width,height,token))
            {
                // Upstream HandAssociation uses axis-aligned ROI overlap and
                // prioritizes the prior regions. Redetection follows loss on
                // the next frame, without replacing successful crops each time.
                if(regions.Any(r=>RegionIou(r,candidate)>.5f))continue;
                regions.Add(candidate);if(regions.Count==2)break;
            }
        var current=Observe(rgba,width,height,regions,token);
        current=HandIdentity.PreserveSeparatedTracks(current,previous);
        return current.GroupBy(h=>h.Side).Select(g=>g.OrderByDescending(h=>h.Presence).First()).ToList();
    }

    List<HandObservation> Observe(byte[] rgba,int width,int height,IEnumerable<HandRect> regions,CancellationToken token)
    {
        var result=new List<HandObservation>();
        foreach(var r in regions)
        {
            var observation=Landmarks(rgba,width,height,r.X,r.Y,r.Size,r.Angle,token);
            if(observation is not null)result.Add(observation with{Tracked=r.Tracked});
        }
        return result;
    }

    static float RegionIou(HandRect a,HandRect b)
    {
        var width=Math.Max(0,Math.Min(a.X+a.Size/2,b.X+b.Size/2)-Math.Max(a.X-a.Size/2,b.X-b.Size/2));
        var height=Math.Max(0,Math.Min(a.Y+a.Size/2,b.Y+b.Size/2)-Math.Max(a.Y-a.Size/2,b.Y-b.Size/2));
        var intersection=width*height;return intersection/(a.Size*a.Size+b.Size*b.Size-intersection);
    }

    HandObservation? Landmarks(byte[] rgba,int width,int height,float cx,float cy,float size,float angle,CancellationToken token)
    {
            var cos=MathF.Cos(angle);var sin=MathF.Sin(angle);
            var predictions=hands.Run(Crop(rgba,width,height,224,cx,cy,size,angle,replicateBorder:true),token);
            if(predictions[1][0]<.5f)return null;
            var screen=new Vector3[21];var world=new Vector3[21];
            for(var i=0;i<21;i++)
            {
                var x=(predictions[0][i*3]/224-.5f)*size;var y=(predictions[0][i*3+1]/224-.5f)*size;
                screen[i]=new(cx+cos*x-sin*y,cy+sin*x+cos*y,predictions[0][i*3+2]/224/.4f*size);
                x=predictions[3][i*3];y=predictions[3][i*3+1];world[i]=new(cos*x-sin*y,sin*x+cos*y,predictions[3][i*3+2]);
            }
            // Preserve the task model's handedness label; mirrored footage can be corrected
            // explicitly with Swap hands. Verified against the visible stirring hand sample.
            var right=predictions[2][0];return new(right>.5f?"R":"L",predictions[1][0],Math.Max(right,1-right),screen,world);
    }
    static float[] Crop(byte[] rgba,int width,int height,int size,float cx,float cy,float span,float angle,bool replicateBorder=false)
    {
        var result=new float[size*size*3];var cos=Math.Cos(angle);var sin=Math.Sin(angle);
        for(var y=0;y<size;y++)for(var x=0;x<size;x++)
        {
            // MediaPipe's CPU converter maps ROI corners to [0,size] and samples
            // at integer destination pixels. OpenCV INTER_LINEAR quantizes the
            // fractional coordinates to 1/32, then rounds interpolated RGB bytes.
            var dx=((double)x/size-.5)*span;var dy=((double)y/size-.5)*span;
            var px=(int)Math.Round((cx+cos*dx-sin*dy)*32);var py=(int)Math.Round((cy+sin*dx+cos*dy)*32);
            var ix=px>>5;var iy=py>>5;var fx=px&31;var fy=py&31;
            for(var c=0;c<3;c++)
            {
                int Sample(int sx,int sy)
                {
                    if(replicateBorder){sx=Math.Clamp(sx,0,width-1);sy=Math.Clamp(sy,0,height-1);}
                    return sx<0||sy<0||sx>=width||sy>=height?0:rgba[(sy*width+sx)*4+c];
                }
                var value=Sample(ix,iy)*(32-fx)*(32-fy)+Sample(ix+1,iy)*fx*(32-fy)
                    +Sample(ix,iy+1)*(32-fx)*fy+Sample(ix+1,iy+1)*fx*fy;
                result[(y*size+x)*3+c]=((value+512)>>10)/255f;
            }
        }
        return result;
    }
}
