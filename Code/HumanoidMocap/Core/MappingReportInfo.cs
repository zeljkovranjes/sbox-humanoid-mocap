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
/// Mapping report for one source file: which profile produced the mapping, how confident it
/// is, and whether the UI should ask the user before trusting it (design §6 no-profile flow).
/// </summary>
public sealed class MappingReportInfo
{
    /// <summary>Profile that produced the mapping (preset name, <c>"auto"</c>/<c>"topology"</c>
    /// for the auto-mapper, or the override's name).</summary>
    public required string ProfileName { get; init; }

    /// <summary>Where the mapping came from (preset / user preset / auto / manual).</summary>
    public required MappingSource Source { get; init; }

    /// <summary>Mapping confidence in [0, 1].</summary>
    public required float Confidence { get; init; }

    /// <summary>
    /// True when NO preset profile matched AND the auto-mapper's confidence is below
    /// <see cref="ProfileDetector.DetectionThreshold"/> (0.8) — this drives the UI's
    /// "No known profile found for this rig" dialog. The pipeline still proceeds with the
    /// best-effort auto map; the flag only marks the result as needing review.
    /// </summary>
    public required bool NeedsUserDecision { get; init; }

    /// <summary>Number of canonical roles resolved to source bones.</summary>
    public required int MappedRoleCount { get; init; }

    /// <summary>Mapping notes (unmapped roles, ignored bones) plus pipeline notes appended
    /// during conversion (e.g. root-motion handling).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Stable <see cref="SkeletonSignature"/> of the source skeleton — the key used
    /// for user preset profiles.</summary>
    public required string SkeletonSignature { get; init; }
}
