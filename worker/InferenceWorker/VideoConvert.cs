using OpenCvSharp;

namespace HumanoidMocap.Worker;

/// <summary>Re-encodes a video Windows cannot decode (iPhone HEVC without the Store extension, 10-bit or 4:4:4
/// H.264) into an H.264 MP4. The frames are read with the FFmpeg decoder bundled in OpenCV's runtime, which
/// applies the phone's rotation, and written with Windows' own H.264 encoder, as the HOT3D import does.
/// Frames are written at the source's average rate; audio is dropped, as capture never uses it.</summary>
public static class VideoConvert
{
    public static void Run(string input,string output,CancellationToken cancellation,Action<string>? progress)
    {
        if(!File.Exists(input))throw new FileNotFoundException("Video not found.",input);
        // OpenCV opens files by narrow name; work in a plain-ASCII folder when either path is not.
        var scratch=new[]{Path.GetDirectoryName(Path.GetFullPath(output))!,Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"sbox-humanoid-mocap")}.First(p=>p.All(c=>c<128));
        Directory.CreateDirectory(scratch);var id=Guid.NewGuid().ToString("N");
        var source=input.All(c=>c<128)?input:Path.Combine(scratch,"hm-source-"+id+Path.GetExtension(input));
        var encoded=Path.Combine(scratch,"hm-converted-"+id+".mp4");
        try
        {
            if(source!=input)File.Copy(input,source,true);
            using var capture=new VideoCapture(source,VideoCaptureAPIs.FFMPEG);
            if(!capture.IsOpened())throw new NotSupportedException("The video could not be opened for conversion.");
            var fps=capture.Fps;if(!(fps is > 1 and < 1000))fps=30;
            var total=capture.FrameCount;using var frame=new Mat();
            if(!capture.Read(frame)||frame.Empty())throw new NotSupportedException("The video has no readable frames.");
            // H.264 needs even dimensions.
            var size=new Size(frame.Width&~1,frame.Height&~1);
            using(var writer=new VideoWriter(encoded,VideoCaptureAPIs.MSMF,FourCC.H264,fps,size))
            {
                if(!writer.IsOpened())throw new NotSupportedException("Windows' H.264 encoder is unavailable.");
                var written=0;var reported=-1;
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(frame.Width!=size.Width||frame.Height!=size.Height){using var cropped=new Mat(frame,new Rect(0,0,size.Width,size.Height));writer.Write(cropped);}
                    else writer.Write(frame);
                    written++;var percent=total>0?(int)(written*100L/total):-1;
                    if(percent>=reported+5){progress?.Invoke(total>0?$"Converting video for this PC · {Math.Min(percent,100)}%":$"Converting video for this PC · {written} frames");reported=percent;}
                }
                while(capture.Read(frame)&&!frame.Empty());
                if(written<2)throw new NotSupportedException("The video is too short to convert.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.Move(encoded,output,true);
        }
        finally
        {
            if(source!=input&&File.Exists(source))File.Delete(source);
            if(File.Exists(encoded))File.Delete(encoded);
        }
    }
}
