#nullable enable annotations

using System.Collections.Generic;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.Core.Solve;

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
    /// Clavicles use the delta relative to a shared mapped chest ancestor, then
    /// inherit the solved target chest motion, so body turns do not become shrugs.
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
