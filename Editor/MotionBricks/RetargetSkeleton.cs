#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

using System.Collections;
using System.Numerics;
using System.Reflection;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>A skeleton created inside the Humanoid Retargeter (opaque handle).</summary>
public sealed class RetargetSkeleton
{
    internal RetargetSkeleton( object handle, int count )
    {
        Handle = handle;
        Count = count;
    }

    internal object Handle { get; }
    public int Count { get; }
}
