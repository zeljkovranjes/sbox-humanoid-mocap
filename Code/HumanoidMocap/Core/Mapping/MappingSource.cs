#nullable enable annotations

using System.Collections.Generic;

namespace HumanoidMocap.Core.Mapping;

/// <summary>How a mapping was produced; determines UI flow and preset-learning behavior.</summary>
public enum MappingSource
{
    /// <summary>A shipped preset profile matched (mixamo, actorcore_cc, ...).</summary>
    Preset,

    /// <summary>A user-saved preset (keyed by <see cref="SkeletonSignature"/>) matched.</summary>
    UserPreset,

    /// <summary>The auto-mapper resolved roles from bone-name tokens (stage A).</summary>
    AutoName,

    /// <summary>The auto-mapper fell back to pure hierarchy/geometry matching (stage B).</summary>
    AutoTopology,

    /// <summary>The user assigned roles by hand in the mapping editor.</summary>
    Manual,

    /// <summary>
    /// The source file itself declares the mapping — e.g. a VRM's
    /// <c>humanoid.humanBones</c> block (both the 0.x <c>extensions.VRM</c> and the 1.0
    /// <c>extensions.VRMC_vrm</c> layouts). Authoritative: consulted before user presets and
    /// shipped-profile detection, at confidence 1.0.
    /// </summary>
    Authored,
}
