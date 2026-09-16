#nullable enable annotations

using System;
using System.Collections.Generic;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Formats.Gltf;

/// <summary>
/// Translates a VRM file's authored <c>humanoid.humanBones</c> map (VRM bone name → glTF
/// node) into a canonical <see cref="MappingResult"/> over <see cref="BoneRole"/>s. Covers
/// both VRM 0.x and VRM 1.0 bone vocabularies — they share most names; the thumb chain is
/// the notable difference (0.x <c>thumbProximal/Intermediate/Distal</c> vs 1.0
/// <c>thumbMetacarpal/Proximal/Distal</c>), and both spellings are in the table.
/// </summary>
internal static class VrmHumanoid
{
    /// <summary>VRM humanoid bone name → canonical role, per the VRM specification
    /// (vrm-specification humanoid chapter; names are identical between 0.x's
    /// <c>VRM.humanoid.humanBones[].bone</c> values and 1.0's
    /// <c>VRMC_vrm.humanoid.humanBones</c> keys except for the thumb).</summary>
    private static readonly Dictionary<string, BoneRole> NameToRole = new(StringComparer.OrdinalIgnoreCase)
    {
        // torso / head
        ["hips"] = BoneRole.Hips,
        ["spine"] = BoneRole.Spine0,
        ["chest"] = BoneRole.Spine1,
        ["upperChest"] = BoneRole.Spine2,
        ["neck"] = BoneRole.Neck,
        ["head"] = BoneRole.Head,

        // arms
        ["leftShoulder"] = BoneRole.ClavicleL,
        ["rightShoulder"] = BoneRole.ClavicleR,
        ["leftUpperArm"] = BoneRole.UpperArmL,
        ["rightUpperArm"] = BoneRole.UpperArmR,
        ["leftLowerArm"] = BoneRole.LowerArmL,
        ["rightLowerArm"] = BoneRole.LowerArmR,
        ["leftHand"] = BoneRole.HandL,
        ["rightHand"] = BoneRole.HandR,

        // legs
        ["leftUpperLeg"] = BoneRole.UpperLegL,
        ["rightUpperLeg"] = BoneRole.UpperLegR,
        ["leftLowerLeg"] = BoneRole.LowerLegL,
        ["rightLowerLeg"] = BoneRole.LowerLegR,
        ["leftFoot"] = BoneRole.FootL,
        ["rightFoot"] = BoneRole.FootR,
        ["leftToes"] = BoneRole.ToeL,
        ["rightToes"] = BoneRole.ToeR,

        // thumb — VRM 1.0 names (metacarpal/proximal/distal)
        ["leftThumbMetacarpal"] = BoneRole.ThumbMetaL,
        ["rightThumbMetacarpal"] = BoneRole.ThumbMetaR,
        // thumb — shared/0.x names (0.x proximal is the first thumb bone; both specs call
        // the second-to-knuckle bone "proximal", so it maps to the proximal phalanx role)
        ["leftThumbProximal"] = BoneRole.ThumbProxL,
        ["rightThumbProximal"] = BoneRole.ThumbProxR,
        ["leftThumbIntermediate"] = BoneRole.ThumbMidL,
        ["rightThumbIntermediate"] = BoneRole.ThumbMidR,
        ["leftThumbDistal"] = BoneRole.ThumbDistL,
        ["rightThumbDistal"] = BoneRole.ThumbDistR,

        // fingers (identical names in 0.x and 1.0; VRM "little" = our "pinky")
        ["leftIndexProximal"] = BoneRole.IndexProxL,
        ["leftIndexIntermediate"] = BoneRole.IndexMidL,
        ["leftIndexDistal"] = BoneRole.IndexDistL,
        ["rightIndexProximal"] = BoneRole.IndexProxR,
        ["rightIndexIntermediate"] = BoneRole.IndexMidR,
        ["rightIndexDistal"] = BoneRole.IndexDistR,
        ["leftMiddleProximal"] = BoneRole.MiddleProxL,
        ["leftMiddleIntermediate"] = BoneRole.MiddleMidL,
        ["leftMiddleDistal"] = BoneRole.MiddleDistL,
        ["rightMiddleProximal"] = BoneRole.MiddleProxR,
        ["rightMiddleIntermediate"] = BoneRole.MiddleMidR,
        ["rightMiddleDistal"] = BoneRole.MiddleDistR,
        ["leftRingProximal"] = BoneRole.RingProxL,
        ["leftRingIntermediate"] = BoneRole.RingMidL,
        ["leftRingDistal"] = BoneRole.RingDistL,
        ["rightRingProximal"] = BoneRole.RingProxR,
        ["rightRingIntermediate"] = BoneRole.RingMidR,
        ["rightRingDistal"] = BoneRole.RingDistR,
        ["leftLittleProximal"] = BoneRole.PinkyProxL,
        ["leftLittleIntermediate"] = BoneRole.PinkyMidL,
        ["leftLittleDistal"] = BoneRole.PinkyDistL,
        ["rightLittleProximal"] = BoneRole.PinkyProxR,
        ["rightLittleIntermediate"] = BoneRole.PinkyMidR,
        ["rightLittleDistal"] = BoneRole.PinkyDistR,

        // leftEye / rightEye / jaw have no canonical role and are ignored (noted).
    };

    /// <summary>
    /// Builds the authored mapping for a parsed VRM document, or null when the document
    /// carries no humanoid bone map. <paramref name="nodeToSkeletonBone"/> resolves a glTF
    /// node index to the imported skeleton's bone index (−1 = node not part of the imported
    /// skeleton; the entry is then skipped with a note). The result is
    /// <see cref="MappingSource.Authored"/> at confidence 1.0 — it comes from the file
    /// itself.
    /// </summary>
    public static MappingResult? BuildMapping(GltfDocument document, Func<int, int> nodeToSkeletonBone)
    {
        if (document.VrmHumanBones is not { Count: > 0 } humanBones)
            return null;

        var result = new MappingResult("vrm", MappingSource.Authored) { Confidence = 1f };
        result.Notes.Add(
            $"Authored humanoid bone map read from the file's VRM {(document.VrmVersion == 1 ? "1.0" : "0.x")} extension.");

        List<string>? roleless = null;
        List<string>? unresolved = null;
        foreach (var (vrmName, nodeIndex) in humanBones)
        {
            if (!NameToRole.TryGetValue(vrmName, out var role))
            {
                (roleless ??= new List<string>()).Add(vrmName);
                continue; // eyes / jaw / unknown future names: no canonical role
            }
            var bone = nodeToSkeletonBone(nodeIndex);
            if (bone < 0)
            {
                (unresolved ??= new List<string>()).Add(vrmName);
                continue;
            }
            result.RoleToBone[role] = bone;
        }

        if (result.RoleToBone.Count == 0)
            return null; // nothing usable: fall back to the regular detection cascade

        roleless?.Sort(StringComparer.Ordinal);
        unresolved?.Sort(StringComparer.Ordinal);
        if (roleless is not null)
            result.Notes.Add("VRM humanoid bones without a canonical role ignored: "
                + string.Join(", ", roleless) + ".");
        if (unresolved is not null)
            result.Notes.Add("VRM humanoid bones whose nodes are not part of the imported "
                + "skeleton ignored: " + string.Join(", ", unresolved) + ".");
        return result;
    }
}
