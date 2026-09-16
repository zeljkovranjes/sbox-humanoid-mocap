using System;
using System.Linq;

namespace HumanoidMocap.Motion;

/// <summary>Initial editor view only. Does not estimate calibration or modify motion.</summary>
public static class CaptureCameraFraming
{
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
