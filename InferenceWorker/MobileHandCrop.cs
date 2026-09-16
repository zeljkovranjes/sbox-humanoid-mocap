using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>Square detector crop for MobileHand's RGB [0,1] input. The released
/// model assumes a right hand; left crops and decoded motion are reflected.</summary>
public static class MobileHandCrop
{
    public sealed record Prepared(float[] Image,GvhmrDecoder.Box Box);
    public static Prepared Prepare(DecodedVideoFrame frame,WildHandsCrop.Box hand,bool right)
    {
        if(!new[]{hand.Left,hand.Top,hand.Right,hand.Bottom}.All(float.IsFinite)||hand.Right<=hand.Left||hand.Bottom<=hand.Top)
            throw new ArgumentException("Invalid MobileHand crop.");
        var cx=(hand.Left+hand.Right)/2;var cy=(hand.Top+hand.Bottom)/2;
        // Keep context around the entire hand, including the wrist. This crop
        // policy is ours, not an upstream video-tracking or calibration claim.
        var size=Math.Max(hand.Right-hand.Left,hand.Bottom-hand.Top)*1.5f;
        var box=new GvhmrDecoder.Box(cx,cy,size);var sign=right?1:-1;
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var rgb=new Mat();Cv2.CvtColor(rgba,rgb,ColorConversionCodes.RGBA2RGB);
        using var transform=Cv2.GetAffineTransform(new[]{new Point2f(cx,cy),new Point2f(cx,cy+size/2),new Point2f(cx+sign*size/2,cy)},
            new[]{new Point2f(112,112),new Point2f(112,224),new Point2f(224,112)});
        using var crop=new Mat();Cv2.WarpAffine(rgb,crop,transform,new Size(224,224),InterpolationFlags.Linear,BorderTypes.Constant,Scalar.Black);
        var bytes=new byte[224*224*3];Marshal.Copy(crop.Data,bytes,0,bytes.Length);var image=new float[bytes.Length];
        for(var c=0;c<3;c++)for(var i=0;i<224*224;i++)image[c*224*224+i]=bytes[i*3+c]/255f;
        return new(image,box);
    }
}
