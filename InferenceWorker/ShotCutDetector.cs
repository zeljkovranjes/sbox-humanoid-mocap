using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>Finds the first hard cut in the selected range. Edited footage that jumps to a close-up or
/// another angle is a different camera and often a different framing of the body; following the
/// subject across it produced confident nonsense on a sample that cut from a full figure to a shot
/// of the legs. A cut is a frame whose coarse thumbnail both differs strongly from the previous one
/// and no longer correlates with it, which fast movement, pans and lighting changes do not do.</summary>
public static class ShotCutDetector
{
    public const string Prefix="Footage cuts to another shot";
    const int Columns=32,Rows=18;
    public const float MinimumDifference=.10f,MaximumCorrelation=.55f;
    /// <summary>Index into the selected frames of the first frame after a cut, or null.</summary>
    public static int? FirstCut(string video,IReadOnlyList<double> times,CancellationToken cancellation)
    {
        using var decoder=new WindowsVideoDecoder(video);float[]? previous=null;
        for(var i=0;i<times.Count;i++)
        {
            cancellation.ThrowIfCancellationRequested();DecodedVideoFrame? frame;
            do{frame=decoder.Read(cancellation);if(frame is null)return null;}while(frame.Time<times[i]-.00001);
            var current=Thumbnail(frame);
            if(previous is not null&&IsCut(previous,current))return i;
            previous=current;
        }
        return null;
    }
    public static bool IsCut(float[] a,float[] b)
    {
        if(a.Length!=b.Length||a.Length==0)throw new ArgumentException("Thumbnails differ in size.");
        double difference=0,meanA=0,meanB=0;
        for(var i=0;i<a.Length;i++){difference+=Math.Abs(a[i]-b[i]);meanA+=a[i];meanB+=b[i];}
        difference/=a.Length;meanA/=a.Length;meanB/=a.Length;
        if(difference<MinimumDifference)return false;
        double covariance=0,varianceA=0,varianceB=0;
        for(var i=0;i<a.Length;i++){var x=a[i]-meanA;var y=b[i]-meanB;covariance+=x*y;varianceA+=x*x;varianceB+=y*y;}
        // A flat frame has no structure to correlate; a large jump to or from one still counts.
        var correlation=varianceA<1e-9||varianceB<1e-9?0:covariance/Math.Sqrt(varianceA*varianceB);
        return correlation<MaximumCorrelation;
    }
    /// <summary>Mean luminance, 0-1, of a 32x18 grid of cells.</summary>
    public static float[] Thumbnail(DecodedVideoFrame frame)
    {
        var result=new float[Columns*Rows];var counts=new int[Columns*Rows];
        // Every fourth pixel is plenty for a cell average.
        for(var y=0;y<frame.Height;y+=4)for(var x=0;x<frame.Width;x+=4)
        {
            var cell=Math.Min(Rows-1,y*Rows/frame.Height)*Columns+Math.Min(Columns-1,x*Columns/frame.Width);var i=(y*frame.Width+x)*4;
            result[cell]+=(frame.Rgba[i]*.299f+frame.Rgba[i+1]*.587f+frame.Rgba[i+2]*.114f)/255;counts[cell]++;
        }
        for(var i=0;i<result.Length;i++)if(counts[i]>0)result[i]/=counts[i];
        return result;
    }
}
