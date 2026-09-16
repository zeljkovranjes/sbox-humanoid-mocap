using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>WildHands demo preprocessing: its 840px square image preparation,
/// followed by the 224px dataset transform. Intrinsics and detector boxes follow
/// both transforms; the source footage remains unchanged.</summary>
public static class WildHandsCrop
{
    public const string ImplementationVersion="wildhands-demo840-v1";
    public sealed record Box(float Left,float Top,float Right,float Bottom);
    public sealed record Camera(float Fx,float Fy,float Cx,float Cy,bool Calibrated);
    public sealed record Prepared(float[] Image,float[] Right,float[] Left,float[] RightCenter,
        float[] RightCorners,float[] LeftCenter,float[] LeftCorners,Camera PatchCamera);
    public static Prepared Prepare(DecodedVideoFrame frame,Box? right,Box? left,Camera camera)
    {
        if(frame.Width<1||frame.Height<1||frame.Rgba.Length!=checked(frame.Width*frame.Height*4))throw new ArgumentException("Invalid video frame.");
        if(camera.Fx<=0||camera.Fy<=0||!new[]{camera.Fx,camera.Fy,camera.Cx,camera.Cy}.All(float.IsFinite))throw new ArgumentException("Invalid camera intrinsics.");
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var rgb=new Mat();Cv2.CvtColor(rgba,rgb,ColorConversionCodes.RGBA2RGB);
        // demo.py prepares this image before dataset.py's Gaussian blur. Skipping
        // the first resize changes the blur and therefore the network input.
        const int size=840;var extent=Math.Max(frame.Width,frame.Height);var demoScale=size/(float)extent;
        float DemoX(float x)=>(x-frame.Width*.5f)*demoScale+size*.5f;
        float DemoY(float y)=>(y-frame.Height*.5f)*demoScale+size*.5f;
        Box? ResizeBox(Box? box)=>box is null?null:new(DemoX(box.Left),DemoY(box.Top),DemoX(box.Right),DemoY(box.Bottom));
        right=ResizeBox(right);left=ResizeBox(left);
        camera=new(camera.Fx*demoScale,camera.Fy*demoScale,DemoX(camera.Cx),DemoY(camera.Cy),camera.Calibrated);
        using var demo=Crop(rgb,frame.Width*.5f,frame.Height*.5f,extent,size);
        using var blurred=new Mat();Cv2.GaussianBlur(demo,blurred,new Size(5,5),8);
        var scale=224f/size;
        using var square=Crop(blurred,size*.5f,size*.5f,size);
        using var global=new Mat();square.ConvertTo(global,MatType.CV_32FC3,1d/255);
        var patchCamera=new Camera(camera.Fx*scale,camera.Fy*scale,
            (camera.Cx-size*.5f)*scale+112,(camera.Cy-size*.5f)*scale+112,camera.Calibrated);
        (float[] Image,float[] Center,float[] Corners) Hand(Box? box)
        {
            float cx=112,cy=112,extent=224;int x0=0,y0=0,x1=223,y1=223;
            if(box is not null)
            {
                if(!new[]{box.Left,box.Top,box.Right,box.Bottom}.All(float.IsFinite)||box.Right<=box.Left||box.Bottom<=box.Top)
                    throw new ArgumentException("Invalid hand crop.");
                int X(float x)=>Math.Clamp((int)(scale*(x-size*.5f)+112)+1,0,223);
                int Y(float y)=>Math.Clamp((int)(scale*(y-size*.5f)+112)+1,0,223);
                x0=X(box.Left);y0=Y(box.Top);x1=X(box.Right);y1=Y(box.Bottom);
                cx=(x0+x1)/2;cy=(y0+y1)/2;extent=Math.Max(x1-x0,y1-y0)*1.75f;
                if(extent<1)throw new ArgumentException("Hand crop collapses outside the image.");
                var half=MathF.Floor(extent/2);
                x0=Math.Clamp((int)(cx-half),0,223);x1=Math.Clamp((int)(cx+half),0,223);
                y0=Math.Clamp((int)(cy-half),0,223);y1=Math.Clamp((int)(cy+half),0,223);
            }
            using var crop=Crop(global,cx,cy,extent);
            float AngleX(float x)=>MathF.Atan2(x-patchCamera.Cx,patchCamera.Fx);
            float AngleY(float y)=>MathF.Atan2(y-patchCamera.Cy,patchCamera.Fy);
            return(Normalize(crop),new[]{AngleX((x0+x1)/2f),AngleY((y0+y1)/2f)},
                new[]{AngleX(x0),AngleY(y0),AngleX(x0),AngleY(y1),AngleX(x1),AngleY(y0),AngleX(x1),AngleY(y1)});
        }
        var r=Hand(right);var l=Hand(left);
        return new(Normalize(global),r.Image,l.Image,r.Center,r.Corners,l.Center,l.Corners,patchCamera);
    }
    static Mat Crop(Mat image,float cx,float cy,float size,int resolution=224)
    {
        using var affine=Cv2.GetAffineTransform(new[]{new Point2f(cx,cy),new Point2f(cx,cy+size/2),new Point2f(cx+size/2,cy)},
            new[]{new Point2f(resolution*.5f,resolution*.5f),new Point2f(resolution*.5f,resolution),new Point2f(resolution,resolution*.5f)});
        using var affineFloat=new Mat();affine.ConvertTo(affineFloat,MatType.CV_32FC1);
        var result=new Mat();
        try{Cv2.WarpAffine(image,result,affineFloat,new Size(resolution,resolution),InterpolationFlags.Cubic,BorderTypes.Constant,Scalar.Black);return result;}
        catch{result.Dispose();throw;}
    }
    static float[] Normalize(Mat image)
    {
        var rgb=new float[224*224*3];Marshal.Copy(image.Data,rgb,0,rgb.Length);
        float[] mean={.485f,.456f,.406f},std={.229f,.224f,.225f};var result=new float[rgb.Length];
        for(var c=0;c<3;c++)for(var pixel=0;pixel<224*224;pixel++)result[c*224*224+pixel]=(Math.Clamp(rgb[pixel*3+c],0,1)-mean[c])/std[c];
        return result;
    }
}
