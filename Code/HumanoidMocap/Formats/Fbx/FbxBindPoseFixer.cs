#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Repairs FBX files that were exported MID-POSE: their node transforms (Lcl
/// Translation/Rotation) hold an animation snapshot while the true skeleton bind lives in
/// the file's Pose/BindPose section. Engines that build the skeleton from node transforms
/// (s&amp;box does; the FBX SDK's own samples do) then import a posed "bind" — the skin
/// stays self-consistent so the model LOOKS fine at rest, but every anatomical assumption
/// about the skeleton (leg chains point down, hands mirror) is silently wrong and
/// retargeted motion comes out mangled on exactly the posed bones. Found in the wild on
/// Auto-Rig Pro exports whose IK'd hands/feet were left posed (one leg at hip height).
/// </summary>
public static class FbxBindPoseFixer
{
    /// <summary>Bones whose node transform is further than this (native units, as a
    /// fraction of skeleton height) from their BindPose matrix count as posed.</summary>
    private const float PositionToleranceOfHeight = 0.01f;

    /// <summary>Rotation disagreement (degrees) that counts as posed.</summary>
    private const float RotationToleranceDeg = 2.0f;

    /// <summary>
    /// Detects the mid-pose condition and rewrites node transforms to the BindPose. Returns
    /// null when the file needs no repair (no BindPose section, or node transforms already
    /// agree with it); otherwise the repaired file bytes.
    /// <paramref name="report"/> always describes what was found.
    /// </summary>
    public static byte[]? TryFix(byte[] fbx, out string report)
    {
        ArgumentNullException.ThrowIfNull(fbx);

        FbxNode root;
        FbxScene scene;
        try
        {
            root = FbxTokenizer.Parse(fbx);
            scene = FbxScene.Build(root);
        }
        catch (FormatException e)
        {
            report = $"not parseable ({e.Message})";
            return null;
        }

        if (scene.BindPose.Count == 0)
        {
            report = "no BindPose section";
            return null;
        }

        // Evaluate the ORIGINAL node-transform FK, roots first.
        var originalWorld = new Dictionary<long, Matrix4x4>();
        var order = new List<FbxObject>();
        foreach (var model in scene.Models)
            VisitModel(model, scene, originalWorld, order);

        // Skeleton height (native units) for the position tolerance.
        float minUp = float.MaxValue, maxUp = float.MinValue;
        foreach (var w in originalWorld.Values)
        {
            var t = w.Translation;
            float up = MathF.Max(MathF.Abs(t.Y), MathF.Abs(t.Z));
            minUp = MathF.Min(minUp, up);
            maxUp = MathF.Max(maxUp, up);
        }
        float posTolerance = MathF.Max(0.0001f, (maxUp - minUp) * PositionToleranceOfHeight);

        // Any bone posed away from its bind?
        int posedCount = 0;
        foreach (var model in order)
        {
            if (!scene.BindPose.TryGetValue(model.Id, out var bind))
                continue;
            var fk = FbxTransform.ToRigid(originalWorld[model.Id]);
            var target = FbxTransform.ToRigid(bind);
            if ((fk.Pos - target.Pos).Length() > posTolerance
                || MathQ.AngleBetween(fk.Rot, target.Rot) > RotationToleranceDeg * MathF.PI / 180f)
            {
                posedCount++;
            }
        }
        if (posedCount == 0)
        {
            report = $"node transforms match the BindPose ({scene.BindPose.Count} entries)";
            return null;
        }

        // Rewrite every BindPose-backed model's local transform so FK lands on the bind.
        // correctedWorld carries the repair down the hierarchy for bones WITHOUT a
        // BindPose entry (helpers keep their original locals under corrected parents).
        var correctedWorld = new Dictionary<long, Matrix4x4>();
        int patched = 0, skipped = 0;
        foreach (var model in order)
        {
            var parent = model.ModelParent;
            Matrix4x4 parentWorld = parent is not null && correctedWorld.TryGetValue(parent.Id, out var pw)
                ? pw
                : Matrix4x4.Identity;

            if (!scene.BindPose.TryGetValue(model.Id, out var bindWorld))
            {
                // No bind info: keep the original local under the (possibly corrected) parent.
                var transform = FbxTransform.FromModel(scene, model);
                correctedWorld[model.Id] = transform.LocalMatrixDefault() * parentWorld;
                continue;
            }

            if (!Matrix4x4.Invert(parentWorld, out var invParent))
            {
                correctedWorld[model.Id] = bindWorld;
                skipped++;
                continue;
            }

            // Row-vector: World = Local · ParentWorld  ⇒  Local = World · ParentWorld⁻¹.
            var desiredLocal = FbxTransform.ToRigid(bindWorld * invParent);
            if (PatchModelLocal(scene, model, desiredLocal))
                patched++;
            else
                skipped++;

            // Children FK from the ACTUAL bind either way (unpatchable bones are rare and
            // their children still deserve correct parent frames).
            correctedWorld[model.Id] = bindWorld;
        }

        report = $"{posedCount} bones were exported mid-pose; repaired {patched}"
            + (skipped > 0 ? $", {skipped} left as-is (pivots/scale beyond the safe rewrite)" : "");
        if (patched == 0)
            return null;
        return FbxBinaryWriter.Write(root);
    }

