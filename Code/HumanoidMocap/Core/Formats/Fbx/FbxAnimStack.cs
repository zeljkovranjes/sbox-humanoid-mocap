#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;

namespace HumanoidMocap.Core.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>One AnimationStack with its curve bindings, flattened across its layers.</summary>
public sealed class FbxAnimStack
{
    /// <summary>The stack object (its Name is the clip name, e.g. "mixamo.com").</summary>
    public FbxObject Object { get; }

    /// <summary>
    /// Curve nodes bound to model transform properties:
    /// (model id, property name) → curve node. When several layers animate the same property
    /// the first connected layer wins (layer blending is not supported).
    /// </summary>
    public Dictionary<(long ModelId, string Property), FbxAnimCurveNode> Bindings { get; } = new();

    /// <summary>LocalStart from the stack's Properties70, in KTIME ticks (0 when absent).</summary>
    public long LocalStart { get; internal set; }

    /// <summary>LocalStop from the stack's Properties70, in KTIME ticks (0 when absent).</summary>
    public long LocalStop { get; internal set; }

    internal FbxAnimStack(FbxObject obj) => Object = obj;
}
