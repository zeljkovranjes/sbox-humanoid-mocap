#nullable enable annotations

namespace HumanoidMocap.Mapping;

/// <summary>
/// Canonical humanoid bone roles shared by all rigs (source and target). A mapping assigns
/// source bones to roles; the target rig assigns its animated bones to roles; the solver
/// transfers motion role-by-role.
/// </summary>
/// <remarks>
/// The enum is a superset of any single rig: <see cref="Spine3"/>/<see cref="Spine4"/> exist
/// for 4–5 bone source spines (the s&amp;box rig has 3), and <see cref="ThumbMetaL"/>/<see
/// cref="ThumbMetaR"/> exist for source rigs with thumb metacarpals (the s&amp;box rig has
/// none — its thumb chain starts at the proximal phalanx). Finger roles follow the pattern
/// finger × segment (Meta = metacarpal, Prox/Mid/Dist = phalanges) × side.
/// </remarks>
public enum BoneRole
{
    /// <summary>Pelvis / hips — the skeleton's translating root role.</summary>
    Hips,

    /// <summary>First spine bone above the hips.</summary>
    Spine0,

    /// <summary>Second spine bone.</summary>
    Spine1,

    /// <summary>Third spine bone.</summary>
    Spine2,

    /// <summary>Fourth spine bone (source rigs with 4+ spine bones only).</summary>
    Spine3,

    /// <summary>Fifth spine bone (source rigs with 5 spine bones only).</summary>
    Spine4,

    /// <summary>Neck.</summary>
    Neck,

    /// <summary>Head.</summary>
    Head,

    /// <summary>Left clavicle / shoulder.</summary>
    ClavicleL,

    /// <summary>Right clavicle / shoulder.</summary>
    ClavicleR,

    /// <summary>Left upper arm.</summary>
    UpperArmL,

    /// <summary>Right upper arm.</summary>
    UpperArmR,

    /// <summary>Left lower arm / forearm.</summary>
    LowerArmL,

    /// <summary>Right lower arm / forearm.</summary>
    LowerArmR,

    /// <summary>Left hand.</summary>
    HandL,

    /// <summary>Right hand.</summary>
    HandR,

    /// <summary>Left upper leg / thigh.</summary>
    UpperLegL,

    /// <summary>Right upper leg / thigh.</summary>
    UpperLegR,

    /// <summary>Left lower leg / calf.</summary>
    LowerLegL,

    /// <summary>Right lower leg / calf.</summary>
    LowerLegR,

    /// <summary>Left foot (ankle bone).</summary>
    FootL,

    /// <summary>Right foot (ankle bone).</summary>
    FootR,

    /// <summary>Left toe (ball of foot).</summary>
    ToeL,

    /// <summary>Right toe (ball of foot).</summary>
    ToeR,

    /// <summary>Left thumb metacarpal (source rigs only; absent on the s&amp;box rig).</summary>
    ThumbMetaL,

    /// <summary>Left thumb proximal phalanx.</summary>
    ThumbProxL,

    /// <summary>Left thumb middle phalanx.</summary>
    ThumbMidL,

    /// <summary>Left thumb distal phalanx.</summary>
    ThumbDistL,

    /// <summary>Right thumb metacarpal (source rigs only; absent on the s&amp;box rig).</summary>
    ThumbMetaR,

    /// <summary>Right thumb proximal phalanx.</summary>
    ThumbProxR,

    /// <summary>Right thumb middle phalanx.</summary>
    ThumbMidR,

    /// <summary>Right thumb distal phalanx.</summary>
    ThumbDistR,

    /// <summary>Left index metacarpal.</summary>
    IndexMetaL,

    /// <summary>Left index proximal phalanx.</summary>
    IndexProxL,

    /// <summary>Left index middle phalanx.</summary>
    IndexMidL,

    /// <summary>Left index distal phalanx.</summary>
    IndexDistL,

    /// <summary>Right index metacarpal.</summary>
    IndexMetaR,

    /// <summary>Right index proximal phalanx.</summary>
    IndexProxR,

    /// <summary>Right index middle phalanx.</summary>
    IndexMidR,

    /// <summary>Right index distal phalanx.</summary>
    IndexDistR,

    /// <summary>Left middle-finger metacarpal.</summary>
    MiddleMetaL,

    /// <summary>Left middle-finger proximal phalanx.</summary>
    MiddleProxL,

    /// <summary>Left middle-finger middle phalanx.</summary>
    MiddleMidL,

    /// <summary>Left middle-finger distal phalanx.</summary>
    MiddleDistL,

    /// <summary>Right middle-finger metacarpal.</summary>
    MiddleMetaR,

    /// <summary>Right middle-finger proximal phalanx.</summary>
    MiddleProxR,

    /// <summary>Right middle-finger middle phalanx.</summary>
    MiddleMidR,

    /// <summary>Right middle-finger distal phalanx.</summary>
    MiddleDistR,

    /// <summary>Left ring-finger metacarpal.</summary>
    RingMetaL,

    /// <summary>Left ring-finger proximal phalanx.</summary>
    RingProxL,

    /// <summary>Left ring-finger middle phalanx.</summary>
    RingMidL,

    /// <summary>Left ring-finger distal phalanx.</summary>
    RingDistL,

    /// <summary>Right ring-finger metacarpal.</summary>
    RingMetaR,

    /// <summary>Right ring-finger proximal phalanx.</summary>
    RingProxR,

    /// <summary>Right ring-finger middle phalanx.</summary>
    RingMidR,

    /// <summary>Right ring-finger distal phalanx.</summary>
    RingDistR,

    /// <summary>Left pinky metacarpal.</summary>
    PinkyMetaL,

    /// <summary>Left pinky proximal phalanx.</summary>
    PinkyProxL,

    /// <summary>Left pinky middle phalanx.</summary>
    PinkyMidL,

    /// <summary>Left pinky distal phalanx.</summary>
    PinkyDistL,

    /// <summary>Right pinky metacarpal.</summary>
    PinkyMetaR,

    /// <summary>Right pinky proximal phalanx.</summary>
    PinkyProxR,

    /// <summary>Right pinky middle phalanx.</summary>
    PinkyMidR,

    /// <summary>Right pinky distal phalanx.</summary>
    PinkyDistR,
}
