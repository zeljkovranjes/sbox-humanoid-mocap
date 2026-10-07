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

namespace HumanoidMocap.EditorTools.MotionBricks;

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
