// Pre/postprocessing adapted from OpenCV Zoo MPPersonDet at
// 47534e27c9851bb1128ccc0102f1145e27f23f98. See PersonDetector.LICENSE.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HumanoidMocap.Inference;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using Size=OpenCvSharp.Size;

namespace HumanoidMocap.Worker;

/// <summary>Lightweight image-space person observations; not a body pose or world tracker.</summary>
public sealed class PersonDetector : IDisposable
{
    public const string CheckpointSha256="47fd5599d6fa17608f03e0eb0ae230baa6e597d7e8a2c8199fe00abea55a701f";
    public const string Version="opencv-blazepose-person-v5-joint-following";
    readonly Net network;
    readonly string[] outputs;
    static readonly Point2f[] Anchors=CreateAnchors();
    public sealed record Detection(float Score,float[] FaceBox,float[] Landmarks)
    {
        // BlazePose's hip center and full-body point define a circumscribing circle.
        // Keep padding for the 3:4 ViTPose crop. These are estimated crop bounds.
        public GvhmrDecoder.Box Crop
        {
            get
            {
                var dx=Landmarks[2]-Landmarks[0];var dy=Landmarks[3]-Landmarks[1];
                // 1.4x radius padding also fits the circle inside the central 3:4
                // input width (0.75 * 2.8r = 2.1r), including raised/wide arms.
                return new(Landmarks[0],Landmarks[1],MathF.Sqrt(dx*dx+dy*dy)*2.8f);
            }
        }
    }
    public PersonDetector(string path)
    {
        // Loaded from memory: OpenCV takes narrow file names, which fail under a user folder with
        // characters outside the system code page.
        var model=File.ReadAllBytes(path);
        if(!Convert.ToHexString(SHA256.HashData(model)).Equals(CheckpointSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Person detector checksum mismatch.");
        network=CvDnn.ReadNetFromOnnx(model)??throw new InvalidDataException("Cannot load person detector.");
        network.SetPreferableBackend(Backend.OPENCV);network.SetPreferableTarget(OpenCvSharp.Dnn.Target.CPU);
        outputs=network.GetUnconnectedOutLayersNames().Select(n=>n??throw new InvalidDataException("Missing detector output name.")).ToArray();
    }
    /// <summary>The whole frame, the window around a previously followed subject and, while
    /// no subject is known, overlapping tiles. The 224-pixel detector input cannot see a
    /// subject that is small in the full frame; zoomed windows can. Results from all
    /// windows are in full-frame pixels, with duplicates of one person merged.</summary>
    public Detection[] DetectFollowing(DecodedVideoFrame frame,GvhmrDecoder.Box? previous,CancellationToken cancellation=default)
    {
        var found=Detect(frame,cancellation).ToList();
        if(previous is { } p)found.AddRange(Detect(frame,cancellation,new Rect2f(p.CenterX-p.Size*.8f,p.CenterY-p.Size*.8f,p.Size*1.6f,p.Size*1.6f)));
        else
        {
            var window=Math.Max(frame.Width,frame.Height)*.5f;
            for(var y=0f;;y+=window*.5f)
            {
                for(var x=0f;;x+=window*.5f)
                {
                    found.AddRange(Detect(frame,cancellation,new Rect2f(x,y,window,window)));
                    if(x+window>=frame.Width)break;
                }
                if(y+window>=frame.Height)break;
            }
        }
        var merged=new List<Detection>();
        foreach(var candidate in found.OrderByDescending(d=>d.Score))
        {
            var crop=candidate.Crop;
            if(merged.Any(d=>{var other=d.Crop;var size=Math.Max(crop.Size,other.Size);
                return MathF.Abs(crop.CenterX-other.CenterX)<size*.3f&&MathF.Abs(crop.CenterY-other.CenterY)<size*.3f&&Math.Min(crop.Size,other.Size)>size*.6f;}))continue;
            merged.Add(candidate);if(merged.Count==16)break;
        }
        return merged.ToArray();
    }
    public Detection[] Detect(DecodedVideoFrame frame,CancellationToken cancellation=default,Rect2f? window=null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var whole=new Mat();Cv2.CvtColor(rgba,whole,ColorConversionCodes.RGBA2RGB);
        var region=new Rect(0,0,frame.Width,frame.Height);
        if(window is { } requested)
        {
            var x0=Math.Clamp((int)MathF.Floor(requested.X),0,frame.Width-1);var y0=Math.Clamp((int)MathF.Floor(requested.Y),0,frame.Height-1);
            var x1=Math.Clamp((int)MathF.Ceiling(requested.X+requested.Width),x0+1,frame.Width);var y1=Math.Clamp((int)MathF.Ceiling(requested.Y+requested.Height),y0+1,frame.Height);
            region=new(x0,y0,x1-x0,y1-y0);if(region.Width<32||region.Height<32)return Array.Empty<Detection>();
        }
        using var rgb=new Mat(whole,region);
        using var normalized=new Mat();rgb.ConvertTo(normalized,MatType.CV_32FC3,2.0/255,-1);
        var ratio=224.0/Math.Max(region.Width,region.Height);
        var w=Math.Max(1,(int)(region.Width*ratio));var h=Math.Max(1,(int)(region.Height*ratio));
        var left=(224-w)/2;var top=(224-h)/2;
        using var resized=new Mat();Cv2.Resize(normalized,resized,new Size(w,h));
        using var padded=new Mat();Cv2.CopyMakeBorder(resized,padded,top,224-h-top,left,224-w-left,BorderTypes.Constant,Scalar.All(0));
        using var blob=CvDnn.BlobFromImage(padded);network.SetInput(blob);
        var tensors=outputs.Select(_=>new Mat()).ToArray();
        try
        {
            network.Forward(tensors,outputs);cancellation.ThrowIfCancellationRequested();
            var deltas=tensors.Single(t=>t.Total()==2254*12);var scores=tensors.Single(t=>t.Total()==2254);
            var raw=new float[2254*12];var logits=new float[2254];
            Marshal.Copy(deltas.Data,raw,0,raw.Length);Marshal.Copy(scores.Data,logits,0,logits.Length);
            var scale=Math.Max(region.Width,region.Height);var padX=(int)(left/ratio)-region.X;var padY=(int)(top/ratio)-region.Y;
            var candidates=new List<Detection>();
            for(var i=0;i<2254;i++)
            {
                var score=1/(1+MathF.Exp(-Math.Clamp(logits[i],-100,100)));if(score<.5f)continue;
                var b=i*12;var cx=(raw[b]/224+Anchors[i].X)*scale-padX;var cy=(raw[b+1]/224+Anchors[i].Y)*scale-padY;
                var bw=raw[b+2]*scale/224;var bh=raw[b+3]*scale/224;
                var points=new float[8];for(var j=0;j<4;j++)
                {points[j*2]=(raw[b+4+j*2]/224+Anchors[i].X)*scale-padX;points[j*2+1]=(raw[b+5+j*2]/224+Anchors[i].Y)*scale-padY;}
                var detection=new Detection(score,new[]{cx-bw/2,cy-bh/2,cx+bw/2,cy+bh/2},points);
                if(!float.IsFinite(score)||!points.All(float.IsFinite)||!detection.FaceBox.All(float.IsFinite)||bw<=0||bh<=0)continue;
                if(detection.Crop.Size<16||detection.Crop.Size>scale*4)continue;
                candidates.Add(detection);
            }
            var selected=new List<Detection>();
            foreach(var candidate in candidates.OrderByDescending(d=>d.Score))
            {
                if(selected.Any(d=>IntersectionOverUnion(candidate.FaceBox,d.FaceBox)>.3f))continue;
                selected.Add(candidate);if(selected.Count==16)break;
            }
            return selected.ToArray();
        }
        finally{foreach(var tensor in tensors)tensor.Dispose();}
    }
    static float IntersectionOverUnion(float[] a,float[] b)
    {
        var intersection=Math.Max(0,Math.Min(a[2],b[2])-Math.Max(a[0],b[0]))*Math.Max(0,Math.Min(a[3],b[3])-Math.Max(a[1],b[1]));
        return intersection/((a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-intersection);
    }
    static Point2f[] CreateAnchors()
    {
        var result=new List<Point2f>();
        foreach(var (grid,repeats) in new[]{(28,2),(14,2),(7,6)})
            for(var y=0;y<grid;y++)for(var x=0;x<grid;x++)for(var i=0;i<repeats;i++)result.Add(new((x+.5f)/grid,(y+.5f)/grid));
        return result.ToArray();
    }
    public void Dispose()=>network.Dispose();
}
