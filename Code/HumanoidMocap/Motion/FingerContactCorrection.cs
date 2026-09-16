using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3=System.Numerics.Vector3;

/// <summary>Bounded hinge corrections on an already retargeted hand. Objects and wrists
/// are authoritative; only the three selected phalanx rotations can change.</summary>
public static class FingerContactCorrection
{
    sealed record Chain(BoneRole Role,int[] Bones,Vector3[] Axes);
    sealed record Goal(Vector3 Point,Vector3 Target,float Weight,float Limit);
    public static bool IsDistal(BoneRole role)=>role is BoneRole.ThumbDistL or BoneRole.IndexDistL or BoneRole.MiddleDistL
        or BoneRole.RingDistL or BoneRole.PinkyDistL or BoneRole.ThumbDistR or BoneRole.IndexDistR or BoneRole.MiddleDistR or BoneRole.RingDistR or BoneRole.PinkyDistR;
    public static Vector3 TargetAt(ContactInterval contact,FingerContactTarget target,double time)=>MotionDocument.V(target.LocalTarget)
        +(contact.Sliding?ContactSolver.LocalTarget(contact,time)-MotionDocument.V(target.SlideOrigin??contact.LocalTarget):Vector3.Zero);

    public static Vector3? BoneEndpoint(TargetRig rig,BoneRole role,TargetUpAxis axis)
    {
        if(!IsDistal(role)||rig.BoneForRole(role) is not int b||rig.TailWorldOf(b) is not Vector3 tail)return null;
        var local=XForm.Compose(rig.Skeleton.RestWorld[b].Inverse(),new(tail,Quaternion.Identity)).Pos;
        return local/(axis==TargetUpAxis.ZUpEngine?39.3700787f:100f);
    }

