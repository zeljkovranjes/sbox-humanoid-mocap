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
