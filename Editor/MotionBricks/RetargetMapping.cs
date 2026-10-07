#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

using System.Collections;
using System.Numerics;
using System.Reflection;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>Result of the retargeter's automatic humanoid detection on a skeleton.</summary>
public sealed record RetargetMapping( IReadOnlyDictionary<BoneRole, int> Roles, float Confidence, string Profile, bool NeedsReview )
{
    internal object? Handle { get; init; }
}
