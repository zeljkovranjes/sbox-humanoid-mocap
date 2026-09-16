#nullable enable annotations

using System;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Per-node FBX local-transform evaluation: the static pivot/pre-rotation data of one Model
/// plus the full FBX transform formula.
/// </summary>
/// <remarks>
/// The FBX SDK formula (column-vector convention, right-multiply children):
/// <code>
/// World = ParentWorld · T · Roff · Rp · Rpre · R · Rpost⁻¹ · Rp⁻¹ · Soff · Sp · S · Sp⁻¹
/// </code>
/// System.Numerics matrices are row-vector (<c>p' = p·M</c>), so the local matrix is composed
/// in reverse order with row-form factors:
/// <code>
/// Local = T(-Sp)·S·T(Sp)·T(Soff)·T(-Rp)·R(Rpre·R·Rpost⁻¹)·T(Rp)·T(Roff)·T(t)
/// World = Local · ParentWorld
/// </code>
/// <para><b>RotationOrder</b> (0=XYZ, 1=XZY, 2=YZX, 3=YXZ, 4=ZXY, 5=ZYX): the letters give the
/// application order, first letter applied first — eEulerXYZ composes <c>R = Rz·Ry·Rx</c> in
/// column convention (verified against Blender-imported ground truth of Mixamo and citizen
/// rigs). Pre/Post rotations always use XYZ order and are only applied when
/// <c>RotationActive</c> is set (matching Blender's importer; defaults to active when the
/// property is absent everywhere).</para>
/// <para><b>InheritType</b> (0=RrSs, 1=RSrs, 2=Rrs): scale is dropped to rigid transforms by
/// this importer — world matrices are evaluated with full scale, then decomposed to pos+rot
/// with scale folded into child translations — so types 0 and 1 are handled identically.
/// Files that use non-uniform scale meaningfully with InheritType 2 (no parent scale
/// propagation) are evaluated as type 0; this is a documented limitation.</para>
/// </remarks>
public sealed class FbxTransform
{
    /// <summary>Static "Lcl Translation" default (native units).</summary>
    public Vector3 LclTranslation { get; init; }

    /// <summary>Static "Lcl Rotation" default, euler degrees in <see cref="RotationOrder"/>.</summary>
    public Vector3 LclRotationDeg { get; init; }

    /// <summary>Static "Lcl Scaling" default.</summary>
    public Vector3 LclScaling { get; init; } = Vector3.One;

    /// <summary>PreRotation as a quaternion (identity when inactive or absent).</summary>
    public Quaternion PreRotation { get; init; } = Quaternion.Identity;

    /// <summary>PostRotation as a quaternion (identity when inactive or absent).</summary>
    public Quaternion PostRotation { get; init; } = Quaternion.Identity;

    /// <summary>RotationOffset (Roff).</summary>
    public Vector3 RotationOffset { get; init; }

    /// <summary>RotationPivot (Rp).</summary>
    public Vector3 RotationPivot { get; init; }

    /// <summary>ScalingOffset (Soff).</summary>
    public Vector3 ScalingOffset { get; init; }

    /// <summary>ScalingPivot (Sp).</summary>
    public Vector3 ScalingPivot { get; init; }

    /// <summary>Euler application order code, 0=XYZ … 5=ZYX (see class remarks).</summary>
    public int RotationOrder { get; init; }

    /// <summary>InheritType 0=RrSs, 1=RSrs, 2=Rrs (see class remarks for handling).</summary>
    public int InheritType { get; init; }

    /// <summary>Reads the static transform data of a Model from its (template-backed) properties.</summary>
    public static FbxTransform FromModel(FbxScene scene, FbxObject model)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(model);

        // Pre/Post rotation participate only when RotationActive is set (Blender-compatible).
        // When the property is absent on both the object and its template, default to active —
        // exporters that strip RotationActive but keep PreRotation expect it applied.
        bool rotationActive = scene.FindProperty(model, "RotationActive") is { } ra
            ? ra.GetInt() != 0
            : true;

        var pre = scene.GetVector3(model, "PreRotation", Vector3.Zero);
        var post = scene.GetVector3(model, "PostRotation", Vector3.Zero);

