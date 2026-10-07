using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Keeps elbows and knees bending on their hinge. The body network places the wrist and ankle well but
/// splits a limb's turn between the upper bone's roll and a sideways bend at the elbow or knee: on dance and tennis
/// clips elbows bent up to 90 degrees sideways, which a character's skin shows as a broken arm. Rolling the upper
/// arm or thigh about its own length moves no joint, so each frame's excess over <see cref="ToleranceDegrees"/> is
/// moved there: shoulder, elbow, wrist, hip, knee and ankle stay exactly where they were, and the forearm and shin
/// keep their orientation, so hands and feet do not turn.</summary>
public static class LimbHinges
{
    /// <summary>How far the bend may lean off the anatomical hinge before it is corrected. The body model's own
    /// joint axes sit 20 to 30 degrees off it in ordinary motion; only the excess is removed.</summary>
    public const float ToleranceDegrees=35;
    /// <summary>A nearly straight limb has no bend to place: corrections fade in between these bends (degrees).</summary>
    const float StraightDegrees=10,BentDegrees=25;
    /// <summary>A bend leaning further than this off the hinge is folded backwards, not sideways; it is left alone.</summary>
    const float BackwardFromDegrees=120,BackwardDegrees=150;
    static readonly (BoneRole Upper,BoneRole Lower,BoneRole End,float Bend)[] Limbs=
    {
        (BoneRole.UpperArmL,BoneRole.LowerArmL,BoneRole.HandL,1),(BoneRole.UpperArmR,BoneRole.LowerArmR,BoneRole.HandR,1),
        (BoneRole.UpperLegL,BoneRole.LowerLegL,BoneRole.FootL,-1),(BoneRole.UpperLegR,BoneRole.LowerLegR,BoneRole.FootR,-1),
    };

    /// <returns>The number of limb frames changed and the largest roll given, degrees.</returns>
    public static (int Changed,float LargestDegrees) Apply(MotionDocument motion)
    {
        var bones=motion.Bones;int Role(BoneRole role)=>bones.FindIndex(b=>b.Role==role);
        int hips=Role(BoneRole.Hips),head=Role(BoneRole.Head),legL=Role(BoneRole.UpperLegL),legR=Role(BoneRole.UpperLegR);
        if(hips<0||head<0||legL<0||legR<0||motion.Frames.Count==0)return (0,0);
        // The rest pose's world rotations and positions, and which way the body faces in it.
        var restRotation=new Quaternion[bones.Count];var restPosition=new Vector3[bones.Count];
        for(var i=0;i<bones.Count;i++)
        {
            var parent=bones[i].Parent;var local=Quaternion.Normalize(MotionDocument.Q(bones[i].RestRotation));var offset=MotionDocument.V(bones[i].RestPosition);
            if(parent<0){restRotation[i]=local;restPosition[i]=offset;}
            else{restRotation[i]=Quaternion.Normalize(restRotation[parent]*local);restPosition[i]=restPosition[parent]+Vector3.Transform(offset,restRotation[parent]);}
        }
        var forward=Vector3.Cross(restPosition[head]-restPosition[hips],restPosition[legR]-restPosition[legL]);
        if(forward.LengthSquared()<1e-8f)return (0,0);
        forward=Vector3.Normalize(forward);
        var changed=0;var largest=0f;var tolerance=ToleranceDegrees*MathF.PI/180;
        foreach(var (upperRole,lowerRole,endRole,bend) in Limbs)
        {
            int upper=Role(upperRole),lower=Role(lowerRole),end=Role(endRole);
            if(upper<0||lower<0||end<0||bones[lower].Parent!=upper||bones[end].Parent!=lower)continue;
            var along=MotionDocument.V(bones[lower].RestPosition);var below=MotionDocument.V(bones[end].RestPosition);
            if(along.LengthSquared()<1e-8f||below.LengthSquared()<1e-8f)continue;
            along=Vector3.Normalize(along);below=Vector3.Normalize(below);
            // The hinge in the upper bone's own frame: elbows fold the forearm forward, knees the shin back.
            var fold=Vector3.Transform(forward*bend,Quaternion.Inverse(restRotation[upper]));fold-=along*Vector3.Dot(fold,along);
            if(fold.LengthSquared()<1e-8f)continue;
            var hinge=Vector3.Normalize(Vector3.Cross(along,fold));
            foreach(var frame in motion.Frames)
            {
                var parentWorld=Quaternion.Identity;
                for(var b=bones[upper].Parent;b>=0;b=bones[b].Parent)parentWorld=Quaternion.Normalize(MotionDocument.Q(frame.Rotations[b])*parentWorld);
                var upperWorld=Quaternion.Normalize(parentWorld*MotionDocument.Q(frame.Rotations[upper]));
                var lowerWorld=Quaternion.Normalize(upperWorld*MotionDocument.Q(frame.Rotations[lower]));
                var u=Vector3.Transform(along,upperWorld);var l=Vector3.Transform(below,lowerWorld);
                var bent=MathF.Acos(Math.Clamp(Vector3.Dot(u,l),-1,1))*180/MathF.PI;
                var weight=Smooth(bent,StraightDegrees,BentDegrees);if(weight<=0)continue;
                // The signed angle about the upper bone from its hinge to the plane the limb actually bends in.
                var wanted=Vector3.Cross(u,l);wanted-=u*Vector3.Dot(wanted,u);
                var current=Vector3.Transform(hinge,upperWorld);current-=u*Vector3.Dot(current,u);
                if(wanted.LengthSquared()<1e-10f||current.LengthSquared()<1e-10f)continue;
                wanted=Vector3.Normalize(wanted);current=Vector3.Normalize(current);
                var off=MathF.Atan2(Vector3.Dot(Vector3.Cross(current,wanted),u),Vector3.Dot(current,wanted));
                weight*=1-Smooth(MathF.Abs(off)*180/MathF.PI,BackwardFromDegrees,BackwardDegrees);
                var roll=(off-Math.Clamp(off,-tolerance,tolerance))*weight;
                if(MathF.Abs(roll)<1e-4f)continue;
                var rolled=Quaternion.Normalize(Quaternion.CreateFromAxisAngle(u,roll)*upperWorld);
                frame.Rotations[upper]=Array(Quaternion.Normalize(Quaternion.Inverse(parentWorld)*rolled));
                frame.Rotations[lower]=Array(Quaternion.Normalize(Quaternion.Inverse(rolled)*lowerWorld));
                changed++;largest=Math.Max(largest,MathF.Abs(roll)*180/MathF.PI);
            }
        }
        return (changed,largest);
    }
    static float Smooth(float value,float from,float to){var w=Math.Clamp((value-from)/(to-from),0,1);return w*w*(3-2*w);}
    static float[] Array(Quaternion q)=>new[]{q.X,q.Y,q.Z,q.W};
}
