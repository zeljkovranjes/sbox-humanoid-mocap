#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Tunables for the grounded-foot stance recalibration pass.</summary>
public sealed class FootGroundAlignOptions
{
    /// <summary>
    /// Dead zone (degrees): measured stance offsets at or below this are genuine planted
    /// articulation (heel-roll bias, natural lean — measured 2–4° on well-rested rigs and
    /// on citizen clips) and are left untouched, keeping the transfer byte-faithful there.
    /// Only offsets beyond it are clearly rest-pose artifacts (measured 12–25° on the
    /// repro rig) and get recalibrated.
    /// </summary>
    public float MinCorrectionDeg { get; set; } = 8f;

    /// <summary>
    /// Maximum mean sole deviation (degrees) a plant may show and still count as a STANCE
    /// for the offset measurement. Plants beyond this are not standing on the sole (crawls,
    /// kneels, prone contact — measured 60–90° there) and are excluded; genuine rest-pose
    /// stance artifacts measure well below it (largest seen: 27°).
    /// </summary>
    public float MaxStanceDeviationDeg { get; set; } = 35f;
}

/// <summary>Per-foot results of a <see cref="FootGroundAlign.Apply"/> run.</summary>
public sealed class FootGroundAlignFootReport
{
    /// <summary>Plants that contributed to the stance measurement.</summary>
    public int StancePlants { get; set; }

    /// <summary>Plants excluded as non-stance (mean sole deviation beyond
    /// <see cref="FootGroundAlignOptions.MaxStanceDeviationDeg"/>).</summary>
    public int SkippedPlants { get; set; }

    /// <summary>Measured planted sole offset from the ground plane, degrees (0 when no
    /// stance plants exist).</summary>
    public float MeasuredOffsetDeg { get; set; }

    /// <summary>Foot correction applied to every frame, degrees (0 = inside the dead zone,
    /// nothing changed).</summary>
    public float AppliedFootDeg { get; set; }

    /// <summary>Toe correction applied to every frame, degrees.</summary>
    public float AppliedToeDeg { get; set; }
}

/// <summary>Results of a <see cref="FootGroundAlign.Apply"/> run.</summary>
public sealed class FootGroundAlignReport
{
    /// <summary>Left-foot results.</summary>
    public required FootGroundAlignFootReport Left { get; init; }

    /// <summary>Right-foot results.</summary>
    public required FootGroundAlignFootReport Right { get; init; }
}

