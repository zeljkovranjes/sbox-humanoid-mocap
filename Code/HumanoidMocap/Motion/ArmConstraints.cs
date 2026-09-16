using System;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

public sealed class ArmSettings
{
    public Vector3 Shoulder { get; set; } = new(.18f,-.15f,0);
    public Vector3 ElbowTarget { get; set; } = new(.4f,-.4f,.2f);
    public float UpperLength { get; set; } = .28f;
    public float ForearmLength { get; set; } = .25f;
    public float MaximumReach { get; set; } = .995f;
    public float MinimumElbowDegrees { get; set; } = 5;
    public float MaximumElbowDegrees { get; set; } = 155;
}
public readonly record struct ArmSolution(Vector3 Shoulder,Vector3 Elbow,Vector3 Wrist,
    Quaternion WristRotation,float ReachError,JointEvidence ShoulderEvidence,JointEvidence ElbowEvidence);

/// <summary>Stateful bend-plane stabilization. Wrist orientation is carried through unchanged.</summary>
public sealed class ArmConstraintSolver
{
    Vector3? previousBend;
    public void Reset()=>previousBend=null;
    public ArmSolution Solve(Vector3 wrist,Quaternion orientation,ArmSettings s)
    {
        if(s.UpperLength<=0 || s.ForearmLength<=0 || s.MaximumReach<=0 || s.MaximumReach>=1
            || s.MinimumElbowDegrees<0 || s.MaximumElbowDegrees>=180 || s.MinimumElbowDegrees>=s.MaximumElbowDegrees)
            throw new ArgumentException("Invalid arm lengths, reach or elbow limits.");
        var delta=wrist-s.Shoulder;var length=delta.Length();
        var direction=length>1e-7f?delta/length:Vector3.UnitZ;
        float Reach(float degrees)=>MathF.Sqrt(s.UpperLength*s.UpperLength+s.ForearmLength*s.ForearmLength
            +2*s.UpperLength*s.ForearmLength*MathF.Cos(degrees*MathF.PI/180));
        var distance=Math.Clamp(length,Reach(s.MaximumElbowDegrees),Math.Min(Reach(s.MinimumElbowDegrees),(s.UpperLength+s.ForearmLength)*s.MaximumReach));
        var bend=s.ElbowTarget-s.Shoulder;bend-=Vector3.Dot(bend,direction)*direction;
        if(bend.LengthSquared()<1e-8f && previousBend is { } old) bend=old-direction*Vector3.Dot(old,direction);
        if(bend.LengthSquared()<1e-8f)bend=MathQ.Perpendicular(direction);
        bend=Vector3.Normalize(bend);
        if(previousBend is { } last)
        {
            var projected=last-direction*Vector3.Dot(last,direction);
            if(projected.LengthSquared()>1e-8f)
            {
                projected=Vector3.Normalize(projected);
                // Limit abrupt bend-plane motion, including pole crossings.
                var angle=MathF.Acos(Math.Clamp(Vector3.Dot(projected,bend),-1,1));
                if(angle>.2f)
                {
                    var axis=Vector3.Cross(projected,bend);
                    if(axis.LengthSquared()<1e-8f)axis=direction;
                    bend=Vector3.Transform(projected,Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),.2f));
                }
            }
        }
        previousBend=bend;
        var along=(s.UpperLength*s.UpperLength-s.ForearmLength*s.ForearmLength+distance*distance)/(2*distance);
        var elbow=s.Shoulder+direction*along+bend*MathF.Sqrt(Math.Max(0,s.UpperLength*s.UpperLength-along*along));
        var end=s.Shoulder+direction*distance;
        return new(s.Shoulder,elbow,end,orientation,Vector3.Distance(end,wrist),JointEvidence.GeneratedIk,JointEvidence.GeneratedIk);
    }
}
