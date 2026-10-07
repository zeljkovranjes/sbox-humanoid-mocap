#nullable enable annotations

using System;
using System.Collections.Generic;

namespace HumanoidMocap.Core.Target;

/// <summary>
/// Report entry for one directional clip family found by
/// <see cref="LocomotionSetDetector.Detect"/> — emitted on
/// <see cref="RetargetBatchResult.LocomotionSets"/> whether or not a blend node was produced
/// (incomplete families are reported with <see cref="Emitted"/> false and a note).
/// </summary>
public sealed class LocomotionSetReport
{
    /// <summary>Common stem of the family's clip names (e.g. <c>Walk</c> for
    /// <c>Walk_N</c>…<c>Walk_NW</c>).</summary>
    public required string Stem { get; init; }

    /// <summary>True when the family was complete (all four cardinals) and a 2DBlend +
    /// Folder were emitted into the generated/augmented vmdl.</summary>
    public required bool Emitted { get; init; }

    /// <summary>Name of the emitted 2DBlend node (collision-suffixed); empty when
    /// <see cref="Emitted"/> is false.</summary>
    public string BlendNodeName { get; init; } = "";

    /// <summary>Name of the emitted Folder node; empty when <see cref="Emitted"/> is false.</summary>
    public string FolderName { get; init; } = "";

    /// <summary>Clip name per canonical direction token (<c>N</c>, <c>NE</c>, <c>E</c>,
    /// <c>SE</c>, <c>S</c>, <c>SW</c>, <c>W</c>, <c>NW</c>); only detected members present.</summary>
    public required IReadOnlyDictionary<string, string> Members { get; init; }

    /// <summary>Sequence name placed in the blend grid's center cell (an idle clip from the
    /// batch when one matched, else the forward member as fallback); null when nothing was
    /// emitted.</summary>
    public string? CenterClipName { get; init; }

    /// <summary>Detection notes: missing cardinals (incomplete family), center-cell
    /// fallback, duplicate direction members, diagonal fill-ins.</summary>
    public List<string> Notes { get; } = new();
}
