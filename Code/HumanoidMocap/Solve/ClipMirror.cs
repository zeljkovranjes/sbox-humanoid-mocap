#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Solve;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Mirrors a solved TARGET-space clip across the target character's sagittal plane,
/// producing the left/right-swapped twin of an animation (e.g. a right-foot-lead walk from a
/// left-foot-lead one).
/// </summary>
/// <remarks>
/// <para><b>Mirror plane.</b> The plane through the rig-space origin spanned by the target
/// character's up and forward directions; its normal is the character's LATERAL axis,
/// computed from the target rig's rest geometry via <see cref="CharacterFrame"/> (never
/// hardcoded — an arbitrary target may be authored in any axis convention). When the
/// computed lateral lies on a coordinate axis up to float dirt (&lt; 1e-3 on the other two
/// components — true for every axis-aligned authored rig, including the s&amp;box citizen
/// rigs), it is snapped to that exact axis, which makes every reflection below an EXACT
/// sign-flip in IEEE arithmetic and therefore the whole mirror a bit-exact involution
/// (mirror ∘ mirror == identity, verified by test).</para>
/// <para><b>Math.</b> Let M = I − 2n̂n̂ᵀ be the reflection across the plane with unit normal
/// n̂. A world transform W = (R, t) maps to its mirror image by conjugation:
/// W′ = M̂ ∘ W ∘ M̂ (M̂ is its own inverse), giving rotation R′ = M·R·M and translation
/// t′ = M·t. For a quaternion q = (v, w), M·R·M is the rotation by the SAME angle about the
/// REFLECTED axis with REVERSED sense (a reflection flips orientation), i.e.
/// q′ = (2(n̂·v)n̂ − v, w); with n̂ = +X that is exactly q′ = (x, −y, −z, w), and positions
/// reflect as p′ = p − 2(n̂·p)n̂ = (−pₓ, p_y, p_z).</para>
/// <para><b>Locals, not worlds.</b> Because conjugation is a homomorphism
/// (M̂(AB)M̂ = (M̂AM̂)(M̂BM̂)) and world transforms are products of locals down the
/// hierarchy, mirroring every LOCAL transform and permuting bones by their L↔R partner is
/// exactly equivalent to mirroring the FK worlds — provided the partner permutation is
/// hierarchy-consistent (the partner's parent is the parent's partner), which is validated
/// and holds on structurally symmetric humanoid rigs. This avoids FK→inverse-FK float drift
/// entirely, which is what makes the double-mirror identity bit-exact.</para>
/// <para><b>Pairing.</b> Left/right bones are paired by the rig's canonical role annotations
/// first (UpperArmL ↔ UpperArmR, …); role-less bones (twist helpers, IK bones) fall back to
/// <c>_L</c>/<c>_R</c> name-token pairing (<c>arm_upper_L_twist0</c> ↔
/// <c>arm_upper_R_twist0</c>, <c>foot_L_IK_target</c> ↔ <c>foot_R_IK_target</c>); anything
/// unpaired (center bones: pelvis, spine, neck, head) mirrors in place, which reflects its
/// rotation across the sagittal plane and negates its lateral translation. IK-baked helper
/// bones are NOT re-baked after mirroring: conjugation is a homomorphism, so the mirrored
/// copies of the primary clip's final helper channels already hang in exactly the mirrored
/// relationship over the mirrored body (re-baking encoded a divergent convention, the Gate 3
/// review's mechanism 2 of the _M render defect; southpaw project, gate3_review.md 3.4).
/// Channel exclusions on mirrored clips go through <see cref="MirrorSafeExclusions"/>.</para>
/// </remarks>
public static class ClipMirror
{
    /// <summary>Maximum off-axis component magnitude below which the computed lateral axis is
    /// snapped to the exact coordinate axis (authored rigs are axis-aligned; the tiny rest
    /// asymmetries of a real mesh stay far below this).</summary>
    private const float AxisSnapTolerance = 1e-3f;

    /// <summary>
    /// Returns the mirrored copy of <paramref name="frames"/> (one new list, inputs
    /// untouched): per frame, bone i takes the conjugated local transform of its L↔R partner
    /// σ(i). See the class remarks for the math and pairing rules.
    /// </summary>
    /// <param name="frames">Solved per-frame local transforms (target skeleton bone order).</param>
    /// <param name="rig">The target rig (skeleton + roles) the frames belong to.</param>
    /// <exception cref="ArgumentException">Thrown when the rig maps a sided role without its
    /// counterpart, the pairing is not hierarchy-consistent, or the character frame is not
    /// computable — mirroring would silently produce garbage in those cases.</exception>
    public static List<XForm[]> Mirror(List<XForm[]> frames, TargetRig rig)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(rig);

