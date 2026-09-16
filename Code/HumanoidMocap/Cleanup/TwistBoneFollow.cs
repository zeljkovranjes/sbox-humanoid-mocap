#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;

namespace HumanoidMocap.Cleanup;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Drives unmapped limb deform bones from the joints whose motion they distribute.
/// Auto-rigged exports (Auto-Rig Pro <c>forearm_twist.l</c>, AdvancedSkeleton
/// <c>ElbowPart1_L</c>, Biped <c>Bip01 L ForeTwist</c>) spread limb roll across helper
/// bones the game constrains at runtime; a baked retarget that leaves them at rest
/// candy-wraps the skin — the reported wrist "spike fans" when the hand pronates.
/// </summary>
/// <remarks>
/// Detection is geometric, name-free: an UNMAPPED bone whose parent is a mapped limb
/// bone (upper/lower arm or leg) and whose rest position lies ON the segment from that
/// parent to the parent's mapped chain child (within 15° of the axis, fraction
/// 0.05..1.1 along it). Each detected twist follows the chain child's per-frame local
/// ROLL — the twist component of its rotation delta about the limb axis — scaled by the
/// twist's fractional position (a bone at 60% of the forearm takes 60% of the hand's
/// roll; ARP's proximal <c>arm_twist</c> at fraction ~0 correctly takes ~none). Pure
/// swing carries no twist component, so elbows/knees bending never move these bones.
/// Serial deform bones between two mapped limb joints are handled separately: their
/// world-space motion delta is interpolated between the endpoints while both mapped
/// endpoint transforms remain unchanged. This covers rigs that split each bend/twist
/// section into two weighted bones without relying on exporter-specific names. An
/// unmapped sibling at the mapped joint's same pivot follows its complete rotation;
/// this covers dual control/deform rigs where a mechanism forearm/femur drives the next
/// joint while coincident anatomical bones carry the skin.
/// </remarks>
public static class TwistBoneFollow
{
    private static readonly (BoneRole Parent, BoneRole Child)[] Segments =
    {
        (BoneRole.ClavicleL, BoneRole.UpperArmL),
        (BoneRole.UpperArmL, BoneRole.LowerArmL), (BoneRole.LowerArmL, BoneRole.HandL),
        (BoneRole.ClavicleR, BoneRole.UpperArmR),
        (BoneRole.UpperArmR, BoneRole.LowerArmR), (BoneRole.LowerArmR, BoneRole.HandR),
        (BoneRole.Hips, BoneRole.UpperLegL),
        (BoneRole.UpperLegL, BoneRole.LowerLegL), (BoneRole.LowerLegL, BoneRole.FootL),
        (BoneRole.Hips, BoneRole.UpperLegR),
        (BoneRole.UpperLegR, BoneRole.LowerLegR), (BoneRole.LowerLegR, BoneRole.FootR),
    };

    private readonly record struct InlineBone(int Bone, int Parent, int Child, float Fraction);

    private readonly record struct FullFollower(int Bone, int Driver);

    /// <summary>Applies the pass in place; returns how many limb helpers were driven.</summary>
    public static int Apply(
        IReadOnlyList<XForm[]> frames, TargetRig rig, IReadOnlySet<int>? excluded)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(rig);
        var skeleton = rig.Skeleton;

        var twists = new List<(int Bone, int Driver, Vector3 Axis, float Fraction)>();
        var fullFollowers = new List<FullFollower>();
        var fullFollowerBones = new HashSet<int>();
        foreach (var (parentRole, childRole) in Segments)
        {
            if (rig.BoneForRole(parentRole) is not { } parent
                || rig.BoneForRole(childRole) is not { } child
                || skeleton[child].ParentIndex != parent)
                continue;

            // Limb axis and length in the PARENT's local space (the chain child's rest
            // local translation).
            var axis = skeleton[child].RestLocal.Pos;
            var length = axis.Length();
            if (length < 1e-3f)
                continue;
            axis /= length;

            for (var i = 0; i < skeleton.Count; i++)
            {
                if (i == child || skeleton[i].ParentIndex != parent
                    || rig.RoleOf(i) is not null || excluded?.Contains(i) == true)
                    continue;
                var pos = skeleton[i].RestLocal.Pos;
                // Blender control/deform exports commonly put a mechanism joint and one
                // or more skinned anatomical joints at the same pivot (MCH_forearm beside
                // radius/ulna, MCH_femur beside femur). The mapped mechanism drives the
                // next joint, but its deform siblings need the complete bend and roll;
                // treating them as ordinary twist bones copies roll only and leaves the
                // mesh behind while the hand/leg moves away.
                if ((pos - skeleton[child].RestLocal.Pos).Length()
                    <= MathF.Max(0.01f, length * 0.01f))
                {
                    if (fullFollowerBones.Add(i))
                        fullFollowers.Add(new FullFollower(i, child));
                    continue;
                }
                var along = Vector3.Dot(pos, axis);
                var fraction = along / length;
                if (fraction is < 0.05f or > 1.1f)
                    continue;
                var offAxis = (pos - axis * along).Length();
                if (offAxis > MathF.Tan(15f * MathF.PI / 180f) * MathF.Max(along, 1e-3f))
                    continue;
                twists.Add((i, child, axis, Math.Clamp(fraction, 0f, 1f)));
            }
        }
        var inline = FindInlineBones(rig, excluded);
        if (twists.Count == 0 && inline.Count == 0 && fullFollowers.Count == 0)
            return 0;

