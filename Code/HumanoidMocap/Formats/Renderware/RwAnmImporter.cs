#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Formats.Renderware;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Options for <see cref="RwAnmImporter.Import"/>.</summary>
public sealed class RwAnmImportOptions
{
    /// <summary>Fixed resampling rate for the motion data, frames per second.</summary>
    public float SampleFps { get; init; } = 30f;

    /// <summary>
    /// Base clip name. RenderWare animations carry no take names, so a single-take file
    /// yields one clip named exactly this and a multi-take bank (.an5) yields
    /// <c>&lt;base&gt;_1</c>, <c>&lt;base&gt;_2</c>, … The pipeline passes the source file
    /// stem here. Default "motion" (the BVH importer's convention — the facade's clip
    /// naming then substitutes the file stem).
    /// </summary>
    public string ClipNameBase { get; init; } = "motion";
}

/// <summary>
/// RenderWare humanoid animation importer: <c>.anm</c> single clips and FSB2 <c>.an5</c>
/// animation banks → <see cref="SourceScene"/>. The animation files carry NO skeleton —
/// the companion model's .dff bytes must be supplied (see
/// <see cref="RwDffSkeleton"/>).
/// </summary>
/// <remarks>
/// <para><b>.anm</b> = one RwAnimAnimation chunk (0x1B): {u32 animVersion=0x100,
/// u32 interpTypeId, u32 numKeyframes, u32 flags, f32 duration} + keyframes.</para>
/// <para><b>.an5</b> (FSB2 container, mapped empirically over all 307 bank files) = a
/// sequence of top-level 0x1B chunks, one per TAKE, each shaped {u32 0x600 (container
/// version stamp — constant over the whole corpus), nested RwAnimAnimation chunks, marker
/// table {u32 count, count × {char[32] name, f32 time}, u32 0}}. Every FSB2 take carries
/// exactly two animations: a 3-node full-TRS anim (root / ball / hips — the only
/// translating nodes) plus a rotation-only anim covering all 71 nodes; the markers are
/// gameplay event tags (BLOCKSTART, SHOOTOUT, …), not clip names — takes are therefore
/// named <c>&lt;base&gt;_&lt;index&gt;</c>.</para>
/// <para><b>Keyframe layouts</b> by interpTypeId: <c>1</c> = 36-byte
/// {f32 time, f32 quat[4] (x,y,z,w), f32 trans[3], i32 prevOffset}; <c>0x100</c> = 24-byte
/// rotation-only {f32 time, f32 quat[4], i32 prevOffset}. Keyframes belong to hierarchy
/// nodes positionally for the leading time-0 block (node 0..N-1) and via
/// <c>prevOffset</c> (byte offset of the same node's previous keyframe) afterwards — the
/// stored prevOffset of the time-0 block itself is writer pointer garbage and is ignored.</para>
/// <para><b>Sampling</b>: per node, keys are slerped/lerped onto the fixed
/// <see cref="RwAnmImportOptions.SampleFps"/> grid; nodes an anim does not cover hold the
/// .dff rest transform, rotation-only nodes hold the rest translation (bone lengths come
/// from the model, exactly like the runtime plays these).</para>
/// <para><b>Units/axes</b>: FSB2 data is centimeters, Y-up (pelvis rest ≈ (0, 98.7, 2.8),
/// head ≈ 184 — human cm proportions), so translations pass through unscaled; a BVH-style
/// meters heuristic (rest height &lt; 10 → ×100) guards other RenderWare sources. Native
/// axes are preserved and recorded as Y-up / Z-front, matching the BVH/FBX policy.</para>
/// </remarks>
public static class RwAnmImporter
{
    private const uint FsbTakeContainerStamp = 0x600;
    private const uint InterpUncompressedTrs = 1;      // 36-byte keyframes
    private const uint InterpRotationOnly = 0x100;     // 24-byte keyframes (FSB2 scheme)
    private const float MeterHeightThreshold = 10f;

