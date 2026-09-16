#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Solve;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Canonical anatomical frames: one world-space rest basis per mapped <see cref="BoneRole"/>,
/// derived from rest <b>geometry</b> (joint head positions) of any rig plus its mapping.
/// Built with the same deterministic convention on source and target, so world-rotation deltas
/// conjugated through these frames transfer between rigs with different bone local axes
/// (the s&amp;box Citizen rig's local axes encode no anatomy — bone-Y points chest-forward).
/// </summary>
/// <remarks>
/// <para><b>Frame convention</b> — for each role the frame quaternion <c>F</c> rotates unit
/// axes onto: <c>X = P</c> (primary), <c>Z</c> = the secondary hint <c>S</c> orthonormalized
/// against <c>P</c>, <c>Y = cross(Z, X)</c> (right-handed; for fingers Y is the curl hinge).</para>
/// <para><b>Primary axis P</b> = normalize(chain-child head − bone head), where the chain
/// child is the next <i>mapped</i> role down the bone's anatomical chain
/// (Hips→Spine0..4→Neck→Head; Clavicle→UpperArm→LowerArm→Hand; UpperLeg→LowerLeg→Foot→Toe;
/// per-finger Meta→Prox→Mid→Dist). Tips: the Head inherits its previous chain segment
/// (neck→head — the skull-base axis, real anatomy; measured 0–27° forward of character up
/// across neutral-rest rigs), falling back to a virtual character-up extension only when
/// that segment is absent or degenerate; Hand points at the midpoint of its mapped finger
/// proximals (else along the forearm); Foot without a toe and Toe extend along character
/// forward; finger distals extend along their previous segment. Other bones with nothing
/// mapped below inherit the previous chain segment's direction. A single mapped finger
/// joint uses its direct child/end-site as its real segment direction when available.</para>
/// <para><b>Secondary axis S</b> by bone class: spine/neck/head/hips and legs use character
/// forward (knee hinge lateral); clavicle/arms/hands use <c>cross(P, characterUp)</c>
/// (elbow hinge ⊥ limb in the character's horizontal plane at T-pose), falling back to
/// character forward when P is vertical; feet/toes use character up; fingers use the hand's
/// dorsal palm normal (see <see cref="HandGeometry.Dorsal"/>) so a positive rotation about
/// frame Y curls fingertips toward the palm on both hands.</para>
/// <para>When used by the solver, build the frames on the <see cref="RestNormalizer"/>-
/// normalized rest via <see cref="Build(SkeletonModel, MappingResult, IReadOnlyList{XForm})"/>;
/// this class itself just measures whatever rest it is given.</para>
/// </remarks>
public sealed class CanonicalFrames
{
    private readonly Dictionary<BoneRole, Quaternion> _frames;
    private readonly HashSet<BoneRole> _virtualPrimary;

    /// <summary>Character forward (the direction the toes point at rest), unit length.</summary>
    public Vector3 CharacterForward { get; }

    /// <summary>Character up (hips toward shoulders at rest), unit length.</summary>
    public Vector3 CharacterUp { get; }

    /// <summary>Rest hip height above the lowest foot/toe point, along character up, cm.</summary>
    public float HipHeight { get; }

    private CanonicalFrames(
        Dictionary<BoneRole, Quaternion> frames, HashSet<BoneRole> virtualPrimary,
        Vector3 forward, Vector3 up, float hipHeight)
    {
        _frames = frames;
        _virtualPrimary = virtualPrimary;
        CharacterForward = forward;
        CharacterUp = up;
        HipHeight = hipHeight;
    }

    /// <summary>True when a canonical frame exists for <paramref name="role"/> (the role is
    /// mapped and its chain geometry is resolvable).</summary>
    public bool Has(BoneRole role) => _frames.ContainsKey(role);

    /// <summary>
    /// True when the role's primary axis is a <b>virtual</b> character-axis extension rather
    /// than real joint geometry (e.g. a Foot with no mapped Toe extends along character
    /// forward; mapped Toes extend along character forward by convention; a Head whose
    /// neck→head segment is degenerate extends along character up). Absolute direction
    /// matching against a virtual primary imposes an arbitrary direction, so the solver
    /// falls back to delta transfer when the source is virtual but the target is real
    /// (see <see cref="GeometricSolver"/> remarks).
    /// </summary>
    public bool HasVirtualPrimary(BoneRole role) => _virtualPrimary.Contains(role);

    /// <summary>The world-space canonical rest frame of <paramref name="role"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Has"/> is false for
    /// the role.</exception>
    public Quaternion WorldFrameOf(BoneRole role)
        => _frames.TryGetValue(role, out var frame)
            ? frame
            : throw new InvalidOperationException($"No canonical frame for role {role} (not mapped or unresolvable).");

    /// <summary>Builds frames from the skeleton's bind rest (<c>skeleton.RestWorld</c>).</summary>
    public static CanonicalFrames Build(SkeletonModel skeleton, MappingResult map)
        => Build(skeleton, map, (skeleton ?? throw new ArgumentNullException(nameof(skeleton))).RestWorld);

