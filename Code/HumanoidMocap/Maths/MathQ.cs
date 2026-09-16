#nullable enable annotations

using System;
using System.Numerics;

namespace HumanoidMocap.Maths;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Quaternion algebra helpers used throughout the retargeting pipeline.
/// All angles are radians; quaternions are XYZW (<see cref="System.Numerics.Quaternion"/> native);
/// <c>a * b</c> applies <c>b</c> first (column-vector convention).
/// </summary>
public static class MathQ
{
    private const float NearZeroLengthSq = 1e-12f;

    /// <summary>
    /// Defensively normalizes a quaternion. Returns <see cref="Quaternion.Identity"/> when the
    /// input has near-zero length or non-finite components instead of producing NaNs.
    /// </summary>
    public static Quaternion Normalize(Quaternion q)
    {
        var lengthSq = q.LengthSquared();
        if (!float.IsFinite(lengthSq) || lengthSq < NearZeroLengthSq)
            return Quaternion.Identity;
        return Quaternion.Multiply(q, 1f / MathF.Sqrt(lengthSq));
    }

    /// <summary>
    /// Shortest-arc rotation taking direction <paramref name="a"/> onto direction <paramref name="b"/>
    /// (magnitudes are ignored). For antiparallel inputs a 180° rotation about a stable axis
    /// perpendicular to <paramref name="a"/> is returned. Near-zero inputs yield identity.
    /// </summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        if (a.LengthSquared() < NearZeroLengthSq || b.LengthSquared() < NearZeroLengthSq)
            return Quaternion.Identity;

        var an = Vector3.Normalize(a);
        var bn = Vector3.Normalize(b);
        var dot = Vector3.Dot(an, bn);

        if (dot >= 1f - 1e-8f)
            return Quaternion.Identity;

        if (dot <= -1f + 1e-7f)
        {
            // Antiparallel: any axis perpendicular to a works; pick a stable one.
            return Quaternion.CreateFromAxisAngle(Perpendicular(an), MathF.PI);
        }

        // q = (a x b, 1 + a·b), normalized — the half-angle construction of the shortest arc.
        var cross = Vector3.Cross(an, bn);
        return Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1f + dot));
    }

    /// <summary>
    /// Decomposes a rotation into <paramref name="swing"/> and <paramref name="twist"/> so that
    /// <c>q == swing * twist</c> (twist applied first). The twist is the projection of the
    /// rotation onto <paramref name="twistAxis"/>: its rotation axis is parallel to
    /// <paramref name="twistAxis"/>, while the swing's rotation axis is perpendicular to it.
    /// When the rotation is a pure 180° swing (no component about the axis), twist is identity.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="twistAxis"/> has near-zero length.</exception>
    public static void SwingTwist(Quaternion q, Vector3 twistAxis, out Quaternion swing, out Quaternion twist)
    {
        if (twistAxis.LengthSquared() < NearZeroLengthSq)
            throw new ArgumentException("Twist axis must be non-zero.", nameof(twistAxis));

        var axis = Vector3.Normalize(twistAxis);
        q = Normalize(q);

        // Project the rotation's vector part onto the twist axis.
        var projection = q.X * axis.X + q.Y * axis.Y + q.Z * axis.Z;
        var raw = new Quaternion(axis.X * projection, axis.Y * projection, axis.Z * projection, q.W);

        // Degenerate case (q.W ~ 0 and projection ~ 0): pure 180° swing, no twist.
        twist = Normalize(raw);
        swing = Normalize(q * Quaternion.Conjugate(twist));
    }

    /// <summary>
    /// Geodesic angle between two rotations in radians, in [0, π], invariant under the
    /// q / -q double cover (so <c>AngleBetween(q, -q) == 0</c>).
    /// </summary>
    public static float AngleBetween(Quaternion a, Quaternion b)
    {
        // Use atan2 on the delta rotation rather than acos on the dot product: acos is
        // ill-conditioned near 0°, atan2 is accurate over the whole range.
        var delta = Quaternion.Conjugate(Normalize(a)) * Normalize(b);
        var sinHalf = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
        var cosHalf = MathF.Abs(delta.W); // |W| folds the q / -q double cover.
        return 2f * MathF.Atan2(sinHalf, cosHalf);
    }

    /// <summary>
    /// Angle between two directions in radians, in [0, π] (magnitudes are ignored).
    /// Near-zero inputs yield 0. Uses atan2, which stays accurate near 0 and π where
    /// acos of the dot product loses precision.
    /// </summary>
    public static float AngleBetween(Vector3 a, Vector3 b)
    {
        if (a.LengthSquared() < NearZeroLengthSq || b.LengthSquared() < NearZeroLengthSq)
            return 0f;
        return MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));
    }

    /// <summary>
    /// Builds an orthonormal right-handed basis rotation from a forward and an up hint.
    /// </summary>
    /// <remarks>
    /// Convention: returns the rotation <c>R</c> such that
    /// <list type="bullet">
    /// <item><c>rotate(R, +Y) == normalize(forward)</c> (primary axis),</item>
    /// <item><c>rotate(R, +Z) == </c> the Gram-Schmidt orthonormalization of <paramref name="up"/>
    /// against forward (secondary axis),</item>
    /// <item><c>rotate(R, +X) == forwardN x upOrtho</c> (right-handed completion).</item>
    /// </list>
    /// Degenerate inputs are handled defensively: a near-zero forward yields identity; an up hint
    /// (near-)parallel to forward falls back to world +Z, or world +X when forward itself is
    /// (near-)parallel to +Z.
    /// </remarks>
    public static Quaternion BasisFromForwardUp(Vector3 forward, Vector3 up)
    {
        if (forward.LengthSquared() < NearZeroLengthSq)
            return Quaternion.Identity;

        var y = Vector3.Normalize(forward);

        var z = up - y * Vector3.Dot(up, y);
        if (z.LengthSquared() < 1e-8f)
        {
            // Up hint unusable: pick a stable fallback and orthonormalize it.
            var fallback = MathF.Abs(Vector3.Dot(y, Vector3.UnitZ)) > 0.99f ? Vector3.UnitX : Vector3.UnitZ;
            z = fallback - y * Vector3.Dot(fallback, y);
        }
        z = Vector3.Normalize(z);

        var x = Vector3.Cross(y, z);

        // System.Numerics matrices act on row vectors, so the rows are the images of the
        // unit axes under the rotation: row1 = R*X, row2 = R*Y, row3 = R*Z.
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);

        return Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>Stable unit vector perpendicular to <paramref name="unit"/> (assumed normalized).
    /// Internal so geometry passes (e.g. <c>TwoBoneIk</c>) reuse one implementation.</summary>
    internal static Vector3 Perpendicular(Vector3 unit)
    {
        var p = Vector3.Cross(unit, Vector3.UnitX);
        if (p.LengthSquared() < 1e-6f)
            p = Vector3.Cross(unit, Vector3.UnitY);
        return Vector3.Normalize(p);
    }
}
