#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Axis/index context for root-motion processing, supplied by the solver.</summary>
public sealed class RootMotionAxes
{
    /// <summary>World up direction of the clip's space.</summary>
    public required Vector3 Up { get; init; }

    /// <summary>Frame-array index of the dedicated root bone.</summary>
    public required int RootIndex { get; init; }

    /// <summary>Frame-array index of the hips bone.</summary>
    public required int HipsIndex { get; init; }

    /// <summary>
    /// True when the hips bone's local transform is parented to the root bone (so moving the
    /// root must subtract from hips locals to preserve world positions). False when both are
    /// scene-root level.
    /// </summary>
    /// <remarks>
    /// Only consulted by the legacy (skeleton-less) <see cref="RootMotion.Apply(List{XForm[]},
    /// RootMotionAxes, RootMotionMode)"/> overload. The skeleton-aware overload derives the
    /// actual parentage from the skeleton and ignores this flag.
    /// </remarks>
    public required bool HipsParentIsRoot { get; init; }

    /// <summary>Half-window (in frames) of the moving-average smoothing for the root path.</summary>
    public int SmoothHalfWindow { get; init; } = 2;
}
