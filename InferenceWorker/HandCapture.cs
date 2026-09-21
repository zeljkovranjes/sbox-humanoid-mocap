using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Editor;
using HumanoidMocap.Inference;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

/// <param name="EstimateFocal">Replace Camera's focal length with the clip's hand-scale
/// estimate when enough hands are found. Camera then supplies only the fallback.</param>
public sealed record HandCaptureRequest(string Video,string Models,string Output,string Backend,double Start,double End,WildHandsCrop.Camera Camera,bool EstimateFocal=false);

/// <summary>Bounded, resumable native hand reconstruction. Target selection and
/// corrections are deliberately excluded from the expensive prediction cache.</summary>
public static class HandCapture
{
    public const string ImplementationVersion="native-mano-v7-followed-hands";
    public const string FollowedSource="WiLoR followed through detector loss";
    /// <summary>A hand WiLoR is following by itself after the landmark detector lost it.</summary>
    public sealed record FollowedHand(string Side,float[][] Image,float[] GlobalRotation,float CropSize,double Since);
    public sealed record CropObservation(string Side,float Presence,float Handedness,float[][] Image,float[][] World,bool Tracked)
    {
        public static CropObservation From(HandObservation hand)=>new(hand.Side,hand.Presence,hand.Handedness,
            hand.ImageLandmarks.Select(MotionDocument.A).ToArray(),hand.RelativeWorldLandmarks.Select(MotionDocument.A).ToArray(),hand.Tracked);
        public HandObservation ToObservation()=>new(Side,Presence,Handedness,Image.Select(MotionDocument.V).ToArray(),World.Select(MotionDocument.V).ToArray(),Tracked);
    }
    public sealed class State
    {
        public string Key { get; set; }="";
        public string Status { get; set; }="pending";
        public string? Error { get; set; }
        public List<ManoFrameSample> Frames { get; set; }=new();
        public List<CropObservation> Tracking { get; set; }=new();
        /// <summary>Per-side WiLoR result of the last completed frame, detected or followed.</summary>
        public List<FollowedHand> Following { get; set; }=new();
        /// <summary>Camera the predictions were made with. Persisted so a resumed
        /// WildHands job keeps the camera-ray encodings of its completed frames.</summary>
        public WildHandsCrop.Camera? InferenceCamera { get; set; }
        public int FocalEstimateSamples { get; set; }
        public double InferenceSeconds { get; set; }
        public long PeakRamBytes { get; set; }
    }
    public static string Run(HandCaptureRequest request,CancellationToken cancellation=default,Action<string>? progress=null)
    {
        if(request.Backend is not ("mobilehand" or "wildhands" or "wilor"))throw new NotSupportedException("Choose MobileHand, WildHands or WiLoR. ACE is never selected automatically.");
        if(!double.IsFinite(request.Start)||!double.IsFinite(request.End)||request.Start<0||request.End<=request.Start)
            throw new ArgumentException("Select a nonempty video range.");
        if(request.Camera is null||request.Camera.Fx<=0||request.Camera.Fy<=0||!new[]{request.Camera.Fx,request.Camera.Fy,request.Camera.Cx,request.Camera.Cy}.All(float.IsFinite))
            throw new ArgumentException("Supply estimated or calibrated pinhole camera parameters.");
        var metadata=Mp4Metadata.Read(request.Video);
        var times=metadata.Times.Where(t=>t>=request.Start&&t<request.End).ToArray();
        if(times.Length is <1 or >1800)throw new ArgumentException("Choose between 1 and 1800 frames; the end time is exclusive.");
        var wild=request.Backend=="wildhands";var mobile=request.Backend=="mobilehand";
        var checkpointPath=Path.Combine(request.Models,mobile?"mobilehand/hmr_model_freihand_auc.pth":wild?"wildhands/wildhands.ckpt":"wilor/wilor_final.ckpt");
        var detectorPath=Path.Combine(request.Models,"hand_landmarker.task");
        string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();}
        cancellation.ThrowIfCancellationRequested();var sourceHash=Hash(request.Video);var detectorHash=Hash(detectorPath);
        var modelHash=mobile?MobileHandModel.CheckpointSha256:wild?WildHandsModel.CheckpointSha256:WilorModel.CheckpointSha256;
        // Verify cached jobs too; a different file must not masquerade as pinned weights.
        if(Hash(checkpointPath)!=modelHash)throw new InvalidDataException("Hand model checksum mismatch.");
        // Reduced precision changes predictions slightly, so it keeps its own cache.
        var wilorPrecision=!wild&&!mobile?WilorModel.ChoosePrecision():null;
        var implementation=ImplementationVersion+(wild?"; "+WildHandsCrop.ImplementationVersion:"")+(wilorPrecision is null or WilorModel.Float32?"":"; wilor-blocks-"+wilorPrecision);
        var keyData=JsonSerializer.Serialize(new{pipeline=implementation,decoder=WindowsVideoDecoder.ImplementationVersion,detectorImplementation=ManagedHands.ImplementationVersion,sourceHash,detectorHash,modelHash,request.Backend,request.Start,request.End,request.Camera});
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyData))).ToLowerInvariant();
        var directory=Path.Combine(Path.GetFullPath(request.Output),key);Directory.CreateDirectory(directory);
        using var jobLock=new FileStream(Path.Combine(directory,"job.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var statePath=Path.Combine(directory,"reconstruction.json");
        if(File.Exists(statePath)&&new FileInfo(statePath).Length>64*1024*1024)throw new InvalidDataException("Cached hand job exceeds metadata limit.");
        var state=File.Exists(statePath)?JsonSerializer.Deserialize<State>(File.ReadAllText(statePath),MotionDocument.JsonOptions)??throw new InvalidDataException("Empty hand job cache."):new State{Key=key};
        if(state.Key!=key||state.Frames.Count>times.Length||state.Frames.Where((f,i)=>Math.Abs(f.Time-times[i])>.001).Any())
            throw new InvalidDataException("Cached hand job does not match source timestamps.");
        void Save(string status)
        {
            state.Status=status;state.PeakRamBytes=Math.Max(state.PeakRamBytes,Process.GetCurrentProcess().PeakWorkingSet64);
            Atomic(statePath,JsonSerializer.Serialize(state,MotionDocument.JsonOptions));
        }
        try
        {
            if(state.Frames.Count<times.Length)
            {
                progress?.Invoke("Loading "+request.Backend+" and MediaPipe crop detector");
                var detector=new ManagedHands(File.ReadAllBytes(detectorPath));
                if(state.InferenceCamera is null)
                {
                    if(state.Frames.Count>0)throw new InvalidDataException("Cached hand job has no inference camera.");
                    state.InferenceCamera=request.Camera;
                    // WildHands consumes the lens before inference, and MobileHand's own hand scale is too
                    // unreliable to size one; WiLoR sizes it from its own predictions when motion is built.
                    if(request.EstimateFocal&&(wild||mobile))
                    {
                        progress?.Invoke("Estimating the recording lens from hand size");
                        var samples=FocalSamples(request.Video,times,detector,cancellation);state.FocalEstimateSamples=samples.Count;
                        if(CaptureCameraFraming.FocalLengthFromHandScale(samples,metadata.Width,metadata.Height) is float focal)
                            state.InferenceCamera=request.Camera with{Fx=focal,Fy=focal};
                    }
                    Save("running");
                }
                using var wildModel=wild?new WildHandsModel(checkpointPath,cancellation):null;
                using var wilorModel=!wild&&!mobile?new WilorModel(checkpointPath,cancellation,wilorPrecision):null;
                using var mobileModel=mobile?new MobileHandModel(checkpointPath,cancellation):null;
                using var decoder=new WindowsVideoDecoder(request.Video);
                for(var i=state.Frames.Count;i<times.Length;i++)
                {
                    cancellation.ThrowIfCancellationRequested();DecodedVideoFrame? frame;
                    do{frame=decoder.Read(cancellation);if(frame is null)throw new InvalidDataException("Video ended before the selected range.");}while(frame.Time<times[i]-.00001);
                    if(Math.Abs(frame.Time-times[i])>.001)throw new InvalidDataException("Decoded timestamps differ from the video sample table.");
                    var clock=Stopwatch.StartNew();
                    var observations=detector.DetectTracked(frame.Rgba,frame.Width,frame.Height,state.Tracking.Select(h=>h.ToObservation()).ToArray(),cancellation)
                        .GroupBy(h=>h.Side).Select(g=>g.OrderByDescending(h=>h.Presence).First()).ToArray();
                    WildHandsCrop.Box Bounds(HandObservation hand)=>new(hand.ImageLandmarks.Min(p=>p.X),hand.ImageLandmarks.Min(p=>p.Y),hand.ImageLandmarks.Max(p=>p.X),hand.ImageLandmarks.Max(p=>p.Y));
                    var reconstructed=new List<ManoHandSample>();
                    if(wild&&observations.Length>0)
                    {
                        var r=observations.FirstOrDefault(h=>h.Side=="R");var l=observations.FirstOrDefault(h=>h.Side=="L");
                        var prepared=WildHandsCrop.Prepare(frame,r is null?null:Bounds(r),l is null?null:Bounds(l),state.InferenceCamera);
                        var prediction=wildModel!.Run(prepared.Image,prepared.Right,prepared.Left,prepared.RightCenter,prepared.RightCorners,prepared.LeftCenter,prepared.LeftCorners,cancellation);
                        foreach(var observed in observations)
                        {
                            var hand=observed.Side=="R"?prediction.Right:prediction.Left;var box=Bounds(observed);
                            reconstructed.Add(new(observed.Side,hand.RotationMatrices,hand.Shape,hand.WeakCamera,
                                new((box.Left+box.Right)/2,(box.Top+box.Bottom)/2,Math.Max(box.Right-box.Left,box.Bottom-box.Top)),observed.Presence,observed.Handedness,
                                observed.Tracked?"MediaPipe tracked landmark ROI":"MediaPipe palm detector",DetectorImageLandmarks:observed.ImageLandmarks.Select(MotionDocument.A).ToArray()));
                        }
                    }
                    else if(mobile)foreach(var observed in observations)
                    {
                        var crop=MobileHandCrop.Prepare(frame,Bounds(observed),observed.Side=="R");
                        var hand=mobileModel!.Run(crop.Image,cancellation);
                        if(hand.WeakCamera[0]<=0)throw new InvalidDataException("MobileHand predicted a nonpositive projection scale. Raw completed frames were preserved.");
                        reconstructed.Add(new(observed.Side,hand.RotationMatrices,hand.Shape,hand.WeakCamera,crop.Box,observed.Presence,observed.Handedness,
                            observed.Tracked?"MediaPipe tracked landmark ROI":"MediaPipe palm detector",hand.Parameters,observed.ImageLandmarks.Select(MotionDocument.A).ToArray()));
                    }
                    else if(!wild)
                    {
                        var following=new List<FollowedHand>();
                        foreach(var observed in observations)
                        {
                            var crop=WilorCrop.Prepare(frame,Bounds(observed),observed.Side=="R");
                            var hand=wilorModel!.Run(crop.Image,cancellation);
                            reconstructed.Add(new(observed.Side,hand.RotationMatrices,hand.Shape,hand.WeakCamera,crop.Box,observed.Presence,observed.Handedness,
                                observed.Tracked?"MediaPipe tracked landmark ROI":"MediaPipe palm detector",DetectorImageLandmarks:observed.ImageLandmarks.Select(MotionDocument.A).ToArray()));
                            following.Add(HandFollowing.Describe(observed.Side,hand,crop.Box,frame.Width,frame.Height,frame.Time));
                        }
                        // The landmark detector gives up on hands that are partly hidden, for example gripping
                        // an object edge-on, although they are in plain view. WiLoR keeps reconstructing such a
                        // hand from its own projected joints while it stays inside the image and moves plausibly.
                        foreach(var last in state.Following.Where(f=>observations.All(o=>o.Side!=f.Side)))
                        {
                            var bounds=new WildHandsCrop.Box(last.Image.Min(p=>p[0]),last.Image.Min(p=>p[1]),last.Image.Max(p=>p[0]),last.Image.Max(p=>p[1]));
                            if(!(bounds.Right>bounds.Left)||!(bounds.Bottom>bounds.Top))continue;
                            var crop=WilorCrop.Prepare(frame,bounds,last.Side=="R");var hand=wilorModel!.Run(crop.Image,cancellation);
                            var next=HandFollowing.Describe(last.Side,hand,crop.Box,frame.Width,frame.Height,last.Since);
                            if(!HandFollowing.Plausible(last,next,frame.Width,frame.Height,frame.Time))continue;
                            reconstructed.Add(new(last.Side,hand.RotationMatrices,hand.Shape,hand.WeakCamera,crop.Box,0,1,FollowedSource));
                            following.Add(next);
                        }
                        state.Following=following;
                    }
                    state.InferenceSeconds+=clock.Elapsed.TotalSeconds;
                    // A followed hand keeps its place in the tracker: its projected joints are the next
                    // frame's first search region, so the detector reacquires it under the same side.
                    observations=observations.Concat(state.Following.Where(f=>observations.All(o=>o.Side!=f.Side)).Select(f=>
                        new HandObservation(f.Side,0,1,f.Image.Select(p=>new System.Numerics.Vector3(p[0],p[1],0)).ToArray(),new System.Numerics.Vector3[21],true))).ToArray();
                    state.Tracking=observations.Select(CropObservation.From).ToList();state.Frames.Add(new(frame.Time,reconstructed));Save("running");
                    progress?.Invoke($"Reconstructed {state.Frames.Count}/{times.Length} frames · {reconstructed.Count} hand(s)");
                }
            }
            else progress?.Invoke("Reusing cached hand predictions");
            cancellation.ThrowIfCancellationRequested();
            var camera=state.InferenceCamera??throw new InvalidDataException("Cached hand job has no inference camera.");
            var estimated=request.EstimateFocal&&camera!=request.Camera;
            // Focal length never enters the WiLoR or MobileHand networks, so their own
            // metric hand scale refines the detector-based estimate without new inference.
            if(request.EstimateFocal&&!wild&&!mobile)
            {
                // Distance is taken along the ray of the detected palm, where the wrist is
                // anchored, rather than the crop centre; they differ in wide-angle views.
                int[] palm={0,5,9,13,17};
                var samples=state.Frames.SelectMany(f=>f.Hands).Where(h=>h.WeakCamera[0]>0).Select(h=>
                {
                    var landmarks=h.DetectorImageLandmarks is {Length:21} image&&image.All(p=>p is {Length:>=2})?image:null;
                    return new CaptureCameraFraming.HandScaleSample(mobile?224/(1000*h.WeakCamera[0]*h.Crop.Size):2/(h.Crop.Size*h.WeakCamera[0]),
                        landmarks is null?h.Crop.CenterX:palm.Average(i=>landmarks[i][0]),landmarks is null?h.Crop.CenterY:palm.Average(i=>landmarks[i][1]));
                }).ToArray();
                if(CaptureCameraFraming.FocalLengthFromHandScale(samples,metadata.Width,metadata.Height) is float focal)
                {camera=camera with{Fx=focal,Fy=focal};estimated=true;state.FocalEstimateSamples=samples.Length;}
            }
            var motion=ManoMotionBuilder.Build(state.Frames,request.Backend,checkpointPath,Path.GetFileNameWithoutExtension(request.Video),
                Path.GetFullPath(request.Video),sourceHash,metadata.FrameRate,camera,metadata.Width,metadata.Height,cancellation,estimated?state.FocalEstimateSamples:null);
            motion.ModelVersion+="; "+implementation+"; crop detector "+ManagedHands.ImplementationVersion+"; "+WindowsVideoDecoder.ImplementationVersion;
            var motionPath=Path.Combine(directory,"raw-hands-v5-camera.hmotion");Atomic(motionPath,motion.ToJson());
            state.Error=null;Save("complete");return motionPath;
        }
        catch(OperationCanceledException){Save("cancelled_resumable");throw;}
        catch(Exception error){state.Error=error.Message;Save("failed_resumable");throw;}
    }
    /// <summary>Untracked detections on at most 48 evenly spaced frames. These only
    /// size the lens assumption; they are never reused as reconstruction observations.</summary>
    static List<CaptureCameraFraming.HandScaleSample> FocalSamples(string video,double[] times,ManagedHands detector,CancellationToken cancellation)
    {
        var wanted=Enumerable.Range(0,Math.Min(48,times.Length)).Select(i=>times.Length<=48?i:(int)Math.Round(i*(times.Length-1)/47d)).ToHashSet();
        var samples=new List<CaptureCameraFraming.HandScaleSample>();using var decoder=new WindowsVideoDecoder(video);
        for(var i=0;i<times.Length;i++)
        {
            cancellation.ThrowIfCancellationRequested();DecodedVideoFrame? frame;
            do{frame=decoder.Read(cancellation);if(frame is null)return samples;}while(frame.Time<times[i]-.00001);
            if(!wanted.Contains(i))continue;
            foreach(var hand in detector.Detect(frame.Rgba,frame.Width,frame.Height,cancellation))
                if(CaptureCameraFraming.DepthPerFocal(hand.ImageLandmarks,hand.RelativeWorldLandmarks) is float depth)
                    samples.Add(new(depth,hand.ImageLandmarks.Average(p=>p.X),hand.ImageLandmarks.Average(p=>p.Y)));
        }
        // Release the decoded full-resolution frames before the pose model loads its working set.
        GC.Collect();
        return samples;
    }
    static void Atomic(string path,string content)
    {var temporary=path+".tmp";File.WriteAllText(temporary,content);File.Move(temporary,path,true);}
}
