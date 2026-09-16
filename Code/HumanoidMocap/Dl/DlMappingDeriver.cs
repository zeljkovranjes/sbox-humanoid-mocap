#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Dl;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Derives a role ↔ source-bone mapping from a DL retarget result (design §6 "DL preview →
/// profile derivation"): when the user confirms a DL preview, the implied alignment is
/// recovered by trajectory correlation so the rig can be saved as a user preset and handled
/// by the deterministic geometric path on every later conversion.
/// </summary>
/// <remarks>
/// For every <see cref="BoneRole"/> the target rig maps (finger roles excluded — the DL
/// solver leaves fingers at rest, so their trajectories carry no signal), the DL-output
/// target bone's root-relative world trajectory (normalized by its own rig's hip height) is
/// correlated — mean per-frame magnitude-weighted cosine over up to 60 evenly sampled
/// frames — against every source bone trajectory (same normalization). Roles are assigned
/// greedily by descending correlation, one source bone per role, and only above
/// <see cref="CorrelationThreshold"/>; everything below stays unmapped (the saved preset is
/// then partial, which the geometric solver handles). Hips are assigned directly to the
/// source root the DL encode used (the root-relative trajectory of the root itself is
/// degenerate).
/// </remarks>
public static class DlMappingDeriver
{
    /// <summary>Minimum mean trajectory cosine for a role to be assigned.</summary>
    public const float CorrelationThreshold = 0.8f;

    private const int MaxSampleFrames = 60;

    /// <summary>
    /// Derives the mapping implied by a DL solve.
    /// </summary>
    /// <param name="scene">The source scene the DL solver consumed.</param>
    /// <param name="clipIndex">The clip that was solved.</param>
    /// <param name="dlOutput">The DL solver's output clip (target bone locals).</param>
    /// <param name="rig">The target rig the clip was decoded onto.</param>
    /// <returns>A <see cref="MappingSource.Manual"/>-style result named
    /// <c>dl_derived</c>; confidence = mean correlation of the assigned roles.</returns>
    public static MappingResult Derive(SourceScene scene, int clipIndex, Clip dlOutput, TargetRig rig)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(dlOutput);
        ArgumentNullException.ThrowIfNull(rig);
        if (clipIndex < 0 || clipIndex >= scene.Clips.Count)
            throw new ArgumentOutOfRangeException(nameof(clipIndex));

        var sourceClip = scene.Clips[clipIndex];
        var frames = Math.Min(sourceClip.FrameCount, dlOutput.FrameCount);
        var result = new MappingResult("dl_derived", MappingSource.Manual);
        result.Notes.Add("Derived from a confirmed deep-learning retarget by trajectory correlation.");
        if (frames < 2)
        {
            result.Notes.Add("Too few frames to correlate trajectories; mapping left empty.");
            return result;
        }

        var samples = SampleIndices(frames);
        var srcHips = SameFeatures.FindHips(scene.Skeleton, null);
        var tgtHips = rig.BoneForRole(BoneRole.Hips) ?? SameFeatures.FindHips(rig.Skeleton, null);

        // Root-relative, hip-height-normalized world trajectories on both sides, rotated
        // into the canonical character frame (X = left, Y = up, Z = forward) so lateral
        // offsets and heights are comparable across rigs.
        var srcAlign = SameFeatures.ComputeAlignment(scene.Skeleton, null, scene);
        var tgtAlign = TargetAlignment(rig);
        var srcTraj = Trajectories(scene.Skeleton, sourceClip, samples, srcHips, srcAlign);
        var tgtTraj = Trajectories(rig.Skeleton, dlOutput, samples, tgtHips, tgtAlign);

        // Candidate correlations for every mapped body role × source bone, with two
        // tie-breakers on the sort key (the threshold still applies to the raw score):
        // collinear chain bones (Head vs a HeadTop end marker) correlate near-identically,
        // so near-ties go to the PROXIMAL joint — the one that actually articulates — via
        // a small depth penalty; and center-line roles (Head, Neck, Spine…) penalize
        // laterally offset source bones (an eye matches the head's height but rides
        // off-center).
        var depth = new int[scene.Skeleton.Count];
        for (var s = 0; s < scene.Skeleton.Count; s++)
        {
            var parent = scene.Skeleton[s].ParentIndex;
            depth[s] = parent < 0 ? 0 : depth[parent] + 1;
        }

        var candidates = new List<(BoneRole Role, int Bone, float Cos, float Key)>();
        for (var b = 0; b < rig.Skeleton.Count; b++)
        {
            if (rig.RoleOf(b) is not { } role || role == BoneRole.Hips || IsFingerRole(role))
                continue;
            var center = IsCenterRole(role);
            for (var s = 0; s < scene.Skeleton.Count; s++)
            {
                var cos = MeanCosine(tgtTraj[b], srcTraj[s], samples.Length);
                if (cos < CorrelationThreshold)
                    continue;
                var key = cos - 0.01f * depth[s];
                if (center)
                    key -= MathF.Abs(MeanLateral(srcTraj[s], samples.Length));
                candidates.Add((role, s, cos, key));
            }
        }

