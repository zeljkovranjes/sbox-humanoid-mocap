#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap.Dl;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// The target-skeleton graph the SAME decoder is conditioned on, plus everything needed to
/// turn decoder output back into target-rig bone locals.
/// </summary>
public sealed class SameTargetGraph
{
    /// <summary>The rig this graph was built from.</summary>
    public required TargetRig Rig { get; init; }

    /// <summary>Rig bone index of the graph root (hips).</summary>
    public required int HipsBone { get; init; }

    /// <summary>Rig bone index per node; -1 for synthesized end joints.</summary>
    public required int[] NodeBone { get; init; }

    /// <summary>Graph-parent node index; -1 for the root (node 0).</summary>
    public required int[] NodeParent { get; init; }

    /// <summary>Node names (bone names; end joints suffixed <c>_end</c>).</summary>
    public required string[] NodeNames { get; init; }

    /// <summary>Normalized skeleton features (lo3 ⊕ go3), flat [NodeCount × 6].</summary>
    public required float[] X { get; init; }

    /// <summary>Raw local offsets, flat [NodeCount × 3] (aligned space; FK uses these).</summary>
    public required float[] LoRaw { get; init; }

    /// <summary>Raw global rest offsets, flat [NodeCount × 3] (diagnostics/tests).</summary>
    public required float[] GoRaw { get; init; }

    /// <summary>World rotation taking rig space into the canonical SAME frame.</summary>
    public required Quaternion Align { get; init; }

    /// <summary>Aligned-space offset restoring the rig's rest root XZ and ground height
    /// when converting decoded root trajectories back to rig space.</summary>
    public required Vector3 RestOffset { get; init; }

    /// <summary>Rig-space rest world rotation per node (the leaf's for end joints).</summary>
    public required Quaternion[] NodeRestWorldRot { get; init; }

    /// <summary>Nodes per frame.</summary>
    public int NodeCount => NodeBone.Length;
}

/// <summary>
/// Target side of the SAME port: builds the decoder's skeleton graph from a
/// <see cref="TargetRig"/> and converts decoder output (normalized r ⊕ q ⊕ c per node)
/// into a <see cref="Clip"/> of target bone locals.
/// </summary>
/// <remarks>
/// <para><b>Graph.</b> Nodes are the rig's <see cref="BoneClass.Animated"/> bones in the
/// hips subtree, <b>excluding finger-role bones</b> (the fingerless "core" variant the
/// spike validated — the checkpoint was trained finger-less, so finger joints would only
/// receive smooth but meaningless rotations; target finger bones keep their rest locals
/// instead, matching the geometric solver's CopyPinky posture), plus one end joint per
/// leaf (from the rig's <c>tail_world</c> data when present, else a half-length
/// continuation of the parent segment). Node 0 is the hips, where the decoder's root
/// output row lives.</para>
/// <para><b>Decode.</b> Per frame: denormalize, take the root row's r = (dθ, dx, dz, h),
/// 6D→rotation for every node, FK over the normalized skeleton (identity rest rotations,
/// offsets only), accumulate the facing deltas over time, then conjugate the normalized
/// world rotations back onto the actual rig: <c>R_rig(j) = A⁻¹ · Ĝ(j) · A · R_rest(j)</c>
/// (the normalized skeleton's world rotations are world-space deltas from the rest pose —
/// only rest <i>world orientations</i> are needed, which sidesteps the Citizen
/// no-anatomical-bone-axes issue). Bones outside the graph (fingers, ConstraintDriven,
/// IkBaked, anything above the hips) keep rest locals; IK baking and cleanup passes run
/// later in the shared pipeline.</para>
/// </remarks>
public static class SameTarget
{
    /// <summary>Builds the decoder conditioning graph for a target rig.</summary>
    /// <param name="rig">Target rig.</param>
    /// <param name="stats">Normalization statistics.</param>
    /// <param name="align">Apply the rest-geometry world alignment (Y-up, +Z facing).
    /// Disabled only by golden-parity tests (the Python reference applies none).</param>
    /// <exception cref="ArgumentException">Thrown when the rig maps no usable hips/root.</exception>
    public static SameTargetGraph Build(TargetRig rig, SameStats stats, bool align = true)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(stats);
        var skeleton = rig.Skeleton;
        var map = rig.ToMappingResult();
        var hips = rig.BoneForRole(BoneRole.Hips) ?? SameFeatures.FindHips(skeleton, null);
        if (hips < 0 || hips >= skeleton.Count)
            throw new ArgumentException("Target rig has no identifiable hips bone.", nameof(rig));

