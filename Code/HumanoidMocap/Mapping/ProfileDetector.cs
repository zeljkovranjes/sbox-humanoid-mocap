#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Mapping;

/// <summary>
/// Scores preset <see cref="Profile"/>s against a source skeleton and picks the best match.
/// Matching is exact on namespace-stripped, normalized names (see
/// <see cref="Profile.NormalizeName"/>), so helper bones (twists, share bones, IK markers)
/// are excluded simply by having no alias.
/// </summary>
public static class ProfileDetector
{
    /// <summary>Minimum confidence for <see cref="Detect"/> to accept a preset.</summary>
    public const float DetectionThreshold = 0.8f;

    /// <summary>
    /// Applies a profile to a skeleton: for each role, the first alias (in preference
    /// order) that names a not-yet-used source bone wins. Returns the full mapping with
    /// confidence and report notes.
    /// </summary>
    public static MappingResult Apply(Profile profile, SkeletonModel skeleton)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(skeleton);

        // Normalized stripped name → bone index; first occurrence wins on collisions.
        var byNormalizedName = new Dictionary<string, int>(skeleton.Count, StringComparer.Ordinal);
        foreach (var bone in skeleton.Bones)
            byNormalizedName.TryAdd(Profile.NormalizeName(profile.StripNamespace(bone.Name)), bone.Index);

        var result = new MappingResult(profile.Name, MappingSource.Preset);
        var usedBones = new HashSet<int>();
        var unmappedRoles = new List<BoneRole>();

        foreach (var role in Enum.GetValues<BoneRole>())
        {
            if (!profile.Aliases.TryGetValue(role, out var aliases))
                continue;

            var mapped = false;
            foreach (var alias in aliases)
            {
                if (byNormalizedName.TryGetValue(Profile.NormalizeName(alias), out var boneIndex)
                    && usedBones.Add(boneIndex))
                {
                    result.RoleToBone[role] = boneIndex;
                    mapped = true;
                    break;
                }
            }
            if (!mapped)
                unmappedRoles.Add(role);
        }

        if (unmappedRoles.Count > 0)
            result.Notes.Add($"Unmapped profile roles: {string.Join(", ", unmappedRoles)}");

        var ignored = skeleton.Bones.Where(b => !usedBones.Contains(b.Index)).Select(b => b.Name).ToList();
        if (ignored.Count > 0)
            result.Notes.Add(
                $"Ignored {ignored.Count} source bones: " +
                string.Join(", ", ignored.Take(12)) + (ignored.Count > 12 ? ", …" : string.Empty));

        result.Confidence = MappingConfidence.Compute(result.RoleToBone.Keys, profile.Aliases.Keys);
        return result;
    }

    /// <summary>Detection score of a profile for a skeleton (== the applied mapping's
    /// confidence): fraction of required humanoid roles matched, plus weighted optional
    /// roles. See <see cref="MappingConfidence"/>.</summary>
    public static float Score(Profile profile, SkeletonModel skeleton)
        => Apply(profile, skeleton).Confidence;

    /// <summary>
    /// Evaluates every profile (default: <see cref="ProfileLibrary.All"/>) and returns the
    /// best one with its full mapping, or null when none reaches
    /// <see cref="DetectionThreshold"/>.
    /// </summary>
    public static (Profile Profile, MappingResult Result)? Detect(
        SkeletonModel skeleton, IReadOnlyList<Profile>? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        profiles ??= ProfileLibrary.All;

        (Profile Profile, MappingResult Result)? best = null;
        foreach (var profile in profiles)
        {
            var result = Apply(profile, skeleton);
            if (best is null || result.Confidence > best.Value.Result.Confidence)
                best = (profile, result);
        }

        return best is { } candidate && candidate.Result.Confidence >= DetectionThreshold
            ? candidate
            : null;
    }
}