    /// <summary>
    /// Parses .anm/.an5 bytes against the companion .dff skeleton and builds the source
    /// scene (one clip per take).
    /// </summary>
    /// <param name="animData">Raw .anm or .an5 bytes.</param>
    /// <param name="skeletonData">Raw bytes of the companion model .dff (the animation
    /// itself has no skeleton). Null throws the instructive error below.</param>
    /// <param name="options">Sampling/naming options.</param>
    /// <exception cref="FormatException">Malformed stream, node-count mismatch against the
    /// skeleton, or missing skeleton data.</exception>
    public static SourceScene Import(
        byte[] animData, byte[]? skeletonData, RwAnmImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(animData);
        options ??= new RwAnmImportOptions();
        if (!(options.SampleFps > 0f) || !float.IsFinite(options.SampleFps))
            throw new ArgumentOutOfRangeException(nameof(options), "SampleFps must be positive.");

        if (skeletonData is null)
            throw new FormatException(
                "RenderWare animations (.anm/.an5) carry no skeleton. Place the character's "
                + "model .dff next to the animation file (or in its parent folder), or pick "
                + "the skeleton file explicitly.");

        var dff = RwDffSkeleton.Parse(skeletonData);
        var takes = ParseTakes(animData);
        if (takes.Count == 0)
            throw new FormatException("RenderWare animation contains no takes.");

        foreach (var take in takes)
        {
            foreach (var anim in take.Anims)
            {
                if (anim.NodeCount > dff.Nodes.Count)
                    throw new FormatException(
                        $"RenderWare animation drives {anim.NodeCount} nodes but the skeleton "
                        + $".dff has only {dff.Nodes.Count} — the model does not match this "
                        + "animation (pick the correct .dff).");
            }
        }

        // ---- skeleton (unit heuristic guards non-cm RenderWare sources) -------------------
        var unitScale = HeuristicUnitScale(dff);
        var defs = new List<BoneDefinition>(dff.Nodes.Count);
        foreach (var node in dff.Nodes)
        {
            defs.Add(new BoneDefinition(
                node.Name,
                node.ParentIndex < 0 ? null : dff.Nodes[node.ParentIndex].Name,
                new XForm(node.RestLocal.Pos * unitScale, node.RestLocal.Rot)));
        }
        var skeleton = Skeleton.Skeleton.Create(defs);

        // ---- clips -------------------------------------------------------------------------
        var clips = new List<Clip>(takes.Count);
        for (var t = 0; t < takes.Count; t++)
        {
            var name = takes.Count == 1
                ? options.ClipNameBase
                : $"{options.ClipNameBase}_{t + 1}";
            clips.Add(ResampleTake(takes[t], dff, skeleton, unitScale, options.SampleFps, name));
        }

        // FSB2 conventional axes: Y-up, Z-front, X-coord — recorded, not converted (same
        // policy as BVH).
        return new SourceScene(
            skeleton, clips, unitScale,
            upAxis: 1, upAxisSign: 1,
            frontAxis: 2, frontAxisSign: 1,
            coordAxis: 0, coordAxisSign: 1,
            originalUpAxis: -1);
    }

    /// <summary>
    /// Cheap probe used by skeleton resolution: the largest node count any contained
    /// animation drives (== the skeleton node count the file was authored against), or null
    /// when the bytes are not a parseable RenderWare animation. Never throws.
    /// </summary>
    public static int? PeekNodeCount(byte[] animData)
    {
        try
        {
            var takes = ParseTakes(animData);
            var max = 0;
            foreach (var take in takes)
            {
                foreach (var anim in take.Anims)
                    max = Math.Max(max, anim.NodeCount);
            }
            return max > 0 ? max : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentNullException)
        {
            return null;
        }
    }

