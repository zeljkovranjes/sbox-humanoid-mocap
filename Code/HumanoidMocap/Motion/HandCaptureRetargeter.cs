using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Retargets an observed hand forest without fabricating a source body.
/// Uses the retargeter's anatomical hand geometry and absolute canonical-frame
/// transfer, then solves target arms against the captured wrist positions.</summary>
public static class HandCaptureRetargeter
{
    public const string ReachWarningPrefix="Target arm reach limited";
    public const string MissingTargetTracksPrefix="Target hand mapping omits captured joints";
    public static bool Supports(MotionDocument document)=>document.Space==MotionSpace.CameraRelative&&
        document.Bones.Any(b=>b.Role is BoneRole.HandL or BoneRole.HandR)&&
        document.Bones.All(b=>b.Role is BoneRole.HandL or BoneRole.HandR||b.Role is { } role&&FingerSolver.IsFingerRole(role));
    public static bool Supports(SourceScene source,MappingResult mapping)=>
        source.CaptureSpace==MotionSpace.CameraRelative&&source.CaptureEvidence is not null&&
        mapping.RoleToBone.Keys.Any(r=>r is BoneRole.HandL or BoneRole.HandR)&&
        mapping.RoleToBone.Keys.All(r=>r is BoneRole.HandL or BoneRole.HandR||FingerSolver.IsFingerRole(r));

    sealed record Transfer(int Source,int Target,bool Left,Quaternion Offset);

    /// <summary>Exact preset aliases for partial hand rigs, whose absent body roles
    /// deliberately fail the full-humanoid confidence threshold. Tied, conflicting
    /// profiles require manual mapping instead of guessing finger numbering.</summary>
    public static MappingResult? DetectTargetHandMapping(HumanoidMocap.Skeleton.Skeleton skeleton)
        =>DetectTargetHandMapping(skeleton,out _);

    public static MappingResult? DetectTargetHandMapping(HumanoidMocap.Skeleton.Skeleton skeleton,out bool ambiguous)
    {
        ambiguous=false;
        var candidates=ProfileLibrary.All.Select(p=>ProfileDetector.Apply(p,skeleton))
            .Where(m=>HasTargetHandGeometry(skeleton,m))
            .Select(m=>(Map:m,Count:m.RoleToBone.Keys.Count(r=>FingerSolver.IsFingerRole(r)||r is BoneRole.HandL or BoneRole.HandR)))
            .OrderByDescending(c=>c.Count).ToArray();
        if(candidates.Length==0)return null;
        var best=candidates[0];
        foreach(var candidate in candidates.Skip(1).TakeWhile(c=>c.Count==best.Count))
            if(candidate.Map.RoleToBone.Count!=best.Map.RoleToBone.Count||
                candidate.Map.RoleToBone.Any(p=>!best.Map.RoleToBone.TryGetValue(p.Key,out var b)||b!=p.Value))
            {ambiguous=true;return null;}
        best.Map.Notes.Add("Exact hand-profile aliases selected for a partial target; full-body mapping confidence is not applicable.");
        return best.Map;
    }

    /// <summary>Whether a partial target has enough mapped rest geometry for hand
    /// transfer. A torso and legs are unnecessary; each mapped hand needs a palm
    /// plane and usable finger segments. This does not validate body retargeting.</summary>
    public static bool HasTargetHandGeometry(HumanoidMocap.Skeleton.Skeleton skeleton,MappingResult mapping)
    {
        if(mapping.RoleToBone.Values.Any(i=>i<0||i>=skeleton.Count)||
            mapping.RoleToBone.Values.Distinct().Count()!=mapping.RoleToBone.Count)return false;
        var found=false;
        try
        {
            foreach(var left in new[]{true,false})
            {
                var handRole=left?BoneRole.HandL:BoneRole.HandR;
                if(!mapping.RoleToBone.TryGetValue(handRole,out var hand))continue;
                found=true;
                _=Frame(mapping,skeleton.RestWorld,handRole,left);
                foreach(var (role,bone) in mapping.RoleToBone)
                {
                    if(!FingerSolver.IsFingerRole(role)||role.ToString().EndsWith("L",StringComparison.Ordinal)!=left)continue;
                    var ancestor=skeleton[bone].ParentIndex;
                    while(ancestor>=0&&ancestor!=hand)ancestor=skeleton[ancestor].ParentIndex;
                    if(ancestor!=hand)return false;
                    _=Frame(mapping,skeleton.RestWorld,role,left);
                }
            }
        }
        catch(ArgumentException){return false;}
        return found;
    }

