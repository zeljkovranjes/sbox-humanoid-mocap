#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Solve;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Finger retargeting. Picks one of three strategies per finger chain:
/// <list type="number">
/// <item><b>1:1 absolute copy</b> (via the <c>transferOneToOne</c> callback into
/// <see cref="GeometricSolver"/>'s body path) when the source and target chains are
/// <i>geometrically identical</i> — same mapped role set, same canonical frames, same
/// normalized rest rotations. This is the same-rig round-trip case and is lossless (exact
/// identity, twist included).</item>
/// <item><b>Direction/swing matching</b> when the phalanx counts match ordinally but the
/// rigs differ (the common cross-rig case, e.g. Mixamo Prox/Mid/Dist onto the s&amp;box finger
/// with its extra metacarpal — which keeps its rest local; a source metacarpal's rotation is
/// implicit in the proximal's absolute direction). Each target phalanx is swung — shortest
/// arc, rotation axis ⊥ the finger axis, hence <b>zero twist by construction</b> — so that its
/// segment direction matches the source phalanx's direction in character-frame coordinates
/// exactly. Curl and splay are both captured by the direction; the source's axial twist is
/// dropped (hinge-joint noise; copying it absolutely would read as roll through the
/// inter-phalanx canonical mismatch between rigs, measured up to ~12° on thumbs). A source
/// with one rigid segment instead transfers its hand-relative swing to the target's first
/// joint; its remaining phalanges follow rigidly, avoiding an ambiguous 180° absolute
/// reorientation or duplicated curl.</item>
/// <item><b>Proportional redistribution</b> when phalanx counts differ (e.g. a two-phalanx
/// source finger): per-phalanx local curls — swing-twist about the canonical hinge Y of
/// <c>λ_b = C_b⁻¹·(ΔR_prev⁻¹·ΔR_b)·C_b</c> — are summed over the source chain (metacarpal
/// included) and redistributed over the target phalanges proportional to rest segment
/// lengths; splay (metacarpal + proximal, swing-twist about canonical Z) goes 100% to the
/// target proximal; the X-twist residual is dropped.</item>
/// </list>
/// In every mode target world deltas rebuild hierarchically from the solved target hand:
/// <c>ΔR_i = ΔR_{i-1} · (C_i · λ_i · C_i⁻¹)</c>, then <c>W_i = ΔR_i · R_tgtNormRest,i</c>.
/// Instances are per-solve and not thread-safe.
/// </summary>
internal sealed class FingerSolver
{
    /// <summary>Two canonical frames / rest rotations within this angle count as identical
    /// (same-rig detection for the lossless 1:1 path); cross-rig differences are degrees.</summary>
    private const float SameRigToleranceRad = 1e-3f;

    private enum ChainMode
    {
        DirectionMatch,
        SingleSegment,
        Proportional,
    }

    private readonly struct SourcePhalanx
    {
        public required int Slot { get; init; }
        public required Quaternion C { get; init; }
        public required Quaternion CInv { get; init; }
        public required Quaternion RestRot { get; init; }
        public required bool TakesSplay { get; init; }
    }

    private readonly struct Recipient
    {
        public required int TgtBone { get; init; }
        public required Quaternion C { get; init; }
        public required Quaternion CInv { get; init; }
        public required Quaternion RestRot { get; init; }
        public required Quaternion AuthoredRestRot { get; init; }
        public required float Weight { get; init; }
        public required bool Splay { get; init; }
    }

    private sealed class Chain
    {
        public required ChainMode Mode { get; init; }
        public required int SrcHandSlot { get; init; }
        public required Quaternion SrcHandRestRot { get; init; }
        public required int TgtHandBone { get; init; }
        public required Quaternion TgtHandNormRestRotInv { get; init; }
        public required Quaternion TgtHandAuthoredRestRotInv { get; init; }
        public required SourcePhalanx[] Sources { get; init; }
        public required Recipient[] Recipients { get; init; }
    }

