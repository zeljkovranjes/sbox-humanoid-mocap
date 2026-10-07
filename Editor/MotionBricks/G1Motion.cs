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

/// <summary>A clip on the G1 skeleton: per frame a root translation and 34 local rotations.</summary>
public sealed class G1Motion
{
    public const float Fps = 30f;

    public Vector3[] Root { get; }
    public Quaternion[][] Local { get; }

    public int FrameCount => Root.Length;

    public G1Motion( int frames )
    {
        Root = new Vector3[frames];
        Local = new Quaternion[frames][];
        for ( var f = 0; f < frames; f++ )
        {
            Local[f] = new Quaternion[G1Skeleton.JointCount];
            Array.Fill( Local[f], Quaternion.Identity );
        }
    }

    public G1Motion Slice( int start, int count )
    {
        var result = new G1Motion( count );
        for ( var f = 0; f < count; f++ )
        {
            result.Root[f] = Root[start + f];
            Local[start + f].CopyTo( result.Local[f], 0 );
        }
        return result;
    }
}
