#nullable enable annotations

using System;
using System.Collections.Generic;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Solve;

/// <summary>
/// Cached, in-memory humanoid pose retargeter for live pose streams. It reuses the same
/// mapping, rest normalization, canonical frames, proportion handling and root transform
/// math as <see cref="GeometricSolver"/>, without creating clips or writing files per frame.
/// </summary>
/// <remarks>
/// A runtime instance owns reusable scratch buffers and is intentionally not thread-safe.
/// Create one instance per independently evaluated character/pose stream. Construction is
/// the expensive mapping/rig-analysis boundary; <see cref="TryRetarget"/> performs no
/// hierarchy scan, mapping rebuild, file IO, or successful-path heap allocation.
/// </remarks>
public sealed class RuntimePoseRetargeter
{
    private readonly GeometricSolver.Plan _plan;
    private readonly XForm[] _scratchDestination;

    /// <summary>The immutable source skeleton used to compile this retarget plan.</summary>
    public SkeletonModel SourceSkeleton { get; }

    /// <summary>The source role mapping used to compile this retarget plan.</summary>
    public MappingResult SourceMapping { get; }

    /// <summary>The target rig used to compile this retarget plan.</summary>
    public TargetRig TargetRig { get; }

    /// <summary>Number of local transforms required in every source pose.</summary>
    public int SourceBoneCount => SourceSkeleton.Count;

    /// <summary>Number of local transforms required in every destination pose.</summary>
    public int TargetBoneCount => TargetRig.Skeleton.Count;

    /// <summary>
    /// Compiles a reusable runtime solve plan.
    /// </summary>
    /// <param name="sourceSkeleton">Source hierarchy and rest pose, in centimeters.</param>
    /// <param name="sourceMapping">Canonical humanoid roles on the source hierarchy.</param>
    /// <param name="targetRig">Mapped target hierarchy.</param>
    /// <param name="initialSourcePose">
    /// Optional initial/reference pose for sources whose bind is non-anatomical (for example
    /// the SOMA/G1 stick-style capture path). Null uses the source rest pose.
    /// </param>
    /// <param name="options">Existing geometric solve options; null uses compatible defaults.</param>
    /// <param name="restPlacementAuthored">
    /// Whether source root placement shares the source rest coordinate space. Set false for
    /// absolute capture-volume streams that should be recentered like BVH input.
    /// </param>
    public RuntimePoseRetargeter(
        SkeletonModel sourceSkeleton,
        MappingResult sourceMapping,
        TargetRig targetRig,
        IReadOnlyList<XForm>? initialSourcePose = null,
        SolveOptions? options = null,
        bool restPlacementAuthored = true)
    {
        SourceSkeleton = sourceSkeleton ?? throw new ArgumentNullException(nameof(sourceSkeleton));
        SourceMapping = sourceMapping ?? throw new ArgumentNullException(nameof(sourceMapping));
        TargetRig = targetRig ?? throw new ArgumentNullException(nameof(targetRig));

        ValidateMapping(sourceSkeleton, sourceMapping);

        var reference = new XForm[sourceSkeleton.Count];
        if (initialSourcePose is null)
        {
            for (var i = 0; i < reference.Length; i++)
                reference[i] = sourceSkeleton[i].RestLocal;
        }
        else
        {
            if (initialSourcePose.Count != sourceSkeleton.Count)
                throw new ArgumentException(
                    $"Initial pose has {initialSourcePose.Count} bones but the source skeleton has {sourceSkeleton.Count}.",
                    nameof(initialSourcePose));
            for (var i = 0; i < reference.Length; i++)
                reference[i] = initialSourcePose[i];
        }

        var clip = new Clip("runtime_reference", 30f, false, new List<XForm[]> { reference });
        var scene = new SourceScene(sourceSkeleton, new[] { clip }, unitScaleCm: 1f)
        {
            RestPlacementAuthored = restPlacementAuthored,
        };

        _plan = new GeometricSolver.Plan(scene, clip, sourceMapping, targetRig, options ?? new SolveOptions());
        _scratchDestination = new XForm[targetRig.Skeleton.Count];
    }

