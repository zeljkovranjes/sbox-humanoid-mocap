#nullable enable annotations

using System;
using System.Numerics;

namespace HumanoidMocap.Maths;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// A rigid transform: rotation followed by translation (no scale or shear).
/// </summary>
/// <remarks>
/// Project conventions (fixed for the whole library):
/// <list type="bullet">
/// <item>Positions are centimeters.</item>
/// <item>Quaternions are XYZW unit quaternions (<see cref="System.Numerics.Quaternion"/> native layout).</item>
/// <item>Column-vector convention: a local-space point maps to outer space as
/// <c>p' = rotate(Rot, p) + Pos</c>, and <c>a * b</c> on quaternions applies <c>b</c> first.</item>
/// </list>
/// </remarks>
public struct XForm : IEquatable<XForm>
{
    /// <summary>Translation component, in centimeters.</summary>
    public Vector3 Pos;

    /// <summary>Rotation component, XYZW unit quaternion.</summary>
    public Quaternion Rot;

    /// <summary>Creates a transform from a translation and a rotation.</summary>
    public XForm(Vector3 pos, Quaternion rot)
    {
        Pos = pos;
        Rot = rot;
    }

    /// <summary>The identity transform (zero translation, identity rotation).</summary>
    public static XForm Identity => new(Vector3.Zero, Quaternion.Identity);

    /// <summary>
    /// Composes a parent transform with a child-local transform, producing the child's
    /// transform in the parent's outer space (world = parent ∘ local):
    /// <c>pos = parent.Pos + rotate(parent.Rot, local.Pos)</c>, <c>rot = parent.Rot * local.Rot</c>.
    /// The resulting rotation is re-normalized to suppress floating-point drift.
    /// </summary>
    public static XForm Compose(in XForm parent, in XForm local)
        => new(
            parent.Pos + Vector3.Transform(local.Pos, parent.Rot),
            MathQ.Normalize(parent.Rot * local.Rot));

    /// <summary>
    /// Returns the inverse transform, such that <c>Compose(x, x.Inverse())</c> and
    /// <c>Compose(x.Inverse(), x)</c> are both identity.
    /// </summary>
    public readonly XForm Inverse()
    {
        var invRot = Quaternion.Conjugate(MathQ.Normalize(Rot));
        return new XForm(-Vector3.Transform(Pos, invRot), invRot);
    }

    /// <summary>
    /// Re-expresses a world transform relative to a parent world transform; the inverse of
    /// <see cref="Compose"/>: <c>ToLocal(p, Compose(p, l)) == l</c>.
    /// </summary>
    public static XForm ToLocal(in XForm parentWorld, in XForm world)
        => Compose(parentWorld.Inverse(), world);

    /// <summary>Transforms a point from this transform's local space to its outer space.</summary>
    public readonly Vector3 TransformPoint(Vector3 point) => Pos + Vector3.Transform(point, Rot);

    /// <summary>Rotates a direction vector by this transform's rotation (translation ignored).</summary>
    public readonly Vector3 TransformVector(Vector3 vector) => Vector3.Transform(vector, Rot);

    /// <inheritdoc />
    public readonly bool Equals(XForm other) => Pos.Equals(other.Pos) && Rot.Equals(other.Rot);

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is XForm other && Equals(other);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(Pos, Rot);

    /// <summary>Componentwise equality (no tolerance).</summary>
    public static bool operator ==(XForm left, XForm right) => left.Equals(right);

    /// <summary>Componentwise inequality (no tolerance).</summary>
    public static bool operator !=(XForm left, XForm right) => !left.Equals(right);

    /// <inheritdoc />
    public override readonly string ToString() => $"XForm(Pos={Pos}, Rot={Rot})";
}
