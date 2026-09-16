#nullable enable annotations

using HumanoidMocap.Mapping;
using HumanoidMocap.Target;

namespace HumanoidMocap.Solve;

/// <summary>
/// Bridges a <see cref="TargetRig"/>'s role annotations into the <see cref="MappingResult"/>
/// shape shared with source mappings, so target-side machinery (rest normalization, canonical
/// frames) can run on the exact same code paths as the source side.
/// </summary>
public static class TargetRigMappingExtensions
{
    /// <summary>Role → target bone index mapping of the rig's annotated animated bones.</summary>
    public static MappingResult ToMappingResult(this TargetRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        var map = new MappingResult(rig.Name, MappingSource.Preset) { Confidence = 1f };
        for (var i = 0; i < rig.Skeleton.Count; i++)
        {
            if (rig.RoleOf(i) is BoneRole role)
                map.RoleToBone[role] = i;
        }
        return map;
    }
}
