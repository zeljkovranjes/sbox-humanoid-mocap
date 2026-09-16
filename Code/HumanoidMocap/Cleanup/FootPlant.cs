#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Tunables for the Kovar foot-plant cleanup pass.</summary>
public sealed class FootPlantOptions
{
    /// <summary>Ankle speed below which a frame can enter a plant (hysteresis exit at 1.5×).</summary>
    public float SpeedThresholdCmPerSec { get; set; } = 8f;

    /// <summary>Ankle height above ground below which a frame can enter a plant (exit at 1.5×).</summary>
    public float HeightThresholdCm { get; set; } = 4f;

    /// <summary>Minimum plant duration in frames; shorter candidate intervals are discarded.</summary>
    public int MinPlantFrames { get; set; } = 3;

    /// <summary>Frames before/after each plant over which corrections ease in/out.</summary>
    public int BlendFrames { get; set; } = 3;

    /// <summary>Maximum allowed per-segment length stretch (fraction; 0.02 = 2%).</summary>
    public float MaxStretch { get; set; } = 0.02f;
}

/// <summary>A leg chain identified by skeleton bone indices.</summary>
public sealed class FootChain
{
    /// <summary>Upper-leg joint bone index.</summary>
    public required int Hip { get; init; }

    /// <summary>Knee bone index.</summary>
    public required int Knee { get; init; }

    /// <summary>Ankle (end effector) bone index — the position that gets pinned.</summary>
    public required int Ankle { get; init; }

    /// <summary>Optional toe bone index (follows via FK; reserved for future toe pinning).</summary>
    public int? Toe { get; init; }
}

/// <summary>Inclusive frame range.</summary>
public readonly record struct FrameRange(int Start, int End)
{
    /// <summary>Number of frames in the range.</summary>
    public int Length => End - Start + 1;
}

/// <summary>Per-foot results of a <see cref="FootPlant.Apply"/> run.</summary>
public sealed class FootPlantFootReport
{
    /// <summary>Detected plant intervals (inclusive frame ranges).</summary>
    public List<FrameRange> Plants { get; } = new();

    /// <summary>Largest rotation correction applied to any chain joint, degrees.</summary>
    public float MaxCorrectionDeg { get; set; }

    /// <summary>Largest world-space ankle displacement applied, centimeters.</summary>
    public float MaxCorrectionCm { get; set; }

    /// <summary>
    /// Largest frame-to-frame ankle movement remaining inside any plant after the pass,
    /// centimeters. Should be ~0 (the foot is pinned to its anchor).
    /// </summary>
    public float ResidualSlideCm { get; set; }
}

/// <summary>Results of a <see cref="FootPlant.Apply"/> run.</summary>
public sealed class FootPlantReport
{
    /// <summary>Left-foot results.</summary>
    public required FootPlantFootReport Left { get; init; }

    /// <summary>Right-foot results.</summary>
    public required FootPlantFootReport Right { get; init; }

    /// <summary>Estimated ground level (height along the up axis), centimeters.</summary>
    public float GroundHeight { get; set; }
}

