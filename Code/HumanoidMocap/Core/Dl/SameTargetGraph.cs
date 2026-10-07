#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Skeleton;
using HumanoidMocap.Core.Solve;
using HumanoidMocap.Core.Target;

namespace HumanoidMocap.Core.Dl;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// The target-skeleton graph the SAME decoder is conditioned on, plus everything needed to
/// turn decoder output back into target-rig bone locals.
/// </summary>
public sealed class SameTargetGraph
{
    /// <summary>The rig this graph was built from.</summary>
    public required TargetRig Rig { get; init; }

    /// <summary>Rig bone index of the graph root (hips).</summary>
    public required int HipsBone { get; init; }

    /// <summary>Rig bone index per node; -1 for synthesized end joints.</summary>
    public required int[] NodeBone { get; init; }

    /// <summary>Graph-parent node index; -1 for the root (node 0).</summary>
    public required int[] NodeParent { get; init; }

    /// <summary>Node names (bone names; end joints suffixed <c>_end</c>).</summary>
    public required string[] NodeNames { get; init; }

    /// <summary>Normalized skeleton features (lo3 ⊕ go3), flat [NodeCount × 6].</summary>
    public required float[] X { get; init; }

    /// <summary>Raw local offsets, flat [NodeCount × 3] (aligned space; FK uses these).</summary>
    public required float[] LoRaw { get; init; }

    /// <summary>Raw global rest offsets, flat [NodeCount × 3] (diagnostics/tests).</summary>
    public required float[] GoRaw { get; init; }

    /// <summary>World rotation taking rig space into the canonical SAME frame.</summary>
    public required Quaternion Align { get; init; }

    /// <summary>Aligned-space offset restoring the rig's rest root XZ and ground height
    /// when converting decoded root trajectories back to rig space.</summary>
    public required Vector3 RestOffset { get; init; }

    /// <summary>Rig-space rest world rotation per node (the leaf's for end joints).</summary>
    public required Quaternion[] NodeRestWorldRot { get; init; }

    /// <summary>Nodes per frame.</summary>
    public int NodeCount => NodeBone.Length;
}
