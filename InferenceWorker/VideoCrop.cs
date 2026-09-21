// UDP/DARK decoding adapted from OpenMMLab code in GVHMR.
// Copyright (c) OpenMMLab. All rights reserved. See ../Editor/HumanoidMocap/Inference/Gvhmr.LICENSE.
using System.Runtime.InteropServices;
using HumanoidMocap.Inference;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

public static class VideoCrop
{
    /// <summary>GVHMR get_batch(path_type="np", img_ds=1): full-resolution RGB, conditional Gaussian antialias,
    /// affine square crop to 256, center 192 columns, ImageNet normalization.</summary>
    public static float[] Prepare(DecodedVideoFrame frame,GvhmrDecoder.Box box,string? previewFile=null)
    {
        if(box.Size<=0||!float.IsFinite(box.Size)||!float.IsFinite(box.CenterX)||!float.IsFinite(box.CenterY))throw new ArgumentException("Invalid person crop.");
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var rgb=new Mat();Cv2.CvtColor(rgba,rgb,ColorConversionCodes.RGBA2RGB);
        using var small=rgb.Clone();
        var factor=box.Size/256/2;
        if(factor>1.1)Cv2.GaussianBlur(small,small,new Size(5,5),(factor-1)/2);
        var cx=box.CenterX;var cy=box.CenterY;var half=box.Size*.5f;
        using var affine=Cv2.GetAffineTransform(new[]{new Point2f(cx-half,cy-half),new Point2f(cx+half,cy-half),new Point2f(cx,cy)},
            new[]{new Point2f(0,0),new Point2f(255,0),new Point2f(127.5f,127.5f)});
        using var crop=new Mat();Cv2.WarpAffine(small,crop,affine,new Size(256,256),InterpolationFlags.Linear,BorderTypes.Constant,Scalar.Black);
        if(previewFile is not null){using var bgr=new Mat();Cv2.CvtColor(crop,bgr,ColorConversionCodes.RGB2BGR);WriteImage(previewFile,bgr);}
        var bytes=new byte[256*256*3];Marshal.Copy(crop.Data,bytes,0,bytes.Length);
        float[] mean={.485f,.456f,.406f},std={.229f,.224f,.225f};var normalized=new float[3*256*192];
        for(var c=0;c<3;c++)for(var y=0;y<256;y++)for(var x=0;x<192;x++)
            normalized[(c*256+y)*192+x]=(bytes[(y*256+x+32)*3+c]/255f-mean[c])/std[c];
        return normalized;
    }
    public static float[] FlipImage(float[] pixels)
    {var result=new float[pixels.Length];for(var c=0;c<3;c++)for(var y=0;y<256;y++)for(var x=0;x<192;x++)result[(c*256+y)*192+x]=pixels[(c*256+y)*192+191-x];return result;}
    public static float[] AverageFlippedHeatmaps(float[] original,float[] flipped)
    {
        if(original.Length!=17*64*48||flipped.Length!=original.Length)throw new ArgumentException("Expected COCO17 heatmaps.");
        var result=new float[original.Length];
        for(var joint=0;joint<17;joint++)for(var y=0;y<64;y++)for(var x=0;x<48;x++)
        {var paired=joint==0?0:joint%2==1?joint+1:joint-1;result[(joint*64+y)*48+x]=(original[(joint*64+y)*48+x]+flipped[(paired*64+y)*48+47-x])*.5f;}
        return result;
    }
    /// <summary>GVHMR's COCO UDP/DARK heatmap decoding, including kernel 11 and unblurred maxima.
    /// Output scores are actual backend heatmap maxima, not calibrated visibility probabilities.</summary>
    public static float[] DecodeHeatmaps(float[] heatmaps,GvhmrDecoder.Box box)
    {
        if(heatmaps.Length!=17*64*48||heatmaps.Any(v=>!float.IsFinite(v)))throw new ArgumentException("Invalid COCO heatmaps.");
        var keypoints=new float[51];
        for(var joint=0;joint<17;joint++)
        {
            var map=heatmaps.AsSpan(joint*64*48,64*48).ToArray();var peak=0;for(var i=1;i<map.Length;i++)if(map[i]>map[peak])peak=i;
            var confidence=map[peak];var x=confidence>0?peak%48:-1;var y=confidence>0?peak/48:-1;
            using var matrix=new Mat(64,48,MatType.CV_32FC1);Marshal.Copy(map,0,matrix.Data,map.Length);Cv2.GaussianBlur(matrix,matrix,new Size(11,11),0);Marshal.Copy(matrix.Data,map,0,map.Length);
            for(var i=0;i<map.Length;i++)map[i]=MathF.Log(Math.Clamp(map[i],.001f,50));
            float At(int dx,int dy)=>map[Math.Clamp(y+dy,0,63)*48+Math.Clamp(x+dx,0,47)];
            var center=At(0,0);var xp=At(1,0);var xm=At(-1,0);var yp=At(0,1);var ym=At(0,-1);
            var dx=.5f*(xp-xm);var dy=.5f*(yp-ym);
            double xx=xp-2*center+xm+1.1920928955078125e-7,yy=yp-2*center+ym+1.1920928955078125e-7;
            double xy=.5f*(At(1,1)-xp-yp+center+center-xm-ym+At(-1,-1));var determinant=xx*yy-xy*xy;
            if(Math.Abs(determinant)<1e-20)throw new ArithmeticException("Singular DARK heatmap refinement.");
            var refinedX=x-(yy*dx-xy*dy)/determinant;var refinedY=y-(xx*dy-xy*dx)/determinant;
            keypoints[joint*3]=(float)(refinedX*(box.Size*.75f/47)+box.CenterX-box.Size*.375f);
            keypoints[joint*3+1]=(float)(refinedY*(box.Size/63)+box.CenterY-box.Size*.5f);keypoints[joint*3+2]=confidence;
        }
        return keypoints;
    }
    public static void SaveOverlay(DecodedVideoFrame frame,float[] joints,string path)
    {
        using var rgba=new Mat(frame.Height,frame.Width,MatType.CV_8UC4);Marshal.Copy(frame.Rgba,0,rgba.Data,frame.Rgba.Length);
        using var image=new Mat();Cv2.CvtColor(rgba,image,ColorConversionCodes.RGBA2BGR);
        var edges=new[]{(5,6),(5,7),(7,9),(6,8),(8,10),(5,11),(6,12),(11,12),(11,13),(13,15),(12,14),(14,16),(0,1),(0,2),(1,3),(2,4)};
        Point P(int j)=>new((int)Math.Round(joints[j*3]),(int)Math.Round(joints[j*3+1]));
        foreach(var (a,b) in edges)if(joints[a*3+2]>.3&&joints[b*3+2]>.3)Cv2.Line(image,P(a),P(b),new Scalar(30,240,30),2);
        for(var j=0;j<17;j++)if(joints[j*3+2]>.3)Cv2.Circle(image,P(j),4,new Scalar(0,100,255),-1);
        WriteImage(path,image);
    }
    /// <summary>Encodes in memory and writes through .NET; OpenCV's own writer cannot open non-ASCII paths.</summary>
    static void WriteImage(string path,Mat image)
    {
        var extension=Path.GetExtension(path);if(!Cv2.ImEncode(extension.Length>0?extension:".png",image,out var bytes))throw new IOException("Cannot encode "+Path.GetFileName(path));
        File.WriteAllBytes(path,bytes);
    }
}
