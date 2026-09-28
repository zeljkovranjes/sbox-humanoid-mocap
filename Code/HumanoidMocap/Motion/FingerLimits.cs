using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Keeps a capture's fingers within what fingers can do, with angle limits in the manner of Spurr et al.,
/// "Weakly supervised 3D hand pose estimation via biomechanical constraints" (Hand-BMC). The hand network, working
/// from a small blurred crop of each hand, bent middle knuckles up to 14 degrees backwards and turned finger joints
/// about their own length, which a character shows as broken fingers. Each finger joint's rotation is split into
/// a bend toward the palm, a sideways bend and a turn about the finger: the turn is dropped, the sideways bend is
/// kept only at the knuckles, and both bends are held within <see cref="Knuckle"/>, <see cref="Middle"/> and
/// <see cref="Tip"/>. Thumbs are left as captured: their joints do not bend toward the palm.</summary>
public static class FingerLimits
{
    /// <summary>Bend toward the palm (negative: backwards) and sideways bend either way, degrees.</summary>
    public readonly record struct Limit(float Back,float Forward,float Sideways);
    public static readonly Limit Knuckle=new(30,100,25),Middle=new(5,115,5),Tip=new(15,90,5);
    static readonly string[] Fingers={"Index","Middle","Ring","Pinky"};

    /// <returns>The number of finger joint samples changed by more than a degree.</returns>
    public static int Apply(MotionDocument motion)
    {
        var bones=motion.Bones;int Role(string name)=>Enum.TryParse<BoneRole>(name,out var role)?bones.FindIndex(b=>b.Role==role):-1;
        int hips=Role("Hips"),head=Role("Head");
        if(hips<0||head<0||motion.Frames.Count==0)return 0;
        var restRotation=new Quaternion[bones.Count];var restPosition=new Vector3[bones.Count];
        for(var i=0;i<bones.Count;i++)
        {
            var parent=bones[i].Parent;var local=Quaternion.Normalize(MotionDocument.Q(bones[i].RestRotation));var offset=MotionDocument.V(bones[i].RestPosition);
            if(parent<0){restRotation[i]=local;restPosition[i]=offset;}
            else{restRotation[i]=Quaternion.Normalize(restRotation[parent]*local);restPosition[i]=restPosition[parent]+Vector3.Transform(offset,restRotation[parent]);}
        }
        // The rest pose holds the arms out with the palms down.
        var palm=restPosition[hips]-restPosition[head];if(palm.LengthSquared()<1e-8f)return 0;palm=Vector3.Normalize(palm);
        var changed=0;
        foreach(var side in new[]{"L","R"})foreach(var finger in Fingers)
        {
            var chain=new[]{Role(finger+"Prox"+side),Role(finger+"Mid"+side),Role(finger+"Dist"+side)};
            if(chain.Any(b=>b<0)||bones[chain[1]].Parent!=chain[0]||bones[chain[2]].Parent!=chain[1])continue;
            for(var k=0;k<3;k++)
            {
                var bone=chain[k];var limit=k==0?Knuckle:k==1?Middle:Tip;
                // The segment the joint turns: toward the next joint, or the last segment's own direction.
                var along=MotionDocument.V(bones[k<2?chain[k+1]:bone].RestPosition);if(along.LengthSquared()<1e-10f)continue;
                along=Vector3.Normalize(along);
                var toward=Vector3.Transform(palm,Quaternion.Inverse(restRotation[bone]));toward-=along*Vector3.Dot(toward,along);
                if(toward.LengthSquared()<1e-8f)continue;
                var bend=Vector3.Normalize(Vector3.Cross(along,Vector3.Normalize(toward)));var sideways=Vector3.Cross(bend,along);
                foreach(var frame in motion.Frames)
                {
                    var q=Quaternion.Normalize(MotionDocument.Q(frame.Rotations[bone]));if(q.W<0)q=-q;
                    // Swing: the rotation carrying the segment to where it points, without its turn about itself.
                    var to=Vector3.Transform(along,q);var swing=Between(along,to);
                    var axis=new Vector3(swing.X,swing.Y,swing.Z);var sin=axis.Length();var angle=2*MathF.Atan2(sin,swing.W);
                    var vector=sin>1e-9f?axis/sin*angle:Vector3.Zero;
                    var forward=Vector3.Dot(vector,bend)*180/MathF.PI;var across=Vector3.Dot(vector,sideways)*180/MathF.PI;
                    var limited=bend*(Math.Clamp(forward,-limit.Back,limit.Forward)*MathF.PI/180)+sideways*(Math.Clamp(across,-limit.Sideways,limit.Sideways)*MathF.PI/180);
                    var length=limited.Length();
                    var result=length>1e-9f?Quaternion.CreateFromAxisAngle(limited/length,length):Quaternion.Identity;
                    if(MotionCleanup.Angle(q,result)>MathF.PI/180)changed++;
                    frame.Rotations[bone]=new[]{result.X,result.Y,result.Z,result.W};
                }
            }
        }
        return changed;
    }
    /// <summary>The shortest rotation taking unit vector <paramref name="from"/> to unit vector <paramref name="to"/>.</summary>
    static Quaternion Between(Vector3 from,Vector3 to)
    {
        var dot=Vector3.Dot(from,to);
        if(dot<-0.999999f)
        {
            var axis=Vector3.Cross(Vector3.UnitX,from);if(axis.LengthSquared()<1e-6f)axis=Vector3.Cross(Vector3.UnitY,from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),MathF.PI);
        }
        var cross=Vector3.Cross(from,to);
        return Quaternion.Normalize(new Quaternion(cross.X,cross.Y,cross.Z,1+dot));
    }
}
