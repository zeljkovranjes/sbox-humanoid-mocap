using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Editor;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

public sealed record BodyCaptureRequest(string Video,string Models,string Output,double Start,double End,GvhmrDecoder.Box? PersonCrop=null);
public static class BodyCapture
{
    sealed class FrameState
    {
        public double Time { get; set; }
        public float[]? Observations { get; set; }
        public float[]? ImageFeatures { get; set; }
        public PersonDetector.Detection[]? Detections { get; set; }
        public PersonCropTrack.Sample? Person { get; set; }
        /// <summary>Left then right finger sample; null entries are hands that were not reconstructed.</summary>
        public BodyHandTracks.Sample?[]? Hands { get; set; }
    }
    sealed class State
    {
        public string Key { get; set; }="";
        public string Status { get; set; }="preparing";
        public string? Error { get; set; }
        public List<FrameState> Frames { get; set; }=new();
        public Dictionary<string,double> Seconds { get; set; }=new();
        public long PeakRamBytes { get; set; }
        public CameraMotionCheck.Result? CameraMotion { get; set; }
        public string? CameraMotionVersion { get; set; }
        public CameraRotationTrack.Result? CameraRotation { get; set; }
        public string? CameraRotationVersion { get; set; }
    }
    /// <summary>The automatic track has no subject at <see cref="Time"/>. <see cref="LastSeen"/> is null when none was found yet.</summary>
    sealed class SubjectLost(string message,double rangeStart,double time,double? lastSeen,List<FrameState> frames):IOException(message)
    {
        public double RangeStart=>rangeStart;public double Time=>time;public double? LastSeen=>lastSeen;public List<FrameState> Frames=>frames;
    }
    public const string PartlyVisiblePrefix="Performer not in view for the whole video";
    /// <summary>The shortest stretch with the performer in view that is still worth capturing.</summary>
    public const double MinimumVisibleSeconds=1;
    /// <summary>Captures the range; when the performer enters late or leaves the picture, captures the part
    /// where they are in view and says so, rather than refusing the whole video.</summary>
    public static string Run(BodyCaptureRequest request,CancellationToken cancellation,Action<string>? progress=null)
    {
        string? note=null;List<FrameState>? seed=null;
        for(var attempt=0;;attempt++)
        {
            try{return RunRange(request,cancellation,progress,seed,note);}
            catch(SubjectLost lost) when(request.PersonCrop is null&&attempt<3)
            {
                if(lost.LastSeen is double seen)
                {
                    if(seen-lost.RangeStart<MinimumVisibleSeconds)throw;
                    note=FormattableString.Invariant($"{PartlyVisiblePrefix}: they could not be followed from {lost.Time:F2} s, so the capture ends at {seen:F2} s. {note}").TrimEnd();
                    progress?.Invoke(FormattableString.Invariant($"Performer left the picture at {lost.Time:F1} s; capturing up to there"));
                    // Keep the 2D poses already found; the shorter range is a job of its own.
                    seed=lost.Frames.Where(f=>f.Time<=seen+.0001&&f.Observations is not null&&f.Person is not null).ToList();
                    request=request with{Start=lost.RangeStart,End=seen+.0001};
                }
                else
                {
                    progress?.Invoke("Looking for where the performer enters the picture");
                    if(FirstSubjectTime(request,lost.Time,cancellation) is not double entered||request.End-entered<MinimumVisibleSeconds)throw;
                    note=FormattableString.Invariant($"{PartlyVisiblePrefix}: nobody could be followed before {entered:F2} s, so the capture starts there.");
                    seed=null;request=request with{Start=entered};
                }
            }
        }
    }
    /// <summary>First time after <paramref name="after"/>, checked about four times a second, at which the detector finds
    /// somebody and the pose model sees enough of their body to follow, which is what the tracker itself requires.</summary>
    static double? FirstSubjectTime(BodyCaptureRequest request,double after,CancellationToken cancellation)
    {
        using var detector=new PersonDetector(Path.Combine(request.Models,"person/person_detection_mediapipe_2023mar.onnx"));
        using var pose=new VisionModel(Path.Combine(request.Models,"vitpose/vitpose-h-multi-coco.pth"),VisionModel.Kind.VitPoseHeatmaps,cancellation);
        using var decoder=new WindowsVideoDecoder(request.Video);var next=after+.25;DecodedVideoFrame? frame;
        while((frame=decoder.Read(cancellation)) is not null&&frame.Time<request.End)
        {
            if(frame.Time<next)continue;next=frame.Time+.25;
            if(PersonCropTrack.Select(null,detector.DetectFollowing(frame,null,cancellation)) is not { } subject)continue;
            var crop=VideoCrop.Prepare(frame,subject.Crop);
            var joints=VideoCrop.DecodeHeatmaps(VideoCrop.AverageFlippedHeatmaps(pose.Run(crop,cancellation),pose.Run(VideoCrop.FlipImage(crop),cancellation)),subject.Crop);
            if(PersonCropTrack.Followable(joints,null,frame.Width,frame.Height))return frame.Time;
        }
        return null;
    }
    static string RunRange(BodyCaptureRequest request,CancellationToken cancellation,Action<string>? progress,List<FrameState>? seed,string? visibilityNote)
    {
        if(!double.IsFinite(request.Start+request.End)||request.Start<0||request.End<=request.Start)throw new ArgumentException("Select a finite non-empty video range.");
        var metadata=Mp4Metadata.Read(request.Video);var captureTimes=metadata.CaptureTimes;
        if(captureTimes.Count(t=>t>=request.Start&&t<request.End) is <1 or >1800)throw new ArgumentException("Select between one and 1,800 frames.");
        // Edited footage: capture the first shot of at least half a second, not a subject followed across a cut.
        string? shotNote=null;
        if(request.PersonCrop is null)
        {
            progress?.Invoke("Checking the footage for cuts");
            for(var guard=0;guard<64;guard++)
            {
                var selected=captureTimes.Where(t=>t>=request.Start&&t<request.End).ToArray();
                if(ShotCutDetector.FirstCut(request.Video,selected,cancellation) is not int cut)break;
                var shotSeconds=selected[cut]-selected[0];
                if(shotSeconds>=.5){shotNote=FormattableString.Invariant($"{ShotCutDetector.Prefix} at {selected[cut]:F2} s. Only the shot from {selected[0]:F2} s up to the cut was captured; trim the video to capture another shot.");request=request with{End=selected[cut]};break;}
                request=request with{Start=selected[cut]};
            }
        }
        var count=captureTimes.Count(t=>t>=request.Start&&t<request.End);
        if(count<1)throw new ArgumentException("No shot of at least half a second was found in the selected range.");
        using var video=File.OpenRead(request.Video);var sourceSha=Convert.ToHexString(SHA256.HashData(video));
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{
            version="gvhmr-csharp-person-crop-v2"+(metadata.CaptureStride>1?"-every"+metadata.CaptureStride:""),detector=request.PersonCrop is null?PersonDetector.Version+PersonDetector.CheckpointSha256:"manual",decoder=WindowsVideoDecoder.ImplementationVersion,sourceSha,request.Start,request.End,request.PersonCrop,
            temporal=GvhmrTemporalNetwork.CheckpointSha256,hmr="2dcf79638109781d1ae5f5c44fee5f55bc83291c210653feead9b7f04fa6f20e",pose="50e33f4077ef2a6bcfd7110c58742b24c5859b7798fb0eedd6d2215e0a8980bc",
            // Reduced precision changes image features slightly, so it keeps its own cache.
            visionPrecision=WilorModel.ChoosePrecision(),
            // Fingers need the WiLoR checkpoint; a capture made without it is a different result.
            fingers=File.Exists(Path.Combine(request.Models,"wilor/wilor_final.ckpt"))?BodyHandTracks.Version:"none"
        }))));
        var folder=Path.Combine(request.Output,key);Directory.CreateDirectory(folder);var statePath=Path.Combine(folder,"reconstruction.json");
        using var jobLock=new FileStream(Path.Combine(folder,"job.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        // A checkpoint that is unreadable (interrupted write) or does not follow this video's sample
        // table is started again rather than failing every later attempt.
        var expected=captureTimes.Where(t=>t>=request.Start&&t<request.End).ToArray();
        State? state=null;
        try{if(File.Exists(statePath))state=JsonSerializer.Deserialize<State>(File.ReadAllText(statePath));}
        catch(JsonException){}
        if(state is null||state.Key!=key||state.Frames.Count>count||state.Frames.Where((f,i)=>Math.Abs(f.Time-expected[i])>.0005).Any())
        {
            if(state is not null)progress?.Invoke("Earlier progress for this video could not be reused; starting again");
            state=new State{Key=key};
        }
        if(state.Frames.Count==0&&seed is not null&&seed.Count<=count&&!seed.Where((f,i)=>Math.Abs(f.Time-expected[i])>.0005).Any())
            state.Frames.AddRange(seed.Select(f=>new FrameState{Time=f.Time,Observations=f.Observations,Detections=f.Detections,Person=f.Person}));
        void Save(string status)
        {
            state.Status=status;state.PeakRamBytes=Math.Max(state.PeakRamBytes,Process.GetCurrentProcess().PeakWorkingSet64);
            File.WriteAllText(statePath+".partial",JsonSerializer.Serialize(state));File.Move(statePath+".partial",statePath,true);progress?.Invoke(status);
        }
        void Visit(Action<DecodedVideoFrame,int> process)
        {
            using var decoder=new WindowsVideoDecoder(request.Video);DecodedVideoFrame? frame;var index=0;
            var wanted=captureTimes.Where(t=>t>=request.Start&&t<request.End).ToArray();
            while((frame=decoder.Read(cancellation))is not null)
            {
                // Follow the sample table rather than comparing decoded times with the range: the decoder
                // rounds to 100 ns, so a range that starts exactly on a frame (after a cut) would lose it.
                // Frames between the sampled ones (footage faster than the capture rate) are passed over.
                if(index>=wanted.Length)break;
                if(frame.Time<wanted[index]-.00001)continue;
                if(index>=1800)throw new InvalidDataException("Decoded range exceeds frame limit.");
                if(index>=state.Frames.Count)state.Frames.Add(new(){Time=frame.Time});
                if(Math.Abs(state.Frames[index].Time-frame.Time)>.0005)throw new InvalidDataException("Decoded timestamps differ from reconstruction checkpoint.");
                process(frame,index++);
            }
            if(index!=count)throw new InvalidDataException($"Expected {count} selected frames but decoded {index}.");
        }
        try
        {
            state.Error=null;Save("preparing");var watch=Stopwatch.StartNew();
            if(request.PersonCrop is { } manual)
            {
                if(!float.IsFinite(manual.CenterX+manual.CenterY+manual.Size)||manual.Size<=0)throw new ArgumentException("Invalid manual person crop.");
                Visit((frame,index)=>state.Frames[index].Person=new(manual,"manual crop",null));
            }
            else
            {
                // The detector finds the subject; after that each crop follows the previous
                // frame's 2D body joints, as pose trackers do. Detector-only association lost
                // a distant performer and was taken over by a nearer bystander on the kata sample.
                if(state.Frames.Count<count||state.Frames.Any(f=>f.Observations is null||f.Person is null))
                {
                    using var detector=new PersonDetector(Path.Combine(request.Models,"person/person_detection_mediapipe_2023mar.onnx"));
                    using var pose=new VisionModel(Path.Combine(request.Models,"vitpose/vitpose-h-multi-coco.pth"),VisionModel.Kind.VitPoseHeatmaps,cancellation);
                    GvhmrDecoder.Box? followed=null;var lastSeen=double.NaN;float? span=null;
                    float[] Observe(DecodedVideoFrame frame,GvhmrDecoder.Box box)
                    {
                        var crop=VideoCrop.Prepare(frame,box);
                        return VideoCrop.DecodeHeatmaps(VideoCrop.AverageFlippedHeatmaps(pose.Run(crop,cancellation),pose.Run(VideoCrop.FlipImage(crop),cancellation)),box);
                    }
                    Visit((frame,index)=>
                    {
                        var current=state.Frames[index];
                        if(current.Observations is null||current.Person is null)
                        {
                            float[]? joints=null;var evidence="followed body joints";float? score=null;
                            if(followed is { } expected)
                            {
                                joints=Observe(frame,expected);
                                if(!PersonCropTrack.Followable(joints,span,frame.Width,frame.Height))
                                {
                                    // Fast or blurred movement: look again in a wider window before asking the detector.
                                    var wider=expected with{Size=expected.Size*1.35f};joints=Observe(frame,wider);
                                    if(!PersonCropTrack.Followable(joints,span,frame.Width,frame.Height))joints=null;else{followed=wider;evidence="widened crop";}
                                }
                            }
                            if(joints is null)
                            {
                                current.Detections=detector.DetectFollowing(frame,followed,cancellation);
                                if(PersonCropTrack.Select(followed,current.Detections) is { } subject)
                                {
                                    // The detector's crop comes from the face and hips and can be far tighter
                                    // than the body; while a subject is followed, keep a comparable size.
                                    var crop=followed is { } known?subject.Crop with{Size=Math.Clamp(subject.Crop.Size,known.Size*.85f,known.Size*1.35f)}:subject.Crop;
                                    var found=Observe(frame,crop);
                                    if(PersonCropTrack.Followable(found,span,frame.Width,frame.Height)){joints=found;followed=crop;evidence="detected";score=subject.Score;}
                                }
                            }
                            if(joints is null)
                            {
                                if(followed is not { } held||!(current.Time-lastSeen<=.5))
                                    throw new SubjectLost($"Cannot follow one person at {current.Time:F2}s. Film one clearly visible performer, whole body in frame, or select the part of the video where they are in view under Advanced.",
                                        request.Start,current.Time,double.IsNaN(lastSeen)?null:lastSeen,state.Frames);
                                // Keep the last crop briefly; the joints are still this frame's own prediction.
                                joints=Observe(frame,held);evidence="held crop";
                            }
                            current.Observations=joints;current.Person=new(followed!.Value,evidence,score);
                            if(index==0||index==count-1)VideoCrop.SaveOverlay(frame,joints,Path.Combine(folder,$"observations-{index}.png"));
                        }
                        // A held crop is not a sighting: its joints did not pass as a followable body, so they
                        // must neither move the crop nor extend how long the subject may stay unseen.
                        if(current.Person!.Evidence!="held crop"&&PersonCropTrack.FromJoints(current.Observations) is { } next)
                        {followed=PersonCropTrack.Continue(followed,next,PersonCropTrack.ConfidentJoints(current.Observations));lastSeen=current.Time;span=PersonCropTrack.Span(current.Observations,frame.Width,frame.Height)??span;}
                        Save($"Followed person and reconstructed 2D pose {index+1}/{count}");
                    });
                    if(state.Frames[^1].Person!.Evidence=="held crop")
                    {
                        var lastFollowed=state.Frames.FindLastIndex(f=>f.Person!.Evidence!="held crop");
                        throw new SubjectLost("The performer could not be followed at the end of the video. Select the part where they are in view under Advanced.",
                            request.Start,state.Frames[lastFollowed+1].Time,lastFollowed<0?null:state.Frames[lastFollowed].Time,state.Frames);
                    }
                }
                // Final model crops come from each frame's own joints, then GVHMR's crop smoothing.
                GvhmrDecoder.Box? steady=null;
                var own=state.Frames.Select(f=>
                {
                    var crop=PersonCropTrack.FromJoints(f.Observations!) is { } box?PersonCropTrack.Continue(steady,box,PersonCropTrack.ConfidentJoints(f.Observations!)):f.Person!.Crop;
                    steady=crop;return f.Person! with{Crop=crop};
                }).ToArray();
                var track=PersonCropTrack.Stabilize(own);
                for(var i=0;i<track.Length;i++)state.Frames[i].Person=track[i];
            }
            state.Seconds["personDetectionThisRun"]=watch.Elapsed.TotalSeconds;Save("person-crops-ready");watch.Restart();
            if(state.CameraMotion is null||state.CameraMotionVersion!=CameraMotionCheck.Version)
            {
                progress?.Invoke("Checking whether the recording camera stayed still");
                using var check=new CameraMotionCheck();
                Visit((frame,index)=>check.Add(frame,state.Frames[index].Person!.Crop));
                state.CameraMotion=check.Finish();state.CameraMotionVersion=CameraMotionCheck.Version;
                state.Seconds["cameraMotionThisRun"]=watch.Elapsed.TotalSeconds;Save("camera-motion-checked");watch.Restart();
            }
            if(state.Frames.Count<count||state.Frames.Any(f=>f.Observations is null))
            {
                using var pose=new VisionModel(Path.Combine(request.Models,"vitpose/vitpose-h-multi-coco.pth"),VisionModel.Kind.VitPoseHeatmaps,cancellation);
                Visit((frame,index)=>
                {
                    if(state.Frames[index].Observations is not null)return;
                    var box=state.Frames[index].Person!.Crop;
                    var crop=VideoCrop.Prepare(frame,box);var heatmap=VideoCrop.AverageFlippedHeatmaps(pose.Run(crop,cancellation),pose.Run(VideoCrop.FlipImage(crop),cancellation));
                    state.Frames[index].Observations=VideoCrop.DecodeHeatmaps(heatmap,box);
                    if(index==0||index==count-1)VideoCrop.SaveOverlay(frame,state.Frames[index].Observations!,Path.Combine(folder,$"observations-{index}.png"));
                    Save($"Reconstructed 2D pose {index+1}/{count}");
                });
            }
            state.Seconds["poseThisRun"]=watch.Elapsed.TotalSeconds;GC.Collect();GC.WaitForPendingFinalizers();watch.Restart();
            var wilorPath=Path.Combine(request.Models,"wilor/wilor_final.ckpt");
            if(File.Exists(wilorPath)&&state.Frames.Any(f=>f.Hands is null))
            {
                progress?.Invoke("Loading WiLoR for finger capture");
                using var wilor=new WilorModel(wilorPath,cancellation);
                Visit((frame,index)=>
                {
                    var current=state.Frames[index];if(current.Hands is not null)return;
                    var hands=new BodyHandTracks.Sample?[2];
                    for(var side=0;side<2;side++)
                        if(BodyHandTracks.Region(current.Observations!,side==0,frame.Width,frame.Height) is { } box)
                            hands[side]=BodyHandTracks.Reconstruct(wilor,frame,box,side==0,cancellation);
                    current.Hands=hands;Save($"Reconstructed fingers {index+1}/{count}");
                });
                state.Seconds["fingersThisRun"]=watch.Elapsed.TotalSeconds;GC.Collect();GC.WaitForPendingFinalizers();watch.Restart();
            }
            var focalLength=MathF.Sqrt(metadata.Width*metadata.Width+metadata.Height*metadata.Height);
            if(!state.CameraMotion!.Stationary&&(state.CameraRotation is null||state.CameraRotationVersion!=CameraRotationTrack.Version))
            {
                progress?.Invoke("Following the moving camera's rotation from the background");
                using var rotation=new CameraRotationTrack(focalLength);
                Visit((frame,index)=>rotation.Add(frame,state.Frames[index].Person!.Crop,index==count-1));
                state.CameraRotation=rotation.Finish();state.CameraRotationVersion=CameraRotationTrack.Version;
                state.Seconds["cameraRotationThisRun"]=watch.Elapsed.TotalSeconds;Save("camera-rotation-followed");watch.Restart();
            }
            if(state.Frames.Any(f=>f.ImageFeatures is null))
            {
                using var hmr=new VisionModel(Path.Combine(request.Models,"hmr2/hmr2.ckpt"),VisionModel.Kind.Hmr2Features,cancellation);
                Visit((frame,index)=>
                {
                    if(state.Frames[index].ImageFeatures is not null)return;
                    state.Frames[index].ImageFeatures=hmr.Run(VideoCrop.Prepare(frame,state.Frames[index].Person!.Crop),cancellation);Save($"Reconstructed image features {index+1}/{count}");
                });
            }
            state.Seconds["imageFeaturesThisRun"]=watch.Elapsed.TotalSeconds;GC.Collect();GC.WaitForPendingFinalizers();watch.Restart();
            Save("temporal-inference");var camera=new GvhmrDecoder.Camera(focalLength,metadata.Width*.5f,metadata.Height*.5f);
            var boxes=state.Frames.Select(f=>f.Person!.Crop).ToArray();var cameras=Enumerable.Repeat(camera,count).ToArray();
            var identityCondition=Enumerable.Range(0,count).SelectMany(_=>new[]{1f,0,0,0,1,0}).ToArray();
            // A still camera, or one whose rotation could not be followed, is conditioned as not rotating.
            var followedRotation=!state.CameraMotion.Stationary&&state.CameraRotation is {Usable:true} solvedRotation&&solvedRotation.AngularVelocity6d.Length==count*6?solvedRotation.AngularVelocity6d:null;
            var conditions=GvhmrDecoder.Prepare(state.Frames.SelectMany(f=>f.Observations!).ToArray(),boxes,cameras,followedRotation??identityCondition);
            var network=new GvhmrTemporalNetwork(Path.Combine(request.Models,"gvhmr/gvhmr_siga24_release.ckpt"),cancellation);
            var prediction=network.Run(count,conditions.Observations,conditions.CliffCamera,conditions.NormalizedAngularVelocity,state.Frames.SelectMany(f=>f.ImageFeatures!).ToArray(),cancellation);
            File.WriteAllText(Path.Combine(folder,"raw-predictions.json"),JsonSerializer.Serialize(prediction));
            var rotationPath=Path.Combine(folder,BodyRefinement.CameraRotationFile);
            if(followedRotation is null)File.Delete(rotationPath);
            else File.WriteAllText(rotationPath,JsonSerializer.Serialize(new BodyRefinement.CameraRotation(followedRotation,state.CameraRotation!.RotationOnly)));
            var decoded=GvhmrDecoder.Decode(prediction.PredX,count);var translation=GvhmrDecoder.CameraTranslation(prediction.PredCam,boxes,cameras);
            var skeleton=new SmplxSkeleton(Path.Combine(request.Models,"smplx/SMPLX_NEUTRAL.npz"),cancellation);
            var motion=BodyMotionBuilder.CameraRelative(skeleton,decoded,translation,state.Frames.Select(f=>f.Time).ToArray(),Path.GetFileNameWithoutExtension(request.Video),request.Video,sourceSha,metadata.CaptureFrameRate,camera);
            if(state.Frames.All(f=>f.Hands is not null))
            {
                BodyHandTracks.Append(motion,state.Frames.Select(f=>f.Hands!).ToArray());
                motion.ModelVersion+="; "+BodyHandTracks.Version+" "+WilorModel.CheckpointSha256;
            }
            motion.ModelVersion+="; "+WindowsVideoDecoder.ImplementationVersion;
            motion.ModelVersion+="; "+(request.PersonCrop is null?PersonDetector.Version:"manual-person-crop");
            motion.Diagnostics.Add(request.PersonCrop is null
                ?$"Automatic single-person image crops ({PersonDetector.Version}): the detector located the subject in {state.Frames.Count(f=>f.Person!.Evidence=="detected")} frame(s), crops then followed the previous frame's 2D body joints, and {state.Frames.Count(f=>f.Person!.Evidence=="held crop")} frame(s) briefly held the last crop. Two centered five-frame crop averages follow. Crop evidence is saved separately in reconstruction.json; it is not joint confidence or camera calibration."
                :"Explicit fixed manual person crop. Automatic subject tracking was not used.");
            if(shotNote is not null)motion.Diagnostics.Add(shotNote);
            if(visibilityNote is not null)motion.Diagnostics.Add(visibilityNote);
            if(metadata.SamplingNote is { } sampling)motion.Diagnostics.Add(sampling);
            motion.Diagnostics.Add(state.CameraMotion.Diagnostic);
            if(!state.CameraMotion.Stationary&&state.CameraRotation is not null)motion.Diagnostics.Add(state.CameraRotation.Diagnostic);
            if(followedRotation is not null)
            {
                // The builder's default note describes a still-camera conditioning that was not used here.
                motion.Diagnostics.RemoveAll(d=>d.StartsWith("Camera-relative reconstruction with identity camera-angular-velocity",StringComparison.Ordinal));
                motion.Diagnostics.Add("Camera-relative reconstruction conditioned on the followed camera rotation; camera translation has not been recovered.");
            }
            var result=Path.Combine(folder,"raw-body.hmotion");File.WriteAllText(result+".partial",motion.ToJson());File.Move(result+".partial",result,true);
            state.Seconds["temporalAndDecodeThisRun"]=watch.Elapsed.TotalSeconds;Save("complete");return result;
        }
        catch(OperationCanceledException){Save("cancelled-resumable");throw;}
        catch(Exception e){state.Error=e.Message;Save("failed-resumable");throw;}
    }
}
