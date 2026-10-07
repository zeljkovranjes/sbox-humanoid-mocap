#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Tunables for the Kovar foot-plant cleanup pass.</summary>
public sealed class FootPlantOptions
{
    /// <summary>Ankle speed below which a frame can enter a plant (hysteresis exit at 1.5×).</summary>
    public float SpeedThresholdCmPerSec { get; set; } = 8f;

    /// <summary>Ankle height above ground below which a frame can enter a plant (exit at 1.5×).</summary>
    public float HeightThresholdCm { get; set; } = 4f;

    /// <summary>Minimum plant duration in frames; shorter candidate intervals are discarded.</summary>
    public int MinPlantFrames { get; set; } = 3;

    /// <summary>Frames before/after each plant over which corrections ease in/out.</summary>
    public int BlendFrames { get; set; } = 3;

    /// <summary>Maximum allowed per-segment length stretch (fraction; 0.02 = 2%).</summary>
    public float MaxStretch { get; set; } = 0.02f;
}
