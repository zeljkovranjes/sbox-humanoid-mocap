using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Motion;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Inference;
using Vector3=System.Numerics.Vector3;

/// <summary>Fits observed landmarks to a canonical hand skeleton. The source contains
/// hands only; shoulders and arm IK belong to target retargeting.</summary>
public sealed class HandMotionBuilder
{
    readonly TargetRig rig;
    readonly XForm[] rest;
    readonly int[] sourceBones;
    readonly Dictionary<int,int> documentIndices;
    CameraObservation? previewCamera;
    public MotionDocument Document { get; }
    public bool SwapHands { get; set; }
    public float WristPlaneWidth { get; set; }=.9f;
    public float WristPlaneDepth { get; set; }=.4f;

    public HandMotionBuilder(TargetRig template,string name,string video,string hash,double fps)
    {
        rig=template;rest=rig.Skeleton.RestWorld.Select(t=>new XForm(t.Pos/100,t.Rot)).ToArray();
        sourceBones=rig.Skeleton.Bones.Where(b=>rig.RoleOf(b.Index) is BoneRole.HandL or BoneRole.HandR||
            rig.RoleOf(b.Index) is { } role&&FingerSolver.IsFingerRole(role)).Select(b=>b.Index).ToArray();
        documentIndices=sourceBones.Select((source,index)=>(source,index)).ToDictionary(x=>x.source,x=>x.index);
        Document=new MotionDocument{Name=name,SourceVideo=video,SourceSha256=hash,SourceFps=fps,
            Backend="MediaPipe hands / experimental managed C#",ModelVersion="hand_landmarker/float16/1; hand-forest-v3-authored-metacarpals; camera-framing-v1",
            Space=MotionSpace.CameraRelative,MetricScaleCalibrated=false};
        foreach(var source in sourceBones)
        {
            var role=rig.RoleOf(source);var hand=role is BoneRole.HandL or BoneRole.HandR;
            var parent=hand?-1:rig.Skeleton[source].ParentIndex;
            while(parent>=0&&!documentIndices.ContainsKey(parent))parent=rig.Skeleton[parent].ParentIndex;
            if(!hand&&parent<0)throw new ArgumentException("Finger mapping must descend from a mapped hand.");
            var local=parent<0?new XForm(Vector3.Zero,rest[source].Rot):XForm.Compose(rest[parent].Inverse(),rest[source]);
            Document.Bones.Add(new(){Name=rig.Skeleton[source].Name,Role=role,Parent=parent<0?-1:documentIndices[parent],Group=hand?"arms":"fingers",
                RestPosition=MotionDocument.A(local.Pos),RestRotation=MotionDocument.A(local.Rot)});
        }
        Document.Diagnostics.AddRange(new[]{"Experimental landmark reconstruction. Model-port parity has not been established.",
            "Hand-relative 3D landmarks are reconstructed; rotations are fitted to a fixed canonical hand skeleton.",
            "Metacarpal rest transforms are authored template anatomy, not observed motion. They are labeled Authored while their hand is observed.",
            "Finger segment directions follow the landmarks. Axial twist is unmeasured and estimated by minimal swing relative to the parent segment; it is not captured finger torsion.",
            "Camera-relative wrist translation uses an assumed image plane, not measured depth or camera motion.",
            "Separated hand tracks can retain left/right identity through weak classifier disagreement. Saved handedness probabilities below 0.5 record that disagreement; no missing hand is generated.",
            "The source contains no shoulders or elbows. Target arm IK is estimated after reconstruction.",
            "Unobserved hands hold their last pose and remain labeled unobserved. No per-joint confidence is supplied."});
    }
    public void Add(double time,int width,int height,IReadOnlyList<HandObservation> observations)
    {
        if(width<=0||height<=0||!float.IsFinite(WristPlaneWidth)||!float.IsFinite(WristPlaneDepth)||WristPlaneWidth<=0||WristPlaneDepth<=0)
            throw new ArgumentException("Invalid image dimensions or assumed wrist plane.");
        var focal=width*WristPlaneDepth/WristPlaneWidth;
        if(previewCamera is null)
        {
            previewCamera=new(){Id="video",Source="Authored preview camera matching the assumed wrist plane; not recovered video calibration",
                ImageWidth=width,ImageHeight=height,Calibrated=false,Synchronized=true,
                Intrinsics=new[]{focal,0,width/2f,0,focal,height/2f,0,0,1}};
            Document.Cameras.Add(previewCamera);
        }
        else if(previewCamera.Intrinsics is {} intrinsics&&(previewCamera.ImageWidth!=width||previewCamera.ImageHeight!=height||intrinsics[0]!=focal))
        {
            // One static camera cannot describe changing image/plane geometry.
            previewCamera.ImageWidth=previewCamera.ImageHeight=null;previewCamera.Intrinsics=null;
            previewCamera.Source="Assumed wrist-plane geometry changes within this clip; preview camera is unspecified";
        }
        var previous=Document.Frames.LastOrDefault();
        var frame=new MotionFrame{Time=time,
            Positions=(previous?.Positions??Document.Bones.Select(b=>b.RestPosition).ToArray()).Select(p=>p.ToArray()).ToArray(),
            Rotations=(previous?.Rotations??Document.Bones.Select(b=>b.RestRotation).ToArray()).Select(q=>q.ToArray()).ToArray(),
            Evidence=Enumerable.Repeat(JointEvidence.Unobserved,sourceBones.Length).ToArray(),Confidence=null};
        var desired=new Dictionary<int,Quaternion>();
        foreach(var observed in observations.GroupBy(h=>h.Side).Select(g=>g.OrderByDescending(h=>h.Presence).First()))
        {
            if(observed.Side is not ("L" or "R")||observed.ImageLandmarks.Length!=21||observed.RelativeWorldLandmarks.Length!=21)continue;
            var side=SwapHands?(observed.Side=="L"?"R":"L"):observed.Side;
            BoneRole Role(string name)=>Enum.Parse<BoneRole>(name+side);
            if(rig.BoneForRole(Role("Hand")) is not int hand||rig.BoneForRole(Role("IndexProx")) is not int index||
                rig.BoneForRole(Role("PinkyProx")) is not int pinky||rig.BoneForRole(Role("MiddleProx")) is not int middle)continue;
            // MediaPipe x-right/y-down/z-away -> document x-right/y-up/z-toward viewer.
            var points=observed.RelativeWorldLandmarks.Select(v=>new Vector3(v.X,-v.Y,-v.Z)).ToArray();
            var across=points[5]-points[17];var restAcross=rest[index].Pos-rest[pinky].Pos;
            if(!TryBasis(rest[middle].Pos-rest[hand].Pos,restAcross,out var reference)||
                !TryBasis(points[9]-points[0],across,out var orientation))continue;
            var wrist=observed.ImageLandmarks[0];
            if(!Finite(wrist))continue;
            var handIndex=documentIndices[hand];
            frame.Positions[handIndex]=MotionDocument.A(new Vector3((wrist.X/width-.5f)*WristPlaneWidth,
                (.5f-wrist.Y/height)*WristPlaneWidth*height/width,-WristPlaneDepth));
            desired[handIndex]=Quaternion.Normalize(orientation*Quaternion.Inverse(reference)*rest[hand].Rot);
            frame.Evidence[handIndex]=JointEvidence.Reconstructed;
            foreach(var finger in new[]{"Thumb","Index","Middle","Ring","Pinky"})
                if(rig.BoneForRole(Role(finger+"Meta")) is int meta&&documentIndices.TryGetValue(meta,out var metaIndex))
                {
                    frame.Positions[metaIndex]=Document.Bones[metaIndex].RestPosition.ToArray();
                    frame.Rotations[metaIndex]=Document.Bones[metaIndex].RestRotation.ToArray();
                    frame.Evidence[metaIndex]=JointEvidence.Authored;
                }
            foreach(var (finger,start) in new[]{("Thumb",1),("Index",5),("Middle",9),("Ring",13),("Pinky",17)})
            {
                var parentDelta=Quaternion.Normalize(desired[handIndex]*Quaternion.Inverse(rest[hand].Rot));
                var segments=new[]{"Prox","Mid","Dist"};
                for(var k=0;k<3;k++)
                {
                    if(rig.BoneForRole(Role(finger+segments[k])) is not int bone)continue;
                    Vector3 direction;
                    if(k<2&&rig.BoneForRole(Role(finger+segments[k+1])) is int next)direction=rest[next].Pos-rest[bone].Pos;
                    else if(k>0&&rig.BoneForRole(Role(finger+segments[k-1])) is int parent)direction=rest[bone].Pos-rest[parent].Pos;
                    else continue;
                    var capturedDirection=points[start+k+1]-points[start+k];
                    if(!Finite(direction)||!Finite(capturedDirection)||direction.LengthSquared()<1e-10f||capturedDirection.LengthSquared()<1e-10f)break;
                    // A segment direction does not measure roll. Carry its parent's
                    // frame and apply only the swing needed to match the observation.
                    // Independent palm-axis bases become singular when a finger
                    // points across the palm and can add a spurious 180-degree twist.
                    var predictedDirection=Vector3.Transform(direction,parentDelta);
                    var delta=Quaternion.Normalize(MathQ.FromTo(predictedDirection,capturedDirection)*parentDelta);
                    var joint=documentIndices[bone];
                    desired[joint]=Quaternion.Normalize(delta*rest[bone].Rot);
                    frame.Evidence[joint]=JointEvidence.Reconstructed;
                    parentDelta=delta;
                }
            }
        }
        var world=new Quaternion[sourceBones.Length];
        for(var i=0;i<world.Length;i++)
        {
            var parent=Document.Bones[i].Parent;var parentRotation=parent<0?Quaternion.Identity:world[parent];
            if(desired.TryGetValue(i,out var rotation))frame.Rotations[i]=MotionDocument.A(Quaternion.Normalize(Quaternion.Inverse(parentRotation)*rotation));
            world[i]=Quaternion.Normalize(parentRotation*MotionDocument.Q(frame.Rotations[i]));
        }
        Document.Frames.Add(frame);
    }
    static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    static bool TryBasis(Vector3 direction,Vector3 across,out Quaternion rotation)
    {
        rotation=Quaternion.Identity;
        if(!Finite(direction)||!Finite(across)||direction.LengthSquared()<1e-10f)return false;
        var x=Vector3.Normalize(direction);var y=across-x*Vector3.Dot(across,x);
        if(y.LengthSquared()<1e-10f)return false;
        y=Vector3.Normalize(y);var z=Vector3.Normalize(Vector3.Cross(x,y));
        rotation=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1)));
        return true;
    }
}
