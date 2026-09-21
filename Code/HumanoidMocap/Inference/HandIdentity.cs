using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Inference;

/// <summary>Retain established left/right identities only while both independently
/// observed hands remain close to their own, spatially separated prior tracks.
/// This changes labels, never landmarks or presence, and creates no observations.</summary>
public static class HandIdentity
{
    static Vector2 WristOf(HandObservation h)=>new(h.ImageLandmarks[0].X,h.ImageLandmarks[0].Y);
    static float SpanOf(HandObservation h)=>new[]{5,9,13,17}.Max(i=>Vector2.Distance(WristOf(h),new(h.ImageLandmarks[i].X,h.ImageLandmarks[i].Y)));
    static bool Usable(HandObservation h)=>h.Side is "L" or "R"&&h.ImageLandmarks.Length==21&&float.IsFinite(h.Handedness)&&h.Handedness>=0&&h.Handedness<=1&&
        h.ImageLandmarks.All(p=>float.IsFinite(p.X+p.Y))&&SpanOf(h)>=4;

    /// <summary>Physical consistency between consecutive frames, applied after
    /// <see cref="PreserveSeparatedTracks"/>. A hand resting on one established track
    /// cannot be the other hand when that hand's track was somewhere else a frame ago,
    /// however confident the side classifier is. Two observations on the same physical
    /// hand collapse to one. Labels and duplicates only: no landmark is edited and no
    /// observation is created.</summary>
    public static List<HandObservation> ResolveConflicts(IReadOnlyList<HandObservation> current,IReadOnlyList<HandObservation> previous)
    {
        var result=current.ToList();
        if(result.Count==0||result.Any(h=>!Usable(h)))return result;
        var tracks=previous.Where(Usable).ToArray();
        HandObservation? Nearest(HandObservation hand)
        {
            var nearest=tracks.OrderBy(t=>Vector2.Distance(WristOf(t),WristOf(hand))).FirstOrDefault();
            return nearest is not null&&Vector2.Distance(WristOf(nearest),WristOf(hand))<=SpanOf(nearest)*.7f?nearest:null;
        }
        if(tracks.Length==2&&tracks[0].Side!=tracks[1].Side)
        {
            var separation=Vector2.Distance(WristOf(tracks[0]),WristOf(tracks[1]));var span=tracks.Max(SpanOf);
            if(separation>=span*1.5f)for(var i=0;i<result.Count;i++)
            {
                var hand=result[i];if(Nearest(hand) is not { } own||own.Side==hand.Side)continue;
                var claimed=tracks.First(t=>t.Side==hand.Side);
                // The claimed side's hand was at least a palm and a half away one frame ago.
                if(Vector2.Distance(WristOf(claimed),WristOf(hand))<span*1.5f)continue;
                result[i]=hand with{Side=own.Side,Handedness=1-hand.Handedness};
            }
        }
        for(var i=0;i<result.Count;i++)for(var j=result.Count-1;j>i;j--)
        {
            var a=result[i];var b=result[j];var span=Math.Max(SpanOf(a),SpanOf(b));
            var apart=Enumerable.Range(0,21).Average(k=>Vector2.Distance(new(a.ImageLandmarks[k].X,a.ImageLandmarks[k].Y),new(b.ImageLandmarks[k].X,b.ImageLandmarks[k].Y)));
            if(apart>=span*.5f)continue;
            // Same physical hand: keep the label of the track it continues, else the stronger presence.
            var side=Nearest(a)?.Side??Nearest(b)?.Side;
            var keepFirst=side is null||a.Side==b.Side?a.Presence>=b.Presence:a.Side==side;
            if(!keepFirst)result[i]=b;
            result.RemoveAt(j);
        }
        return result;
    }

    public static List<HandObservation> PreserveSeparatedTracks(IReadOnlyList<HandObservation> current,IReadOnlyList<HandObservation> previous)
    {
        var result=current.ToList();
        if(current.Count!=2||previous.Count!=2||previous[0].Side==previous[1].Side||current.Any(h=>!h.Tracked)||
            current.Concat(previous).Any(h=>h.Side is not ("L" or "R")||h.ImageLandmarks.Length!=21||
                !float.IsFinite(h.Handedness)||h.Handedness<0||h.Handedness>1))return result;
        Vector2 Wrist(HandObservation h)=>new(h.ImageLandmarks[0].X,h.ImageLandmarks[0].Y);
        float Span(HandObservation h)=>new[]{5,9,13,17}.Max(i=>Vector2.Distance(Wrist(h),new(h.ImageLandmarks[i].X,h.ImageLandmarks[i].Y)));
        var spans=previous.Select(Span).ToArray();
        if(spans.Any(s=>!float.IsFinite(s)||s<4))return result;
        var assignment=new int[2];
        for(var i=0;i<2;i++)
        {
            var a=Vector2.Distance(Wrist(current[i]),Wrist(previous[0]));
            var b=Vector2.Distance(Wrist(current[i]),Wrist(previous[1]));
            if(!float.IsFinite(a)||!float.IsFinite(b))return result;
            var nearest=a<b?0:1;var distance=Math.Min(a,b);var alternative=Math.Max(a,b);
            // Bounds scale with the observed palm. Close/crossing tracks, jumps and
            // reacquisition use the fresh classifier instead of forced identity.
            if(distance>spans[nearest]*.7f||alternative-distance<spans.Max()*.75f)return result;
            assignment[i]=nearest;
        }
        if(assignment[0]==assignment[1]||Vector2.Distance(Wrist(current[0]),Wrist(current[1]))<spans.Max()*1.5f)return result;
        for(var i=0;i<2;i++)
        {
            var side=previous[assignment[i]].Side;
            // Conservative heuristic: do not overrule a strong side prediction or
            // a weak presence result solely because its wrist stayed nearby.
            if(current[i].Side!=side&&current[i].Handedness<.75f&&current[i].Presence>=.9f)
                // The binary classifier's actual probability for the retained side.
                // A value below 0.5 honestly records disagreement, not added confidence.
                result[i]=current[i] with {Side=side,Handedness=1-current[i].Handedness};
        }
        return result;
    }
}