    private static void VisitModel(
        FbxObject model, FbxScene scene,
        Dictionary<long, Matrix4x4> world, List<FbxObject> order)
    {
        if (world.ContainsKey(model.Id))
            return;
        Matrix4x4 parentWorld = Matrix4x4.Identity;
        if (model.ModelParent is { } parent)
        {
            VisitModel(parent, scene, world, order);
            parentWorld = world[parent.Id];
        }
        var transform = FbxTransform.FromModel(scene, model);
        world[model.Id] = transform.LocalMatrixDefault() * parentWorld;
        order.Add(model);
    }

    // ------------------------------------------------------------------ patching

    /// <summary>
    /// Rewrites one Model's Lcl Translation/Rotation so its local evaluates to
    /// <paramref name="desiredLocal"/>. Verified by re-evaluating through the full FBX
    /// transform formula — models using pivots/offsets/scale that the rewrite cannot
    /// express are left untouched (returns false).
    /// </summary>
    private static bool PatchModelLocal(FbxScene scene, FbxObject model, XForm desiredLocal)
    {
        var transform = FbxTransform.FromModel(scene, model);

        // R_total = Pre · R · Post⁻¹  ⇒  R = Pre⁻¹ · R_total · Post
        var r = MathQ.Normalize(
            Quaternion.Conjugate(transform.PreRotation)
            * desiredLocal.Rot
            * transform.PostRotation);

        var eulerDeg = QuaternionToEulerDegrees(r, transform.RotationOrder);

        // Full-formula verification (catches pivots, scale, decomposition branches).
        var check = new FbxTransform
        {
            LclTranslation = desiredLocal.Pos,
            LclRotationDeg = eulerDeg,
            LclScaling = transform.LclScaling,
            PreRotation = transform.PreRotation,
            PostRotation = transform.PostRotation,
            RotationOffset = transform.RotationOffset,
            RotationPivot = transform.RotationPivot,
            ScalingOffset = transform.ScalingOffset,
            ScalingPivot = transform.ScalingPivot,
            RotationOrder = transform.RotationOrder,
        };
        var evaluated = FbxTransform.ToRigid(check.LocalMatrixDefault());
        float posScale = MathF.Max(1f, desiredLocal.Pos.Length());
        if ((evaluated.Pos - desiredLocal.Pos).Length() > 0.001f * posScale
            || MathQ.AngleBetween(evaluated.Rot, desiredLocal.Rot) > 0.1f * MathF.PI / 180f)
        {
            return false;
        }

        SetProperty70(model.Node, "Lcl Translation", "Lcl Translation", "A",
            desiredLocal.Pos.X, desiredLocal.Pos.Y, desiredLocal.Pos.Z);
        SetProperty70(model.Node, "Lcl Rotation", "Lcl Rotation", "A",
            eulerDeg.X, eulerDeg.Y, eulerDeg.Z);
        return true;
    }

