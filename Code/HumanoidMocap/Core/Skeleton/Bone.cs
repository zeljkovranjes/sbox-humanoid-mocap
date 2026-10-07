#nullable enable annotations

using System;
using System.Collections.Generic;
using HumanoidMocap.Core.Maths;

namespace HumanoidMocap.Core.Skeleton;

/// <summary>One bone of an immutable <see cref="Skeleton"/>.</summary>
public readonly struct Bone
{
    /// <summary>Index of this bone in the skeleton (parents always have a smaller index).</summary>
    public int Index { get; }

    /// <summary>Unique bone name.</summary>
    public string Name { get; }

    /// <summary>Index of the parent bone, or -1 for a root bone.</summary>
    public int ParentIndex { get; }

    /// <summary>Rest (bind) transform relative to the parent bone (or armature space for roots).</summary>
    public XForm RestLocal { get; }

    internal Bone(int index, string name, int parentIndex, XForm restLocal)
    {
        Index = index;
        Name = name;
        ParentIndex = parentIndex;
        RestLocal = restLocal;
    }

    /// <inheritdoc />
    public override string ToString() => $"[{Index}] {Name} (parent {ParentIndex})";
}
