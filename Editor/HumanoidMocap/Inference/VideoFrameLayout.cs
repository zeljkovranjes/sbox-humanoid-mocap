using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HumanoidMocap.Inference;

/// <summary>Visible RGB32 aperture and display orientation, independent of coded padding.
/// Rotation undoes the counter-clockwise content rotation reported by Media Foundation.</summary>
internal sealed class VideoFrameLayout
{
    readonly int bufferWidth,bufferHeight,x,y,width,height,rotation;
    public int Width=>rotation is 90 or 270?height:width;
    public int Height=>rotation is 90 or 270?width:height;

    public VideoFrameLayout(int bufferWidth,int bufferHeight,int x,int y,int width,int height,int rotation)
    {
        if(bufferWidth<=0||bufferHeight<=0||(long)bufferWidth*bufferHeight>33_177_600)
            throw new InvalidDataException("Video exceeds the 8K decode limit.");
        if(x<0||y<0||width<=0||height<=0||(long)x+width>bufferWidth||(long)y+height>bufferHeight)
            throw new InvalidDataException("Video display aperture is outside its decoded frame.");
        if(rotation is not (0 or 90 or 180 or 270))throw new InvalidDataException("Unsupported video rotation metadata.");
        this.bufferWidth=bufferWidth;this.bufferHeight=bufferHeight;this.x=x;this.y=y;
        this.width=width;this.height=height;this.rotation=rotation;
    }

    public byte[] CopyRgba(IntPtr data,int length,int stride,CancellationToken token)
    {
        var pitch=Math.Abs((long)stride);
        if(pitch<bufferWidth*4L||pitch*bufferHeight>length)
            throw new InvalidDataException("Decoded buffer is shorter than its frame dimensions or stride.");
        var result=new byte[checked(Width*Height*4)];
        var row=rotation==0?null:new byte[checked(width*4)];
        for(var sourceY=0;sourceY<height;sourceY++)
        {
            token.ThrowIfCancellationRequested();
            var bufferY=stride<0?bufferHeight-1-y-sourceY:y+sourceY;
            var source=IntPtr.Add(data,checked((int)(bufferY*pitch+x*4L)));
            if(rotation==0)Marshal.Copy(source,result,sourceY*width*4,width*4);
            else
            {
                Marshal.Copy(source,row!,0,width*4);
                for(var sourceX=0;sourceX<width;sourceX++)
                {
                    var destination=rotation switch
                    {
                        90=>(sourceX*Width+height-1-sourceY)*4,
                        180=>((height-1-sourceY)*Width+width-1-sourceX)*4,
                        _=>((width-1-sourceX)*Width+sourceY)*4
                    };
                    var offset=sourceX*4;
                    result[destination]=row![offset];result[destination+1]=row[offset+1];
                    result[destination+2]=row[offset+2];
                }
            }
        }
        for(var i=0;i<result.Length;i+=4){(result[i],result[i+2])=(result[i+2],result[i]);result[i+3]=255;}
        return result;
    }
}
