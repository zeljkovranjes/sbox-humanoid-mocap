#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Formats.Fbx;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Formats.Gltf;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Options for <see cref="GltfImporter.Import"/>.</summary>
public sealed class GltfImportOptions
{
    /// <summary>Use authored skin binds as the target rest instead of a potentially posed
    /// scene state when the skins share a compatible bind space. Animation samples
    /// remain in their original node hierarchy.</summary>
    public bool UseSkinBindPose { get; init; }
    /// <summary>Fixed resampling rate for all clips, frames per second.</summary>
    public float SampleFps { get; init; } = 30f;

    /// <summary>
    /// Optional reader for external buffer URIs in plain <c>.gltf</c> files. The core
    /// importer remains file-system agnostic; IO-owning callers can resolve paths relative
    /// to the picked document. Null preserves the self-contained GLB/data-URI-only policy.
    /// </summary>
    public Func<string, byte[]>? ExternalBufferResolver { get; init; }
}

/// <summary>
/// glTF 2.0 (.glb / .gltf) → <see cref="SourceScene"/> importer. Pure managed, no
/// packages, no file IO: GLB containers and base64 <c>data:</c>-URI buffers are supported;
/// an IO-owning caller can provide a resolver for external buffer files.
/// </summary>
/// <remarks>
/// <para><b>Skeleton selection</b>: the kept node set is the union of all skins' joints and
/// all animation-channel targets (rotation/translation paths), plus every ancestor of those
/// — multi-root is fine. Mesh-only leaves outside that closure are excluded. The REST pose
/// is each node's own TRS (or decomposed <c>matrix</c>); skin <c>inverseBindMatrices</c> are
/// ignored by default — node TRS is the rest pose of the node hierarchy the animations
/// drive. Custom targets may opt into <see cref="GltfImportOptions.UseSkinBindPose"/>.</para>
/// <para><b>Units/axes</b>: glTF is meters, Y-up, right-handed (assets face +Z). All
/// translations are converted to centimeters (×100, <see cref="SourceScene.UnitScaleCm"/> =
/// 100); axes are preserved and recorded on the scene like the other importers (up = Y,
/// front = Z, coord = X).</para>
/// <para><b>Animation</b>: every glTF animation becomes one clip (multi-take). Channels are
/// resampled onto the <see cref="GltfImportOptions.SampleFps"/> grid over the union key
/// range of the skeleton's channels. Interpolation: LINEAR lerps positions and slerps
/// rotations between the bracketing keys (per spec), STEP holds the earlier key.
/// CUBICSPLINE is evaluated as a documented simplification: only each key's VALUE element
/// is used (in/out tangents ignored) with LINEAR interpolation between keys — exact at the
/// keys themselves, and the 30 fps resampling grid makes the residual between-key error
/// negligible for mocap-density data. Scale and morph-weight channels are ignored.
/// Unanimated properties fall back to the node's rest TRS. Sampled rotations are
/// hemisphere-aligned per bone (<see cref="QuaternionContinuity"/>) like the FBX
/// importer.</para>
/// </remarks>
public static class GltfImporter
{
    /// <summary>Meters → centimeters.</summary>
    private const float UnitScale = 100f;

    /// <summary>Maximum key-time span (seconds) a single animation may cover — one hour is
    /// far beyond any real clip; larger spans are treated as malformed data (the resampled
    /// frame count is span × fps, so an unbounded span is an allocation bomb).</summary>
    private const double MaxClipDurationSeconds = 3600;

    /// <summary>Parses glTF/GLB bytes and builds the source scene.</summary>
    /// <exception cref="FormatException">Malformed/truncated file, unresolved external
    /// buffer URIs, or no skeleton-like nodes (no skin joints and no animated nodes).</exception>
    public static SourceScene Import(byte[] data, GltfImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new GltfImportOptions();
        if (!(options.SampleFps > 0f) || !float.IsFinite(options.SampleFps))
            throw new ArgumentOutOfRangeException(nameof(options), "SampleFps must be positive.");

        var document = GltfDocument.Parse(data, options.ExternalBufferResolver);

        var bones = SelectSkeletonNodes(document);
        if (bones.Count == 0)
            throw new FormatException(
                "glTF contains no skeleton nodes (no skin joints and no animated nodes).");

        var ctx = new ImportContext(document, bones);

        // ---- rest pose: node TRS, worlds chained, rigid locals in cm ----------------
        var skeleton = BuildSkeleton(ctx, WorldsToLocals(ctx, EvaluateRestWorlds(ctx)));
        if (options.UseSkinBindPose)
            skeleton = GltfModelDmxWriter.WithSkinBindPose(document, skeleton);

        // ---- clips: one per glTF animation ------------------------------------------
        var clips = new List<Clip>();
        for (int i = 0; i < document.Animations.Count; i++)
        {
            var clip = SampleClip(ctx, skeleton, document.Animations[i], i, options.SampleFps);
            if (clip is not null)
                clips.Add(clip);
        }

        // glTF spec axes: Y-up (1), Z-front (2), X-coord (0) — recorded, not converted.
        var scene = new SourceScene(
            skeleton, clips, UnitScale,
            upAxis: 1, upAxisSign: 1,
            frontAxis: 2, frontAxisSign: 1,
            coordAxis: 0, coordAxisSign: 1,
            originalUpAxis: -1);

        // VRM (.vrm = glTF + VRM extension): the file's authored humanoid.humanBones map
        // becomes a pre-resolved Authored mapping (node index → skeleton bone index).
        scene.AuthoredMapping = VrmHumanoid.BuildMapping(document, nodeIndex =>
            ctx.BoneIndexByNode.TryGetValue(nodeIndex, out var bone)
                ? skeleton.IndexOf(ctx.BoneNames[bone])
                : -1);
        return scene;
    }

