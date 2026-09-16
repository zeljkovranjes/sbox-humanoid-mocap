// Copyright (c) Meta Platforms, Inc. and affiliates.
// UmeTrack FK and Fisheye624 equations adapted under Apache-2.0 from
// hand_tracking_toolkit, revision 950d64f7e8d2ba1fd38cd2ceede6608a8fa7f5aa.
using System.Diagnostics;
using System.Formats.Tar;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HumanoidMocap.Mapping;
using HumanoidMocap.Inference;
using HumanoidMocap.Maths;
using HumanoidMocap.Motion;
using OpenCvSharp;

namespace HumanoidMocap.Worker;

public sealed record Hot3dImportRequest(string Archive,string Output);

/// <summary>Imports published HOT3D-Clips annotations. Does not run reconstruction.
/// Only annotated Aria RGB clips with UmeTrack poses are supported.</summary>
public static class Hot3dClipImport
{
    public const string Version="hot3d-aria-umetrack-v1";
    const int Size=640;
    static readonly Quaternion VirtualFromCamera=Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/6)*Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2);
    static readonly Quaternion CaptureFromVirtual=Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI);
    static readonly Quaternion CaptureFromCamera=CaptureFromVirtual*VirtualFromCamera;
    static readonly float Focal=Size*.5f/MathF.Tan(MathF.PI/3);
    const float Center=(Size-1)*.5f;
    sealed record Frame(int Index,long Timestamp,JsonElement Camera,JsonElement Hands,JsonElement Objects,byte[] Image);
    sealed record Archive(JsonElement Shape,List<Frame> Frames);

    public static string Run(Hot3dImportRequest request,CancellationToken token=default,Action<string>? progress=null)
    {
        var clock=Stopwatch.StartNew();var archivePath=Path.GetFullPath(request.Archive);
        if(new FileInfo(archivePath).Length>512L*1024*1024)throw new InvalidDataException("HOT3D clip exceeds 512 MiB.");
        token.ThrowIfCancellationRequested();string hash;
        using(var stream=File.OpenRead(archivePath))hash=Convert.ToHexString(SHA256.HashData(stream));
        var folder=Path.Combine(Path.GetFullPath(request.Output),Version+"-"+hash);Directory.CreateDirectory(folder);
        using var jobLock=new FileStream(Path.Combine(folder,"job.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var motionPath=Path.Combine(folder,"annotations.hmotion");var videoPath=Path.Combine(folder,"rectified.mp4");
        var receiptPath=Path.Combine(folder,"import.json");var annotationsPath=Path.Combine(folder,"source-annotations.json");
        if(File.Exists(receiptPath))
        {
            if(!File.Exists(motionPath)||!File.Exists(videoPath)||!File.Exists(annotationsPath))
                throw new InvalidDataException("A completed HOT3D import is missing outputs. Restore them or choose a new output folder; remaining files were preserved.");
            using var receipt=JsonDocument.Parse(File.ReadAllText(receiptPath));
            if(receipt.RootElement.GetProperty("archiveSha256").GetString()==hash&&
                receipt.RootElement.GetProperty("motionSha256").GetString()==Hash(motionPath)&&
                receipt.RootElement.GetProperty("videoSha256").GetString()==Hash(videoPath)&&File.Exists(annotationsPath)&&
                receipt.RootElement.TryGetProperty("annotationsSha256",out var annotationHash)&&annotationHash.GetString()==Hash(annotationsPath))
            {MotionDocument.Parse(File.ReadAllBytes(motionPath));progress?.Invoke("Reusing imported HOT3D annotations and review video");return motionPath;}
            throw new InvalidDataException("Imported HOT3D files changed. Choose a new output folder; existing files were preserved.");
        }
        var archive=Read(archivePath,token);var frames=archive.Frames;var first=frames[0].Timestamp;
        var times=frames.Select(f=>(f.Timestamp-first)/1e9).ToArray();var fps=(frames.Count-1)/times[^1];
        if(fps is <1 or >120||times.Where((t,i)=>Math.Abs(t-i/fps)>.25/fps).Any())
            throw new NotSupportedException("This clip needs variable-rate video encoding. Its annotations were not resampled.");
        var doc=BuildMotion(archive,times,fps,videoPath,archivePath,hash,token);doc.Validate();
        var temporary=Path.Combine(folder,"rectified.partial.mp4");
        Cv2.SetNumThreads(4);
        try
        {
            using(var writer=new VideoWriter(temporary,VideoCaptureAPIs.MSMF,FourCC.H264,fps,new Size(Size,Size)))
            {
                if(!writer.IsOpened())throw new InvalidOperationException("H.264 review video encoder is unavailable.");
                using var mapX=new Mat(Size,Size,MatType.CV_32FC1);using var mapY=new Mat(Size,Size,MatType.CV_32FC1);
                for(var i=0;i<frames.Count;i++)
                {
                    token.ThrowIfCancellationRequested();var frame=frames[i];
                    RectificationMap(frame.Camera.GetProperty("calibration"),mapX,mapY,token);
                    var calibration=frame.Camera.GetProperty("calibration");
                    ValidateJpeg(frame.Image,calibration);
                    using var original=Cv2.ImDecode(frame.Image,ImreadModes.Color);using var rectified=new Mat();
                    if(original.Empty()||original.Width!=calibration.GetProperty("image_width").GetInt32()||original.Height!=calibration.GetProperty("image_height").GetInt32())
                        throw new InvalidDataException("HOT3D JPEG dimensions disagree with calibration.");
                    Cv2.Remap(original,rectified,mapX,mapY,InterpolationFlags.Linear,BorderTypes.Constant,Scalar.Black);writer.Write(rectified);
                    progress?.Invoke($"Importing HOT3D annotations and review video {i+1}/{frames.Count}");
                }
            }
            double maximumPreviewTimestampError=0;
            using(var decoder=new WindowsVideoDecoder(temporary))
            {
                var count=0;
                while(decoder.Read(token) is {} decoded)
                {
                    if(count>=frames.Count||decoded.Width!=Size||decoded.Height!=Size||Math.Abs(decoded.Time-times[count])>.25/fps)
                        throw new InvalidDataException("Encoded review frame dimensions or timestamps disagree with annotations.");
                    maximumPreviewTimestampError=Math.Max(maximumPreviewTimestampError,Math.Abs(decoded.Time-times[count]));count++;
                }
                if(count!=frames.Count)throw new InvalidDataException("Encoded review video lost frames.");
            }
            token.ThrowIfCancellationRequested();doc.Validate();
            // These are derived job outputs; the input archive and annotations remain untouched.
            File.Move(temporary,videoPath,true);
            File.WriteAllText(motionPath+".partial",doc.ToJson());File.Move(motionPath+".partial",motionPath,true);
            File.WriteAllText(annotationsPath+".partial",JsonSerializer.Serialize(new{
                archiveSha256=hash,originTimestampNs=first,shape=archive.Shape,frames=frames.Select(f=>new{f.Index,f.Timestamp,f.Camera,f.Hands,f.Objects})},MotionDocument.JsonOptions));
            File.Move(annotationsPath+".partial",annotationsPath,true);
            File.WriteAllText(receiptPath+".partial",JsonSerializer.Serialize(new{implementation=Version,archive=archivePath,archiveSha256=hash,
                motionSha256=Hash(motionPath),videoSha256=Hash(videoPath),annotationsSha256=Hash(annotationsPath),frames=frames.Count,fps,duration=times[^1],
                width=Size,height=Size,focal=Focal,center=Center,virtualFromCamera=MotionDocument.A(VirtualFromCamera),
                maximumReviewTimingErrorSeconds=maximumPreviewTimestampError,
                elapsedSeconds=clock.Elapsed.TotalSeconds,peakWorkerRamBytes=Process.GetCurrentProcess().PeakWorkingSet64,
                inferenceRun=false,source="Published HOT3D UmeTrack and object annotations",originalAnnotations="source-annotations.json"},MotionDocument.JsonOptions));
            File.Move(receiptPath+".partial",receiptPath,true);return motionPath;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    internal static void ValidateJpeg(byte[] bytes,JsonElement calibration)
    {
        var width=calibration.GetProperty("image_width").GetInt32();var height=calibration.GetProperty("image_height").GetInt32();
        if(width is <1 or >4096||height is <1 or >4096||bytes.Length<4||bytes[0]!=255||bytes[1]!=216)
            throw new InvalidDataException("Invalid or oversized HOT3D JPEG.");
        for(var p=2;p+3<bytes.Length;)
        {
            if(bytes[p++]!=255)break;
            while(p<bytes.Length&&bytes[p]==255)p++;
            if(p+2>=bytes.Length)break;var marker=bytes[p++];
            if(marker is 0xD8 or 0xD9 or 0xDA)break;
            var length=(bytes[p]<<8)|bytes[p+1];if(length<2||p+length>bytes.Length)break;
            if(marker is >=0xC0 and <=0xCF&&marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if(length<8||((bytes[p+3]<<8)|bytes[p+4])!=height||((bytes[p+5]<<8)|bytes[p+6])!=width)break;
                return;
            }
            p+=length;
        }
        throw new InvalidDataException("HOT3D JPEG header does not match bounded calibration dimensions.");
    }
    static Archive Read(string path,CancellationToken token)
    {
        var entries=new Dictionary<string,byte[]>();long total=0;
        using(var stream=File.OpenRead(path))using(var tar=new TarReader(stream))
        {
            TarEntry? entry;
            while((entry=tar.GetNextEntry()) is not null)
            {
                token.ThrowIfCancellationRequested();
                if(!Regex.IsMatch(entry.Name,@"^(__hand_shapes\.json__|\d{6}\.(cameras\.json|hands\.json|objects\.json|info\.json|image_214-1\.jpg))$"))continue;
                if(entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)||entry.DataStream is null||entry.Length>8*1024*1024||entries.Count>=10801)
                    throw new InvalidDataException("Invalid or oversized HOT3D entry.");
                total+=entry.Length;if(total>256L*1024*1024)throw new InvalidDataException("HOT3D selected data exceeds 256 MiB.");
                using var memory=new MemoryStream();entry.DataStream.CopyTo(memory);
                if(memory.Length!=entry.Length||!entries.TryAdd(entry.Name,memory.ToArray()))throw new InvalidDataException("Truncated or duplicate HOT3D entry.");
            }
        }
        JsonElement Json(string name)
        {if(!entries.TryGetValue(name,out var bytes))throw new InvalidDataException("Missing HOT3D annotation: "+name+". Use an annotated Aria training clip.");using var document=JsonDocument.Parse(bytes);return document.RootElement.Clone();}
        var shape=Json("__hand_shapes.json__").GetProperty("umetrack");var frames=new List<Frame>();
        foreach(var name in entries.Keys.Where(n=>n.EndsWith(".info.json",StringComparison.Ordinal)).OrderBy(n=>n,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();var prefix=name[..6];var info=Json(name);var timestamp=info.GetProperty("ref_timestamp_ns").GetInt64();
            if(info.GetProperty("device").GetString()!="Aria"||info.GetProperty("image_timestamps_ns").GetProperty("214-1").GetInt64()!=timestamp)
                throw new NotSupportedException("HOT3D import requires synchronized annotated Aria RGB frames.");
            var camera=Json(prefix+".cameras.json").GetProperty("214-1");
            if(!entries.TryGetValue(prefix+".image_214-1.jpg",out var image))throw new InvalidDataException("Missing HOT3D RGB image.");
            frames.Add(new(int.Parse(prefix),timestamp,camera,Json(prefix+".hands.json"),Json(prefix+".objects.json"),image));
        }
        if(frames.Count is <2 or >1800||frames.Zip(frames.Skip(1),(a,b)=>b.Timestamp<=a.Timestamp||b.Index!=a.Index+1).Any(invalid=>invalid))
            throw new InvalidDataException("Expected 2–1800 consecutive, strictly increasing HOT3D frames.");
        return new(shape,frames);
    }

    static Vector3 V(JsonElement e,float scale=1)=>new(e[0].GetSingle()*scale,e[1].GetSingle()*scale,e[2].GetSingle()*scale);
    static XForm Transform(JsonElement e)
    {
        var q=e.GetProperty("quaternion_wxyz");var rotation=new Quaternion(q[1].GetSingle(),q[2].GetSingle(),q[3].GetSingle(),q[0].GetSingle());
        if(!float.IsFinite(rotation.LengthSquared())||Math.Abs(rotation.LengthSquared()-1)>.01f)throw new InvalidDataException("Invalid HOT3D orientation.");
        return new(V(e.GetProperty("translation_xyz")),Quaternion.Normalize(rotation));
    }
    static XForm Mirror(XForm x)=>new(new(-x.Pos.X,x.Pos.Y,x.Pos.Z),new(x.Rot.X,-x.Rot.Y,-x.Rot.Z,x.Rot.W));

    static MotionDocument BuildMotion(Archive archive,double[] times,double fps,string video,string source,string hash,CancellationToken token)
    {
        var doc=new MotionDocument{Name=Path.GetFileNameWithoutExtension(source),Backend="HOT3D / imported UmeTrack annotations (not inference)",ModelVersion=Version,
            SourceSha256=hash,SourceVideo=video,SourceFps=fps,Space=MotionSpace.CameraRelative,MetricScaleCalibrated=true};
        doc.Cameras.Add(new(){Id="video",Source="HOT3D Aria RGB calibration; authored upright pinhole view with 30-degree tilt. Original per-frame extrinsics, calibration and modeled visibility are retained in source-annotations.json.",
            Calibrated=true,Synchronized=true,ImageWidth=Size,ImageHeight=Size,Intrinsics=new[]{Focal,0,Center,0,Focal,Center,0,0,1f}});
        var joints=archive.Shape.GetProperty("joint_rest_positions");var axes=archive.Shape.GetProperty("joint_rotation_axes");
        if(joints.GetArrayLength()!=22||axes.GetArrayLength()!=22)throw new NotSupportedException("Unsupported UmeTrack skeleton profile.");
        var selected=new[]{0,1,2,3,5,6,7,9,10,11,13,14,15,17,18,19};
        var roles=new[]{"ThumbMeta","ThumbProx","ThumbMid","ThumbDist","IndexProx","IndexMid","IndexDist","MiddleProx","MiddleMid","MiddleDist","RingProx","RingMid","RingDist","PinkyProx","PinkyMid","PinkyDist"};
        var rest=new List<XForm>();
        for(var side=0;side<2;side++)
        {
            var suffix=side==0?"L":"R";var root=doc.Bones.Count;rest.Add(XForm.Identity);doc.Bones.Add(new(){Name="hand_"+suffix,Role=Enum.Parse<BoneRole>("Hand"+suffix)});
            for(var i=0;i<selected.Length;i++)
            {
                var j=selected[i];var p=V(joints[j],.001f);if(side==1)p.X=-p.X;
                var parent=i==0||j%4==1&&j>3?root:root+i;
                var local=p-rest[parent].Pos;rest.Add(new(p,Quaternion.Identity));
                doc.Bones.Add(new(){Name=roles[i]+"_"+suffix,Role=Enum.Parse<BoneRole>(roles[i]+suffix),Parent=parent,RestPosition=MotionDocument.A(local)});
            }
        }
        for(var f=0;f<archive.Frames.Count;f++)
        {
            token.ThrowIfCancellationRequested();var input=archive.Frames[f];var cameraFromWorld=Transform(input.Camera.GetProperty("T_world_from_camera")).Inverse();
            var captureFromWorld=XForm.Compose(new(Vector3.Zero,CaptureFromCamera),cameraFromWorld);
            var frame=new MotionFrame{Time=times[f],Positions=doc.Bones.Select(b=>b.RestPosition.ToArray()).ToArray(),Rotations=doc.Bones.Select(b=>b.RestRotation.ToArray()).ToArray(),Evidence=Enumerable.Repeat(JointEvidence.Unobserved,doc.Bones.Count).ToArray()};
            for(var side=0;side<2;side++)
            {
                var root=side*17;
                if(!input.Hands.TryGetProperty(side==0?"left":"right",out var hand))
                {if(f>0)for(var b=root;b<root+17;b++){frame.Positions[b]=doc.Frames[^1].Positions[b].ToArray();frame.Rotations[b]=doc.Frames[^1].Rotations[b].ToArray();}continue;}
                var pose=hand.GetProperty("umetrack_pose");var angles=pose.GetProperty("joint_angles");
                // Upstream uses the first 20 finger angles; profiles may also carry
                // two unused wrist entries (the wrist transform is supplied separately).
                if(angles.GetArrayLength() is not (20 or 22))throw new NotSupportedException("Expected 20 or 22 UmeTrack joint angles.");
                var rootPose=XForm.Compose(captureFromWorld,Transform(pose.GetProperty("T_world_from_wrist")));
                frame.Positions[root]=MotionDocument.A(rootPose.Pos);frame.Rotations[root]=MotionDocument.A(rootPose.Rot);frame.Evidence[root]=JointEvidence.Reconstructed;
                var transformed=new XForm[20];
                for(var finger=0;finger<5;finger++)
                {
                    var accumulated=XForm.Identity;
                    for(var k=0;k<4;k++)
                    {
                        var j=finger*4+k;var aa=V(axes[j])*angles[j].GetSingle();var angle=aa.Length();var rotation=angle<1e-6f?Quaternion.Identity:Quaternion.CreateFromAxisAngle(aa/angle,angle);
                        var pivot=V(joints[j],.001f);accumulated=XForm.Compose(accumulated,new(pivot-Vector3.Transform(pivot,rotation),rotation));
                        var joint=new XForm(accumulated.TransformPoint(pivot),accumulated.Rot);transformed[j]=side==0?joint:Mirror(joint);
                    }
                }
                for(var i=0;i<selected.Length;i++)
                {
                    var index=root+i+1;var parent=doc.Bones[index].Parent;
                    var parentPose=parent==root?XForm.Identity:transformed[selected[parent-root-1]];
                    var local=XForm.Compose(parentPose.Inverse(),transformed[selected[i]]);
                    frame.Positions[index]=MotionDocument.A(local.Pos);frame.Rotations[index]=MotionDocument.A(local.Rot);frame.Evidence[index]=JointEvidence.Reconstructed;
                }
            }
            doc.Frames.Add(frame);
        }
        var objectFrames=archive.Frames.Select(f=>Objects(f.Objects)).ToArray();
        var allObjects=objectFrames.SelectMany(f=>f.Keys).Distinct().OrderBy(id=>id,StringComparer.Ordinal).ToArray();
        if(allObjects.Length>64)throw new InvalidDataException("HOT3D clip exceeds 64 distinct object tracks.");
        foreach(var id in allObjects)
        {
            token.ThrowIfCancellationRequested();var samples=objectFrames.Select(f=>f.GetValueOrDefault(id)).ToArray();
            if(samples.Any(p=>p.ValueKind==JsonValueKind.Undefined)){doc.Diagnostics.Add("Object "+id+" has incomplete annotations; retained in source-annotations.json but omitted from export tracks.");continue;}
            var track=new PropTrack{Id="hot3d_"+id,Source=ObjectMotionSource.Tracked,Space=MotionSpace.CameraRelative,Bones=new(){new(){Name="root"}}};
            for(var f=0;f<samples.Length;f++)
            {
                var pose=XForm.Compose(Transform(archive.Frames[f].Camera.GetProperty("T_world_from_camera")).Inverse(),Transform(samples[f].GetProperty("T_world_from_object")));
                pose=XForm.Compose(new(Vector3.Zero,CaptureFromCamera),pose);
                track.Frames.Add(new(){Time=times[f],Positions=new[]{MotionDocument.A(pose.Pos)},Rotations=new[]{MotionDocument.A(pose.Rot)},Evidence=new[]{JointEvidence.Reconstructed}});
            }
            track.Bones[0].RestPosition=track.Frames[0].Positions[0].ToArray();track.Bones[0].RestRotation=track.Frames[0].Rotations[0].ToArray();doc.Objects.Add(track);
        }
        doc.Diagnostics.Add("Imported published HOT3D annotations; no neural inference was run. Reconstructed labels describe supplied fitted tracks, not this library's predictions. Modeled visibility is retained separately and is not confidence.");
        doc.Diagnostics.Add("Camera-relative hand and rigid-object tracks share recorded calibration and timestamps. Object geometry/contact surfaces are not supplied by this importer; contacts are not inferred. Other cameras are not fused.");
        return doc;
    }
    static Dictionary<string,JsonElement> Objects(JsonElement objects)
    {
        var result=new Dictionary<string,JsonElement>();
        foreach(var property in objects.EnumerateObject())foreach(var value in property.Value.EnumerateArray())
            if(!result.TryAdd(value.GetProperty("object_uid").GetString()!,value))throw new NotSupportedException("Ambiguous duplicate HOT3D object UID.");
        if(result.Count>64)throw new InvalidDataException("HOT3D object budget exceeded.");return result;
    }
    static void RectificationMap(JsonElement calibration,Mat xMap,Mat yMap,CancellationToken token)
    {
        if(calibration.GetProperty("projection_model_type").GetString()!="CameraModelType.FISHEYE624")throw new NotSupportedException("Expected Aria Fisheye624 calibration.");
        var parameters=calibration.GetProperty("projection_params").EnumerateArray().Select(p=>p.GetDouble()).ToArray();
        if(parameters.Length!=15||parameters.Any(p=>!double.IsFinite(p)))throw new InvalidDataException("Invalid Aria calibration.");
        var inverse=Quaternion.Conjugate(VirtualFromCamera);
        for(var y=0;y<Size;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<Size;x++)
            {
                var ray=Vector3.Transform(new((x-Center)/Focal,(y-Center)/Focal,1),inverse);
                var uv=ray.Z>0?Project(ray,parameters):new Vector2(-1,-1);xMap.Set(y,x,uv.X);yMap.Set(y,x,uv.Y);
            }
        }
    }
    static Vector2 Project(Vector3 point,double[] p)
    {
        double x=point.X,y=point.Y,r=Math.Sqrt(x*x+y*y);var factor=Math.Atan2(r,point.Z)/Math.Max(r,1e-30);x*=factor;y*=factor;
        var r2=Math.Min(x*x+y*y,Math.PI*Math.PI);double radial=1,power=r2;for(var k=0;k<6;k++){radial+=p[k+3]*power;power*=r2;}
        x*=radial;y*=radial;var x2=x*x;var y2=y*y;var xy=x*y;r2=x2+y2;var r4=r2*r2;
        var dx=2*p[10]*xy+p[9]*(r2+2*x2)+p[11]*r2+p[12]*r4;var dy=2*p[9]*xy+p[10]*(r2+2*y2)+p[13]*r2+p[14]*r4;
        return new((float)((x+dx)*p[0]+p[1]),(float)((y+dy)*p[0]+p[2]));
    }
}