    /// <summary>Number of takes in .anm/.an5 bytes (1 for .anm), or 0 when unparseable.</summary>
    public static int PeekTakeCount(byte[] animData)
    {
        try
        {
            return ParseTakes(animData).Count;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    /// <summary>Content sniff: does this look like a RenderWare animation stream (.anm
    /// single clip or .an5 take container)? Checks the leading chunk header and payload
    /// stamp only.</summary>
    public static bool LooksLikeRenderwareAnim(byte[] data)
    {
        if (data is null || data.Length < 16)
            return false;
        var type = RwStream.U32(data, 0);
        var size = RwStream.U32(data, 4);
        if (type != RwStream.ChunkAnimAnimation || size > int.MaxValue || 12 + size > data.Length)
            return false;
        var stamp = RwStream.U32(data, 12);
        return stamp == 0x100 || stamp == FsbTakeContainerStamp;
    }

    // ================================================================ stream parsing

    private sealed class RwAnim
    {
        public required uint Interp;
        public required float Duration;
        /// <summary>Per NODE keys, time-ascending. Trans is null on rotation-only keys.</summary>
        public required List<(float Time, Quaternion Rot, Vector3? Trans)>[] NodeKeys;
        public int NodeCount => NodeKeys.Length;
    }

    private sealed class RwTake
    {
        public required List<RwAnim> Anims;
        public float Duration
        {
            get
            {
                var d = 0f;
                foreach (var anim in Anims)
                    d = MathF.Max(d, anim.Duration);
                return d;
            }
        }
    }

    /// <summary>Walks the top-level chunk sequence: plain RwAnimAnimation payloads (stamp
    /// 0x100) become single-anim takes (.anm); FSB2 take containers (stamp 0x600) are
    /// unpacked into their nested animations (.an5). Both shapes may repeat.</summary>
    private static List<RwTake> ParseTakes(byte[] data)
    {
        var takes = new List<RwTake>();
        var offset = 0;
        while (offset < data.Length)
        {
            var (type, payloadStart, payloadSize) = RwStream.ReadChunk(data, offset, data.Length);
            if (type != RwStream.ChunkAnimAnimation)
                throw new FormatException(
                    $"RenderWare animation: unexpected top-level chunk 0x{type:X} at offset {offset}.");
            var payloadEnd = payloadStart + payloadSize;

            var stamp = RwStream.U32(data, payloadStart);
            if (stamp == FsbTakeContainerStamp)
                takes.Add(ParseTakeContainer(data, payloadStart + 4, payloadEnd));
            else
                takes.Add(new RwTake
                {
                    Anims = new List<RwAnim> { ParseAnim(data, payloadStart, payloadEnd) },
                });

            offset = payloadEnd;
        }
        return takes;
    }

    /// <summary>One .an5 take container body: nested anim chunks, then the marker table
    /// (skipped — gameplay event tags, validated structurally so corruption is caught).</summary>
    private static RwTake ParseTakeContainer(byte[] data, int start, int end)
    {
        var anims = new List<RwAnim>();
        var offset = start;
        while (offset + 12 <= end)
        {
            var type = RwStream.U32(data, offset);
            if (type != RwStream.ChunkAnimAnimation)
                break; // marker table reached
            var (_, payloadStart, payloadSize) = RwStream.ReadChunk(data, offset, end);
            anims.Add(ParseAnim(data, payloadStart, payloadStart + payloadSize));
            offset = payloadStart + payloadSize;
        }
        if (anims.Count == 0)
            throw new FormatException("RenderWare .an5 take contains no animations.");

        if (offset < end)
        {
            // Marker table: {u32 count, count × {char[32] name, f32 time}, u32 0}.
            var count = RwStream.I32(data, offset);
            if (count < 0 || offset + 4 + count * 36 + 4 != end)
                throw new FormatException(
                    "RenderWare .an5 take has trailing data that is not a marker table.");
        }
        return new RwTake { Anims = anims };
    }

    private static RwAnim ParseAnim(byte[] data, int start, int end)
    {
        if (end - start < 20)
            throw new FormatException("RenderWare animation chunk is too small.");
        var version = RwStream.U32(data, start);
        var interp = RwStream.U32(data, start + 4);
        var numKeyframes = RwStream.I32(data, start + 8);
        var duration = RwStream.F32(data, start + 16);
        if (version != 0x100)
            throw new FormatException($"RenderWare animation has unknown version 0x{version:X}.");
        if (numKeyframes < 0)
            throw new FormatException("RenderWare animation declares a negative keyframe count.");
        if (!(duration >= 0f) || !float.IsFinite(duration))
            throw new FormatException($"RenderWare animation has invalid duration {duration}.");

        var stride = interp switch
        {
            InterpUncompressedTrs => 36,
            InterpRotationOnly => 24,
            _ => throw new FormatException(
                $"RenderWare animation uses unknown interpolation scheme {interp}."),
        };
        var keysStart = start + 20;
        if (keysStart + numKeyframes * stride != end)
            throw new FormatException(
                $"RenderWare animation payload size mismatch: {numKeyframes} keyframes × "
                + $"{stride} bytes ≠ {end - keysStart} payload bytes.");

        // ---- node assignment: leading time-0 block is positional; the rest chain through
        // prevOffset (byte offset of the same node's previous keyframe). ----
        var nodeOfKey = new int[numKeyframes];
        var nodeCount = 0;
        while (nodeCount < numKeyframes && RwStream.F32(data, keysStart + nodeCount * stride) == 0f)
            nodeCount++;
        if (nodeCount == 0 && numKeyframes > 0)
            throw new FormatException("RenderWare animation has no time-0 keyframe block.");

        var nodeKeys = new List<(float, Quaternion, Vector3?)>[nodeCount];
        for (var n = 0; n < nodeCount; n++)
            nodeKeys[n] = new List<(float, Quaternion, Vector3?)>();

        for (var i = 0; i < numKeyframes; i++)
        {
            var p = keysStart + i * stride;
            var time = RwStream.F32(data, p);
            if (!float.IsFinite(time) || time < 0f)
                throw new FormatException($"RenderWare keyframe {i} has invalid time {time}.");

            int node;
            if (i < nodeCount)
            {
                node = i; // positional; stored prevOffset here is writer garbage
            }
            else
            {
                var prev = RwStream.I32(data, p + stride - 4);
                if (prev < 0 || prev % stride != 0 || prev / stride >= i)
                    throw new FormatException(
                        $"RenderWare keyframe {i} has invalid previous-keyframe offset {prev}.");
                node = nodeOfKey[prev / stride];
            }
            nodeOfKey[i] = node;

            var rot = new Quaternion(
                RwStream.F32(data, p + 4), RwStream.F32(data, p + 8),
                RwStream.F32(data, p + 12), RwStream.F32(data, p + 16));
            if (!float.IsFinite(rot.X) || !float.IsFinite(rot.Y)
                || !float.IsFinite(rot.Z) || !float.IsFinite(rot.W))
                throw new FormatException($"RenderWare keyframe {i} has a non-finite rotation.");

            Vector3? trans = null;
            if (stride == 36)
            {
                var v = new Vector3(
                    RwStream.F32(data, p + 20), RwStream.F32(data, p + 24), RwStream.F32(data, p + 28));
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
                    throw new FormatException($"RenderWare keyframe {i} has a non-finite translation.");
                trans = v;
            }

            nodeKeys[node].Add((time, MathQ.Normalize(rot), trans));
        }

        // Keys of one node arrive time-ascending in these files, but sort defensively —
        // the sampler requires it.
        foreach (var keys in nodeKeys)
            keys.Sort((a, b) => a.Item1.CompareTo(b.Item1));

        return new RwAnim { Interp = interp, Duration = duration, NodeKeys = nodeKeys };
    }

    // ================================================================ sampling

    /// <summary>
    /// Merges a take's animations per node (an FSB2 take = rotation-only full-body anim +
    /// full-TRS anim for the few translating nodes; rotations agree where both cover a
    /// node) and resamples onto the fps grid. Uncovered nodes hold rest; rotation-only
    /// nodes hold the rest translation.
    /// </summary>
    private static Clip ResampleTake(
        RwTake take, RwDffSkeletonData dff, Skeleton.Skeleton skeleton,
        float unitScale, float fps, string clipName)
    {
        var nodeCount = dff.Nodes.Count;
        var rotKeys = new List<(float Time, Quaternion Rot, Vector3? Trans)>?[nodeCount];
        var transKeys = new List<(float Time, Quaternion Rot, Vector3? Trans)>?[nodeCount];

        // Wider anims are merged later so the full-body rotation set wins where the sets
        // overlap (values agree in practice; ordering makes it deterministic). Translations
        // only ever come from 36-byte TRS keyframes.
        var anims = new List<RwAnim>(take.Anims);
        anims.Sort((a, b) => a.NodeCount.CompareTo(b.NodeCount));
        foreach (var anim in anims)
        {
            for (var n = 0; n < anim.NodeCount; n++)
            {
                if (anim.NodeKeys[n].Count == 0)
                    continue;
                rotKeys[n] = anim.NodeKeys[n];
                if (anim.Interp == InterpUncompressedTrs)
                    transKeys[n] = anim.NodeKeys[n];
            }
        }

        // Map node order → skeleton bone order (Skeleton.Create may topologically re-sort).
        var toSkeleton = new int[nodeCount];
        for (var n = 0; n < nodeCount; n++)
            toSkeleton[n] = skeleton.IndexOf(dff.Nodes[n].Name);

        var duration = take.Duration;
        var outCount = Math.Max(1, (int)Math.Round(duration * (double)fps) + 1);
        var frames = new List<XForm[]>(outCount);
        for (var f = 0; f < outCount; f++)
        {
            var time = f / fps;
            var frame = new XForm[skeleton.Count];
            for (var n = 0; n < nodeCount; n++)
            {
                var rest = dff.Nodes[n].RestLocal;
                var rot = rotKeys[n] is { } rk ? SampleRotation(rk, time) : rest.Rot;
                var pos = transKeys[n] is { } tk
                    ? SampleTranslation(tk, time) * unitScale
                    : rest.Pos * unitScale;
                frame[toSkeleton[n]] = new XForm(pos, rot);
            }
            frames.Add(frame);
        }

        QuaternionContinuity.AlignFrames(frames);

        // The corpus keys sit on a 30 fps grid; that native rate is what external frame
        // ranges would be expressed in.
        return new Clip(clipName, fps, looping: false, frames, nativeFps: 30f);
    }

    private static Quaternion SampleRotation(
        List<(float Time, Quaternion Rot, Vector3? Trans)> keys, float time)
    {
        var (lo, hi, u) = Bracket(keys, time);
        var a = keys[lo].Rot;
        var b = keys[hi].Rot;
        // Quaternion.Slerp takes the short way (negates on dot < 0) — keyframe sign flips
        // cannot spin the long way around.
        return u <= 0f ? a : MathQ.Normalize(Quaternion.Slerp(a, b, u));
    }

    private static Vector3 SampleTranslation(
        List<(float Time, Quaternion Rot, Vector3? Trans)> keys, float time)
    {
        var (lo, hi, u) = Bracket(keys, time);
        var a = keys[lo].Trans ?? Vector3.Zero;
        var b = keys[hi].Trans ?? a;
        return u <= 0f ? a : Vector3.Lerp(a, b, u);
    }

    /// <summary>Bracketing key pair for <paramref name="time"/> plus the interpolation
    /// fraction; clamps outside the keyed range.</summary>
    private static (int Lo, int Hi, float U) Bracket(
        List<(float Time, Quaternion Rot, Vector3? Trans)> keys, float time)
    {
        if (time <= keys[0].Time || keys.Count == 1)
            return (0, 0, 0f);
        var last = keys.Count - 1;
        if (time >= keys[last].Time)
            return (last, last, 0f);

        // Binary search for the last key with Time <= time.
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid].Time <= time)
                lo = mid;
            else
                hi = mid;
        }
        var span = keys[hi].Time - keys[lo].Time;
        var u = span > 0f ? Math.Clamp((time - keys[lo].Time) / span, 0f, 1f) : 0f;
        return (lo, hi, u);
    }

    // ================================================================ units

    /// <summary>BVH-style meters-vs-centimeters heuristic on the rest skeleton height
    /// (max−min world Y): &lt; 10 → meters → ×100, else ×1. FSB2 is authored in cm
    /// (~185 rest height) and passes through unscaled.</summary>
    private static float HeuristicUnitScale(RwDffSkeletonData dff)
    {
        var count = dff.Nodes.Count;
        var world = new XForm[count];
        float min = float.MaxValue, max = float.MinValue;
        for (var n = 0; n < count; n++)
        {
            var parent = dff.Nodes[n].ParentIndex;
            world[n] = parent < 0
                ? dff.Nodes[n].RestLocal
                : XForm.Compose(world[parent], dff.Nodes[n].RestLocal);
            min = MathF.Min(min, world[n].Pos.Y);
            max = MathF.Max(max, world[n].Pos.Y);
        }
        var height = max - min;
        return height > 0f && height < MeterHeightThreshold ? 100f : 1f;
    }
}