/// <summary>
/// Kovar-style foot-plant cleanup ("Footskate Cleanup", Kovar et al. 2002): detect frames
/// where a foot should be planted, anchor it there, and remove residual sliding with
/// analytic two-bone leg IK plus a small allowed limb stretch, blending corrections in/out
/// so no pops appear at plant boundaries.
/// </summary>
/// <remarks>
/// Pipeline per foot:
/// <list type="number">
/// <item><b>Ground estimate:</b> 5th percentile of per-frame min(left, right) ankle heights
/// projected on the up axis — robust to brief dips below ground and to airborne frames.</item>
/// <item><b>Plant detection:</b> a frame enters a plant when ankle speed (central difference)
/// is below <see cref="FootPlantOptions.SpeedThresholdCmPerSec"/> AND height above ground is
/// below <see cref="FootPlantOptions.HeightThresholdCm"/>; it stays planted until either
/// exceeds 1.5× its threshold (hysteresis). Intervals shorter than
/// <see cref="FootPlantOptions.MinPlantFrames"/> are discarded.</item>
/// <item><b>Anchor:</b> mean of the planted ankle positions (horizontal + vertical mean;
/// height is kept as-is rather than snapped to ground).</item>
/// <item><b>Correction:</b> per planted frame, two-bone IK pulls the ankle onto the anchor.
/// If the anchor is beyond reach, BOTH segment local translations (knee + ankle) are scaled
/// by up to (1 + <see cref="FootPlantOptions.MaxStretch"/>) for that frame only — a per-frame
/// bone "length change" à la Kovar. This is representable in the output because DMX carries
/// per-frame bone positions, not just rotations.</item>
/// <item><b>Blending:</b> over <see cref="FootPlantOptions.BlendFrames"/> frames before/after
/// each plant the correction eases in/out: the world-space rotation deltas are slerped toward
/// identity and the stretch factor lerped toward 1.</item>
/// </list>
/// The ankle's world ROTATION is preserved exactly (foot orientation is never disturbed);
/// only its position is corrected. Toes and other descendants follow via FK.
/// </remarks>
public static class FootPlant
{
    /// <summary>Detects and removes foot sliding in place; returns a report of what was done.</summary>
    /// <param name="frames">Per-frame local transforms (skeleton bone order); modified in place.</param>
    /// <param name="skeleton">Bone hierarchy the frames are expressed against.</param>
    /// <param name="left">Left leg chain bone indices.</param>
    /// <param name="right">Right leg chain bone indices.</param>
    /// <param name="up">World up direction of the clip's space.</param>
    /// <param name="fps">Clip sample rate (used to convert per-frame deltas to cm/s).</param>
    /// <param name="options">Tunables; defaults used when null.</param>
    public static FootPlantReport Apply(
        List<XForm[]> frames,
        SkeletonModel skeleton,
        FootChain left,
        FootChain right,
        Vector3 up,
        float fps,
        FootPlantOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        options ??= new FootPlantOptions();
        var report = new FootPlantReport
        {
            Left = new FootPlantFootReport(),
            Right = new FootPlantFootReport(),
        };

        int n = frames.Count;
        if (n == 0 || up.LengthSquared() < 1e-12f || fps <= 0f)
            return report;
        up = Vector3.Normalize(up);

        // (a) Ankle world trajectories + robust ground estimate.
        var ankleL = AnkleWorldPositions(frames, skeleton, left.Ankle);
        var ankleR = AnkleWorldPositions(frames, skeleton, right.Ankle);
        report.GroundHeight = EstimateGround(ankleL, ankleR, up);

        // One FK scratch buffer shared by every per-frame correction (perf: the pass used
        // to run several allocating full-skeleton FK passes per corrected frame).
        var fkScratch = new XForm[skeleton.Count];
        ProcessFoot(frames, skeleton, left, ankleL, up, fps, report.GroundHeight, options, report.Left, fkScratch);
        ProcessFoot(frames, skeleton, right, ankleR, up, fps, report.GroundHeight, options, report.Right, fkScratch);
        return report;
    }

    /// <summary>
    /// Detection-only entry point — steps (a)+(b) of <see cref="Apply"/> (robust ground
    /// estimate, then per-foot hysteresis detection) without modifying anything. Used by
    /// callers that need plant intervals from a DIFFERENT clip than the one they correct:
    /// the grounded-foot stance alignment (<see cref="FootGroundAlign"/>) detects plants on
    /// the SOURCE clip (ground truth; the target-side trajectories may sit outside the
    /// cm-tuned thresholds after hip-height scaling) and applies them to the 1:1 solved
    /// target frames.
    /// </summary>
    public static (List<FrameRange> Left, List<FrameRange> Right) DetectPlantIntervals(
        List<XForm[]> frames,
        SkeletonModel skeleton,
        FootChain left,
        FootChain right,
        Vector3 up,
        float fps,
        FootPlantOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        options ??= new FootPlantOptions();
        if (frames.Count == 0 || up.LengthSquared() < 1e-12f || fps <= 0f)
            return (new List<FrameRange>(), new List<FrameRange>());
        up = Vector3.Normalize(up);

        var ankleL = AnkleWorldPositions(frames, skeleton, left.Ankle);
        var ankleR = AnkleWorldPositions(frames, skeleton, right.Ankle);
        var ground = EstimateGround(ankleL, ankleR, up);
        return (DetectPlants(ankleL, up, ground, fps, options),
                DetectPlants(ankleR, up, ground, fps, options));
    }

