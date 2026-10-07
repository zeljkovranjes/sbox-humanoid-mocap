#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HumanoidMocap.Core.Target;

/// <summary>One animation to register in a vmdl AnimationList.</summary>
public sealed class AnimEntry
{
    /// <summary>Sequence name (must be unique within the AnimationList).</summary>
    public string Name { get; set; } = "";

    /// <summary>Animation source path relative to the assets root
    /// (e.g. <c>models/x/animations/walk.dmx</c>).</summary>
    public string SourceFilename { get; set; } = "";

    /// <summary>Whether the sequence loops.</summary>
    public bool Looping { get; set; }

    /// <summary>Whether to add an ExtractMotion child node (ground-plane translation
    /// extraction, linear, matching the shipped citizen prefab usage).</summary>
    public bool ExtractMotion { get; set; }

    /// <summary>AnimEvent children to emit on the AnimFile node (e.g. generated
    /// <c>AE_FOOTSTEP</c> events); empty = no event nodes.</summary>
    public IReadOnlyList<AnimEventEntry> Events { get; set; } = Array.Empty<AnimEventEntry>();

    /// <summary>
    /// When set, the AnimFile node gets an <c>AnimSubtract</c> child (first child, like the
    /// shipped citizen data orders it) making the sequence an additive delta at compile time:
    /// <c>anim_name</c> = this value (the sequence whose reference frame is subtracted —
    /// the shipped <c>IdleLayer_01_delta</c> names its base sequence; other shipped entries
    /// self-reference), <c>frame</c> = <see cref="SubtractFrame"/>. The animation source is
    /// reused verbatim (<see cref="SourceFilename"/> points at the SAME file as the base
    /// entry) — resourcecompiler performs the per-frame subtraction; no frame math happens
    /// here. The AnimFile's own <c>delta</c> attribute stays <c>false</c>, exactly like every
    /// shipped <c>_delta</c> sequence. Null = plain (non-additive) sequence.
    /// </summary>
    public string? SubtractAnimName { get; set; }

    /// <summary>Reference frame index the AnimSubtract child subtracts (the shipped data uses
    /// 0 for loops and mid-clip indices for aim matrices); ignored while
    /// <see cref="SubtractAnimName"/> is null.</summary>
    public int SubtractFrame { get; set; }
}
