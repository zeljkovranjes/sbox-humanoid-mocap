using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Reversible target edit in capture-camera metres. Never a reconstructed observation.</summary>
public sealed class WristPositionOffset
{
    public BoneRole Hand { get; set; } = BoneRole.HandR;
    public double Start { get; set; }
    public double End { get; set; }
    public float[] Offset { get; set; } = new float[3];
    public double FadeSeconds { get; set; } = .1;
    public bool Enabled { get; set; } = true;
    public WristPositionOffset Copy()=>new(){Hand=Hand,Start=Start,End=End,Offset=Offset.ToArray(),FadeSeconds=FadeSeconds,Enabled=Enabled};
}
