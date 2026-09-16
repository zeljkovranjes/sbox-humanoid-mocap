#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace HumanoidMocap.Formats.Gltf;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>One glTF node, reduced to what skeleton import needs (TRS rest + hierarchy).</summary>
internal sealed class GltfNode
{
    public string? Name;
    public int[] Children = Array.Empty<int>();
    public int Parent = -1;
    public bool HasMesh;

    // Rest local transform: TRS properties, or the decomposed "matrix" property (the spec
    // makes them exclusive; animated nodes must use TRS). Shear is not representable.
    public Vector3 Translation;                       // meters
    public Quaternion Rotation = Quaternion.Identity; // xyzw
    public Vector3 Scale = Vector3.One;
}

/// <summary>One decoded animation channel: keyframe times + values for one node property.</summary>
internal sealed class GltfChannel
{
    public required int NodeIndex;
    public required bool IsRotation;     // true = rotation (VEC4 quat), false = translation (VEC3)
    public required float[] Times;       // seconds, ascending
    public required float[] Values;      // flattened; 4 (or 3) floats per element
    public required string Interpolation; // LINEAR / STEP / CUBICSPLINE

    /// <summary>Floats per element (3 translation / 4 rotation).</summary>
    public int Comps => IsRotation ? 4 : 3;

    /// <summary>Elements stored per key: CUBICSPLINE keys carry in-tangent/value/out-tangent.</summary>
    public int ElementsPerKey => Interpolation == "CUBICSPLINE" ? 3 : 1;

    /// <summary>Number of keys.</summary>
    public int KeyCount => Times.Length;
}

/// <summary>One glTF animation with its decoded rotation/translation channels.</summary>
internal sealed class GltfAnimation
{
    public string? Name;
    public List<GltfChannel> Channels { get; } = new();
}

/// <summary>
/// Container + JSON layer of the glTF importer: parses a GLB binary container or a plain
/// .gltf JSON document, resolves buffers (GLB BIN chunk and base64 <c>data:</c> URIs — file
/// IO is banned in Code/, so external file URIs throw), and decodes nodes, skin joints and
/// animation samplers into plain arrays. Throws <see cref="FormatException"/> on anything
/// malformed or unsupported.
/// </summary>
internal sealed class GltfDocument
{
    private const uint GlbMagic = 0x46546C67;     // 'glTF' little-endian
    private const uint ChunkJson = 0x4E4F534A;    // 'JSON'
    private const uint ChunkBin = 0x004E4942;     // 'BIN\0'

    /// <summary>All nodes, indexed as in the file, with parents resolved from children lists.</summary>
    public List<GltfNode> Nodes { get; } = new();

    /// <summary>The detached JSON root and resolved binary buffers. Kept so the model-DMX
    /// bridge can reuse the same validated container/buffer handling as animation import.</summary>
    public JsonElement Root { get; private set; }
    public List<byte[]> Buffers { get; } = new();

    /// <summary>Union of all skins' joint node indices.</summary>
    public HashSet<int> SkinJoints { get; } = new();

    /// <summary>All animations with decoded rotation/translation channels (scale/weights ignored).</summary>
    public List<GltfAnimation> Animations { get; } = new();

    /// <summary>
    /// The VRM humanoid bone map authored in the file, when present: VRM bone name
    /// (<c>hips</c>, <c>leftUpperArm</c>, …) → node index. Read from BOTH extension layouts:
    /// VRM 0.x <c>extensions.VRM.humanoid.humanBones</c> (an ARRAY of
    /// <c>{ "bone": "hips", "node": 14 }</c> entries) and VRM 1.0
    /// <c>extensions.VRMC_vrm.humanoid.humanBones</c> (an OBJECT
    /// <c>{ "hips": { "node": 14 }, … }</c>). Null when the file carries neither.
    /// </summary>
    public Dictionary<string, int>? VrmHumanBones { get; private set; }

    /// <summary>Which VRM extension supplied <see cref="VrmHumanBones"/>: <c>0</c> for the
    /// 0.x <c>VRM</c> extension, <c>1</c> for the 1.0 <c>VRMC_vrm</c> extension, <c>-1</c>
    /// when none.</summary>
    public int VrmVersion { get; private set; } = -1;

    private GltfDocument()
    {
    }

