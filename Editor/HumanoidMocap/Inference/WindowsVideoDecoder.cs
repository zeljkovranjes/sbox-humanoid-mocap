using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HumanoidMocap.Inference;

public sealed record DecodedVideoFrame(byte[] Rgba,int Width,int Height,double Time);

/// <summary>Direct C# access to Windows Media Foundation. Sequential decoded samples carry
/// their actual presentation timestamps. No subprocess, Python, or frame-seeking approximation.
/// ABI slots and GUIDs are from Windows SDK 10.0.26100 mfobjects.h / mfreadwrite.h.</summary>
public sealed class WindowsVideoDecoder : IDisposable
{
    public const string ImplementationVersion="wmf-visible-oriented-v2";
    const int VideoStream=unchecked((int)0xfffffffc);
    IntPtr reader;
    bool started;
    int stride;
    VideoFrameLayout layout=null!;
    static readonly Guid Major=new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"),Subtype=new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        Video=new("73646976-0000-0010-8000-00aa00389b71"),Rgb32=new("00000016-0000-0010-8000-00aa00389b71"),
        Processing=new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"),Size=new("1652c33d-d6b2-4012-b834-72030849a37d"),
        Stride=new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),Rotation=new("c380465d-2271-428c-9b83-ecea3b4a85c1"),
        MinimumAperture=new("d7388766-18fe-48c6-a177-ee894867c8c4"),GeometricAperture=new("66758743-7e5f-400d-980a-aa8596c85696");
    public WindowsVideoDecoder(string path)
    {
        if(!File.Exists(path))throw new FileNotFoundException("Video not found.",path);
        using var com=new ComScope();IntPtr attributes=IntPtr.Zero,type=IntPtr.Zero;
        try
        {
            Check(MFStartup(0x20070,0));started=true;Check(MFCreateAttributes(out attributes,1));
            Check(Method<SetInt>(attributes,21)(attributes,Processing,1));
            Check(MFCreateSourceReaderFromURL(Path.GetFullPath(path),attributes,out reader));
            Check(Method<SelectStream>(reader,4)(reader,unchecked((int)0xfffffffe),0));
            Check(Method<SelectStream>(reader,4)(reader,VideoStream,1));
            Check(MFCreateMediaType(out type));
            Check(Method<SetGuid>(type,24)(type,Major,Video));Check(Method<SetGuid>(type,24)(type,Subtype,Rgb32));
            Check(Method<SetType>(reader,7)(reader,VideoStream,IntPtr.Zero,type));ReadFormat();
        }
        // MF_E_TOPO_CODEC_NOT_FOUND / unsupported stream: Windows has no decoder for this video's codec.
        catch(COMException error) when(error.HResult is unchecked((int)0xC00D5212) or unchecked((int)0xC00D36C4) or unchecked((int)0xC00D36B4))
        {Dispose();throw new NotSupportedException(MissingCodecMessage(path),error);}
        catch{Dispose();throw;}
        finally{Release(ref type);Release(ref attributes);}
    }
    /// <summary>Names the codec when the container says so. iPhones record HEVC unless set to Most Compatible,
    /// and Windows decodes HEVC only with the HEVC Video Extensions installed.</summary>
    public static string MissingCodecMessage(string path)
    {
        var codec="";
        try
        {
            using var stream=File.OpenRead(path);var window=(int)Math.Min(stream.Length,4*1024*1024);var bytes=new byte[window];
            foreach(var offset in new[]{0L,Math.Max(0,stream.Length-window)})
            {
                stream.Position=offset;var read=stream.Read(bytes,0,window);var text=System.Text.Encoding.Latin1.GetString(bytes,0,read);
                if(text.Contains("hvc1")||text.Contains("hev1")){codec="HEVC (H.265)";break;}
                if(text.Contains("av01")){codec="AV1";break;}
                if(text.Contains("vp09")){codec="VP9";break;}
            }
        }
        catch(IOException){}
        return codec.StartsWith("HEVC",StringComparison.Ordinal)
            ?"This video is HEVC (H.265), which this PC cannot decode. Install \"HEVC Video Extensions\" from the Microsoft Store and upload it again, or record in H.264: on iPhone choose Settings → Camera → Formats → Most Compatible."
            :$"Windows has no decoder for this video{(codec.Length>0?" ("+codec+")":"")}. Convert it to an H.264 MP4 and upload it again.";
    }
    void ReadFormat()
    {
        Check(Method<GetMediaType>(reader,6)(reader,VideoStream,out var type));
        try
        {
            Check(Method<GetLong>(type,8)(type,Size,out var dimensions));var width=(int)(dimensions>>32);var height=(int)(dimensions&uint.MaxValue);
            if(width<=0||height<=0||(long)width*height>33_177_600)throw new InvalidDataException("Video exceeds the 8K decode limit.");
            var hr=Method<GetInt>(type,7)(type,Stride,out stride);if(hr<0)stride=checked(width*4);
            if(Math.Abs((long)stride)<width*4L)throw new InvalidDataException("Invalid video stride.");
            var aperture=ReadAperture(type,MinimumAperture)??ReadAperture(type,GeometricAperture);
            var rotation=0;hr=Method<GetInt>(type,7)(type,Rotation,out var value);
            if(hr>=0)rotation=value;else if(hr!=unchecked((int)0xc00d36e6))Check(hr);
            layout=new(width,height,aperture?.X??0,aperture?.Y??0,aperture?.Width??width,aperture?.Height??height,rotation);
        }
        finally{Release(ref type);}
    }
    static (int X,int Y,int Width,int Height)? ReadAperture(IntPtr type,Guid key)
    {
        var bytes=new byte[16];var hr=Method<GetBlob>(type,15)(type,key,bytes,bytes.Length,out var size);
        if(hr==unchecked((int)0xc00d36e6))return null;Check(hr);
        if(size!=16)throw new InvalidDataException("Invalid video display aperture metadata.");
        if(BitConverter.ToUInt16(bytes,0)!=0||BitConverter.ToUInt16(bytes,4)!=0)
            throw new InvalidDataException("Fractional video aperture offsets are not supported. Export the video with square pixels and an integer crop.");
        return (BitConverter.ToInt16(bytes,2),BitConverter.ToInt16(bytes,6),BitConverter.ToInt32(bytes,8),BitConverter.ToInt32(bytes,12));
    }
    /// <summary>Seek to the preceding keyframe. Read forward to the requested presentation timestamp.</summary>
    public void Seek(double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0)throw new ArgumentOutOfRangeException(nameof(seconds));
        using var com=new ComScope();
        var position=new TimePosition{Type=20,Value=checked((long)(seconds*10_000_000))}; // VT_I8, 100 ns
        Check(Method<SetPosition>(reader,8)(reader,Guid.Empty,position));
    }
    public DecodedVideoFrame Read(CancellationToken token)
    {
        using var com=new ComScope();
        for(var attempts=0;attempts<1000;attempts++)
        {
            token.ThrowIfCancellationRequested();
            Check(Method<ReadSample>(reader,9)(reader,VideoStream,0,out _,out var flags,out var timestamp,out var sample));
            IntPtr buffer=IntPtr.Zero;
            try
            {
                if((flags&1)!=0)throw new IOException("Windows video decoder reported an error.");
                if((flags&0x20)!=0)ReadFormat();
                if(sample==IntPtr.Zero){if((flags&2)!=0)return null;continue;}
                Check(Method<GetBuffer>(sample,41)(sample,out buffer));
                Check(Method<LockBuffer>(buffer,3)(buffer,out var data,out _,out var length));
                try
                {
                    return new(layout.CopyRgba(data,length,stride,token),layout.Width,layout.Height,timestamp/10_000_000d);
                }
                finally{Check(Method<NoArgs>(buffer,4)(buffer));}
            }
            finally{Release(ref buffer);Release(ref sample);}
        }
        throw new InvalidDataException("Video decoder produced too many empty samples.");
    }
    public void Dispose(){Release(ref reader);if(started){started=false;MFShutdown();}}
    static void Release(ref IntPtr pointer){if(pointer!=IntPtr.Zero){Marshal.Release(pointer);pointer=IntPtr.Zero;}}
    static T Method<T>(IntPtr pointer,int slot) where T:Delegate
    {if(pointer==IntPtr.Zero)throw new ObjectDisposedException(nameof(WindowsVideoDecoder));return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer),slot*IntPtr.Size));}
    static void Check(int hr){if(hr<0)Marshal.ThrowExceptionForHR(hr);}
    sealed class ComScope:IDisposable
    {
        readonly bool initialized;
        public ComScope(){var hr=CoInitializeEx(IntPtr.Zero,0);if(hr>=0)initialized=true;else if(hr!=unchecked((int)0x80010106))Check(hr);}
        public void Dispose(){if(initialized)CoUninitialize();}
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetInt(IntPtr self,in Guid key,int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetGuid(IntPtr self,in Guid key,in Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetLong(IntPtr self,in Guid key,out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInt(IntPtr self,in Guid key,out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBlob(IntPtr self,in Guid key,[Out] byte[] bytes,int capacity,out int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SelectStream(IntPtr self,int stream,int selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetType(IntPtr self,int stream,IntPtr reserved,IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMediaType(IntPtr self,int stream,out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReadSample(IntPtr self,int stream,int control,out int actual,out int flags,out long timestamp,out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBuffer(IntPtr self,out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LockBuffer(IntPtr self,out IntPtr data,out int maximum,out int length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgs(IntPtr self);
    [StructLayout(LayoutKind.Explicit,Size=24)] struct TimePosition
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetPosition(IntPtr self,in Guid format,in TimePosition position);
    [DllImport("mfplat.dll",ExactSpelling=true)] static extern int MFStartup(int version,int flags);
    [DllImport("mfplat.dll",ExactSpelling=true)] static extern int MFShutdown();
    [DllImport("mfplat.dll",ExactSpelling=true)] static extern int MFCreateAttributes(out IntPtr attributes,int count);
    [DllImport("mfplat.dll",ExactSpelling=true)] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll",ExactSpelling=true,CharSet=CharSet.Unicode)] static extern int MFCreateSourceReaderFromURL(string url,IntPtr attributes,out IntPtr reader);
    [DllImport("ole32.dll",ExactSpelling=true)] static extern int CoInitializeEx(IntPtr reserved,int flags);
    [DllImport("ole32.dll",ExactSpelling=true)] static extern void CoUninitialize();
}