    /// <summary>
    /// Builds frames from explicit rest world transforms (e.g. a <see cref="RestPose"/>
    /// produced by <see cref="RestNormalizer"/>), indexed like <c>skeleton.Bones</c>.
    /// </summary>
    public static CanonicalFrames Build(
        SkeletonModel skeleton, MappingResult map, IReadOnlyList<XForm> worldRest, Vector3? worldUp = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(worldRest);
        if (worldRest.Count != skeleton.Count)
            throw new ArgumentException(
                $"worldRest has {worldRest.Count} entries for a {skeleton.Count}-bone skeleton.");

        var cf = CharacterFrame.Compute(skeleton, map, worldRest, worldUp);
        var frames = new Dictionary<BoneRole, Quaternion>();
        var virtualPrimary = new HashSet<BoneRole>();

        foreach (var (chain, kind, left) in Chains())
            BuildChainFrames(skeleton, chain, kind, left, map, worldRest, cf, frames, virtualPrimary);

        return new CanonicalFrames(frames, virtualPrimary, cf.Forward, cf.Up, cf.HipHeight);
    }

    // ---------------------------------------------------------------- chain construction

    private enum ChainKind
    {
        Body,
        Arm,
        Leg,
        Finger,
    }

    private static IEnumerable<(BoneRole[] Chain, ChainKind Kind, bool Left)> Chains()
    {
        yield return (new[]
        {
            BoneRole.Hips, BoneRole.Spine0, BoneRole.Spine1, BoneRole.Spine2, BoneRole.Spine3,
            BoneRole.Spine4, BoneRole.Neck, BoneRole.Head,
        }, ChainKind.Body, false);

        foreach (var left in new[] { true, false })
        {
            var s = left ? "L" : "R";
            yield return (new[]
            {
                Role("Clavicle", s), Role("UpperArm", s), Role("LowerArm", s), Role("Hand", s),
            }, ChainKind.Arm, left);
            yield return (new[]
            {
                Role("UpperLeg", s), Role("LowerLeg", s), Role("Foot", s), Role("Toe", s),
            }, ChainKind.Leg, left);

            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                yield return (new[]
                {
                    Role(finger + "Meta", s), Role(finger + "Prox", s),
                    Role(finger + "Mid", s), Role(finger + "Dist", s),
                }, ChainKind.Finger, left);
            }
        }
    }

    private static BoneRole Role(string baseName, string side) => Enum.Parse<BoneRole>(baseName + side);

    private static void BuildChainFrames(
        SkeletonModel skeleton, BoneRole[] chain, ChainKind kind, bool left, MappingResult map,
        IReadOnlyList<XForm> worldRest, CharacterFrame cf, Dictionary<BoneRole, Quaternion> frames,
        HashSet<BoneRole> virtualPrimary)
    {
        // Collapse to the mapped chain members; gaps are skipped so e.g. a missing Spine1
        // makes Spine0 point straight at Spine2.
        var mapped = new List<(BoneRole Role, Vector3 Pos)>(chain.Length);
        foreach (var role in chain)
        {
            if (map.RoleToBone.TryGetValue(role, out var index))
                mapped.Add((role, worldRest[index].Pos));
        }

        Vector3? dorsal = kind == ChainKind.Finger ? HandGeometry.Dorsal(map, worldRest, left) : null;

        for (var i = 0; i < mapped.Count; i++)
        {
            var (role, pos) = mapped[i];
            Vector3? prevDir = i > 0 ? pos - mapped[i - 1].Pos : null;

            var (primary, isVirtual) = i + 1 < mapped.Count
                ? ((Vector3?)(mapped[i + 1].Pos - pos), false)
                : TipPrimary(skeleton, kind, role, pos, prevDir, left, map, worldRest, cf);
            if (primary is null || primary.Value.LengthSquared() < 1e-8f)
                continue;

            var secondary = Secondary(kind, role, primary.Value, dorsal, cf);
            frames[role] = BasisFromPrimarySecondary(primary.Value, secondary, cf);
            if (isVirtual)
                virtualPrimary.Add(role);
        }
    }

    /// <summary>Primary direction for the last mapped bone of a chain. <c>Virtual</c> is true
    /// when the direction is a character-axis convention rather than this rig's real joint
    /// geometry (see <see cref="HasVirtualPrimary"/>).</summary>
    private static (Vector3? Dir, bool Virtual) TipPrimary(
        SkeletonModel skeleton, ChainKind kind, BoneRole role, Vector3 pos, Vector3? prevDir, bool left,
        MappingResult map, IReadOnlyList<XForm> worldRest, CharacterFrame cf)
    {
        switch (kind)
        {
            case ChainKind.Body:
                // Head: its primary is the REAL previous chain segment (neck→head — the
                // skull-base axis; the rest lean of that segment is head-joint-placement
                // anatomy the delta transfer modes reference, and the posed-rest gaze
                // fallback measures — see GeometricSolver remarks). Only a degenerate or
                // absent segment falls back to the virtual character-up extension (e.g. a
                // head stacked on the neck). A body chain that ends early keeps its
                // previous segment direction, defaulting to up.
                if (role == BoneRole.Head)
                    return prevDir is { } seg && seg.LengthSquared() >= 1e-8f ? (seg, false) : (cf.Up, true);
                return prevDir is not null ? (prevDir, false) : (cf.Up, true);

            case ChainKind.Arm:
                if (role is BoneRole.HandL or BoneRole.HandR)
                {
                    var knuckles = HandGeometry.FingerProximalMidpoint(map, worldRest, left);
                    if (knuckles is not null)
                    {
                        var direction = knuckles.Value - pos;
                        if (direction.LengthSquared() >= 1e-8f)
                            return (direction, false);
                    }
                }
                return (prevDir, false); // along the forearm / previous segment; null → no frame

            case ChainKind.Leg:
                // Foot without a mapped toe, and the toe itself, extend along character
                // forward (toes point forward by the character-frame convention).
                if (role is BoneRole.FootL or BoneRole.FootR or BoneRole.ToeL or BoneRole.ToeR)
                    return (cf.Forward, true);
                return (prevDir, false);

            case ChainKind.Finger:
                if (prevDir is not null)
                    return (prevDir, false); // distal tip extrapolates its previous segment
                // One-joint BVH fingers place the joint at the hand and keep their actual
                // direction in an unmapped End Site. Prefer that real segment geometry;
                // otherwise the coincident joint has no resolvable canonical frame and its
                // animation is silently discarded.
                if (map.RoleToBone.TryGetValue(role, out var fingerIndex))
                {
                    Vector3? longestChild = null;
                    for (var child = 0; child < skeleton.Count; child++)
                    {
                        if (skeleton[child].ParentIndex != fingerIndex)
                            continue;
                        var direction = worldRest[child].Pos - pos;
                        if (direction.LengthSquared() > (longestChild?.LengthSquared() ?? 1e-8f))
                            longestChild = direction;
                    }
                    if (longestChild is not null)
                        return (longestChild, false);
                }

                // Single mapped finger bone: point away from the hand when possible.
                var handRole = left ? BoneRole.HandL : BoneRole.HandR;
                if (map.RoleToBone.TryGetValue(handRole, out var handIndex))
                    return (pos - worldRest[handIndex].Pos, false);
                return (null, false);

            default:
                return (null, false);
        }
    }

    /// <summary>Secondary (Z) hint by bone class; see the class remarks for rationale.</summary>
    private static Vector3 Secondary(ChainKind kind, BoneRole role, Vector3 primary, Vector3? dorsal, CharacterFrame cf)
    {
        switch (kind)
        {
            case ChainKind.Body:
                return cf.Forward;

            case ChainKind.Arm:
            {
                var hinge = Vector3.Cross(Vector3.Normalize(primary), cf.Up);
                return hinge.LengthSquared() < 1e-6f ? cf.Forward : hinge;
            }

            case ChainKind.Leg:
                // Feet and toes lie near the character-forward direction, so they use up as
                // the secondary; thigh/calf use forward (knee hinge lateral).
                if (role is BoneRole.FootL or BoneRole.FootR or BoneRole.ToeL or BoneRole.ToeR)
                    return cf.Up;
                return cf.Forward;

            case ChainKind.Finger:
                if (dorsal is not null)
                    return dorsal.Value;
                var fallback = Vector3.Cross(Vector3.Normalize(primary), cf.Up);
                return fallback.LengthSquared() < 1e-6f ? cf.Forward : fallback;

            default:
                return cf.Forward;
        }
    }

    /// <summary>
    /// Orthonormal right-handed basis: <c>X = normalize(primary)</c>, <c>Z = secondary</c>
    /// Gram-Schmidt-orthonormalized against X (falling back to character forward, then up,
    /// then world axes when degenerate), <c>Y = cross(Z, X)</c>.
    /// </summary>
    private static Quaternion BasisFromPrimarySecondary(Vector3 primary, Vector3 secondary, CharacterFrame cf)
    {
        var x = Vector3.Normalize(primary);

        var z = Orthonormalized(secondary, x)
            ?? Orthonormalized(cf.Forward, x)
            ?? Orthonormalized(cf.Up, x)
            ?? Orthonormalized(Vector3.UnitZ, x)
            ?? Orthonormalized(Vector3.UnitX, x)!.Value;

        var y = Vector3.Cross(z, x);

        // System.Numerics matrices act on row vectors: the rows are the images of the unit
        // axes under the rotation (row1 = R*X, row2 = R*Y, row3 = R*Z).
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);

        return MathQ.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private static Vector3? Orthonormalized(Vector3 hint, Vector3 x)
    {
        var z = hint - x * Vector3.Dot(hint, x);
        return z.LengthSquared() < 1e-6f ? null : Vector3.Normalize(z);
    }
}
