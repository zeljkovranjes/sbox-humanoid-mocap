#nullable enable annotations

using System;

namespace HumanoidMocap.Formats.Renderware;

/// <summary>
/// RenderWare binary-stream primitives shared by the .dff skeleton parser and the
/// .anm/.an5 animation importer: little-endian scalar readers and the 12-byte chunk
/// header {u32 type, u32 size, u32 libraryId}.
/// </summary>
/// <remarks>
/// Verified against FreeStyle 2 (FSB2) game data (libraryId <c>0x1C020065</c> = RW 3.x
/// stamp): chunk sizes are payload sizes (header excluded) and nesting is by
/// containment — a container chunk's payload is itself a sequence of chunks.
/// </remarks>
internal static class RwStream
{
    // Chunk type ids used by this library (RenderWare stream token ids).
    public const uint ChunkStruct = 0x01;
    public const uint ChunkExtension = 0x03;
    public const uint ChunkFrameList = 0x0E;
    public const uint ChunkClump = 0x10;
    public const uint ChunkAnimAnimation = 0x1B;
    public const uint ChunkHAnimPlg = 0x11E;
    public const uint ChunkUserDataPlg = 0x11F;
    public const uint ChunkFrameName = 0x253F2FE;

    /// <summary>Reads a little-endian u32; throws on truncation.</summary>
    public static uint U32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            throw new FormatException("RenderWare stream truncated (u32 read past end).");
        return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
    }

    /// <summary>Reads a little-endian i32; throws on truncation.</summary>
    public static int I32(byte[] data, int offset) => unchecked((int)U32(data, offset));

    /// <summary>Reads a little-endian f32; throws on truncation (NaN/Inf are surfaced as-is —
    /// callers validate where it matters).</summary>
    public static float F32(byte[] data, int offset)
        => BitConverter.Int32BitsToSingle(I32(data, offset));

    /// <summary>
    /// Reads a chunk header at <paramref name="offset"/> and validates that the declared
    /// payload fits inside <paramref name="end"/> (exclusive). Returns the payload window.
    /// </summary>
    public static (uint Type, int PayloadStart, int PayloadSize) ReadChunk(byte[] data, int offset, int end)
    {
        if (offset + 12 > end)
            throw new FormatException("RenderWare stream truncated (chunk header past end).");
        var type = U32(data, offset);
        var size = U32(data, offset + 4);
        if (size > int.MaxValue || offset + 12 + (int)size > end)
            throw new FormatException(
                $"RenderWare chunk 0x{type:X} at offset {offset} declares {size} payload bytes past the stream end.");
        return (type, offset + 12, (int)size);
    }
}