    public static void Apply(List<XForm[]> frames,TargetRig rig,TargetUpAxis axis,TargetCorrectionSettings settings,PropContactMotion contacts,float fps)
    {
        if(string.IsNullOrEmpty(settings.ContactTargetKey))return;
        var bindings=contacts.Contacts.Where(c=>c.Review==ContactReview.Confirmed)
            .SelectMany(c=>c.FingerTargets.Where(t=>t.TargetKey==settings.ContactTargetKey).Select(t=>(Contact:c,Target:t))).ToArray();
        if(bindings.Length==0)return;
        if((long)bindings.Length*frames.Count>1_000_000)throw new ArgumentException("Too many finger contact intervals for this clip. Use a shorter range or fewer intervals.");
        var map=new MappingResult("Target contact anatomy",MappingSource.Authored);
        foreach(var bone in rig.Skeleton.Bones)if(rig.RoleOf(bone.Index) is {} role)map.RoleToBone[role]=bone.Index;
        var rest=rig.Skeleton.RestWorld;var chains=new List<Chain>();
        foreach(var role in bindings.Select(b=>b.Target.Role).Distinct().OrderBy(r=>r))
        {
            var name=role.ToString();var side=name.EndsWith("L",StringComparison.Ordinal)?"L":"R";var stem=name[..^5];
            var roles=new[]{stem+"Prox"+side,stem+"Mid"+side,stem+"Dist"+side}.Select(Enum.Parse<BoneRole>).ToArray();
            var bones=roles.Select(r=>rig.BoneForRole(r)??-1).ToArray();
            if(bones.Any(b=>b<0)||rig.Skeleton[bones[1]].ParentIndex!=bones[0]||rig.Skeleton[bones[2]].ParentIndex!=bones[1])
                throw new ArgumentException("Finger contact needs a directly connected proximal/middle/distal chain: "+role);
            var dorsal=HandGeometry.Dorsal(map,rest,side=="L")??throw new ArgumentException("Finger contact needs a nondegenerate target palm.");
            var axes=new Vector3[3];
            for(var j=0;j<3;j++)
            {
                var direction=j<2?rest[bones[j+1]].Pos-rest[bones[j]].Pos:rest[bones[2]].Pos-rest[bones[1]].Pos;
                var hinge=Vector3.Cross(dorsal,direction);
                if(hinge.LengthSquared()<1e-10f)throw new ArgumentException("Degenerate finger contact hinge: "+roles[j]);
                axes[j]=Vector3.Transform(Vector3.Normalize(hinge),Quaternion.Inverse(rest[bones[j]].Rot));
            }
            chains.Add(new(role,bones,axes));
        }
        // Each active chain uses at most 19 whole-skeleton FK evaluations per frame.
        if((long)frames.Count*chains.Count*rig.Skeleton.Count*19>50_000_000)
            throw new ArgumentException("Finger contact correction exceeds its work budget. Use a shorter range or fewer constrained fingers.");
        var placement=CapturePlacement.ForTarget(axis,settings);var world=new XForm[rig.Skeleton.Count];
        var fade=new ContactSettings();
        for(var f=0;f<frames.Count;f++)
        {
            var time=Math.Min(contacts.StartTime+f/(double)fps,contacts.EndTime);var frame=frames[f];
            foreach(var chain in chains)
            {
                var goals=new List<Goal>();
                foreach(var binding in bindings.Where(b=>b.Target.Role==chain.Role).OrderBy(b=>b.Contact.Object,StringComparer.Ordinal).ThenBy(b=>b.Contact.ObjectBone,StringComparer.Ordinal).ThenBy(b=>b.Contact.Start))
                {
                    var weight=ContactSolver.Weight(binding.Contact,time,fade);if(weight<=0)continue;
                    if(!contacts.TrySample(binding.Contact.Object,time,out var props,out var available))continue;
                    var prop=contacts.Objects.Single(p=>p.Id==binding.Contact.Object);
                    var bone=string.IsNullOrEmpty(binding.Contact.ObjectBone)?0:prop.Bones.FindIndex(b=>b.Name==binding.Contact.ObjectBone);
                    if(bone<0||!available[bone])continue;
                    var t=binding.Target;
                    goals.Add(new(MotionDocument.V(t.LocalPoint)*placement.Units,
                        placement.Transform(XForm.Compose(props[bone],new(TargetAt(binding.Contact,t,time),Quaternion.Identity))).Pos,
                        weight,t.MaximumDegrees*MathF.PI/180));
                }
                if(goals.Count==0)continue;
                var original=chain.Bones.Select(b=>frame[b].Rot).ToArray();var angles=new float[3];
                var limit=goals.Min(g=>g.Limit);var activation=Math.Min(1,goals.Sum(g=>g.Weight));
                FkUtil.ToWorld(frame,rig.Skeleton,world);
                for(var iteration=0;iteration<6;iteration++)for(var j=2;j>=0;j--)
                {
                    var bone=chain.Bones[j];var pivot=world[bone].Pos;var hinge=Vector3.Transform(chain.Axes[j],world[bone].Rot);
                    float cross=0,dot=0;
                    foreach(var goal in goals)
                    {
                        var point=XForm.Compose(world[chain.Bones[2]],new(goal.Point,Quaternion.Identity)).Pos-pivot;
                        var target=goal.Target-pivot;point-=hinge*Vector3.Dot(point,hinge);target-=hinge*Vector3.Dot(target,hinge);
                        cross+=Vector3.Dot(hinge,Vector3.Cross(point,target))*goal.Weight;dot+=Vector3.Dot(point,target)*goal.Weight;
                    }
                    if(!float.IsFinite(cross)||!float.IsFinite(dot))throw new ArgumentException("Finger contact values exceed the numeric range. Check point and object coordinates in metres.");
                    if(Math.Abs(cross)+Math.Abs(dot)<1e-10f)continue;
                    angles[j]=Math.Clamp(angles[j]+MathF.Atan2(cross,dot),-limit,limit);
                    frame[bone].Rot=Quaternion.Normalize(original[j]*Quaternion.CreateFromAxisAngle(chain.Axes[j],angles[j]));
                    FkUtil.ToWorld(frame,rig.Skeleton,world);
                }
                for(var j=0;j<3;j++)frame[chain.Bones[j]].Rot=Quaternion.Normalize(Quaternion.Slerp(original[j],frame[chain.Bones[j]].Rot,activation));
            }
        }
    }
}