/// <summary>
/// Shared confidence model for preset detection and the auto-mapper. A humanoid mapping is
/// usable when 15 required slots are filled — Hips, at least one spine bone, Neck-or-Head,
/// and the upper-arm/lower-arm/hand/upper-leg/lower-leg/foot chains on both sides.
/// Everything else (more spine bones, clavicles, toes, fingers) is optional and contributes
/// the remaining weight.
/// </summary>
/// <remarks>
/// Every missing REQUIRED slot additionally penalizes the score multiplicatively
/// (<see cref="MissingRequiredPenalty"/>). This keeps detection honest: rich optional
/// coverage (e.g. 30 matching finger names) can never carry a preset over
/// <see cref="ProfileDetector.DetectionThreshold"/> when a required limb is unmatched —
/// with one slot missing the ceiling is (0.75·14/15 + 0.25)·0.75 ≈ 0.71 &lt; 0.8. A preset
/// only claims a rig when all 15 required slots resolve; otherwise the auto-mapper (which
/// sees the same names with more flexibility) takes over. Found via
/// Neutral_throw_ball_001__A057.bvh (NVIDIA SOMA skeleton): its mixamo-identical upper
/// body + finger names scored 0.90 while both upper legs were unmapped (SOMA's "LeftLeg"
/// is the thigh, mixamo's is the calf).
/// </remarks>
internal static class MappingConfidence
{
    private const float RequiredWeight = 0.75f;
    private const float OptionalWeight = 0.25f;

    /// <summary>Multiplicative score penalty applied once per missing required slot.</summary>
    private const float MissingRequiredPenalty = 0.75f;

    private static readonly BoneRole[] SpineRoles =
        { BoneRole.Spine0, BoneRole.Spine1, BoneRole.Spine2, BoneRole.Spine3, BoneRole.Spine4 };

    /// <summary>Roles consumed by required slots — excluded from the optional pool.</summary>
    private static readonly HashSet<BoneRole> RequiredPool = new()
    {
        BoneRole.Hips, BoneRole.Spine0, BoneRole.Neck, BoneRole.Head,
        BoneRole.UpperArmL, BoneRole.UpperArmR, BoneRole.LowerArmL, BoneRole.LowerArmR,
        BoneRole.HandL, BoneRole.HandR,
        BoneRole.UpperLegL, BoneRole.UpperLegR, BoneRole.LowerLegL, BoneRole.LowerLegR,
        BoneRole.FootL, BoneRole.FootR,
    };

    /// <summary>
    /// Confidence in [0, 1] of a set of mapped roles. <paramref name="definedRoles"/> is
    /// the universe of roles the mapper could have produced (a profile's alias keys, or all
    /// roles for the auto-mapper); optional coverage is measured against it.
    /// </summary>
    public static float Compute(IEnumerable<BoneRole> mappedRoles, IEnumerable<BoneRole> definedRoles)
    {
        var mapped = mappedRoles as ICollection<BoneRole> ?? mappedRoles.ToList();
        var mappedSet = new HashSet<BoneRole>(mapped);

        var requiredSatisfied = 0;
        const int requiredTotal = 15;
        if (mappedSet.Contains(BoneRole.Hips)) requiredSatisfied++;
        if (SpineRoles.Any(mappedSet.Contains)) requiredSatisfied++;
        if (mappedSet.Contains(BoneRole.Neck) || mappedSet.Contains(BoneRole.Head)) requiredSatisfied++;
        foreach (var role in RequiredPool)
        {
            if (role is BoneRole.Hips or BoneRole.Spine0 or BoneRole.Neck or BoneRole.Head)
                continue;
            if (mappedSet.Contains(role))
                requiredSatisfied++;
        }

        var requiredFraction = requiredSatisfied / (float)requiredTotal;
        var missingPenalty = MathF.Pow(MissingRequiredPenalty, requiredTotal - requiredSatisfied);

        var optionalDefined = definedRoles.Where(r => !RequiredPool.Contains(r)).Distinct().ToList();
        if (optionalDefined.Count == 0)
            return requiredFraction * missingPenalty;

        var optionalFraction = optionalDefined.Count(mappedSet.Contains) / (float)optionalDefined.Count;
        return (RequiredWeight * requiredFraction + OptionalWeight * optionalFraction) * missingPenalty;
    }
}
