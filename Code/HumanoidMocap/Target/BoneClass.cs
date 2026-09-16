#nullable enable annotations

namespace HumanoidMocap.Target;

/// <summary>
/// How each s&amp;box target-rig bone receives motion (design doc §3).
/// </summary>
public enum BoneClass
{
    /// <summary>
    /// Receives retargeted animation channels directly (pelvis, spine, limbs, fingers, ...).
    /// </summary>
    Animated,

    /// <summary>
    /// Driven by vmdl AnimConstraints at runtime — never export channels for these
    /// (twist bones, elbow/knee helpers, neck_clothing).
    /// </summary>
    ConstraintDriven,

    /// <summary>
    /// IK helper bones the default animgraph expects real baked channels for
    /// (root_IK, IK targets/attaches, ikrule bones, aim_matrix bones, hold bones).
    /// </summary>
    IkBaked,
}
