#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Skeleton;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Formats.Ant;

/// <summary>Sampling and naming options for <see cref="AntImporter"/>.</summary>
public sealed class AntImportOptions
{
    /// <summary>Resample rate for the imported clips (Hz).</summary>
    public float SampleFps { get; init; } = 30f;

    /// <summary>
    /// Authored tick rate of the package. ANT key times are in TICKS; Fight Night's packages
    /// are authored at 30 ticks per second (a 66-tick punch is 2.2 s, which matches the
    /// shipped clip lengths).
    /// </summary>
    public float TicksPerSecond { get; init; } = 30f;

    /// <summary>Base name for clips (ANT clip chunks carry no name of their own —
    /// see <see cref="AntImporter"/> remarks). Takes become <c>{Base}_1</c>, <c>_2</c>, …</summary>
    public string ClipNameBase { get; init; } = "clip";
}
