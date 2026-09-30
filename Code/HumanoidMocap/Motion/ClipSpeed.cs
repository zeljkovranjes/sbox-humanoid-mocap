using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HumanoidMocap.Motion;

/// <summary>How fast a clip moves in the picture, and how much to slow a clip that is too fast. Measured from the 2D pose, not the
/// capture: the body network smooths rushed motion, so a clip sped up twice was captured no faster (0.78 vs 0.81 m/s) while its
/// joints crossed the picture 1.7 times as fast. The measure is the median over frames of the joints' speed around the hips, in
/// body heights per second. Test clips: ordinary recordings 0.2-0.6, fast game emotes at their real speed 0.7-1.1 (tiki 1.08), tiki
/// sped up twice 1.82. Clips above <see cref="TooFast"/> are offered a slow-down to about <see cref="Comfortable"/>. Clips where the
/// performer turns upside down (a backflip, 2.5) are genuinely fast and are not measured; nor are clips without the feet in view.</summary>
public static class ClipSpeed
{
    public const float TooFast=1.5f,Comfortable=1f,SlowestSpeed=.25f;
    public const string PicturePrefix="Picture motion:";
    /// <summary>Share of frames with the shoulders below the hips in the picture that marks acrobatics.</summary>
    const float UpsideDownShare=.05f;

    /// <summary>The picture speed from COCO 2D joints (x, y, score per joint) and their times; null when it cannot be judged.</summary>
    public static float? Picture(IReadOnlyList<float[]> observations,IReadOnlyList<double> times)
    {
        if(observations.Count<10||observations.Count!=times.Count)return null;
        bool Seen(float[] o,int j)=>o is {Length:>=51}&&o[j*3+2]>.5f;
        // Body height: nose to the lower ankle, where both are seen; the feet must be in view.
        var heights=observations.Where(o=>Seen(o,0)&&(Seen(o,15)||Seen(o,16))).Select(o=>Math.Max(Seen(o,15)?o[15*3+1]:0,Seen(o,16)?o[16*3+1]:0)-o[1]).Where(h=>h>0).OrderBy(h=>h).ToArray();
        if(heights.Length<observations.Count/2)return null;
        var body=heights[heights.Length/2];
        var upsideDown=observations.Count(o=>Seen(o,5)&&Seen(o,6)&&Seen(o,11)&&Seen(o,12)&&(o[5*3+1]+o[6*3+1])/2>(o[11*3+1]+o[12*3+1])/2);
        if(upsideDown>=UpsideDownShare*observations.Count)return null;
        var speeds=new List<float>();
        for(var t=1;t<observations.Count;t++)
        {
            var a=observations[t-1];var b=observations[t];var dt=(float)(times[t]-times[t-1]);
            if(!(dt>0)||!Seen(a,11)||!Seen(a,12)||!Seen(b,11)||!Seen(b,12))continue;
            float ax=(a[33]+a[36])/2,ay=(a[34]+a[37])/2,bx=(b[33]+b[36])/2,by=(b[34]+b[37])/2;
            var moved=new List<float>();
            for(var j=0;j<17;j++)
                if(Seen(a,j)&&Seen(b,j))
                {
                    float dx=(b[j*3]-bx)-(a[j*3]-ax),dy=(b[j*3+1]-by)-(a[j*3+1]-ay);
                    moved.Add(MathF.Sqrt(dx*dx+dy*dy)/dt/body);
                }
            if(moved.Count>=8)speeds.Add(moved.Average());
        }
        if(speeds.Count<observations.Count/2)return null;
        speeds.Sort();return speeds[speeds.Count/2];
    }

    /// <summary>The capture's note recording the picture speed.</summary>
    public static string Note(float speed)=>FormattableString.Invariant($"{PicturePrefix} the joints moved {speed:0.00} body heights per second around the hips (median), for judging whether the clip is too fast.");
    /// <summary>The picture speed recorded in a capture's notes, or null.</summary>
    public static float? FromNotes(IEnumerable<string> notes)
    {
        var note=notes?.FirstOrDefault(n=>n.StartsWith(PicturePrefix,StringComparison.Ordinal));if(note is null)return null;
        var words=note.Substring(PicturePrefix.Length).Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var index=Array.IndexOf(words,"moved");
        return index>=0&&index+1<words.Length&&float.TryParse(words[index+1],NumberStyles.Float,CultureInfo.InvariantCulture,out var speed)?speed:null;
    }

    /// <summary>The playback speed to offer (0.25-0.95, in steps of 0.05), or null when the clip is not too fast.</summary>
    public static float? Recommended(float pictureSpeed)
    {
        if(!(pictureSpeed>TooFast))return null;
        return MathF.Floor(Math.Clamp(Comfortable/pictureSpeed,SlowestSpeed,.95f)*20)/20;
    }

    /// <summary>The capture played at <paramref name="speed"/> (0.5 = half speed): every time stretched by 1/speed.</summary>
    public static MotionDocument Retimed(MotionDocument motion,float speed)
    {
        if(!(speed>0)||Math.Abs(speed-1)<1e-4f)return motion;
        var copy=motion.Copy();var start=copy.Frames[0].Time;
        double At(double t)=>start+(t-start)/speed;
        foreach(var f in copy.Frames)f.Time=At(f.Time);
        foreach(var c in copy.Cameras)foreach(var f in c.Frames)f.Time=At(f.Time);
        foreach(var o in copy.Objects)foreach(var f in o.Frames)f.Time=At(f.Time);
        foreach(var c in copy.Contacts){c.Start=At(c.Start);c.End=At(c.End);foreach(var k in c.TargetKeys)k.Time=At(k.Time);}
        foreach(var c in copy.Corrections){c.Start=At(c.Start);c.End=At(c.End);}
        copy.SourceFps*=speed;
        copy.Diagnostics.Add(FormattableString.Invariant($"Played at {speed*100:0}% of the video's speed."));
        return copy;
    }
}
