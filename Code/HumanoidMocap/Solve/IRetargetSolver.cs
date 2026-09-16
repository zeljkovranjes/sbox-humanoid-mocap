#nullable enable annotations

using HumanoidMocap.Mapping;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;

namespace HumanoidMocap.Solve;

/// <summary>
/// A retarget solver: maps one source clip onto the target rig's skeleton. The pluggable
/// seam between the shipped <see cref="GeometricSolver"/> and possible future solvers
/// (design §10).
/// </summary>
public interface IRetargetSolver
{
    /// <summary>
    /// Retargets the clip selected by <paramref name="options"/> from <paramref name="source"/>
    /// onto <paramref name="target"/>. The result holds one local transform per target skeleton
    /// bone per frame (target bone order); fps and looping are copied from the source clip.
    /// </summary>
    Clip Solve(SourceScene source, MappingResult sourceMap, TargetRig target, SolveOptions options);
}
