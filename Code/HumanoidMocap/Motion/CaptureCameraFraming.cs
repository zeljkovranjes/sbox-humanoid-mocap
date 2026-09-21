using System;
using System.Collections.Generic;
using System.Linq;

namespace HumanoidMocap.Motion;

/// <summary>Explicit pinhole assumptions and initial editor framing. Does not recover calibration.</summary>
public static class CaptureCameraFraming
{
    /// <summary>Focal length in source-image pixels. A supplied horizontal FOV is
    /// a recording-lens assumption, independent of the viewmodel camera.</summary>
    public static float EstimatedFocalLength(int width,int height,float? horizontalFovDegrees=null)
    {
        if(width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(width),"Video dimensions must be positive.");
        if(horizontalFovDegrees is not float fov)return (float)Math.Sqrt((double)width*width+(double)height*height);
        if(!float.IsFinite(fov)||fov<20||fov>150)
            throw new ArgumentOutOfRangeException(nameof(horizontalFovDegrees),"Recording FOV must be between 20 and 150 degrees.");
        return width/(2*MathF.Tan(fov*MathF.PI/360));
    }

    /// <summary>One hand's depth divided by the unknown focal length (metres per
    /// focal pixel), with the image position used for its viewing ray.</summary>
    public readonly record struct HandScaleSample(float DepthPerFocal,float X,float Y);
    public const float TypicalHandDistance=.45f,MaximumHandDistance=.65f;
    public const int MinimumHandScaleSamples=8;

    /// <summary>Metres per focal pixel from a weak-perspective fit of hand-centred
    /// metric landmarks to their image positions. Null when the fit is degenerate.</summary>
    public static float? DepthPerFocal(System.Numerics.Vector3[] image,System.Numerics.Vector3[] metric)
    {
        if(image is null||metric is null||image.Length!=metric.Length||image.Length<3)return null;
        float ix=0,iy=0,mx=0,my=0;
        for(var i=0;i<image.Length;i++){ix+=image[i].X;iy+=image[i].Y;mx+=metric[i].X;my+=metric[i].Y;}
        ix/=image.Length;iy/=image.Length;mx/=image.Length;my/=image.Length;
        double cross=0,spread=0;
        for(var i=0;i<image.Length;i++)
        {
            var ax=metric[i].X-mx;var ay=metric[i].Y-my;
            cross+=(image[i].X-ix)*ax+(image[i].Y-iy)*ay;spread+=ax*ax+ay*ay;
        }
        if(!double.IsFinite(cross+spread)||spread<1e-8||cross<=1e-6)return null;
        return (float)(spread/cross);
    }

    /// <summary>First-person lens assumption from hand size. A hand's metric size
    /// fixes depth only up to focal length, so the focal length is chosen to put
    /// the clip's median hand at a typical working distance from a head- or
    /// chest-mounted camera, and its farthest hands within arm's reach. This is an
    /// anthropometric prior, not calibration. Null when evidence is insufficient.</summary>
    public static float? FocalLengthFromHandScale(IReadOnlyList<HandScaleSample> samples,int width,int height)
    {
        if(width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(width),"Video dimensions must be positive.");
        var valid=samples?.Where(s=>float.IsFinite(s.DepthPerFocal+s.X+s.Y)&&s.DepthPerFocal>0).ToArray();
        if(valid is null||valid.Length<MinimumHandScaleSamples)return null;
        float Solve(float fraction,float distance)
        {
            var focal=(float)Math.Max(width,height);
            for(var iteration=0;iteration<40;iteration++)
            {
                var distances=valid.Select(s=>
                {
                    var x=(s.X-width*.5f)/focal;var y=(s.Y-height*.5f)/focal;
                    return s.DepthPerFocal*focal*MathF.Sqrt(1+x*x+y*y);
                }).OrderBy(d=>d).ToArray();
                focal*=distance/distances[(int)Math.Round((distances.Length-1)*fraction)];
            }
            return focal;
        }
        var estimate=Math.Min(Solve(.5f,TypicalHandDistance),Solve(.95f,MaximumHandDistance));
        if(!float.IsFinite(estimate))return null;
        // Keep the assumption inside the range the recording-FOV control accepts.
        return Math.Clamp(estimate,width/(2*MathF.Tan(150*MathF.PI/360)),width/(2*MathF.Tan(20*MathF.PI/360)));
    }

    /// <summary>Clip-wide depth multiplier that places the median hand at the typical
    /// first-person distance and the farthest within reach. For a backend whose depth is
    /// consistently biased relative to its viewing rays. Null with insufficient evidence.</summary>
    public static float? DepthGainFromHandDistances(IReadOnlyList<float> distances)
    {
        var valid=distances?.Where(d=>float.IsFinite(d)&&d>0).OrderBy(d=>d).ToArray();
        if(valid is null||valid.Length<MinimumHandScaleSamples)return null;
        var gain=Math.Min(TypicalHandDistance/valid[(valid.Length-1)/2],MaximumHandDistance/valid[(int)Math.Round((valid.Length-1)*.95f)]);
        return float.IsFinite(gain)&&gain>0?gain:null;
    }

    /// <summary>Camera-space wrist at the given depth whose palm joints, offset from
    /// the wrist by the supplied camera-space vectors, project with their centroid on
    /// the detected palm centroid. Depth is untouched; only the lateral position moves.</summary>
    public static System.Numerics.Vector3 AnchorWristToImage(float depth,IReadOnlyList<System.Numerics.Vector3> palmFromWrist,
        System.Numerics.Vector2 palmCentroid,float fx,float fy,float cx,float cy)
    {
        if(palmFromWrist is null||palmFromWrist.Count==0||!(depth>0)||!(fx>0)||!(fy>0))throw new ArgumentException("Invalid wrist anchoring input.");
        double ax=0,ay=0,bx=0,by=0;
        foreach(var offset in palmFromWrist)
        {
            var z=depth+offset.Z;if(!(z>1e-4f))throw new ArgumentException("Palm joint lies behind the camera.");
            ax+=fx/z;ay+=fy/z;bx+=fx*offset.X/z;by+=fy*offset.Y/z;
        }
        var n=palmFromWrist.Count;
        return new((float)((palmCentroid.X-cx-bx/n)/(ax/n)),(float)((palmCentroid.Y-cy-by/n)/(ay/n)),depth);
    }

    public static float InitialHorizontalFov(MotionDocument document,float fallback=75)
    {
        if(document.Space!=MotionSpace.CameraRelative)return fallback;
        var cameras=document.Cameras.Where(c=>c.Id=="video").ToArray();
        if(cameras.Length!=1)return fallback;
        var camera=cameras[0];var k=camera.Intrinsics;
        if(camera.ImageWidth is not int width||camera.ImageHeight is not int height||width<=0||height<=0||
            k is not {Length:9}||k.Any(v=>!float.IsFinite(v))||k[0]<=0||k[4]<=0||
            k[1]!=0||k[3]!=0||k[6]!=0||k[7]!=0||k[8]!=1||k[2]<0||k[2]>width||k[5]<0||k[5]>height||
            camera.Distortion?.Any(v=>!float.IsFinite(v)||v!=0)==true)return fallback;
        // The engine uses a symmetric horizontal FOV. Cover both edges even when
        // the optical centre is off-centre. A distorted camera must be rectified first.
        var halfWidth=Math.Max(k[2],width-k[2]);
        // Widen for wide-angle capture; never unexpectedly narrow the familiar
        // default view using uncertain estimated intrinsics.
        return Math.Clamp(Math.Max(fallback,2*MathF.Atan(halfWidth/k[0])*180/MathF.PI),35,120);
    }
}
