using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HumanoidMocap.EditorTools.Inference;

/// <summary>A recording's depth, frame by frame, as small 16-bit images over the same view as the picture. Full
/// (sensor) depth is in millimetres; partial (Cinematic mode) depth is the phone's relative disparity scaled to the
/// 16-bit range, larger nearer, with no unit. Zero is no reading. Optionally the phone's own camera pose per frame
/// (Record3D .r3d: ARKit camera-to-world, x right, y up, z back, metres), which is also the axes of a
/// camera-relative motion document.</summary>
public sealed class DepthTrack
{
    public const string FileName="depth.hmdepth";
    const uint Magic=0x50444D48; // "HMDP"
    public DepthKind Kind{get;init;}
    public int Width{get;init;}
    public int Height{get;init;}
    /// <summary>The picture the depth covers, in pixels; depth pixel (x, y) sees picture pixel (x, y) scaled by size.</summary>
    public int PictureWidth{get;init;}
    public int PictureHeight{get;init;}
    public List<double> Times{get;}=new();
    public List<ushort[]> Frames{get;}=new();
    /// <summary>Per frame [qx, qy, qz, qw, tx, ty, tz], or null.</summary>
    public List<float[]>? Poses{get;set;}

    public void Save(string path)
    {
        using var writer=new BinaryWriter(File.Create(path+".partial"));
        writer.Write(Magic);writer.Write(1);writer.Write((int)Kind);writer.Write(Width);writer.Write(Height);writer.Write(PictureWidth);writer.Write(PictureHeight);
        writer.Write(Frames.Count);writer.Write(Poses is {Count:>0}&&Poses.Count==Frames.Count);
        for(var i=0;i<Frames.Count;i++)
        {
            writer.Write(Times[i]);foreach(var v in Frames[i])writer.Write(v);
            if(Poses is {Count:>0}&&Poses.Count==Frames.Count)foreach(var v in Poses[i])writer.Write(v);
        }
        writer.Close();File.Move(path+".partial",path,true);
    }
    public static DepthTrack Load(string path)
    {
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.ReadUInt32()!=Magic||reader.ReadInt32()!=1)throw new InvalidDataException("Not a depth track.");
        var track=new DepthTrack{Kind=(DepthKind)reader.ReadInt32(),Width=reader.ReadInt32(),Height=reader.ReadInt32(),PictureWidth=reader.ReadInt32(),PictureHeight=reader.ReadInt32()};
        var count=reader.ReadInt32();var poses=reader.ReadBoolean();if(poses)track.Poses=new();
        for(var i=0;i<count;i++)
        {
            track.Times.Add(reader.ReadDouble());var frame=new ushort[track.Width*track.Height];for(var k=0;k<frame.Length;k++)frame[k]=reader.ReadUInt16();track.Frames.Add(frame);
            if(poses){var pose=new float[7];for(var k=0;k<7;k++)pose[k]=reader.ReadSingle();track.Poses!.Add(pose);}
        }
        return track;
    }
    /// <summary>The depth frame taken within half a frame of <paramref name="time"/>, or -1.</summary>
    public int FrameAt(double time)
    {
        if(Times.Count==0)return -1;
        var index=Times.BinarySearch(time);if(index<0)index=~index;
        int best=-1;var bestGap=double.MaxValue;
        foreach(var i in new[]{index-1,index})if(i>=0&&i<Times.Count&&Math.Abs(Times[i]-time)<bestGap){best=i;bestGap=Math.Abs(Times[i]-time);}
        var step=Times.Count>1?(Times[^1]-Times[0])/(Times.Count-1):1/30.0;
        return bestGap<=step*.6?best:-1;
    }
    /// <summary>The median reading (see <see cref="Value"/>) within <paramref name="radius"/> depth pixels of a picture point, keeping only readings
    /// near the nearest ones found there (a wrist against a far wall reads the wrist), or null when there is none.</summary>
    public float? Sample(int frame,float x,float y,int radius=2)
    {
        if(frame<0||frame>=Frames.Count||PictureWidth<=0||PictureHeight<=0)return null;
        var cx=(int)MathF.Round(x*Width/PictureWidth-.5f);var cy=(int)MathF.Round(y*Height/PictureHeight-.5f);
        var values=new List<ushort>();var pixels=Frames[frame];
        for(var dy=-radius;dy<=radius;dy++)for(var dx=-radius;dx<=radius;dx++)
        {
            int px=cx+dx,py=cy+dy;if(px<0||py<0||px>=Width||py>=Height)continue;
            var v=pixels[py*Width+px];if(v!=0)values.Add(v);
        }
        if(values.Count==0)return null;
        values.Sort();
        // Full depth: nearest is the smallest; partial disparity: nearest is the largest.
        var near=Kind==DepthKind.Full?values[0]:values[^1];var band=Kind==DepthKind.Full?80:3000;
        var kept=values.Where(v=>Math.Abs(v-near)<=band).ToList();
        return Value(kept[kept.Count/2]);
    }
    /// <summary>Metres for full depth, 0..1 relative disparity for partial.</summary>
    public float Value(ushort raw)=>Kind==DepthKind.Full?raw/1000f:raw/65535f;
}
