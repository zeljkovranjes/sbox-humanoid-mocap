#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

namespace HumanoidMocap.Core.Target;

/// <summary>Options for <see cref="VmdlAugmenter.Augment"/>.</summary>
public sealed class AugmentOptions
{
    /// <summary>
    /// <c>default_root_bone_name</c> used when the vmdl has NO AnimationList yet (the value
    /// also seeds ExtractMotion nodes' <c>root_bone_name</c>) — same value the standalone
    /// writer uses (<see cref="RetargetTargetSpec.DefaultRootBone"/>). An existing
    /// AnimationList's own non-empty value always wins.
    /// </summary>
    public string DefaultRootBone { get; init; } = "";

    /// <summary>
    /// When true, every CopyPinky ring→pinky constraint in the vmdl (the citizen base
    /// model's transitional <c>AnimConstraintOrient</c> folder, or any constraint whose
    /// driven bone is a <c>finger_pinky_*</c>) gets its weights set to 0, so the exported
    /// pinky channels are no longer overridden at runtime. Idempotent; everything else in
    /// the document is untouched.
    /// </summary>
    public bool NeutralizePinkyConstraints { get; init; }

    /// <summary>
    /// Detected locomotion families to splice as Folder + 2DBlend groups (see
    /// <see cref="LocomotionSetDetector"/>). Each set's member entries are grouped under a
    /// Folder named <see cref="LocomotionSetSpec.FolderName"/> instead of being spliced at
    /// the AnimationList top level. Pipeline-owned locomotion folders are rebuilt AS A UNIT:
    /// a previous run's same-named folder is replaced wholesale when every AnimFile inside
    /// it is pipeline-owned per <see cref="DmxFolderRelative"/> — even when the new batch is
    /// a SHRUNKEN family (e.g. 8-way re-run as 4-way), whose stale members are simply
    /// dropped with the old folder. A hand-edited or foreign folder is never destroyed (it
    /// throws <see cref="VmdlAugmentException"/> instead). Null/empty = no grouping.
    /// </summary>
    public IReadOnlyList<LocomotionSetSpec>? LocomotionSets { get; init; }

    /// <summary>
    /// Assets-relative folder this batch's DMX files live in (back- or forward slashes,
    /// trailing slash ignored). Decides which existing nodes are PIPELINE-OWNED — an
    /// AnimFile whose <c>source_filename</c> sits under this folder — mirroring the
    /// name-seeding ownership rules in <c>Retargeter.ConvertBatch</c>, so a folder the
    /// batch's collision seeding treated as replaceable is also replaceable here. Empty
    /// (default) = only sources with no directory component count as ours.
    /// </summary>
    public string DmxFolderRelative { get; init; } = "";

    /// <summary>
    /// Animation source paths (<c>source_filename</c> values, assets-relative, either slash
    /// direction) the IO-owning caller knows are MISSING on disk. Existing AnimFile nodes
    /// referencing them are removed from the augmented output — a single unresolvable source
    /// fails the whole vmdl recompile (<c>Node 'X' resolve failure</c>), so a stale entry
    /// left over from an earlier batch (its DMX deleted or moved) would take every animation
    /// in the model down with it. Nodes whose name this batch (re)writes are exempt — their
    /// DMX is about to exist. 2DBlend nodes referencing a pruned sequence and Folder nodes
    /// emptied by the pruning are removed with it. Null/empty = keep everything (default;
    /// the augmenter itself never touches the filesystem).
    /// </summary>
    public IReadOnlyCollection<string>? MissingSourceFiles { get; init; }
}
