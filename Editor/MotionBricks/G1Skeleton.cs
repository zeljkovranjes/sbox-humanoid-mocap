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
/// The MotionBricks G1 skeleton (34 joints: the root, 29 robot bodies and four virtual
/// hand/toe endpoints) as stored in <c>support.gguf</c>. Coordinates are right-handed, Y up,
/// Z forward, in metres. Every joint's rest rotation is identity.
/// </summary>
public sealed class G1Skeleton
{
    public const int JointCount = 34;

    /// <summary>Virtual endpoints (no physical body); the model reads identity rotations for them.</summary>
    public static readonly int[] VirtualJoints = { 7, 14, 25, 33 };

    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<int> Parents { get; }

    /// <summary>Rest (neutral) joint positions in model space.</summary>
    public IReadOnlyList<Vector3> Neutral { get; }

    public G1Skeleton( IReadOnlyList<string> names, IReadOnlyList<int> parents, IReadOnlyList<Vector3> neutral )
    {
        if ( names.Count != JointCount || parents.Count != JointCount || neutral.Count != JointCount )
            throw new InvalidDataException( "The MotionBricks skeleton must have 34 joints." );
        for ( var i = 0; i < JointCount; i++ )
            if ( (i == 0 && parents[i] != -1) || (i > 0 && (parents[i] < 0 || parents[i] >= i)) )
                throw new InvalidDataException( "The MotionBricks skeleton has an invalid parent order." );
        Names = names;
        Parents = parents;
        Neutral = neutral;
    }

    public int IndexOf( string name ) => Names.ToList().IndexOf( name );

    /// <summary>Rest offset of a joint from its parent.</summary>
    public Vector3 RestOffset( int joint ) => Parents[joint] < 0 ? Neutral[joint] : Neutral[joint] - Neutral[Parents[joint]];

    /// <summary>Forward kinematics of one frame: global rotations and positions.</summary>
    public void Forward( Vector3 root, ReadOnlySpan<Quaternion> locals, Span<Quaternion> globalRotations, Span<Vector3> globalPositions )
    {
        for ( var j = 0; j < JointCount; j++ )
        {
            var parent = Parents[j];
            if ( parent < 0 )
            {
                globalRotations[j] = Quaternion.Normalize( locals[j] );
                globalPositions[j] = root;
            }
            else
            {
                globalRotations[j] = Quaternion.Normalize( globalRotations[parent] * locals[j] );
                globalPositions[j] = globalPositions[parent] + Vector3.Transform( RestOffset( j ), globalRotations[parent] );
            }
        }
    }
}