        var skeleton = rig.Skeleton;
        var lateral = LateralAxis(rig);
        var pair = BuildPairing(rig);
        var fkFix = HierarchyInconsistentBones(rig, pair);

        var result = new List<XForm[]>(frames.Count);
        var baseWorld = fkFix.Count > 0 ? new XForm[skeleton.Count] : null;
        var mirrorWorld = fkFix.Count > 0 ? new XForm[skeleton.Count] : null;
        foreach (var locals in frames)
        {
            if (locals.Length != skeleton.Count)
                throw new ArgumentException(
                    $"Frame has {locals.Length} bones but the target skeleton has {skeleton.Count}.",
                    nameof(frames));

            var mirrored = new XForm[locals.Length];
            for (var i = 0; i < locals.Length; i++)
            {
                var source = locals[pair[i]];
                mirrored[i] = new XForm(
                    ReflectPoint(source.Pos, lateral),
                    ReflectRotation(source.Rot, lateral));
            }

            // Hierarchy-inconsistent pairs (the citizen parents arm_elbow_helper_R under
            // arm_lower_R_twist0 while _L hangs under arm_lower_L, the W3a-documented
            // rig quirk): the partner's conjugated LOCAL under a non-mirrored parent
            // chain misplaces the bone by the parent-chain difference (measured ~1 in on
            // the elbow/knee helpers). Solve their locals by FK so the mirrored WORLD is
            // the exact reflection of the partner's world (southpaw G8 mirror fix).
            if (fkFix.Count > 0)
            {
                for (var i = 0; i < skeleton.Count; i++)
                {
                    var parent = skeleton[i].ParentIndex;
                    baseWorld![i] = parent < 0
                        ? locals[i]
                        : XForm.Compose(baseWorld[parent], locals[i]);
                }
                for (var i = 0; i < skeleton.Count; i++)
                {
                    var parent = skeleton[i].ParentIndex;
                    if (fkFix.Contains(i))
                    {
                        var desired = new XForm(
                            ReflectPoint(baseWorld![pair[i]].Pos, lateral),
                            ReflectRotation(baseWorld[pair[i]].Rot, lateral));
                        mirrored[i] = parent < 0
                            ? desired
                            : XForm.ToLocal(mirrorWorld![parent], desired);
                    }
                    mirrorWorld![i] = parent < 0
                        ? mirrored[i]
                        : XForm.Compose(mirrorWorld[parent], mirrored[i]);
                }
            }
            result.Add(mirrored);
        }
        return result;
    }

    /// <summary>
    /// Bones whose L/R pairing is NOT hierarchy-consistent (the partner hangs under a
    /// non-mirrored parent). Only constraint-driven helpers can reach this state
    /// (<see cref="BuildPairing"/> fails hard for any other bone); their mirrored locals
    /// need the FK solve in <see cref="Mirror"/> and their DMX channels must be written
    /// (<see cref="MirrorSafeExclusions"/>).
    /// </summary>
    private static HashSet<int> HierarchyInconsistentBones(TargetRig rig, int[] pair)
    {
        var skeleton = rig.Skeleton;
        var result = new HashSet<int>();
        for (var i = 0; i < pair.Length; i++)
        {
            var parent = skeleton[i].ParentIndex;
            var partnerParent = skeleton[pair[i]].ParentIndex;
            var consistent = parent < 0
                ? partnerParent < 0
                : partnerParent == pair[parent];
            if (!consistent)
                result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// Filters a channel-exclusion set for a MIRRORED clip: returns the subset of
    /// <paramref name="excluded"/> that is still safe to leave channel-less after mirroring.
    /// A channel-less bone is rendered/baked at its own REST local under its (mirrored)
    /// parent; the mirrored frames instead carry the conjugated rest local of the bone's
    /// L/R partner. Those agree only when the rig's rest locals are mirror conjugates
    /// (restLocal(i) == conjugate(restLocal(partner(i)))). Bones breaking that symmetry
    /// (measured on the citizen rig: leg/arm *_twist1 chains and neck_clothing, 19 to 37 cm
    /// off) MUST keep explicit mirrored channels or every data consumer (model compiler
    /// sequence bake, render-side helper evaluation) places them wrong: the Gate 3 review's
    /// MECHANISM 1 of the _M render defect (southpaw project, gate3_review.md 3.4).
    /// Truly symmetric helpers stay excluded exactly as on primary clips.
    /// </summary>
    /// <param name="rig">The target rig the exclusion set belongs to.</param>
    /// <param name="excluded">The primary-clip exclusion set (constraint-driven bones).</param>
    /// <returns>The mirror-safe subset, or null when nothing remains excluded.</returns>
    public static IReadOnlySet<int>? MirrorSafeExclusions(TargetRig rig, IReadOnlySet<int>? excluded)
    {
        if (excluded is null || excluded.Count == 0)
            return excluded;

        var skeleton = rig.Skeleton;
        var lateral = LateralAxis(rig);
        var pair = BuildPairing(rig);
        var inconsistent = HierarchyInconsistentBones(rig, pair);

        const float posTolCm = 0.1f;
        const float rotTolDeg = 0.5f;
        var cosTol = MathF.Cos(rotTolDeg * MathF.PI / 360f); // half-angle for quat dot

        var safe = new HashSet<int>();
        foreach (var i in excluded)
        {
            // Hierarchy-inconsistent pairs always need explicit channels: their mirrored
            // locals are FK-solved (see Mirror) and no rest local can stand in for them.
            if (inconsistent.Contains(i))
                continue;

            var own = skeleton[i].RestLocal;
            var partnerRest = skeleton[pair[i]].RestLocal;
            var needed = new XForm(
                ReflectPoint(partnerRest.Pos, lateral),
                ReflectRotation(partnerRest.Rot, lateral));

            var posOk = (own.Pos - needed.Pos).Length() <= posTolCm;
            var dot = MathF.Abs(
                own.Rot.X * needed.Rot.X + own.Rot.Y * needed.Rot.Y
                + own.Rot.Z * needed.Rot.Z + own.Rot.W * needed.Rot.W);
            var rotOk = dot >= cosTol;
            if (posOk && rotOk)
                safe.Add(i);
        }
        return safe.Count > 0 ? safe : null;
    }

    // ================================================================ mirror plane

    /// <summary>The unit mirror normal: the target character's lateral axis from rest
    /// geometry, snapped to an exact coordinate axis when within tolerance (bit-exact
    /// reflections, see class remarks).</summary>
    private static Vector3 LateralAxis(TargetRig rig)
    {
        Vector3 lateral;
        try
        {
            lateral = CharacterFrame.Compute(
                rig.Skeleton, rig.ToMappingResult(), rig.Skeleton.RestWorld).Lateral;
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException(
                $"Cannot mirror: target character frame not computable ({e.Message}).", e);
        }

        var a = Vector3.Abs(lateral);
        if (a.Y <= AxisSnapTolerance && a.Z <= AxisSnapTolerance)
            return Vector3.UnitX;
        if (a.X <= AxisSnapTolerance && a.Z <= AxisSnapTolerance)
            return Vector3.UnitY;
        if (a.X <= AxisSnapTolerance && a.Y <= AxisSnapTolerance)
            return Vector3.UnitZ;
        return lateral; // general (non-axis-aligned) rig: exact involution is lost, math is not
    }

    /// <summary>p′ = p − 2(n̂·p)n̂. With a snapped axis this is an exact sign flip of one
    /// component (IEEE subtraction of representable values is exact).</summary>
    private static Vector3 ReflectPoint(Vector3 p, Vector3 n)
        => p - 2f * Vector3.Dot(p, n) * n;

    /// <summary>q′ = (2(n̂·v)n̂ − v, w): the conjugated rotation M·R·M — same angle, axis
    /// reflected, sense reversed. With n̂ = +X this is (x, −y, −z, w). Components are
    /// preserved exactly (no renormalization), keeping the double mirror bit-exact.</summary>
    private static Quaternion ReflectRotation(Quaternion q, Vector3 n)
    {
        var v = new Vector3(q.X, q.Y, q.Z);
        var reflected = 2f * Vector3.Dot(v, n) * n - v;
        return new Quaternion(reflected.X, reflected.Y, reflected.Z, q.W);
    }

    // ================================================================ L↔R pairing

    /// <summary>
    /// σ: bone → mirror partner (identity for center/unpaired bones). Roles pair first;
    /// role-less bones pair by <c>_L</c>/<c>_R</c> name tokens. Validated to be an involution
    /// consistent with the hierarchy (σ(parent(i)) == parent(σ(i))).
    /// </summary>
    private static int[] BuildPairing(TargetRig rig)
    {
        var skeleton = rig.Skeleton;
        var pair = new int[skeleton.Count];
        for (var i = 0; i < pair.Length; i++)
            pair[i] = i;

        for (var i = 0; i < skeleton.Count; i++)
        {
            if (rig.RoleOf(i) is { } role)
            {
                if (MirrorRole(role) is not { } mirroredRole)
                    continue; // center role: mirrors in place
                pair[i] = rig.BoneForRole(mirroredRole)
                    ?? throw new ArgumentException(
                        $"Cannot mirror: target rig maps role {role} ('{skeleton[i].Name}') "
                        + $"but not its counterpart {mirroredRole}.");
            }
            else
            {
                var partnerName = SwapSideTokens(skeleton[i].Name);
                if (partnerName is null)
                    continue; // no side token: center bone
                var partner = skeleton.IndexOf(partnerName);
                if (partner >= 0)
                    pair[i] = partner;
                // No partner bone: leave in place (e.g. an asymmetric prop bone) — its
                // rotation still mirrors across the sagittal plane.
            }
        }

        for (var i = 0; i < pair.Length; i++)
        {
            // Constraint-driven helper bones are excluded from the output DMX (the model's
            // AnimConstraintList re-drives them at runtime, see Retargeter.EmitClip
            // ChannelExcludedBones), so their mirrored channels are never written. The shipped
            // s&box citizen rig parents these asymmetrically (arm_elbow_helper_R hangs under
            // arm_lower_R_twist0 while arm_elbow_helper_L hangs under arm_lower_L), a benign
            // data quirk that must not fail the whole mirror. Skip the strict L/R
            // hierarchy-consistency requirement for them: their pairing does not affect any
            // written channel. (W3a fix, southpaw project.)
            if (rig.HelpersAreConstraintDriven && rig.ClassOf(i) == BoneClass.ConstraintDriven)
                continue;

            if (pair[pair[i]] != i)
                throw new ArgumentException(
                    $"Cannot mirror: bone pairing is not symmetric ('{skeleton[i].Name}' → "
                    + $"'{skeleton[pair[i]].Name}' → '{skeleton[pair[pair[i]]].Name}').");

            var parent = skeleton[i].ParentIndex;
            var partnerParent = skeleton[pair[i]].ParentIndex;
            var consistent = parent < 0
                ? partnerParent < 0
                : partnerParent == pair[parent];
            if (!consistent)
                throw new ArgumentException(
                    $"Cannot mirror: left/right pairing is not hierarchy-consistent — "
                    + $"'{skeleton[i].Name}' and partner '{skeleton[pair[i]].Name}' hang under "
                    + "non-mirrored parents.");
        }

        return pair;
    }

    /// <summary>UpperArmL → UpperArmR (and back); null for center roles. Every sided
    /// <see cref="BoneRole"/> ends in <c>L</c>/<c>R</c>; no center role does.</summary>
    private static BoneRole? MirrorRole(BoneRole role)
    {
        var name = role.ToString();
        var mirroredName = name[^1] switch
        {
            'L' => name[..^1] + "R",
            'R' => name[..^1] + "L",
            _ => null,
        };
        return mirroredName is not null && Enum.TryParse<BoneRole>(mirroredName, out var mirrored)
            ? mirrored
            : null;
    }

    /// <summary>Swaps <c>L</c>/<c>R</c> underscore-delimited name tokens
    /// (<c>foot_L_IK_target</c> → <c>foot_R_IK_target</c>); null when the name carries no
    /// side token.</summary>
    private static string? SwapSideTokens(string name)
    {
        var tokens = name.Split('_');
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = tokens[i] switch
            {
                "L" => "R",
                "R" => "L",
                "l" => "r",
                "r" => "l",
                _ => tokens[i],
            };
        }
        var result = string.Join('_', tokens);
        return string.Equals(result, name, StringComparison.Ordinal) ? null : result;
    }
}
