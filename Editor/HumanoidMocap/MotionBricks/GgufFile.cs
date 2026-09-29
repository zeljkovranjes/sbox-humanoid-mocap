#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace HumanoidMocap.MotionBricks;

/// <summary>Element type of a GGUF tensor (the subset of <c>ggml_type</c> MotionBricks uses).</summary>
public enum GgufTensorType : uint
{
    F32 = 0,
    F16 = 1,
    I32 = 26,
    BF16 = 30,
}

/// <summary>One tensor entry of a GGUF file. <see cref="Shape"/> is in GGML order (fastest dimension first).</summary>
public sealed record GgufTensorInfo( string Name, long[] Shape, GgufTensorType Type, long DataOffset )
{
    public long ElementCount => Shape.Aggregate( 1L, ( a, b ) => a * b );

    public long ByteCount => Type switch
    {
        GgufTensorType.F32 or GgufTensorType.I32 => ElementCount * 4,
        GgufTensorType.F16 or GgufTensorType.BF16 => ElementCount * 2,
        _ => throw new InvalidDataException( $"Tensor '{Name}' has an unsupported element type {(uint)Type}." ),
    };
}

/// <summary>
/// Minimal, checked reader for GGUF v3 files (the format the MotionBricks weights ship in).
/// Parses the header, metadata and tensor table eagerly; tensor data is read on demand and
/// converted to <see cref="float"/> arrays. Every length and offset is bounds-checked against
/// the file so a truncated or hostile file fails with <see cref="InvalidDataException"/>.
/// </summary>
public sealed class GgufFile
{
    private const uint Magic = 0x46554747; // "GGUF"
    private const int MaxStringBytes = 1 << 20;
    private const long MaxTensors = 1 << 16;

    private readonly Dictionary<string, object> _metadata = new( StringComparer.Ordinal );
    private readonly Dictionary<string, GgufTensorInfo> _tensors = new( StringComparer.Ordinal );

    public string Path { get; }
    public uint Version { get; private set; }
    public long FileLength { get; private set; }
    public long DataStart { get; private set; }

    public IReadOnlyDictionary<string, object> Metadata => _metadata;
    public IReadOnlyDictionary<string, GgufTensorInfo> Tensors => _tensors;

    private GgufFile( string path )
    {
        Path = path;
    }

