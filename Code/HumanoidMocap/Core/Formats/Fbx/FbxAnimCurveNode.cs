#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;

namespace HumanoidMocap.Core.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// An AnimationCurveNode: up to three channel curves (X/Y/Z) targeting one transform
/// property (<c>"Lcl Translation"</c> / <c>"Lcl Rotation"</c> / <c>"Lcl Scaling"</c>) of one Model.
/// </summary>
public sealed class FbxAnimCurveNode
{
    /// <summary>The curve node object.</summary>
    public FbxObject Object { get; }

    /// <summary>Channel curves by axis ('X'/'Y'/'Z'), from <c>"d|X"</c>-style OP connections.</summary>
    public Dictionary<char, FbxAnimCurve> Channels { get; } = new();

    internal FbxAnimCurveNode(FbxObject obj) => Object = obj;

    /// <summary>
    /// Samples one component: the channel curve when connected, else the curve node's static
    /// <c>d|X</c> default, else <paramref name="fallback"/> (the model's Lcl value).
    /// </summary>
    public float Component(char axis, long ticks, float fallback)
    {
        if (Channels.TryGetValue(axis, out var curve))
            return curve.Evaluate(ticks);
        if (Object.Properties.TryGetValue("d|" + axis, out var def) && def.Values.Count > 0)
            return (float)def.GetDouble();
        return fallback;
    }
}