    /// <summary>Sets (or adds) a 3-double P entry in the node's Properties70 block.</summary>
    private static void SetProperty70(
        FbxNode modelNode, string name, string type, string flags,
        double x, double y, double z)
    {
        var block = modelNode.Child("Properties70");
        if (block is null)
        {
            block = new FbxNode("Properties70");
            modelNode.Children.Insert(0, block);
        }

        foreach (var p in block.ChildrenNamed("P"))
        {
            if (p.Properties.Count >= 1 && p.Properties[0] is string n && n == name)
            {
                // Values live at indices 4.. — replace, extending if the entry was short.
                while (p.Properties.Count < 7)
                    p.Properties.Add(0.0);
                p.Properties[4] = x;
                p.Properties[5] = y;
                p.Properties[6] = z;
                return;
            }
        }

        var entry = new FbxNode("P");
        entry.Properties.Add(name);
        entry.Properties.Add(type);
        entry.Properties.Add("");
        entry.Properties.Add(flags);
        entry.Properties.Add(x);
        entry.Properties.Add(y);
        entry.Properties.Add(z);
        block.Children.Add(entry);
    }

    // ------------------------------------------------------------------ euler decomposition

    /// <summary>
    /// Decomposes a quaternion into FBX euler degrees for the given RotationOrder, the
    /// exact inverse of <see cref="FbxTransform.EulerDegreesToQuaternion"/>. Tait-Bryan
    /// extraction on the column-convention rotation matrix.
    /// </summary>
    public static Vector3 QuaternionToEulerDegrees(Quaternion q, int order)
    {
        // Column-convention matrix C (v' = C·v): C = transpose of System.Numerics' row form.
        var m = Matrix4x4.CreateFromQuaternion(q);
        // C[r,c]: row r, column c.
        float c00 = m.M11, c01 = m.M21, c02 = m.M31;
        float c10 = m.M12, c11 = m.M22, c12 = m.M32;
        float c20 = m.M13, c21 = m.M23, c22 = m.M33;

        const float radToDeg = 180f / MathF.PI;
        float a, b, c;
        switch (order)
        {
            case 0: // XYZ: C = Rz·Ry·Rx
                b = MathF.Asin(Math.Clamp(-c20, -1f, 1f));
                a = MathF.Atan2(c21, c22);
                c = MathF.Atan2(c10, c00);
                return new Vector3(a * radToDeg, b * radToDeg, c * radToDeg);
            case 1: // XZY: C = Ry·Rz·Rx
                b = MathF.Asin(Math.Clamp(c10, -1f, 1f));
                a = MathF.Atan2(-c12, c11);
                c = MathF.Atan2(-c20, c00);
                return new Vector3(a * radToDeg, c * radToDeg, b * radToDeg);
            case 2: // YZX: C = Rx·Rz·Ry
                b = MathF.Asin(Math.Clamp(-c01, -1f, 1f));
                a = MathF.Atan2(c02, c00);
                c = MathF.Atan2(c21, c11);
                return new Vector3(c * radToDeg, a * radToDeg, b * radToDeg);
            case 3: // YXZ: C = Rz·Rx·Ry
                b = MathF.Asin(Math.Clamp(c21, -1f, 1f));
                a = MathF.Atan2(-c20, c22);
                c = MathF.Atan2(-c01, c11);
                return new Vector3(b * radToDeg, a * radToDeg, c * radToDeg);
            case 4: // ZXY: C = Ry·Rx·Rz
                b = MathF.Asin(Math.Clamp(-c12, -1f, 1f));
                a = MathF.Atan2(c02, c22);
                c = MathF.Atan2(c10, c11);
                return new Vector3(b * radToDeg, a * radToDeg, c * radToDeg);
            case 5: // ZYX: C = Rx·Ry·Rz
            case 6: // eSphericXYZ treated as XYZ on read; mirror that here
            default:
                if (order == 5)
                {
                    b = MathF.Asin(Math.Clamp(c02, -1f, 1f));
                    a = MathF.Atan2(-c01, c00);
                    c = MathF.Atan2(-c12, c22);
                    return new Vector3(c * radToDeg, b * radToDeg, a * radToDeg);
                }
                goto case 0;
        }
    }
}
