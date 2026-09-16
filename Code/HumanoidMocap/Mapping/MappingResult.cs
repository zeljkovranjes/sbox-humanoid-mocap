#nullable enable annotations

using System.Collections.Generic;

namespace HumanoidMocap.Mapping;

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

/// <summary>
/// The outcome of mapping a source skeleton onto canonical <see cref="BoneRole"/>s:
/// role → source bone index, an overall confidence, and human-readable notes
/// (unmapped roles, ignored bones, ambiguities) for the mapping report.
/// </summary>
public sealed class MappingResult
{
    /// <summary>Creates an empty result; callers fill <see cref="RoleToBone"/> and
    /// <see cref="Confidence"/>.</summary>
    public MappingResult(string profileName, MappingSource source)
    {
        ProfileName = profileName;
        Source = source;
    }

    /// <summary>Resolved roles: canonical role → bone index in the source skeleton.</summary>
    public Dictionary<BoneRole, int> RoleToBone { get; } = new();

    /// <summary>Mapping confidence in [0, 1]; 1 means every required and optional role
    /// resolved unambiguously.</summary>
    public float Confidence { get; set; }

    /// <summary>Profile that produced the mapping — a preset name, or <c>"auto"</c> /
    /// <c>"topology"</c> for the auto-mapper stages.</summary>
    public string ProfileName { get; }

    /// <summary>Where the mapping came from.</summary>
    public MappingSource Source { get; }

    /// <summary>Report notes: unmapped roles, ignored source bones, ambiguities.</summary>
    public List<string> Notes { get; } = new();
}
