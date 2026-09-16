using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>Released WiLoR ViTDetDataset crop geometry, with source-image reflection
/// for left hands and the 192:256 bounding-box aspect ratio.</summary>
public static class WilorCrop
{
    public sealed record Prepared(float[] Image,GvhmrDecoder.Box Box,bool Right);
    public static Prepared Prepare(DecodedVideoFrame frame,WildHandsCrop.Box hand,bool right,float expansion=2)
    {
        if(!new[]{hand.Left,hand.Top,hand.Right,hand.Bottom,expansion}.All(float.IsFinite)||hand.Right<=hand.Left||hand.Bottom<=hand.Top||expansion<=0)
            throw new ArgumentException("Invalid WiLoR hand crop.");
        var cx=(hand.Left+hand.Right)/2;var cy=(hand.Top+hand.Bottom)/2;
        var size=Math.Max((hand.Right-hand.Left)*4/3,hand.Bottom-hand.Top)*expansion;
        var sourceBox=new GvhmrDecoder.Box(cx,cy,size);
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var rgb=new Mat();Cv2.CvtColor(rgba,rgb,ColorConversionCodes.RGBA2RGB);
        using var image=rgb.Clone();var factor=size/512;
        if(factor>1.1f)
        {
            // skimage gaussian preserves range, promotes uint8 to double, and uses
            // nearest borders with a four-sigma truncated kernel.
            image.ConvertTo(image,MatType.CV_64FC3);var sigma=(factor-1)/2;
            var radius=(int)(4*sigma+.5f);
            Cv2.GaussianBlur(image,image,new Size(radius*2+1,radius*2+1),sigma,sigma,BorderTypes.Replicate);
        }
        if(!right){Cv2.Flip(image,image,FlipMode.Y);cx=frame.Width-cx-1;}
        using var transform=Cv2.GetAffineTransform(new[]{new Point2f(cx,cy),new Point2f(cx,cy+size/2),new Point2f(cx+size/2,cy)},
            new[]{new Point2f(128,128),new Point2f(128,256),new Point2f(256,128)});
        using var crop=new Mat();Cv2.WarpAffine(image,crop,transform,new Size(256,256),InterpolationFlags.Linear,BorderTypes.Constant,Scalar.Black);
        using var floating=new Mat();crop.ConvertTo(floating,MatType.CV_32FC3);
        var pixels=new float[256*256*3];Marshal.Copy(floating.Data,pixels,0,pixels.Length);
        float[] mean={.485f,.456f,.406f},std={.229f,.224f,.225f};var result=new float[3*256*192];
        for(var c=0;c<3;c++)for(var y=0;y<256;y++)for(var x=0;x<192;x++)
            result[(c*256+y)*192+x]=(pixels[(y*256+x+32)*3+c]/255-mean[c])/std[c];
        return new(result,sourceBox,right);
    }
}
