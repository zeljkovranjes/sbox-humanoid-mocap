#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap;

/// <summary>Imported source after the public mapping and bind-rest preparation cascade.</summary>
public sealed record ResolvedSource( SourceScene Scene, MappingResult Mapping, MappingReportInfo Report );

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

/// <summary>Outcome of retargeting one source take to one output clip.</summary>
public sealed class ClipResult
{
    /// <summary>Final (collision-suffixed) sequence name.</summary>
    public required string ClipName { get; init; }

    /// <summary>The file the request came from (as supplied on the request).</summary>
    public required string SourceFileName { get; init; }

    /// <summary>
    /// Identity of the originating request (<see cref="RetargetRequest.SourceId"/>, falling
    /// back to <see cref="RetargetRequest.SourceFileName"/>). Callers join clip results back
    /// to their own entries on this — file NAMES may collide across folders.
    /// </summary>
    public string SourceId { get; init; } = "";

    /// <summary>Sanitized DMX file name (<c>&lt;clip&gt;.dmx</c>); empty on failure.</summary>
    public string DmxFileName { get; init; } = "";

    /// <summary>Full DMX text (keyvalues2) of the retargeted clip; empty on failure or after
    /// <see cref="ReleaseHeavyData"/>.</summary>
    public string DmxContent { get; internal set; } = "";

    /// <summary>Replaces the serialized DMX (diagnostic harnesses only: the UI smoke
    /// gate's bone-bisect re-serializes mutated frames so the COMPILE reflects them).</summary>
    public void OverrideDmxContent(string dmx) => DmxContent = dmx ?? "";

    /// <summary>Mapping report of the source file; null when the failure happened before
    /// detection (unreadable file).</summary>
    public MappingReportInfo? Mapping { get; init; }

    /// <summary>Whether this clip converted successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Failure description when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The final solved frames (after cleanup and IK baking) — one local
    /// <see cref="XForm"/> per TARGET rig bone, in TARGET skeleton bone order, per frame.
    /// Retained for the UI preview path (<c>Model.Builder.AddFrame</c> consumes exactly
    /// this layout). Null on failure or after <see cref="ReleaseHeavyData"/>.
    /// </summary>
    public List<XForm[]>? SolvedFrames { get; internal set; }

    /// <summary>
    /// Drops the heavy per-clip payloads (<see cref="DmxContent"/>, <see cref="SolvedFrames"/>)
    /// once the caller has written them to disk — UI windows keep clip results around for
    /// status display, which must not pin megabytes of frame data indefinitely. Previews
    /// re-solve on demand (subsecond), so nothing else needs the frames after the write.
    /// </summary>
    public void ReleaseHeavyData()
    {
        DmxContent = "";
        SolvedFrames = null;
    }

    /// <summary>Output sample rate.</summary>
    public float Fps { get; init; }

    /// <summary>Looping flag of the output sequence (source flag or request override).</summary>
    public bool Looping { get; init; }

    /// <summary>Whether the clip's vmdl AnimFile entry carries an ExtractMotion node
    /// (request had <c>RootMotion == Extract</c>).</summary>
    public bool ExtractMotion { get; init; }

    /// <summary>
    /// Generated <c>AE_FOOTSTEP</c> events for this clip, in frame order — the same list
    /// emitted as AnimEvent children on the clip's vmdl AnimFile entry. Empty unless the
    /// request set <see cref="RetargetRequest.GenerateFootstepEvents"/> (and detection found
    /// touchdowns).
    /// </summary>
    public IReadOnlyList<AnimEventEntry> FootstepEvents { get; init; } = Array.Empty<AnimEventEntry>();

    /// <summary>
    /// True when this clip is the mirrored twin produced by
    /// <see cref="RetargetRequest.CreateMirroredVariant"/> (named <c>&lt;clip&gt;_M</c>,
    /// collision-suffixed as usual).
    /// </summary>
    public bool IsMirroredVariant { get; init; }

    /// <summary>
    /// True when <see cref="RetargetRequest.CreateAdditiveVariant"/> registered a companion
    /// additive (<c>_delta</c>) AnimFile entry for this clip in the generated/augmented vmdl
    /// (an AnimSubtract sequence reusing this clip's DMX — no separate clip result exists,
    /// since no separate DMX is produced).
    /// </summary>
    public bool HasAdditiveVariant { get; init; }

    /// <summary>Name of the additive variant sequence (<c>&lt;clip&gt;_delta</c>,
    /// collision-suffixed); null when <see cref="HasAdditiveVariant"/> is false.</summary>
    public string? AdditiveVariantName { get; init; }
}

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
