#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Dl;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>The z-normalization statistics shipped with the SAME checkpoint
/// (<c>ms_dict</c>): per-feature mean/std applied to every input except contact.</summary>
public sealed class SameStats
{
    internal float[] LoM, LoS, GoM, GoS, QM, QS, PM, PS, RM, RS, PvM, PvS, QvM, QvS, PprevM, PprevS;

    /// <summary>Reads the 16 <c>ms.*</c> arrays from a parsed weight blob.</summary>
    public SameStats(SameWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        LoM = weights.Stat("lo_m"); LoS = weights.Stat("lo_s");
        GoM = weights.Stat("go_m"); GoS = weights.Stat("go_s");
        QM = weights.Stat("q_m"); QS = weights.Stat("q_s");
        PM = weights.Stat("p_m"); PS = weights.Stat("p_s");
        RM = weights.Stat("r_m"); RS = weights.Stat("r_s");
        PvM = weights.Stat("pv_m"); PvS = weights.Stat("pv_s");
        QvM = weights.Stat("qv_m"); QvS = weights.Stat("qv_s");
        PprevM = weights.Stat("pprev_m"); PprevS = weights.Stat("pprev_s");
    }
}

/// <summary>A batched per-frame source graph ready for <see cref="SameModel.Encode"/>.</summary>
public sealed class SameSourceGraph
{
    /// <summary>Normalized node features, flat [FrameCount·JointCount × 32].</summary>
    public required float[] X { get; init; }

    /// <summary>Edge sources (bidirectional + self-loops, all frames).</summary>
    public required int[] EdgeSrc { get; init; }

    /// <summary>Edge destinations.</summary>
    public required int[] EdgeDst { get; init; }

    /// <summary>Frame id per node.</summary>
    public required int[] Batch { get; init; }

    /// <summary>Number of feature frames (matches the clip's frame count in production
    /// mode; native frames − 2 in golden-parity mode).</summary>
    public required int FrameCount { get; init; }

    /// <summary>Graph joints per frame (hips subtree + end joints).</summary>
    public required int JointCount { get; init; }

    /// <summary>Graph node names within one frame (bone names; synthesized leaf tips get
    /// a <c>_end</c> suffix). For diagnostics and parity tests.</summary>
    public required string[] JointNames { get; init; }
}

/// <summary>
/// Source-side feature pipeline of the SAME port (FEASIBILITY.md "C# port work list"
/// steps 1–5): skeleton normalization, cm/Y-up/+Z-facing alignment, per-frame
/// q/p/r/pv/qv/pprev/c features in the root-facing frame, z-normalization, and the
/// bidirectional+self-loop edge list.
/// </summary>
/// <remarks>
/// <para><b>Skeleton normalization without an intermediate skeleton.</b> SAME's
/// <c>motion_normalize</c> rebuilds the rig with identity rest-local rotations and
/// re-expresses every frame against it. Algebraically the normalized motion's world
/// rotations are exactly the world-space deltas from the T-pose,
/// <c>Ĝ(j,t) = G(j,t) · G_tpose(j)⁻¹</c>, its local rotations are
/// <c>Ĝ(parent)⁻¹ · Ĝ(j)</c>, and its world positions equal the original world positions
/// — so this port computes the features directly from FK world transforms, no rebuilt
/// skeleton needed (verified against the Python pipeline by the golden-vector tests).</para>
/// <para><b>T-pose reference.</b> SAME consumes the source clip's first frame as the
/// reference; production keeps that convention but emits one feature frame per clip frame
/// (the sequence is computed over [f0, f0…fN−1] with f0 doubling as the reference — see
/// <see cref="TposeReference"/> for why the rest-pose alternative measurably loses).
/// Golden-parity mode replicates Python's frame accounting exactly (frame 0 = reference,
/// frame 1 dropped).</para>
/// <para><b>Alignment.</b> Features assume cm (guaranteed by the importers), Y-up and
/// rest facing +Z with +X to the character's left. The source is rotated by a world
/// alignment derived from the rig's rest geometry (<see cref="CharacterFrame"/> via the
/// mapping when computable, else the file's axis metadata), snapped to the nearest whole
/// axis permutation (an exact-axis rig must map to the identity — the rest-geometry tilt
/// of a few degrees otherwise leaks into every feature), and shifted so the lowest joint
/// over the clip sits on the ground plane.</para>
/// <para><b>Graph.</b> Nodes are the hips subtree (hips = mapped Hips role, else the
/// shallowest branch bone) in skeleton order — hips is always node 0, which is where the
/// root feature row lives — plus one synthesized end joint per childless leaf (BVH End
/// Sites already import as <c>_end</c> bones and are used as-is; FBX leaves get a
/// half-length continuation of their parent segment).</para>
/// </remarks>
public static class SameFeatures
{
    /// <summary>How the T-pose reference (skeleton normalization + lo/go features) is chosen.</summary>
    public enum TposeReference
    {
        /// <summary>The clip's own first frame — SAME's native convention and the
        /// production default. Empirically the pretrained checkpoint tracks arms FAR
        /// better against the clip's first frame than against a synthesized true T-pose,
        /// even though its training references are T-poses (measured on the fixture clip:
        /// mean role cosine vs the geometric solver 0.94 first-frame vs 0.57 rest-pose,
        /// hands flipping negative — reproduced identically in the Python reference
        /// pipeline, so it is a property of the checkpoint, not of this port).</summary>
        FirstFrame,

