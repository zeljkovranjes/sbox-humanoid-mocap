using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>Conservative single-subject association in image coordinates. Long loss or
/// ambiguous subjects require a shorter range or an explicit manual crop.</summary>
public static class PersonCropTrack
{
    public sealed record Frame(double Time,PersonDetector.Detection[] Detections);
    public sealed record Sample(GvhmrDecoder.Box Crop,string Evidence,float? DetectorScore);
    public static Sample[] Build(IReadOnlyList<Frame> frames)
    {
        if(frames.Count==0)throw new ArgumentException("No frames for person tracking.");
        var result=new Sample[frames.Count];GvhmrDecoder.Box? previous=null;var lastObserved=-1;
        for(var i=0;i<frames.Count;i++)
        {
            if(!double.IsFinite(frames[i].Time)||(i>0&&frames[i].Time<=frames[i-1].Time))throw new ArgumentException("Person-track timestamps must increase.");
            var candidates=frames[i].Detections.Where(d=>d.Score>=.5f&&float.IsFinite(d.Score)&&
                d.Landmarks.Length==8&&d.Landmarks.All(float.IsFinite)&&d.Crop.Size>=16&&float.IsFinite(d.Crop.Size)).ToArray();
            PersonDetector.Detection? selected=null;
            if(previous is null)
            {
                // Prefer the prominent foreground subject; do not pick arbitrarily
                // when two candidates have comparable apparent size and evidence.
                var ranked=candidates.OrderByDescending(d=>d.Crop.Size*d.Score).ToArray();
                if(ranked.Length>0&&(ranked.Length==1||ranked[1].Crop.Size*ranked[1].Score<ranked[0].Crop.Size*ranked[0].Score*.8f))selected=ranked[0];
            }
            else
            {
                var p=previous.Value;
                var ranked=candidates.Select(d=>new{Detection=d,Distance=Distance(d.Crop,p)/p.Size,Scale=d.Crop.Size/p.Size})
                    .Where(d=>d.Distance<.65f&&d.Scale>.5f&&d.Scale<2)
                    .Select(d=>new{d.Detection,Cost=d.Distance+Math.Abs(MathF.Log(d.Scale))*.4f+(1-d.Detection.Score)*.1f})
                    .OrderBy(d=>d.Cost).ToArray();
                if(ranked.Length>0&&(ranked.Length==1||ranked[1].Cost-ranked[0].Cost>.15f))selected=ranked[0].Detection;
            }
            if(selected is null)
            {
                if(lastObserved<0||frames[i].Time-frames[lastObserved].Time>.25)
                    throw new InvalidDataException($"Cannot follow one person at {frames[i].Time:F2}s. Use a shorter range with one clearly visible subject or supply PersonCrop in a manual worker job.");
                continue;
            }
            var crop=selected.Crop;result[i]=new(crop,"detected",selected.Score);
            if(lastObserved>=0&&i>lastObserved+1)
            {
                if(frames[i].Time-frames[lastObserved].Time>.25)throw new InvalidDataException("Person tracking gap exceeds 0.25 seconds. Use a shorter range or a manual crop.");
                for(var missing=lastObserved+1;missing<i;missing++)
                {
                    var amount=(float)((frames[missing].Time-frames[lastObserved].Time)/(frames[i].Time-frames[lastObserved].Time));
                    var a=previous!.Value;
                    result[missing]=new(new(a.CenterX+(crop.CenterX-a.CenterX)*amount,a.CenterY+(crop.CenterY-a.CenterY)*amount,a.Size+(crop.Size-a.Size)*amount),"interpolated crop",null);
                }
            }
            previous=crop;lastObserved=i;
        }
        if(lastObserved!=frames.Count-1)throw new InvalidDataException("Person tracking is lost at the end. Trim the range or supply a manual crop.");
        return result;
    }
    static float Distance(GvhmrDecoder.Box a,GvhmrDecoder.Box b)=>MathF.Sqrt((a.CenterX-b.CenterX)*(a.CenterX-b.CenterX)+(a.CenterY-b.CenterY)*(a.CenterY-b.CenterY));

    /// <summary>GVHMR's tracker applies a centered five-frame average twice to its
    /// bounding boxes before neural reconstruction. This filters image crops only;
    /// original detections, timestamps and reconstructed motion remain separate.</summary>
    public static Sample[] Stabilize(Sample[] track)
    {
        var result=track.ToArray();
        for(var pass=0;pass<2;pass++)
        {
            var source=result;result=new Sample[source.Length];
            for(var i=0;i<result.Length;i++)
            {
                float x=0,y=0,size=0;
                for(var offset=-2;offset<=2;offset++)
                {
                    var box=source[Math.Clamp(i+offset,0,source.Length-1)].Crop;
                    x+=box.CenterX/5;y+=box.CenterY/5;size+=box.Size/5;
                }
                result[i]=source[i] with{Crop=new(x,y,size)};
            }
        }
        return result;
    }
}