        // Greedy unique assignment by descending (depth-tie-broken) correlation.
        candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
        var usedBones = new HashSet<int> { srcHips };
        var total = 0f;
        result.RoleToBone[BoneRole.Hips] = srcHips;
        foreach (var (role, bone, cos, _) in candidates)
        {
            if (result.RoleToBone.ContainsKey(role) || !usedBones.Add(bone))
                continue;
            result.RoleToBone[role] = bone;
            total += cos;
        }

        var assigned = result.RoleToBone.Count - 1; // hips assigned structurally
        result.Confidence = assigned > 0 ? total / assigned : 0f;
        result.Notes.Add($"{assigned} role(s) correlated above {CorrelationThreshold:0.00}; "
            + "finger roles and below-threshold roles left unmapped.");
        return result;
    }

    /// <summary>Per-bone root-relative trajectories at the sampled frames, rotated by
    /// <paramref name="align"/> into the canonical character frame and normalized by the
    /// skeleton's own hip height (aligned root rest height above the lowest rest joint).</summary>
    private static Vector3[][] Trajectories(
        SkeletonModel skeleton, Clip clip, int[] samples, int hips, Quaternion align)
    {
        var lowest = float.PositiveInfinity;
        foreach (var rest in skeleton.RestWorld)
            lowest = MathF.Min(lowest, Vector3.Transform(rest.Pos, align).Y);
        var hipHeight = MathF.Abs(Vector3.Transform(skeleton.RestWorld[hips].Pos, align).Y - lowest);
        if (hipHeight < 1e-3f)
            hipHeight = 1f;

        var trajectories = new Vector3[skeleton.Count][];
        for (var b = 0; b < skeleton.Count; b++)
            trajectories[b] = new Vector3[samples.Length];

        for (var si = 0; si < samples.Length; si++)
        {
            var world = new Pose(clip.Frames[samples[si]]).ToWorld(skeleton);
            var root = world[hips].Pos;
            for (var b = 0; b < skeleton.Count; b++)
                trajectories[b][si] = Vector3.Transform(world[b].Pos - root, align) / hipHeight;
        }
        return trajectories;
    }

    private static Quaternion TargetAlignment(TargetRig rig)
    {
        try
        {
            var frame = HumanoidMocap.Solve.CharacterFrame.Compute(
                rig.Skeleton, rig.ToMappingResult(), rig.Skeleton.RestWorld);
            return SameFeatures.AlignFromBasis(frame.Lateral, frame.Up, frame.Forward);
        }
        catch (ArgumentException)
        {
            return Quaternion.Identity;
        }
    }

    private static float MeanLateral(Vector3[] trajectory, int count)
    {
        var sum = 0f;
        for (var i = 0; i < count; i++)
            sum += trajectory[i].X;
        return sum / count;
    }

    /// <summary>Roles on the body center line (no L/R side).</summary>
    private static bool IsCenterRole(BoneRole role)
    {
        var name = role.ToString();
        return !name.EndsWith("L", StringComparison.Ordinal)
            && !name.EndsWith("R", StringComparison.Ordinal);
    }

    /// <summary>Magnitude-weighted trajectory cosine: per frame
    /// <c>dot(a, b) / max(|a|², |b|²)</c> = cosine × (shorter/longer length ratio). The
    /// pure direction cosine cannot separate collinear chain bones (knee, ankle and toe all
    /// point "down" from the hips); the length ratio does.</summary>
    private static float MeanCosine(Vector3[] a, Vector3[] b, int count)
    {
        var sum = 0f;
        for (var i = 0; i < count; i++)
        {
            var maxSq = MathF.Max(a[i].LengthSquared(), b[i].LengthSquared());
            if (maxSq < 1e-8f)
                return -1f; // degenerate (root-coincident) trajectories never match
            sum += Vector3.Dot(a[i], b[i]) / maxSq;
        }
        return sum / count;
    }

    private static int[] SampleIndices(int frames)
    {
        var count = Math.Min(frames, MaxSampleFrames);
        var indices = new int[count];
        for (var i = 0; i < count; i++)
            indices[i] = (int)((long)i * (frames - 1) / Math.Max(count - 1, 1));
        return indices;
    }

    private static bool IsFingerRole(BoneRole role)
    {
        var name = role.ToString();
        return name.StartsWith("Thumb", StringComparison.Ordinal)
            || name.StartsWith("Index", StringComparison.Ordinal)
            || name.StartsWith("Middle", StringComparison.Ordinal)
            || name.StartsWith("Ring", StringComparison.Ordinal)
            || name.StartsWith("Pinky", StringComparison.Ordinal);
    }
}