        var alignRot = align ? ComputeAlignment(skeleton, map) : Quaternion.Identity;

        // ---- node selection: Animated, hips subtree, no finger roles ---------------------
        var inSubtree = new bool[skeleton.Count];
        inSubtree[hips] = true;
        for (var i = hips + 1; i < skeleton.Count; i++)
        {
            var parent = skeleton[i].ParentIndex;
            if (parent >= 0 && inSubtree[parent])
                inSubtree[i] = true;
        }

        var nodeBone = new List<int>();
        var nodeParent = new List<int>();
        var names = new List<string>();
        var nodeOfBone = new Dictionary<int, int>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (!inSubtree[i] || rig.ClassOf(i) != BoneClass.Animated || IsFingerRole(rig.RoleOf(i)))
                continue;
            // Graph parent: nearest included ancestor; bones whose chain to the hips is
            // interrupted by an excluded bone are skipped entirely.
            var parentNode = -1;
            if (i != hips)
            {
                var a = skeleton[i].ParentIndex;
                while (a >= 0 && !nodeOfBone.ContainsKey(a))
                    a = skeleton[a].ParentIndex;
                if (a < 0)
                    continue;
                parentNode = nodeOfBone[a];
            }
            nodeOfBone[i] = nodeBone.Count;
            nodeBone.Add(i);
            nodeParent.Add(parentNode);
            names.Add(skeleton[i].Name);
        }
        if (nodeBone.Count == 0 || nodeBone[0] != hips)
            throw new ArgumentException("Target rig's hips bone is not an Animated bone.", nameof(rig));

        // ---- end joints per leaf ----------------------------------------------------------
        var isLeaf = new bool[nodeBone.Count];
        Array.Fill(isLeaf, true);
        for (var n = 0; n < nodeParent.Count; n++)
        {
            if (nodeParent[n] >= 0)
                isLeaf[nodeParent[n]] = false;
        }
        var endTips = new Dictionary<int, Vector3>(); // node index → rig-space tip delta
        var boneCount = nodeBone.Count;
        for (var n = 0; n < boneCount; n++)
        {
            if (!isLeaf[n])
                continue;
            var b = nodeBone[n];
            Vector3 tip;
            if (rig.TailWorldOf(b) is { } tail)
            {
                tip = tail - skeleton.RestWorld[b].Pos;
            }
            else
            {
                var p = skeleton[b].ParentIndex;
                tip = p >= 0 ? (skeleton.RestWorld[b].Pos - skeleton.RestWorld[p].Pos) * 0.5f : Vector3.Zero;
            }
            if (tip.Length() < 1e-6f)
                tip = new Vector3(0f, 2f, 0f); // matches the spike's BVH generator fallback
            nodeBone.Add(-1);
            nodeParent.Add(n);
            names.Add(skeleton[b].Name + "_end");
            endTips[nodeBone.Count - 1] = tip;
        }

        // ---- aligned rest geometry --------------------------------------------------------
        var count = nodeBone.Count;
        var restPos = new Vector3[count];
        var restRot = new Quaternion[count];
        for (var n = 0; n < count; n++)
        {
            if (nodeBone[n] >= 0)
            {
                restPos[n] = Vector3.Transform(skeleton.RestWorld[nodeBone[n]].Pos, alignRot);
                restRot[n] = skeleton.RestWorld[nodeBone[n]].Rot;
            }
            else
            {
                var leafBone = nodeBone[nodeParent[n]];
                restPos[n] = Vector3.Transform(
                    skeleton.RestWorld[leafBone].Pos + endTips[n], alignRot);
                restRot[n] = skeleton.RestWorld[leafBone].Rot;
            }
        }

        // No ground shift on the target: the convention keeps the rig's authored ground
        // plane (the decoded height channel is interpreted against it) — matching the
        // validated spike setup, where the s&box rig was consumed as authored.
        var rootXZ = new Vector3(restPos[0].X, 0f, restPos[0].Z);

        var lo = new float[count * 3];
        var go = new float[count * 3];
        var x = new float[count * SameModel.SkelDim];
        for (var n = 0; n < count; n++)
        {
            Vector3 loV, goV;
            if (n == 0)
            {
                loV = new Vector3(0f, restPos[0].Y, 0f);
                goV = loV;
            }
            else
            {
                loV = restPos[n] - restPos[nodeParent[n]];
                goV = restPos[n] - rootXZ;
            }
            lo[n * 3] = loV.X;
            lo[n * 3 + 1] = loV.Y;
            lo[n * 3 + 2] = loV.Z;
            go[n * 3] = goV.X;
            go[n * 3 + 1] = goV.Y;
            go[n * 3 + 2] = goV.Z;
            x[n * 6] = (loV.X - stats.LoM[0]) / stats.LoS[0];
            x[n * 6 + 1] = (loV.Y - stats.LoM[1]) / stats.LoS[1];
            x[n * 6 + 2] = (loV.Z - stats.LoM[2]) / stats.LoS[2];
            x[n * 6 + 3] = (goV.X - stats.GoM[0]) / stats.GoS[0];
            x[n * 6 + 4] = (goV.Y - stats.GoM[1]) / stats.GoS[1];
            x[n * 6 + 5] = (goV.Z - stats.GoM[2]) / stats.GoS[2];
        }

        return new SameTargetGraph
        {
            Rig = rig,
            HipsBone = hips,
            NodeBone = nodeBone.ToArray(),
            NodeParent = nodeParent.ToArray(),
            NodeNames = names.ToArray(),
            X = x,
            LoRaw = lo,
            GoRaw = go,
            Align = alignRot,
            RestOffset = new Vector3(rootXZ.X, 0f, rootXZ.Z),
            NodeRestWorldRot = restRot,
        };
    }

    /// <summary>Tiles the single-frame graph for a batch of frames: features, edges, batch ids.</summary>
    public static (float[] X, int[] EdgeSrc, int[] EdgeDst, int[] Batch) Tile(SameTargetGraph graph, int frames)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var j = graph.NodeCount;
        var x = new float[frames * j * SameModel.SkelDim];
        var batch = new int[frames * j];
        for (var f = 0; f < frames; f++)
        {
            Array.Copy(graph.X, 0, x, f * j * SameModel.SkelDim, graph.X.Length);
            for (var i = 0; i < j; i++)
                batch[f * j + i] = f;
        }
        var (src, dst) = SameFeatures.BuildEdges(graph.NodeParent, frames);
        return (x, src, dst, batch);
    }

    /// <summary>
    /// Converts decoder output into a clip of target bone locals (one frame per decoded
    /// frame; non-graph bones at rest).
    /// </summary>
    /// <param name="hatD">Decoder output, flat [frames·NodeCount × 11].</param>
    /// <exception cref="InvalidOperationException">Thrown when the output is non-finite.</exception>
    public static Clip DecodeClip(
        float[] hatD, SameTargetGraph graph, int frames, SameStats stats,
        string name, float fps, bool looping)
    {
        ArgumentNullException.ThrowIfNull(hatD);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(stats);
        var j = graph.NodeCount;
        if (hatD.Length != frames * j * SameModel.OutDim)
            throw new ArgumentException(
                $"hatD length {hatD.Length} != frames {frames} × nodes {j} × {SameModel.OutDim}.");

        var skeleton = graph.Rig.Skeleton;
        var alignInv = Quaternion.Conjugate(graph.Align);
        var clip = new Clip(name, fps, looping);

        // Facing accumulator (yaw rotation + ground-plane translation).
        var accumYaw = 0f;
        var accumPos = Vector3.Zero;

        var nodeOfBone = new int[skeleton.Count];
        Array.Fill(nodeOfBone, -1);
        for (var n = 0; n < j; n++)
        {
            if (graph.NodeBone[n] >= 0)
                nodeOfBone[graph.NodeBone[n]] = n;
        }

        var normWorld = new Quaternion[j];
        for (var f = 0; f < frames; f++)
        {
            var rootRow = (f * j) * SameModel.OutDim;
            var dTheta = hatD[rootRow] * stats.RS[0] + stats.RM[0];
            var dx = hatD[rootRow + 1] * stats.RS[1] + stats.RM[1];
            var dz = hatD[rootRow + 2] * stats.RS[2] + stats.RM[2];
            var height = hatD[rootRow + 3] * stats.RS[3] + stats.RM[3];

            // accumulate: T_acc(f) = T_acc(f−1) ∘ T_f
            accumPos += Vector3.Transform(new Vector3(dx, 0f, dz),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, accumYaw));
            accumYaw += dTheta;
            var accumRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, accumYaw);

            // Normalized-skeleton world rotations via graph FK.
            for (var n = 0; n < j; n++)
            {
                var row = (f * j + n) * SameModel.OutDim + 4; // q starts after r
                var local = SixDToQuaternion(
                    hatD[row] * stats.QS[0] + stats.QM[0],
                    hatD[row + 1] * stats.QS[1] + stats.QM[1],
                    hatD[row + 2] * stats.QS[2] + stats.QM[2],
                    hatD[row + 3] * stats.QS[3] + stats.QM[3],
                    hatD[row + 4] * stats.QS[4] + stats.QM[4],
                    hatD[row + 5] * stats.QS[5] + stats.QM[5]);
                normWorld[n] = n == 0
                    ? MathQ.Normalize(accumRot * local)
                    : MathQ.Normalize(normWorld[graph.NodeParent[n]] * local);
            }

            // Root world position in rig space.
            var alignedRootPos = new Vector3(accumPos.X, height, accumPos.Z) + graph.RestOffset;
            var rigRootPos = Vector3.Transform(alignedRootPos, alignInv);

            // Bone locals: graph bones get conjugated world rotations (rest-local
            // translations preserved), the hips additionally gets the decoded position.
            var locals = new XForm[skeleton.Count];
            var world = new XForm[skeleton.Count];
            for (var b = 0; b < skeleton.Count; b++)
            {
                var parent = skeleton[b].ParentIndex;
                var n = nodeOfBone[b];
                if (n < 0)
                {
                    locals[b] = skeleton[b].RestLocal;
                }
                else
                {
                    var rigWorldRot = MathQ.Normalize(
                        alignInv * normWorld[n] * graph.Align * graph.NodeRestWorldRot[n]);
                    if (b == graph.HipsBone)
                    {
                        var desired = new XForm(rigRootPos, rigWorldRot);
                        locals[b] = parent < 0 ? desired : XForm.ToLocal(world[parent], desired);
                    }
                    else
                    {
                        var parentRot = parent < 0 ? Quaternion.Identity : world[parent].Rot;
                        locals[b] = new XForm(
                            skeleton[b].RestLocal.Pos,
                            MathQ.Normalize(Quaternion.Conjugate(parentRot) * rigWorldRot));
                    }
                }
                world[b] = parent < 0 ? locals[b] : XForm.Compose(world[parent], locals[b]);
            }

            AssertFiniteFrame(locals);
            clip.Frames.Add(locals);
        }

        return clip;
    }

    /// <summary>Gram-Schmidt 6D → quaternion (columns e1, e2, e1×e2).</summary>
    internal static Quaternion SixDToQuaternion(float v1x, float v1y, float v1z, float v2x, float v2y, float v2z)
    {
        var v1 = new Vector3(v1x, v1y, v1z);
        var v2 = new Vector3(v2x, v2y, v2z);
        var e1 = v1.LengthSquared() > 1e-12f ? Vector3.Normalize(v1) : Vector3.UnitX;
        var u2 = v2 - e1 * Vector3.Dot(e1, v2);
        var e2 = u2.LengthSquared() > 1e-12f ? Vector3.Normalize(u2) : Vector3.UnitY;
        var e3 = Vector3.Cross(e1, e2);
        // Rows of the row-vector-convention matrix are the columns of the rotation.
        var m = new Matrix4x4(
            e1.X, e1.Y, e1.Z, 0f,
            e2.X, e2.Y, e2.Z, 0f,
            e3.X, e3.Y, e3.Z, 0f,
            0f, 0f, 0f, 1f);
        return MathQ.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private static bool IsFingerRole(BoneRole? role)
    {
        if (role is not { } r)
            return false;
        var name = r.ToString();
        return name.StartsWith("Thumb", StringComparison.Ordinal)
            || name.StartsWith("Index", StringComparison.Ordinal)
            || name.StartsWith("Middle", StringComparison.Ordinal)
            || name.StartsWith("Ring", StringComparison.Ordinal)
            || name.StartsWith("Pinky", StringComparison.Ordinal);
    }

    private static Quaternion ComputeAlignment(
        HumanoidMocap.Skeleton.Skeleton skeleton, MappingResult map)
    {
        try
        {
            var frame = CharacterFrame.Compute(skeleton, map, skeleton.RestWorld);
            return SameFeatures.AlignFromBasis(frame.Lateral, frame.Up, frame.Forward);
        }
        catch (ArgumentException)
        {
            return Quaternion.Identity; // assume the rig is already Y-up/+Z (the shipped one is)
        }
    }

    private static void AssertFiniteFrame(XForm[] locals)
    {
        foreach (var xf in locals)
        {
            if (!float.IsFinite(xf.Pos.X) || !float.IsFinite(xf.Pos.Y) || !float.IsFinite(xf.Pos.Z)
                || !float.IsFinite(xf.Rot.X) || !float.IsFinite(xf.Rot.Y)
                || !float.IsFinite(xf.Rot.Z) || !float.IsFinite(xf.Rot.W))
                throw new InvalidOperationException("DL solve produced non-finite transforms.");
        }
    }
}
