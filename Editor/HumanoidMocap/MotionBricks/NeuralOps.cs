#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Runtime.InteropServices;

namespace HumanoidMocap.MotionBricks;

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

/// <summary>
/// The handful of neural operators the released MotionBricks graphs need, written for batch
/// size one on the CPU: linear layers, layer norm, activations, softmax attention helpers and
/// dilated 1-D convolution. Matrix work is SIMD (<see cref="Vector{T}"/>) and spread over
/// worker threads, leaving one core for the editor.
/// </summary>
public static class NeuralOps
{
    private static readonly ParallelOptions Parallelism = new()
    {
        MaxDegreeOfParallelism = Math.Max( 1, Environment.ProcessorCount - 1 ),
    };

    /// <summary>
    /// <c>y[n, o] = b[o] + sum_i W[o, i] * x[n, i]</c> for <paramref name="n"/> input rows.
    /// </summary>
    public static float[] Linear( ReadOnlySpan<float> x, int n, Tensor weight, Tensor? bias )
    {
        var inDim = weight.Cols;
        var outDim = weight.Rows;
        if ( x.Length < n * inDim )
            throw new ArgumentException( $"Linear '{weight.Name}' expects {inDim} inputs per row." );
        if ( bias is not null && bias.Data.Length != outDim )
            throw new InvalidDataException( $"Bias of '{weight.Name}' has the wrong size." );
        var input = x[..(n * inDim)].ToArray();
        var output = new float[n * outDim];
        var w = weight.Data;
        var b = bias?.Data;

        // Work in blocks of output rows; each block reads its weight rows once for all inputs.
        const int block = 8;
        var blocks = (outDim + block - 1) / block;
        void Run( int blockIndex )
        {
            var start = blockIndex * block;
            var end = Math.Min( outDim, start + block );
            for ( var o = start; o < end; o++ )
            {
                var row = w.AsSpan( o * inDim, inDim );
                var offset = b?[o] ?? 0f;
                for ( var r = 0; r < n; r++ )
                    output[r * outDim + o] = offset + Dot( row, input.AsSpan( r * inDim, inDim ) );
            }
        }

        if ( (long)outDim * inDim * n < 1 << 16 )
        {
            for ( var i = 0; i < blocks; i++ )
                Run( i );
        }
        else
        {
            Parallel.For( 0, blocks, Parallelism, Run );
        }
        return output;
    }

    public static float Dot( ReadOnlySpan<float> a, ReadOnlySpan<float> b )
    {
        var i = 0;
        var sum = 0f;
        if ( Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count * 2 )
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>( a );
            var vb = MemoryMarshal.Cast<float, Vector<float>>( b );
            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var v = 0;
            for ( ; v + 1 < va.Length; v += 2 )
            {
                acc0 += va[v] * vb[v];
                acc1 += va[v + 1] * vb[v + 1];
            }
            for ( ; v < va.Length; v++ )
                acc0 += va[v] * vb[v];
            sum = Vector.Dot( acc0 + acc1, Vector<float>.One );
            i = va.Length * Vector<float>.Count;
        }
        for ( ; i < a.Length; i++ )
            sum += a[i] * b[i];
        return sum;
    }

    public static void Relu( Span<float> values )
    {
        for ( var i = 0; i < values.Length; i++ )
            if ( values[i] < 0f )
                values[i] = 0f;
    }

    public static void LeakyRelu( Span<float> values, float slope = 0.01f )
    {
        for ( var i = 0; i < values.Length; i++ )
            if ( values[i] < 0f )
                values[i] *= slope;
    }

    public static void AddInPlace( Span<float> target, ReadOnlySpan<float> addend )
    {
        for ( var i = 0; i < target.Length; i++ )
            target[i] += addend[i];
    }

    /// <summary>Per-row layer norm (PyTorch semantics, biased variance) with affine scale and bias.</summary>
    public static void LayerNorm( Span<float> values, int rows, int width, Tensor scale, Tensor bias, float epsilon = 1e-5f )
    {
        for ( var r = 0; r < rows; r++ )
        {
            var row = values.Slice( r * width, width );
            var mean = 0f;
            for ( var i = 0; i < width; i++ )
                mean += row[i];
            mean /= width;
            var variance = 0f;
            for ( var i = 0; i < width; i++ )
            {
                var d = row[i] - mean;
                variance += d * d;
            }
            variance /= width;
            var inv = 1f / MathF.Sqrt( variance + epsilon );
            for ( var i = 0; i < width; i++ )
                row[i] = (row[i] - mean) * inv * scale.Data[i] + bias.Data[i];
        }
    }

    public static void SoftmaxInPlace( Span<float> row )
    {
        var max = float.NegativeInfinity;
        foreach ( var v in row )
            if ( v > max )
                max = v;
        var sum = 0f;
        for ( var i = 0; i < row.Length; i++ )
        {
            row[i] = float.IsNegativeInfinity( row[i] ) ? 0f : MathF.Exp( row[i] - max );
            sum += row[i];
        }
        var inv = sum > 0f ? 1f / sum : 0f;
        for ( var i = 0; i < row.Length; i++ )
            row[i] *= inv;
    }

    /// <summary>Row-major <c>[rows, cols]</c> to <c>[cols, rows]</c>.</summary>
    public static float[] Transpose( ReadOnlySpan<float> values, int rows, int cols )
    {
        var result = new float[rows * cols];
        for ( var r = 0; r < rows; r++ )
            for ( var c = 0; c < cols; c++ )
                result[c * rows + r] = values[r * cols + c];
        return result;
    }

    /// <summary>
    /// PyTorch <c>Conv1d</c> (stride 1) on a channel-major <c>[inChannels, length]</c> signal. The
    /// kernel has GGML shape <c>[kernel, inChannels, outChannels]</c>. Returns channel-major output.
    /// </summary>
    public static float[] Conv1d( ReadOnlySpan<float> input, int inChannels, int length, Tensor kernel, Tensor bias, int padding, int dilation )
    {
        var k = kernel.Shape[0];
        if ( kernel.Shape[1] != inChannels )
            throw new InvalidDataException( $"Convolution '{kernel.Name}' expects {kernel.Shape[1]} input channels, got {inChannels}." );
        var outLength = length + 2 * padding - dilation * (k - 1);
        if ( outLength <= 0 )
            throw new InvalidDataException( $"Convolution '{kernel.Name}' has no output frames." );

        // im2col: one row per output frame holding every (channel, tap) input value.
        var columns = new float[outLength * inChannels * k];
        for ( var t = 0; t < outLength; t++ )
        {
            var row = columns.AsSpan( t * inChannels * k, inChannels * k );
            for ( var c = 0; c < inChannels; c++ )
            {
                for ( var tap = 0; tap < k; tap++ )
                {
                    var source = t + tap * dilation - padding;
                    row[c * k + tap] = source >= 0 && source < length ? input[c * length + source] : 0f;
                }
            }
        }
        var flatKernel = new Tensor( kernel.Name, kernel.Data, new[] { inChannels * k, kernel.Shape[2] } );
        var frames = Linear( columns, outLength, flatKernel, bias );
        return Transpose( frames, outLength, kernel.Shape[2] );
    }

    /// <summary>Nearest-neighbour 2x upsampling along time of a channel-major signal.</summary>
    public static float[] UpsampleNearest2x( ReadOnlySpan<float> input, int channels, int length )
    {
        var result = new float[channels * length * 2];
        for ( var c = 0; c < channels; c++ )
            for ( var t = 0; t < length * 2; t++ )
                result[c * length * 2 + t] = input[c * length + t / 2];
        return result;
    }
}
