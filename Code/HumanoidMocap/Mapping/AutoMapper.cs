#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Mapping;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Maps unknown rigs to canonical <see cref="BoneRole"/>s (design §6, steps 2–3).
/// Stage A tokenizes bone names (namespace stripping, camel/snake/digit splitting, side
/// classification, per-role alias token sets) and validates candidates against the
/// hierarchy. When name evidence is too weak (confidence &lt; 0.5) stage B falls back to
/// pure topology: it finds the two mirror-symmetric branch points of the bone graph
/// (pelvis → legs, chest → arms), takes their lowest common ancestor as the hips, walks
/// the limb chains, and disambiguates left/right by the rest-pose lateral sign.
/// </summary>
public static class AutoMapper
{
    /// <summary>Below this stage-A confidence the topology fallback is attempted.</summary>
    private const float TopologyFallbackThreshold = 0.5f;

    /// <summary>Confidence ceiling of the nameless topology fallback (&lt; 1 by design —
    /// without names some assignments are educated guesses).</summary>
    private const float TopologyConfidenceScale = 0.85f;

    /// <summary>Maps a skeleton: name tokens first, topology fallback when names fail.</summary>
    public static MappingResult Map(SkeletonModel skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);

        var byName = MapByNames(skeleton);
        if (byName.Confidence >= TopologyFallbackThreshold)
            return byName;

