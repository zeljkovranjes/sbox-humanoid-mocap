using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;

public sealed class ContactSettings
{
    public float EnterDistance { get; set; } = .025f;
    public float ExitDistance { get; set; } = .05f;
    public float MaximumRelativeSpeed { get; set; } = .15f;
    public double MinimumPersistence { get; set; } = .12;
    public double ReleasePersistence { get; set; } = .08;
    public double BlendSeconds { get; set; } = .08;
    public double MaximumSampleGap { get; set; } = .1;
}
