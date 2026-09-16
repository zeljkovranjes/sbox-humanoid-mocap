#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Target;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Bakes real animation channels onto the <see cref="BoneClass.IkBaked"/> helper bones from
/// the already-solved <see cref="BoneClass.Animated"/> body bones. The s&amp;box default
/// animgraph reads these bones, so every retargeted clip must carry data for them.
/// </summary>
/// <remarks>
/// <para>
/// Every rule below was DERIVED from shipped Facepunch clips
/// (<c>Citizen@Run_N.fbx</c>, <c>Citizen@Dab.fbx</c>, plus the <c>*_AimMatrix</c> clips for
/// the aim bones) by measuring per-frame relationships between the IK bones and the solved
/// body bones, and is verified by reproducing those clips' IK channels in
/// <c>IkBoneBakerTests</c>. Measured residuals on both probe clips (positions cm, FBX scene
/// space of the citizen sources, Y-up):
/// </para>
/// <list type="bullet">
/// <item><c>foot_L/R_IK_target</c>: world transform equals the world transform of
/// <c>ankle_L/R</c> exactly (max residual 1e-4 cm / 0.000°).</item>
/// <item><c>hand_L/R_IK_target</c>: world transform equals <c>hand_L/R</c> exactly
/// (1e-4 cm / 0.000°).</item>
/// <item><c>hand_L_to_R_ikrule</c> (parented under <c>hand_R</c>): world transform equals
/// <c>hand_L</c> exactly — the bone named <c>hand_X_to_Y_ikrule</c> carries hand X's world
/// transform expressed in hand Y's space (1e-4 cm / 0.000°). Symmetrically for
/// <c>hand_R_to_L_ikrule</c>.</item>
/// <item><c>root_IK</c>: world position equals the pelvis world position projected onto the
/// ground plane — the lateral components match the pelvis exactly (0.0000 cm) and the up
/// component stays at root_IK's rest height (0). World rotation never animates: it stays at
/// root_IK's rest world rotation (0.000° deviation even while the pelvis yaws), which is the
/// up-axis frame change quaternion, i.e. identity in engine space. The up axis is derived
/// from rest geometry (the dominant component of pelvisRest − rootIkRest), not hardcoded.</item>
/// <item><c>hold_L/R</c>: local transform stays at rest (0.0000 cm / 0.000°).</item>
/// <item><c>hand_L/R_IK_attach</c>: absent from every probed shipped clip skeleton (the clip
/// FBXs parent the hand IK targets directly under <c>root_IK</c>); in the human_male rig its
/// rest local under <c>root_IK</c> is identity. Keeping rest reproduces the shipped layout,
/// and the hand targets still land on the hands because their locals are solved through the
/// attach's world.</item>
/// <item><c>aim_matrix_*</c>: absent from all regular shipped clips. Even in the dedicated
/// <c>*_AimMatrix</c> clips, <c>aim_matrix_02/03</c> sit exactly at rest local
/// (0.0000 cm / 0.000°) and <c>aim_matrix_01</c> holds an animator-authored constant pose
/// (not derivable from the body) — these bones are animgraph aim-space references, so
/// retargeted clips keep them at rest.</item>
/// </list>
/// <para>
/// Any <see cref="BoneClass.IkBaked"/> bone without a recognized name also keeps its rest
/// local (the conservative choice — identical to omitting the channel).
/// </para>
/// </remarks>
public static class IkBoneBaker
{
    /// <summary>
    /// Overwrites the local transforms of every <see cref="BoneClass.IkBaked"/> bone in every
    /// frame, deriving them from the already-solved body bones of the same frame. All other
    /// bones' locals are never touched. Deterministic; mutates <paramref name="frames"/> in
    /// place.
    /// </summary>
    /// <param name="frames">Per-frame local transforms, one <see cref="XForm"/> per bone in
    /// <paramref name="target"/> skeleton order (e.g. <c>Clip.Frames</c>).</param>
    /// <param name="target">The target rig supplying skeleton, bone classes and roles.</param>
    /// <exception cref="ArgumentException">Thrown when a frame's length does not match the
    /// target skeleton.</exception>
    public static void Bake(List<XForm[]> frames, TargetRig target)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(target);

        var skeleton = target.Skeleton;
        var count = skeleton.Count;
        for (var f = 0; f < frames.Count; f++)
        {
            if (frames[f].Length != count)
                throw new ArgumentException(
                    $"Frame {f} has {frames[f].Length} bones but the target skeleton has {count}.",
                    nameof(frames));
        }

        var rules = BuildRules(target);

