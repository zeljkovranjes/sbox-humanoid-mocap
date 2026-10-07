#nullable enable annotations

using System.Collections.Generic;

namespace HumanoidMocap.Core.Mapping;

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