    private static void ProcessFoot(
        List<XForm[]> frames, SkeletonModel skeleton, FootChain chain, Vector3[] ankle,
        Vector3 up, float fps, float ground, FootPlantOptions options, FootPlantFootReport report,
        XForm[] fkScratch)
    {
        // (b) Detection with hysteresis + minimum duration.
        var plants = DetectPlants(ankle, up, ground, fps, options);
        report.Plants.AddRange(plants);
        if (plants.Count == 0)
            return;

        var bendAxis = RestBendAxis(skeleton, chain, up);
        int n = frames.Count;

        // Frames owned by some plant (full-weight corrections); blend ramps of one plant
        // must never dilute another plant's pinned frames.
        var planted = new bool[n];
        foreach (var plant in plants)
            for (int f = plant.Start; f <= plant.End; f++)
                planted[f] = true;

        foreach (var plant in plants)
        {
            // (c) Anchor = mean planted ankle position (height kept as-is).
            var anchor = Vector3.Zero;
            for (int f = plant.Start; f <= plant.End; f++)
                anchor += ankle[f];
            anchor /= plant.Length;

            // (d)+(e) Corrections over the plant plus blend windows on both sides.
            for (int f = plant.Start - options.BlendFrames; f <= plant.End + options.BlendFrames; f++)
            {
                if (f < 0 || f >= n)
                    continue;
                float w = CorrectionWeight(f, plant, options.BlendFrames);
                if (w <= 0f)
                    continue;
                bool isBlendFrame = f < plant.Start || f > plant.End;
                if (isBlendFrame && planted[f])
                    continue; // frame belongs to a neighboring plant — leave it pinned there
                CorrectFrame(frames[f], skeleton, chain, anchor, w, bendAxis, options, report, fkScratch);
            }
        }

        // (f) Residual slide inside each plant after the pass (must be ~0).
        foreach (var plant in plants)
        {
            var prev = AnkleWorld(frames[plant.Start], skeleton, chain.Ankle);
            for (int f = plant.Start + 1; f <= plant.End; f++)
            {
                var cur = AnkleWorld(frames[f], skeleton, chain.Ankle);
                report.ResidualSlideCm = MathF.Max(report.ResidualSlideCm, Vector3.Distance(cur, prev));
                prev = cur;
            }
        }
    }

    /// <summary>Applies one frame's weighted IK-to-anchor correction (stretch + rotations).</summary>
    private static void CorrectFrame(
        XForm[] locals, SkeletonModel skeleton, FootChain chain, Vector3 anchor, float w,
        Vector3 bendAxis, FootPlantOptions options, FootPlantFootReport report, XForm[] fkScratch)
    {
        FkUtil.ToWorld(locals, skeleton, fkScratch);
        var world = fkScratch;
        var a = world[chain.Hip].Pos;
        var b = world[chain.Knee].Pos;
        var c = world[chain.Ankle].Pos;
        var cOriginal = c;

        // (d) Allowed stretch: if the anchor is beyond full extension, scale BOTH segment
        // local translations for this frame only, by up to (1 + MaxStretch), lerped toward 1
        // by the blend weight. DMX carries per-frame positions, so this is representable.
        float l1 = Vector3.Distance(a, b);
        float l2 = Vector3.Distance(b, c);
        float reach = l1 + l2;
        float dist = Vector3.Distance(anchor, a);
        if (reach > 1e-6f && dist > reach)
        {
            float stretch = MathF.Min(dist / reach, 1f + options.MaxStretch);
            float s = 1f + (stretch - 1f) * w;
            locals[chain.Knee] = new XForm(locals[chain.Knee].Pos * s, locals[chain.Knee].Rot);
            locals[chain.Ankle] = new XForm(locals[chain.Ankle].Pos * s, locals[chain.Ankle].Rot);
            var b1 = a + (b - a) * s;
            c = b1 + (c - b) * s;
            b = b1;
        }

        // Two-bone IK toward the anchor; no reach softening — plants want the exact point.
        var result = TwoBoneIk.Solve(a, b, c, anchor, soften: 0f, stableBendAxis: bendAxis);

        // (e) Ease the rotation corrections in/out: slerp the world deltas toward identity.
        var dUpper = Quaternion.Slerp(Quaternion.Identity, result.UpperWorldDelta, w);
        var dLower = Quaternion.Slerp(Quaternion.Identity, result.LowerWorldDelta, w);

        EffectorIk.ApplyWorldDeltas(
            locals, skeleton, chain.Hip, chain.Knee, chain.Ankle, dUpper, dLower, fkScratch);

        // Report bookkeeping: largest joint rotation and ankle displacement applied.
        float deg = MathF.Max(MathQ.AngleBetween(Quaternion.Identity, dUpper),
                              MathQ.AngleBetween(Quaternion.Identity, dLower)) * (180f / MathF.PI);
        report.MaxCorrectionDeg = MathF.Max(report.MaxCorrectionDeg, deg);
        var cFinal = AnkleWorld(locals, skeleton, chain.Ankle);
        report.MaxCorrectionCm = MathF.Max(report.MaxCorrectionCm, Vector3.Distance(cFinal, cOriginal));
    }

