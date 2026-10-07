#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>
/// The MotionBricks pose network: a 16-layer, 1024-wide transformer that predicts eight
/// 10-way pose codes per token from the (masked) codes, the planned root path and the sparse
/// boundary poses. Port of <c>run_pose_planner</c> in motion-bricks.cpp.
/// </summary>
public sealed class PosePlanner
{
    public const int Heads = 8;
    public const int Codes = 10;
    public const int MaskToken = 10;
    private const int Width = 1024;
    private const int PoseWidth = 304;
    private const int FrameEmbedding = 160;

    private readonly WeightSet _w;
    private readonly TransformerEncoder _transformer;

    public PosePlanner( WeightSet weights )
    {
        _w = weights;
        weights.Shaped( "_pose_token_emb.weight", 32, 88 );
        weights.Shaped( "_position_emb.embed", Width, 1, 16 );
        _ = weights["_proj_pose_output_logit.weight"];
        _transformer = new TransformerEncoder( weights, "_transformer_model", 16, Width, 16 );
    }

    /// <param name="poseTokens"><c>[positions, 8]</c> codes (10 = masked).</param>
    /// <param name="rootCondition"><c>[frames, 4]</c> normalized x, z, cos, sin of the planned root.</param>
    /// <param name="poseCondition"><c>[frames, 304]</c> normalized boundary poses (zero elsewhere).</param>
    /// <param name="hasPose">1 on frames that carry a boundary pose.</param>
    /// <returns>Logits <c>[positions, 8, 10]</c>.</returns>
    public float[] Run( int[] poseTokens, float[] rootCondition, float[] poseCondition, byte[] hasPose, int positions, CancellationToken cancel )
    {
        var frames = positions * 4;
        var embeddings = _w["_pose_token_emb.weight"];
        var tokenEmbedding = new float[positions * Heads * 32];
        for ( var p = 0; p < positions; p++ )
            for ( var h = 0; h < Heads; h++ )
                embeddings.Row( poseTokens[p * Heads + h] + h * (Codes + 1) ).CopyTo( tokenEmbedding.AsSpan( (p * Heads + h) * 32 ) );

        var hidden = NeuralOps.Linear( tokenEmbedding, positions, _w["_proj_pose_token_emb.fc_layers.0.weight"], _w["_proj_pose_token_emb.fc_layers.0.bias"] );
        NeuralOps.LeakyRelu( hidden );
        hidden = NeuralOps.Linear( hidden, positions, _w["_proj_pose_token_emb.fc_layers.1.weight"], _w["_proj_pose_token_emb.fc_layers.1.bias"] );
        NeuralOps.LeakyRelu( hidden );
        var tokenFrames = NeuralOps.Linear( hidden, positions, _w["_proj_pose_token_emb.forward_projection.weight"], _w["_proj_pose_token_emb.forward_projection.bias"] );

        var condition = NeuralOps.Linear( poseCondition, frames, _w["_proj_local_pose.weight"], _w["_proj_local_pose.bias"] );
        for ( var f = 0; f < frames; f++ )
        {
            if ( hasPose[f] == 0 )
                continue;
            for ( var c = 0; c < FrameEmbedding; c++ )
            {
                var i = f * FrameEmbedding + c;
                tokenFrames[i] += condition[i] - tokenFrames[i];
            }
        }

        var rootEmbedding = NeuralOps.Linear( rootCondition, positions, _w["_proj_local_root_values.weight"], _w["_proj_local_root_values.bias"] );
        var rootWidth = rootEmbedding.Length / positions;
        var duration = _w["_proj_num_valid_positions.weight"].Row( positions - 6 );
        var poseWidth = 4 * FrameEmbedding;
        var combinedWidth = poseWidth + rootWidth + duration.Length;
        var combined = new float[positions * combinedWidth];
        for ( var p = 0; p < positions; p++ )
        {
            var row = combined.AsSpan( p * combinedWidth, combinedWidth );
            tokenFrames.AsSpan( p * poseWidth, poseWidth ).CopyTo( row );
            rootEmbedding.AsSpan( p * rootWidth, rootWidth ).CopyTo( row[poseWidth..] );
            duration.CopyTo( row[(poseWidth + rootWidth)..] );
        }

        var input = NeuralOps.Linear( combined, positions, _w["_proj_input.0.weight"], _w["_proj_input.0.bias"] );
        NeuralOps.Relu( input );
        NeuralOps.AddInPlace( input, _w["_position_emb.embed"].Data.AsSpan( 0, positions * Width ) );

        // Every position is valid (num_tokens == positions), so no key is masked.
        var output = _transformer.Forward( input, positions, null, cancel );
        return NeuralOps.Linear( output, positions, _w["_proj_pose_output_logit.weight"], _w["_proj_pose_output_logit.bias"] );
    }

    /// <summary>
    /// Gumbel-max sampling over each head's 10 codes (argmax when <paramref name="argmax"/>),
    /// with a private SplitMix64 stream so results are reproducible per seed.
    /// </summary>
    public static int[] Sample( float[] logits, ulong seed, bool argmax )
    {
        var rows = logits.Length / Codes;
        var tokens = new int[rows];
        var state = seed;
        var uniforms = argmax ? null : new float[logits.Length];
        if ( uniforms is not null )
            for ( var i = 0; i < uniforms.Length; i++ )
                uniforms[i] = Uniform( ref state );
        for ( var r = 0; r < rows; r++ )
        {
            var best = float.NegativeInfinity;
            var selected = 0;
            for ( var c = 0; c < Codes; c++ )
            {
                var index = r * Codes + c;
                var score = logits[index] + (uniforms is null ? 0f : Gumbel( uniforms[index] ));
                if ( score > best )
                {
                    best = score;
                    selected = c;
                }
            }
            tokens[r] = selected;
        }
        return tokens;
    }

    private static float Uniform( ref ulong state )
    {
        var z = state += 0x9e3779b97f4a7c15UL;
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        z ^= z >> 31;
        return (z >> 40) * (1f / 16777216f);
    }

    private static float Gumbel( float uniform )
        => -MathF.Log( MathF.Max( -MathF.Log( MathF.Max( uniform, 1e-20f ) ), 1e-20f ) );
}