    // =====================================================================================
    // skeleton node selection
    // =====================================================================================

    /// <summary>
    /// Kept set = skins[].joints ∪ animated nodes ∪ VRM humanoid bones ∪ their ancestors,
    /// returned in parent-before-child order (DFS from the roots, document order among
    /// siblings, recursing THROUGH non-kept nodes — re-parenting to the nearest kept
    /// ancestor happens in <see cref="ImportContext"/>).
    /// </summary>
    private static List<int> SelectSkeletonNodes(GltfDocument document)
    {
        var nodes = document.Nodes;
        var kept = new HashSet<int>(document.SkinJoints);
        foreach (var animation in document.Animations)
            foreach (var channel in animation.Channels)
                kept.Add(channel.NodeIndex);
        if (document.VrmHumanBones is { } vrmBones)
        {
            // VRM humanoid bones are authored skeleton ground truth — always part of the
            // imported skeleton even when a (malformed) file forgets to skin one of them.
            foreach (var nodeIndex in vrmBones.Values)
                kept.Add(nodeIndex);
        }

        // Close over ancestors so disjoint subtrees stay connected to their roots.
        // (Cycle-safe: kept doubles as the walk's visited set — a malformed parent cycle
        // revisits a kept node and breaks instead of looping forever.)
        foreach (var index in new List<int>(kept))
        {
            for (int parent = nodes[index].Parent; parent >= 0; parent = nodes[parent].Parent)
            {
                if (!kept.Add(parent))
                    break;
            }
        }

        // Iterative DFS with a visited set over ALL nodes (kept or not): a malformed node
        // graph whose children lists form a cycle would otherwise recurse unboundedly
        // (StackOverflow takes the host process down uncatchably). Per spec each node has at
        // most one parent, so ANY revisit means the graph is cyclic/malformed — fail cleanly.
        var result = new List<int>(kept.Count);
        var visited = new HashSet<int>();
        var stack = new Stack<int>();

        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i].Parent < 0)
                stack.Push(i);
        }
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            if (!visited.Add(index))
                throw new FormatException("glTF: node graph contains a cycle.");
            if (kept.Contains(index))
                result.Add(index);
            var children = nodes[index].Children;
            for (int c = children.Length - 1; c >= 0; c--)
                stack.Push(children[c]); // reversed: document order among siblings
        }
        return result;
    }

    // =====================================================================================
    // evaluation
    // =====================================================================================

    /// <summary>Per-import precomputed state (mirrors the FBX importer's context).</summary>
    private sealed class ImportContext
    {
        public GltfDocument Document { get; }
        public List<int> Bones { get; }                    // node indices, parent-before-child
        public Dictionary<int, int> BoneIndexByNode { get; } = new();
        public int[] ParentIndex { get; }                  // index into Bones, -1 for roots
        public string[] BoneNames { get; }                 // deduplicated

        public ImportContext(GltfDocument document, List<int> bones)
        {
            Document = document;
            Bones = bones;
            ParentIndex = new int[bones.Count];
            BoneNames = new string[bones.Count];

            for (int i = 0; i < bones.Count; i++)
                BoneIndexByNode[bones[i]] = i;

            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < bones.Count; i++)
            {
                var node = document.Nodes[bones[i]];

                // Nearest KEPT ancestor: the kept set can be non-contiguous (e.g. an
                // animated node under a mesh-only node) — re-parent across the gap.
                // Visited-guarded: a malformed parent cycle among non-bone nodes (possible
                // even when the bone itself is reachable acyclically) would otherwise walk
                // forever — fail with the same clean cycle error as the node DFS.
                ParentIndex[i] = -1;
                var walked = new HashSet<int>();
                for (int parent = node.Parent; parent >= 0; parent = document.Nodes[parent].Parent)
                {
                    if (!walked.Add(parent))
                        throw new FormatException("glTF: node graph contains a cycle.");
                    if (BoneIndexByNode.TryGetValue(parent, out var boneIndex))
                    {
                        ParentIndex[i] = boneIndex;
                        break;
                    }
                }

                var name = string.IsNullOrEmpty(node.Name) ? $"node_{bones[i]}" : node.Name!;
                if (!usedNames.Add(name))
                {
                    name = $"{name}#{bones[i]}";
                    usedNames.Add(name);
                }
                BoneNames[i] = name;
            }
        }

        /// <summary>Local matrix from TRS (row-vector convention: scale, then rotate, then translate).</summary>
        public static Matrix4x4 LocalMatrix(Vector3 translation, Quaternion rotation, Vector3 scale)
            => Matrix4x4.CreateScale(scale)
               * Matrix4x4.CreateFromQuaternion(rotation)
               * Matrix4x4.CreateTranslation(translation);
    }

    private static Matrix4x4[] EvaluateRestWorlds(ImportContext ctx)
    {
        var worlds = new Matrix4x4[ctx.Bones.Count];
        for (int i = 0; i < ctx.Bones.Count; i++)
        {
            var node = ctx.Document.Nodes[ctx.Bones[i]];
            var local = ImportContext.LocalMatrix(node.Translation, node.Rotation, node.Scale);
            int parent = ctx.ParentIndex[i];
            worlds[i] = parent < 0 ? local : local * worlds[parent];
        }
        return worlds;
    }

    /// <summary>Derives rigid (cm) parent-relative locals from world matrices, in bone order
    /// — identical math to the FBX importer (ancestor scale folds into child positions).</summary>
    private static XForm[] WorldsToLocals(ImportContext ctx, Matrix4x4[] worlds)
    {
        var rigid = new XForm[worlds.Length];
        for (int i = 0; i < worlds.Length; i++)
            rigid[i] = FbxTransform.ToRigid(worlds[i]);

        var locals = new XForm[worlds.Length];
        for (int i = 0; i < worlds.Length; i++)
        {
            int parent = ctx.ParentIndex[i];
            var local = parent < 0 ? rigid[i] : XForm.ToLocal(rigid[parent], rigid[i]);
            local.Pos *= UnitScale;
            locals[i] = local;
        }
        return locals;
    }

    private static Skeleton.Skeleton BuildSkeleton(ImportContext ctx, XForm[] locals)
    {
        var defs = new List<BoneDefinition>(ctx.Bones.Count);
        for (int i = 0; i < ctx.Bones.Count; i++)
        {
            int parent = ctx.ParentIndex[i];
            defs.Add(new BoneDefinition(
                ctx.BoneNames[i],
                parent < 0 ? null : ctx.BoneNames[parent],
                locals[i]));
        }
        return Skeleton.Skeleton.Create(defs);
    }

    // =====================================================================================
    // clips
    // =====================================================================================

    /// <summary>
    /// Samples one glTF animation onto the fps grid over the union key range of the channels
    /// that drive skeleton nodes. Returns null when the animation drives none of them.
    /// </summary>
    private static Clip? SampleClip(
        ImportContext ctx, Skeleton.Skeleton skeleton, GltfAnimation animation, int animationIndex, float fps)
    {
        // Per-bone channel lookup + overall key-time range.
        var rotation = new GltfChannel?[ctx.Bones.Count];
        var translation = new GltfChannel?[ctx.Bones.Count];
        float start = float.MaxValue, stop = float.MinValue;
        foreach (var channel in animation.Channels)
        {
            if (!ctx.BoneIndexByNode.TryGetValue(channel.NodeIndex, out var bone))
                continue;
            if (channel.IsRotation)
                rotation[bone] ??= channel;
            else
                translation[bone] ??= channel;
            start = MathF.Min(start, channel.Times[0]);
            stop = MathF.Max(stop, channel.Times[^1]);
        }
        if (start > stop)
            return null;

        // The frame count is derived from the file's key-time span: a malicious/corrupt span
        // (two keys [0, 1e7] → 300M frames at 30 fps) would OOM-allocate, and an unchecked
        // double→int conversion can wrap negative and get masked to 1 frame. Cap the clip
        // duration and convert under an explicit overflow guard — FormatException keeps the
        // importer's malformed-file contract.
        double duration = (double)stop - start;
        if (double.IsNaN(duration) || duration > MaxClipDurationSeconds)
            throw new FormatException(
                $"glTF: animation key-time span {duration:0.###} s (keys {start:0.###}..{stop:0.###} s) "
                + $"exceeds the supported maximum of {MaxClipDurationSeconds} s.");

        double frameEstimate = Math.Round(duration * fps) + 1;
        if (!(frameEstimate < int.MaxValue))
            throw new FormatException(
                $"glTF: animation key-time span {duration:0.###} s at {fps} fps yields an "
                + "unrepresentable frame count.");
        int frameCount = Math.Max(1, (int)frameEstimate);

        // Skeleton bone order may differ from context bone order (topological sort) — map.
        var boneToSkeleton = new int[ctx.Bones.Count];
        for (int i = 0; i < ctx.Bones.Count; i++)
            boneToSkeleton[i] = skeleton.IndexOf(ctx.BoneNames[i]);

        var frames = new List<XForm[]>(frameCount);
        var worlds = new Matrix4x4[ctx.Bones.Count];
        for (int f = 0; f < frameCount; f++)
        {
            float t = start + (float)(f / (double)fps);

            for (int i = 0; i < ctx.Bones.Count; i++)
            {
                var node = ctx.Document.Nodes[ctx.Bones[i]];
                var pos = translation[i] is { } tc ? SampleVec3(tc, t) : node.Translation;
                var rot = rotation[i] is { } rc ? SampleQuat(rc, t) : node.Rotation;
                var local = ImportContext.LocalMatrix(pos, rot, node.Scale);
                int parent = ctx.ParentIndex[i];
                worlds[i] = parent < 0 ? local : local * worlds[parent];
            }

            var locals = WorldsToLocals(ctx, worlds);
            var frame = new XForm[skeleton.Count];
            for (int i = 0; i < locals.Length; i++)
                frame[boneToSkeleton[i]] = locals[i];
            frames.Add(frame);
        }

        // Per-frame matrix→quaternion conversion can flip hemisphere between consecutive
        // frames; align signs per bone so downstream interpolation never spins the long way.
        QuaternionContinuity.AlignFrames(frames);

        var name = string.IsNullOrEmpty(animation.Name)
            ? (animationIndex == 0 ? "clip" : $"clip_{animationIndex + 1}")
            : animation.Name!;
        return new Clip(name, fps, looping: false, frames);
    }

    // =====================================================================================
    // channel sampling
    // =====================================================================================

    /// <summary>Bracketing keys for time <paramref name="t"/>: lo ≤ t ≤ hi with blend u;
    /// clamps outside the key range (constant extrapolation, per spec).</summary>
    private static (int Lo, int Hi, float U) Bracket(float[] times, float t)
    {
        int n = times.Length;
        if (t <= times[0])
            return (0, 0, 0f);
        if (t >= times[n - 1])
            return (n - 1, n - 1, 0f);

        int hi = Array.BinarySearch(times, t);
        if (hi >= 0)
            return (hi, hi, 0f);
        hi = ~hi; // first index with time > t; in (0, n-1] here
        int lo = hi - 1;
        float span = times[hi] - times[lo];
        float u = span <= 0f ? 0f : (t - times[lo]) / span;
        return (lo, hi, u);
    }

    /// <summary>Index of key <paramref name="key"/>'s VALUE element in the channel's flat
    /// value array (CUBICSPLINE keys are [in-tangent, value, out-tangent] triplets — the
    /// value is the middle element; see class remarks for the documented simplification).</summary>
    private static int ValueOffset(GltfChannel channel, int key)
        => (key * channel.ElementsPerKey + (channel.ElementsPerKey == 3 ? 1 : 0)) * channel.Comps;

    private static Vector3 SampleVec3(GltfChannel channel, float t)
    {
        var (lo, hi, u) = Bracket(channel.Times, t);
        var a = ReadVec3(channel, lo);
        if (hi == lo || channel.Interpolation == "STEP")
            return a;
        return Vector3.Lerp(a, ReadVec3(channel, hi), u);
    }

    private static Quaternion SampleQuat(GltfChannel channel, float t)
    {
        var (lo, hi, u) = Bracket(channel.Times, t);
        var a = ReadQuat(channel, lo);
        if (hi == lo || channel.Interpolation == "STEP")
            return a;
        // Quaternion.Slerp takes the short way (negates on dot < 0), matching the spec's
        // spherical linear interpolation requirement.
        return MathQ.Normalize(Quaternion.Slerp(a, ReadQuat(channel, hi), u));
    }

    private static Vector3 ReadVec3(GltfChannel channel, int key)
    {
        int at = ValueOffset(channel, key);
        return new Vector3(channel.Values[at], channel.Values[at + 1], channel.Values[at + 2]);
    }

    private static Quaternion ReadQuat(GltfChannel channel, int key)
    {
        int at = ValueOffset(channel, key);
        return MathQ.Normalize(new Quaternion(
            channel.Values[at], channel.Values[at + 1], channel.Values[at + 2], channel.Values[at + 3]));
    }
}
