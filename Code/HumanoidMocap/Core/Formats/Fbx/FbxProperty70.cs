#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;

namespace HumanoidMocap.Core.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>One <c>P</c> entry of a <c>Properties70</c> block: name, FBX type string, values.</summary>
public sealed class FbxProperty70
{
    /// <summary>Property name (e.g. <c>"Lcl Translation"</c>, <c>"PreRotation"</c>, <c>"d|X"</c>).</summary>
    public string Name { get; }

    /// <summary>FBX type string (e.g. <c>"Lcl Translation"</c>, <c>"enum"</c>, <c>"Number"</c>).</summary>
    public string Type { get; }

    /// <summary>Raw values (props 4.. of the P node).</summary>
    public IReadOnlyList<object> Values { get; }

    internal FbxProperty70(string name, string type, IReadOnlyList<object> values)
    {
        Name = name;
        Type = type;
        Values = values;
    }

    /// <summary>Value <paramref name="i"/> as a double (tolerant of int/long/float storage).</summary>
    public double GetDouble(int i = 0) => Convert.ToDouble(Values[i], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Value <paramref name="i"/> as an int (tolerant of long/double storage).</summary>
    public int GetInt(int i = 0) => Convert.ToInt32(Values[i], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>First three values as a vector.</summary>
    public Vector3 GetVector3()
        => new((float)GetDouble(0), (float)GetDouble(1), (float)GetDouble(2));
}
