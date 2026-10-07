#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using HumanoidMocap.Core.Maths;

namespace HumanoidMocap.Core.Formats.Renderware;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Result of parsing a .dff model's skeleton.</summary>
public sealed class RwDffSkeletonData
{
    /// <summary>HAnim nodes in node-index order (== animation keyframe node order).</summary>
    public required IReadOnlyList<RwDffNode> Nodes { get; init; }

    /// <summary>Total FrameList frame count (HAnim and non-HAnim frames alike).</summary>
    public required int FrameCount { get; init; }
}
