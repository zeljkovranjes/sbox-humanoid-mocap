#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>A KV3 integer (no decimal point in the source).</summary>
public sealed class KvLong : KvValue
{
    /// <summary>The integer value.</summary>
    public long Value { get; }

    /// <summary>Creates an integer value.</summary>
    public KvLong(long value) => Value = value;
}
