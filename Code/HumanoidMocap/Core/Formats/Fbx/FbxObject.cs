#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;

namespace HumanoidMocap.Core.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// One object from the FBX <c>Objects</c> section (Model, NodeAttribute, AnimationStack,
/// AnimationLayer, AnimationCurveNode, AnimationCurve, Pose, ...).
/// </summary>
public sealed class FbxObject
{
    /// <summary>Unique object id (the first property of the object node).</summary>
    public long Id { get; }

    /// <summary>Node type — the FBX node name: "Model", "AnimationCurve", ...</summary>
    public string NodeType { get; }

    /// <summary>Object name (namespace prefixes like <c>mixamorig1:</c> preserved).</summary>
    public string Name { get; }

    /// <summary>Object sub-class (third property): "LimbNode", "Null", "Root", "Mesh", ...</summary>
    public string SubClass { get; }

    /// <summary>The underlying token-tree node.</summary>
    public FbxNode Node { get; }

    /// <summary>Own Properties70 entries by name (template defaults NOT merged — see <see cref="FbxScene.FindProperty"/>).</summary>
    public IReadOnlyDictionary<string, FbxProperty70> Properties { get; }

    /// <summary>For Models: the parent Model via an OO connection, or null at the scene root.</summary>
    public FbxObject? ModelParent { get; internal set; }

    /// <summary>For Models: child Models via OO connections, in connection order.</summary>
    public List<FbxObject> ModelChildren { get; } = new();

    internal FbxObject(
        long id, string nodeType, string name, string subClass, FbxNode node,
        IReadOnlyDictionary<string, FbxProperty70> properties)
    {
        Id = id;
        NodeType = nodeType;
        Name = name;
        SubClass = subClass;
        Node = node;
        Properties = properties;
    }

    /// <inheritdoc />
    public override string ToString() => $"{NodeType} '{Name}' ({SubClass}) #{Id}";
}
