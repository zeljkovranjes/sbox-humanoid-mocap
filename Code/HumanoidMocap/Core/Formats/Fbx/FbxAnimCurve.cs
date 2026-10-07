#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;

namespace HumanoidMocap.Core.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>A single animation curve: keyframes for one scalar channel.</summary>
public sealed class FbxAnimCurve
{
    /// <summary>KTIME ticks per second (FBX constant).</summary>
    public const long TicksPerSecond = 46186158000L;

    /// <summary>Key times in KTIME ticks, ascending.</summary>
    public long[] KeyTimes { get; }

    /// <summary>Key values, parallel to <see cref="KeyTimes"/>.</summary>
    public float[] KeyValues { get; }

    internal FbxAnimCurve(long[] keyTimes, float[] keyValues)
    {
        KeyTimes = keyTimes;
        KeyValues = keyValues;
    }

    /// <summary>
    /// Samples the curve at a KTIME tick: linear interpolation between keys, constant
    /// extrapolation outside the key range.
    /// </summary>
    public float Evaluate(long ticks)
    {
        var times = KeyTimes;
        int n = times.Length;
        if (n == 0)
            return 0f;
        if (ticks <= times[0])
            return KeyValues[0];
        if (ticks >= times[n - 1])
            return KeyValues[n - 1];

        int hi = Array.BinarySearch(times, ticks);
        if (hi >= 0)
            return KeyValues[hi];
        hi = ~hi; // first index with time > ticks; >=1 and <=n-1 here
        int lo = hi - 1;
        double span = times[hi] - times[lo];
        double t = span <= 0 ? 0.0 : (ticks - times[lo]) / span;
        return (float)(KeyValues[lo] + (KeyValues[hi] - KeyValues[lo]) * t);
    }
}
