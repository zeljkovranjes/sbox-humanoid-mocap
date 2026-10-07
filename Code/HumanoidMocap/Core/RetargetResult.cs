#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Target;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core;

/// <summary>Result of a single-file <see cref="Retargeter.Convert"/> (all takes in the file).</summary>
public sealed class RetargetResult
{
    /// <summary>One result per take in the source file.</summary>
    public required IReadOnlyList<ClipResult> Clips { get; init; }

    /// <summary>Standalone vmdl text registering every successful clip.</summary>
    public required string StandaloneVmdl { get; init; }

    /// <summary>Aggregated error messages (one per failed clip).</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>True when at least one clip was produced and none failed.</summary>
    public bool Success => Clips.Count > 0 && Clips.All(c => c.Success);
}

/// <summary>
/// Result of <see cref="Retargeter.ConvertBatch"/>: per-clip results plus the combined vmdl
/// outputs. Pure data — the caller (Editor pipeline) does all file IO.
/// </summary>
public sealed class RetargetBatchResult
{
    /// <summary>Every produced clip across all requests, in request order (failures included).</summary>
    public List<ClipResult> Clips { get; } = new();

    /// <summary>One standalone vmdl containing ALL successful clips' AnimFile entries.</summary>
    public string StandaloneVmdl { get; set; } = "";

    /// <summary>The augmented vmdl text when <see cref="BatchOptions.AugmentVmdlText"/> was
    /// provided (and augmentation succeeded); null otherwise.</summary>
    public string? AugmentedVmdl { get; set; }

    /// <summary>Aggregated error messages (per-clip failures, augmentation failures). The
    /// batch result stays usable regardless — one bad file never aborts the batch.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>Non-fatal notices the caller should surface (e.g. stale AnimFile entries
    /// removed from the augmented vmdl because their animation sources are gone from disk —
    /// see <see cref="BatchOptions.MissingAnimSources"/>).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Directional locomotion families found among the batch's successful clips when
    /// <see cref="BatchOptions.DetectLocomotionSets"/> was on — one report per family,
    /// including incomplete ones (reported with <see cref="LocomotionSetReport.Emitted"/>
    /// false and a note instead of a blend node). Empty when the option is off.
    /// </summary>
    public List<LocomotionSetReport> LocomotionSets { get; } = new();
}