    /// <summary>Parses the header and tensor table of <paramref name="path"/>.</summary>
    public static GgufFile Open( string path )
    {
        var file = new GgufFile( path );
        using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16 );
        file.FileLength = stream.Length;
        using var reader = new BinaryReader( stream, Encoding.UTF8, leaveOpen: true );
        file.ReadHeader( reader );
        return file;
    }

    private void ReadHeader( BinaryReader reader )
    {
        if ( FileLength < 24 || reader.ReadUInt32() != Magic )
            throw new InvalidDataException( "Not a GGUF file." );
        Version = reader.ReadUInt32();
        if ( Version != 3 )
            throw new InvalidDataException( $"Unsupported GGUF version {Version} (expected 3)." );
        var tensorCount = reader.ReadInt64();
        var metadataCount = reader.ReadInt64();
        if ( tensorCount < 0 || tensorCount > MaxTensors || metadataCount < 0 || metadataCount > MaxTensors )
            throw new InvalidDataException( "GGUF header counts are out of range." );

        for ( var i = 0; i < metadataCount; i++ )
        {
            var key = ReadString( reader );
            var type = reader.ReadUInt32();
            _metadata[key] = ReadValue( reader, type, depth: 0 );
        }

        var infos = new List<(string Name, long[] Shape, uint Type, long Offset)>();
        for ( var i = 0; i < tensorCount; i++ )
        {
            var name = ReadString( reader );
            var dims = reader.ReadUInt32();
            if ( dims is 0 or > 4 )
                throw new InvalidDataException( $"Tensor '{name}' has {dims} dimensions." );
            var shape = new long[dims];
            for ( var d = 0; d < dims; d++ )
            {
                shape[d] = reader.ReadInt64();
                if ( shape[d] <= 0 || shape[d] > int.MaxValue )
                    throw new InvalidDataException( $"Tensor '{name}' has an invalid dimension." );
            }
            infos.Add( (name, shape, reader.ReadUInt32(), reader.ReadInt64()) );
        }

        var alignment = _metadata.TryGetValue( "general.alignment", out var a ) ? Convert.ToInt64( a ) : 32L;
        if ( alignment <= 0 || alignment > 1 << 20 )
            throw new InvalidDataException( "Invalid GGUF alignment." );
        var position = reader.BaseStream.Position;
        DataStart = (position + alignment - 1) / alignment * alignment;

        foreach ( var (name, shape, rawType, offset) in infos )
        {
            if ( !Enum.IsDefined( typeof( GgufTensorType ), rawType ) )
                throw new InvalidDataException( $"Tensor '{name}' has unsupported type {rawType}." );
            var info = new GgufTensorInfo( name, shape, (GgufTensorType)rawType, offset );
            if ( offset < 0 || DataStart + offset + info.ByteCount > FileLength )
                throw new InvalidDataException( $"Tensor '{name}' lies outside the file (truncated download?)." );
            if ( !_tensors.TryAdd( name, info ) )
                throw new InvalidDataException( $"Duplicate tensor '{name}'." );
        }
    }

    private static string ReadString( BinaryReader reader )
    {
        var length = reader.ReadInt64();
        if ( length < 0 || length > MaxStringBytes )
            throw new InvalidDataException( "GGUF string length is out of range." );
        var bytes = reader.ReadBytes( (int)length );
        if ( bytes.Length != length )
            throw new InvalidDataException( "Unexpected end of GGUF file." );
        return Encoding.UTF8.GetString( bytes );
    }

    private static object ReadValue( BinaryReader reader, uint type, int depth )
    {
        switch ( type )
        {
            case 0: return reader.ReadByte();
            case 1: return reader.ReadSByte();
            case 2: return reader.ReadUInt16();
            case 3: return reader.ReadInt16();
            case 4: return reader.ReadUInt32();
            case 5: return reader.ReadInt32();
            case 6: return reader.ReadSingle();
            case 7: return reader.ReadByte() != 0;
            case 8: return ReadString( reader );
            case 9:
            {
                if ( depth > 2 )
                    throw new InvalidDataException( "GGUF arrays are nested too deeply." );
                var elementType = reader.ReadUInt32();
                var count = reader.ReadInt64();
                if ( count < 0 || count > 1 << 24 )
                    throw new InvalidDataException( "GGUF array length is out of range." );
                var values = new object[count];
                for ( var i = 0; i < count; i++ )
                    values[i] = ReadValue( reader, elementType, depth + 1 );
                return values;
            }
            case 10: return reader.ReadUInt64();
            case 11: return reader.ReadInt64();
            case 12: return reader.ReadDouble();
            default: throw new InvalidDataException( $"Unknown GGUF metadata type {type}." );
        }
    }

    public string? GetString( string key ) => _metadata.TryGetValue( key, out var v ) ? v as string : null;

    public ulong? GetUInt( string key ) => _metadata.TryGetValue( key, out var v ) ? v switch
    {
        uint u => u,
        ulong ul => ul,
        int i when i >= 0 => (ulong)i,
        long l when l >= 0 => (ulong)l,
        _ => null,
    } : null;

    /// <summary>Finds a tensor, following the converter's name compaction for names at GGML's 64-byte limit.</summary>
    public GgufTensorInfo? Find( string name )
    {
        if ( _tensors.TryGetValue( name, out var info ) )
            return info;
        if ( name.Length >= 64 && name.Contains( "self_attn.", StringComparison.Ordinal ) )
            return _tensors.GetValueOrDefault( name.Replace( "self_attn.", "attn.", StringComparison.Ordinal ) );
        return null;
    }

    /// <summary>Reads a float tensor (F32, F16 or BF16 are converted) from an already open stream.</summary>
    public float[] ReadFloats( Stream stream, GgufTensorInfo info )
    {
        var count = checked((int)info.ElementCount);
        var result = new float[count];
        stream.Seek( DataStart + info.DataOffset, SeekOrigin.Begin );
        switch ( info.Type )
        {
            case GgufTensorType.F32:
                ReadExactly( stream, MemoryMarshal.AsBytes( result.AsSpan() ) );
                if ( !BitConverter.IsLittleEndian )
                    throw new PlatformNotSupportedException( "Big-endian hosts are not supported." );
                break;
            case GgufTensorType.F16:
            case GgufTensorType.BF16:
            {
                var raw = new byte[count * 2];
                ReadExactly( stream, raw );
                for ( var i = 0; i < count; i++ )
                {
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian( raw.AsSpan( i * 2 ) );
                    result[i] = info.Type == GgufTensorType.F16
                        ? (float)BitConverter.UInt16BitsToHalf( bits )
                        : BitConverter.Int32BitsToSingle( bits << 16 );
                }
                break;
            }
            default:
                throw new InvalidDataException( $"Tensor '{info.Name}' is not a float tensor." );
        }
        return result;
    }

    public int[] ReadInts( Stream stream, GgufTensorInfo info )
    {
        if ( info.Type != GgufTensorType.I32 )
            throw new InvalidDataException( $"Tensor '{info.Name}' is not an I32 tensor." );
        var result = new int[checked((int)info.ElementCount)];
        stream.Seek( DataStart + info.DataOffset, SeekOrigin.Begin );
        ReadExactly( stream, MemoryMarshal.AsBytes( result.AsSpan() ) );
        return result;
    }

    private static void ReadExactly( Stream stream, Span<byte> destination )
    {
        while ( destination.Length > 0 )
        {
            var read = stream.Read( destination );
            if ( read <= 0 )
                throw new InvalidDataException( "Unexpected end of GGUF tensor data (truncated download?)." );
            destination = destination[read..];
        }
    }
}
