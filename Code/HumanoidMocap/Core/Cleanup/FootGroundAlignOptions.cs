#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Tunables for the grounded-foot stance recalibration pass.</summary>
public sealed class FootGroundAlignOptions
{
    /// <summary>
    /// Dead zone (degrees): measured stance offsets at or below this are genuine planted
    /// articulation (heel-roll bias, natural lean — measured 2–4° on well-rested rigs and
    /// on citizen clips) and are left untouched, keeping the transfer byte-faithful there.
    /// Only offsets beyond it are clearly rest-pose artifacts (measured 12–25° on the
    /// repro rig) and get recalibrated.
    /// </summary>
    public float MinCorrectionDeg { get; set; } = 8f;

    /// <summary>
    /// Maximum mean sole deviation (degrees) a plant may show and still count as a STANCE
    /// for the offset measurement. Plants beyond this are not standing on the sole (crawls,
    /// kneels, prone contact — measured 60–90° there) and are excluded; genuine rest-pose
    /// stance artifacts measure well below it (largest seen: 27°).
    /// </summary>
    public float MaxStanceDeviationDeg { get; set; } = 35f;
}
