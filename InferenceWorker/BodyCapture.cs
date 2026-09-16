using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Editor;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

public sealed record BodyCaptureRequest(string Video,string Models,string Output,double Start,double End,GvhmrDecoder.Box PersonCrop);
public static class BodyCapture
{
    sealed class FrameState
    {
        public double Time { get; set; }
        public float[]? Observations { get; set; }
        public float[]? ImageFeatures { get; set; }
    }
    sealed class State
    {
        public string Key { get; set; }="";
        public string Status { get; set; }="preparing";
        public string? Error { get; set; }
        public List<FrameState> Frames { get; set; }=new();
        public Dictionary<string,double> Seconds { get; set; }=new();
        public long PeakRamBytes { get; set; }
    }
    public static string Run(BodyCaptureRequest request,CancellationToken cancellation,Action<string>? progress=null)
    {
        if(!double.IsFinite(request.Start+request.End)||request.Start<0||request.End<=request.Start)throw new ArgumentException("Select a finite non-empty video range.");
        var metadata=Mp4Metadata.Read(request.Video);var count=metadata.Times.Count(t=>t>=request.Start&&t<request.End);
        if(count<1||count>1800)throw new ArgumentException("Select between one and 1,800 frames.");
        using var video=File.OpenRead(request.Video);var sourceSha=Convert.ToHexString(SHA256.HashData(video));
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{
            version="gvhmr-csharp-camera-raw-v1",sourceSha,request.Start,request.End,request.PersonCrop,
            temporal=GvhmrTemporalNetwork.CheckpointSha256,hmr="2dcf79638109781d1ae5f5c44fee5f55bc83291c210653feead9b7f04fa6f20e",pose="50e33f4077ef2a6bcfd7110c58742b24c5859b7798fb0eedd6d2215e0a8980bc"
        }))));
        var folder=Path.Combine(request.Output,key);Directory.CreateDirectory(folder);var statePath=Path.Combine(folder,"reconstruction.json");
        var state=File.Exists(statePath)?JsonSerializer.Deserialize<State>(File.ReadAllText(statePath))!:new State{Key=key};
        if(state is null||state.Key!=key||state.Frames.Count>count)throw new InvalidDataException("Invalid reconstruction checkpoint.");
        void Save(string status)
        {
            state.Status=status;state.PeakRamBytes=Process.GetCurrentProcess().PeakWorkingSet64;
            File.WriteAllText(statePath+".partial",JsonSerializer.Serialize(state));File.Move(statePath+".partial",statePath,true);progress?.Invoke(status);
        }
        void Visit(Action<DecodedVideoFrame,int> process)
        {
            using var decoder=new WindowsVideoDecoder(request.Video);DecodedVideoFrame? frame;var index=0;
            while((frame=decoder.Read(cancellation))is not null)
            {
                if(frame.Time<request.Start)continue;if(frame.Time>=request.End)break;
                if(index>=1800)throw new InvalidDataException("Decoded range exceeds frame limit.");
                if(index>=state.Frames.Count)state.Frames.Add(new(){Time=frame.Time});
                if(Math.Abs(state.Frames[index].Time-frame.Time)>1e-7)throw new InvalidDataException("Decoded timestamps differ from reconstruction checkpoint.");
                process(frame,index++);
            }
            if(index!=count)throw new InvalidDataException($"Expected {count} selected frames but decoded {index}.");
        }
        try
        {
            state.Error=null;Save("preparing");var watch=Stopwatch.StartNew();
            if(state.Frames.Count<count||state.Frames.Any(f=>f.Observations is null))
            {
                using var pose=new VisionModel(Path.Combine(request.Models,"vitpose/vitpose-h-multi-coco.pth"),VisionModel.Kind.VitPoseHeatmaps,cancellation);
                Visit((frame,index)=>
                {
                    if(state.Frames[index].Observations is not null)return;
                    var crop=VideoCrop.Prepare(frame,request.PersonCrop);var heatmap=VideoCrop.AverageFlippedHeatmaps(pose.Run(crop,cancellation),pose.Run(VideoCrop.FlipImage(crop),cancellation));
                    state.Frames[index].Observations=VideoCrop.DecodeHeatmaps(heatmap,request.PersonCrop);
                    if(index==0||index==count-1)VideoCrop.SaveOverlay(frame,state.Frames[index].Observations!,Path.Combine(folder,$"observations-{index}.png"));
                    Save($"Reconstructed 2D pose {index+1}/{count}");
                });
            }
            state.Seconds["poseThisRun"]=watch.Elapsed.TotalSeconds;GC.Collect();GC.WaitForPendingFinalizers();watch.Restart();
            if(state.Frames.Any(f=>f.ImageFeatures is null))
            {
                using var hmr=new VisionModel(Path.Combine(request.Models,"hmr2/hmr2.ckpt"),VisionModel.Kind.Hmr2Features,cancellation);
                Visit((frame,index)=>
                {
                    if(state.Frames[index].ImageFeatures is not null)return;
                    state.Frames[index].ImageFeatures=hmr.Run(VideoCrop.Prepare(frame,request.PersonCrop),cancellation);Save($"Reconstructed image features {index+1}/{count}");
                });
            }
            state.Seconds["imageFeaturesThisRun"]=watch.Elapsed.TotalSeconds;GC.Collect();GC.WaitForPendingFinalizers();watch.Restart();
            Save("temporal-inference");var camera=new GvhmrDecoder.Camera(MathF.Sqrt(metadata.Width*metadata.Width+metadata.Height*metadata.Height),metadata.Width*.5f,metadata.Height*.5f);
            var boxes=Enumerable.Repeat(request.PersonCrop,count).ToArray();var cameras=Enumerable.Repeat(camera,count).ToArray();
            var identityCondition=Enumerable.Range(0,count).SelectMany(_=>new[]{1f,0,0,0,1,0}).ToArray();
            var conditions=GvhmrDecoder.Prepare(state.Frames.SelectMany(f=>f.Observations!).ToArray(),boxes,cameras,identityCondition);
            var network=new GvhmrTemporalNetwork(Path.Combine(request.Models,"gvhmr/gvhmr_siga24_release.ckpt"),cancellation);
            var prediction=network.Run(count,conditions.Observations,conditions.CliffCamera,conditions.NormalizedAngularVelocity,state.Frames.SelectMany(f=>f.ImageFeatures!).ToArray(),cancellation);
            File.WriteAllText(Path.Combine(folder,"raw-predictions.json"),JsonSerializer.Serialize(prediction));
            var decoded=GvhmrDecoder.Decode(prediction.PredX,count);var translation=GvhmrDecoder.CameraTranslation(prediction.PredCam,boxes,cameras);
            var skeleton=new SmplxSkeleton(Path.Combine(request.Models,"smplx/SMPLX_NEUTRAL.npz"),cancellation);
            var motion=BodyMotionBuilder.CameraRelative(skeleton,decoded,translation,state.Frames.Select(f=>f.Time).ToArray(),Path.GetFileNameWithoutExtension(request.Video),request.Video,sourceSha,metadata.FrameRate,camera);
            var result=Path.Combine(folder,"raw-body.hmotion");File.WriteAllText(result+".partial",motion.ToJson());File.Move(result+".partial",result,true);
            state.Seconds["temporalAndDecodeThisRun"]=watch.Elapsed.TotalSeconds;Save("complete");return result;
        }
        catch(OperationCanceledException){Save("cancelled-resumable");throw;}
        catch(Exception e){state.Error=e.Message;Save("failed-resumable");throw;}
    }
}
