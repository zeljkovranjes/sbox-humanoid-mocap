#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Target;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core;

/// <summary>
/// Result of <see cref="Retargeter.Inspect"/>: the mapping report plus the file's take
/// metadata, so UI listings can expand a multi-take file into one entry per take (each
/// convertible independently via <see cref="RetargetRequest.TakeIndex"/>).
/// </summary>
public sealed class InspectResult
{
    /// <summary>Mapping report from the same preset-then-auto cascade conversion uses.</summary>
    public required MappingReportInfo Mapping { get; init; }

    /// <summary>Names of the file's animation takes, in take-index order (the index is what
    /// <see cref="RetargetRequest.TakeIndex"/> accepts). Empty when the file has no animation.</summary>
    public required IReadOnlyList<string> TakeNames { get; init; }

    /// <summary>Number of animation takes in the file.</summary>
    public int TakeCount => TakeNames.Count;
}
