#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Runtime.InteropServices;

namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>
/// A dense F32 tensor. <see cref="Shape"/> uses GGML order (fastest dimension first), so a
/// PyTorch <c>Linear(in, out)</c> weight has shape <c>[in, out]</c> and is stored row-major
/// as <c>out</c> rows of <c>in</c> values.
/// </summary>
public sealed class Tensor
{
    public string Name { get; }
    public float[] Data { get; }
    public int[] Shape { get; }

    public Tensor( string name, float[] data, int[] shape )
    {
        Name = name;
        Data = data;
        Shape = shape;
        var count = shape.Aggregate( 1L, ( a, b ) => a * b );
        if ( count != data.Length )
            throw new InvalidDataException( $"Tensor '{name}' holds {data.Length} values but its shape needs {count}." );
    }

    /// <summary>Length of one row (GGML ne0).</summary>
    public int Cols => Shape[0];

    /// <summary>Number of rows (product of the remaining dimensions).</summary>
    public int Rows => Data.Length / Shape[0];

    public ReadOnlySpan<float> Row( int index ) => Data.AsSpan( index * Cols, Cols );
}
