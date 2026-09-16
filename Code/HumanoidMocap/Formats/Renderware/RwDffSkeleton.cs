#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Formats.Renderware;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>One HAnim node of a parsed .dff skeleton, in HAnim node order — the order
/// RwAnimAnimation keyframes address nodes in.</summary>
public sealed class RwDffNode
{
    /// <summary>HAnim node id (stable across FSB2 characters, e.g. 1000 = "Bip01").</summary>
    public required int NodeId { get; init; }

    /// <summary>
    /// Bone name: the frame's authored name when the .dff carries one (FSB2 stores 3ds Max
    /// Biped names like <c>Bip01 L Thigh</c> in the RpUserData extension), else the
    /// synthesized stable fallback <c>rw_node_&lt;id&gt;</c>. Unique within the skeleton.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Parent NODE index (into the node list), or -1 for the root node.</summary>
    public required int ParentIndex { get; init; }

    /// <summary>Rest (bind) transform relative to the parent NODE (intermediate non-HAnim
    /// frames composed in), native .dff units/axes.</summary>
    public required XForm RestLocal { get; init; }

    /// <summary>HAnim PUSH/POP hierarchy flags from the node table (diagnostic).</summary>
    public required uint Flags { get; init; }
}

/// <summary>Result of parsing a .dff model's skeleton.</summary>
public sealed class RwDffSkeletonData
{
    /// <summary>HAnim nodes in node-index order (== animation keyframe node order).</summary>
    public required IReadOnlyList<RwDffNode> Nodes { get; init; }

    /// <summary>Total FrameList frame count (HAnim and non-HAnim frames alike).</summary>
    public required int FrameCount { get; init; }
}

/// <summary>
/// RenderWare .dff model skeleton parser — reads ONLY the Clump's FrameList and the RpHAnim
/// plugin data (geometry is skipped entirely).
/// </summary>
/// <remarks>
/// <para><b>Layout</b> (verified against FSB2 <c>character.pak</c> models): Clump (0x10) →
/// struct (0x1) → FrameList (0xE) → struct (0x1) = {u32 numFrames, numFrames × 56-byte
/// frames {f32 rot[9] row-major 3x3, f32 pos[3], i32 parentIndex, u32 flags}}, then one
/// Extension (0x3) chunk PER frame containing optional sub-chunks: 0x11E = HAnimPLG
/// {u32 version, u32 nodeId, u32 numNodes, [u32 flags, u32 keyFrameSize,
/// numNodes × {u32 nodeId, u32 nodeIndex, u32 nodeFlags}]} (exactly one frame owns the full
/// node table), 0x11F = RpUserData (FSB2 stores the real 3ds Max bone name under the
/// <c>name</c> attribute — the classic frame-name chunk 0x253F2FE is present but empty).</para>
/// <para><b>Rotation matrices</b> are row-major with rows = the frame's basis vectors
/// (RenderWare right/up/at), i.e. row-vector convention <c>v_parent = v_child · M</c> —
/// the same convention as <see cref="Matrix4x4"/>, so
/// <see cref="Quaternion.CreateFromRotationMatrix"/> converts directly (verified: FK over
/// the FSB2 rig lands the feet at ground level and the head at ~184 cm).</para>
/// <para><b>Node order and parents</b>: the HAnim node table order IS the animation
/// keyframe node order. Each frame's own HAnimPLG carries its nodeId; a node's parent is
/// the nearest ancestor FRAME that is itself an HAnim node (intermediate plain frames —
/// FSB2 has one root dummy — are composed into the node's rest transform).</para>
/// </remarks>
public static class RwDffSkeleton
{
    /// <summary>Parses the skeleton (FrameList + HAnim) out of .dff bytes.</summary>
    /// <exception cref="FormatException">Malformed/truncated stream, or no HAnim data.</exception>
    public static RwDffSkeletonData Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var (rootType, clumpStart, clumpSize) = RwStream.ReadChunk(data, 0, data.Length);
        if (rootType != RwStream.ChunkClump)
            throw new FormatException(
                $"Not a RenderWare model (.dff): expected a Clump chunk (0x10), found 0x{rootType:X}.");
        var clumpEnd = clumpStart + clumpSize;

