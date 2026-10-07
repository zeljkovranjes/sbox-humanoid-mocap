#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>The KV3 <c>null</c> value.</summary>
public sealed class KvNull : KvValue
{
    /// <summary>Shared instance.</summary>
    public static readonly KvNull Instance = new();
}
