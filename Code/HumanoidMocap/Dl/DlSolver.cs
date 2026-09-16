#nullable enable annotations

using System;
using HumanoidMocap.Mapping;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Dl;

/// <summary>
/// The experimental deep-learning retarget solver (design §10, Milestone 10): SAME
/// (Skeleton-Agnostic Motion Embedding, Lee et al., SIGGRAPH Asia 2023) pretrained
/// checkpoint, run as a pure-managed float32 forward pass — no ONNX Runtime, no native
/// dependencies. Strictly the no-profile fallback: the <see cref="GeometricSolver"/>
/// remains better wherever a role mapping exists.
/// </summary>
/// <remarks>
/// <para><b>Mapping is not required.</b> The model is skeleton-agnostic; the
/// <c>sourceMap</c> argument is consulted only for hips identification and the
/// rest-geometry world alignment (both fall back to topology/axis heuristics when roles
/// are missing) — per-role assignments are otherwise ignored, which is the whole point of
/// the fallback. Fingers stay at rest: the checkpoint was trained finger-less, and the
/// CopyPinky handling of the geometric path applies unchanged (finger channels carry rest
/// pose, so the base model's constraints keep driving pinkies).</para>
/// <para><b>Weights</b> are passed as bytes (no file IO in <c>Code/</c>): the Editor reads
/// <c>Assets/humanoid_mocap/dl/same_v1.weights</c> (CC BY-NC 4.0, see the adjacent
/// ATTRIBUTION.md) and hands them to <see cref="RetargetTargetSpec.DlWeights"/>.</para>
/// <para><b>Options:</b> <see cref="SolveOptions.ClipIndex"/>/<see cref="SolveOptions.ClipName"/>
/// are honored; hip scales and <see cref="SolveOptions.TransferFingers"/> do not apply to
/// this solver (the decoder produces the target-shaped trajectory directly).</para>
/// <para>Deterministic: fixed-order float32 arithmetic throughout. Output is asserted
/// finite. ~0.1 s per 100 frames on one core.</para>
/// </remarks>
public sealed class DlSolver : IRetargetSolver
{
    private readonly SameModel _model;
    private readonly SameStats _stats;

    /// <summary>Builds the solver from the raw bytes of the committed weight blob.</summary>
    /// <exception cref="FormatException">Thrown when the bytes are not a valid weight blob.</exception>
    public DlSolver(byte[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var parsed = SameWeights.Parse(weights);
        _model = new SameModel(parsed);
        _stats = new SameStats(parsed);
    }

    /// <inheritdoc />
    public Clip Solve(SourceScene source, MappingResult sourceMap, TargetRig target, SolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new SolveOptions();
        if (options.ClipIndex < 0 || options.ClipIndex >= source.Clips.Count)
            throw new ArgumentOutOfRangeException(nameof(options), options.ClipIndex,
                $"ClipIndex out of range; the source has {source.Clips.Count} clip(s).");
        var clip = source.Clips[options.ClipIndex];

        var sourceGraph = SameFeatures.BuildSourceGraph(source, options.ClipIndex, sourceMap, _stats);
        var z = _model.Encode(
            sourceGraph.X, sourceGraph.EdgeSrc, sourceGraph.EdgeDst,
            sourceGraph.Batch, sourceGraph.FrameCount);

        var targetGraph = SameTarget.Build(target, _stats);
        var (tiledX, edgeSrc, edgeDst, batch) = SameTarget.Tile(targetGraph, sourceGraph.FrameCount);
        var hatD = _model.Decode(z, tiledX, edgeSrc, edgeDst, batch);

        return SameTarget.DecodeClip(
            hatD, targetGraph, sourceGraph.FrameCount, _stats,
            options.ClipName ?? clip.Name, clip.Fps, clip.Looping);
    }
}
