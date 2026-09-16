using System.Numerics;
using HumanoidMocap.Inference;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

public sealed record ManoHandSample(string Side,float[] RotationMatrices,float[] Shape,float[] WeakCamera,
    GvhmrDecoder.Box Crop,float DetectorPresence,float DetectorHandedness,string CropSource="MediaPipe palm detector",float[]? NativeParameters=null,
    float[][]? DetectorImageLandmarks=null);
public sealed record ManoFrameSample(double Time,List<ManoHandSample> Hands);

/// <summary>Preserves native MANO articulation and camera-space wrist translation.
/// Target arm IK is a later stage; no measured shoulders or elbows are invented.</summary>
public static class ManoMotionBuilder
{
    static readonly string[] Roles={"Hand","IndexProx","IndexMid","IndexDist","MiddleProx","MiddleMid","MiddleDist",
        "PinkyProx","PinkyMid","PinkyDist","RingProx","RingMid","RingDist","ThumbProx","ThumbMid","ThumbDist"};
    public static MotionDocument Build(IReadOnlyList<ManoFrameSample> samples,string backend,string checkpointPath,
        string name,string video,string sourceHash,double fps,WildHandsCrop.Camera camera,int width,int height,CancellationToken cancellation=default)
    {
        if(backend is not ("mobilehand" or "wildhands" or "wilor"))throw new ArgumentException("Unsupported MANO motion backend.");
        if(samples.Count==0||!samples.Any(f=>f.Hands.Count>0))throw new InvalidDataException("No reconstructed hands. Raw observations were retained.");
        var wild=backend=="wildhands";var mobile=backend=="mobilehand";using var checkpoint=mobile?null:new TorchCheckpoint(checkpointPath);
        var mobileDecoder=mobile?MobileHandModel.ReadDecoder(checkpointPath,cancellation):null;
        var document=new MotionDocument{Name=name,SourceVideo=video,SourceSha256=sourceHash,SourceFps=fps,
            Backend=mobile?"MobileHand / C# native CPU / MediaPipe crops":wild?"WildHands / C# native CPU / MediaPipe crops":"WiLoR / C# native CPU / MediaPipe crops",
            ModelVersion=(mobile?MobileHandModel.CheckpointSha256:wild?WildHandsModel.CheckpointSha256:WilorModel.CheckpointSha256)+"; camera-framing-v1",
            Space=MotionSpace.CameraRelative,MetricScaleCalibrated=false};
        var cameraToDocument=Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI);
        var decoders=new ManoDecoder[2];var betas=new float[2][];var rest=new ManoDecoder.DecodedHand[2];
        var identity=Enumerable.Range(0,16).SelectMany(_=>new float[]{1,0,0,0,1,0,0,0,1}).ToArray();
        for(var sideIndex=0;sideIndex<2;sideIndex++)
        {
            var side=sideIndex==0?"L":"R";
            decoders[sideIndex]=mobileDecoder??new(checkpoint!,wild?"model.mano_"+side.ToLowerInvariant()+".mano.":"mano.");
            var observed=samples.SelectMany(f=>f.Hands).Where(h=>h.Side==side).ToArray();
            betas[sideIndex]=Enumerable.Range(0,10).Select(i=>observed.Length==0?0f:observed.Average(h=>h.Shape[i])).ToArray();
            rest[sideIndex]=decoders[sideIndex].Decode(identity,betas[sideIndex],false,cancellation);
            var parents=decoders[sideIndex].Parents;var positions=rest[sideIndex].RestJoints;
            Vector3 Reflect(Vector3 value)=>!wild&&side=="L"?new(-value.X,value.Y,value.Z):value;
            for(var j=0;j<16;j++)document.Bones.Add(new()
            {
                Name=Roles[j]+side,Role=Enum.Parse<BoneRole>(Roles[j]+side),Parent=parents[j]<0?-1:sideIndex*16+parents[j],Group=j==0?"arms":"fingers",
                RestPosition=MotionDocument.A(j==0?Vector3.Transform(Reflect(positions[j]),cameraToDocument):Reflect(positions[j]-positions[parents[j]])),
                RestRotation=MotionDocument.A(j==0?cameraToDocument:Quaternion.Identity)
            });
        }
        var previousPositions=document.Bones.Select(b=>(float[])b.RestPosition.Clone()).ToArray();
        var previousRotations=document.Bones.Select(b=>(float[])b.RestRotation.Clone()).ToArray();
        var palmResiduals=new List<float>();var palmRelativeResiduals=new List<float>();
        foreach(var sample in samples)
        {
            cancellation.ThrowIfCancellationRequested();
            var frame=new MotionFrame{Time=sample.Time,Positions=previousPositions.Select(p=>(float[])p.Clone()).ToArray(),
                Rotations=previousRotations.Select(q=>(float[])q.Clone()).ToArray(),Evidence=Enumerable.Repeat(JointEvidence.Unobserved,32).ToArray(),Confidence=null};
            foreach(var observed in sample.Hands)
            {
                var side=observed.Side=="L"?0:1;var mirror=!wild&&side==0;var sign=mirror?-1:1;
                var hand=decoders[side].Decode(observed.RotationMatrices,betas[side],wild,cancellation);
                var weak=observed.WeakCamera;Vector3 translation;
                if(wild)
                {
                    var f=(camera.Fx+camera.Fy)/2;
                    translation=new(weak[1],weak[2],2*f/(Math.Max(width,height)*Math.Max(.1f,weak[0])+1e-9f));
                }
                else if(mobile)
                {
                    // Upstream projects millimetres directly into 224px crop
                    // coordinates. Depth is inferred from this weak perspective
                    // scale and estimated intrinsics, not measured world motion.
                    if(weak[0]<=0)throw new InvalidDataException("Invalid MobileHand projection scale.");
                    var pixelScale=1000*weak[0]*observed.Crop.Size/224;
                    var z=(camera.Fx+camera.Fy)/(2*pixelScale);
                    var x=observed.Crop.CenterX+sign*(weak[1]-112)*observed.Crop.Size/224;
                    var y=observed.Crop.CenterY+(weak[2]-112)*observed.Crop.Size/224;
                    translation=new((x-camera.Cx)*z/camera.Fx,(y-camera.Cy)*z/camera.Fy,z);
                }
                else
                {
                    var scale=observed.Crop.Size*weak[0]+1e-9f;
                    translation=new(sign*weak[1]+2*(observed.Crop.CenterX-camera.Cx)/scale,
                        weak[2]+2*(observed.Crop.CenterY-camera.Cy)/scale,(camera.Fx+camera.Fy)/scale);
                }
                if(observed.DetectorImageLandmarks is {Length:21} image)
                {
                    if(image.Any(p=>p is null||p.Length<2||!float.IsFinite(p[0]+p[1])))throw new InvalidDataException("Invalid cached detector image landmarks.");
                    int[] palm={0,5,9,13,17};var errors=new List<float>();float span=0;
                    foreach(var a in palm)foreach(var b in palm)
                        span=Math.Max(span,Vector2.Distance(new(image[a][0],image[a][1]),new(image[b][0],image[b][1])));
                    foreach(var p in palm)
                    {
                        var point=hand.Landmarks[p];point.X*=sign;point+=translation;
                        if(point.Z<=0)continue;
                        var projected=new Vector2(camera.Fx*point.X/point.Z+camera.Cx,camera.Fy*point.Y/point.Z+camera.Cy);
                        errors.Add(Vector2.Distance(projected,new(image[p][0],image[p][1])));
                    }
                    if(errors.Count==5&&span>1)
                    {var rms=MathF.Sqrt(errors.Average(e=>e*e));palmResiduals.Add(rms);palmRelativeResiduals.Add(rms/span);}
                }
                for(var j=0;j<16;j++)
                {
                    var index=side*16+j;var rotation=hand.LocalRotations[j];
                    if(mirror)rotation=new(rotation.X,-rotation.Y,-rotation.Z,rotation.W); // S R S, S=diag(-1,1,1)
                    if(j==0)
                    {
                        var wrist=hand.RestJoints[0];wrist.X*=sign;
                        frame.Positions[index]=MotionDocument.A(Vector3.Transform(wrist+translation,cameraToDocument));
                        rotation=Quaternion.Normalize(cameraToDocument*rotation);
                    }
                    var previous=MotionDocument.Q(previousRotations[index]);
                    if(Quaternion.Dot(rotation,previous)<0)rotation=new(-rotation.X,-rotation.Y,-rotation.Z,-rotation.W);
                    frame.Rotations[index]=MotionDocument.A(rotation);frame.Evidence[index]=JointEvidence.Reconstructed;
                }
            }
            document.Frames.Add(frame);previousPositions=frame.Positions;previousRotations=frame.Rotations;
        }
        document.Cameras.Add(new(){Id="video",Source=camera.Calibrated?"User-supplied camera calibration":"Estimated pinhole camera; not calibration",
            Calibrated=camera.Calibrated,Synchronized=true,ImageWidth=width,ImageHeight=height,Intrinsics=new[]{camera.Fx,0,camera.Cx,0,camera.Fy,camera.Cy,0,0,1}});
        document.Diagnostics.AddRange(new[]{
            "Native MANO wrist and finger rotations are retained. Shoulders and elbows are not observed and require target-rig IK.",
            "Camera-relative output; camera motion and world-space trajectories have not been recovered. Monocular metric scale remains model-derived.",
            "MediaPipe supplies hand crops and side labels, not this backend's finger articulation. Detector scores are stored separately; no per-joint 3D confidence is available.",
            "Separated hand tracks may retain identity through weak classifier disagreement. Detector handedness is the model probability for the assigned side and can be below 0.5.",
            "Absent hands hold their prior pose and are labeled Unobserved. No automatic long-gap reconstruction is claimed.",
            "A mean predicted hand shape fixes bone lengths within this clip. Original per-frame shape predictions remain in the raw cache.",
            "These C# model ports remain experimental. Independent image-network reference parity and ground-truth accuracy are not established."
        });
        if(mobile)document.Diagnostics.Add("MobileHand is a small single-image model. Sample videos showed large rotation and estimated-depth jumps; no temporal or occlusion accuracy is established. Original 39-parameter predictions remain in the raw cache.");
        if(palmResiduals.Count>0)
        {
            float Percentile(List<float> values,float fraction){var sorted=values.OrderBy(v=>v).ToArray();return sorted[(int)Math.Round((sorted.Length-1)*fraction)];}
            var median=Percentile(palmResiduals,.5f);var p95=Percentile(palmResiduals,.95f);var relative=Percentile(palmRelativeResiduals,.5f);
            document.Diagnostics.Add(FormattableString.Invariant($"Native palm reprojection versus MediaPipe image landmarks: median {median:F1} px, p95 {p95:F1} px across {palmResiduals.Count} observed hands. Derived disagreement metric, not 3D confidence or ground-truth accuracy."));
            if(relative>.2f)document.Diagnostics.Add("Review wrist placement: native palm projection disagrees with detected image landmarks by more than 20% of palm span at the median. This heuristic can flag model/camera/crop errors; it does not identify which estimate is correct.");
        }
        document.Validate();return document;
    }
}
