#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>A KV3 string (quoted, multi-line, or bare-identifier in the source).</summary>
public sealed class KvString : KvValue
{
    /// <summary>The unescaped string value.</summary>
    public string Value { get; }

    /// <summary>Creates a string value.</summary>
    public KvString(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
}
