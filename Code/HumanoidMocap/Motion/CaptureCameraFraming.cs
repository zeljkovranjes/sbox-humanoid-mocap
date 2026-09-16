using System;
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