    public static Clip Solve(SourceScene source,MappingResult mapping,TargetRig target,TargetUpAxis axis,
        TargetCorrectionSettings settings,int take,string name,bool fingers=true,Action<string>? diagnostic=null)
    {
        if(!Supports(source,mapping)||take!=0)throw new ArgumentException("Expected a camera-relative hand motion document.");
        if(!Finite(settings.CaptureCameraPosition)||!float.IsFinite(settings.CaptureCameraYawDegrees)||!float.IsFinite(settings.CaptureCameraPitchDegrees))
            throw new ArgumentException("Capture camera placement must be finite.");
        var input=source.Clips[take];var evidence=source.CaptureEvidence!;
        if(evidence.Count!=input.Frames.Count)throw new ArgumentException("Hand evidence does not match the clip sample grid.");
        WristPositionOffsets.Validate(settings.WristOffsets);
        var clipStart=source.CaptureContacts?.StartTime??0;
        var clipEnd=source.CaptureContacts?.EndTime??clipStart+(input.Frames.Count-1)/(double)input.Fps;
        foreach(var edit in settings.WristOffsets)
        {
            if(edit.Start<clipStart||edit.End>clipEnd||!mapping.RoleToBone.TryGetValue(edit.Hand,out var bone))
                throw new ArgumentException("Wrist correction must identify a captured hand and lie within this clip.");
            if(edit.Enabled&&!Enumerable.Range(0,input.Frames.Count).Any(f=>clipStart+f/(double)input.Fps<=edit.Start&&evidence[f][bone]==JointEvidence.Reconstructed))
                throw new ArgumentException("Wrist correction needs an earlier observed hand pose. It cannot create a never-tracked hand.");
        }
        var targetMap=new MappingResult("Target hand anatomy",MappingSource.Authored);
        foreach(var bone in target.Skeleton.Bones)if(target.RoleOf(bone.Index) is { } role)targetMap.RoleToBone[role]=bone.Index;
        var sourceRest=source.Skeleton.RestWorld;var targetRest=target.Skeleton.RestWorld;
        var plans=new List<Transfer>();
        var mappedHands=new Dictionary<bool,(int Source,int Target)>();
        foreach(var left in new[]{true,false})
        {
            var handRole=left?BoneRole.HandL:BoneRole.HandR;
            if(!mapping.RoleToBone.TryGetValue(handRole,out var sourceHand)||target.BoneForRole(handRole) is not int targetHand)continue;
            mappedHands[left]=(sourceHand,targetHand);
            foreach(var (role,sourceBone) in mapping.RoleToBone)
            {
                if(role!=handRole&&(!fingers||!FingerSolver.IsFingerRole(role)||role.ToString().EndsWith("L",StringComparison.Ordinal)!=left))continue;
                if(target.BoneForRole(role) is not int targetBone)continue;
                var sourceFrame=Frame(mapping,sourceRest,role,left);
                var targetFrame=Frame(targetMap,targetRest,role,left);
                var offset=Quaternion.Normalize(Quaternion.Inverse(sourceRest[sourceBone].Rot)*sourceFrame*
                    Quaternion.Inverse(targetFrame)*targetRest[targetBone].Rot);
                plans.Add(new(sourceBone,targetBone,left,offset));
            }
        }
        if(mappedHands.Count==0)throw new ArgumentException("Map at least one target hand and its finger joints.");
        if(diagnostic is not null)
        {
            var omitted=new List<string>();
            foreach(var left in new[]{true,false})
            {
                var suffix=left?"L":"R";var label=left?"left":"right";
                bool Observed(BoneRole role)=>mapping.RoleToBone.TryGetValue(role,out var b)&&
                    evidence.Any(frame=>frame[b]==JointEvidence.Reconstructed);
                var handRole=left?BoneRole.HandL:BoneRole.HandR;
                if(target.BoneForRole(handRole) is null)
                {
                    if(Observed(handRole))omitted.Add(label+" hand");
                    continue;
                }
                if(!fingers)continue;
                foreach(var finger in new[]{"Thumb","Index","Middle","Ring","Pinky"})
                    if(mapping.RoleToBone.Keys.Any(role=>role.ToString().StartsWith(finger,StringComparison.Ordinal)&&
                        role.ToString().EndsWith(suffix,StringComparison.Ordinal)&&target.BoneForRole(role) is null&&Observed(role)))
                        omitted.Add(label+" "+finger.ToLowerInvariant());
            }
            if(omitted.Count>0)diagnostic(MissingTargetTracksPrefix+" in: "+string.Join(", ",omitted)+
                ". Their motion cannot be included in this target's export. Map those joints or choose a compatible target; the original capture retains those tracks.");
        }
        var capturePlacement=CapturePlacement.ForTarget(axis,settings);
        var placement=capturePlacement.Rotation;
        var sourceWorld=new XForm[source.Skeleton.Count];var targetWorld=new XForm[target.Skeleton.Count];
        var previous=target.Skeleton.Bones.Select(b=>b.RestLocal).ToArray();
        var seen=new HashSet<bool>();var targets=new List<Dictionary<BoneRole,XForm>>();
        var output=new Clip(name,input.Fps,input.Looping);
        var contactSettings=new ContactSettings();
        for(var f=0;f<input.Frames.Count;f++)
        {
            FkUtil.ToWorld(input.Frames[f],source.Skeleton,sourceWorld);
            foreach(var (left,hand) in mappedHands)
                if(evidence[f][hand.Source]!=JointEvidence.Unobserved)seen.Add(left);
            var desired=new Dictionary<int,Quaternion>();
            foreach(var plan in plans)if(seen.Contains(plan.Left))
                desired[plan.Target]=Quaternion.Normalize(placement*sourceWorld[plan.Source].Rot*plan.Offset);
            var frame=previous.ToArray();var wristTargets=new Dictionary<BoneRole,XForm>();
            foreach(var (left,hand) in mappedHands)if(seen.Contains(left))
            {
                var captured=new XForm(sourceWorld[hand.Source].Pos/100,sourceWorld[hand.Source].Rot);
                var time=Math.Min(clipStart+f/(double)input.Fps,clipEnd);
                captured.Pos+=WristPositionOffsets.Sample(settings.WristOffsets,left?BoneRole.HandL:BoneRole.HandR,time,clipStart,clipEnd);
                if(source.CaptureContacts is { } contacts)
                {
                    var corrected=contacts.ApplyWristPose(source.Skeleton[hand.Source].Name,captured,
                        time,contactSettings);
                    var delta=Quaternion.Normalize(placement*corrected.Rot*Quaternion.Inverse(captured.Rot)*Quaternion.Inverse(placement));
                    // Rotate the whole hand together: local finger articulation stays captured.
                    foreach(var plan in plans.Where(p=>p.Left==left))desired[plan.Target]=Quaternion.Normalize(delta*desired[plan.Target]);
                    captured=corrected;
                }
                var position=capturePlacement.Transform(captured).Pos;
                wristTargets[left?BoneRole.HandL:BoneRole.HandR]=new(position,desired[hand.Target]);
            }
            for(var i=0;i<frame.Length;i++)
            {
                var parent=target.Skeleton[i].ParentIndex;
                var parentWorld=parent<0?XForm.Identity:targetWorld[parent];
                if(desired.TryGetValue(i,out var rotation))frame[i].Rot=Quaternion.Normalize(Quaternion.Inverse(parentWorld.Rot)*rotation);
                targetWorld[i]=parent<0?frame[i]:XForm.Compose(parentWorld,frame[i]);
            }
            output.Frames.Add(frame);targets.Add(wristTargets);previous=frame;
        }
        TargetCorrections.Apply(output.Frames,target,axis,settings,targets);
        if(diagnostic is not null)
        {
            var observed=0;var limited=0;var maximumCm=0f;
            var toCm=axis==TargetUpAxis.ZUpEngine?2.54f:1f;
            for(var f=0;f<output.Frames.Count;f++)
            {
                FkUtil.ToWorld(output.Frames[f],target.Skeleton,targetWorld);
                foreach(var (left,hand) in mappedHands)
                {
                    if(evidence[f][hand.Source]!=JointEvidence.Reconstructed||!targets[f].TryGetValue(left?BoneRole.HandL:BoneRole.HandR,out var goal))continue;
                    observed++;
                    var error=Vector3.Distance(targetWorld[hand.Target].Pos,goal.Pos)*toCm;
                    if(error<=.1f)continue; // ignore sub-millimetre numerical differences
                    limited++;maximumCm=Math.Max(maximumCm,error);
                }
            }
            if(limited>0)diagnostic(FormattableString.Invariant($"{ReachWarningPrefix} {limited}/{observed} observed wrist targets by more than 1 mm; maximum displacement {maximumCm:F1} cm. Bone lengths were preserved, but these wrist trajectories could not be reproduced. Review camera/depth assumptions and target placement. This is target IK displacement, not measured reconstruction accuracy."));
        }
        if(source.CaptureContacts is {} finalContacts)FingerContactCorrection.Apply(output.Frames,target,axis,settings,finalContacts,input.Fps);
        return output;
    }

