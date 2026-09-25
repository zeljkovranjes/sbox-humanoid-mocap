using System.Numerics;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>Where a still camera's performer stands. The body network judges distance from the size of the person in
/// the picture, so a dancer on one spot drifted 50 cm away from the camera over a clip as arms spread and the body
/// crouched. With a still camera the floor is still too: a planted ankle lies where the line from the camera through
/// its picture position meets the floor. The floor sits at the planted ankles' median height along gravity (the
/// network's own estimate, averaged over the clip); each planted ankle then gives the body's offset from where the
/// network put it, and those offsets, filled across steps and smoothed, move the camera-space path.
/// <see cref="InPlace"/> then says whether that path stays on one spot.</summary>
public static class StillCameraDepth
{
    /// <summary>Gaussian smoothing of the correction, seconds.</summary>
    public const float SmoothSeconds=.5f;
    /// <summary>Hips on the floor, averaged over AverageSeconds, within this radius 90% of the time mark a performance in place (fully below InPlaceCm,
    /// fading out by InPlaceFullCm), as <see cref="HumanoidMocap.Motion.CaptureContactRoot"/> judges it.</summary>
    public const float InPlaceCm=15,InPlaceFullCm=30,AverageSeconds=2;
    /// <summary>Lines of sight flatter than this against the floor (sine of the angle below horizontal) give unstable distances.</summary>
    const float MinimumDip=.05f;
    const float MaximumOffset=1.5f;

    /// <param name="ankles">Per frame, the left and right ankle positions in camera axes (x right, y down, z forward), metres.</param>
    /// <param name="observations">Per frame, COCO 2D joints (x, y, score) in the picture.</param>
    /// <param name="planted">Per frame, whether the left and right ankle rest on the floor.</param>
    /// <param name="down">Gravity's direction in camera axes.</param>
    /// <returns>The offset to add to each frame's camera-space translation and the largest one in metres; null when
    /// too few planted, visible ankles see the floor from above.</returns>
    public static (Vector3[] Offsets,float Largest)? Solve(Vector3[][] ankles,float[][] observations,bool[][] planted,Vector3 down,float focal,float centerX,float centerY,float fps)
    {
        var frames=ankles.Length;if(frames<3||observations.Length!=frames||planted.Length!=frames)return null;
        down=Vector3.Normalize(down);
        var heights=new List<float>();
        for(var t=0;t<frames;t++)for(var s=0;s<2;s++)if(planted[t][s])heights.Add(Vector3.Dot(ankles[t][s],down));
        if(heights.Count<Math.Max(4,frames/10))return null;
        heights.Sort();var floor=heights[heights.Count/2];if(floor<=0)return null;
        var samples=new Vector3?[frames];
        for(var t=0;t<frames;t++)
        {
            var sum=Vector3.Zero;var n=0;
            for(var s=0;s<2;s++)
            {
                var o=observations[t];var k=15+s;if(!planted[t][s]||o.Length<51||o[k*3+2]<.5f)continue;
                var ray=new Vector3((o[k*3]-centerX)/focal,(o[k*3+1]-centerY)/focal,1);
                if(Vector3.Dot(Vector3.Normalize(ray),down)<MinimumDip)continue;
                var offset=ray*(floor/Vector3.Dot(ray,down))-ankles[t][s];
                if(offset.Length()>MaximumOffset)continue;
                sum+=offset;n++;
            }
            if(n>0)samples[t]=sum/n;
        }
        var known=Enumerable.Range(0,frames).Where(t=>samples[t] is not null).ToArray();
        if(known.Length<Math.Max(4,frames/10))return null;
        var filled=new Vector3[frames];
        for(var t=0;t<frames;t++)
        {
            var after=Array.FindIndex(known,k=>k>=t);
            if(after<0)filled[t]=samples[known[^1]]!.Value;
            else if(after==0||known[after]==t)filled[t]=samples[known[after]]!.Value;
            else{var a=known[after-1];var b=known[after];filled[t]=Vector3.Lerp(samples[a]!.Value,samples[b]!.Value,(t-a)/(float)(b-a));}
        }
        var sigma=Math.Max(1,SmoothSeconds*fps);var radius=(int)Math.Ceiling(3*sigma);var result=new Vector3[frames];var largest=0f;
        for(var t=0;t<frames;t++)
        {
            var sum=Vector3.Zero;var weights=0f;
            for(var k=-radius;k<=radius;k++){var w=MathF.Exp(-k*k/(2*sigma*sigma));sum+=filled[Math.Clamp(t+k,0,frames-1)]*w;weights+=w;}
            result[t]=sum/weights;largest=Math.Max(largest,result[t].Length());
        }
        return (result,largest);
    }
    /// <summary>1 when a camera-space path stays on one spot of the floor (a performance in place), 0 when it travels,
    /// graded between. The floor position is the path with its height along gravity removed, averaged over
    /// AverageSeconds so a dancer's sway (hips swinging 40 cm while the feet stay put) is not taken for travel.</summary>
    public static float InPlace(IReadOnlyList<Vector3> path,Vector3 down,float fps)
    {
        if(path.Count<3)return 0;down=Vector3.Normalize(down);
        var ground=path.Select(p=>p-down*Vector3.Dot(p,down)).ToArray();var half=Math.Max(1,(int)(AverageSeconds*fps/2));
        var floor=Enumerable.Range(0,ground.Length).Select(t=>{var sum=Vector3.Zero;var n=0;for(var k=Math.Max(0,t-half);k<=Math.Min(ground.Length-1,t+half);k++){sum+=ground[k];n++;}return sum/n;}).ToArray();
        float Median(Func<Vector3,float> axis){var v=floor.Select(axis).OrderBy(x=>x).ToArray();return v[v.Length/2];}
        var middle=new Vector3(Median(p=>p.X),Median(p=>p.Y),Median(p=>p.Z));
        var spread=floor.Select(p=>(p-middle).Length()*100).OrderBy(d=>d).ElementAt((int)(floor.Length*.9f));
        return Math.Clamp((InPlaceFullCm-spread)/(InPlaceFullCm-InPlaceCm),0,1);
    }
}