        /// <summary>Synthesize the reference from the skeleton's rest pose (the
        /// FEASIBILITY suggestion; kept for experiments — see above for why it lost).</summary>
        RestPose,
    }

    /// <summary>Options for <see cref="BuildSourceGraph"/>; defaults are production mode.</summary>
    public sealed class SourceOptions
    {
        /// <summary>T-pose reference choice (see <see cref="TposeReference"/>).</summary>
        public TposeReference Reference { get; init; } = TposeReference.FirstFrame;

        /// <summary>SAME's native frame accounting: the first frame is consumed as the
        /// reference and the next dropped for its undefined velocity, so the output has
        /// two frames fewer than the clip. Golden-parity tests only — production emits
        /// one feature frame per clip frame (the first frame doubles as the reference
        /// and gets zero velocity).</summary>
        public bool NativeFrameDrop { get; init; }

        /// <summary>Apply the rest-geometry world alignment (Y-up, +Z facing). Disabled
        /// only by golden-parity tests (Python applies none).</summary>
        public bool Align { get; init; } = true;

        /// <summary>Ground both the T-pose reference and the animation: the T-pose is
        /// shifted so its lowest joint sits at height 0 (a BVH rest pose has its root at
        /// the origin and would otherwise put the hips on the floor), and the animation is
        /// shifted by its own lowest joint height over the clip (no-op for the usual
        /// authored-ground-at-0 data). Disabled only by golden-parity tests (the Python
        /// reference consumes data as authored).</summary>
        public bool GroundShift { get; init; } = true;
    }

    private const float ContactHeightCm = 5f;
    private const float ContactSpeedMps = 0.4f;
    private const float VelocityFps = 30f;

