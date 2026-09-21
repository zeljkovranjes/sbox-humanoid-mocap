using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>Conservative single-subject association in image coordinates. Long loss or
/// ambiguous subjects require a shorter range or an explicit manual crop.</summary>
public static class PersonCropTrack
{
    public sealed record Frame(double Time,PersonDetector.Detection[] Detections);
    public sealed record Sample(GvhmrDecoder.Box Crop,string Evidence,float? DetectorScore);
    /// <summary>The detection that continues the followed subject, or the single prominent
    /// subject while none is followed. Null when absent or ambiguous.</summary>
    public static PersonDetector.Detection? Select(GvhmrDecoder.Box? previous,IReadOnlyList<PersonDetector.Detection> detections)
    {
        var candidates=detections.Where(d=>d.Score>=.5f&&float.IsFinite(d.Score)&&
            d.Landmarks.Length==8&&d.Landmarks.All(float.IsFinite)&&d.Crop.Size>=16&&float.IsFinite(d.Crop.Size)).ToArray();
        if(previous is not { } p)
        {
            // Prefer the prominent foreground subject; do not pick arbitrarily
            // when two candidates have comparable apparent size and evidence.
            var prominent=candidates.OrderByDescending(d=>d.Crop.Size*d.Score).ToArray();
            return prominent.Length>0&&(prominent.Length==1||prominent[1].Crop.Size*prominent[1].Score<prominent[0].Crop.Size*prominent[0].Score*.8f)?prominent[0]:null;
        }
        var ranked=candidates.Select(d=>new{Detection=d,Distance=Distance(d.Crop,p)/p.Size,Scale=d.Crop.Size/p.Size})
            .Where(d=>d.Distance<.65f&&d.Scale>.5f&&d.Scale<2)
            .Select(d=>new{d.Detection,Cost=d.Distance+Math.Abs(MathF.Log(d.Scale))*.4f+(1-d.Detection.Score)*.1f})
            .OrderBy(d=>d.Cost).ToArray();
        return ranked.Length>0&&(ranked.Length==1||ranked[1].Cost-ranked[0].Cost>.15f)?ranked[0].Detection:null;
    }
    /// <summary>Square model crop around confidently located COCO-17 joints (x, y, score),
    /// or null when fewer than six are confident. The joint box excludes the top of the
    /// head, hands and feet, so it is padded more than a detector's person box would be.</summary>
    public static GvhmrDecoder.Box? FromJoints(float[] joints)
    {
        if(joints.Length!=51)throw new ArgumentException("Expected COCO-17 joints.");
        float left=float.MaxValue,top=float.MaxValue,right=float.MinValue,bottom=float.MinValue;var confident=0;
        for(var j=0;j<17;j++)
        {
            if(!(joints[j*3+2]>=.5f)||!float.IsFinite(joints[j*3]+joints[j*3+1]))continue;
            confident++;left=Math.Min(left,joints[j*3]);right=Math.Max(right,joints[j*3]);top=Math.Min(top,joints[j*3+1]);bottom=Math.Max(bottom,joints[j*3+1]);
        }
        if(confident<6)return null;
        var size=Math.Max(right-left,bottom-top)*1.4f;
        return size>=16?new((left+right)/2,(top+bottom)/2,size):null;
    }
    /// <summary>Whether these joints show a body that can be followed. <paramref name="previousSpan"/> is the
    /// <see cref="Span"/> of the subject's last sighting, or null on first acquisition.
    /// As a performer walked out of the picture the pose model kept answering: first with joints piled
    /// along the border they left by, then with a confident "body" 30 pixels tall on a shelf the detector
    /// had offered, which kept a capture going for a second after the person was gone. A followable body
    /// has six confident joints clear of the picture's border, some of its torso among them, and has not
    /// suddenly shrunk to a fraction of its size a moment ago.</summary>
    public static bool Followable(float[] joints,float? previousSpan,int width,int height)
        =>FromJoints(joints) is not null&&Span(joints,width,height) is float span&&(previousSpan is not float known||span>=known*MinimumSpanKept);
    /// <summary>Larger side of the box around confident joints clear of the border, or null when fewer than six
    /// are, or none of them is a shoulder or hip.</summary>
    public static float? Span(float[] joints,int width,int height)
    {
        float marginX=width*BorderFraction,marginY=height*BorderFraction,left=float.MaxValue,top=float.MaxValue,right=float.MinValue,bottom=float.MinValue;
        var inside=0;var torso=false;
        for(var j=0;j<17;j++)
        {
            float x=joints[j*3],y=joints[j*3+1];
            if(!(joints[j*3+2]>=.5f)||x<marginX||x>width-marginX||y<marginY||y>height-marginY)continue;
            inside++;torso|=j is 5 or 6 or 11 or 12;left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
        }
        return inside>=6&&torso?Math.Max(right-left,bottom-top):null;
    }
    public const float BorderFraction=.03f,MinimumSpanKept=.4f;
    public static int ConfidentJoints(float[] joints)=>Enumerable.Range(0,17).Count(j=>joints[j*3+2]>=.5f);
    /// <summary>Next crop while following one subject. A box around partly visible joints is
    /// smaller than the body, and a smaller crop hides more joints on the next frame; on the
    /// kata sample that collapsed a 292-pixel crop to 54 pixels within five frames. Size may
    /// therefore shrink by at most 8% per frame, only while at least twelve joints are
    /// confident, and grow by at most 35%.</summary>
    public static GvhmrDecoder.Box Continue(GvhmrDecoder.Box? previous,GvhmrDecoder.Box fromJoints,int confidentJoints)
    {
        if(previous is not { } p)return fromJoints;
        var smallest=confidentJoints>=12?p.Size*.92f:p.Size;
        return fromJoints with{Size=Math.Clamp(fromJoints.Size,smallest,p.Size*1.35f)};
    }
    public static Sample[] Build(IReadOnlyList<Frame> frames)
    {
        if(frames.Count==0)throw new ArgumentException("No frames for person tracking.");
        var result=new Sample[frames.Count];GvhmrDecoder.Box? previous=null;var lastObserved=-1;
        for(var i=0;i<frames.Count;i++)
        {
            if(!double.IsFinite(frames[i].Time)||(i>0&&frames[i].Time<=frames[i-1].Time))throw new ArgumentException("Person-track timestamps must increase.");
            var selected=Select(previous,frames[i].Detections);
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
