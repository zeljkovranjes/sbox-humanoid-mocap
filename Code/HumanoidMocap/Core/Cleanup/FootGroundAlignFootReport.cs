#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Per-foot results of a <see cref="FootGroundAlign.Apply"/> run.</summary>
public sealed class FootGroundAlignFootReport
{
    /// <summary>Plants that contributed to the stance measurement.</summary>
    public int StancePlants { get; set; }

    /// <summary>Plants excluded as non-stance (mean sole deviation beyond
    /// <see cref="FootGroundAlignOptions.MaxStanceDeviationDeg"/>).</summary>
    public int SkippedPlants { get; set; }

    /// <summary>Measured planted sole offset from the ground plane, degrees (0 when no
    /// stance plants exist).</summary>
    public float MeasuredOffsetDeg { get; set; }

    /// <summary>Foot correction applied to every frame, degrees (0 = inside the dead zone,
    /// nothing changed).</summary>
    public float AppliedFootDeg { get; set; }

    /// <summary>Toe correction applied to every frame, degrees.</summary>
    public float AppliedToeDeg { get; set; }
}