    /// <summary>
    /// Builds the batched source graph for one clip: graph selection, alignment, per-frame
    /// features, normalization, edges.
    /// </summary>
    /// <param name="scene">Imported source (cm, native axes).</param>
    /// <param name="clipIndex">Clip to encode.</param>
    /// <param name="map">Source mapping; used only for hips identification and the
    /// rest-geometry alignment (the model itself is skeleton-agnostic). May be sparse —
    /// heuristics cover missing roles.</param>
    /// <param name="stats">Normalization statistics.</param>
    /// <param name="options">Null = production mode.</param>
    public static SameSourceGraph BuildSourceGraph(
        SourceScene scene, int clipIndex, MappingResult? map, SameStats stats, SourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(stats);
        options ??= new SourceOptions();
        if (clipIndex < 0 || clipIndex >= scene.Clips.Count)
            throw new ArgumentOutOfRangeException(nameof(clipIndex));
        var clip = scene.Clips[clipIndex];
        if (clip.FrameCount < 1)
            throw new ArgumentException("Clip has no frames.", nameof(clipIndex));
        if (options.NativeFrameDrop && clip.FrameCount < 3)
            throw new ArgumentException("Native frame accounting needs at least 3 frames.", nameof(options));

        var skeleton = scene.Skeleton;
        var hips = FindHips(skeleton, map);
        var nodes = GraphNodes.Build(skeleton, hips);

        var align = options.Align ? ComputeAlignment(skeleton, map, scene) : Quaternion.Identity;

        // T-pose reference world transforms (aligned), grounded on its own lowest joint
        // (a BVH rest pose has the root at the origin — ungrounded, its hips would sit on
        // the floor and every height-bearing feature would be wrong).
        var tposeLocals = options.Reference == TposeReference.RestPose
            ? Pose.Rest(skeleton).Locals
            : clip.Frames[0];
        var tposeWorld = AlignedWorld(skeleton, tposeLocals, align, nodes);
        if (options.GroundShift)
            ShiftToGround(tposeWorld.Pos);

        // The pose sequence the features run over; features are emitted for seq[1..].
        var seq = new List<XForm[]>();
        if (options.NativeFrameDrop)
        {
            for (var f = 1; f < clip.FrameCount; f++)
                seq.Add(clip.Frames[f]);
        }
        else
        {
            seq.Add(clip.Frames[0]); // duplicated: gives the real first frame zero velocity
            for (var f = 0; f < clip.FrameCount; f++)
                seq.Add(clip.Frames[f]);
        }

        var frames = seq.Count - 1;
        var j = nodes.Count;

        // Pass 0: aligned world transforms; ground the whole clip on its lowest joint.
        var worlds = new AlignedFrame[seq.Count];
        for (var t = 0; t < seq.Count; t++)
            worlds[t] = AlignedWorld(skeleton, seq[t], align, nodes);
        if (options.GroundShift)
        {
            var ground = float.PositiveInfinity;
            foreach (var world in worlds)
            {
                foreach (var p in world.Pos)
                    ground = MathF.Min(ground, p.Y);
            }
            if (float.IsFinite(ground) && ground != 0f)
            {
                foreach (var world in worlds)
                {
                    for (var i = 0; i < j; i++)
                        world.Pos[i].Y -= ground;
                }
            }
        }

        // Pass 1: normalized-skeleton local rotations + facing per frame.
        var localRots = new Quaternion[seq.Count][]; // facing-adjusted at the root row
        var facing = new (float Yaw, Vector3 Pos)[seq.Count];
        for (var t = 0; t < seq.Count; t++)
        {
            var world = worlds[t];

            // Normalized-skeleton world rotations: world delta from the T-pose.
            var normWorld = new Quaternion[j];
            for (var i = 0; i < j; i++)
                normWorld[i] = MathQ.Normalize(world.Rot[i] * Quaternion.Conjugate(tposeWorld.Rot[i]));

            // Root facing: yaw (about +Y) of the normalized root rotation, at the root's
            // ground-plane position.
            var yaw = YawAngle(normWorld[0]);
            facing[t] = (yaw, new Vector3(world.Pos[0].X, 0f, world.Pos[0].Z));

            // Normalized-skeleton local rotations; root premultiplied by the inverse facing.
            var locals = new Quaternion[j];
            locals[0] = MathQ.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yaw) * normWorld[0]);
            for (var i = 1; i < j; i++)
            {
                locals[i] = MathQ.Normalize(
                    Quaternion.Conjugate(normWorld[nodes.Parent[i]]) * normWorld[i]);
            }
            localRots[t] = locals;
        }

        // Pass 2: feature rows.
        var x = new float[frames * j * SameModel.InputDim];
        for (var t = 1; t < seq.Count; t++)
        {
            var f = t - 1;
            var (yaw, fpos) = facing[t];
            var invFacing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yaw);
            var (yawPrev, fposPrev) = facing[t - 1];
            var invFacingPrev = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yawPrev);

            // r: facing delta (dθ, dx, dz) + absolute root height.
            var dTheta = WrapPi(yaw - yawPrev);
            var dPlanar = Vector3.Transform(fpos - fposPrev, invFacingPrev);
            var rootHeight = worlds[t].Pos[0].Y;

            for (var i = 0; i < j; i++)
            {
                var row = (f * j + i) * SameModel.InputDim;
                var col = 0;

                // ---- skel: lo, go (tiled per frame) -------------------------------------
                Vector3 lo, go;
                if (i == 0)
                {
                    lo = new Vector3(0f, tposeWorld.Pos[0].Y, 0f);
                    go = lo;
                }
                else
                {
                    lo = tposeWorld.Pos[i] - tposeWorld.Pos[nodes.Parent[i]];
                    go = tposeWorld.Pos[i] - new Vector3(tposeWorld.Pos[0].X, 0f, tposeWorld.Pos[0].Z);
                }
                WriteNorm3(x, row, ref col, lo, stats.LoM, stats.LoS);
                WriteNorm3(x, row, ref col, go, stats.GoM, stats.GoS);

                // ---- q ------------------------------------------------------------------
                WriteNorm6(x, row, ref col, SixD(localRots[t][i]), stats.QM, stats.QS);

                // ---- p (facing-frame-relative global position) --------------------------
                var p = Vector3.Transform(worlds[t].Pos[i] - fpos, invFacing);
                WriteNorm3(x, row, ref col, p, stats.PM, stats.PS);

                // ---- r (root row only; other rows are the mean → zeros after norm) ------
                if (i == 0)
                {
                    x[row + col++] = (dTheta - stats.RM[0]) / stats.RS[0];
                    x[row + col++] = (dPlanar.X - stats.RM[1]) / stats.RS[1];
                    x[row + col++] = (dPlanar.Z - stats.RM[2]) / stats.RS[2];
                    x[row + col++] = (rootHeight - stats.RM[3]) / stats.RS[3];
                }
                else
                {
                    col += 4; // already zero
                }

                // ---- pv (facing-frame velocity, ×30 fps) ---------------------------------
                var pv = Vector3.Transform(worlds[t].Pos[i] - worlds[t - 1].Pos[i], invFacing) * VelocityFps;
                WriteNorm3(x, row, ref col, pv, stats.PvM, stats.PvS);

                // ---- qv (local rotation delta) -------------------------------------------
                var qv = MathQ.Normalize(Quaternion.Conjugate(localRots[t - 1][i]) * localRots[t][i]);
                WriteNorm6(x, row, ref col, SixD(qv), stats.QvM, stats.QvS);

                // ---- pprev (previous position in the CURRENT facing frame) ---------------
                var pprev = Vector3.Transform(worlds[t - 1].Pos[i] - fpos, invFacing);
                WriteNorm3(x, row, ref col, pprev, stats.PprevM, stats.PprevS);

                // ---- c (ground contact; not normalized) -----------------------------------
                var speedMps = (worlds[t].Pos[i] - worlds[t - 1].Pos[i]).Length() * VelocityFps / 100f;
                x[row + col] = worlds[t].Pos[i].Y < ContactHeightCm && speedMps < ContactSpeedMps ? 1f : 0f;
            }
        }

        var (edgeSrc, edgeDst) = BuildEdges(nodes.Parent, frames);
        var batch = new int[frames * j];
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < j; i++)
                batch[f * j + i] = f;
        }

        AssertFinite(x, "SAME source features");
        return new SameSourceGraph
        {
            X = x,
            EdgeSrc = edgeSrc,
            EdgeDst = edgeDst,
            Batch = batch,
            FrameCount = frames,
            JointCount = j,
            JointNames = nodes.Names,
        };
    }

    // ================================================================ graph topology

    /// <summary>The per-frame graph node set: hips-subtree bones in skeleton order
    /// (hips first) plus synthesized end joints for childless leaves.</summary>
    internal sealed class GraphNodes
    {
        /// <summary>Skeleton bone index per node; -1 for synthesized end joints.</summary>
        public required int[] Bone { get; init; }

        /// <summary>Graph-parent node index; -1 for the root (node 0).</summary>
        public required int[] Parent { get; init; }

        /// <summary>For synthesized end joints: the rest-local offset from the leaf bone
        /// (zero vector for real bones).</summary>
        public required Vector3[] EndOffset { get; init; }

        public required string[] Names { get; init; }

        public int Count => Bone.Length;

        public static GraphNodes Build(SkeletonModel skeleton, int hips)
        {
            // Hips subtree, skeleton order (parents precede children, hips first).
            var inSubtree = new bool[skeleton.Count];
            inSubtree[hips] = true;
            var bones = new List<int> { hips };
            for (var i = hips + 1; i < skeleton.Count; i++)
            {
                var parent = skeleton[i].ParentIndex;
                if (parent >= 0 && inSubtree[parent])
                {
                    inSubtree[i] = true;
                    bones.Add(i);
                }
            }

            var nodeOfBone = new Dictionary<int, int>(bones.Count);
            for (var n = 0; n < bones.Count; n++)
                nodeOfBone[bones[n]] = n;

            var hasChild = new bool[skeleton.Count];
            foreach (var b in bones)
            {
                var parent = skeleton[b].ParentIndex;
                if (parent >= 0 && inSubtree[parent])
                    hasChild[parent] = true;
            }

            var bone = new List<int>(bones);
            var parentNode = new List<int>(bones.Count);
            var endOffset = new List<Vector3>(bones.Count);
            var names = new List<string>(bones.Count);
            foreach (var b in bones)
            {
                var p = skeleton[b].ParentIndex;
                parentNode.Add(b == hips ? -1 : nodeOfBone[p]);
                endOffset.Add(Vector3.Zero);
                names.Add(skeleton[b].Name);
            }

            // Synthesized end joints: leaves with no children anywhere in the skeleton.
            // BVH End Sites already import as real `_end`/`_End` bones and ARE the end
            // joints — no tip on a tip. The tip continues the parent→leaf segment at half
            // length — a neutral stand-in for the unknown bone tail (FBX carries none).
            foreach (var b in bones)
            {
                if (hasChild[b]
                    || skeleton[b].Name.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
                    continue;
                var p = skeleton[b].ParentIndex;
                var segment = p >= 0
                    ? skeleton.RestWorld[b].Pos - skeleton.RestWorld[p].Pos
                    : Vector3.Zero;
                var tip = segment.Length() > 1e-4f ? segment * 0.5f : new Vector3(0f, 2f, 0f);
                // Express in the leaf's rest-local frame (applied via the leaf's world rot).
                var local = Vector3.Transform(tip, Quaternion.Conjugate(skeleton.RestWorld[b].Rot));
                bone.Add(-1);
                parentNode.Add(nodeOfBone[b]);
                endOffset.Add(local);
                names.Add(skeleton[b].Name + "_end");
            }

            return new GraphNodes
            {
                Bone = bone.ToArray(),
                Parent = parentNode.ToArray(),
                EndOffset = endOffset.ToArray(),
                Names = names.ToArray(),
            };
        }
    }

    /// <summary>Aligned world transforms of the graph nodes for one pose.</summary>
    internal readonly struct AlignedFrame
    {
        public required Vector3[] Pos { get; init; }
        public required Quaternion[] Rot { get; init; }
    }

    private static AlignedFrame AlignedWorld(
        SkeletonModel skeleton, XForm[] locals, Quaternion align, GraphNodes nodes)
    {
        var world = new Pose(locals).ToWorld(skeleton);
        var pos = new Vector3[nodes.Count];
        var rot = new Quaternion[nodes.Count];
        for (var n = 0; n < nodes.Count; n++)
        {
            XForm w;
            if (nodes.Bone[n] >= 0)
            {
                w = world[nodes.Bone[n]];
            }
            else
            {
                // Synthesized end joint: rides its leaf bone (identity local rotation).
                var leaf = world[nodes.Bone[nodes.Parent[n]]];
                w = new XForm(leaf.TransformPoint(nodes.EndOffset[n]), leaf.Rot);
            }
            pos[n] = Vector3.Transform(w.Pos, align);
            rot[n] = MathQ.Normalize(align * w.Rot);
        }
        return new AlignedFrame { Pos = pos, Rot = rot };
    }

    /// <summary>Bidirectional parent↔child pairs plus one self-loop per node, replicated
    /// per frame with node indices offset.</summary>
    internal static (int[] Src, int[] Dst) BuildEdges(int[] parent, int frames)
    {
        var j = parent.Length;
        var nonRoot = 0;
        for (var i = 0; i < j; i++)
        {
            if (parent[i] >= 0)
                nonRoot++;
        }
        var perFrame = nonRoot * 2 + j;
        var src = new int[perFrame * frames];
        var dst = new int[perFrame * frames];
        var e = 0;
        for (var f = 0; f < frames; f++)
        {
            var offset = f * j;
            for (var i = 0; i < j; i++)
            {
                if (parent[i] < 0)
                    continue;
                src[e] = offset + parent[i];
                dst[e] = offset + i;
                e++;
                src[e] = offset + i;
                dst[e] = offset + parent[i];
                e++;
            }
            for (var i = 0; i < j; i++)
            {
                src[e] = offset + i;
                dst[e] = offset + i;
                e++;
            }
        }
        return (src, dst);
    }

    // ================================================================ alignment + hips

    /// <summary>Mapped Hips role when available, else the shallowest bone with two or more
    /// children (the hips of any humanoid: the legs/spine branch point).</summary>
    internal static int FindHips(SkeletonModel skeleton, MappingResult? map)
    {
        if (map is not null && map.RoleToBone.TryGetValue(BoneRole.Hips, out var mapped)
            && mapped >= 0 && mapped < skeleton.Count)
            return mapped;

        var childCount = new int[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (skeleton[i].ParentIndex >= 0)
                childCount[skeleton[i].ParentIndex]++;
        }

        var best = -1;
        var bestDepth = int.MaxValue;
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (childCount[i] < 2)
                continue;
            var depth = 0;
            for (var a = skeleton[i].ParentIndex; a >= 0; a = skeleton[a].ParentIndex)
                depth++;
            if (depth < bestDepth)
            {
                best = i;
                bestDepth = depth;
            }
        }
        return best >= 0 ? best : 0;
    }

    /// <summary>
    /// World rotation taking the rig into the canonical SAME frame (X = character left,
    /// Y = up, Z = facing): rest-geometry character frame when computable from the mapping,
    /// else the file's recorded axis conventions.
    /// </summary>
    internal static Quaternion ComputeAlignment(SkeletonModel skeleton, MappingResult? map, SourceScene? scene)
    {
        if (map is not null)
        {
            try
            {
                var frame = CharacterFrame.Compute(skeleton, map, skeleton.RestWorld);
                return AlignFromBasis(frame.Lateral, frame.Up, frame.Forward);
            }
            catch (ArgumentException)
            {
                // fall through to axis metadata
            }
        }

        if (scene is not null)
        {
            var up = AxisVector(scene.UpAxis, scene.UpAxisSign);
            var forward = AxisVector(scene.FrontAxis, scene.FrontAxisSign);
            if (MathF.Abs(Vector3.Dot(up, forward)) < 0.5f)
                return AlignFromBasis(Vector3.Cross(up, forward), up, forward);
        }

        return Quaternion.Identity;
    }

    /// <summary>
    /// Rotation mapping the given (left, up, forward) world directions onto (+X, +Y, +Z),
    /// snapped to the nearest whole axis permutation when one is unambiguous: rigs authored
    /// on exact axes (BVH Y-up/+Z, the s&amp;box rig, Z-up FBX) must map by an exact
    /// quarter-turn — the few degrees of rest-geometry tilt (shoulders not exactly above
    /// hips) otherwise leak into every feature and measurably cost accuracy.
    /// </summary>
    internal static Quaternion AlignFromBasis(Vector3 left, Vector3 up, Vector3 forward)
    {
        var l = SnapAxis(left);
        var u = SnapAxis(up);
        var f = SnapAxis(forward);
        if (MathF.Abs(Vector3.Dot(l, u)) > 0.5f || MathF.Abs(Vector3.Dot(l, f)) > 0.5f
            || MathF.Abs(Vector3.Dot(u, f)) > 0.5f)
        {
            // Genuinely oblique rig: keep the exact (orthonormalized) directions.
            l = Vector3.Normalize(left);
            u = Vector3.Normalize(up - l * Vector3.Dot(up, l));
            f = Vector3.Cross(l, u);
        }

        // Row-major with rows = basis images maps +X→left, +Y→up, +Z→forward
        // (System.Numerics row-vector convention); the alignment is its inverse.
        var m = new Matrix4x4(
            l.X, l.Y, l.Z, 0f,
            u.X, u.Y, u.Z, 0f,
            f.X, f.Y, f.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Conjugate(MathQ.Normalize(Quaternion.CreateFromRotationMatrix(m)));
    }

    private static Vector3 SnapAxis(Vector3 v)
    {
        var ax = MathF.Abs(v.X);
        var ay = MathF.Abs(v.Y);
        var az = MathF.Abs(v.Z);
        if (ax >= ay && ax >= az)
            return new Vector3(MathF.Sign(v.X), 0f, 0f);
        if (ay >= az)
            return new Vector3(0f, MathF.Sign(v.Y), 0f);
        return new Vector3(0f, 0f, MathF.Sign(v.Z));
    }

    private static Vector3 AxisVector(int axis, int sign) => axis switch
    {
        0 => new Vector3(sign, 0f, 0f),
        2 => new Vector3(0f, 0f, sign),
        _ => new Vector3(0f, sign, 0f),
    };

    // ================================================================ small math

    /// <summary>The yaw (rotation about +Y) closest to <paramref name="q"/> — fairmotion's
    /// <c>Q_closest(q, identity, +Y)</c>, reproduced exactly for parity.</summary>
    internal static float YawAngle(Quaternion q)
    {
        var alpha = Math.Atan2(q.W, q.Y);
        var theta1 = -2.0 * alpha + Math.PI;
        var theta2 = -2.0 * alpha - Math.PI;
        var d1 = q.Y * Math.Sin(theta1 * 0.5) + q.W * Math.Cos(theta1 * 0.5);
        var d2 = q.Y * Math.Sin(theta2 * 0.5) + q.W * Math.Cos(theta2 * 0.5);
        return (float)(d1 > d2 ? theta1 : theta2);
    }

    private static void ShiftToGround(Vector3[] positions)
    {
        var ground = float.PositiveInfinity;
        foreach (var p in positions)
            ground = MathF.Min(ground, p.Y);
        if (!float.IsFinite(ground) || ground == 0f)
            return;
        for (var i = 0; i < positions.Length; i++)
            positions[i].Y -= ground;
    }

    internal static float WrapPi(float angle)
    {
        while (angle > MathF.PI)
            angle -= 2f * MathF.PI;
        while (angle < -MathF.PI)
            angle += 2f * MathF.PI;
        return angle;
    }

    /// <summary>6D rotation representation: the first two columns of the rotation matrix
    /// (<c>R·e_x</c> then <c>R·e_y</c>).</summary>
    internal static (Vector3 C0, Vector3 C1) SixD(Quaternion q)
        => (Vector3.Transform(Vector3.UnitX, q), Vector3.Transform(Vector3.UnitY, q));

    private static void WriteNorm3(float[] x, int row, ref int col, Vector3 v, float[] m, float[] s)
    {
        x[row + col++] = (v.X - m[0]) / s[0];
        x[row + col++] = (v.Y - m[1]) / s[1];
        x[row + col++] = (v.Z - m[2]) / s[2];
    }

    private static void WriteNorm6(float[] x, int row, ref int col, (Vector3 C0, Vector3 C1) sixD, float[] m, float[] s)
    {
        x[row + col++] = (sixD.C0.X - m[0]) / s[0];
        x[row + col++] = (sixD.C0.Y - m[1]) / s[1];
        x[row + col++] = (sixD.C0.Z - m[2]) / s[2];
        x[row + col++] = (sixD.C1.X - m[3]) / s[3];
        x[row + col++] = (sixD.C1.Y - m[4]) / s[4];
        x[row + col++] = (sixD.C1.Z - m[5]) / s[5];
    }

    internal static void AssertFinite(float[] values, string what)
    {
        foreach (var v in values)
        {
            if (!float.IsFinite(v))
                throw new InvalidOperationException($"{what} contain non-finite values.");
        }
    }
}
