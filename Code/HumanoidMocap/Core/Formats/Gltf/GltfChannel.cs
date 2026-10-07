#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace HumanoidMocap.Core.Formats.Gltf;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>One decoded animation channel: keyframe times + values for one node property.</summary>
internal sealed class GltfChannel
{
    public required int NodeIndex;
    public required bool IsRotation;     // true = rotation (VEC4 quat), false = translation (VEC3)
    public required float[] Times;       // seconds, ascending
    public required float[] Values;      // flattened; 4 (or 3) floats per element
    public required string Interpolation; // LINEAR / STEP / CUBICSPLINE

    /// <summary>Floats per element (3 translation / 4 rotation).</summary>
    public int Comps => IsRotation ? 4 : 3;

    /// <summary>Elements stored per key: CUBICSPLINE keys carry in-tangent/value/out-tangent.</summary>
    public int ElementsPerKey => Interpolation == "CUBICSPLINE" ? 3 : 1;

    /// <summary>Number of keys.</summary>
    public int KeyCount => Times.Length;
}
