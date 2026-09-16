#nullable enable annotations

using System;
using System.Numerics;
using HumanoidMocap.Maths;

using NVector3 = System.Numerics.Vector3;

namespace HumanoidMocap.Target;

/// <summary>
/// Converts compiled-model bind locals (engine Z-up inches) back into the source space
/// declared by an embedded-FBX target.
/// </summary>
public static class CompiledRigSourceSpace
{
    /// <summary>
    /// Converts one parent-local transform. The unit conversion applies to every bone;
    /// the inverse Z-up-to-Y-up basis applies only to roots of Y-up source rigs. Z-up FBX
    /// targets (including Unreal exports) remain Z-up and therefore are never rotated twice.
    /// </summary>
    public static XForm FromEngineLocal(XForm local, bool isRoot, TargetUpAxis sourceUpAxis)
    {
        var position = local.Pos / RetargetTargetSpec.SboxSourceScale;
        var rotation = local.Rot;
        if (isRoot && sourceUpAxis == TargetUpAxis.YUpCm)
        {
            var inverseUp = Quaternion.CreateFromAxisAngle(NVector3.UnitX, -MathF.PI * 0.5f);
            position = NVector3.Transform(position, inverseUp);
            rotation = Quaternion.Normalize(inverseUp * rotation);
        }

        return new XForm(position, rotation);
    }
}
