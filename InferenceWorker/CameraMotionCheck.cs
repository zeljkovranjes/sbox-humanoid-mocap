using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>Decides whether the recording camera stayed still, from background image
/// features tracked against the first selected frame. The tracked person's crop is
/// excluded. This is an image-motion test only: it does not recover camera motion,
/// and a still image of a moving scene can mislead it.</summary>
public sealed class CameraMotionCheck : IDisposable
{
    public const string Version="background-features-v3";
    public const string StationaryPrefix="Stationary recording camera:";
    public const string MovingPrefix="Moving or undetermined recording camera:";
    /// <summary>Background shift, as a fraction of image width, below which the camera counts as still.</summary>
    public const float StationaryShift=.004f;
    const int WorkingWidth=480,SampleInterval=5,MinimumPoints=24;
    /// <summary>Largest mean change of background brightness, 0-1, below which a featureless background counts as unchanged.</summary>
    public const float UnchangedBackground=.012f;
    public sealed record Result(bool Stationary,float MedianShift,float LargestShift,int Samples,int UsableSamples,bool PlainBackground=false,float LargestBackgroundChange=0)
    {
        public string Diagnostic=>PlainBackground
            ?FormattableString.Invariant($"{StationaryPrefix} the background has too little detail to follow ({UsableSamples}/{Samples} sampled frames had enough features), but outside the tracked person its brightness changed by at most {LargestBackgroundChange*100:F1}% from the first frame, so nothing shows the camera moving and it is treated as still. Image test on a plain backdrop, not camera tracking.")
            :FormattableString.Invariant($"{(Stationary?StationaryPrefix:MovingPrefix)} background features moved {MedianShift*100:F2}% of image width at the median and {LargestShift*100:F2}% at most across {UsableSamples}/{Samples} sampled frames, outside the tracked person. Image-motion test, not camera tracking.");
    }
    Mat? reference;Point2f[] points=Array.Empty<Point2f>();float scale=1;int frames;
    readonly List<float> shifts=new();int samples;float largestChange;Rect2f referenceBody;
    public void Add(DecodedVideoFrame frame,GvhmrDecoder.Box person)
    {
        var index=frames++;if(index%SampleInterval!=0)return;
        scale=WorkingWidth/(float)frame.Width;
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var full=new Mat();Cv2.CvtColor(rgba,full,ColorConversionCodes.RGBA2GRAY);
        var gray=new Mat();Cv2.Resize(full,gray,new Size(WorkingWidth,Math.Max(1,(int)MathF.Round(frame.Height*scale))),0,0,InterpolationFlags.Area);
        // The model crop is a generous square around a tall, narrow subject; masking all of
        // it left no background on the tennis sample. Limbs outside this band are handled
        // by forward-backward agreement and the median.
        Rect2f Body()=>new((person.CenterX-person.Size*.3f)*scale,(person.CenterY-person.Size*.55f)*scale,person.Size*.6f*scale,person.Size*1.1f*scale);
        if(reference is null)
        {
            reference=gray;var body=Body();referenceBody=body;
            using var mask=new Mat(gray.Size(),MatType.CV_8UC1,Scalar.White);
            Cv2.Rectangle(mask,new Rect((int)body.X,(int)body.Y,(int)body.Width,(int)body.Height),Scalar.Black,-1);
            points=Cv2.GoodFeaturesToTrack(gray,400,.01,8,mask,3,false,.04);
            return;
        }
        using(gray)
        {
            samples++;
            // Brightness change outside the person in both frames: the only evidence a plain backdrop offers.
            using(var difference=new Mat())
            using(var outside=new Mat(gray.Size(),MatType.CV_8UC1,Scalar.White))
            {
                Cv2.Absdiff(reference,gray,difference);
                foreach(var box in new[]{referenceBody,Body()})
                    Cv2.Rectangle(outside,new Rect((int)(box.X-box.Width*.25f),(int)(box.Y-box.Height*.1f),(int)(box.Width*1.5f),(int)(box.Height*1.2f)),Scalar.Black,-1);
                if(Cv2.CountNonZero(outside)>gray.Width*gray.Height/10)largestChange=Math.Max(largestChange,(float)Cv2.Mean(difference,outside).Val0/255);
                else largestChange=1; // the person fills the frame: no background to judge
            }
            if(points.Length<MinimumPoints)return;
            var tracked=new Point2f[points.Length];
            Cv2.CalcOpticalFlowPyrLK(reference,gray,points,ref tracked,out var status,out _,new Size(21,21),4);
            var returned=new Point2f[points.Length];
            Cv2.CalcOpticalFlowPyrLK(gray,reference,tracked,ref returned,out var back,out _,new Size(21,21),4);
            var now=Body();var moved=new List<float>();
            for(var i=0;i<points.Length;i++)
            {
                if(status[i]==0||back[i]==0||now.Contains(tracked[i]))continue;
                // Forward-backward agreement rejects features lost behind the moving person.
                if(points[i].DistanceTo(returned[i])>1)continue;
                moved.Add((float)points[i].DistanceTo(tracked[i])/WorkingWidth);
            }
            if(moved.Count<MinimumPoints)return;
            moved.Sort();shifts.Add(moved[moved.Count/2]);
        }
    }
    public Result Finish()
    {
        if(samples>=2&&shifts.Count<samples*.8f&&largestChange<UnchangedBackground)
            return new(true,0,0,samples,shifts.Count,true,largestChange);
        if(samples<2||shifts.Count<samples*.8f)return new(false,shifts.Count==0?0:shifts.OrderBy(s=>s).ElementAt(shifts.Count/2),shifts.Count==0?0:shifts.Max(),samples,shifts.Count);
        var ordered=shifts.OrderBy(s=>s).ToArray();
        return new(ordered[^1]<StationaryShift,ordered[ordered.Length/2],ordered[^1],samples,ordered.Length);
    }
    public void Dispose(){reference?.Dispose();reference=null;}
}
