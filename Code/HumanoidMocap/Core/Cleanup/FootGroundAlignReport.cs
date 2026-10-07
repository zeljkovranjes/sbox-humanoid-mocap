#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Results of a <see cref="FootGroundAlign.Apply"/> run.</summary>
public sealed class FootGroundAlignReport
{
    /// <summary>Left-foot results.</summary>
    public required FootGroundAlignFootReport Left { get; init; }

    /// <summary>Right-foot results.</summary>
    public required FootGroundAlignFootReport Right { get; init; }
}
