#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>A leg chain identified by skeleton bone indices.</summary>
public sealed class FootChain
{
    /// <summary>Upper-leg joint bone index.</summary>
    public required int Hip { get; init; }

    /// <summary>Knee bone index.</summary>
    public required int Knee { get; init; }

    /// <summary>Ankle (end effector) bone index — the position that gets pinned.</summary>
    public required int Ankle { get; init; }

    /// <summary>Optional toe bone index (follows via FK; reserved for future toe pinning).</summary>
    public int? Toe { get; init; }
}
