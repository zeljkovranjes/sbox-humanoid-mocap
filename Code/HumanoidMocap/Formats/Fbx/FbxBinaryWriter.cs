#nullable enable annotations

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace HumanoidMocap.Formats.Fbx;

/// <summary>
/// Serializes an <see cref="FbxNode"/> tree back to binary FBX (version 7400 layout —
/// u32 header fields, universally readable). The inverse of
/// <see cref="FbxTokenizer.Parse"/>: a tree parsed from a 7.x binary file and written
/// here re-parses to an identical tree (arrays are written uncompressed; zlib-encoded
/// inputs therefore round-trip by VALUE, not byte-for-byte).
/// </summary>
/// <remarks>
/// Used by <see cref="FbxBindPoseFixer"/> to persist repaired node transforms. The footer
/// is written the way Blender's exporter does: a fixed 16-byte watermark (importers treat
/// it as opaque), zero padding to a 16-byte boundary, the version echo, 120 zero bytes and
/// the closing magic. The FBX SDK computes a content hash here, but every consumer we
/// target (s&amp;box, Blender, assimp) ignores it.
/// </remarks>
public static class FbxBinaryWriter
{
    private const uint Version = 7400;

    private static readonly byte[] HeaderMagic =
        "Kaydara FBX Binary  \0\x1a\0"u8.ToArray();

    // Blender's fbx_binary.py FOOT_ID + closing magic bytes.
    private static readonly byte[] FooterWatermark =
    {
        0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e,
    };

    private static readonly byte[] FooterMagic =
    {
        0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0x6e, 0x0c, 0xc0, 0x00, 0x00,
    };

    /// <summary>
    /// Serializes <paramref name="root"/> (a virtual root whose children are the top-level
    /// document nodes, as produced by <see cref="FbxTokenizer.Parse"/>).
    /// </summary>
    public static byte[] Write(FbxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using var ms = new MemoryStream();
        ms.Write(HeaderMagic);
        WriteU32(ms, Version);

        foreach (var child in root.Children)
            WriteNode(ms, child);
        WriteNullRecord(ms);

        WriteFooter(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ nodes

    private static void WriteNode(MemoryStream ms, FbxNode node)
    {
        long headerAt = ms.Position;
        // Placeholder header: endOffset, numProps, propListLen (patched after the body).
        WriteU32(ms, 0);
        WriteU32(ms, (uint)node.Properties.Count);
        WriteU32(ms, 0);
        var nameBytes = Encoding.ASCII.GetBytes(node.Name);
        if (nameBytes.Length > byte.MaxValue)
            throw new FormatException($"FBX write: node name too long ({node.Name.Length} chars).");
        ms.WriteByte((byte)nameBytes.Length);
        ms.Write(nameBytes);

        long propsAt = ms.Position;
        foreach (var p in node.Properties)
            WriteProperty(ms, p, node.Name);
        long propsLen = ms.Position - propsAt;

        if (node.Children.Count > 0)
        {
            foreach (var child in node.Children)
                WriteNode(ms, child);
            WriteNullRecord(ms);
        }

        long endAt = ms.Position;
        ms.Position = headerAt;
        WriteU32(ms, checked((uint)endAt));
        WriteU32(ms, (uint)node.Properties.Count);
        WriteU32(ms, checked((uint)propsLen));
        ms.Position = endAt;
    }

    private static void WriteNullRecord(MemoryStream ms)
    {
        Span<byte> zeros = stackalloc byte[13];
        zeros.Clear();
        ms.Write(zeros);
    }

    // ------------------------------------------------------------------ properties

    private static void WriteProperty(MemoryStream ms, object value, string owner)
    {
        switch (value)
        {
            case short y:
                ms.WriteByte((byte)'Y');
                WriteI16(ms, y);
                break;
            case bool c:
                ms.WriteByte((byte)'C');
                ms.WriteByte(c ? (byte)1 : (byte)0);
                break;
            case int i:
                ms.WriteByte((byte)'I');
                WriteI32(ms, i);
                break;
            case float f:
                ms.WriteByte((byte)'F');
                WriteF32(ms, f);
                break;
            case double d:
                ms.WriteByte((byte)'D');
                WriteF64(ms, d);
                break;
            case long l:
                ms.WriteByte((byte)'L');
                WriteI64(ms, l);
                break;

            case float[] fa:
                WriteArrayHeader(ms, 'f', fa.Length, 4);
                foreach (var x in fa)
                    WriteF32(ms, x);
                break;
            case double[] da:
                WriteArrayHeader(ms, 'd', da.Length, 8);
                foreach (var x in da)
                    WriteF64(ms, x);
                break;
            case long[] la:
                WriteArrayHeader(ms, 'l', la.Length, 8);
                foreach (var x in la)
                    WriteI64(ms, x);
                break;
            case int[] ia:
                WriteArrayHeader(ms, 'i', ia.Length, 4);
                foreach (var x in ia)
                    WriteI32(ms, x);
                break;
            case bool[] ba:
                WriteArrayHeader(ms, 'b', ba.Length, 1);
                foreach (var x in ba)
                    ms.WriteByte(x ? (byte)1 : (byte)0);
                break;

            case string s:
            {
                ms.WriteByte((byte)'S');
                var bytes = Encoding.UTF8.GetBytes(s);
                WriteU32(ms, (uint)bytes.Length);
                ms.Write(bytes);
                break;
            }
            case byte[] r:
                ms.WriteByte((byte)'R');
                WriteU32(ms, (uint)r.Length);
                ms.Write(r);
                break;

            default:
                throw new FormatException(
                    $"FBX write: node '{owner}': unsupported property CLR type {value?.GetType().Name ?? "null"}.");
        }
    }

    private static void WriteArrayHeader(MemoryStream ms, char code, int count, int elemSize)
    {
        ms.WriteByte((byte)code);
        WriteU32(ms, (uint)count);
        WriteU32(ms, 0); // encoding 0 = uncompressed
        WriteU32(ms, checked((uint)(count * elemSize)));
    }

    // ------------------------------------------------------------------ footer

    private static void WriteFooter(MemoryStream ms)
    {
        ms.Write(FooterWatermark);

        // Zero-pad so the version echo starts 16-aligned (Blender pads at least 1 byte).
        int pad = (int)(16 - ms.Position % 16);
        for (int i = 0; i < pad; i++)
            ms.WriteByte(0);

        WriteU32(ms, Version);
        Span<byte> zeros = stackalloc byte[120];
        zeros.Clear();
        ms.Write(zeros);
        ms.Write(FooterMagic);
    }

    // ------------------------------------------------------------------ primitives

    private static void WriteU32(MemoryStream ms, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteI16(MemoryStream ms, short v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteI32(MemoryStream ms, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteI64(MemoryStream ms, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteF32(MemoryStream ms, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteF64(MemoryStream ms, double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        ms.Write(b);
    }
}
