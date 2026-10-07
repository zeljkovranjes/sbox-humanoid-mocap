#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>A KV3 floating-point number (decimal point or exponent in the source).</summary>
public sealed class KvDouble : KvValue
{
    /// <summary>The floating-point value.</summary>
    public double Value { get; }

    /// <summary>Creates a floating-point value.</summary>
    public KvDouble(double value) => Value = value;
}
