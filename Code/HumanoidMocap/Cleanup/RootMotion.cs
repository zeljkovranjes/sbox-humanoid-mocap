#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>How root motion should be handled in the output clip.</summary>
public enum RootMotionMode
{
    /// <summary>Leave hips/root channels exactly as solved.</summary>
    Off,

    /// <summary>
    /// Move the ground-projected, smoothed hips trajectory onto the dedicated root bone;
    /// hips keep height and full rotation locally (Unity/UE convention).
    /// </summary>
    Extract,

    /// <summary>Remove horizontal hips travel entirely (in-place clip); root stays put.</summary>
    InPlace,
}

/// <summary>Axis/index context for root-motion processing, supplied by the solver.</summary>
public sealed class RootMotionAxes
{
    /// <summary>World up direction of the clip's space.</summary>
    public required Vector3 Up { get; init; }

    /// <summary>Frame-array index of the dedicated root bone.</summary>
    public required int RootIndex { get; init; }

    /// <summary>Frame-array index of the hips bone.</summary>
    public required int HipsIndex { get; init; }

    /// <summary>
    /// True when the hips bone's local transform is parented to the root bone (so moving the
    /// root must subtract from hips locals to preserve world positions). False when both are
    /// scene-root level.
    /// </summary>
    /// <remarks>
    /// Only consulted by the legacy (skeleton-less) <see cref="RootMotion.Apply(List{XForm[]},
    /// RootMotionAxes, RootMotionMode)"/> overload. The skeleton-aware overload derives the
    /// actual parentage from the skeleton and ignores this flag.
    /// </remarks>
    public required bool HipsParentIsRoot { get; init; }

    /// <summary>Half-window (in frames) of the moving-average smoothing for the root path.</summary>
    public int SmoothHalfWindow { get; init; } = 2;
}

/// <summary>
/// Root-motion post-pass over solved frames (lists of per-bone local transforms).
/// Convention (production standard): root motion = ground-plane projection of the hips
/// trajectory, low-pass filtered; vertical motion always stays on the hips.
/// </summary>
public static class RootMotion
{
    /// <summary>
    /// Legacy overload, kept verbatim for the facade (Retargeter) until wave 2 rewires it.
    /// OBSOLETE-BY-CONVENTION: prefer <see cref="Apply(List{XForm[]}, SkeletonModel,
    /// RootMotionAxes, RootMotionMode)"/> — this overload trusts
    /// <see cref="RootMotionAxes.HipsParentIsRoot"/>, assumes the root bone sits at
    /// scene-top with identity rest rotation, and reads the hips LOCAL as its world.
    /// Those assumptions hold for the s&amp;box rig path (no dedicated root; InPlace on a
    /// parentless hips whose local == world) but break for root→intermediate→hips chains
    /// and for roots with non-identity rest rotations.
    /// </summary>
    public static void Apply(List<XForm[]> frames, RootMotionAxes axes, RootMotionMode mode)
    {
        if (mode == RootMotionMode.Off || frames.Count == 0)
            return;

        var up = Vector3.Normalize(axes.Up);
        int n = frames.Count;

        // Hips world positions (hips local == world unless parented to the moving root,
        // which is identity before this pass by construction).
        var hipsWorld = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var hips = frames[i][axes.HipsIndex].Pos;
            if (axes.HipsParentIsRoot)
                hips += frames[i][axes.RootIndex].Pos;
            hipsWorld[i] = hips;
        }

        // Horizontal (ground-plane) component of the hips trajectory.
        var horizontal = new Vector3[n];
        for (int i = 0; i < n; i++)
            horizontal[i] = hipsWorld[i] - Vector3.Dot(hipsWorld[i], up) * up;

