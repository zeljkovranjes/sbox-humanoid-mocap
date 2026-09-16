using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Editor;
using HumanoidMocap.Inference;
using HumanoidMocap.Motion;
using HumanoidMocap.Target;

namespace HumanoidMocap.Worker;

public sealed record LandmarkCaptureRequest(string Video,string Model,string Output,string Template,double Start,double? End,bool SwapHands=false);

/// <summary>Managed MediaPipe inference in the companion process. The observation
/// key and JSON remain compatible with earlier in-editor reconstruction caches.</summary>
public static class LandmarkCapture
{
    sealed class PriorMetrics
    {
        public double ElapsedSeconds { get; set; }
        public double InferenceSeconds { get; set; }
        public long PeakWorkerRamBytes { get; set; }
    }
    public static string Run(LandmarkCaptureRequest request,CancellationToken token=default,Action<string>? progress=null)
    {
        if(!double.IsFinite(request.Start)||request.Start<0||request.End is double end&&(!double.IsFinite(end)||end<=request.Start))
            throw new ArgumentException("Select a nonempty video range.");
        var metadata=Mp4Metadata.Read(request.Video);
        var times=metadata.Times.Where(t=>t>=request.Start&&(!request.End.HasValue||t<request.End)).ToArray();
        if(times.Length is <1 or >1800)throw new ArgumentException("Choose between 1 and 1800 frames; the end time is exclusive.");
        var canonical=TargetRig.SboxDefault(File.ReadAllText(request.Template));
        string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
        token.ThrowIfCancellationRequested();var sourceHash=Hash(request.Video);var modelHash=Hash(request.Model);
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{ManagedHands.ImplementationVersion}|{sourceHash}|{modelHash}|{request.Start:R}|{request.End:R}")))).ToLowerInvariant();
        var directory=Path.Combine(Path.GetFullPath(request.Output),key);Directory.CreateDirectory(directory);
        using var jobLock=new FileStream(Path.Combine(directory,"job.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var rawPath=Path.Combine(directory,"observations.json");
        if(File.Exists(rawPath)&&new FileInfo(rawPath).Length>128*1024*1024)throw new InvalidDataException("Observation cache exceeds its size limit.");
        var raw=File.Exists(rawPath)?JsonSerializer.Deserialize<List<RawHandSample>>(File.ReadAllText(rawPath),MotionDocument.JsonOptions)
            ??throw new InvalidDataException("Empty observation cache."):new List<RawHandSample>();
        if(raw.Count>times.Length||raw.Where((r,i)=>Math.Abs(r.Time-times[i])>1e-6||r.Width<=0||r.Height<=0||r.Hands is null||r.Hands.Count>2).Any())
            throw new InvalidDataException("Cached observations do not match this job.");
        var receiptPath=Path.Combine(directory,"worker-job.json");
        var prior=File.Exists(receiptPath)?JsonSerializer.Deserialize<PriorMetrics>(File.ReadAllText(receiptPath),MotionDocument.JsonOptions)??new():new PriorMetrics();
        if(!double.IsFinite(prior.ElapsedSeconds)||!double.IsFinite(prior.InferenceSeconds)||prior.ElapsedSeconds<0||prior.InferenceSeconds<0||prior.PeakWorkerRamBytes<0)
            throw new InvalidDataException("Invalid cached worker metrics.");
        var cacheHit=raw.Count==times.Length;var clock=Stopwatch.StartNew();var inferenceSeconds=prior.InferenceSeconds;
        void Save(string state,string? error=null)
        {
            Atomic(rawPath,JsonSerializer.Serialize(raw,MotionDocument.JsonOptions));
            Atomic(receiptPath,JsonSerializer.Serialize(new{state,error,sourceHash,modelHash,
                detectorImplementation=ManagedHands.ImplementationVersion,frames=raw.Count,total=times.Length,elapsedSeconds=prior.ElapsedSeconds+clock.Elapsed.TotalSeconds,
                inferenceSeconds,sessionElapsedSeconds=clock.Elapsed.TotalSeconds,sessionInferenceSeconds=inferenceSeconds-prior.InferenceSeconds,
                cacheHit,workerPid=Environment.ProcessId,peakWorkerRamBytes=Math.Max(prior.PeakWorkerRamBytes,Process.GetCurrentProcess().PeakWorkingSet64),
                device="CPU",inferenceGpuUsed=false,decoder="Windows Media Foundation sequential samples; native presentation timestamps"},MotionDocument.JsonOptions));
        }
        try
        {
            if(raw.Count<times.Length)
            {
                progress?.Invoke("Loading MediaPipe in the C# worker");
                var backend=new ManagedHands(File.ReadAllBytes(request.Model));using var reader=new WindowsVideoDecoder(request.Video);
                for(var i=raw.Count;i<times.Length;i++)
                {
                    token.ThrowIfCancellationRequested();DecodedVideoFrame? frame;
                    do{frame=reader.Read(token);if(frame is null)throw new InvalidDataException("Video ended before the selected frame.");}while(frame.Time<times[i]-.00001);
                    if(Math.Abs(frame.Time-times[i])>.001)throw new InvalidDataException("Decoder timestamps disagree with the video sample table.");
                    var previous=raw.Count>0?raw[^1].Hands.Select(h=>h.ToObservation()).ToArray():Array.Empty<HandObservation>();
                    var inference=Stopwatch.StartNew();var hands=backend.DetectTracked(frame.Rgba,frame.Width,frame.Height,previous,token);
                    inferenceSeconds+=inference.Elapsed.TotalSeconds;
                    raw.Add(new(){Time=frame.Time,DecoderTime=frame.Time,Width=frame.Width,Height=frame.Height,
                        Hands=hands.Select(h=>new RawHandSample.RawHand{Side=h.Side,Presence=h.Presence,Handedness=h.Handedness,Tracked=h.Tracked,
                            Image=h.ImageLandmarks.Select(MotionDocument.A).ToArray(),RelativeWorld=h.RelativeWorldLandmarks.Select(MotionDocument.A).ToArray()}).ToList()});
                    if(raw.Count%10==0)Save("running");progress?.Invoke($"Reconstructed {raw.Count}/{times.Length} frames · {hands.Count} hand(s)");
                }
                Save("reconstructed");
            }
            else progress?.Invoke("Reusing cached hand observations");
            if(!raw.Any(r=>r.Hands.Count>0))throw new InvalidDataException("No hands detected. Raw observations retained; no captured animation was generated.");
            var builder=new HandMotionBuilder(canonical,Path.GetFileNameWithoutExtension(request.Video),request.Video,sourceHash,metadata.FrameRate){SwapHands=request.SwapHands};
            builder.Document.ModelVersion+="; "+ManagedHands.ImplementationVersion;
            foreach(var frame in raw){token.ThrowIfCancellationRequested();builder.Add(frame.Time,frame.Width,frame.Height,frame.Hands.Select(h=>h.ToObservation()).ToArray());}
            builder.Document.Validate();token.ThrowIfCancellationRequested();
            var path=Path.Combine(directory,request.SwapHands?"raw-hands-v5-swapped.hmotion":"raw-hands-v5.hmotion");
            Atomic(path,builder.Document.ToJson());Save("complete");return path;
        }
        catch(OperationCanceledException){Save("cancelled_resumable");throw;}
        catch(Exception error){Save("failed_resumable",error.Message);throw;}
    }
    static void Atomic(string path,string content)
    {var temporary=path+".tmp";File.WriteAllText(temporary,content);File.Move(temporary,path,true);}
}
