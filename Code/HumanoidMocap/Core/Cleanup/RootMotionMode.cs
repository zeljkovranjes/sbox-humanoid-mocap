#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>How root motion should be handled in the output clip.</summary>
public enum RootMotionMode
{
    /// <summary>Leave hips/root channels exactly as solved.</summary>
    Off,

    /// <summary>
    /// Move the ground-projected, smoothed hips trajectory onto the dedicated root bone;
    /// hips keep height and full rotation locally (Unity/UE convention).
    /// </summary>
    Extract,

    /// <summary>Remove horizontal hips travel entirely (in-place clip); root stays put.</summary>
    InPlace,
}
