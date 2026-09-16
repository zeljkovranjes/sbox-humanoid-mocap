#nullable enable annotations

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Formats.Ant;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Low-level reader for EA's ANT (Animation Tool) container — the <c>.cba</c> animation
/// packages shipped by EA Canada titles (verified on Fight Night Champion, PS3).
/// </summary>
/// <remarks>
/// <para>
/// The whole format is BIG-ENDIAN. Layout, established by decoding
/// <c>package_proxy_punch_ali.cba</c> against a known-good reference extraction of the same
/// file (312 of 312 channels reproduce bit-exactly on times, rotations and joint indices):
/// </para>
/// <code>
/// ANTSTM3b &lt;u32 totalSize&gt;&lt;u32 _&gt;   outer wrapper: 16-byte header, spans the file
///   then a TILING list of inner chunks, each &lt;8-byte tag&gt;&lt;u32 size&gt;&lt;u32 _&gt;:
///     ANTREF2b   type/schema table (class and property names)
///     ANTDAT4b   data — ONE CHUNK PER CLIP
///
/// clip payload (chunk + 16):
///   +96   u32   channel count
///   +116  u32   duration (ticks)
///   +120  u32   contact tick
///   +128  channel table, 8 bytes per entry, payload-relative offset in the 2nd u32
///
/// channel block at payload-relative `co`:
///   co+32 u32   descriptor-block pointer (block-relative)
///   descriptor records, 16 bytes each: (count, capacity, pad=0, payload-relative offset);
///   the LAST THREE records are times, positions and rotations
///   joint index: u16 at descBlock + 16*records + 2
///   times      count x float32
///   positions  count x vec4   — xyz followed by a ZERO w pad (16 bytes per key)
///   rotations  count x float32 quaternion, W FIRST (wxyz)
/// </code>
/// <para><b>The vec4 position stride is not incidental.</b> A widely circulated extraction of
/// these packages read the position array with a 12-byte (vec3) stride over what is really a
/// 16-byte vec4 buffer, so every key after the first slid one float further out of alignment
/// and the last quarter of each channel was never read at all. Reading vec4 and discarding
/// <c>w</c> is what makes the data correct — the corrupted form is recognisable because every
/// 4th float of the flattened stream is exactly 0.</para>
/// </remarks>
public static class AntStream
{
    /// <summary>Outer container tag; also the file's magic.</summary>
    public const string StreamTag = "ANTSTM3b";

    private const string DataTag = "ANTDAT4b";
    private const int ChunkHeader = 16;

    /// <summary>True when the bytes open with the ANT stream magic.</summary>
    public static bool IsAnt(byte[] data)
        => data is not null && data.Length >= ChunkHeader && Tag(data, 0) == StreamTag;

    /// <summary>One animated joint of a clip: the joint's index into the companion
    /// skeleton plus its sampled local key track.</summary>
    public sealed class Channel
    {
        public int JointIndex { get; init; }
        public float[] Times { get; init; } = Array.Empty<float>();
        public XForm[] Keys { get; init; } = Array.Empty<XForm>();
    }

    /// <summary>One clip: its key tracks plus the authored tick metadata.</summary>
    public sealed class ClipData
    {
        public int DurationTicks { get; init; }
        public int ContactTick { get; init; }
        public List<Channel> Channels { get; } = new();
    }

    /// <summary>
    /// Parses every clip chunk in an ANT package. Chunks that do not decode as clips (the
    /// schema table, controller/asset data) are skipped rather than failing the import — a
    /// package mixes them freely.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the bytes are not an ANT stream.</exception>
    public static List<ClipData> ParseClips(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsAnt(data))
            throw new FormatException("Not an EA ANT stream (expected the 'ANTSTM3b' magic).");