        var byTopology = MapByTopology(skeleton);
        return byTopology.Confidence >= byName.Confidence ? byTopology : byName;
    }

    // ================================================================ stage A: name tokens

    private enum SideTag { None, L, R }

    private readonly record struct NameInfo(int Index, SideTag Side, string Core, int Digit);

    private static readonly string[] LeftTokens = { "l", "left", "lft" };
    private static readonly string[] RightTokens = { "r", "right", "rgt" };

    /// <summary>Tokens marking helper bones that must never receive a role.</summary>
    private static readonly string[] ExcludedTokens =
        { "twist", "roll", "share", "helper", "ik", "end", "nub", "tip", "dummy", "null", "control" };

    /// <summary>
    /// Tokens dropped while building the core (the bone is still considered): Auto-Rig Pro
    /// exports center bones with an <c>.x</c> suffix (<c>spine_01.x</c>, <c>neck.x</c>) and
    /// names the deform limb bones <c>*_stretch</c> (<c>thigh_stretch.l</c>,
    /// <c>arm_stretch.l</c>) — both are naming decoration, not identity.
    /// </summary>
    private static readonly string[] IgnoredCoreTokens = { "x", "stretch", "mch" };

    private static readonly string[] HipsCores = { "hips", "hip", "pelvis" };
    private static readonly string[] SpineCores =
        { "spine", "chest", "torso", "waist", "lowerback", "abdomen", "vertebrae", "vertebraet", "lumbar", "thoracic" };
    private static readonly string[] NeckCores = { "neck", "vertebraec", "cervical" };
    private static readonly string[] HeadCores = { "head", "skull", "cranium" };
    private static readonly string[] ClavicleCores =
        { "shoulder", "pivshoulder", "clavicle", "collar", "collarbone", "scapula" };
    // "armupper"/"legupper" etc. are the s&box-style reversed word order (arm_upper_L) -
    // custom rigs built for s&box reuse that convention with their own skeletons.
    private static readonly string[] UpperArmCores =
        { "upperarm", "uparm", "armupper", "arm", "bicep", "humerus" };
    private static readonly string[] LowerArmCores =
        { "forearm", "lowerarm", "lowarm", "armlower", "elbow", "radius", "ulna" };
    private static readonly string[] HandCores = { "hand", "wrist" };
    private static readonly string[] UpperLegCores =
        { "upleg", "upperleg", "legupper", "thigh", "femur" };
    private static readonly string[] LowerLegCores =
        { "lowerleg", "lowleg", "leglower", "calf", "shin", "knee", "tibia" };
    private static readonly string[] FootCores = { "foot", "ankle" };
    private static readonly string[] ToeCores = { "toe", "toebase", "toes", "ball" };

    private static MappingResult MapByNames(SkeletonModel skeleton)
    {
        var result = new MappingResult("auto", MappingSource.AutoName);
        var stripped = StripCommonNamespace(skeleton);

        // -------- classify every bone into role-family candidate buckets
        var hips = new List<NameInfo>();
        var spine = new List<NameInfo>();
        var neck = new List<NameInfo>();
        var head = new List<NameInfo>();
        var sided = new Dictionary<string, List<NameInfo>[]>
        {
            ["Clavicle"] = NewSided(), ["UpperArm"] = NewSided(), ["LowerArm"] = NewSided(),
            ["Hand"] = NewSided(), ["UpperLeg"] = NewSided(), ["LowerLeg"] = NewSided(),
            ["Foot"] = NewSided(), ["Toe"] = NewSided(),
        };
        var bareLeg = NewSided();
        var rootCandidates = new List<NameInfo>();
        var fingers = new Dictionary<(string Finger, SideTag Side), List<NameInfo>>();

        for (var i = 0; i < skeleton.Count; i++)
        {
            var tokens = Tokenize(stripped[i]);
            if (tokens.Count == 0 || tokens.Any(t => ExcludedTokens.Contains(t)))
                continue;

            var side = ClassifySide(tokens);
            var digit = -1;
            var coreBuilder = new StringBuilder();
            foreach (var token in tokens)
            {
                if (token.All(char.IsDigit))
                {
                    if (digit < 0)
                        digit = int.Parse(token, CultureInfo.InvariantCulture);
                    continue;
                }
                if (LeftTokens.Contains(token) || RightTokens.Contains(token))
                    continue;
                if (IgnoredCoreTokens.Contains(token))
                    continue;
                coreBuilder.Append(token);
            }
            var core = coreBuilder.ToString();
            if (core.Length == 0)
                continue;

            var resolvedSide = side != SideTag.None ? side : SideFromWorldX(skeleton, i);
            var info = new NameInfo(i, resolvedSide, core, digit);

            if (HipsCores.Contains(core))
            {
                // A NAME-sided singular "hip" (SMPL "L_Hip", classic BVH "LeftHip") is the
                // thigh — a child of the pelvis, not the pelvis itself. Only the name side
                // counts here: a slightly off-center pelvis must never become a leg.
                if (core == "hip" && side != SideTag.None)
                {
                    AddSided(sided["UpperLeg"], info, result);
                    continue;
                }
                // Some production rigs insert pelvis_L/pelvis_R helpers above the actual
                // upper-leg joints. They are neither the centered hips nor thighs; letting
                // either one win Hips invalidates every opposite-side chain.
                if (core == "pelvis" && side != SideTag.None)
                    continue;
                hips.Add(info);
                continue;
            }
            if (core == "root") { rootCandidates.Add(info); continue; }
            if (SpineCores.Contains(core)) { spine.Add(info); continue; }
            if (NeckCores.Contains(core)) { neck.Add(info); continue; }
            if (HeadCores.Contains(core)) { head.Add(info); continue; }
            if (ClavicleCores.Contains(core)) { AddSided(sided["Clavicle"], info, result); continue; }
            if (UpperArmCores.Contains(core)) { AddSided(sided["UpperArm"], info, result); continue; }
            if (LowerArmCores.Contains(core)) { AddSided(sided["LowerArm"], info, result); continue; }
            if (HandCores.Contains(core)) { AddSided(sided["Hand"], info, result); continue; }
            if (UpperLegCores.Contains(core)) { AddSided(sided["UpperLeg"], info, result); continue; }
            if (LowerLegCores.Contains(core)) { AddSided(sided["LowerLeg"], info, result); continue; }
            if (core == "leg") { AddSided(bareLeg, info, result); continue; }
            if (FootCores.Contains(core)) { AddSided(sided["Foot"], info, result); continue; }
            if (ToeCores.Contains(core)) { AddSided(sided["Toe"], info, result); continue; }

            if (TryClassifyFinger(info, out var finger))
            {
                var key = (finger, info.Side);
                if (info.Side == SideTag.None)
                {
                    result.Notes.Add($"Finger bone '{skeleton[i].Name}' has no resolvable side; ignored.");
                    continue;
                }
                if (!fingers.TryGetValue(key, out var list))
                    fingers[key] = list = new List<NameInfo>();
                list.Add(info);
            }
        }

        // -------- bare "leg" bones: lower leg when an upper-leg candidate exists on that side
        foreach (var side in new[] { SideTag.L, SideTag.R })
        {
            var legs = bareLeg[(int)side - 1];
            if (legs.Count == 0)
                continue;
            var upper = sided["UpperLeg"][(int)side - 1];
            var lower = sided["LowerLeg"][(int)side - 1];
            var ordered = legs.OrderBy(l => Depth(skeleton, l.Index)).ToList();
            if (upper.Count == 0)
            {
                upper.Add(ordered[0]);
                if (ordered.Count > 1 && lower.Count == 0)
                    lower.Add(ordered[1]);
            }
            else if (lower.Count == 0)
            {
                lower.Add(ordered[0]);
            }
        }

        // -------- "shoulder" bones: in classic BVH (Collar → Shoulder → Elbow → Wrist) the
        // shoulder IS the upper arm; in Mixamo (Shoulder → Arm → ForeArm) it is the
        // clavicle. When no upper-arm candidate exists, promote a shoulder out of the
        // clavicle bucket if a separate collar/clavicle-named bone covers that side, or if
        // the lower-arm candidate is a DIRECT child of the shoulder bone (no room for an
        // upper arm in between).
        foreach (var side in new[] { SideTag.L, SideTag.R })
        {
            var clavicles = sided["Clavicle"][(int)side - 1];
            var upperArms = sided["UpperArm"][(int)side - 1];
            if (upperArms.Count > 0)
                continue;
            var shoulders = clavicles.Where(c => c.Core == "shoulder").ToList();
            if (shoulders.Count == 0)
                continue;
            var lowerArms = sided["LowerArm"][(int)side - 1];
            var promote = clavicles.Any(c => c.Core != "shoulder")
                ? shoulders
                : shoulders
                    .Where(s => lowerArms.Any(l => skeleton[l.Index].ParentIndex == s.Index))
                    .ToList();
            foreach (var shoulder in promote)
            {
                clavicles.Remove(shoulder);
                upperArms.Add(shoulder);
            }
        }

        // -------- resolve buckets to roles
        var map = result.RoleToBone;

        if (hips.Count > 0)
            map[BoneRole.Hips] = ResolvePreferAncestor(skeleton, hips, result, "Hips");

        var orderedSpine = PrimaryNamedChain(skeleton, spine);
        for (var i = 0; i < orderedSpine.Count && i < 5; i++)
            map[BoneRole.Spine0 + i] = orderedSpine[i].Index;
        if (orderedSpine.Count > 5)
            result.Notes.Add($"Source spine chain has {orderedSpine.Count} bones; only 5 mapped.");

        if (neck.Count > 0)
        {
            var orderedNeck = neck.OrderBy(n => Depth(skeleton, n.Index)).ToList();
            map[BoneRole.Neck] = orderedNeck[0].Index;
            for (var i = 1; i < orderedNeck.Count; i++)
                result.Notes.Add($"Extra neck bone '{skeleton[orderedNeck[i].Index].Name}' left unmapped.");
        }
        if (head.Count > 0)
            map[BoneRole.Head] = head.OrderBy(h => Depth(skeleton, h.Index)).First().Index;

        foreach (var (family, buckets) in sided)
        {
            foreach (var side in new[] { SideTag.L, SideTag.R })
            {
                var bucket = buckets[(int)side - 1];
                if (bucket.Count == 0)
                    continue;
                var role = Enum.Parse<BoneRole>(family + side);
                var parentRole = ParentRoleFor(role);
                var coherent = parentRole is { } parent && map.TryGetValue(parent, out var parentBone)
                    ? bucket.Where(c => IsAncestor(skeleton, parentBone, c.Index)).ToList()
                    : bucket;
                if (ChildFamilyFor(family) is { } childFamily)
                {
                    var childCandidates = sided[childFamily][(int)side - 1];
                    var reachesChild = coherent
                        .Where(c => childCandidates.Any(child => IsAncestor(skeleton, c.Index, child.Index)))
                        .ToList();
                    if (reachesChild.Count > 0)
                        coherent = reachesChild;
                }
                map[role] = ResolvePreferAncestor(
                    skeleton, coherent.Count > 0 ? coherent : bucket, result, role.ToString());
            }
        }

        // -------- hips fallback for rigs without a hips/pelvis name: Auto-Rig Pro calls the
        // pelvis "root.x" (under a ground bone "root"). Only when the primary cores found
        // nothing, pick the DEEPEST "root"-named bone that is still an ancestor of every
        // mapped upper leg / spine start — that is the actual pelvis, not the ground root.
        if (!map.ContainsKey(BoneRole.Hips) && rootCandidates.Count > 0)
        {
            var anchors = new List<int>();
            if (map.TryGetValue(BoneRole.UpperLegL, out var legL)) anchors.Add(legL);
            if (map.TryGetValue(BoneRole.UpperLegR, out var legR)) anchors.Add(legR);
            if (map.TryGetValue(BoneRole.Spine0, out var spine0)) anchors.Add(spine0);
            if (anchors.Count > 0)
            {
                NameInfo? pick = null;
                foreach (var candidate in rootCandidates)
                {
                    if (!anchors.All(a => IsAncestor(skeleton, candidate.Index, a)))
                        continue;
                    if (pick is null || Depth(skeleton, candidate.Index) > Depth(skeleton, pick.Value.Index))
                        pick = candidate;
                }
                if (pick is { } hipsPick)
                {
                    map[BoneRole.Hips] = hipsPick.Index;
                    result.Notes.Add(
                        $"Hips mapped to '{skeleton[hipsPick.Index].Name}': no hips/pelvis-named bone "
                        + "found; used the deepest 'root' bone above the mapped legs/spine.");
                }
            }
        }

        foreach (var ((finger, side), candidates) in fingers)
        {
            foreach (var info in candidates)
            {
                var segment = SegmentOf(info);
                if (segment is null)
                {
                    result.Notes.Add($"Finger bone '{skeleton[info.Index].Name}' has no phalanx digit; ignored.");
                    continue;
                }
                var role = Enum.Parse<BoneRole>(finger + segment + side);
                if (!map.TryAdd(role, info.Index))
                    result.Notes.Add($"Ambiguous {role}: kept '{map[role]}', ignored '{skeleton[info.Index].Name}'.");
            }
        }

        CompleteFingersByTopology(skeleton, result);
        ValidateChains(skeleton, result);

        result.Confidence = MappingConfidence.Compute(map.Keys, Enum.GetValues<BoneRole>());
        return result;
    }

    /// <summary>Completes an otherwise reliable name map when the hand has clearly
    /// articulated child chains but their exporter-specific names carry no finger words.</summary>
    internal static void CompleteFingersByTopology(SkeletonModel skeleton, MappingResult result)
    {
        var children = BuildChildren(skeleton);
        var subtreeDepth = BuildSubtreeDepth(skeleton, children);
        var positions = skeleton.RestWorld.Select(x => x.Pos).ToArray();
        foreach (var side in new[] { "L", "R" })
        {
            if (!result.RoleToBone.TryGetValue(
                Enum.Parse<BoneRole>("Hand" + side), out var hand))
                continue;
            var inferred = new MappingResult("topology", MappingSource.AutoTopology);
            MapFingersByTopology(inferred, skeleton, children, subtreeDepth, positions, hand, side);
            // Fill only entirely missing chains. Existing profile/name assignments win,
            // and a bone already assigned to another digit must never be reused.
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                var chain = inferred.RoleToBone.Where(p => p.Key.ToString().StartsWith(finger)
                    && p.Key.ToString().EndsWith(side)).ToArray();
                if (result.RoleToBone.Keys.Any(r => r.ToString().StartsWith(finger)
                    && r.ToString().EndsWith(side))
                    || chain.Any(p => result.RoleToBone.ContainsValue(p.Value)))
                    continue;
                foreach (var pair in chain)
                    result.RoleToBone.Add(pair.Key, pair.Value);
            }
        }
    }

    /// <summary>Removes the common two-leaf hand placeholders that Biped-style naming can
    /// otherwise mistake for articulated fingers. Explicit/user mappings remain authoritative;
    /// this is used only on automatically detected profiles.</summary>
    internal static void PruneNonArticulatedFingerStubs(
        SkeletonModel skeleton, MappingResult result)
    {
        var children = BuildChildren(skeleton);
        foreach (var side in new[] { "L", "R" })
        {
            var roles = result.RoleToBone.Keys.Where(role => IsFingerRole(role, side)).ToArray();
            if (roles.Length == 0 || roles.Length > 2
                || roles.Any(role => children[result.RoleToBone[role]].Count > 0))
                continue;
            foreach (var role in roles)
                result.RoleToBone.Remove(role);
            result.Notes.Add(
                $"Hand {side}: {roles.Length} leaf placeholder bone(s) left unmapped; "
                + "no articulated finger chain was present.");
        }
    }

    /// <summary>Adds the common one-joint <c>LThumb</c>/<c>RThumb</c> channels to an
    /// otherwise authoritative body profile. The joint must have real child/end-site
    /// geometry, so leaf placeholders remain excluded.</summary>
    internal static void CompleteSingleJointThumbs(
        SkeletonModel skeleton, MappingResult result)
    {
        var byName = MapByNames(skeleton);
        var used = result.RoleToBone.Values.ToHashSet();
        var added = 0;
        foreach (var role in new[] { BoneRole.ThumbProxL, BoneRole.ThumbProxR })
        {
            if (result.RoleToBone.ContainsKey(role)
                || !byName.RoleToBone.TryGetValue(role, out var bone)
                || used.Contains(bone))
                continue;

            var hasSegment = false;
            for (var child = 0; child < skeleton.Count; child++)
            {
                if (skeleton[child].ParentIndex == bone
                    && (skeleton.RestWorld[child].Pos - skeleton.RestWorld[bone].Pos)
                        .LengthSquared() > 1e-8f)
                {
                    hasSegment = true;
                    break;
                }
            }
            if (!hasSegment)
                continue;

            result.RoleToBone[role] = bone;
            used.Add(bone);
            added++;
        }
        if (added > 0)
            result.Notes.Add($"Mapped {added} articulated single-joint thumb channel(s) by name and end-site geometry.");
    }

    private static bool IsFingerRole(BoneRole role, string side)
    {
        var name = role.ToString();
        return name.EndsWith(side, StringComparison.Ordinal)
            && (name.StartsWith("Thumb", StringComparison.Ordinal)
                || name.StartsWith("Index", StringComparison.Ordinal)
                || name.StartsWith("Middle", StringComparison.Ordinal)
                || name.StartsWith("Ring", StringComparison.Ordinal)
                || name.StartsWith("Pinky", StringComparison.Ordinal));
    }

    private static List<NameInfo>[] NewSided() => new[] { new List<NameInfo>(), new List<NameInfo>() };

    private static void AddSided(List<NameInfo>[] buckets, NameInfo info, MappingResult result)
    {
        if (info.Side == SideTag.None)
            return; // no name side and centered rest position — cannot attribute
        buckets[(int)info.Side - 1].Add(info);
    }

    private static SideTag ClassifySide(List<string> tokens)
    {
        var left = tokens.Any(t => LeftTokens.Contains(t));
        var right = tokens.Any(t => RightTokens.Contains(t));
        if (left == right)
            return SideTag.None;
        return left ? SideTag.L : SideTag.R;
    }

    /// <summary>Side tie-break by rest world lateral sign (left = +X in both fixture
    /// conventions: Mixamo Y-up and ActorCore/UE Z-up rigs keep X lateral).</summary>
    private static SideTag SideFromWorldX(SkeletonModel skeleton, int boneIndex)
    {
        var x = skeleton.RestWorld[boneIndex].Pos.X;
        const float tolerance = 0.5f; // cm
        if (x > tolerance) return SideTag.L;
        if (x < -tolerance) return SideTag.R;
        return SideTag.None;
    }

    private static bool TryClassifyFinger(NameInfo info, out string finger)
    {
        finger = string.Empty;
        var core = info.Core;
        if (core.Contains("toe"))
            return false; // BigToe1 / RingToe1 style toe bones are not fingers
        if (core.Contains("thumb")) finger = "Thumb";
        else if (core.Contains("index")) finger = "Index";
        else if (core.Contains("middle")) finger = "Middle";
        else if (core.Contains("pinky") || core.Contains("little")) finger = "Pinky";
        else if (core.Contains("ring")) finger = "Ring";
        else if (core == "mid" || core == "handmid" || core.Contains("fingmid")) finger = "Middle";
        else return false;
        return info.Digit >= 0 || core == "thumb" || core.Contains("meta") || core.Contains("palm")
            || HasAlphabeticFingerSegment(core, 'a')
            || HasAlphabeticFingerSegment(core, 'b')
            || HasAlphabeticFingerSegment(core, 'c');
    }

    private static string? SegmentOf(NameInfo info) => info switch
    {
        _ when info.Core.Contains("meta") => "Meta",
        _ when info.Core.Contains("palm") => "Meta",
        _ when HasAlphabeticFingerSegment(info.Core, 'a') => "Prox",
        _ when HasAlphabeticFingerSegment(info.Core, 'b') => "Mid",
        _ when HasAlphabeticFingerSegment(info.Core, 'c') => "Dist",
        _ when info.Core == "thumb" && info.Digit < 0 => "Prox",
        // Auto-Rig Pro chains: index1_base → index1 → index2 → index3. The '_base' bone
        // is the metacarpal inside the palm; its digit belongs to the FINGER, not the
        // phalanx (digit-only reading collided it with the true first knuckle, whose
        // curls then bent the palm while the knuckle froze — "the fingers look weird").
        _ when info.Core.Contains("base") => "Meta",
        { Digit: 0 } => "Meta",
        { Digit: 1 } => "Prox",
        { Digit: 2 } => "Mid",
        { Digit: 3 } => "Dist",
        _ => null, // 4+ are fingertip end markers
    };

    private static bool HasAlphabeticFingerSegment(string core, char segment)
    {
        foreach (var finger in new[] { "thumb", "index", "middle", "mid", "ring", "pinky", "little" })
        {
            if (core.EndsWith(finger + segment, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static BoneRole? ParentRoleFor(BoneRole role) => role switch
    {
        BoneRole.ClavicleL or BoneRole.UpperLegL => BoneRole.Hips,
        BoneRole.ClavicleR or BoneRole.UpperLegR => BoneRole.Hips,
        BoneRole.UpperArmL => BoneRole.ClavicleL,
        BoneRole.UpperArmR => BoneRole.ClavicleR,
        BoneRole.LowerArmL => BoneRole.UpperArmL,
        BoneRole.LowerArmR => BoneRole.UpperArmR,
        BoneRole.HandL => BoneRole.LowerArmL,
        BoneRole.HandR => BoneRole.LowerArmR,
        BoneRole.LowerLegL => BoneRole.UpperLegL,
        BoneRole.LowerLegR => BoneRole.UpperLegR,
        BoneRole.FootL => BoneRole.LowerLegL,
        BoneRole.FootR => BoneRole.LowerLegR,
        BoneRole.ToeL => BoneRole.FootL,
        BoneRole.ToeR => BoneRole.FootR,
        _ => null,
    };

    private static string? ChildFamilyFor(string family) => family switch
    {
        "Clavicle" => "UpperArm",
        "UpperArm" => "LowerArm",
        "LowerArm" => "Hand",
        "UpperLeg" => "LowerLeg",
        "LowerLeg" => "Foot",
        "Foot" => "Toe",
        _ => null,
    };

    /// <summary>Chooses the longest connected chain among same-family axial candidates.
    /// This rejects disconnected chest/control pivots without depending on exporter names.</summary>
    private static List<NameInfo> PrimaryNamedChain(
        SkeletonModel skeleton, List<NameInfo> candidates)
    {
        if (candidates.Count < 2)
            return candidates.OrderBy(c => Depth(skeleton, c.Index)).ToList();
        var root = candidates
            .OrderByDescending(c => candidates.Count(other => other.Index == c.Index
                || IsAncestor(skeleton, c.Index, other.Index)))
            .ThenBy(c => Depth(skeleton, c.Index))
            .First();
        return candidates
            .Where(c => c.Index == root.Index || IsAncestor(skeleton, root.Index, c.Index))
            .OrderBy(c => Depth(skeleton, c.Index))
            .ToList();
    }

    /// <summary>Resolves multiple candidates for one role: prefer the candidate that is an
    /// ancestor of all others (e.g. CC_Base_Hip over CC_Base_Pelvis), else the first.</summary>
    private static int ResolvePreferAncestor(
        SkeletonModel skeleton, List<NameInfo> candidates, MappingResult result, string roleName)
    {
        if (candidates.Count == 1)
            return candidates[0].Index;

        var pick = candidates.FirstOrDefault(
            c => candidates.All(o => o.Index == c.Index || IsAncestor(skeleton, c.Index, o.Index)),
            candidates[0]);
        result.Notes.Add(
            $"Ambiguous {roleName}: chose '{skeleton[pick.Index].Name}' among " +
            string.Join(", ", candidates.Select(c => $"'{skeleton[c.Index].Name}'")));
        return pick.Index;
    }

    // -------- namespace stripping

    /// <summary>
    /// Removes the per-bone FBX namespace (everything up to the last ':') and then the
    /// longest common '_'-terminated prefix shared by &gt;70% of bones (e.g.
    /// <c>CC_Base_</c>, <c>Bip01_</c>, <c>bone_</c>).
    /// </summary>
    private static string[] StripCommonNamespace(SkeletonModel skeleton)
    {
        var names = new string[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
        {
            var name = skeleton[i].Name;
            var colon = name.LastIndexOf(':');
            names[i] = colon >= 0 ? name[(colon + 1)..] : name;
        }

        var prefixCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var underscores = 0;
            for (var i = 0; i < name.Length && underscores < 3; i++)
            {
                if (name[i] != '_')
                    continue;
                underscores++;
                var prefix = name[..(i + 1)];
                prefixCounts[prefix] = prefixCounts.GetValueOrDefault(prefix) + 1;
            }
        }

        var threshold = (int)Math.Ceiling(skeleton.Count * 0.7);
        var best = prefixCounts
            .Where(kv => kv.Value >= threshold && kv.Key.Length >= 3)
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        if (best is null)
            return names;

        for (var i = 0; i < names.Length; i++)
        {
            if (names[i].Length > best.Length && names[i].StartsWith(best, StringComparison.Ordinal))
                names[i] = names[i][best.Length..];
        }
        return names;
    }

    // -------- tokenization

    /// <summary>Splits a bone name on separators, camel-case boundaries and letter/digit
    /// boundaries; returns lower-case tokens ("LeftForeArm" → left, fore, arm; "spine03" →
    /// spine, 03).</summary>
    private static List<string> Tokenize(string name)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char previous = '\0';
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                previous = '\0';
                continue;
            }
            if (current.Length > 0)
            {
                var boundary = char.IsDigit(c) != char.IsDigit(previous)
                    || (char.IsUpper(c) && char.IsLower(previous))
                    // Acronym/single-side prefix before a Pascal word: LThumb → L, Thumb;
                    // FBXNode → FBX, Node. Without this, one-joint BVH thumbs lose both
                    // their side and their otherwise unambiguous finger name.
                    || (char.IsUpper(c) && char.IsUpper(previous)
                        && i + 1 < name.Length && char.IsLower(name[i + 1]));
                if (boundary)
                    Flush();
            }
            current.Append(char.ToLowerInvariant(c));
            previous = c;
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

    // -------- topology validation of name-derived candidates

    private static readonly BoneRole[][] ValidationChains = BuildValidationChains();

    private static BoneRole[][] BuildValidationChains()
    {
        var chains = new List<BoneRole[]>
        {
            new[]
            {
                BoneRole.Hips, BoneRole.Spine0, BoneRole.Spine1, BoneRole.Spine2,
                BoneRole.Spine3, BoneRole.Spine4, BoneRole.Neck,
            },
        };
        foreach (var side in new[] { "L", "R" })
        {
            chains.Add(new[]
            {
                BoneRole.Hips,
                Enum.Parse<BoneRole>("UpperLeg" + side), Enum.Parse<BoneRole>("LowerLeg" + side),
                Enum.Parse<BoneRole>("Foot" + side), Enum.Parse<BoneRole>("Toe" + side),
            });
            chains.Add(new[]
            {
                BoneRole.Hips,
                Enum.Parse<BoneRole>("Clavicle" + side), Enum.Parse<BoneRole>("UpperArm" + side),
                Enum.Parse<BoneRole>("LowerArm" + side), Enum.Parse<BoneRole>("Hand" + side),
            });
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                chains.Add(new[]
                {
                    Enum.Parse<BoneRole>("Hand" + side),
                    Enum.Parse<BoneRole>(finger + "Meta" + side), Enum.Parse<BoneRole>(finger + "Prox" + side),
                    Enum.Parse<BoneRole>(finger + "Mid" + side), Enum.Parse<BoneRole>(finger + "Dist" + side),
                });
            }
        }
        return chains.ToArray();
    }

    /// <summary>
    /// Hierarchy consistency: along each anatomical chain every mapped role must descend
    /// from the previously mapped one (hips above legs and spine, chest branch to arms,
    /// fingers below the hand). Violating assignments are dropped with a note.
    /// </summary>
    private static void ValidateChains(SkeletonModel skeleton, MappingResult result)
    {
        foreach (var chain in ValidationChains)
        {
            var previous = -1;
            foreach (var role in chain)
            {
                if (!result.RoleToBone.TryGetValue(role, out var bone))
                    continue;
                if (previous >= 0 && !IsAncestor(skeleton, previous, bone))
                {
                    result.RoleToBone.Remove(role);
                    result.Notes.Add(
                        $"Dropped {role} ('{skeleton[bone].Name}'): not a descendant of " +
                        $"'{skeleton[previous].Name}'.");
                    continue;
                }
                previous = bone;
            }
        }
    }

    // ================================================================ stage B: topology fallback

    private static MappingResult MapByTopology(SkeletonModel skeleton)
    {
        var result = new MappingResult("topology", MappingSource.AutoTopology);
        result.Notes.Add("Bone names unusable; mapped by hierarchy/geometry only.");
        if (skeleton.Count < 10)
        {
            result.Notes.Add("Too few bones for topology matching.");
            return result;
        }

        var children = BuildChildren(skeleton);
        var depth = new int[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
            depth[i] = skeleton[i].ParentIndex < 0 ? 0 : depth[skeleton[i].ParentIndex] + 1;
        var subtreeDepth = BuildSubtreeDepth(skeleton, children);
        var positions = skeleton.RestWorld.Select(x => x.Pos).ToArray();

        var (lateralAxis, tolerance) = DetectLateralAxis(positions);

        var pairs = FindSymmetricPairs(skeleton, children, subtreeDepth, positions, lateralAxis, tolerance);
        if (pairs.Count < 2)
        {
            result.Notes.Add($"Found {pairs.Count} symmetric limb pair(s); need legs and arms.");
            return result;
        }

        // The two deepest pairs are the limb attachments (legs at the pelvis region, arms
        // at the chest).
        var primary = pairs
            .OrderByDescending(p => Math.Min(subtreeDepth[p.Left], subtreeDepth[p.Right]))
            .Take(2)
            .ToArray();
        if (LowestCommonAncestor(skeleton, depth, primary[0].Node, primary[1].Node) is not { } lca)
        {
            result.Notes.Add(
                "Limb pairs share no common ancestor (disconnected skeleton roots); no valid hips.");
            return result;
        }
        var legsPair = PickLegsPair(
            primary[0], primary[1], children, subtreeDepth, positions, lateralAxis, depth, lca);
        var armsPair = legsPair == primary[0] ? primary[1] : primary[0];

        var map = result.RoleToBone;
        map[BoneRole.Hips] = lca;

        // Spine: path from the hips down to the chest (arms attachment), Spine0 first.
        var chest = armsPair.Node;
        var spinePath = PathBetween(skeleton, lca, chest);
        for (var i = 0; i < spinePath.Count && i < 5; i++)
            map[BoneRole.Spine0 + i] = spinePath[i];
        if (spinePath.Count > 5)
            result.Notes.Add($"Spine path has {spinePath.Count} bones; only 5 mapped.");

        // Neck/head: the most lateral-centered chest child that is not an arm root.
        var neckStart = children[chest]
            .Where(c => c != armsPair.Left && c != armsPair.Right)
            .OrderBy(c => Math.Abs(SubtreeMeanLateral(children, positions, lateralAxis, c)))
            .Cast<int?>()
            .FirstOrDefault();
        if (neckStart is { } neckRoot)
        {
            var neckChain = PrimaryChain(children, subtreeDepth, neckRoot);
            if (neckChain.Count >= 2)
            {
                map[BoneRole.Neck] = neckChain[0];
                map[BoneRole.Head] = neckChain[1];
            }
            else
            {
                map[BoneRole.Head] = neckChain[0];
            }
        }
        else
        {
            result.Notes.Add("No neck/head chain found at the chest branch.");
        }

        // Legs: walk each leg chain — upper leg, lower leg, foot, toe.
        foreach (var (root, side) in SidedRoots(legsPair))
        {
            var chain = PrimaryChain(children, subtreeDepth, root);
            var roles = new[] { "UpperLeg", "LowerLeg", "Foot", "Toe" };
            for (var i = 0; i < roles.Length && i < chain.Count; i++)
                map[Enum.Parse<BoneRole>(roles[i] + side)] = chain[i];
        }

        // Arms: locate the hand (first chain node with ≥2 non-trivial child chains —
        // fingers), then assign backwards: lower arm, upper arm, clavicle.
        foreach (var (root, side) in SidedRoots(armsPair))
        {
            var chain = PrimaryChain(children, subtreeDepth, root);
            var handIndex = -1;
            for (var i = 0; i < chain.Count; i++)
            {
                if (children[chain[i]].Count(c => subtreeDepth[c] >= 2) >= 2)
                {
                    handIndex = i;
                    break;
                }
            }
            if (handIndex < 0 && chain.Count >= 3)
                handIndex = chain.Count - 1;
            if (handIndex < 2)
            {
                result.Notes.Add($"Arm chain on side {side} too short to resolve.");
                continue;
            }

            map[Enum.Parse<BoneRole>("Hand" + side)] = chain[handIndex];
            map[Enum.Parse<BoneRole>("LowerArm" + side)] = chain[handIndex - 1];
            map[Enum.Parse<BoneRole>("UpperArm" + side)] = chain[handIndex - 2];
            if (handIndex >= 3)
                map[Enum.Parse<BoneRole>("Clavicle" + side)] = chain[handIndex - 3];

            MapFingersByTopology(
                result, skeleton, children, subtreeDepth, positions, chain[handIndex], side);
        }

        result.Confidence =
            TopologyConfidenceScale * MappingConfidence.Compute(map.Keys, Enum.GetValues<BoneRole>());
        return result;
    }

    private readonly record struct SymmetricPair(int Node, int Left, int Right);

    private static IEnumerable<(int Root, string Side)> SidedRoots(SymmetricPair pair)
    {
        yield return (pair.Left, "L");
        yield return (pair.Right, "R");
    }

    /// <summary>
    /// Decides geometrically which of the two limb pairs is the legs: the legs' chain end
    /// effectors (feet/toes) sit at the very edge of the skeleton's vertical extent, while
    /// the arms' ends (hands) lie well inside it in any rest pose (T- or A-pose). The up
    /// axis is estimated from the rest extents (the non-lateral axis spanning the most);
    /// comparing each end's distance to the NEAREST extent boundary makes the rule
    /// independent of the up axis' sign. Near-ties fall back to pair-root extremity and
    /// finally to hierarchy depth — never to construction order alone.
    /// </summary>
    private static SymmetricPair PickLegsPair(
        SymmetricPair p0, SymmetricPair p1, List<int>[] children, int[] subtreeDepth,
        Vector3[] positions, int lateralAxis, int[] depth, int lca)
    {
        var min = positions.Aggregate(Vector3.Min);
        var max = positions.Aggregate(Vector3.Max);

        var upAxis = -1;
        var bestExtent = -1f;
        for (var axis = 0; axis < 3; axis++)
        {
            if (axis == lateralAxis)
                continue;
            var extent = Component(max, axis) - Component(min, axis);
            if (extent > bestExtent)
            {
                bestExtent = extent;
                upAxis = axis;
            }
        }

        var lo = Component(min, upAxis);
        var hi = Component(max, upAxis);

        // Distance of a pair's chain end effectors to the nearest vertical extent boundary.
        float BoundaryDistance(SymmetricPair p)
        {
            var d = float.MaxValue;
            foreach (var (root, _) in SidedRoots(p))
            {
                var chain = PrimaryChain(children, subtreeDepth, root);
                var e = Component(positions[chain[^1]], upAxis);
                d = MathF.Min(d, MathF.Min(e - lo, hi - e));
            }
            return d;
        }

        var tie = MathF.Max(1f, 0.02f * bestExtent);

        var d0 = BoundaryDistance(p0);
        var d1 = BoundaryDistance(p1);
        if (MathF.Abs(d0 - d1) > tie)
            return d0 < d1 ? p0 : p1;

        // Near-tie on end effectors: the pelvis attachment sits nearer the feet end of the
        // extent than the chest does.
        var n0 = MathF.Min(Component(positions[p0.Node], upAxis) - lo,
                           hi - Component(positions[p0.Node], upAxis));
        var n1 = MathF.Min(Component(positions[p1.Node], upAxis) - lo,
                           hi - Component(positions[p1.Node], upAxis));
        if (MathF.Abs(n0 - n1) > tie)
            return n0 < n1 ? p0 : p1;

        // Geometric dead heat (degenerate rig): fall back to the hierarchy heuristic — the
        // pair attached closer to the common ancestor is the legs.
        return depth[p0.Node] - depth[lca] <= depth[p1.Node] - depth[lca] ? p0 : p1;
    }

    /// <summary>
    /// Lateral axis = the coordinate axis across which the rest pose is most mirror
    /// symmetric. The tolerance scales with skeleton size.
    /// </summary>
    private static (int Axis, float Tolerance) DetectLateralAxis(Vector3[] positions)
    {
        var min = positions.Aggregate(Vector3.Min);
        var max = positions.Aggregate(Vector3.Max);
        var tolerance = MathF.Max(1f, 0.02f * (max - min).Length());

        var bestAxis = 0;
        var bestScore = -1f;
        for (var axis = 0; axis < 3; axis++)
        {
            var considered = 0;
            var matched = 0;
            foreach (var p in positions)
            {
                if (MathF.Abs(Component(p, axis)) <= tolerance)
                    continue;
                considered++;
                var mirrored = Mirror(p, axis);
                if (positions.Any(q => (q - mirrored).Length() < tolerance))
                    matched++;
            }
            var score = considered == 0 ? 0f : matched / (float)considered;
            if (score > bestScore)
            {
                bestScore = score;
                bestAxis = axis;
            }
        }
        return (bestAxis, tolerance);
    }

    /// <summary>
    /// Finds nodes with two children whose subtrees are mirror images of each other across
    /// the lateral axis and at least 3 bones deep (limb chains): the pelvis region (legs)
    /// and the chest (arms). Shallow symmetric decorations (eyes, breast helpers) are
    /// filtered by the depth requirement. Left = greater mean lateral coordinate.
    /// </summary>
    private static List<SymmetricPair> FindSymmetricPairs(
        SkeletonModel skeleton, List<int>[] children, int[] subtreeDepth,
        Vector3[] positions, int lateralAxis, float tolerance)
    {
        var pairs = new List<SymmetricPair>();
        for (var node = 0; node < skeleton.Count; node++)
        {
            var kids = children[node];
            for (var a = 0; a < kids.Count; a++)
            {
                for (var b = a + 1; b < kids.Count; b++)
                {
                    var ca = kids[a];
                    var cb = kids[b];
                    if (subtreeDepth[ca] < 3 || subtreeDepth[cb] < 3)
                        continue;
                    var subtreeA = CollectSubtree(children, ca);
                    var subtreeB = CollectSubtree(children, cb);
                    if (subtreeA.Count != subtreeB.Count)
                        continue;
                    if (!MirrorsOnto(subtreeA, subtreeB, positions, lateralAxis, tolerance)
                        || !MirrorsOnto(subtreeB, subtreeA, positions, lateralAxis, tolerance))
                        continue;

                    var meanA = SubtreeMeanLateral(children, positions, lateralAxis, ca);
                    var meanB = SubtreeMeanLateral(children, positions, lateralAxis, cb);
                    pairs.Add(meanA >= meanB
                        ? new SymmetricPair(node, ca, cb)
                        : new SymmetricPair(node, cb, ca));
                }
            }
        }
        return pairs;
    }

    private static bool MirrorsOnto(
        List<int> from, List<int> to, Vector3[] positions, int lateralAxis, float tolerance)
    {
        foreach (var a in from)
        {
            var mirrored = Mirror(positions[a], lateralAxis);
            var matched = false;
            foreach (var b in to)
            {
                if ((positions[b] - mirrored).Length() < tolerance)
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Finger assignment without names: the thumb is the child chain rooted closest to the
    /// hand; the remaining chains are ordered along the splay axis (the line through the
    /// two most-separated finger roots, starting at the thumb end) as index, middle, ring,
    /// pinky.
    /// </summary>
    private static void MapFingersByTopology(
        MappingResult result, SkeletonModel skeleton, List<int>[] children, int[] subtreeDepth,
        Vector3[] positions, int hand, string side)
    {
        var chains = children[hand]
            .Where(c => subtreeDepth[c] >= 2)
            .Select(c => PrimaryChain(children, subtreeDepth, c))
            .ToList();
        if (chains.Count < 2 || chains.Count > 5)
        {
            result.Notes.Add($"Hand {side}: {chains.Count} finger chains; fingers not mapped.");
            return;
        }

        // Prefer explicit thumb evidence. Some DCC rigs call the digits finger1..5 but
        // uniquely omit a metacarpal for finger1 (the thumb); that hierarchy is stronger
        // evidence than root distance, which can put a middle metacarpal closest to wrist.
        var namedThumbs = chains.Where(chain => chain.Any(bone =>
            skeleton[bone].Name.Contains("thumb", StringComparison.OrdinalIgnoreCase))).ToList();
        var withoutMetacarpal = chains.Where(chain => !IsMetacarpal(skeleton[chain[0]].Name)).ToList();
        var thumbChain = namedThumbs.Count == 1
            ? namedThumbs[0]
            : withoutMetacarpal.Count == 1 && chains.Count(chain =>
                IsMetacarpal(skeleton[chain[0]].Name)) == chains.Count - 1
                ? withoutMetacarpal[0]
                : chains.OrderBy(c => (positions[c[0]] - positions[hand]).Length()).First();
        var others = chains.Where(c => c != thumbChain).ToList();

        if (others.Count > 1)
        {
            var numbered = others.Select(chain => (Chain: chain,
                Number: FingerNumber(skeleton, chain))).ToList();
            if (numbered.All(entry => entry.Number >= 0)
                && numbered.Select(entry => entry.Number).Distinct().Count() == numbered.Count)
            {
                others = numbered.OrderBy(entry => entry.Number)
                    .Select(entry => entry.Chain).ToList();
            }
            else
            {
                // Splay line through the two farthest finger roots, oriented thumb-first.
                var (u, v) = others
                    .SelectMany(x => others.Select(y => (x, y)))
                    .OrderByDescending(p => (positions[p.x[0]] - positions[p.y[0]]).Length())
                    .First();
                var start = (positions[u[0]] - positions[thumbChain[0]]).Length()
                    <= (positions[v[0]] - positions[thumbChain[0]]).Length() ? u : v;
                var direction = positions[(start == u ? v : u)[0]] - positions[start[0]];
                others = others
                    .OrderBy(c => Vector3.Dot(positions[c[0]] - positions[start[0]], direction))
                    .ToList();
            }
        }

        AssignFingerChain(result, skeleton, thumbChain, "Thumb", side);
        var fingerNames = new[] { "Index", "Middle", "Ring", "Pinky" };
        for (var i = 0; i < others.Count && i < fingerNames.Length; i++)
            AssignFingerChain(result, skeleton, others[i], fingerNames[i], side);
    }

    private static void AssignFingerChain(
        MappingResult result, SkeletonModel skeleton, List<int> chain, string finger, string side)
    {
        // A named metacarpal is a real palm segment; otherwise 4+ joints commonly end in
        // an exported tip marker and the first three are the phalanges.
        var segments = IsMetacarpal(skeleton[chain[0]].Name)
            ? new[] { "Meta", "Prox", "Mid", "Dist" }
            : new[] { "Prox", "Mid", "Dist" };
        var count = Math.Min(segments.Length, chain.Count);
        for (var i = 0; i < count; i++)
            result.RoleToBone[Enum.Parse<BoneRole>(finger + segments[i] + side)] = chain[i];
    }

    private static bool IsMetacarpal(string name)
        => name.Contains("metacarp", StringComparison.OrdinalIgnoreCase);

    private static int FingerNumber(SkeletonModel skeleton, IEnumerable<int> chain)
    {
        foreach (var bone in chain)
        {
            var name = skeleton[bone].Name;
            var start = name.IndexOf("finger", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;
            start += "finger".Length;
            while (start < name.Length && !char.IsDigit(name[start]))
                start++;
            var end = start;
            while (end < name.Length && char.IsDigit(name[end]))
                end++;
            if (end > start && int.TryParse(name[start..end], out var number))
                return number;
        }
        return -1;
    }

    // ================================================================ topology helpers

    private static List<int>[] BuildChildren(SkeletonModel skeleton)
    {
        var children = new List<int>[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
            children[i] = new List<int>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            var parent = skeleton[i].ParentIndex;
            if (parent >= 0)
                children[parent].Add(i);
        }
        return children;
    }

    /// <summary>Max chain length downward including the node itself (leaves = 1).</summary>
    private static int[] BuildSubtreeDepth(SkeletonModel skeleton, List<int>[] children)
    {
        var depth = new int[skeleton.Count];
        for (var i = skeleton.Count - 1; i >= 0; i--) // children have larger indices
            depth[i] = 1 + (children[i].Count == 0 ? 0 : children[i].Max(c => depth[c]));
        return depth;
    }

    private static List<int> CollectSubtree(List<int>[] children, int root)
    {
        var nodes = new List<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            nodes.Add(node);
            foreach (var child in children[node])
                stack.Push(child);
        }
        return nodes;
    }

    /// <summary>Greedy deepest-descent chain from a node (the anatomical limb continues
    /// along the deepest subtree; short decorations branch off).</summary>
    private static List<int> PrimaryChain(List<int>[] children, int[] subtreeDepth, int start)
    {
        var chain = new List<int>();
        var current = start;
        while (true)
        {
            chain.Add(current);
            var kids = children[current];
            if (kids.Count == 0)
                break;
            current = kids.OrderByDescending(c => subtreeDepth[c]).First();
        }
        return chain;
    }

    private static float SubtreeMeanLateral(List<int>[] children, Vector3[] positions, int lateralAxis, int root)
    {
        var nodes = CollectSubtree(children, root);
        return nodes.Average(n => Component(positions[n], lateralAxis));
    }

    /// <summary>Lowest common ancestor of two bones, or null when they live in different
    /// trees (multi-root skeletons / disconnected armatures).</summary>
    private static int? LowestCommonAncestor(SkeletonModel skeleton, int[] depth, int a, int b)
    {
        while (a != b)
        {
            if (depth[a] >= depth[b])
                a = skeleton[a].ParentIndex;
            else
                b = skeleton[b].ParentIndex;
            if (a < 0 || b < 0)
                return null;
        }
        return a;
    }

    /// <summary>Path from (exclusive) <paramref name="ancestor"/> down to (inclusive)
    /// <paramref name="descendant"/>, top-first; empty when equal.</summary>
    private static List<int> PathBetween(SkeletonModel skeleton, int ancestor, int descendant)
    {
        var path = new List<int>();
        var current = descendant;
        while (current != ancestor && current >= 0)
        {
            path.Add(current);
            current = skeleton[current].ParentIndex;
        }
        path.Reverse();
        return path;
    }

    private static bool IsAncestor(SkeletonModel skeleton, int ancestor, int node)
    {
        var current = skeleton[node].ParentIndex;
        while (current >= 0)
        {
            if (current == ancestor)
                return true;
            current = skeleton[current].ParentIndex;
        }
        return false;
    }

    private static int Depth(SkeletonModel skeleton, int node)
    {
        var depth = 0;
        var current = skeleton[node].ParentIndex;
        while (current >= 0)
        {
            depth++;
            current = skeleton[current].ParentIndex;
        }
        return depth;
    }

    private static float Component(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    private static Vector3 Mirror(Vector3 v, int axis) => axis switch
    {
        0 => new Vector3(-v.X, v.Y, v.Z),
        1 => new Vector3(v.X, -v.Y, v.Z),
        _ => new Vector3(v.X, v.Y, -v.Z),
    };
}
