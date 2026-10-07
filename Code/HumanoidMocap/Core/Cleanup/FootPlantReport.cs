#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Results of a <see cref="FootPlant.Apply"/> run.</summary>
public sealed class FootPlantReport
{
    /// <summary>Left-foot results.</summary>
    public required FootPlantFootReport Left { get; init; }

    /// <summary>Right-foot results.</summary>
    public required FootPlantFootReport Right { get; init; }

    /// <summary>Estimated ground level (height along the up axis), centimeters.</summary>
    public float GroundHeight { get; set; }
}