    /// <summary>
    /// Retargets one parent-local source pose into a caller-owned parent-local destination.
    /// Throws for invalid buffer sizes or non-finite/degenerate input.
    /// </summary>
    public void Retarget(ReadOnlySpan<XForm> sourceLocals, Span<XForm> destinationLocals)
    {
        if (destinationLocals.Length < TargetBoneCount)
            throw new ArgumentException(
                $"Destination has {destinationLocals.Length} bones but the target skeleton requires {TargetBoneCount}.",
                nameof(destinationLocals));

        _plan.SolveFrameInto(sourceLocals, _scratchDestination);
        _scratchDestination.AsSpan().CopyTo(destinationLocals);
    }

    /// <summary>
    /// Safe runtime variant. On failure it returns false, reports an actionable diagnostic,
    /// and leaves <paramref name="destinationLocals"/> unchanged.
    /// </summary>
    public bool TryRetarget(
        ReadOnlySpan<XForm> sourceLocals,
        Span<XForm> destinationLocals,
        out string? error)
    {
        if (sourceLocals.Length != SourceBoneCount)
        {
            error = $"Source pose has {sourceLocals.Length} bones; expected {SourceBoneCount}.";
            return false;
        }

        if (destinationLocals.Length < TargetBoneCount)
        {
            error = $"Destination pose has {destinationLocals.Length} bones; expected at least {TargetBoneCount}.";
            return false;
        }

        try
        {
            _plan.SolveFrameInto(sourceLocals, _scratchDestination);
            _scratchDestination.AsSpan().CopyTo(destinationLocals);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error = $"Runtime retarget failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Builds a target rig from the library's existing automatic humanoid mapper and then
    /// compiles a runtime plan. This is an additive convenience for mocap, VR and procedural
    /// animation consumers; it has no dependency on any motion-generation package.
    /// </summary>
    public static bool TryCreateAutoMapped(
        SkeletonModel sourceSkeleton,
        MappingResult sourceMapping,
        SkeletonModel targetSkeleton,
        out RuntimePoseRetargeter? retargeter,
        out string? error,
        IReadOnlyList<XForm>? initialSourcePose = null,
        SolveOptions? options = null)
    {
        retargeter = null;
        error = null;
        try
        {
            var targetMap = AutoMapper.Map(targetSkeleton);
            if (!HasMinimumHumanoidRoles(targetMap))
            {
                error = "Target skeleton could not be mapped as a humanoid (hips, head, hands, and feet are required).";
                return false;
            }

            var targetRig = TargetRig.FromSkeleton(targetSkeleton, targetMap);
            retargeter = new RuntimePoseRetargeter(
                sourceSkeleton, sourceMapping, targetRig, initialSourcePose, options);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error = $"Could not create runtime retargeter: {ex.Message}";
            return false;
        }
    }

    private static void ValidateMapping(SkeletonModel skeleton, MappingResult mapping)
    {
        foreach (var (role, bone) in mapping.RoleToBone)
        {
            if ((uint)bone >= (uint)skeleton.Count)
                throw new ArgumentException(
                    $"Source mapping assigns {role} to bone {bone}, outside the source skeleton (count {skeleton.Count}).",
                    nameof(mapping));
        }

        if (!mapping.RoleToBone.ContainsKey(BoneRole.Hips))
            throw new ArgumentException("Source mapping must include the Hips role.", nameof(mapping));
    }

    private static bool HasMinimumHumanoidRoles(MappingResult mapping)
        => mapping.RoleToBone.ContainsKey(BoneRole.Hips)
           && mapping.RoleToBone.ContainsKey(BoneRole.Head)
           && mapping.RoleToBone.ContainsKey(BoneRole.HandL)
           && mapping.RoleToBone.ContainsKey(BoneRole.HandR)
           && mapping.RoleToBone.ContainsKey(BoneRole.FootL)
           && mapping.RoleToBone.ContainsKey(BoneRole.FootR);
}
