using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HumanoidMocap.Inference;
using HumanoidMocap.Motion;
using HumanoidMocap.Target;

namespace HumanoidMocap.Editor;

public sealed class RawHandSample
{
    public double Time { get; set; }
    public double DecoderTime { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<RawHand> Hands { get; set; } = new();
    public sealed class RawHand
    {
        public string Side { get; set; }
        public float Presence { get; set; }
        public float Handedness { get; set; }
        public float[][] Image { get; set; }
        public float[][] RelativeWorld { get; set; }
        public HandObservation ToObservation()=>new(Side,Presence,Handedness,Image.Select(MotionDocument.V).ToArray(),RelativeWorld.Select(MotionDocument.V).ToArray());
    }
}

/// <summary>Single in-process job. Expensive predictions are cached separately from target fitting.</summary>
public static class HandCaptureJob
{
    static readonly SemaphoreSlim SingleJob=new(1,1);
    public static async Task<string> RunAsync(string video,string modelPath,TargetRig template,double start,double? end,
        bool swapHands,Action<int,int,string> progress,CancellationToken token)
    {
        if(!await SingleJob.WaitAsync(0,token))throw new InvalidOperationException("Another reconstruction job is active.");
        try
        {
            var metadata=Mp4Metadata.Read(video);
            var times=metadata.Times.Where(t=>t>=start && (!end.HasValue||t<end)).ToArray();
            if(times.Length==0||times.Length>1800)throw new ArgumentException("Choose a nonempty range of at most 1800 frames.");
            string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
            var sourceHash=Hash(video);var modelHash=Hash(modelPath);
            var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"managed-hands-v2-mf|{sourceHash}|{modelHash}|{start:R}|{end:R}")))).ToLowerInvariant();
            var fs=global::Editor.FileSystem.ProjectTemporary;var relative="humanoid_mocap/jobs/"+key;fs.CreateDirectory(relative);
            var directory=fs.GetFullPath(relative);var rawPath=Path.Combine(directory,"observations.json");
            var raw=File.Exists(rawPath)?JsonSerializer.Deserialize<List<RawHandSample>>(File.ReadAllText(rawPath),MotionDocument.JsonOptions):new List<RawHandSample>();
            raw??=new();if(raw.Count>times.Length||raw.Where((r,i)=>Math.Abs(r.Time-times[i])>1e-6).Any())throw new FormatException("Cached timestamps differ from this job.");
            var cacheHit=raw.Count==times.Length;var watch=Stopwatch.StartNew();long peak=Process.GetCurrentProcess().WorkingSet64;
            void Save(string state)
            {
                var temp=rawPath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(raw,MotionDocument.JsonOptions));File.Move(temp,rawPath,true);
                File.WriteAllText(Path.Combine(directory,"job.json"),JsonSerializer.Serialize(new{state,sourceHash,modelHash,frames=raw.Count,total=times.Length,
                    elapsedSeconds=watch.Elapsed.TotalSeconds,cacheHit,peakEditorProcessRamBytes=peak,device="CPU",inferenceGpuUsed=false,editorVramMeasured=false,
                    decoder="Windows Media Foundation sequential samples; native presentation timestamps"},MotionDocument.JsonOptions));
            }
            if(raw.Count<times.Length)
            {
                progress(raw.Count,times.Length,"Loading C# hand model");
                var backend=await Task.Run(()=>new ManagedHands(File.ReadAllBytes(modelPath)),token);
                await EditorPipeline.SwitchToMainThread();
                using var reader=await Task.Run(()=>new WindowsVideoDecoder(video),token);
                try
                {
                    for(var i=raw.Count;i<times.Length;i++)
                    {
                        token.ThrowIfCancellationRequested();var sampleTime=times[i];
                        var frame=await Task.Run(()=>
                        {
                            DecodedVideoFrame decoded;
                            do{decoded=reader.Read(token);if(decoded is null)throw new InvalidDataException("Video ended before the selected frame.");}
                            while(decoded.Time<sampleTime-0.00001);
                            if(Math.Abs(decoded.Time-sampleTime)>.001)throw new InvalidDataException("Decoder timestamps disagree with the video sample table.");
                            return decoded;
                        },token);
                        var hands=await Task.Run(()=>backend.Detect(frame.Rgba,frame.Width,frame.Height,token),token);
                        await EditorPipeline.SwitchToMainThread();
                        raw.Add(new RawHandSample{Time=frame.Time,DecoderTime=frame.Time,Width=frame.Width,Height=frame.Height,
                            Hands=hands.Select(h=>new RawHandSample.RawHand{Side=h.Side,Presence=h.Presence,Handedness=h.Handedness,
                                Image=h.ImageLandmarks.Select(MotionDocument.A).ToArray(),RelativeWorld=h.RelativeWorldLandmarks.Select(MotionDocument.A).ToArray()}).ToList()});
                        peak=Math.Max(peak,Process.GetCurrentProcess().WorkingSet64);if(raw.Count%10==0)Save("running");
                        progress(raw.Count,times.Length,$"Reconstructing · {hands.Count} visible hand(s)");
                    }
                    Save("reconstructed");
                }
                catch(OperationCanceledException){Save("cancelled_resumable");throw;}
                catch{Save("failed_resumable");throw;}
            }
            if(!raw.Any(r=>r.Hands.Count>0))throw new InvalidOperationException("No hands detected. Raw observations retained; no captured animation was generated.");
            // A canonical source skeleton keeps target proportions out of reconstruction/cache.
            var canonical=TargetPickers.SboxDefault().Spec.Rig;
            var builder=new HandMotionBuilder(canonical,Path.GetFileNameWithoutExtension(video),video,sourceHash,metadata.FrameRate){SwapHands=swapHands};
            foreach(var frame in raw){token.ThrowIfCancellationRequested();builder.Add(frame.Time,frame.Width,frame.Height,frame.Hands.Select(h=>h.ToObservation()).ToArray());}
            builder.Document.Validate();
            var path=Path.Combine(directory,swapHands?"raw-motion-swapped.hmotion":"raw-motion.hmotion");
            File.WriteAllText(path,builder.Document.ToJson());Save("complete");return path;
        }
        finally{SingleJob.Release();}
    }
}
