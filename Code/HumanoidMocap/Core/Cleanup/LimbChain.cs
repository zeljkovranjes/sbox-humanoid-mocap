#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// A three-joint limb chain identified by skeleton bone indices:
/// hip/knee/ankle for a leg, shoulder/elbow/wrist for an arm.
/// <see cref="Lower"/> is expected to be a child of <see cref="Upper"/> and
/// <see cref="End"/> a child of <see cref="Lower"/> (the usual humanoid layout);
/// locals are re-derived against the actual parents, so the solve stays exact
/// for direct parent-child chains.
/// </summary>
public sealed class LimbChain
{
    /// <summary>Upper joint bone index (hip / shoulder).</summary>
    public required int Upper { get; init; }

    /// <summary>Mid joint bone index (knee / elbow).</summary>
    public required int Lower { get; init; }

    /// <summary>End effector bone index (ankle / wrist).</summary>
    public required int End { get; init; }
}