/// <summary>
/// Grounded-foot stance recalibration: measures how far the foot's SOLE sits from the ground
/// plane while planted, and — when that offset is clearly a rest-pose artifact — rotates it
/// out with one constant per foot, applied to every frame of the clip.
/// </summary>
/// <remarks>
/// <para><b>Why a cleanup pass.</b> The solver transfers feet as rest-relative deltas
/// (<see cref="Solve.RoleTransferMode.CharacterDeltaFromRest"/>), so the target keeps its own
/// ankle anatomy — correct whenever the source's rest pose is a flat-footed stance (the delta
/// is then "deviation from standing"). Some rigs ship a NON-stance rest (measured: an
/// Auto-Rig-Pro export whose rest foot sits 12–25° from its planted stance), and that constant
/// offset rides into every frame of the replay — planted feet hover toe-down/heel-up. What a
/// stance actually looks like is animation evidence (planted phases), which a per-frame
/// solver cannot see, so the recalibration lives here.</para>
/// <para><b>Measurement.</b> Per foot: over every planted frame, the sole normal = rest up
/// carried by the foot's world delta from the target bind rest (whose feet stand on the
/// ground by construction); plants whose own mean normal sits beyond
/// <see cref="FootGroundAlignOptions.MaxStanceDeviationDeg"/> are excluded (crawl/kneel/prone
/// contact is not a stance). The pooled mean normal's deviation from up is the stance
/// offset.</para>
/// <para><b>Correction.</b> Offsets inside <see cref="FootGroundAlignOptions.MinCorrectionDeg"/>
/// are genuine articulation — nothing is changed (well-rested rigs and same-rig round trips
/// stay byte-identical through this pass). Beyond it, the shortest-arc rotation taking the
/// pooled normal back to up (pitch+roll only — yaw/toe-out is pose and follows the source)
/// premultiplies the foot's world rotation on EVERY frame: a rest artifact is constant, so
/// the fix is too — within-plant heel-roll, swing styling and frame-to-frame continuity are
/// preserved exactly, and no blending is needed. The toe then receives its own residual
/// constant measured on top of the corrected foot (it neither double-rotates with the foot
/// fix nor inherits the source toe's own rest artifact). Corrections rotate bones about
/// their own joints: ankle positions are untouched, so the pass composes freely with the
/// <see cref="FootPlant"/> position pinning (which preserves foot world rotations).</para>
/// <para><b>Plant intervals come from the caller</b> (the pipeline detects them on the
/// SOURCE clip via <see cref="FootPlant.DetectPlantIntervals"/> — ground truth, immune to
/// the hip-height rescaling that can push target-side trajectories outside the cm-tuned
/// Kovar thresholds). So does the decision to run at all: the pipeline invokes this pass
/// only when the source's normalized rest is implausible as a flat stance (toe at/above
/// ankle level or asymmetric feet — see <c>Retargeter.GroundAlignFeet</c>); on plausible
/// stance rests the solver's rest-relative transfer is already faithful and planted-sole
/// deviations are genuine articulation (boxing stances, heel rolls) that must not be
/// flattened.</para>
/// </remarks>
public static class FootGroundAlign
{
    /// <summary>Measures planted stance offsets and recalibrates feet whose offset is a
    /// rest-pose artifact; returns what was measured and done.</summary>
    /// <param name="frames">Per-frame local transforms (skeleton bone order); modified in place.</param>
    /// <param name="skeleton">Bone hierarchy the frames are expressed against; its bind rest
    /// is the flat-stance reference.</param>
    /// <param name="left">Left leg chain bone indices.</param>
    /// <param name="right">Right leg chain bone indices.</param>
    /// <param name="up">World up direction of the clip's space.</param>
    /// <param name="leftPlants">Left-foot plant intervals (frame indices into
    /// <paramref name="frames"/>; out-of-range parts are clamped/ignored).</param>
    /// <param name="rightPlants">Right-foot plant intervals.</param>
    /// <param name="options">Tunables; defaults used when null.</param>
    public static FootGroundAlignReport Apply(
        List<XForm[]> frames,
        SkeletonModel skeleton,
        FootChain left,
        FootChain right,
        Vector3 up,
        IReadOnlyList<FrameRange> leftPlants,
        IReadOnlyList<FrameRange> rightPlants,
        FootGroundAlignOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(leftPlants);
        ArgumentNullException.ThrowIfNull(rightPlants);

        options ??= new FootGroundAlignOptions();
        var report = new FootGroundAlignReport
        {
            Left = new FootGroundAlignFootReport(),
            Right = new FootGroundAlignFootReport(),
        };
        if (frames.Count == 0 || up.LengthSquared() < 1e-12f)
            return report;
        up = Vector3.Normalize(up);

        RecalibrateFoot(frames, skeleton, left, up, leftPlants, options, report.Left);
        RecalibrateFoot(frames, skeleton, right, up, rightPlants, options, report.Right);
        return report;
    }