        foreach (var frame in frames)
        {
            foreach (var follower in fullFollowers)
            {
                // Both bones share a parent, so the driver's local-space rotation delta
                // can be applied directly while retaining the deform bone's bind offset.
                var delta = MathQ.Normalize(frame[follower.Driver].Rot
                    * Quaternion.Conjugate(skeleton[follower.Driver].RestLocal.Rot));
                frame[follower.Bone] = new XForm(
                    frame[follower.Bone].Pos,
                    MathQ.Normalize(delta * skeleton[follower.Bone].RestLocal.Rot));
            }
            foreach (var (bone, driver, axis, fraction) in twists)
            {
                // The driver's rotation delta from rest, in the shared parent's space,
                // forced to the SHORTEST arc (W >= 0) so the twist angle below is
                // continuous in (-180°, 180°) and never flips representation.
                var delta = MathQ.Normalize(
                    frame[driver].Rot * Quaternion.Conjugate(skeleton[driver].RestLocal.Rot));
                if (delta.W < 0f)
                    delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
                // Twist component about the limb axis (swing-twist decomposition).
                var proj = Vector3.Dot(new Vector3(delta.X, delta.Y, delta.Z), axis);
                // Ill-conditioned when the delta approaches a pure 180° SWING (both the
                // axis projection and W collapse toward 0): the decomposition then
                // amplifies noise into huge fake rolls — measured on a throw clip, the
                // kicking foot injected ±99° into the calf twist bone and the calf skin
                // flipped upward ("the leg is up"). Keep rest instead.
                var conditioning = MathF.Sqrt(proj * proj + delta.W * delta.W);
                if (conditioning < 0.2f)
                    continue;
                var angle = 2f * MathF.Atan2(proj, delta.W);
                var scaled = Quaternion.CreateFromAxisAngle(axis, angle * fraction);
                frame[bone] = new XForm(
                    frame[bone].Pos, MathQ.Normalize(scaled * skeleton[bone].RestLocal.Rot));
            }

            if (inline.Count > 0)
                FollowInlineBones(frame, skeleton, inline);
        }
        return twists.Count + inline.Count + fullFollowers.Count;
    }

    private static List<InlineBone> FindInlineBones(
        TargetRig rig, IReadOnlySet<int>? excluded)
    {
        var skeleton = rig.Skeleton;
        var result = new List<InlineBone>();
        var seen = new HashSet<int>();
        foreach (var (parentRole, childRole) in Segments)
        {
            if (rig.BoneForRole(parentRole) is not { } parent
                || rig.BoneForRole(childRole) is not { } child)
                continue;

            var path = new List<int>();
            for (var bone = skeleton[child].ParentIndex;
                 bone >= 0 && bone != parent;
                 bone = skeleton[bone].ParentIndex)
                path.Add(bone);
            if (path.Count == 0
                || skeleton[path[^1]].ParentIndex != parent
                || path.Any(bone => rig.RoleOf(bone) is not null
                    || excluded?.Contains(bone) == true))
                continue;
            path.Reverse();

            var length = 0f;
            var previous = parent;
            foreach (var bone in path.Append(child))
            {
                length += (skeleton.RestWorld[bone].Pos
                    - skeleton.RestWorld[previous].Pos).Length();
                previous = bone;
            }
            if (length < 1e-3f)
                continue;

            var along = 0f;
            previous = parent;
            foreach (var bone in path)
            {
                along += (skeleton.RestWorld[bone].Pos
                    - skeleton.RestWorld[previous].Pos).Length();
                if (seen.Add(bone))
                    result.Add(new InlineBone(bone, parent, child, along / length));
                previous = bone;
            }
        }
        return result;
    }

    private static void FollowInlineBones(
        XForm[] frame, Skeleton.Skeleton skeleton, IReadOnlyList<InlineBone> inline)
    {
        var world = new Skeleton.Pose(frame).ToWorld(skeleton);
        var desired = world.ToArray();
        var pathBones = new HashSet<int>();

        foreach (var group in inline.GroupBy(entry => (entry.Parent, entry.Child)))
        {
            var parent = group.Key.Parent;
            var child = group.Key.Child;
            var parentDelta = MathQ.Normalize(world[parent].Rot
                * Quaternion.Conjugate(skeleton.RestWorld[parent].Rot));
            var childDelta = MathQ.Normalize(world[child].Rot
                * Quaternion.Conjugate(skeleton.RestWorld[child].Rot));
            if (Quaternion.Dot(parentDelta, childDelta) < 0f)
                childDelta = new Quaternion(
                    -childDelta.X, -childDelta.Y, -childDelta.Z, -childDelta.W);

            foreach (var entry in group)
            {
                var delta = MathQ.Normalize(Quaternion.Slerp(
                    parentDelta, childDelta, entry.Fraction));
                desired[entry.Bone] = new XForm(
                    world[entry.Bone].Pos,
                    MathQ.Normalize(delta * skeleton.RestWorld[entry.Bone].Rot));
                pathBones.Add(entry.Bone);
            }
            // Compensate the mapped endpoint locally so its already-solved world transform
            // remains exact after its intermediary parent starts following the motion.
            pathBones.Add(child);
        }

        foreach (var bone in pathBones.OrderBy(index => index))
        {
            var parent = skeleton[bone].ParentIndex;
            frame[bone] = parent < 0
                ? desired[bone]
                : XForm.ToLocal(desired[parent], desired[bone]);
        }
    }
}
