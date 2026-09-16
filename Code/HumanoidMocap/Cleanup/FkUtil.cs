#nullable enable annotations

using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Cleanup;

/// <summary>
/// Allocation-free forward-kinematics helpers shared by the cleanup passes.
/// Both helpers perform exactly the same sequence of <see cref="XForm.Compose"/>
/// operations as <see cref="HumanoidMocap.Skeleton.Pose.ToWorld"/>, so their
/// results are bit-identical to a full FK pass — they only avoid the per-call
/// allocations (scratch buffer reuse, single-bone ancestor walks).
/// </summary>
internal static class FkUtil
{
    /// <summary>
    /// Full-skeleton FK from <paramref name="locals"/> into the caller-owned
    /// <paramref name="world"/> scratch buffer (one world transform per bone,
    /// skeleton bone order). The buffer must be at least <c>locals.Length</c> long.
    /// </summary>
    public static void ToWorld(XForm[] locals, SkeletonModel skeleton, XForm[] world)
    {
        for (int i = 0; i < locals.Length; i++)
        {
            var parent = skeleton[i].ParentIndex;
            world[i] = parent < 0 ? locals[i] : XForm.Compose(world[parent], locals[i]);
        }
    }

    /// <summary>
    /// World transform of a single bone via an ancestor-chain walk: composes the same
    /// nested chain a full FK pass would, restricted to the bone's ancestors, so the
    /// result is bit-identical to <c>ToWorld(...)[bone]</c> without touching any other bone.
    /// </summary>
    public static XForm BoneWorld(XForm[] locals, SkeletonModel skeleton, int bone)
    {
        var parent = skeleton[bone].ParentIndex;
        return parent < 0
            ? locals[bone]
            : XForm.Compose(BoneWorld(locals, skeleton, parent), locals[bone]);
    }
}
