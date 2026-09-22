using System.Collections.Concurrent;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>Decodes a video on a background thread a few frames ahead of the caller, so decoding (about
/// 18 ms per 1080p frame) overlaps the inference that consumes the frames instead of adding to it. The
/// decoder lives entirely on its own thread; frames arrive in presentation order.</summary>
public static class PrefetchedFrames
{
    /// <summary>Frames ahead of the consumer. Each 1080p frame is 8 MB, 4K 33 MB.</summary>
    public const int Depth=3;
    /// <param name="start">Seconds to seek to first (the preceding keyframe); earlier frames may still arrive.</param>
    public static IEnumerable<DecodedVideoFrame> Read(string video,CancellationToken cancellation,double start=0)
    {
        using var queue=new BlockingCollection<DecodedVideoFrame>(Depth);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Exception? failure=null;
        var producer=new Thread(()=>
        {
            try
            {
                using var decoder=new WindowsVideoDecoder(video);DecodedVideoFrame? frame;
                if(start>1)decoder.Seek(start-1);
                while((frame=decoder.Read(stop.Token)) is not null)queue.Add(frame,stop.Token);
            }
            catch(OperationCanceledException){}
            catch(Exception error){failure=error;}
            finally{queue.CompleteAdding();}
        }){IsBackground=true,Name="Video prefetch"};
        producer.Start();
        try
        {
            foreach(var frame in queue.GetConsumingEnumerable(cancellation))yield return frame;
            if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally{stop.Cancel();producer.Join();}
    }
}
