using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Inference;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

/// <param name="UseCameraRotation">Use the camera rotation the capture followed from the
/// background (camera-rotation.json beside the capture) instead of assuming a still camera.</param>
public sealed record BodyRefinementRequest(string Motion,string Models,string Output,bool AssumeStationaryCamera=false,bool UseCameraRotation=false);

/// <summary>Cheap, reversible processing of saved GVHMR predictions. Never loads
/// the image/temporal networks or rewrites the original reconstruction.</summary>
public static class BodyRefinement
{
    public const string Version="gvhmr-stationary-contact-ccd-v8";
    public const string MovingVersion="gvhmr-followed-rotation-contact-ccd-v5";
    /// <summary>Per-frame GVHMR camera angular velocity, and whether the camera only turned in place.</summary>
    public sealed record CameraRotation(float[] AngularVelocity6d,bool RotationOnly);
    public const string CameraRotationFile="camera-rotation.json";
    /// <summary>A foot well above the floor bears no weight, whatever the network says. In slowed footage a foot
    /// in the air moves so little that it read as stationary, and pinning it erased a skateboarder's ollie.
    /// Using the gravity recorded with the capture, foot predictions where the ankle is more than 25 cm or the
    /// toe more than 15 cm above where the feet are lowest are switched off. Channels: left ankle, left foot,
    /// right ankle, right foot, left wrist, right wrist.</summary>
    static float[] GroundedStaticLogits(MotionDocument source,float[] logits)
    {
        var result=logits.ToArray();
        if(source.Cameras.FirstOrDefault(c=>c.Id=="video")?.Up is not {Length:3} upValues)return result;
        var up=Vector3.Normalize(MotionDocument.V(upValues));
        int Role(HumanoidMocap.Mapping.BoneRole role)=>source.Bones.FindIndex(b=>b.Role==role);
        var joints=new[]{Role(HumanoidMocap.Mapping.BoneRole.FootL),Role(HumanoidMocap.Mapping.BoneRole.ToeL),Role(HumanoidMocap.Mapping.BoneRole.FootR),Role(HumanoidMocap.Mapping.BoneRole.ToeR)};
        if(joints.Any(j=>j<0)||result.Length!=source.Frames.Count*6)return result;
        var heights=source.Frames.Select(f=>
        {
            var count=source.Bones.Count;var p=new Vector3[count];var q=new Quaternion[count];
            for(var i=0;i<count;i++)
            {
                var local=MotionDocument.V(f.Positions[i]);var rotation=MotionDocument.Q(f.Rotations[i]);var parent=source.Bones[i].Parent;
                if(parent<0){p[i]=local;q[i]=rotation;}else{p[i]=p[parent]+Vector3.Transform(local,q[parent]);q[i]=Quaternion.Normalize(q[parent]*rotation);}
            }
            return joints.Select(j=>Vector3.Dot(p[j],up)).ToArray();
        }).ToArray();
        var lows=heights.Select(h=>h.Min()).OrderBy(v=>v).ToArray();var floor=lows[lows.Length/20];
        var limits=new[]{.25f,.15f,.25f,.15f};
        for(var f=0;f<heights.Length;f++)for(var c=0;c<4;c++)if(heights[f][c]-floor>limits[c])result[f*6+c]=Math.Min(result[f*6+c],-10);
        return result;
    }
    public static string Run(BodyRefinementRequest request,CancellationToken cancellation,Action<string>? progress=null)
    {
        if(request.AssumeStationaryCamera==request.UseCameraRotation)throw new ArgumentException("Choose either an explicitly stationary camera or the camera rotation followed during capture.");
        cancellation.ThrowIfCancellationRequested();
        var sourceBytes=File.ReadAllBytes(request.Motion);var source=MotionDocument.Parse(sourceBytes);
        if(source.Space!=MotionSpace.CameraRelative||source.Bones.Count<22||!source.ModelVersion.Contains(GvhmrTemporalNetwork.CheckpointSha256,StringComparison.Ordinal))
            throw new ArgumentException("Open the original camera-relative GVHMR reconstruction before refining it.");
        var predictions=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.Motion))!,"raw-predictions.json");
        if(!File.Exists(predictions))throw new FileNotFoundException("Saved GVHMR predictions are missing. Open raw-body.hmotion from its original reconstruction folder.",predictions);
        if(new FileInfo(predictions).Length>128*1024*1024)throw new InvalidDataException("Saved GVHMR predictions exceed the processing budget.");
        var predictionBytes=File.ReadAllBytes(predictions);
        var rawHash=Convert.ToHexString(SHA256.HashData(sourceBytes));var predictionHash=Convert.ToHexString(SHA256.HashData(predictionBytes));
        var moving=request.UseCameraRotation;byte[]? rotationBytes=null;
        if(moving)
        {
            var rotationPath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.Motion))!,CameraRotationFile);
            if(!File.Exists(rotationPath))throw new FileNotFoundException("This capture has no followed camera rotation. Process the video again, or refine it as a stationary camera.",rotationPath);
            rotationBytes=File.ReadAllBytes(rotationPath);predictionHash+="|"+Convert.ToHexString(SHA256.HashData(rotationBytes));
        }
        // The derived document embeds its original's location for reversible editing.
        // Identical captures copied elsewhere must not restore an unrelated old path.
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((moving?MovingVersion:Version)+"|"+Path.GetFullPath(request.Motion)+"|"+rawHash+"|"+predictionHash+"|"+SmplxSkeleton.NeutralSha256)));
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
        var followed=rotationBytes is null?null:JsonSerializer.Deserialize<CameraRotation>(rotationBytes)??throw new InvalidDataException("Empty camera rotation track.");
        var rotation=followed?.AngularVelocity6d??stationary;
        if(rotation.Length!=pose.Frames*6)throw new InvalidDataException("The camera rotation track does not match the motion sample count.");
        if(followed is {RotationOnly:true})
        {
            // A camera turning about a fixed point: the pelvis seen from frame t, turned back by the
            // accumulated rotation, is the pelvis in the first frame's camera axes, as if it had stood still.
            var orientation=Quaternion.Identity;
            for(var t=0;t<pose.Frames;t++)
            {
                cameraTranslation[t]=Vector3.Transform(cameraTranslation[t],Quaternion.Conjugate(orientation));
                orientation=Quaternion.Normalize(GvhmrDecoder.Rotation6D(rotation.AsSpan(t*6,6))*orientation);
            }
        }
        var root=GvhmrDecoder.WorldRoot(pose,rotation);
        var raw=BodyMotionBuilder.WorldRelative(skeleton,pose,root,source,false);
        progress?.Invoke("Correcting stationary-camera root and contact targets");
        // Camera-space pelvis positions only anchor the root when the camera itself stood still.
        var anchored=!moving||followed is {RotationOnly:true};
        var staticLogits=GroundedStaticLogits(source,prediction.StaticConfidenceLogits);
        var correction=GvhmrContactProcessing.CorrectRoot(skeleton,pose,root,staticLogits,anchored?cameraTranslation:null,cancellation);
        progress?.Invoke("Refining source limb contacts");
        var refinedPose=pose with {BodyRotations=GvhmrLimbIk.Solve(skeleton,pose,correction.Root,correction.ContactTargets,cancellation)};
        var refined=BodyMotionBuilder.WorldRelative(skeleton,refinedPose,correction.Root,source,true);
        for(var channel=0;channel<6;channel++)
        {
            int c=channel;
            refined.StationaryJoints.Add(new(){Bone=refined.Bones[GvhmrContactProcessing.ContactJoints[c]].Name,
                Source="GVHMR static-joint head; uncalibrated contact probability, not visibility or 3D confidence",
                Probability=Enumerable.Range(0,pose.Frames).Select(f=>1/(1+MathF.Exp(-staticLogits[f*6+c]))).ToArray()});
        }
        // Finger tracks hang beneath the wrists, unaffected by root or limb refinement, and arrive with the copied source.
        BodyHandTracks.CarryFingerNotes(source,raw);BodyHandTracks.CarryFingerNotes(source,refined);
        // Seated moments found by the capture carry over; the refinement rebuilds the stationary joints.
        foreach(var seated in source.StationaryJoints.Where(s=>s.Source==SeatedDetection.Source))
            foreach(var doc in new[]{raw,refined})if(doc.StationaryJoints.All(s=>s.Source!=SeatedDetection.Source))doc.StationaryJoints.Add(seated);
        raw.OriginalReconstruction=new(){Path=Path.GetFullPath(request.Motion),Sha256=rawHash};
        refined.OriginalReconstruction=new(){Path=Path.GetFullPath(request.Motion),Sha256=rawHash};
        refined.ModelVersion+="; "+(moving?MovingVersion:Version);
        refined.Diagnostics.Add("Original camera-relative reconstruction: "+Path.GetFullPath(request.Motion));
        refined.Corrections.Add(new(){Type=moving?"GVHMR followed-camera-rotation contact refinement":"GVHMR stationary-camera contact refinement",Start=source.Frames[0].Time,End=source.Frames[^1].Time,Settings=new(){["stationaryCameraAssumption"]=moving?0:1,["ccdIterations"]=2}});
        if(moving)
        {
            // The builder's notes describe the still-camera assumption, which was not made here.
            refined.Diagnostics.RemoveAll(d=>d.Contains("stationary-camera",StringComparison.OrdinalIgnoreCase)&&!d.StartsWith("Original camera-relative",StringComparison.Ordinal));
            refined.Diagnostics.Add("Two-iteration limb CCD applied to source limb contacts. Limb transforms affected by IK are labeled GeneratedIk.");
        }
        if(moving)refined.Diagnostics.Add(anchored
            ?"World-relative root from GVHMR's gravity-view rollout with the camera rotation followed from the background. The background showed little parallax, so the camera is treated as turning in place and camera-space pelvis positions, turned back by that rotation, anchor the root as for a still camera. A camera that also travelled would make travel distance wrong."
            :"World-relative root from GVHMR's gravity-view rollout with the camera rotation followed from the background; static-joint root correction without a camera-space anchor, because background parallax shows the camera also travelled. Camera translation and scale are not recovered, so travel distance remains the model's estimate.");
        cancellation.ThrowIfCancellationRequested();
        void Write(string path,string json){File.WriteAllText(path+".partial",json);File.Move(path+".partial",path,true);}
        var refinedJson=refined.ToJson();
        Write(Path.Combine(folder,"raw-world.hmotion"),raw.ToJson());Write(destination,refinedJson);
        Write(Path.Combine(folder,"complete.json"),JsonSerializer.Serialize(new{version=Version,source=Path.GetFullPath(request.Motion),rawHash,predictionHash,
            stationaryCameraAssumption=!moving,frames=pose.Frames,seconds=watch.Elapsed.TotalSeconds,peakWorkerRamBytes=Process.GetCurrentProcess().PeakWorkingSet64,
            neuralInference=false,metricScaleCalibrated=false,outputSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refinedJson)))},MotionDocument.JsonOptions));
        progress?.Invoke((moving?"Moving-camera":"Stationary-camera")+" refinement complete; review contacts before export");return destination;
    }
}
