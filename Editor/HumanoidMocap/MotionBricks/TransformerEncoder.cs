#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HumanoidMocap.MotionBricks;

/// <summary>
/// PyTorch <c>nn.TransformerEncoder</c> as released in MotionBricks: post-norm layers,
/// ReLU feed-forward, packed QKV projection, scaled dot-product attention with an optional
/// additive key mask. Hidden states are row-major <c>[sequence, width]</c>.
/// </summary>
public sealed class TransformerEncoder
{
    private readonly Layer[] _layers;
    private readonly int _width;
    private readonly int _heads;

    private sealed record Layer( Tensor InProj, Tensor InBias, Tensor OutProj, Tensor OutBias,
        Tensor Norm1, Tensor Norm1Bias, Tensor Linear1, Tensor Linear1Bias, Tensor Linear2, Tensor Linear2Bias,
        Tensor Norm2, Tensor Norm2Bias );

    public TransformerEncoder( WeightSet weights, string prefix, int layers, int width, int heads )
    {
        _width = width;
        _heads = heads;
        _layers = new Layer[layers];
        for ( var i = 0; i < layers; i++ )
        {
            var p = $"{prefix}.layers.{i}.";
            _layers[i] = new Layer(
                weights.Shaped( p + "self_attn.in_proj_weight", width, width * 3 ),
                weights[p + "self_attn.in_proj_bias"],
                weights.Shaped( p + "self_attn.out_proj.weight", width, width ),
                weights[p + "self_attn.out_proj.bias"],
                weights[p + "norm1.weight"], weights[p + "norm1.bias"],
                weights[p + "linear1.weight"], weights[p + "linear1.bias"],
                weights[p + "linear2.weight"], weights[p + "linear2.bias"],
                weights[p + "norm2.weight"], weights[p + "norm2.bias"] );
        }
    }

    /// <summary>
    /// Runs every layer over <paramref name="hidden"/> (<c>[sequence, width]</c>, replaced in
    /// place). <paramref name="keyMask"/> is added to attention scores per key (0 or -inf).
    /// </summary>
    public float[] Forward( float[] hidden, int sequence, float[]? keyMask, CancellationToken cancel )
    {
        foreach ( var layer in _layers )
        {
            cancel.ThrowIfCancellationRequested();
            var attention = Attention( hidden, sequence, layer, keyMask );
            NeuralOps.AddInPlace( hidden, attention );
            NeuralOps.LayerNorm( hidden, sequence, _width, layer.Norm1, layer.Norm1Bias );

            var feed = NeuralOps.Linear( hidden, sequence, layer.Linear1, layer.Linear1Bias );
            NeuralOps.Relu( feed );
            feed = NeuralOps.Linear( feed, sequence, layer.Linear2, layer.Linear2Bias );
            NeuralOps.AddInPlace( hidden, feed );
            NeuralOps.LayerNorm( hidden, sequence, _width, layer.Norm2, layer.Norm2Bias );
        }
        return hidden;
    }

    private float[] Attention( float[] hidden, int sequence, Layer layer, float[]? keyMask )
    {
        var qkv = NeuralOps.Linear( hidden, sequence, layer.InProj, layer.InBias );
        var headWidth = _width / _heads;
        var scale = 1f / MathF.Sqrt( headWidth );
        var merged = new float[sequence * _width];
        var stride = _width * 3;
        var scores = new float[sequence];
        for ( var h = 0; h < _heads; h++ )
        {
            var qOffset = h * headWidth;
            var kOffset = _width + h * headWidth;
            var vOffset = 2 * _width + h * headWidth;
            for ( var q = 0; q < sequence; q++ )
            {
                var query = qkv.AsSpan( q * stride + qOffset, headWidth );
                for ( var k = 0; k < sequence; k++ )
                {
                    scores[k] = NeuralOps.Dot( query, qkv.AsSpan( k * stride + kOffset, headWidth ) ) * scale;
                    if ( keyMask is not null )
                        scores[k] += keyMask[k];
                }
                NeuralOps.SoftmaxInPlace( scores.AsSpan( 0, sequence ) );
                var output = merged.AsSpan( q * _width + h * headWidth, headWidth );
                for ( var k = 0; k < sequence; k++ )
                {
                    var weight = scores[k];
                    if ( weight == 0f )
                        continue;
                    var value = qkv.AsSpan( k * stride + vOffset, headWidth );
                    for ( var d = 0; d < headWidth; d++ )
                        output[d] += weight * value[d];
                }
            }
        }
        return NeuralOps.Linear( merged, sequence, layer.OutProj, layer.OutBias );
    }
}