        switch (mode)
        {
            case RootMotionMode.Extract:
            {
                var smoothed = Smooth(horizontal, axes.SmoothHalfWindow);
                // Root starts where frame 0's smoothed path starts, relative to itself:
                // anchor at the first sample so clips begin at the origin.
                var origin = smoothed[0];
                for (int i = 0; i < n; i++)
                {
                    var rootPos = smoothed[i] - origin;
                    var f = frames[i];
                    f[axes.RootIndex] = new XForm(rootPos, f[axes.RootIndex].Rot);

                    // Hips keep the residual (wobble + height), so root ∘ hips ≈ original world.
                    f[axes.HipsIndex] = new XForm(hipsWorld[i] - rootPos, f[axes.HipsIndex].Rot);
                }
                break;
            }

            case RootMotionMode.InPlace:
            {
                var origin = horizontal[0];
                for (int i = 0; i < n; i++)
                {
                    var f = frames[i];
                    var travel = horizontal[i] - origin;
                    var newHips = hipsWorld[i] - travel;
                    if (axes.HipsParentIsRoot)
                        newHips -= f[axes.RootIndex].Pos;
                    f[axes.HipsIndex] = new XForm(newHips, f[axes.HipsIndex].Rot);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Skeleton-aware root-motion pass. The hips WORLD trajectory is computed by real FK
    /// over its ancestor chain (correct whether the hips is parentless, a direct child of
    /// the root, or sits under intermediate bones), and locals are re-derived against each
    /// bone's actual parent world transform — including the parent's rotation, so a root
    /// with a non-identity rest rotation keeps the hips' vertical bob vertical instead of
    /// leaking it into the ground plane.
    /// </summary>
    /// <param name="frames">Per-frame local transforms (skeleton bone order); modified in place.</param>
    /// <param name="skeleton">Bone hierarchy the frames are expressed against.</param>
    /// <param name="axes">Axis/index context. <see cref="RootMotionAxes.HipsParentIsRoot"/>
    /// is ignored; parentage is derived from <paramref name="skeleton"/>.</param>
    /// <param name="mode">Root-motion mode.</param>
    public static void Apply(
        List<XForm[]> frames, SkeletonModel skeleton, RootMotionAxes axes, RootMotionMode mode)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(axes);
        if (mode == RootMotionMode.Off || frames.Count == 0)
            return;

        var up = Vector3.Normalize(axes.Up);
        int n = frames.Count;
        int hips = axes.HipsIndex;
        int root = axes.RootIndex;

        // Hips world per frame via real FK over the ancestor chain.
        var hipsWorld = new XForm[n];
        for (int i = 0; i < n; i++)
            hipsWorld[i] = FkUtil.BoneWorld(frames[i], skeleton, hips);

        // Horizontal (ground-plane) component of the hips trajectory.
        var horizontal = new Vector3[n];
        for (int i = 0; i < n; i++)
            horizontal[i] = hipsWorld[i].Pos - Vector3.Dot(hipsWorld[i].Pos, up) * up;

        switch (mode)
        {
            case RootMotionMode.Extract:
            {
                var smoothed = Smooth(horizontal, axes.SmoothHalfWindow);
                // Anchor at the first sample so clips begin at the origin.
                var origin = smoothed[0];
                bool hipsUnderRoot = root != hips && IsAncestorOf(skeleton, root, hips);
                for (int i = 0; i < n; i++)
                {
                    var f = frames[i];
                    var rootWorld0 = FkUtil.BoneWorld(f, skeleton, root);

                    // Root carries the smoothed ground path (world rotation preserved).
                    // The root is normally scene-top; when it has a parent, FK-correct
                    // through it so the WORLD position is the ground path regardless.
                    var rootWorld1 = new XForm(smoothed[i] - origin, rootWorld0.Rot);
                    var rootParent = skeleton[root].ParentIndex;
                    f[root] = rootParent < 0
                        ? rootWorld1
                        : XForm.ToLocal(FkUtil.BoneWorld(f, skeleton, rootParent), rootWorld1);

                    // Hips keep the residual (wobble + height). When the hips ride under
                    // the root the original WORLD pose must be preserved exactly (the root's
                    // new travel is absorbed by the re-derived local); otherwise (siblings /
                    // separate trees) the root's world displacement is subtracted so
                    // root ∘ residual still reproduces the source trajectory.
                    var hipsPos = hipsUnderRoot
                        ? hipsWorld[i].Pos
                        : hipsWorld[i].Pos - (rootWorld1.Pos - rootWorld0.Pos);
                    WriteWorld(f, skeleton, hips, new XForm(hipsPos, hipsWorld[i].Rot));
                }
                break;
            }

            case RootMotionMode.InPlace:
            {
                var origin = horizontal[0];
                for (int i = 0; i < n; i++)
                {
                    // Remove the actual path. Filtering here leaves a horizontal
                    // residual, particularly at clip boundaries and fast turns.
                    var travel = horizontal[i] - origin;
                    WriteWorld(frames[i], skeleton, hips,
                        new XForm(hipsWorld[i].Pos - travel, hipsWorld[i].Rot));
                }
                break;
            }
        }
    }

    /// <summary>
    /// Re-derives a bone's LOCAL from a desired WORLD transform against its actual parent's
    /// current world — the residual is expressed in the parent's frame via the inverse parent
    /// rotation, which is what keeps vertical motion vertical under rotated ancestors.
    /// </summary>
    private static void WriteWorld(XForm[] locals, SkeletonModel skeleton, int bone, in XForm world)
    {
        var parent = skeleton[bone].ParentIndex;
        locals[bone] = parent < 0
            ? world
            : XForm.ToLocal(FkUtil.BoneWorld(locals, skeleton, parent), world);
    }

    private static bool IsAncestorOf(SkeletonModel skeleton, int ancestor, int node)
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

    /// <summary>Centered moving average with edge clamping.</summary>
    private static Vector3[] Smooth(Vector3[] values, int halfWindow)
    {
        if (halfWindow <= 0)
        {
            // Array.Clone is explicitly denied by the s&box whitelist; Array.Copy is fine.
            var copy = new Vector3[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }

        var result = new Vector3[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var sum = Vector3.Zero;
            int count = 0;
            for (int k = -halfWindow; k <= halfWindow; k++)
            {
                int j = Math.Clamp(i + k, 0, values.Length - 1);
                sum += values[j];
                count++;
            }
            result[i] = sum / count;
        }
        return result;
    }
}
