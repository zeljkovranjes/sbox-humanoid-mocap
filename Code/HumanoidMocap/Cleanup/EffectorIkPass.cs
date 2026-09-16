#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// A three-joint limb chain identified by skeleton bone indices:
/// hip/knee/ankle for a leg, shoulder/elbow/wrist for an arm.
/// <see cref="Lower"/> is expected to be a child of <see cref="Upper"/> and
/// <see cref="End"/> a child of <see cref="Lower"/> (the usual humanoid layout);
/// locals are re-derived against the actual parents, so the solve stays exact
/// for direct parent-child chains.
/// </summary>
public sealed class LimbChain
{
    /// <summary>Upper joint bone index (hip / shoulder).</summary>
    public required int Upper { get; init; }

    /// <summary>Mid joint bone index (knee / elbow).</summary>
    public required int Lower { get; init; }

    /// <summary>End effector bone index (ankle / wrist).</summary>
    public required int End { get; init; }
}

/// <summary>
/// End-effector IK cleanup pass: per frame, runs analytic <see cref="TwoBoneIk"/> on a limb
/// chain and folds the resulting world-space deltas back into the frame's LOCAL rotations.
/// Operates on raw frame lists (one local <see cref="XForm"/> per bone) + a skeleton +
/// explicit bone indices, so it is solver-agnostic and testable with synthetic data.
/// </summary>
/// <remarks>
/// Correction model (rotations only — rest-local translations are preserved):
/// <list type="bullet">
/// <item><see cref="LimbChain.Upper"/> and <see cref="LimbChain.Lower"/> world rotations are
/// premultiplied by the solver's deltas, then re-derived as locals against their parents'
/// world rotations. Because local translations are untouched, the FK positions land exactly
/// on the solver's <c>b' = a + dU*(b−a)</c>, <c>c' = b' + dL*(c−b)</c> convention.</item>
/// <item><see cref="LimbChain.End"/>'s local rotation is counter-rotated by the inverse of
/// the Lower world delta so the effector's WORLD rotation is unchanged — the IK swing must
/// not disturb foot/hand orientation. Descendants (toes, fingers) follow naturally via FK.</item>
/// </list>
/// </remarks>
public static class EffectorIk
{
    /// <summary>
    /// Pulls the chain's end effector onto <paramref name="goalsWorld"/> (one world-space goal
    /// per frame), modifying the frames' local rotations in place.
    /// </summary>
    /// <param name="frames">Per-frame local transforms (skeleton bone order); modified in place.</param>
    /// <param name="skeleton">Bone hierarchy the frames are expressed against.</param>
    /// <param name="chain">Limb chain bone indices.</param>
    /// <param name="goalsWorld">World-space effector goal per frame; must match <paramref name="frames"/> in count.</param>
    /// <param name="stableBendAxis">Hinge axis used when the chain is near-collinear (see <see cref="TwoBoneIk.Solve"/>).</param>
    /// <param name="soften">Reach-softening fraction forwarded to <see cref="TwoBoneIk.Solve"/>.</param>
    public static void ApplyGoals(
        List<XForm[]> frames,
        SkeletonModel skeleton,
        LimbChain chain,
        IReadOnlyList<Vector3> goalsWorld,
        Vector3 stableBendAxis,
        float soften = 0.02f)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(goalsWorld);
        if (goalsWorld.Count != frames.Count)
            throw new ArgumentException(
                $"Goal count ({goalsWorld.Count}) must match frame count ({frames.Count}).", nameof(goalsWorld));

        // One FK scratch buffer shared across all frames (perf: this pass used to allocate
        // four world arrays per frame).
        var world = new XForm[skeleton.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            var locals = frames[i];
            FkUtil.ToWorld(locals, skeleton, world);
            var result = TwoBoneIk.Solve(
                world[chain.Upper].Pos, world[chain.Lower].Pos, world[chain.End].Pos,
                goalsWorld[i], soften, stableBendAxis);
            ApplyWorldDeltas(locals, skeleton, chain.Upper, chain.Lower, chain.End,
                result.UpperWorldDelta, result.LowerWorldDelta, world);
        }
    }

    /// <summary>
    /// Folds a pair of world-space rotation deltas (per the <see cref="TwoBoneIk.Result"/>
    /// convention) into a frame's local rotations: Upper and Lower world rotations get the
    /// deltas premultiplied and are re-derived as locals; End's local is compensated so its
    /// world rotation stays exactly what it was before the call. Local translations untouched.
    /// A parentless Lower or End bone breaks the chain assumption (its local IS its world;
    /// there is no parent frame to re-derive against), so the correction is skipped entirely.
    /// </summary>
    /// <param name="worldScratch">Caller-owned FK scratch buffer (≥ skeleton bone count);
    /// overwritten by this call.</param>
    internal static void ApplyWorldDeltas(
        XForm[] locals, SkeletonModel skeleton, int upper, int lower, int end,
        Quaternion upperDelta, Quaternion lowerDelta, XForm[] worldScratch)
    {
        // Guard: lower/end without a parent would index world[-1] below — treat the whole
        // correction as a no-op rather than corrupting part of the chain.
        if (skeleton[lower].ParentIndex < 0 || skeleton[end].ParentIndex < 0)
            return;

        var world = worldScratch;
        FkUtil.ToWorld(locals, skeleton, world);
        var lowerWorldRot0 = world[lower].Rot;
        var endWorldRot0 = world[end].Rot;

        // Upper: premultiply world rotation, re-derive local against (unchanged) parent.
        var upperParent = skeleton[upper].ParentIndex;
        var upperParentRot = upperParent < 0 ? Quaternion.Identity : world[upperParent].Rot;
        var upperWorldRot1 = MathQ.Normalize(upperDelta * world[upper].Rot);
        locals[upper] = new XForm(
            locals[upper].Pos,
            MathQ.Normalize(Quaternion.Conjugate(upperParentRot) * upperWorldRot1));

        // Lower: same, but its parent's world rotation just changed — ancestor-chain walk
        // over the updated locals (bit-identical to a full FK re-run, without allocating).
        var lowerParentRot = FkUtil.BoneWorld(locals, skeleton, skeleton[lower].ParentIndex).Rot;
        var lowerWorldRot1 = MathQ.Normalize(lowerDelta * lowerWorldRot0);
        locals[lower] = new XForm(
            locals[lower].Pos,
            MathQ.Normalize(Quaternion.Conjugate(lowerParentRot) * lowerWorldRot1));

        // End: counter-rotate so its WORLD rotation is unchanged (effector orientation must
        // not be disturbed by the IK swing). For end.parent == lower this is exactly the
        // inverse of the Lower world delta.
        var endParentRot = FkUtil.BoneWorld(locals, skeleton, skeleton[end].ParentIndex).Rot;
        locals[end] = new XForm(
            locals[end].Pos,
            MathQ.Normalize(Quaternion.Conjugate(endParentRot) * endWorldRot0));
    }
}
