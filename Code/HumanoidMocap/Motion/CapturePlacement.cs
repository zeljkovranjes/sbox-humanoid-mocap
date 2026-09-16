using System;
using System.Numerics;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>The same explicit camera placement for captured hands and authoritative props.</summary>
public readonly record struct CapturePlacement(float Units,Quaternion Rotation,Vector3 Position)
{
    public static CapturePlacement ForTarget(TargetUpAxis axis,TargetCorrectionSettings settings)
    {
        var units=axis==TargetUpAxis.ZUpEngine?39.3700787f:100f;
        var axisRotation=axis==TargetUpAxis.YUpCm?Quaternion.Identity:Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2);
        var camera=Quaternion.CreateFromYawPitchRoll(settings.CaptureCameraYawDegrees*MathF.PI/180,
            settings.CaptureCameraPitchDegrees*MathF.PI/180,0);
        return new(units,Quaternion.Normalize(axisRotation*camera),Vector3.Transform(settings.CaptureCameraPosition*units,axisRotation));
    }
    public XForm Transform(XForm capture)=>new(Vector3.Transform(capture.Pos*Units,Rotation)+Position,
        Quaternion.Normalize(Rotation*capture.Rot));
}
