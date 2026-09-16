#nullable enable annotations

using System.Collections.Generic;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Solve;

/// <summary>How a mapped role's rotation is transferred by the <see cref="GeometricSolver"/>.</summary>
public enum RoleTransferMode
{
    /// <summary>
    /// Absolute canonical-orientation matching: the target's animated chain direction is
    /// driven to <b>equal</b> the source's (in character-frame coordinates). Right for limbs
    /// and the spine — the pose IS the direction — but it also imposes the source rig's rest
    /// proportions/posture on roles whose rest directions legitimately differ between rigs.
    /// </summary>
    AbsoluteDirection,

    /// <summary>
    /// Rest-relative delta: the source's canonical-space rotation <i>delta from its own
    /// normalized rest</i> is replayed onto the <b>target's</b> normalized rest
    /// (<c>W_t(f) = C_t·ΔC(f)·C_t⁻¹·R_tgtNormRest</c> with
    /// <c>ΔC(f) = C_s⁻¹·ΔR(f)·C_s</c>). The target keeps its own rest carriage (shoulder
    /// line height, neck-base angle) and moves with the source. Identical to
    /// <see cref="AbsoluteDirection"/> when source and target rigs coincide.
    /// </summary>
    DeltaFromRest,

    /// <summary>
    /// Character-space delta: the source's world-rotation delta from its normalized rest is
    /// re-expressed in character coordinates and applied to the <b>target's</b> normalized
    /// rest (<c>W_t(f) = M·ΔR(f)·M⁻¹·R_tgtNormRest</c> with <c>M = Q_tgt·Q_src⁻¹</c>, the
    /// same character basis change <see cref="AbsoluteDirection"/> premultiplies). Like
    /// <see cref="DeltaFromRest"/> the target keeps its own rest carriage, but the delta
    /// keeps its <i>world</i> rotation axes instead of being remapped through the per-role
    /// canonical frames — the faithful replay when the rigs' rest chain directions diverge
    /// so far that canonical-axis remapping would tilt every rotation axis by that
    /// divergence (measured 23–44° on feet: CMU/ARP ankle anatomy vs the s&amp;box rig's
    /// steep ankle, where canonical remapping mis-pitched planted feet by up to 47°).
    /// Identical to the other modes when source and target rigs coincide.
    /// </summary>
    CharacterDeltaFromRest,
}

/// <summary>Options controlling a single retarget solve (one clip → one output clip).</summary>
public sealed class SolveOptions
{
    // The grounding pipeline uses world vertical for legs as well as pelvis travel.
    // Keep the standalone solver's character-relative direction contract unchanged.
    internal bool GroundedLegDirections { get; init; }

    /// <summary>
    /// Default per-role transfer modes: shoulder girdle and neck carriage are
    /// <see cref="RoleTransferMode.DeltaFromRest"/> (each rig's clavicle line / neck-base
    /// direction is rig anatomy, not pose — absolute matching was measured to drag the
    /// s&amp;box shoulders 6–28° toward the source's flatter/lower clavicle line and is the
    /// "low shoulders, hunched neck" artifact), and feet are
    /// <see cref="RoleTransferMode.CharacterDeltaFromRest"/> (a rest foot→toe direction is
    /// ankle anatomy too — rigs diverge 11–44° from the s&amp;box rig's steep ankle, so
    /// absolute matching pitched planted feet up to 25° off flat, the "feet bent
    /// upward/inward" artifact; the character-space delta keeps the rotation's world axes,
    /// which canonical-frame remapping would tilt by that same divergence). The head is
    /// <see cref="RoleTransferMode.CharacterDeltaFromRest"/> for the same reason: the rest
    /// neck→head direction is head-joint-placement anatomy (measured 0–27° forward lean
    /// across neutral-rest rigs vs the s&amp;box rig's 25.5°), so the target keeps its own
    /// neutral skull attitude and replays the source's attitude <i>changes</i> — for the
    /// head this computes exactly what the previous virtual-frame absolute matching did.
    /// Two solver fallbacks adjust these defaults per rig pair: on a toe-less source the
    /// foot entries become <see cref="RoleTransferMode.DeltaFromRest"/> (virtual-foot
    /// fallback), and a source whose normalized rest head attitude is implausible as a
    /// neutral carriage (a posed bind — e.g. a chin-down/tilted fighting-stance rest,
    /// measured 40.7° forward / 16.9° lateral on such a rig where the delta replay read
    /// ~12° "looking up at an angle") switches the head to
    /// <see cref="RoleTransferMode.AbsoluteDirection"/> so the gaze follows the source
    /// absolutely instead of replaying deltas from a posed reference (see the
    /// <see cref="GeometricSolver"/> remarks for both). Everything else (limbs, spine,
    /// toes, fingers) stays absolute: there the worldspace direction IS the pose.
    /// </summary>
    public static IReadOnlyDictionary<BoneRole, RoleTransferMode> DefaultTransferModes { get; } =
        new Dictionary<BoneRole, RoleTransferMode>
        {
            [BoneRole.ClavicleL] = RoleTransferMode.DeltaFromRest,
            [BoneRole.ClavicleR] = RoleTransferMode.DeltaFromRest,
            [BoneRole.Neck] = RoleTransferMode.DeltaFromRest,
            [BoneRole.Head] = RoleTransferMode.CharacterDeltaFromRest,
            [BoneRole.FootL] = RoleTransferMode.CharacterDeltaFromRest,
            [BoneRole.FootR] = RoleTransferMode.CharacterDeltaFromRest,
        };

    /// <summary>
    /// Per-role transfer modes. Null (default) = <see cref="DefaultTransferModes"/> plus the
    /// solver's fallback heuristics (a toe-less source's virtual foot direction overrides
    /// the foot default to <see cref="RoleTransferMode.DeltaFromRest"/>, and a posed-rest
    /// source head overrides the head default to
    /// <see cref="RoleTransferMode.AbsoluteDirection"/> — see the
    /// <see cref="GeometricSolver"/> remarks). A non-null map REPLACES the defaults entirely
    /// and disables every fallback heuristic: each role uses exactly the mode in the map, and
    /// roles absent from it are <see cref="RoleTransferMode.AbsoluteDirection"/>. Pass an
    /// empty dictionary for fully absolute (legacy) behavior — API callers supplying a map
    /// opt out of all heuristics.
    /// </summary>
    public IReadOnlyDictionary<BoneRole, RoleTransferMode>? TransferModes { get; init; }

    /// <summary>
    /// Scale applied to the pelvis translation components perpendicular to the character up
    /// direction. Null (default) = automatic: target hip height / source hip height, both
    /// measured on the normalized rests.
    /// </summary>
    public float? HipScaleHorizontal { get; init; }

    /// <summary>
    /// Scale applied to the pelvis translation component along the character up direction.
    /// Null (default) = the same automatic hip-height ratio as <see cref="HipScaleHorizontal"/>.
    /// </summary>
    public float? HipScaleVertical { get; init; }

    /// <summary>Whether finger roles are transferred; when false, target finger bones keep
    /// their rest locals.</summary>
    public bool TransferFingers { get; init; } = true;

    /// <summary>Output clip name; null = the source clip's name.</summary>
    public string? ClipName { get; init; }

    /// <summary>Index of the source clip to retarget (<c>SourceScene.Clips</c>).</summary>
    public int ClipIndex { get; init; }
}