        var clips = new List<ClipData>();
        var offset = ChunkHeader;
        while (offset + ChunkHeader <= data.Length)
        {
            var tag = Tag(data, offset);
            if (tag is null)
                break;
            var size = ReadU32(data, offset + 8);
            // A zero//overlong size would loop forever or read past the end: stop cleanly,
            // keeping whatever parsed so far (packages are append-only chunk lists).
            if (size < ChunkHeader || offset + size > data.Length)
                break;
            if (tag == DataTag && TryParseClip(data, offset, (int)size) is { } clip)
                clips.Add(clip);
            offset += (int)size;
        }
        return clips;
    }

    private static ClipData? TryParseClip(byte[] d, int chunk, int chunkSize)
    {
        var p = chunk + ChunkHeader;
        var end = chunkSize - ChunkHeader; // payload-relative end
        if (end < 136)
            return null;

        var channelCount = (int)ReadU32(d, p + 96);
        // Non-clip data chunks land here too; their +96 word is not a plausible channel
        // count, and the table would run past the payload.
        if (channelCount <= 0 || channelCount > 4096 || 128 + 8 * channelCount > end)
            return null;

        var offsets = new int[channelCount];
        for (var i = 0; i < channelCount; i++)
        {
            offsets[i] = (int)ReadU32(d, p + 128 + 8 * i + 4);
            if (offsets[i] <= 0 || offsets[i] >= end)
                return null;
        }

        var clip = new ClipData
        {
            DurationTicks = (int)ReadU32(d, p + 116),
            ContactTick = (int)ReadU32(d, p + 120),
        };

        for (var i = 0; i < channelCount; i++)
        {
            var start = offsets[i];
            var stop = i + 1 < channelCount ? offsets[i + 1] : end;
            var channel = TryParseChannel(d, p, start, stop);
            if (channel is null)
                return null;
            clip.Channels.Add(channel);
        }
        return clip;
    }

    private static Channel? TryParseChannel(byte[] d, int p, int start, int stop)
    {
        if (start + 36 > stop)
            return null;
        var descriptors = start + (int)ReadU32(d, p + start + 32);
        if (descriptors <= start || descriptors + 16 > stop)
            return null;

        // (count, capacity, pad, offset) records run until one fails the shape test.
        var records = new List<(int Count, int Offset)>();
        for (var r = descriptors; r + 16 <= stop; r += 16)
        {
            var count = (int)ReadU32(d, p + r);
            var capacity = (int)ReadU32(d, p + r + 4);
            var pad = ReadU32(d, p + r + 8);
            var offset = (int)ReadU32(d, p + r + 12);
            if (pad != 0 || count != capacity || count <= 0 || count > 65536
                || offset <= start || offset > stop)
                break;
            records.Add((count, offset));
        }
        if (records.Count < 3)
            return null;

        var times = records[^3];
        var positions = records[^2];
        var rotations = records[^1];
        var n = times.Count;
        if (positions.Count != n || rotations.Count != n)
            return null;
        if (times.Offset + 4 * n > stop
            || positions.Offset + 16 * n > stop || rotations.Offset + 16 * n > stop)
            return null;

        var jointWord = descriptors + 16 * records.Count + 2;
        if (jointWord + 2 > stop)
            return null;

        var t = new float[n];
        var keys = new XForm[n];
        for (var k = 0; k < n; k++)
        {
            t[k] = ReadF32(d, p + times.Offset + 4 * k);

            // vec4 stride: xyz then a zero w pad. Reading this as vec3 is the corruption
            // described in the class remarks.
            var pos = new Vector3(
                ReadF32(d, p + positions.Offset + 16 * k),
                ReadF32(d, p + positions.Offset + 16 * k + 4),
                ReadF32(d, p + positions.Offset + 16 * k + 8));

            // Quaternion is stored W FIRST.
            var w = ReadF32(d, p + rotations.Offset + 16 * k);
            var rot = new Quaternion(
                ReadF32(d, p + rotations.Offset + 16 * k + 4),
                ReadF32(d, p + rotations.Offset + 16 * k + 8),
                ReadF32(d, p + rotations.Offset + 16 * k + 12),
                w);
            if (!IsFinite(pos) || !IsFinite(rot))
                return null;
            keys[k] = new XForm(pos, MathQ.Normalize(rot));
        }

        return new Channel
        {
            JointIndex = ReadU16(d, p + jointWord),
            Times = t,
            Keys = keys,
        };
    }

    // ------------------------------------------------------------------ primitives

    private static string? Tag(byte[] d, int offset)
    {
        if (offset + 8 > d.Length)
            return null;
        for (var i = 0; i < 8; i++)
        {
            var c = d[offset + i];
            if (c is < 0x30 or > 0x7a)
                return null;
        }
        var tag = System.Text.Encoding.ASCII.GetString(d, offset, 8);
        return tag.StartsWith("ANT", StringComparison.Ordinal) ? tag : null;
    }

    private static uint ReadU32(byte[] d, int o)
        => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));

    private static ushort ReadU16(byte[] d, int o)
        => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));

    private static float ReadF32(byte[] d, int o)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o, 4)));

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Quaternion q)
        => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W)
            && q.LengthSquared() > 1e-8f;
}
