#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Target;

/// <summary>
/// Name-based bone classification and role assignment rules for the s&amp;box humanoid rig
/// (design doc §3). Used by <see cref="TargetRigGenerator"/> to produce the committed
/// target-rig definition; consumers should read classes/roles from <see cref="TargetRig"/>
/// rather than re-deriving them.
/// </summary>
public static partial class SboxBoneClassifier
{
    // Plain cached Regex instead of [GeneratedRegex]: the s&box in-engine compiler
    // does not run the regex source generator, so partial GeneratedRegex methods
    // fail to compile there ("must have an implementation part").
    private static readonly Regex TwistSuffixRegex = new(@"_twist\d+$");
    private static Regex TwistSuffix() => TwistSuffixRegex;

    // arm_elbow/leg_knee on the human rig; leg_glute on the legacy citizen rig.
    private static readonly Regex ConstraintHelperRegex = new(@"^(arm_elbow|leg_knee|leg_glute)_helper(_|$)");
    private static Regex ConstraintHelper() => ConstraintHelperRegex;

    private static readonly Regex IkSuffixRegex = new(@"(_IK_target|_IK_attach|_ikrule)$");
    private static Regex IkSuffix() => IkSuffixRegex;

    private static readonly Regex AimMatrixPrefixRegex = new(@"^aim_matrix_");
    private static Regex AimMatrixPrefix() => AimMatrixPrefixRegex;

    // Face bones on the classic citizen rig (eye_L/R, ear_L/R, face_lid_*): no canonical
    // role exists for them, the solver never retargets them, and the engine's procedural
    // systems (eye look-at, blinking) pose them in game.
    private static readonly Regex FacePrefixRegex = new(@"^(eye|ear|face)_");
    private static Regex FacePrefix() => FacePrefixRegex;

    // Case-insensitive twin of FacePrefixRegex for IsFaceBone: custom rigs classified by
    // BoneClassRules match face names case-insensitively, and the channel decision must
    // agree with that classification.
    private static readonly Regex FacePrefixAnyCaseRegex = new(@"^(eye|ear|face)_", RegexOptions.IgnoreCase);

    /// <summary>
    /// True for face bones (<c>eye_*</c>, <c>ear_*</c>, <c>face_*</c>). Unlike the
    /// twist/helper <see cref="BoneClass.ConstraintDriven"/> bones — which the model's
    /// AnimConstraintList re-drives on every evaluated frame — NOTHING drives face bones in
    /// a compiled sequence: the constraint list never references them and the engine's eye
    /// look-at / blinking only runs in game. A face joint left channel-less in the DMX is
    /// baked statically by resourcecompiler, so the eyes detach from the moving head in
    /// ModelDoc ("eyes out of their sockets"). Retargeted clips must therefore carry
    /// rest-local channels for them — exactly what the shipped fbx2dmx clips do
    /// (reference: <c>dev/m0/ref_idlepose.dmx</c> carries <c>eye_L_p/_o</c>,
    /// <c>face_lid_*_p/_o</c> channels).
    /// </summary>
    public static bool IsFaceBone(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return FacePrefixAnyCaseRegex.IsMatch(name);
    }

    /// <summary>Classifies an s&amp;box rig bone by name.</summary>
    public static BoneClass Classify(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TwistSuffix().IsMatch(name) || ConstraintHelper().IsMatch(name) || name == "neck_clothing"
            || FacePrefix().IsMatch(name))
            return BoneClass.ConstraintDriven;

        if (name == "root_IK" || name == "hold_L" || name == "hold_R"
            || IkSuffix().IsMatch(name) || AimMatrixPrefix().IsMatch(name))
            return BoneClass.IkBaked;

        return BoneClass.Animated;
    }

    /// <summary>
    /// Returns the canonical role of an s&amp;box rig bone, or null when the bone carries no
    /// role (every non-<see cref="BoneClass.Animated"/> bone, by construction).
    /// </summary>
    public static BoneRole? RoleFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Classify(name) != BoneClass.Animated)
            return null;

        return RoleByName.TryGetValue(name, out var role) ? role : null;
    }

    /// <summary>Role table for the s&amp;box bone names (built once, ordinal-keyed).</summary>
    private static readonly IReadOnlyDictionary<string, BoneRole> RoleByName = BuildRoleTable();

    private static Dictionary<string, BoneRole> BuildRoleTable()
    {
        var table = new Dictionary<string, BoneRole>(StringComparer.Ordinal)
        {
            ["pelvis"] = BoneRole.Hips,
            ["spine_0"] = BoneRole.Spine0,
            ["spine_1"] = BoneRole.Spine1,
            ["spine_2"] = BoneRole.Spine2,
            ["neck_0"] = BoneRole.Neck,
            ["head"] = BoneRole.Head,
        };

        foreach (var side in new[] { "L", "R" })
        {
            table[$"clavicle_{side}"] = ParseRole($"Clavicle{side}");
            table[$"arm_upper_{side}"] = ParseRole($"UpperArm{side}");
            table[$"arm_lower_{side}"] = ParseRole($"LowerArm{side}");
            table[$"hand_{side}"] = ParseRole($"Hand{side}");
            table[$"leg_upper_{side}"] = ParseRole($"UpperLeg{side}");
            table[$"leg_lower_{side}"] = ParseRole($"LowerLeg{side}");
            table[$"ankle_{side}"] = ParseRole($"Foot{side}");
            table[$"ball_{side}"] = ParseRole($"Toe{side}");

            foreach (var (finger, rolePrefix) in new[]
            {
                ("thumb", "Thumb"), ("index", "Index"), ("middle", "Middle"),
                ("ring", "Ring"), ("pinky", "Pinky"),
            })
            {
                // Segment naming on the rig: meta = metacarpal, 0/1/2 = proximal/middle/distal.
                // The s&box thumb has no metacarpal bone, but the rule is kept uniform so a
                // hypothetical finger_thumb_meta_* would still map (the enum defines ThumbMeta*).
                table[$"finger_{finger}_meta_{side}"] = ParseRole($"{rolePrefix}Meta{side}");
                table[$"finger_{finger}_0_{side}"] = ParseRole($"{rolePrefix}Prox{side}");
                table[$"finger_{finger}_1_{side}"] = ParseRole($"{rolePrefix}Mid{side}");
                table[$"finger_{finger}_2_{side}"] = ParseRole($"{rolePrefix}Dist{side}");
            }
        }

        return table;
    }

    private static BoneRole ParseRole(string name) => Enum.Parse<BoneRole>(name);
}