    private readonly List<Chain> _chains;
    private readonly Quaternion _chrSrcInv;
    private readonly Quaternion _chrTgt;

    private FingerSolver(List<Chain> chains, Quaternion chrSrcInv, Quaternion chrTgt)
    {
        _chains = chains;
        _chrSrcInv = chrSrcInv;
        _chrTgt = chrTgt;
    }

    // ---------------------------------------------------------------- role tables

    private static readonly BoneRole[][] ChainRoles = BuildChainRoles();
    private static readonly HashSet<BoneRole> FingerRoleSet = ChainRoles.SelectMany(c => c.Skip(1)).ToHashSet();

    private static BoneRole[][] BuildChainRoles()
    {
        var chains = new List<BoneRole[]>();
        foreach (var side in new[] { "L", "R" })
        {
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                // Element 0 is the hand the chain hangs off; 1.. are Meta/Prox/Mid/Dist.
                chains.Add(new[]
                {
                    Enum.Parse<BoneRole>("Hand" + side),
                    Enum.Parse<BoneRole>(finger + "Meta" + side),
                    Enum.Parse<BoneRole>(finger + "Prox" + side),
                    Enum.Parse<BoneRole>(finger + "Mid" + side),
                    Enum.Parse<BoneRole>(finger + "Dist" + side),
                });
            }
        }
        return chains.ToArray();
    }

    /// <summary>True for the 40 per-finger segment roles (Meta/Prox/Mid/Dist × finger × side).</summary>
    public static bool IsFingerRole(BoneRole role) => FingerRoleSet.Contains(role);

    // ---------------------------------------------------------------- build

    /// <summary>
    /// Builds the per-chain plans. Geometrically identical chains are reported through
    /// <paramref name="transferOneToOne"/> instead of being planned here. Returns null when
    /// every mapped chain took that path (or none is mapped).
    /// </summary>
    public static FingerSolver? Build(
        MappingResult sourceMap,
        CanonicalFrames srcCanon,
        IReadOnlyList<XForm> srcNormRest,
        Func<BoneRole, int?> tgtBoneForRole,
        CanonicalFrames tgtCanon,
        IReadOnlyList<XForm> tgtNormRest,
        IReadOnlyList<XForm> tgtAuthoredRest,
        Quaternion chrSrcInv,
        Quaternion chrTgt,
        Func<int, int> registerSlot,
        Action<BoneRole> transferOneToOne)
    {
        var chains = new List<Chain>();
        foreach (var chainRoles in ChainRoles)
        {
            var handRole = chainRoles[0];
            var metaRole = chainRoles[1];
            var proxRole = chainRoles[2];
            var segments = chainRoles.Skip(1).ToArray();

            var srcRoles = segments
                .Where(r => sourceMap.RoleToBone.ContainsKey(r) && srcCanon.Has(r))
                .ToArray();
            var tgtRoles = segments
                .Where(r => tgtBoneForRole(r) is not null && tgtCanon.Has(r))
                .ToArray();
            if (srcRoles.Length == 0 || tgtRoles.Length == 0)
                continue;

            if (srcRoles.SequenceEqual(tgtRoles) && ChainsCoincide(
                srcRoles, sourceMap, srcCanon, srcNormRest, tgtBoneForRole, tgtCanon, tgtNormRest))
            {
                foreach (var role in srcRoles)
                    transferOneToOne(role);
                continue;
            }

            var srcPhalanges = srcRoles.Where(r => r != metaRole).ToArray();
            var tgtPhalanges = tgtRoles.Where(r => r != metaRole).ToArray();
            var recipientRoles = tgtPhalanges.Length > 0 ? tgtPhalanges : tgtRoles;
            if (srcPhalanges.Length == 1)
            {
                // A one-joint finger describes one rigid ray from the hand. Drive the
                // target chain's first joint (its metacarpal when present), so the whole
                // digit follows that ray without separating/corkscrewing later phalanges.
                recipientRoles = tgtRoles.Take(1).ToArray();
            }
            var mode = srcPhalanges.Length == 1
                ? ChainMode.SingleSegment
                : srcPhalanges.Length == recipientRoles.Length && srcPhalanges.Length > 0
                    ? ChainMode.DirectionMatch
                    : ChainMode.Proportional;

            // Direction matching consumes only the non-meta phalanges (the metacarpal's
            // motion is implicit in the proximal's absolute direction); redistribution
            // decomposes every mapped source segment including the metacarpal.
            var sourceRolesUsed = mode is ChainMode.DirectionMatch or ChainMode.SingleSegment
                ? srcPhalanges
                : srcRoles;
            var sources = sourceRolesUsed.Select(r =>
            {
                var c = srcCanon.WorldFrameOf(r);
                return new SourcePhalanx
                {
                    Slot = registerSlot(sourceMap.RoleToBone[r]),
                    C = c,
                    CInv = Quaternion.Conjugate(c),
                    RestRot = srcNormRest[sourceMap.RoleToBone[r]].Rot,
                    TakesSplay = r == metaRole || r == proxRole,
                };
            }).ToArray();

            var weights = SegmentWeights(tgtRoles, recipientRoles, tgtBoneForRole, tgtNormRest);
            var recipients = recipientRoles.Select((r, i) =>
            {
                var bone = tgtBoneForRole(r)!.Value;
                var c = tgtCanon.WorldFrameOf(r);
                return new Recipient
                {
                    TgtBone = bone,
                    C = c,
                    CInv = Quaternion.Conjugate(c),
                    RestRot = tgtNormRest[bone].Rot,
                    AuthoredRestRot = tgtAuthoredRest[bone].Rot,
                    Weight = weights[i],
                    Splay = i == 0,
                };
            }).ToArray();

            var tgtHand = tgtBoneForRole(handRole);
            chains.Add(new Chain
            {
                Mode = mode,
                SrcHandSlot = sourceMap.RoleToBone.TryGetValue(handRole, out var srcHand)
                    ? registerSlot(srcHand)
                    : -1,
                SrcHandRestRot = sourceMap.RoleToBone.TryGetValue(handRole, out srcHand)
                    ? srcNormRest[srcHand].Rot
                    : Quaternion.Identity,
                TgtHandBone = tgtHand ?? -1,
                TgtHandNormRestRotInv = tgtHand is int h
                    ? Quaternion.Conjugate(tgtNormRest[h].Rot)
                    : Quaternion.Identity,
                TgtHandAuthoredRestRotInv = tgtHand is int authoredHand
                    ? Quaternion.Conjugate(tgtAuthoredRest[authoredHand].Rot)
                    : Quaternion.Identity,
                Sources = sources,
                Recipients = recipients,
            });
        }

        return chains.Count > 0 ? new FingerSolver(chains, chrSrcInv, chrTgt) : null;
    }

    /// <summary>Same-rig detection: every chain member's canonical frame and normalized rest
    /// rotation agree between source and target (within float noise). Only then is the 1:1
    /// absolute copy lossless.</summary>
    private static bool ChainsCoincide(
        BoneRole[] roles, MappingResult sourceMap, CanonicalFrames srcCanon,
        IReadOnlyList<XForm> srcNormRest, Func<BoneRole, int?> tgtBoneForRole,
        CanonicalFrames tgtCanon, IReadOnlyList<XForm> tgtNormRest)
    {
        foreach (var role in roles)
        {
            var srcBone = sourceMap.RoleToBone[role];
            var tgtBone = tgtBoneForRole(role)!.Value;
            if (MathQ.AngleBetween(srcCanon.WorldFrameOf(role), tgtCanon.WorldFrameOf(role)) > SameRigToleranceRad
                || MathQ.AngleBetween(srcNormRest[srcBone].Rot, tgtNormRest[tgtBone].Rot) > SameRigToleranceRad)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Normalized rest segment lengths of the recipient phalanges (the proportional
    /// curl weights). The distal segment, having no chain child, is estimated as 0.8× its
    /// preceding segment.</summary>
    private static float[] SegmentWeights(
        BoneRole[] tgtRoles, BoneRole[] recipientRoles,
        Func<BoneRole, int?> tgtBoneForRole, IReadOnlyList<XForm> tgtNormRest)
    {
        var positions = tgtRoles.Select(r => tgtNormRest[tgtBoneForRole(r)!.Value].Pos).ToArray();
        var weights = new float[recipientRoles.Length];
        for (var i = 0; i < recipientRoles.Length; i++)
        {
            var j = Array.IndexOf(tgtRoles, recipientRoles[i]);
            weights[i] = j + 1 < positions.Length
                ? (positions[j + 1] - positions[j]).Length()
                : j > 0 ? 0.8f * (positions[j] - positions[j - 1]).Length() : 1f;
        }

        var sum = weights.Sum();
        if (sum <= 1e-6f)
            return Enumerable.Repeat(1f / weights.Length, weights.Length).ToArray();
        for (var i = 0; i < weights.Length; i++)
            weights[i] /= sum;
        return weights;
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>
    /// Solves the planned chains for one frame. <paramref name="srcDeltas"/> holds the
    /// registered source world rotation deltas (from normalized rest); solved target world
    /// rotations are written into <paramref name="rot"/>/<paramref name="solved"/>. The target
    /// hands must already be solved (body pass runs first).
    /// </summary>
    public void Apply(Quaternion[] srcDeltas, bool[] solved, Quaternion[] rot)
    {
        foreach (var chain in _chains)
        {
            var acc = chain.TgtHandBone >= 0 && solved[chain.TgtHandBone]
                ? MathQ.Normalize(rot[chain.TgtHandBone] * chain.TgtHandNormRestRotInv)
                : Quaternion.Identity;

            if (chain.Mode == ChainMode.DirectionMatch)
                ApplyDirectionMatch(chain, srcDeltas, acc, solved, rot);
            else if (chain.Mode == ChainMode.SingleSegment)
                ApplySingleSegment(chain, srcDeltas, solved, rot);
            else
                ApplyProportional(chain, srcDeltas, acc, solved, rot);
        }
    }

    private static void ApplySingleSegment(
        Chain chain, Quaternion[] srcDeltas, bool[] solved, Quaternion[] rot)
    {
        // A finger world rotation is meaningful only relative to its solved hand. If either
        // rig lacks that frame, leave the target digit at its authored local rest instead of
        // applying a world-space rotation as a local one.
        if (chain.SrcHandSlot < 0 || chain.TgtHandBone < 0 || !solved[chain.TgtHandBone])
            return;

        var sp = chain.Sources[0];
        var rc = chain.Recipients[0];
        var parentDelta = srcDeltas[chain.SrcHandSlot];
        var parentWorld = MathQ.Normalize(parentDelta * chain.SrcHandRestRot);
        var childWorld = MathQ.Normalize(srcDeltas[sp.Slot] * sp.RestRot);
        var currentLocal = MathQ.Normalize(Quaternion.Conjugate(parentWorld) * childWorld);
        var restLocal = MathQ.Normalize(
            Quaternion.Conjugate(chain.SrcHandRestRot) * sp.RestRot);
        var localDelta = MathQ.Normalize(currentLocal * Quaternion.Conjugate(restLocal));
        var worldDelta = MathQ.Normalize(
            chain.SrcHandRestRot * localDelta * Quaternion.Conjugate(chain.SrcHandRestRot));
        var canonical = MathQ.Normalize(sp.CInv * worldDelta * sp.C);
        MathQ.SwingTwist(canonical, Vector3.UnitX, out var swing, out _);

        var acc = MathQ.Normalize(
            rot[chain.TgtHandBone] * chain.TgtHandAuthoredRestRotInv);
        acc = MathQ.Normalize(acc * (rc.C * swing * rc.CInv));
        rot[rc.TgtBone] = MathQ.Normalize(acc * rc.AuthoredRestRot);
        solved[rc.TgtBone] = true;
    }

    private void ApplyDirectionMatch(
        Chain chain, Quaternion[] srcDeltas, Quaternion acc, bool[] solved, Quaternion[] rot)
    {
        for (var i = 0; i < chain.Recipients.Length; i++)
        {
            var sp = chain.Sources[i];
            var rc = chain.Recipients[i];

            // Source phalanx direction in character coords; re-expressed in the target world,
            // then relative to the already-reconstructed parent delta, then in the phalanx's
            // canonical frame — where the rest direction is unit X.
            var srcAbs = MathQ.Normalize(_chrSrcInv * srcDeltas[sp.Slot] * sp.C);
            var dirChr = Vector3.Transform(Vector3.UnitX, srcAbs);
            var dirTgtWorld = Vector3.Transform(dirChr, _chrTgt);
            var dirLocal = Vector3.Transform(dirTgtWorld, Quaternion.Conjugate(acc));
            var dirCanon = Vector3.Transform(dirLocal, rc.CInv);

            // Shortest-arc swing X -> dir: rotation axis ⊥ X, so it carries zero finger-axis
            // twist by construction.
            var swing = MathQ.FromTo(Vector3.UnitX, dirCanon);

            acc = MathQ.Normalize(acc * (rc.C * swing * rc.CInv));
            rot[rc.TgtBone] = MathQ.Normalize(acc * rc.RestRot);
            solved[rc.TgtBone] = true;
        }
    }

    private static void ApplyProportional(
        Chain chain, Quaternion[] srcDeltas, Quaternion acc, bool[] solved, Quaternion[] rot)
    {
        // Decompose: total local curl over the chain, splay from metacarpal + proximal.
        var prev = chain.SrcHandSlot >= 0 ? srcDeltas[chain.SrcHandSlot] : Quaternion.Identity;
        float totalCurl = 0f, splay = 0f;
        foreach (var sp in chain.Sources)
        {
            var dr = srcDeltas[sp.Slot];
            var local = MathQ.Normalize(Quaternion.Conjugate(prev) * dr);
            var canon = MathQ.Normalize(sp.CInv * local * sp.C);

            MathQ.SwingTwist(canon, Vector3.UnitY, out var swing, out var curlQ);
            totalCurl += SignedAngle(curlQ, Vector3.UnitY);

            if (sp.TakesSplay)
            {
                MathQ.SwingTwist(swing, Vector3.UnitZ, out _, out var splayQ);
                splay += SignedAngle(splayQ, Vector3.UnitZ);
            }

            prev = dr;
        }

        foreach (var rc in chain.Recipients)
        {
            var mu = Quaternion.CreateFromAxisAngle(Vector3.UnitY, totalCurl * rc.Weight);
            if (rc.Splay)
                mu = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, splay) * mu;

            acc = MathQ.Normalize(acc * (rc.C * mu * rc.CInv));
            rot[rc.TgtBone] = MathQ.Normalize(acc * rc.RestRot);
            solved[rc.TgtBone] = true;
        }
    }

    /// <summary>Signed rotation angle of an axis-aligned twist quaternion about
    /// <paramref name="axis"/>, wrapped to (−π, π].</summary>
    private static float SignedAngle(Quaternion twist, Vector3 axis)
    {
        var s = twist.X * axis.X + twist.Y * axis.Y + twist.Z * axis.Z;
        var angle = 2f * MathF.Atan2(s, twist.W);
        if (angle > MathF.PI)
            angle -= 2f * MathF.PI;
        else if (angle < -MathF.PI)
            angle += 2f * MathF.PI;
        return angle;
    }
}