    /// <summary>
    /// Plant detection: enter when speed &lt; threshold AND height-above-ground &lt; threshold;
    /// stay until either exceeds 1.5× its threshold (hysteresis); drop intervals shorter than
    /// <see cref="FootPlantOptions.MinPlantFrames"/>.
    /// </summary>
    private static List<FrameRange> DetectPlants(
        Vector3[] ankle, Vector3 up, float ground, float fps, FootPlantOptions options)
    {
        int n = ankle.Length;
        var plants = new List<FrameRange>();
        if (n < options.MinPlantFrames)
            return plants;

        var speed = new float[n];
        var height = new float[n];
        for (int i = 0; i < n; i++)
        {
            int i0 = Math.Max(i - 1, 0);
            int i1 = Math.Min(i + 1, n - 1);
            speed[i] = i1 > i0
                ? Vector3.Distance(ankle[i1], ankle[i0]) * fps / (i1 - i0)
                : 0f;
            height[i] = Vector3.Dot(ankle[i], up) - ground;
        }

        bool inPlant = false;
        int start = 0;
        for (int i = 0; i < n; i++)
        {
            bool enter = speed[i] < options.SpeedThresholdCmPerSec
                && height[i] < options.HeightThresholdCm;
            bool stay = speed[i] < options.SpeedThresholdCmPerSec * 1.5f
                && height[i] < options.HeightThresholdCm * 1.5f;

            if (!inPlant && enter)
            {
                inPlant = true;
                start = i;
            }
            else if (inPlant && !stay)
            {
                plants.Add(new FrameRange(start, i - 1));
                inPlant = false;
            }
        }
        if (inPlant)
            plants.Add(new FrameRange(start, n - 1));

        plants.RemoveAll(p => p.Length < options.MinPlantFrames);
        return plants;
    }

    /// <summary>Eased correction weight: 1 inside the plant, smoothstep ramp over the blend windows.</summary>
    private static float CorrectionWeight(int frame, FrameRange plant, int blendFrames)
    {
        float t;
        if (frame >= plant.Start && frame <= plant.End)
            return 1f;
        if (frame < plant.Start)
            t = 1f - (plant.Start - frame) / (float)(blendFrames + 1);
        else
            t = 1f - (frame - plant.End) / (float)(blendFrames + 1);
        if (t <= 0f)
            return 0f;
        return t * t * (3f - 2f * t); // smoothstep ease
    }

    /// <summary>Ground = 5th percentile of per-frame min(left, right) ankle heights along up.</summary>
    private static float EstimateGround(Vector3[] ankleL, Vector3[] ankleR, Vector3 up)
    {
        int n = ankleL.Length;
        var minHeights = new float[n];
        for (int i = 0; i < n; i++)
            minHeights[i] = MathF.Min(Vector3.Dot(ankleL[i], up), Vector3.Dot(ankleR[i], up));
        Array.Sort(minHeights);
        return minHeights[(int)MathF.Floor(0.05f * (n - 1))];
    }

    /// <summary>
    /// Knee hinge axis fallback for near-collinear chains, derived from the rest pose's bend
    /// plane; falls back to a horizontal axis perpendicular to the leg when the rest chain is
    /// itself collinear (straight-leg rigs).
    /// </summary>
    private static Vector3 RestBendAxis(SkeletonModel skeleton, FootChain chain, Vector3 up)
    {
        var a = skeleton.RestWorld[chain.Hip].Pos;
        var b = skeleton.RestWorld[chain.Knee].Pos;
        var c = skeleton.RestWorld[chain.Ankle].Pos;

        var axis = Vector3.Cross(c - a, b - a);
        if (axis.LengthSquared() > 1e-6f)
            return Vector3.Normalize(axis);

        var leg = c - a;
        axis = Vector3.Cross(up, leg);
        if (axis.LengthSquared() > 1e-6f)
            return Vector3.Normalize(axis);

        // Straight vertical leg: any horizontal axis perpendicular to the leg works.
        var fallback = Vector3.Cross(leg, Vector3.UnitX);
        if (fallback.LengthSquared() < 1e-6f)
            fallback = Vector3.Cross(leg, Vector3.UnitZ);
        return fallback.LengthSquared() > 1e-12f ? Vector3.Normalize(fallback) : Vector3.UnitX;
    }

    private static Vector3[] AnkleWorldPositions(List<XForm[]> frames, SkeletonModel skeleton, int ankle)
    {
        var positions = new Vector3[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            positions[i] = AnkleWorld(frames[i], skeleton, ankle);
        return positions;
    }

    /// <summary>Single ankle world position via ancestor-chain FK (bit-identical to a full
    /// FK pass, allocation-free).</summary>
    private static Vector3 AnkleWorld(XForm[] locals, SkeletonModel skeleton, int ankle)
        => FkUtil.BoneWorld(locals, skeleton, ankle).Pos;
}
