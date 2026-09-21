using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Keeps a captured body's stance width when the target's hips are a different width.
/// Leg rotations copied onto a rig whose hip joints sit wider (relative to its legs) than the
/// performer's carry both feet outward by the extra half-width: on a reconstructed performer
/// with hip joints 0.15 leg-lengths apart and Human's 0.25, a 0.62 leg-length stance became 0.73
/// and read as splayed legs. Each ankle is moved back along the pelvis' own lateral axis by that
/// difference and the leg is re-solved, preserving bone lengths and the foot's world orientation.
/// Applied before ground alignment and foot anchoring. A proportion correction, not new capture.</summary>
public static class CaptureStanceProportion
{
    public sealed record Result(float HalfWidthCorrection,int Samples);
    public static Result Apply(List<XForm[]> frames,SourceScene source,MappingResult mapping,TargetRig target,FootChain left,FootChain right)
    {
        if(frames.Count==0)return new(0,0);
        var rig=target.Skeleton;var sourceRest=source.Skeleton.RestWorld;
        if(!mapping.RoleToBone.TryGetValue(BoneRole.UpperLegL,out var hipL)||!mapping.RoleToBone.TryGetValue(BoneRole.UpperLegR,out var hipR)||
            !mapping.RoleToBone.TryGetValue(BoneRole.LowerLegL,out var kneeL)||!mapping.RoleToBone.TryGetValue(BoneRole.FootL,out var footL))return new(0,0);
        var sourceLeg=Vector3.Distance(sourceRest[hipL].Pos,sourceRest[kneeL].Pos)+Vector3.Distance(sourceRest[kneeL].Pos,sourceRest[footL].Pos);
        var targetRest=rig.RestWorld;
        var targetLeg=Vector3.Distance(targetRest[left.Hip].Pos,targetRest[left.Knee].Pos)+Vector3.Distance(targetRest[left.Knee].Pos,targetRest[left.Ankle].Pos);
        if(!(sourceLeg>1e-4f)||!(targetLeg>1e-4f))return new(0,0);
        // Half the hip-joint spacing each skeleton has per unit of its own leg, in target units.
        var correction=(Vector3.Distance(targetRest[left.Hip].Pos,targetRest[right.Hip].Pos)/targetLeg
            -Vector3.Distance(sourceRest[hipL].Pos,sourceRest[hipR].Pos)/sourceLeg)*targetLeg*.5f;
        if(!float.IsFinite(correction)||MathF.Abs(correction)<targetLeg*.005f)return new(0,0);
        var world=new XForm[rig.Count];var samples=0;
        foreach(var frame in frames)
        {
            foreach(var (leg,other) in new[]{(left,right),(right,left)})
            {
                FkUtil.ToWorld(frame,rig,world);
                var lateral=world[leg.Hip].Pos-world[other.Hip].Pos;if(lateral.LengthSquared()<1e-8f)continue;
                lateral=Vector3.Normalize(lateral);
                var hip=world[leg.Hip];var knee=world[leg.Knee];var ankle=world[leg.Ankle];var footRotation=ankle.Rot;
                var goal=ankle.Pos-lateral*correction;
                // Never ask for more than the leg can reach; the foot keeps its direction from the hip.
                var reach=(Vector3.Distance(hip.Pos,knee.Pos)+Vector3.Distance(knee.Pos,ankle.Pos))*.9995f;
                var fromHip=goal-hip.Pos;if(fromHip.Length()>reach)goal=hip.Pos+Vector3.Normalize(fromHip)*reach;
                var bend=Vector3.Cross(knee.Pos-hip.Pos,ankle.Pos-knee.Pos);
                if(bend.LengthSquared()<1e-8f)bend=Vector3.Transform(Vector3.UnitX,hip.Rot);
                var ik=TwoBoneIk.Solve(hip.Pos,knee.Pos,ankle.Pos,goal,soften:0,stableBendAxis:bend);
                EffectorIk.ApplyWorldDeltas(frame,rig,leg.Hip,leg.Knee,leg.Ankle,ik.UpperWorldDelta,ik.LowerWorldDelta,world);
                FkUtil.ToWorld(frame,rig,world);
                var parent=rig[leg.Ankle].ParentIndex;
                frame[leg.Ankle].Rot=Quaternion.Normalize((parent<0?Quaternion.Identity:Quaternion.Inverse(world[parent].Rot))*footRotation);
                samples++;
            }
        }
        return new(correction,samples);
    }
}