        // Find the FrameList inside the clump (the clump struct precedes it; geometry
        // lists follow and are never visited — we stop at the first FrameList).
        var offset = clumpStart;
        while (offset < clumpEnd)
        {
            var (type, payloadStart, payloadSize) = RwStream.ReadChunk(data, offset, clumpEnd);
            if (type == RwStream.ChunkFrameList)
                return ParseFrameList(data, payloadStart, payloadStart + payloadSize);
            offset = payloadStart + payloadSize;
        }
        throw new FormatException("RenderWare .dff has no FrameList chunk — cannot read a skeleton.");
    }

    /// <summary>
    /// Cheap probe used by skeleton resolution: the HAnim node count of .dff bytes, or null
    /// when the bytes are not a parseable .dff with HAnim data. Never throws.
    /// </summary>
    public static int? PeekNodeCount(byte[] data)
    {
        try
        {
            return Parse(data).Nodes.Count;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ================================================================ frame list

    private sealed class Frame
    {
        public required XForm Local;
        public required int Parent;
        public int NodeId = -1;         // HAnim node id, -1 when the frame has no HAnimPLG
        public string Name = "";
    }

    private static RwDffSkeletonData ParseFrameList(byte[] data, int start, int end)
    {
        var (structType, structStart, structSize) = RwStream.ReadChunk(data, start, end);
        if (structType != RwStream.ChunkStruct)
            throw new FormatException("RenderWare FrameList: expected leading struct chunk.");

        var frameCount = RwStream.I32(data, structStart);
        if (frameCount <= 0 || structSize < 4 + frameCount * 56)
            throw new FormatException($"RenderWare FrameList declares invalid frame count {frameCount}.");

        var frames = new List<Frame>(frameCount);
        for (var i = 0; i < frameCount; i++)
        {
            var p = structStart + 4 + i * 56;
            var local = ReadFrameTransform(data, p);
            var parent = RwStream.I32(data, p + 48);
            if (parent >= i || parent < -1)
                throw new FormatException(
                    $"RenderWare FrameList: frame {i} has invalid parent index {parent}.");
            frames.Add(new Frame { Local = local, Parent = parent });
        }

        // One Extension chunk per frame, in frame order.
        (int NodeId, int NodeIndex, uint Flags)[]? nodeTable = null;
        var offset = structStart + structSize;
        for (var i = 0; i < frameCount; i++)
        {
            var (extType, extStart, extSize) = RwStream.ReadChunk(data, offset, end);
            if (extType != RwStream.ChunkExtension)
                throw new FormatException(
                    $"RenderWare FrameList: expected Extension chunk for frame {i}, found 0x{extType:X}.");
            ParseFrameExtension(data, extStart, extStart + extSize, frames[i], ref nodeTable);
            offset = extStart + extSize;
        }

        if (nodeTable is null)
            throw new FormatException(
                "RenderWare .dff has no HAnim node table — the model carries no animatable skeleton.");

        return BuildNodes(frames, nodeTable, frameCount);
    }

    private static XForm ReadFrameTransform(byte[] data, int p)
    {
        // Row-major 3x3, rows = basis vectors (row-vector convention, see class remarks).
        var m = new Matrix4x4(
            RwStream.F32(data, p + 0), RwStream.F32(data, p + 4), RwStream.F32(data, p + 8), 0f,
            RwStream.F32(data, p + 12), RwStream.F32(data, p + 16), RwStream.F32(data, p + 20), 0f,
            RwStream.F32(data, p + 24), RwStream.F32(data, p + 28), RwStream.F32(data, p + 32), 0f,
            0f, 0f, 0f, 1f);
        var pos = new Vector3(
            RwStream.F32(data, p + 36), RwStream.F32(data, p + 40), RwStream.F32(data, p + 44));
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
            throw new FormatException("RenderWare FrameList: non-finite frame translation.");
        var rot = MathQ.Normalize(Quaternion.CreateFromRotationMatrix(m));
        if (!float.IsFinite(rot.X) || !float.IsFinite(rot.Y) || !float.IsFinite(rot.Z) || !float.IsFinite(rot.W))
            throw new FormatException("RenderWare FrameList: non-finite frame rotation.");
        return new XForm(pos, rot);
    }

    // ================================================================ extensions

    private static void ParseFrameExtension(
        byte[] data, int start, int end, Frame frame,
        ref (int NodeId, int NodeIndex, uint Flags)[]? nodeTable)
    {
        var offset = start;
        while (offset < end)
        {
            var (type, payloadStart, payloadSize) = RwStream.ReadChunk(data, offset, end);
            switch (type)
            {
                case RwStream.ChunkHAnimPlg:
                    ParseHAnim(data, payloadStart, payloadSize, frame, ref nodeTable);
                    break;
                case RwStream.ChunkUserDataPlg:
                    var userName = ReadUserDataName(data, payloadStart, payloadStart + payloadSize);
                    if (!string.IsNullOrEmpty(userName))
                        frame.Name = userName;
                    break;
                case RwStream.ChunkFrameName:
                    // Classic frame-name string chunk. FSB2 leaves these empty (the real
                    // names live in RpUserData) but honor them when present, without
                    // overriding an already-found user-data name.
                    if (frame.Name.Length == 0 && payloadSize > 0)
                        frame.Name = ReadCString(data, payloadStart, payloadSize);
                    break;
            }
            offset = payloadStart + payloadSize;
        }
    }

    private static void ParseHAnim(
        byte[] data, int start, int size, Frame frame,
        ref (int NodeId, int NodeIndex, uint Flags)[]? nodeTable)
    {
        if (size < 12)
            throw new FormatException("RenderWare HAnimPLG chunk is too small.");
        frame.NodeId = RwStream.I32(data, start + 4);
        var numNodes = RwStream.I32(data, start + 8);
        if (numNodes <= 0)
            return;
        if (size < 20 + numNodes * 12)
            throw new FormatException(
                $"RenderWare HAnimPLG node table truncated (numNodes={numNodes}, size={size}).");
        if (nodeTable is not null)
            throw new FormatException("RenderWare .dff carries more than one HAnim node table.");

        nodeTable = new (int, int, uint)[numNodes];
        for (var i = 0; i < numNodes; i++)
        {
            var p = start + 20 + i * 12;
            nodeTable[i] = (RwStream.I32(data, p), RwStream.I32(data, p + 4), RwStream.U32(data, p + 8));
        }
    }

    /// <summary>RpUserData: {u32 numAttrs, per attr {u32 nameLen, name, u32 format,
    /// u32 count, elements}}; format 3 = string elements {u32 len, chars}. Returns the
    /// first string value of the attribute named <c>name</c>, or "".</summary>
    private static string ReadUserDataName(byte[] data, int start, int end)
    {
        if (end - start < 4)
            return "";
        var attrCount = RwStream.I32(data, start);
        var offset = start + 4;
        for (var a = 0; a < attrCount; a++)
        {
            if (offset + 4 > end)
                return "";
            var nameLen = RwStream.I32(data, offset);
            offset += 4;
            if (nameLen < 0 || offset + nameLen > end)
                return "";
            var attrName = ReadCString(data, offset, nameLen);
            offset += nameLen;
            if (offset + 8 > end)
                return "";
            var format = RwStream.I32(data, offset);
            var elementCount = RwStream.I32(data, offset + 4);
            offset += 8;
            for (var e = 0; e < elementCount; e++)
            {
                switch (format)
                {
                    case 1: // int
                    case 2: // float
                        offset += 4;
                        break;
                    case 3: // string
                        if (offset + 4 > end)
                            return "";
                        var len = RwStream.I32(data, offset);
                        offset += 4;
                        if (len < 0 || offset + len > end)
                            return "";
                        if (attrName == "name")
                            return ReadCString(data, offset, len);
                        offset += len;
                        break;
                    default:
                        return ""; // unknown element format — cannot skip safely
                }
            }
        }
        return "";
    }

    private static string ReadCString(byte[] data, int start, int maxLen)
    {
        var len = 0;
        while (len < maxLen && data[start + len] != 0)
            len++;
        return Encoding.ASCII.GetString(data, start, len);
    }

    // ================================================================ node building

    private static RwDffSkeletonData BuildNodes(
        List<Frame> frames, (int NodeId, int NodeIndex, uint Flags)[] nodeTable, int frameCount)
    {
        // frame index by node id (each HAnim frame carries its own node id).
        var frameByNodeId = new Dictionary<int, int>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].NodeId >= 0 && !frameByNodeId.TryAdd(frames[i].NodeId, i))
                throw new FormatException(
                    $"RenderWare .dff: duplicate HAnim node id {frames[i].NodeId}.");
        }

        // The table's nodeIndex is the animation keyframe order — order by it.
        var ordered = new (int NodeId, uint Flags)[nodeTable.Length];
        var seen = new bool[nodeTable.Length];
        foreach (var (nodeId, nodeIndex, flags) in nodeTable)
        {
            if (nodeIndex < 0 || nodeIndex >= nodeTable.Length || seen[nodeIndex])
                throw new FormatException(
                    $"RenderWare HAnim node table has invalid/duplicate node index {nodeIndex}.");
            seen[nodeIndex] = true;
            ordered[nodeIndex] = (nodeId, flags);
        }

        var nodeIndexByFrame = new Dictionary<int, int>(nodeTable.Length);
        for (var n = 0; n < ordered.Length; n++)
        {
            if (!frameByNodeId.TryGetValue(ordered[n].NodeId, out var frameIndex))
                throw new FormatException(
                    $"RenderWare HAnim node id {ordered[n].NodeId} has no matching frame.");
            nodeIndexByFrame[frameIndex] = n;
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new RwDffNode[ordered.Length];
        for (var n = 0; n < ordered.Length; n++)
        {
            var frameIndex = frameByNodeId[ordered[n].NodeId];

            // Parent = nearest ancestor frame that is itself an HAnim node; plain frames
            // in between are composed into the rest transform (world = parent ∘ local).
            var local = frames[frameIndex].Local;
            var parentFrame = frames[frameIndex].Parent;
            var parentNode = -1;
            while (parentFrame >= 0)
            {
                if (nodeIndexByFrame.TryGetValue(parentFrame, out var pn))
                {
                    parentNode = pn;
                    break;
                }
                local = XForm.Compose(frames[parentFrame].Local, local);
                parentFrame = frames[parentFrame].Parent;
            }
            if (parentNode >= n && parentNode != -1)
                throw new FormatException(
                    $"RenderWare HAnim node order is not parent-first (node {n} has parent node {parentNode}).");

            var name = frames[frameIndex].Name;
            if (string.IsNullOrEmpty(name))
                name = $"rw_node_{ordered[n].NodeId}";
            name = UniqueName(name, usedNames);

            nodes[n] = new RwDffNode
            {
                NodeId = ordered[n].NodeId,
                Name = name,
                ParentIndex = parentNode,
                RestLocal = local,
                Flags = ordered[n].Flags,
            };
        }

        return new RwDffSkeletonData { Nodes = nodes, FrameCount = frameCount };
    }

    private static string UniqueName(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name))
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name}#{i}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }
}
