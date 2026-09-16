#nullable enable annotations

using System;
using System.Collections.Generic;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Skeleton;

/// <summary>
/// Input definition for a single bone, used to build a <see cref="Skeleton"/>.
/// Order does not matter; construction topologically sorts parents before children.
/// </summary>
/// <param name="Name">Unique bone name.</param>
/// <param name="ParentName">Parent bone name, or null for a root bone (multiple roots allowed).</param>
/// <param name="RestLocal">Rest (bind) transform relative to the parent bone, centimeters.</param>
public readonly record struct BoneDefinition(string Name, string? ParentName, XForm RestLocal);

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

/// <summary>
/// Immutable bone hierarchy with rest pose. Bones are stored topologically sorted
/// (every bone's parent precedes it), which lets forward kinematics run as a single
/// in-order pass. Supports multiple roots (e.g. the s&amp;box rig has both
/// <c>root_IK</c> and <c>pelvis</c> as parentless bones).
/// </summary>
public sealed class Skeleton
{
    private readonly Bone[] _bones;
    private readonly XForm[] _restWorld;
    private readonly Dictionary<string, int> _indexByName;

    private Skeleton(Bone[] bones, XForm[] restWorld, Dictionary<string, int> indexByName)
    {
        _bones = bones;
        _restWorld = restWorld;
        _indexByName = indexByName;
    }

    /// <summary>Number of bones.</summary>
    public int Count => _bones.Length;

    /// <summary>All bones, topologically sorted (parents before children).</summary>
    public IReadOnlyList<Bone> Bones => _bones;

    /// <summary>Rest (bind) world transforms, indexed like <see cref="Bones"/>.</summary>
    public IReadOnlyList<XForm> RestWorld => _restWorld;

    /// <summary>Bone name to index lookup.</summary>
    public IReadOnlyDictionary<string, int> IndexByName => _indexByName;

    /// <summary>The bone at <paramref name="index"/>.</summary>
    public Bone this[int index] => _bones[index];

    /// <summary>Returns the index of the named bone, or -1 when absent.</summary>
    public int IndexOf(string name) => _indexByName.TryGetValue(name, out var index) ? index : -1;

    /// <summary>
    /// Builds a skeleton from bone definitions in any order: validates names and parent links,
    /// topologically sorts (stable — input order is preserved among bones whose parents are
    /// already placed), and computes rest world transforms.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown on duplicate bone names, unknown parent names, or parent cycles.
    /// </exception>
    public static Skeleton Create(IReadOnlyList<BoneDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var count = definitions.Count;
        var originalIndexByName = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var name = definitions[i].Name;
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"Bone at position {i} has an empty name.", nameof(definitions));
            if (!originalIndexByName.TryAdd(name, i))
                throw new ArgumentException($"Duplicate bone name '{name}'.", nameof(definitions));
        }

        foreach (var def in definitions)
        {
            if (def.ParentName is not null && !originalIndexByName.ContainsKey(def.ParentName))
                throw new ArgumentException(
                    $"Bone '{def.Name}' references unknown parent '{def.ParentName}'.", nameof(definitions));
            if (def.ParentName == def.Name)
                throw new ArgumentException($"Bone '{def.Name}' is its own parent.", nameof(definitions));
        }

        // Stable topological sort: repeatedly place, in input order, every bone whose parent
        // is already placed. O(n^2) worst case — fine for skeleton-sized inputs (~100 bones).
        var newIndexByOriginal = new int[count];
        Array.Fill(newIndexByOriginal, -1);
        var order = new List<int>(count);
        while (order.Count < count)
        {
            var progressed = false;
            for (var i = 0; i < count; i++)
            {
                if (newIndexByOriginal[i] >= 0)
                    continue;
                var parentName = definitions[i].ParentName;
                if (parentName is not null && newIndexByOriginal[originalIndexByName[parentName]] < 0)
                    continue;
                newIndexByOriginal[i] = order.Count;
                order.Add(i);
                progressed = true;
            }
            if (!progressed)
                throw new ArgumentException("Bone hierarchy contains a parent cycle.", nameof(definitions));
        }

        var bones = new Bone[count];
        var restWorld = new XForm[count];
        var indexByName = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var newIndex = 0; newIndex < count; newIndex++)
        {
            var def = definitions[order[newIndex]];
            var parentIndex = def.ParentName is null ? -1 : newIndexByOriginal[originalIndexByName[def.ParentName]];
            bones[newIndex] = new Bone(newIndex, def.Name, parentIndex, def.RestLocal);
            restWorld[newIndex] = parentIndex < 0
                ? def.RestLocal
                : XForm.Compose(restWorld[parentIndex], def.RestLocal);
            indexByName.Add(def.Name, newIndex);
        }

        return new Skeleton(bones, restWorld, indexByName);
    }
}
