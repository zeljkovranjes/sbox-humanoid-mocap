#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Target;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core;

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