        return new FbxTransform
        {
            LclTranslation = scene.GetVector3(model, "Lcl Translation", Vector3.Zero),
            LclRotationDeg = scene.GetVector3(model, "Lcl Rotation", Vector3.Zero),
            LclScaling = scene.GetVector3(model, "Lcl Scaling", Vector3.One),
            PreRotation = rotationActive ? EulerDegreesToQuaternion(pre, 0) : Quaternion.Identity,
            PostRotation = rotationActive ? EulerDegreesToQuaternion(post, 0) : Quaternion.Identity,
            RotationOffset = scene.GetVector3(model, "RotationOffset", Vector3.Zero),
            RotationPivot = scene.GetVector3(model, "RotationPivot", Vector3.Zero),
            ScalingOffset = scene.GetVector3(model, "ScalingOffset", Vector3.Zero),
            ScalingPivot = scene.GetVector3(model, "ScalingPivot", Vector3.Zero),
            RotationOrder = scene.GetInt(model, "RotationOrder", 0),
            InheritType = scene.GetInt(model, "InheritType", 0),
        };
    }

    /// <summary>
    /// Local matrix (row-vector layout) for given TRS values; pass the Lcl defaults for the
    /// rest pose or per-frame sampled values for animation.
    /// </summary>
    public Matrix4x4 LocalMatrix(Vector3 translation, Vector3 rotationDeg, Vector3 scaling)
    {
        // Column chain R-part: Rpre · R · Rpost⁻¹  →  our quaternion convention (a*b applies
        // b first) composes the same way.
        var rotation = MathQ.Normalize(
            PreRotation *
            EulerDegreesToQuaternion(rotationDeg, RotationOrder) *
            Quaternion.Conjugate(PostRotation));

        // Row-vector composition, first-applied factor leftmost (see class remarks).
        var m = Matrix4x4.CreateTranslation(-ScalingPivot);
        m *= Matrix4x4.CreateScale(scaling);
        m *= Matrix4x4.CreateTranslation(ScalingPivot + ScalingOffset - RotationPivot);
        m *= Matrix4x4.CreateFromQuaternion(rotation);
        m *= Matrix4x4.CreateTranslation(RotationPivot + RotationOffset + translation);
        return m;
    }

    /// <summary>Local matrix from the static Lcl defaults (rest evaluation, no animation).</summary>
    public Matrix4x4 LocalMatrixDefault() => LocalMatrix(LclTranslation, LclRotationDeg, LclScaling);

    /// <summary>
    /// Converts FBX euler angles (degrees) to a quaternion for the given RotationOrder code.
    /// The order letters list the application sequence (XYZ = X first, then Y, then Z).
    /// </summary>
    public static Quaternion EulerDegreesToQuaternion(Vector3 degrees, int order)
    {
        const float degToRad = MathF.PI / 180f;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * degToRad);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * degToRad);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * degToRad);

        // q = third * second * first (our convention applies the right factor first).
        return order switch
        {
            0 => qz * qy * qx, // XYZ
            1 => qy * qz * qx, // XZY
            2 => qx * qz * qy, // YZX
            3 => qz * qx * qy, // YXZ
            4 => qy * qx * qz, // ZXY
            5 => qx * qy * qz, // ZYX
            6 => qz * qy * qx, // eSphericXYZ — treated as XYZ
            _ => throw new FormatException($"Unsupported FBX RotationOrder {order}."),
        };
    }

    /// <summary>
    /// Extracts the rigid part of a (possibly scaled) world matrix: translation taken directly,
    /// rotation from the orthonormalized basis. Scale is thereby folded into child translations
    /// when parent-relative transforms are derived from rigid worlds. Mirrored (negative
    /// determinant) bases fall back to the dominant proper rotation (best effort — mirrored
    /// skeletons are not meaningfully supported).
    /// </summary>
    public static XForm ToRigid(in Matrix4x4 world)
    {
        var pos = world.Translation;

        var x = new Vector3(world.M11, world.M12, world.M13);
        var y = new Vector3(world.M21, world.M22, world.M23);
        var z = new Vector3(world.M31, world.M32, world.M33);

        x = SafeNormalize(x, Vector3.UnitX);
        y = SafeNormalize(y, Vector3.UnitY);
        z = SafeNormalize(z, Vector3.UnitZ);

        // Mirrored basis: flip the third axis to restore a proper rotation.
        if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f)
            z = -z;

        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);

        return new XForm(pos, MathQ.Normalize(Quaternion.CreateFromRotationMatrix(m)));
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float lsq = v.LengthSquared();
        if (!float.IsFinite(lsq) || lsq < 1e-20f)
            return fallback;
        return v / MathF.Sqrt(lsq);
    }
}
