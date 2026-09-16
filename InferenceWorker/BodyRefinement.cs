using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Inference;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

public sealed record BodyRefinementRequest(string Motion,string Models,string Output,bool AssumeStationaryCamera=false);

/// <summary>Cheap, reversible processing of saved GVHMR predictions. Never loads
/// the image/temporal networks or rewrites the original reconstruction.</summary>
public static class BodyRefinement
{
    public const string Version="gvhmr-stationary-contact-ccd-v5";
    public static string Run(BodyRefinementRequest request,CancellationToken cancellation,Action<string>? progress=null)
    {
        if(!request.AssumeStationaryCamera)throw new ArgumentException("This refinement requires an explicitly stationary camera. Moving-camera recovery is not implemented.");
        cancellation.ThrowIfCancellationRequested();
        var sourceBytes=File.ReadAllBytes(request.Motion);var source=MotionDocument.Parse(sourceBytes);
        if(source.Space!=MotionSpace.CameraRelative||source.Bones.Count!=22||!source.ModelVersion.Contains(GvhmrTemporalNetwork.CheckpointSha256,StringComparison.Ordinal))
            throw new ArgumentException("Open the original camera-relative GVHMR reconstruction before refining it.");
        var predictions=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.Motion))!,"raw-predictions.json");
        if(!File.Exists(predictions))throw new FileNotFoundException("Saved GVHMR predictions are missing. Open raw-body.hmotion from its original reconstruction folder.",predictions);
        if(new FileInfo(predictions).Length>128*1024*1024)throw new InvalidDataException("Saved GVHMR predictions exceed the processing budget.");
        var predictionBytes=File.ReadAllBytes(predictions);
        var rawHash=Convert.ToHexString(SHA256.HashData(sourceBytes));var predictionHash=Convert.ToHexString(SHA256.HashData(predictionBytes));
        // The derived document embeds its original's location for reversible editing.
        // Identical captures copied elsewhere must not restore an unrelated old path.
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Version+"|"+Path.GetFullPath(request.Motion)+"|"+rawHash+"|"+predictionHash+"|"+SmplxSkeleton.NeutralSha256)));
        var folder=Path.Combine(Path.GetFullPath(request.Output),key);Directory.CreateDirectory(folder);
        using var jobLock=new FileStream(Path.Combine(folder,"job.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var destination=Path.Combine(folder,"contact-body.hmotion");
        if(File.Exists(destination)&&File.Exists(Path.Combine(folder,"complete.json")))
        {
            var bytes=File.ReadAllBytes(destination);using var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"complete.json")));
            if(!receipt.RootElement.TryGetProperty("outputSha256",out var digest)||digest.GetString()!=Convert.ToHexString(SHA256.HashData(bytes)))
                throw new InvalidDataException("The cached refinement changed. Choose a new output folder to rebuild it; the original capture is preserved.");
            MotionDocument.Parse(bytes);progress?.Invoke("Reusing cached stationary-camera refinement");return destination;
        }
        var watch=Stopwatch.StartNew();progress?.Invoke("Reading saved body predictions; no neural inference");
        var prediction=JsonSerializer.Deserialize<GvhmrTemporalNetwork.Output>(predictionBytes)??throw new InvalidDataException("Empty GVHMR prediction cache.");
        if(prediction.Frames!=source.Frames.Count)throw new InvalidDataException("Saved predictions do not match the motion sample count.");
        var pose=GvhmrDecoder.Decode(prediction.PredX,prediction.Frames);
        var skeletonPath=Path.Combine(request.Models,"smplx/SMPLX_NEUTRAL.npz");
        if(!File.Exists(skeletonPath))throw new FileNotFoundException("The SMPL-X neutral model used by body capture is missing. Restore SMPLX_NEUTRAL.npz in the configured models/smplx folder.",skeletonPath);
        var skeleton=new SmplxSkeleton(skeletonPath,cancellation);
        var rest=skeleton.RestPose(pose.Betas.AsSpan(0,10));
        var cameraTranslation=source.Frames.Select(f=>Vector3.Transform(MotionDocument.V(f.Positions[0]),Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI))-rest[0]).ToArray();
        var stationary=Enumerable.Range(0,pose.Frames).SelectMany(_=>new[]{1f,0,0,0,1,0}).ToArray();
        var root=GvhmrDecoder.WorldRoot(pose,stationary);
        var raw=BodyMotionBuilder.WorldRelative(skeleton,pose,root,source,false);
        progress?.Invoke("Correcting stationary-camera root and contact targets");
        var correction=GvhmrContactProcessing.CorrectRoot(skeleton,pose,root,prediction.StaticConfidenceLogits,cameraTranslation,cancellation);
        progress?.Invoke("Refining source limb contacts");
        var refinedPose=pose with {BodyRotations=GvhmrLimbIk.Solve(skeleton,pose,correction.Root,correction.ContactTargets,cancellation)};
        var refined=BodyMotionBuilder.WorldRelative(skeleton,refinedPose,correction.Root,source,true);
        for(var channel=0;channel<6;channel++)
        {
            int c=channel;
            refined.StationaryJoints.Add(new(){Bone=refined.Bones[GvhmrContactProcessing.ContactJoints[c]].Name,
                Source="GVHMR static-joint head; uncalibrated contact probability, not visibility or 3D confidence",
                Probability=Enumerable.Range(0,pose.Frames).Select(f=>1/(1+MathF.Exp(-prediction.StaticConfidenceLogits[f*6+c]))).ToArray()});
        }
        raw.OriginalReconstruction=new(){Path=Path.GetFullPath(request.Motion),Sha256=rawHash};
        refined.OriginalReconstruction=new(){Path=Path.GetFullPath(request.Motion),Sha256=rawHash};
        refined.ModelVersion+="; "+Version;
        refined.Diagnostics.Add("Original camera-relative reconstruction: "+Path.GetFullPath(request.Motion));
        refined.Corrections.Add(new(){Type="GVHMR stationary-camera contact refinement",Start=source.Frames[0].Time,End=source.Frames[^1].Time,Settings=new(){["stationaryCameraAssumption"]=1,["ccdIterations"]=2}});
        cancellation.ThrowIfCancellationRequested();
        void Write(string path,string json){File.WriteAllText(path+".partial",json);File.Move(path+".partial",path,true);}
        var refinedJson=refined.ToJson();
        Write(Path.Combine(folder,"raw-world.hmotion"),raw.ToJson());Write(destination,refinedJson);
        Write(Path.Combine(folder,"complete.json"),JsonSerializer.Serialize(new{version=Version,source=Path.GetFullPath(request.Motion),rawHash,predictionHash,
            stationaryCameraAssumption=true,frames=pose.Frames,seconds=watch.Elapsed.TotalSeconds,peakWorkerRamBytes=Process.GetCurrentProcess().PeakWorkingSet64,
            neuralInference=false,metricScaleCalibrated=false,outputSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refinedJson)))},MotionDocument.JsonOptions));
        progress?.Invoke("Stationary-camera refinement complete; review contacts before export");return destination;
    }
}
