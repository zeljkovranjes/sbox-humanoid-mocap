#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Per-foot results of a <see cref="FootPlant.Apply"/> run.</summary>
public sealed class FootPlantFootReport
{
    /// <summary>Detected plant intervals (inclusive frame ranges).</summary>
    public List<FrameRange> Plants { get; } = new();

    /// <summary>Largest rotation correction applied to any chain joint, degrees.</summary>
    public float MaxCorrectionDeg { get; set; }

    /// <summary>Largest world-space ankle displacement applied, centimeters.</summary>
    public float MaxCorrectionCm { get; set; }

    /// <summary>
    /// Largest frame-to-frame ankle movement remaining inside any plant after the pass,
    /// centimeters. Should be ~0 (the foot is pinned to its anchor).
    /// </summary>
    public float ResidualSlideCm { get; set; }
}