    /// <summary>Parses GLB or plain-JSON glTF bytes.</summary>
    /// <exception cref="FormatException">Truncated/malformed container, invalid JSON,
    /// unresolvable buffers, or unsupported accessor layouts.</exception>
    public static GltfDocument Parse(byte[] data, Func<string, byte[]>? externalBufferResolver = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte[] json;
        byte[]? bin = null;
        if (data.Length >= 4 && ReadU32(data, 0) == GlbMagic)
            (json, bin) = ParseGlbContainer(data);
        else
            json = data;

        JsonElement root;
        try
        {
            // Parse via string: Memory<T>/ReadOnlyMemory<T> are not on the s&box runtime
            // whitelist (SB1000), and the string path also lets us strip a UTF-8 BOM
            // (Utf8JsonReader rejects raw BOM bytes). Clone detaches from the disposed
            // JsonDocument.
            var text = System.Text.Encoding.UTF8.GetString(json).TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new FormatException($"glTF: invalid JSON ({e.Message})");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("asset", out _))
            throw new FormatException("glTF: missing required 'asset' object (not a glTF file?).");

        var document = new GltfDocument();
        var buffers = ResolveBuffers(root, bin, externalBufferResolver);
        document.Root = root;
        document.Buffers.AddRange(buffers);
        document.ReadNodes(root);
        document.ReadSkins(root);
        document.ReadAnimations(root, buffers);
        document.ReadVrmHumanoid(root);
        return document;
    }

    // ================================================================== VRM humanoid

