#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace HumanoidMocap.Core.Formats.Gltf;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>One glTF node, reduced to what skeleton import needs (TRS rest + hierarchy).</summary>
internal sealed class GltfNode
{
    public string? Name;
    public int[] Children = Array.Empty<int>();
    public int Parent = -1;
    public bool HasMesh;

    // Rest local transform: TRS properties, or the decomposed "matrix" property (the spec
    // makes them exclusive; animated nodes must use TRS). Shear is not representable.
    public Vector3 Translation;                       // meters
    public Quaternion Rotation = Quaternion.Identity; // xyzw
    public Vector3 Scale = Vector3.One;
}
