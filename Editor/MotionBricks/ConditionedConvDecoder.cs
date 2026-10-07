#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>
/// The convolutional token-to-frame decoder MotionBricks uses twice (root trajectory and VQ
/// pose decoding). Each token covers four frames; two stages each inject the sparse boundary
/// targets and a per-frame external condition, run a dilated residual stack (27, 9, 3, 1) and
/// upsample time 2x.
/// </summary>
public sealed class ConditionedConvDecoder
{
    private static readonly int[] Dilations = { 27, 9, 3, 1 };
    private const int Width = 512;

    private readonly WeightSet _w;
    private readonly string _prefix;
    private readonly int _externalWidth;
    private readonly int _outputWidth;

    /// <param name="prefix">Tensor name prefix, e.g. <c>"_conv_output."</c> or <c>"decoder."</c>.</param>
    public ConditionedConvDecoder( WeightSet weights, string prefix, int externalWidth, int outputWidth )
    {
        _w = weights;
        _prefix = prefix;
        _externalWidth = externalWidth;
        _outputWidth = outputWidth;
        // Resolve eagerly so a mismatched file fails at load time, not mid-generation.
        _ = weights[prefix + "model.0.weight"];
        _ = weights[prefix + "model.6.weight"];
    }

    /// <param name="tokens">Channel-major <c>[channels, positions]</c> token features.</param>
    /// <param name="external">Frame-major <c>[frames, externalWidth]</c> condition.</param>
    /// <param name="target">Frame-major <c>[frames, targetWidth]</c> sparse targets.</param>
    /// <param name="targetMask">1 where a frame has a target, else 0.</param>
    /// <returns>Frame-major <c>[frames, outputWidth]</c>.</returns>
    public float[] Forward( float[] tokens, int tokenChannels, int positions, float[] external, float[] target, float[] targetMask, CancellationToken cancel )
    {
        var frames = positions * 4;
        var hidden = Conv( tokens, tokenChannels, positions, "model.0", 1, 1 );
        NeuralOps.Relu( hidden );

        for ( var stageIndex = 0; stageIndex < 2; stageIndex++ )
        {
            cancel.ThrowIfCancellationRequested();
            var stage = stageIndex + 2;
            var group = 1 << (2 - stageIndex);
            var stagePositions = positions << stageIndex;
            var groupWidth = Width / group;

            var targetEmbedding = NeuralOps.Linear( target, frames,
                _w[$"{_prefix}target_cond_blocks.{stageIndex * 2}.weight"], _w[$"{_prefix}target_cond_blocks.{stageIndex * 2}.bias"] );
            NeuralOps.Relu( targetEmbedding );

            // [stagePositions, 512] frame-major rows are exactly [frames, 512 / group].
            var hiddenFrames = NeuralOps.Transpose( hidden, Width, stagePositions );
            for ( var f = 0; f < frames; f++ )
            {
                var mask = targetMask[f];
                if ( mask == 0f )
                    continue;
                for ( var c = 0; c < groupWidth; c++ )
                {
                    var index = f * groupWidth + c;
                    hiddenFrames[index] += (targetEmbedding[index] - hiddenFrames[index]) * mask;
                }
            }

            var groupedExternal = _externalWidth * group;
            var fusedWidth = Width + groupedExternal;
            var fused = new float[stagePositions * fusedWidth];
            for ( var p = 0; p < stagePositions; p++ )
            {
                hiddenFrames.AsSpan( p * Width, Width ).CopyTo( fused.AsSpan( p * fusedWidth ) );
                external.AsSpan( p * groupedExternal, groupedExternal ).CopyTo( fused.AsSpan( p * fusedWidth + Width ) );
            }
            var projected = NeuralOps.Linear( fused, stagePositions,
                _w[$"{_prefix}external_cond_blocks.{stageIndex * 2}.weight"], _w[$"{_prefix}external_cond_blocks.{stageIndex * 2}.bias"] );
            NeuralOps.Relu( projected );
            hidden = NeuralOps.Transpose( projected, stagePositions, Width );

            for ( var block = 0; block < Dilations.Length; block++ )
            {
                var d = Dilations[block];
                var branch = (float[])hidden.Clone();
                NeuralOps.Relu( branch );
                branch = Conv( branch, Width, stagePositions, $"model.{stage}.0.model.{block}.conv1", d, d );
                NeuralOps.Relu( branch );
                branch = Conv( branch, Width, stagePositions, $"model.{stage}.0.model.{block}.conv2", 0, 1 );
                NeuralOps.AddInPlace( hidden, branch );
            }

            hidden = NeuralOps.UpsampleNearest2x( hidden, Width, stagePositions );
            hidden = Conv( hidden, Width, stagePositions * 2, $"model.{stage}.2", 1, 1 );
        }

        hidden = Conv( hidden, Width, frames, "model.4", 1, 1 );
        NeuralOps.Relu( hidden );
        hidden = Conv( hidden, Width, frames, "model.6", 1, 1 );
        return NeuralOps.Transpose( hidden, _outputWidth, frames );
    }

    private float[] Conv( float[] input, int channels, int length, string name, int padding, int dilation )
        => NeuralOps.Conv1d( input, channels, length, _w[$"{_prefix}{name}.weight"], _w[$"{_prefix}{name}.bias"], padding, dilation );
}
