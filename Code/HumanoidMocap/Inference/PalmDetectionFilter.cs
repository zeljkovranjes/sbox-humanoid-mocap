// Weighted suppression adapted from MediaPipe's NonMaxSuppressionCalculator.
// Copyright 2019 The MediaPipe Authors. Licensed under Apache-2.0.
// See Editor/HumanoidMocap/Inference/MediaPipe.LICENSE and THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Inference;
using Vector2 = System.Numerics.Vector2;

public sealed record PalmDetection(float Score,float X,float Y,float Width,float Height,Vector2[] Points);

public static class PalmDetectionFilter
{
    /// <summary>Merge boxes and keypoints by detection score. Each cluster is
    /// compared with its highest-scoring original box, not its moving average.
    /// The seed score is retained; averaging does not create a confidence score.</summary>
    public static List<PalmDetection> Merge(IEnumerable<PalmDetection> candidates,int limit=2,float threshold=.3f)
    {
        var remaining=candidates.Where(p=>float.IsFinite(p.Score+p.X+p.Y+p.Width+p.Height)
            &&p.Score>0&&p.Width>0&&p.Height>0&&p.Points.All(v=>float.IsFinite(v.X+v.Y)))
            .OrderByDescending(p=>p.Score).ToList();
        var result=new List<PalmDetection>();
        while(remaining.Count>0&&result.Count<limit)
        {
            var seed=remaining[0];var rest=new List<PalmDetection>();
            var points=new Vector2[seed.Points.Length];float weight=0,xmin=0,ymin=0,xmax=0,ymax=0;
            foreach(var candidate in remaining)
            {
                if(Iou(seed,candidate)<=threshold){rest.Add(candidate);continue;}
                if(candidate.Points.Length!=points.Length)throw new ArgumentException("Palm keypoint counts differ.");
                var score=candidate.Score;weight+=score;
                xmin+=(candidate.X-candidate.Width/2)*score;ymin+=(candidate.Y-candidate.Height/2)*score;
                xmax+=(candidate.X+candidate.Width/2)*score;ymax+=(candidate.Y+candidate.Height/2)*score;
                for(var i=0;i<points.Length;i++)points[i]+=candidate.Points[i]*score;
            }
            if(weight<=0)break;
            xmin/=weight;ymin/=weight;xmax/=weight;ymax/=weight;
            for(var i=0;i<points.Length;i++)points[i]/=weight;
            result.Add(new(seed.Score,(xmin+xmax)/2,(ymin+ymax)/2,xmax-xmin,ymax-ymin,points));
            remaining=rest;
        }
        return result;
    }

    static float Iou(PalmDetection a,PalmDetection b)
    {
        var width=Math.Max(0,Math.Min(a.X+a.Width/2,b.X+b.Width/2)-Math.Max(a.X-a.Width/2,b.X-b.Width/2));
        var height=Math.Max(0,Math.Min(a.Y+a.Height/2,b.Y+b.Height/2)-Math.Max(a.Y-a.Height/2,b.Y-b.Height/2));
        var intersection=width*height;var union=a.Width*a.Height+b.Width*b.Height-intersection;
        return union>0?intersection/union:0;
    }
}
