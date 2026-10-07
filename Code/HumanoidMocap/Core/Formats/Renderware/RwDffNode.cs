#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using HumanoidMocap.Core.Maths;

namespace HumanoidMocap.Core.Formats.Renderware;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>One HAnim node of a parsed .dff skeleton, in HAnim node order — the order
/// RwAnimAnimation keyframes address nodes in.</summary>
public sealed class RwDffNode
{
    /// <summary>HAnim node id (stable across FSB2 characters, e.g. 1000 = "Bip01").</summary>
    public required int NodeId { get; init; }

    /// <summary>
    /// Bone name: the frame's authored name when the .dff carries one (FSB2 stores 3ds Max
    /// Biped names like <c>Bip01 L Thigh</c> in the RpUserData extension), else the
    /// synthesized stable fallback <c>rw_node_&lt;id&gt;</c>. Unique within the skeleton.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Parent NODE index (into the node list), or -1 for the root node.</summary>
    public required int ParentIndex { get; init; }

    /// <summary>Rest (bind) transform relative to the parent NODE (intermediate non-HAnim
    /// frames composed in), native .dff units/axes.</summary>
    public required XForm RestLocal { get; init; }

    /// <summary>HAnim PUSH/POP hierarchy flags from the node table (diagnostic).</summary>
    public required uint Flags { get; init; }
}