        var world = new XForm[count];
        var computed = new bool[count];
        foreach (var locals in frames)
        {
            Array.Clear(computed);

            // Pass 1: world transforms of every bone with no IkBaked ancestor (the body).
            // A bone is skipped exactly when its parent was skipped or is itself IkBaked,
            // so the skip set is precisely the IK bones and their descendants.
            for (var i = 0; i < count; i++)
            {
                if (target.ClassOf(i) == BoneClass.IkBaked)
                    continue;
                var parent = skeleton[i].ParentIndex;
                if (parent >= 0 && !computed[parent])
                    continue;
                world[i] = parent < 0 ? locals[i] : XForm.Compose(world[parent], locals[i]);
                computed[i] = true;
            }

            // Pass 2: bake IK bones (and propagate worlds through their subtrees). Bones are
            // topologically sorted, so a parent's world is always available by the time its
            // child is processed.
            for (var i = 0; i < count; i++)
            {
                if (computed[i])
                    continue;
                var parent = skeleton[i].ParentIndex;

                if (target.ClassOf(i) == BoneClass.IkBaked)
                {
                    if (rules[i].Kind == RuleKind.KeepRestLocal)
                    {
                        locals[i] = skeleton[i].RestLocal;
                        world[i] = parent < 0 ? locals[i] : XForm.Compose(world[parent], locals[i]);
                    }
                    else
                    {
                        var desired = Desired(in rules[i], world, computed, skeleton[i].Name);
                        locals[i] = parent < 0 ? desired : XForm.ToLocal(world[parent], desired);
                        world[i] = desired;
                    }
                }
                else
                {
                    // Non-IK descendant of an IK bone: local untouched, world propagated.
                    world[i] = parent < 0 ? locals[i] : XForm.Compose(world[parent], locals[i]);
                }
                computed[i] = true;
            }
        }
    }

    /// <summary>
    /// Pins the KeepRestLocal-classified IK helper bones (aim_matrix_*, hold_*,
    /// *_IK_attach and unrecognized IkBaked names) to their REST locals in every frame.
    /// Used on MIRRORED clips (southpaw G8 mirror fix): these bones are aim-space
    /// reference CONSTANTS the model's aim/eye constraint setup was authored against
    /// (measured at rest in every shipped citizen clip); conjugating them like body
    /// geometry feeds the aim chain an alien reference frame and the head/eye region
    /// collapses. The body-derived helpers (root_IK, IK targets, ikrule) are NOT
    /// touched: their mirrored channels stay the exact conjugates of the primary's.
    /// </summary>
    public static void PinRestLocalReferenceBones(List<XForm[]> frames, TargetRig target)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(target);

        var skeleton = target.Skeleton;
        var rules = BuildRules(target);
        foreach (var locals in frames)
        {
            for (var i = 0; i < locals.Length && i < skeleton.Count; i++)
            {
                if (target.ClassOf(i) == BoneClass.IkBaked && rules[i].Kind == RuleKind.KeepRestLocal)
                    locals[i] = skeleton[i].RestLocal;
            }
        }
    }

    // ------------------------------------------------------------------ rules

    private enum RuleKind
    {
        /// <summary>Keep the rest local transform (hold_*, aim_matrix_*, *_IK_attach, unknown).</summary>
        KeepRestLocal,

        /// <summary>World transform copies a body bone's world transform (IK targets, ikrule).</summary>
        FollowBody,

        /// <summary>root_IK: pelvis world position projected to the ground plane, fixed rest rotation.</summary>
        GroundProjectedHips,
    }

    private readonly struct Rule
    {
        public RuleKind Kind { get; init; }
        public int BodyBone { get; init; }
        public int UpAxis { get; init; }
        public float GroundLevel { get; init; }
        public Quaternion FixedWorldRot { get; init; }
    }

    private static Rule[] BuildRules(TargetRig target)
    {
        var skeleton = target.Skeleton;
        var rules = new Rule[skeleton.Count];

        foreach (var i in target.BonesOfClass(BoneClass.IkBaked))
        {
            rules[i] = skeleton[i].Name switch
            {
                "root_IK" => RootIkRule(target, i),
                "foot_L_IK_target" => FollowRule(target, BoneRole.FootL),
                "foot_R_IK_target" => FollowRule(target, BoneRole.FootR),
                "hand_L_IK_target" => FollowRule(target, BoneRole.HandL),
                "hand_R_IK_target" => FollowRule(target, BoneRole.HandR),
                "hand_L_to_R_ikrule" => FollowRule(target, BoneRole.HandL),
                "hand_R_to_L_ikrule" => FollowRule(target, BoneRole.HandR),
                _ => default, // KeepRestLocal
            };
        }

        return rules;
    }

    private static Rule FollowRule(TargetRig target, BoneRole role)
        => target.BoneForRole(role) is { } body
            ? new Rule { Kind = RuleKind.FollowBody, BodyBone = body }
            : default; // body bone missing from the rig: keep rest

    private static Rule RootIkRule(TargetRig target, int rootIk)
    {
        if (target.BoneForRole(BoneRole.Hips) is not { } pelvis)
            return default;

        var skeleton = target.Skeleton;
        // Up axis = dominant component of the rest offset pelvis − root_IK. In every probed
        // rig they share the lateral components exactly and differ only by the hip height.
        var d = Vector3.Abs(skeleton.RestWorld[pelvis].Pos - skeleton.RestWorld[rootIk].Pos);
        var up = d.X >= d.Y ? (d.X >= d.Z ? 0 : 2) : (d.Y >= d.Z ? 1 : 2);

        return new Rule
        {
            Kind = RuleKind.GroundProjectedHips,
            BodyBone = pelvis,
            UpAxis = up,
            GroundLevel = Component(skeleton.RestWorld[rootIk].Pos, up),
            FixedWorldRot = skeleton.RestWorld[rootIk].Rot,
        };
    }

    private static XForm Desired(in Rule rule, XForm[] world, bool[] computed, string name)
    {
        if (!computed[rule.BodyBone])
            throw new InvalidOperationException(
                $"IK bone '{name}' follows a body bone that is itself under an IK bone — unsupported hierarchy.");

        return rule.Kind == RuleKind.FollowBody
            ? world[rule.BodyBone]
            : new XForm(
                WithComponent(world[rule.BodyBone].Pos, rule.UpAxis, rule.GroundLevel),
                rule.FixedWorldRot);
    }

    private static float Component(Vector3 v, int axis)
        => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static Vector3 WithComponent(Vector3 v, int axis, float value)
    {
        switch (axis)
        {
            case 0: v.X = value; break;
            case 1: v.Y = value; break;
            default: v.Z = value; break;
        }
        return v;
    }
}
