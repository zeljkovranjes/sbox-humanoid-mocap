#nullable enable annotations

using System;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Skeleton;

/// <summary>
/// A single pose of a skeleton: one parent-relative local transform per bone,
/// indexed exactly like the skeleton's bone list.
/// </summary>
public sealed class Pose
{
    /// <summary>Parent-relative local transforms, one per bone (skeleton bone order).</summary>
    public XForm[] Locals { get; }

    /// <summary>Wraps an existing local-transform array (not copied).</summary>
    public Pose(XForm[] locals)
    {
        Locals = locals ?? throw new ArgumentNullException(nameof(locals));
    }

    /// <summary>Creates a pose initialized to the skeleton's rest local transforms.</summary>
    public static Pose Rest(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var locals = new XForm[skeleton.Count];
        for (var i = 0; i < locals.Length; i++)
            locals[i] = skeleton[i].RestLocal;
        return new Pose(locals);
    }

    /// <summary>
    /// Forward kinematics: composes local transforms down the hierarchy and returns
    /// one world transform per bone.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the pose length does not match the skeleton.</exception>
    public XForm[] ToWorld(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        if (Locals.Length != skeleton.Count)
            throw new ArgumentException(
                $"Pose has {Locals.Length} bones but skeleton has {skeleton.Count}.", nameof(skeleton));

        var world = new XForm[Locals.Length];
        for (var i = 0; i < Locals.Length; i++)
        {
            var parent = skeleton[i].ParentIndex;
            world[i] = parent < 0 ? Locals[i] : XForm.Compose(world[parent], Locals[i]);
        }
        return world;
    }
}
