#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

namespace HumanoidMocap.Core.Target;

/// <summary>
/// Thrown by <see cref="VmdlAugmenter.Augment"/> when an animation name collides with an
/// existing AnimationList node that is not an AnimFile (replacing it would destroy user data).
/// </summary>
public sealed class VmdlAugmentException : Exception
{
    /// <summary>One message per colliding entry, naming the sequence and the existing
    /// node's class.</summary>
    public IReadOnlyList<string> Collisions { get; }

    /// <summary>Creates the exception from the collected collision messages.</summary>
    public VmdlAugmentException(IReadOnlyList<string> collisions)
        : base("Cannot augment vmdl, name collisions with non-AnimFile nodes: "
            + string.Join("; ", collisions))
    {
        Collisions = collisions;
    }
}
