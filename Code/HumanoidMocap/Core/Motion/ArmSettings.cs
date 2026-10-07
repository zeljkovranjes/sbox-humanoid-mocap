using System;
using System.Numerics;
using HumanoidMocap.Core.Maths;

namespace HumanoidMocap.Core.Motion;
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
