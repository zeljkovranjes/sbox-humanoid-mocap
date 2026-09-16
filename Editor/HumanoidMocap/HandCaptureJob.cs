using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HumanoidMocap.Target;

namespace HumanoidMocap.Editor;

/// <summary>Editor-side preparation and progress dispatch. Neural reconstruction
/// runs in the same separate C# worker used by the other capture backends.</summary>
public static class HandCaptureJob
{
    static readonly SemaphoreSlim SingleJob=new(1,1);
    public static async Task<string> RunAsync(string video,string modelPath,TargetRig template,double start,double? end,
        bool swapHands,Action<int,int,string> progress,CancellationToken token)
    {
        if(!await SingleJob.WaitAsync(0,token))throw new InvalidOperationException("Another reconstruction job is active.");
        try
        {
            // Canonical source proportions are independent of the selected target.
            var fs=global::Editor.FileSystem.ProjectTemporary;fs.CreateDirectory("humanoid_mocap/jobs");
            var directory=fs.GetFullPath("humanoid_mocap/jobs");
            var metadata=await Task.Run(()=>Mp4Metadata.Read(video),token);
            var total=metadata.Times.Count(t=>t>=start&&(!end.HasValue||t<end));
            if(total is <1 or >1800)throw new ArgumentException("Choose a nonempty range of at most 1800 frames.");
            await EditorPipeline.SwitchToMainThread();
            var messages=new ConcurrentQueue<string>();var done=0;
            var job=NativeCapture.LandmarksAsync(video,modelPath,directory,start,end,swapHands,messages.Enqueue,token);
            void Dispatch()
            {
                while(messages.TryDequeue(out var message))
                {
                    if(message.StartsWith("Reconstructed ",StringComparison.Ordinal))
                    {
                        var counts=message.Split(' ')[1].Split('/');
                        if(counts.Length==2&&int.TryParse(counts[0],out var count))done=count;
                    }
                    else if(message=="Reusing cached hand observations")done=total;
                    progress?.Invoke(done,total,message);
                }
            }
            while(!job.IsCompleted)
            {
                await Task.WhenAny(job,Task.Delay(100));
                await EditorPipeline.SwitchToMainThread();Dispatch();
            }
            await EditorPipeline.SwitchToMainThread();Dispatch();
            return await job;
        }
        finally{SingleJob.Release();}
    }
}