    private static void RecalibrateFoot(
        List<XForm[]> frames, SkeletonModel skeleton, FootChain chain, Vector3 up,
        IReadOnlyList<FrameRange> plants, FootGroundAlignOptions options,
        FootGroundAlignFootReport report)
    {
        int n = frames.Count;
        var foot = chain.Ankle;
        var restFootRotInv = Quaternion.Conjugate(skeleton.RestWorld[foot].Rot);
        var maxStanceCos = MathF.Cos(options.MaxStanceDeviationDeg * MathF.PI / 180f);

        // ---- measurement: pooled planted sole normal over the stance plants ----
        var pooled = Vector3.Zero;
        foreach (var plant in plants)
        {
            int start = Math.Max(plant.Start, 0);
            int end = Math.Min(plant.End, n - 1);
            if (start > end)
                continue;

            var plantSum = Vector3.Zero;
            for (int f = start; f <= end; f++)
            {
                var footRot = FkUtil.BoneWorld(frames[f], skeleton, foot).Rot;
                plantSum += Vector3.Transform(up, MathQ.Normalize(footRot * restFootRotInv));
            }
            if (plantSum.LengthSquared() < 1e-8f
                || Vector3.Dot(Vector3.Normalize(plantSum), up) < maxStanceCos)
            {
                report.SkippedPlants++; // not standing on the sole — crawl/kneel/toe contact
                continue;
            }
            report.StancePlants++;
            pooled += plantSum; // frame-count-weighted: longer stances dominate
        }
        if (pooled.LengthSquared() < 1e-8f)
            return;
        pooled = Vector3.Normalize(pooled);

        var offsetDeg = MathQ.AngleBetween(pooled, up) * (180f / MathF.PI);
        report.MeasuredOffsetDeg = offsetDeg;
        if (offsetDeg <= options.MinCorrectionDeg)
            return; // genuine planted articulation — leave the transfer byte-faithful

        // ---- correction: one constant per foot, every frame ----
        var footFix = MathQ.FromTo(pooled, up);
        report.AppliedFootDeg = offsetDeg;

        // Toe residual measured on top of the corrected foot, same dead zone.
        var toeFix = Quaternion.Identity;
        if (chain.Toe is { } toe && skeleton[toe].ParentIndex == foot)
        {
            var restToeRotInv = Quaternion.Conjugate(skeleton.RestWorld[toe].Rot);
            var toePooled = Vector3.Zero;
            foreach (var plant in plants)
            {
                int start = Math.Max(plant.Start, 0);
                int end = Math.Min(plant.End, n - 1);
                for (int f = start; f <= end && f >= 0; f++)
                {
                    var toeRot = FkUtil.BoneWorld(frames[f], skeleton, toe).Rot;
                    toePooled += Vector3.Transform(
                        up, MathQ.Normalize(footFix * toeRot * restToeRotInv));
                }
            }
            if (toePooled.LengthSquared() > 1e-8f)
            {
                toePooled = Vector3.Normalize(toePooled);
                var toeDeg = MathQ.AngleBetween(toePooled, up) * (180f / MathF.PI);
                if (toeDeg > options.MinCorrectionDeg && Vector3.Dot(toePooled, up) >= maxStanceCos)
                {
                    toeFix = MathQ.FromTo(toePooled, up);
                    report.AppliedToeDeg = toeDeg;
                }
            }
        }

        for (int f = 0; f < n; f++)
            CorrectFrame(frames[f], skeleton, chain, footFix, toeFix);
    }

    /// <summary>Premultiplies the foot's world rotation by the constant fix (the joint
    /// position is untouched — the rotation pivots the foot about its own head), then gives
    /// the toe its own residual on top of the corrected foot.</summary>
    private static void CorrectFrame(
        XForm[] locals, SkeletonModel skeleton, FootChain chain,
        Quaternion footFix, Quaternion toeFix)
    {
        var foot = chain.Ankle;
        var parent = skeleton[foot].ParentIndex;
        var parentRot = parent < 0
            ? Quaternion.Identity
            : FkUtil.BoneWorld(locals, skeleton, parent).Rot;

        var footWorld = MathQ.Normalize(parentRot * locals[foot].Rot);
        var newFootWorld = MathQ.Normalize(footFix * footWorld);
        locals[foot] = new XForm(
            locals[foot].Pos, MathQ.Normalize(Quaternion.Conjugate(parentRot) * newFootWorld));

        if (chain.Toe is { } toe && skeleton[toe].ParentIndex == foot)
        {
            // Desired toe world = toeFix ∘ footFix ∘ original world; re-derive its local
            // against the corrected foot so it does not double-rotate with the foot fix.
            var toeWorldOld = MathQ.Normalize(footWorld * locals[toe].Rot);
            var desired = MathQ.Normalize(toeFix * footFix * toeWorldOld);
            locals[toe] = new XForm(
                locals[toe].Pos, MathQ.Normalize(Quaternion.Conjugate(newFootWorld) * desired));
        }
    }
}