    /// <summary>
    /// Reads the authored humanoid bone map of a VRM file (a .vrm is a regular glTF 2.0/GLB
    /// container plus a VRM extension). VRM 1.0's <c>VRMC_vrm</c> wins when both extensions
    /// are present. Defensive throughout: malformed entries and out-of-range node indices
    /// are skipped (a broken bone map degrades to the regular detection cascade rather than
    /// failing the import).
    /// </summary>
    private void ReadVrmHumanoid(JsonElement root)
    {
        if (!root.TryGetProperty("extensions", out var extensions)
            || extensions.ValueKind != JsonValueKind.Object)
            return;

        // ---- VRM 1.0: extensions.VRMC_vrm.humanoid.humanBones = { "<bone>": { "node": n } } ----
        if (TryGetHumanBones(extensions, "VRMC_vrm", out var humanBones1)
            && humanBones1.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in humanBones1.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("node", out var node)
                    && node.ValueKind == JsonValueKind.Number
                    && node.TryGetInt32(out var index)
                    && index >= 0 && index < Nodes.Count)
                {
                    map[property.Name] = index;
                }
            }
            if (map.Count > 0)
            {
                VrmHumanBones = map;
                VrmVersion = 1;
                return;
            }
        }

        // ---- VRM 0.x: extensions.VRM.humanoid.humanBones = [ { "bone": "...", "node": n } ] ----
        if (TryGetHumanBones(extensions, "VRM", out var humanBones0)
            && humanBones0.ValueKind == JsonValueKind.Array)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in humanBones0.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("bone", out var bone)
                    && bone.ValueKind == JsonValueKind.String
                    && entry.TryGetProperty("node", out var node)
                    && node.ValueKind == JsonValueKind.Number
                    && node.TryGetInt32(out var index)
                    && index >= 0 && index < Nodes.Count)
                {
                    map[bone.GetString()!] = index;
                }
            }
            if (map.Count > 0)
            {
                VrmHumanBones = map;
                VrmVersion = 0;
            }
        }
    }

    private static bool TryGetHumanBones(JsonElement extensions, string extensionName, out JsonElement humanBones)
    {
        humanBones = default;
        return extensions.TryGetProperty(extensionName, out var vrm)
            && vrm.ValueKind == JsonValueKind.Object
            && vrm.TryGetProperty("humanoid", out var humanoid)
            && humanoid.ValueKind == JsonValueKind.Object
            && humanoid.TryGetProperty("humanBones", out humanBones);
    }

    // ================================================================== GLB container

    /// <summary>GLB layout: 12-byte header (magic 'glTF', u32 version = 2, u32 length),
    /// then chunks of (u32 length, u32 type, bytes): one JSON chunk, optionally one BIN.</summary>
    private static (byte[] Json, byte[]? Bin) ParseGlbContainer(byte[] data)
    {
        if (data.Length < 12)
            throw new FormatException("GLB: truncated header (need 12 bytes).");

        uint version = ReadU32(data, 4);
        if (version != 2)
            throw new FormatException($"GLB: unsupported container version {version} (expected 2).");

        long declared = ReadU32(data, 8);
        if (declared > data.Length)
            throw new FormatException(
                $"GLB: truncated file (header declares {declared} bytes, got {data.Length}).");

        byte[]? json = null, bin = null;
        long offset = 12;
        while (offset + 8 <= declared)
        {
            long length = ReadU32(data, (int)offset);
            uint type = ReadU32(data, (int)offset + 4);
            offset += 8;
            if (offset + length > data.Length)
                throw new FormatException("GLB: truncated chunk (declared length exceeds the file).");

            if (type == ChunkJson && json is null)
                json = data.AsSpan((int)offset, (int)length).ToArray();
            else if (type == ChunkBin && bin is null)
                bin = data.AsSpan((int)offset, (int)length).ToArray();
            // Unknown chunk types are skipped per spec.

            offset += length + (length % 4 == 0 ? 0 : 4 - length % 4); // chunks are 4-aligned
        }

        if (json is null)
            throw new FormatException("GLB: no JSON chunk found.");
        return (json, bin);
    }

    private static uint ReadU32(byte[] data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    // ================================================================== buffers

    /// <summary>
    /// Resolves every entry of <c>buffers</c>: no <c>uri</c> = the GLB BIN chunk (spec: only
    /// buffer 0 may do this), <c>data:</c> URIs are base64-decoded inline. External file
    /// URIs are delegated to the optional caller-supplied resolver so this core remains
    /// independent of the file system.
    /// </summary>
    private static List<byte[]> ResolveBuffers(
        JsonElement root, byte[]? bin, Func<string, byte[]>? externalBufferResolver)
    {
        var buffers = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out var array) || array.ValueKind != JsonValueKind.Array)
            return buffers;

        foreach (var buffer in array.EnumerateArray())
        {
            if (!buffer.TryGetProperty("uri", out var uriProp))
            {
                buffers.Add(bin ?? throw new FormatException(
                    "glTF: buffer has no uri but the file has no GLB BIN chunk."));
                continue;
            }

            var uri = uriProp.GetString() ?? "";
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("glTF: only base64 data: URIs are supported for buffers.");
                try
                {
                    buffers.Add(Convert.FromBase64String(uri[(comma + 1)..]));
                }
                catch (Exception e) when (e is FormatException or ArgumentException)
                {
                    throw new FormatException("glTF: invalid base64 in buffer data: URI.");
                }
            }
            else
            {
                if (externalBufferResolver is null)
                {
                    throw new FormatException(
                        $"glTF: buffer references an external file ('{uri}') which this importer cannot "
                        + "read (no file IO). Export as .glb (binary, self-contained) instead.");
                }
                try
                {
                    buffers.Add(externalBufferResolver(uri) ?? throw new FormatException(
                        $"glTF: external buffer resolver returned no data for '{uri}'."));
                }
                catch (FormatException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new FormatException(
                        $"glTF: could not read external buffer '{uri}' ({e.Message}).");
                }
            }
        }
        return buffers;
    }

    // ================================================================== nodes + skins

    private void ReadNodes(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        Span<float> m = stackalloc float[16]; // matrix scratch (outside the loop: CA2014)
        foreach (var n in array.EnumerateArray())
        {
            var node = new GltfNode
            {
                Name = n.TryGetProperty("name", out var name) ? name.GetString() : null,
                HasMesh = n.TryGetProperty("mesh", out _),
            };

            if (n.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                var list = new List<int>();
                foreach (var c in children.EnumerateArray())
                    list.Add(c.GetInt32());
                node.Children = list.ToArray();
            }

            if (n.TryGetProperty("matrix", out var matrix) && matrix.ValueKind == JsonValueKind.Array)
            {
                // Column-major 16 floats; the element order maps 1:1 onto System.Numerics'
                // row-vector matrices (translation in elements 12..14 either way).
                int i = 0;
                foreach (var v in matrix.EnumerateArray())
                {
                    if (i >= 16)
                        break;
                    m[i++] = v.GetSingle();
                }
                if (i < 16)
                    throw new FormatException("glTF: node matrix has fewer than 16 elements.");
                var local = new Matrix4x4(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11],
                    m[12], m[13], m[14], m[15]);
                if (Matrix4x4.Decompose(local, out var scale, out var rot, out var pos))
                {
                    node.Translation = pos;
                    node.Rotation = rot;
                    node.Scale = scale;
                }
                else
                {
                    node.Translation = local.Translation; // degenerate: keep position at least
                }
            }
            else
            {
                node.Translation = ReadVec3(n, "translation", Vector3.Zero);
                node.Scale = ReadVec3(n, "scale", Vector3.One);
                if (n.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Array
                    && r.GetArrayLength() >= 4)
                {
                    node.Rotation = new Quaternion(
                        r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
                }
            }

            Nodes.Add(node);
        }

        // Resolve parents (per spec a node is referenced by at most one other node's children).
        for (int i = 0; i < Nodes.Count; i++)
        {
            foreach (var child in Nodes[i].Children)
            {
                if (child < 0 || child >= Nodes.Count)
                    throw new FormatException($"glTF: node {i} references nonexistent child {child}.");
                if (Nodes[child].Parent < 0)
                    Nodes[child].Parent = i;
            }
        }
    }

    private static Vector3 ReadVec3(JsonElement element, string property, Vector3 fallback)
    {
        if (!element.TryGetProperty(property, out var v) || v.ValueKind != JsonValueKind.Array
            || v.GetArrayLength() < 3)
            return fallback;
        return new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
    }

    private void ReadSkins(JsonElement root)
    {
        if (!root.TryGetProperty("skins", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var skin in array.EnumerateArray())
        {
            if (!skin.TryGetProperty("joints", out var joints) || joints.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var j in joints.EnumerateArray())
            {
                int index = j.GetInt32();
                if (index >= 0 && index < Nodes.Count)
                    SkinJoints.Add(index);
            }
        }
    }

    // ================================================================== animations

    private void ReadAnimations(JsonElement root, List<byte[]> buffers)
    {
        if (!root.TryGetProperty("animations", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        root.TryGetProperty("accessors", out var accessors);
        root.TryGetProperty("bufferViews", out var views);

        foreach (var a in array.EnumerateArray())
        {
            var animation = new GltfAnimation
            {
                Name = a.TryGetProperty("name", out var name) ? name.GetString() : null,
            };

            if (!a.TryGetProperty("channels", out var channels) || !a.TryGetProperty("samplers", out var samplers))
            {
                Animations.Add(animation);
                continue;
            }

            foreach (var channel in channels.EnumerateArray())
            {
                if (!channel.TryGetProperty("target", out var target)
                    || !target.TryGetProperty("node", out var nodeProp)
                    || !target.TryGetProperty("path", out var pathProp))
                    continue; // extension targets (e.g. KHM_animation_pointer) are ignored

                var path = pathProp.GetString();
                if (path is not ("rotation" or "translation"))
                    continue; // scale / weights channels are ignored by design

                int node = nodeProp.GetInt32();
                if (node < 0 || node >= Nodes.Count)
                    continue;

                int samplerIndex = channel.TryGetProperty("sampler", out var s) ? s.GetInt32() : -1;
                if (samplerIndex < 0 || samplerIndex >= samplers.GetArrayLength())
                    throw new FormatException("glTF: animation channel references a nonexistent sampler.");
                var sampler = samplers[samplerIndex];

                var interpolation = sampler.TryGetProperty("interpolation", out var interp)
                    ? interp.GetString() ?? "LINEAR"
                    : "LINEAR";

                bool isRotation = path == "rotation";
                int comps = isRotation ? 4 : 3;

                var times = ReadAccessor(accessors, views, buffers,
                    RequiredInt(sampler, "input", "animation sampler"), 1, normalizedAllowed: false);
                var values = ReadAccessor(accessors, views, buffers,
                    RequiredInt(sampler, "output", "animation sampler"), comps, normalizedAllowed: isRotation);

                int elementsPerKey = interpolation == "CUBICSPLINE" ? 3 : 1;
                if (times.Length == 0 || values.Length < times.Length * elementsPerKey * comps)
                    continue; // empty or under-filled sampler: nothing usable

                animation.Channels.Add(new GltfChannel
                {
                    NodeIndex = node,
                    IsRotation = isRotation,
                    Times = times,
                    Values = values,
                    Interpolation = interpolation,
                });
            }

            Animations.Add(animation);
        }
    }

    private static int RequiredInt(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var v))
            throw new FormatException($"glTF: {context} is missing '{property}'.");
        return v.GetInt32();
    }

    // ================================================================== accessors

    /// <summary>
    /// Decodes an accessor to floats. Component types: f32 directly; normalized i8/u8/i16/u16
    /// per the spec's normalization rules when <paramref name="normalizedAllowed"/> (rotation
    /// outputs); anything else throws. Honors accessor/bufferView byte offsets and an
    /// explicit byteStride. Sparse accessors are not supported.
    /// </summary>
    private static float[] ReadAccessor(
        JsonElement accessors, JsonElement views, List<byte[]> buffers,
        int accessorIndex, int expectedComps, bool normalizedAllowed)
    {
        if (accessors.ValueKind != JsonValueKind.Array || accessorIndex < 0
            || accessorIndex >= accessors.GetArrayLength())
            throw new FormatException($"glTF: accessor {accessorIndex} does not exist.");
        var accessor = accessors[accessorIndex];

        if (accessor.TryGetProperty("sparse", out _))
            throw new FormatException("glTF: sparse accessors are not supported.");

        var type = accessor.TryGetProperty("type", out var t) ? t.GetString() : null;
        int comps = type switch
        {
            "SCALAR" => 1,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => throw new FormatException($"glTF: unsupported accessor type '{type}'."),
        };
        if (comps != expectedComps)
            throw new FormatException(
                $"glTF: accessor {accessorIndex} is {type}, expected {expectedComps} component(s).");

        int count = RequiredInt(accessor, "count", "accessor");
        int componentType = RequiredInt(accessor, "componentType", "accessor");
        bool normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();

        // The count is attacker-controlled: validate it BEFORE any allocation sized by it.
        // Negative would throw OverflowException from the array allocation (breaking the
        // FormatException malformed-file contract); huge would OOM; count * comps can wrap.
        if (count < 0)
            throw new FormatException($"glTF: accessor {accessorIndex} has a negative count ({count}).");

        int compSize = componentType switch
        {
            5126 => 4,            // FLOAT
            5120 or 5121 => 1,    // BYTE / UNSIGNED_BYTE
            5122 or 5123 => 2,    // SHORT / UNSIGNED_SHORT
            _ => throw new FormatException(
                $"glTF: unsupported accessor componentType {componentType}."),
        };
        if (componentType != 5126 && !(normalized && normalizedAllowed))
            throw new FormatException(
                $"glTF: accessor {accessorIndex} must be float (or a normalized integer "
                + "rotation output).");

        int elementSize = comps * compSize;

        if (!accessor.TryGetProperty("bufferView", out var viewIndexProp))
        {
            // Zero-filled when no bufferView (legal per spec) — but then nothing backs the
            // count, so cap it by the file's total decoded buffer bytes (a real file's
            // accessors never outgrow its payload; a small floor keeps tiny legitimate
            // zero-filled accessors working in buffer-less documents).
            long totalBufferBytes = 0;
            foreach (var b in buffers)
                totalBufferBytes += b.Length;
            long capacity = Math.Min(
                Math.Max(totalBufferBytes / elementSize, 65536),
                int.MaxValue / comps); // keeps count * comps int-representable
            if (count > capacity)
                throw new FormatException(
                    $"glTF: accessor {accessorIndex} count {count} exceeds what the file's "
                    + "buffers could back (malformed or hostile file).");
            return new float[checked(count * comps)];
        }

        int viewIndex = viewIndexProp.GetInt32();
        if (views.ValueKind != JsonValueKind.Array || viewIndex < 0 || viewIndex >= views.GetArrayLength())
            throw new FormatException($"glTF: bufferView {viewIndex} does not exist.");
        var view = views[viewIndex];

        int bufferIndex = RequiredInt(view, "buffer", "bufferView");
        if (bufferIndex < 0 || bufferIndex >= buffers.Count)
            throw new FormatException($"glTF: buffer {bufferIndex} does not exist.");
        var buffer = buffers[bufferIndex];

        int viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
        int accessorOffset = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        int stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : elementSize;
        if (stride < elementSize)
            throw new FormatException("glTF: bufferView byteStride is smaller than the element size.");

        // Bounds check in long arithmetic BEFORE allocating: the backing range must fit the
        // buffer, which also caps count at buffer.Length / stride (+1) — so the allocation
        // below is bounded by the actual file size and checked() can no longer overflow.
        long start = (long)viewOffset + accessorOffset;
        long end = start + (long)(count - 1) * stride + elementSize;
        if (count > 0 && (start < 0 || end > buffer.Length))
            throw new FormatException(
                $"glTF: accessor {accessorIndex} reads past the end of its buffer (truncated file?).");

        var result = new float[checked(count * comps)];
        for (int element = 0; element < count; element++)
        {
            int offset = (int)(start + (long)element * stride);
            for (int c = 0; c < comps; c++)
            {
                int at = offset + c * compSize;
                result[element * comps + c] = componentType switch
                {
                    5126 => BitConverter.ToSingle(buffer, at),
                    5120 => MathF.Max((sbyte)buffer[at] / 127f, -1f),
                    5121 => buffer[at] / 255f,
                    5122 => MathF.Max(BitConverter.ToInt16(buffer, at) / 32767f, -1f),
                    _ => BitConverter.ToUInt16(buffer, at) / 65535f,
                };
            }
        }
        return result;
    }
}
