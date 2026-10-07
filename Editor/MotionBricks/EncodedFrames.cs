#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Boundary features of four consecutive frames, unnormalized:
/// global root <c>[4, 5]</c> (x, y, z, cos heading, sin heading), local root <c>[4, 4]</c>
/// (heading velocity, x velocity, z velocity, height) and poses <c>[4, 303]</c>
/// (33 root-relative joint positions, 34 global 6D rotations).
/// </summary>
public sealed class EncodedFrames
{
    public readonly float[] GlobalRoot = new float[4 * MotionRepresentation.GlobalRootWidth];
    public readonly float[] LocalRoot = new float[4 * MotionRepresentation.LocalRootWidth];
    public readonly float[] Poses = new float[4 * MotionRepresentation.ExternalPoseWidth];
}
