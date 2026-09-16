#nullable enable annotations

using System;
using System.Collections.Generic;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Skeleton;

/// <summary>
/// The result of ingesting a source animation file: a skeleton with its rest pose plus the
/// file's clips resampled on a fixed fps grid.
/// </summary>
/// <remarks>
/// Unit policy: all translations are converted to <b>centimeters</b> at import time
/// (<see cref="UnitScaleCm"/> records the applied source-unit→cm factor for diagnostics).
/// Axis policy: the file's native axes are <b>preserved</b> — no axis conversion is performed.
/// The original FBX <c>GlobalSettings</c> axes are recorded so later pipeline stages can
/// interpret directions (axis indices: 0 = X, 1 = Y, 2 = Z).
/// </remarks>
public sealed class SourceScene
{
    /// <summary>Source skeleton with rest pose, in centimeters, native axes.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>Clips resampled at a fixed fps; locals indexed in skeleton bone order.</summary>
    public IReadOnlyList<Clip> Clips { get; }

    /// <summary>Source-unit → centimeter factor that was applied to all translations at import.</summary>
    public float UnitScaleCm { get; }

    /// <summary>Up axis index from GlobalSettings (0 = X, 1 = Y, 2 = Z; FBX default 1).</summary>
    public int UpAxis { get; }

    /// <summary>Sign of the up axis (+1 or -1).</summary>
    public int UpAxisSign { get; }

    /// <summary>Front axis index from GlobalSettings (FBX default 2 = Z).</summary>
    public int FrontAxis { get; }

    /// <summary>Sign of the front axis (+1 or -1).</summary>
    public int FrontAxisSign { get; }

    /// <summary>Coordinate (right) axis index from GlobalSettings (FBX default 0 = X).</summary>
    public int CoordAxis { get; }

    /// <summary>Sign of the coordinate axis (+1 or -1).</summary>
    public int CoordAxisSign { get; }

    /// <summary>OriginalUpAxis from GlobalSettings (-1 when the exporter did not record one).</summary>
    public int OriginalUpAxis { get; }

    /// <summary>
    /// Human-readable import diagnostics (e.g. cross-stack static-translation disagreements).
    /// Empty when the import was unambiguous.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// True (the default) when the format anchors the rest skeleton at an authored world
    /// placement shared with its motion data — FBX/glTF bind poses live in the same scene
    /// space as their animation curves, so absolute root translations are meaningful as-is.
    /// The BVH importer sets this FALSE: a BVH rest skeleton is OFFSETs only (root at the
    /// file origin, ground implicitly at the rest feet) while its MOTION root positions are
    /// absolute capture-volume coordinates (ground at the capture floor, subject anywhere on
    /// the stage) — the two spaces share no origin or ground level, so the solver normalizes
    /// each clip's placement against the rest skeleton before measuring pelvis travel
    /// (see <c>GeometricSolver</c> remarks).
    /// </summary>
    public bool RestPlacementAuthored { get; init; } = true;

    /// <summary>
    /// A role mapping AUTHORED inside the source file itself, when the format carries one —
    /// a VRM's <c>humanoid.humanBones</c> block (set by the glTF importer,
    /// <see cref="MappingSource.Authored"/>, confidence 1.0). Null for formats/files without
    /// authored role data. <see cref="Retargeter.ResolveMapping"/> consults this before user
    /// presets and shipped-profile detection — it is ground truth from the file.
    /// </summary>
    public MappingResult? AuthoredMapping { get; set; }

    /// <summary>Capture coordinates and resampled evidence, present for motion documents.
    /// Evidence uses this skeleton's bone order and the first clip's sample grid.</summary>
    public Motion.MotionSpace? CaptureSpace { get; init; }
    public IReadOnlyList<Motion.JointEvidence[]>? CaptureEvidence { get; init; }

    /// <summary>
    /// Alternate skeleton whose rest is the file's Pose/BindPose, offered by the FBX
    /// importer when the node transforms are GROSSLY posed away from it (a mid-pose
    /// export — the primary <see cref="Skeleton"/> then rests in an animation snapshot:
    /// a foot at hip height, IK'd hands). Same bones in the same order, so
    /// <see cref="Clips"/> and mappings apply to either. The retargeter adopts it after
    /// mapping when it makes the character stand straighter along the file's up axis
    /// (solving on a posed rest was measured 48° mean off ground truth on a real rig);
    /// null for consistent exports and non-FBX sources.
    /// </summary>
    public Skeleton? MidPoseBindSkeleton { get; set; }

    /// <summary>Creates a source scene container.</summary>
    public SourceScene(
        Skeleton skeleton,
        IReadOnlyList<Clip> clips,
        float unitScaleCm,
        int upAxis = 1, int upAxisSign = 1,
        int frontAxis = 2, int frontAxisSign = 1,
        int coordAxis = 0, int coordAxisSign = 1,
        int originalUpAxis = -1,
        IReadOnlyList<string>? notes = null)
    {
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        Clips = clips ?? throw new ArgumentNullException(nameof(clips));
        UnitScaleCm = unitScaleCm;
        UpAxis = upAxis;
        UpAxisSign = upAxisSign;
        FrontAxis = frontAxis;
        FrontAxisSign = frontAxisSign;
        CoordAxis = coordAxis;
        CoordAxisSign = coordAxisSign;
        OriginalUpAxis = originalUpAxis;
        Notes = notes ?? Array.Empty<string>();
    }
}