    static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);

    static Quaternion Frame(MappingResult map,IReadOnlyList<XForm> rest,BoneRole role,bool left)
    {
        var index=map.RoleToBone[role];Vector3 primary;
        if(role is BoneRole.HandL or BoneRole.HandR)
            primary=(HandGeometry.FingerProximalMidpoint(map,rest,left)??throw new ArgumentException("Hand mapping needs finger proximal joints."))-rest[index].Pos;
        else
        {
            var name=role.ToString();var side=left?"L":"R";
            var stem=name[..^1];string next,previous;
            if(stem.EndsWith("Meta",StringComparison.Ordinal)){next=stem[..^4]+"Prox";previous="Hand";}
            else if(stem.EndsWith("Prox",StringComparison.Ordinal)){next=stem[..^4]+"Mid";previous="Hand";}
            else if(stem.EndsWith("Mid",StringComparison.Ordinal)){next=stem[..^3]+"Dist";previous=stem[..^3]+"Prox";}
            else {next="";previous=stem[..^4]+"Mid";}
            if(next.Length>0&&map.RoleToBone.TryGetValue(Enum.Parse<BoneRole>(next+side),out var child))primary=rest[child].Pos-rest[index].Pos;
            else if(map.RoleToBone.TryGetValue(Enum.Parse<BoneRole>(previous+side),out var parent))primary=rest[index].Pos-rest[parent].Pos;
            else throw new ArgumentException("Finger mapping needs adjacent phalanges: "+role);
        }
        if(primary.LengthSquared()<1e-8f)throw new ArgumentException("Degenerate hand segment: "+role);
        var x=Vector3.Normalize(primary);
        var dorsal=HandGeometry.Dorsal(map,rest,left)??throw new ArgumentException("Hand mapping needs a nondegenerate index-to-pinky knuckle line.");
        var z=dorsal-x*Vector3.Dot(dorsal,x);
        z=z.LengthSquared()<1e-8f?MathQ.Perpendicular(x):Vector3.Normalize(z);
        var y=Vector3.Cross(z,x);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1)));
    }
}
