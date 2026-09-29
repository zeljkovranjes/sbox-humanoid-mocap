#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HumanoidMocap.MotionBricks;

/// <summary>
/// The MotionBricks root network: embeds the four start and four end boundary frames, predicts
/// the transition duration (token logits) and decodes a dense global root trajectory
/// (<c>[frames, 5]</c>: x, y, z, cos heading, sin heading; normalized) for a chosen duration.
/// Port of <c>run_root_planner_impl</c> in motion-bricks.cpp.
/// </summary>
public sealed class RootPlanner
{
    public const int BoundaryFrames = 8;
    public const int Width = 512;
    public const int MaskedDurationIndex = 12; // upstream duration token 18

    private readonly WeightSet _w;
    private readonly TransformerEncoder _shared;
    private readonly TransformerEncoder _tokens;
    private readonly ConditionedConvDecoder _decoder;

    public RootPlanner( WeightSet weights )
    {
        _w = weights;
        weights.Shaped( "_position_emb.embed", Width, 1, 16 );
        weights.Shaped( "_proj_num_token_output_logit.weight", Width, 12 );
        weights.Shaped( "_conv_output.model.6.weight", 3, Width, 5 );
        _shared = new TransformerEncoder( weights, "_shared_transformer_model", 3, Width, 16 );
        _tokens = new TransformerEncoder( weights, "_root_token_transformer_model", 3, Width, 16 );
        _decoder = new ConditionedConvDecoder( weights, "_conv_output.", Width, 5 );
    }

    public sealed record Result( int Tokens, float[] DurationLogits, float[] GlobalRoot );

    /// <param name="global">Normalized <c>[8, 5]</c> global root boundary features.</param>
    /// <param name="local">Normalized <c>[8, 4]</c> local root boundary features.</param>
    /// <param name="poses">Normalized <c>[8, 304]</c> pose boundary features.</param>
    /// <param name="tokens">Planned duration in tokens (6–16, four frames each).</param>
    public Result Run( float[] global, byte[] hasGlobal, float[] local, byte[] hasLocal, float[] poses, byte[] hasPoses, int tokens, CancellationToken cancel )
    {
        if ( tokens is < 6 or > 16 )
            throw new ArgumentOutOfRangeException( nameof( tokens ) );
        var frames = tokens * 4;

        var poseEmb = Blend( NeuralOps.Linear( poses, BoundaryFrames, _w["_proj_local_pose.weight"], _w["_proj_local_pose.bias"] ), "_no_local_pose_emb", hasPoses );
        var localEmb = Blend( NeuralOps.Linear( local, BoundaryFrames, _w["_proj_local_root_value.weight"], _w["_proj_local_root_value.bias"] ), "_no_local_root_emb", hasLocal );
        var globalEmb = Blend( NeuralOps.Linear( global, BoundaryFrames, _w["_proj_global_root_value.weight"], _w["_proj_global_root_value.bias"] ), "_no_global_root_emb", hasGlobal );
        var pw = poseEmb.Length / BoundaryFrames;
        var lw = localEmb.Length / BoundaryFrames;
        var gw = globalEmb.Length / BoundaryFrames;
        var bw = pw + lw + gw;
        var boundary = new float[BoundaryFrames * bw];
        for ( var f = 0; f < BoundaryFrames; f++ )
        {
            poseEmb.AsSpan( f * pw, pw ).CopyTo( boundary.AsSpan( f * bw ) );
            localEmb.AsSpan( f * lw, lw ).CopyTo( boundary.AsSpan( f * bw + pw ) );
            globalEmb.AsSpan( f * gw, gw ).CopyTo( boundary.AsSpan( f * bw + pw + lw ) );
        }

        var startFrames = LeakyMlp( boundary.AsSpan( 0, 4 * bw ), 4, "_proj_start_input" );
        var endFrames = LeakyMlp( boundary.AsSpan( 4 * bw, 4 * bw ), 4, "_proj_end_input" );
        var frameEmb = new float[BoundaryFrames * Width];
        startFrames.CopyTo( frameEmb, 0 );
        endFrames.CopyTo( frameEmb, 4 * Width );

        var positioned = (float[])frameEmb.Clone();
        NeuralOps.AddInPlace( positioned, _w.Shaped( "_input_position_emb.weight", Width, BoundaryFrames ).Data );

        // Pass 1: duration logits from [duration token, 8 boundary frames].
        var first = new float[(1 + BoundaryFrames) * Width];
        _w["_proj_input_num_tokens.weight"].Row( MaskedDurationIndex ).CopyTo( first );
        positioned.CopyTo( first, Width );
        first = _shared.Forward( first, 1 + BoundaryFrames, null, cancel );
        var durationLogits = NeuralOps.Linear( first.AsSpan( 0, Width ), 1, _w["_proj_num_token_output_logit.weight"], _w["_proj_num_token_output_logit.bias"] );

        // Pass 2: root tokens from [planned duration, 8 boundary frames, positions].
        var sequence = 1 + BoundaryFrames + tokens;
        var second = new float[sequence * Width];
        _w["_middle_token_emb.weight"].Row( tokens - 6 ).CopyTo( second );
        positioned.CopyTo( second, Width );
        _w["_position_emb.embed"].Data.AsSpan( 0, tokens * Width ).CopyTo( second.AsSpan( (1 + BoundaryFrames) * Width ) );
        second = _tokens.Forward( second, sequence, null, cancel );
        var rootTokens = NeuralOps.Transpose( second.AsSpan( (1 + BoundaryFrames) * Width, tokens * Width ), tokens, Width );

        // Dense per-frame condition: boundary frame embeddings where present, "no frame" elsewhere.
        var absentFrame = _w["_conv_no_frame_emb"].Data;
        var dense = new float[frames * Width];
        for ( var f = 0; f < frames; f++ )
            absentFrame.AsSpan( 0, Width ).CopyTo( dense.AsSpan( f * Width ) );
        for ( var b = 0; b < BoundaryFrames; b++ )
        {
            var has = Math.Clamp( hasGlobal[b] + hasLocal[b] + hasPoses[b], 0, 1 );
            var frame = b < 4 ? b : frames - 8 + b;
            for ( var c = 0; c < Width; c++ )
                dense[frame * Width + c] = absentFrame[c] + (frameEmb[b * Width + c] - absentFrame[c]) * has;
        }

        var denseTarget = new float[frames * 5];
        var denseMask = new float[frames];
        for ( var b = 0; b < BoundaryFrames; b++ )
        {
            var frame = b < 4 ? b : frames - 8 + b;
            global.AsSpan( b * 5, 5 ).CopyTo( denseTarget.AsSpan( frame * 5 ) );
            denseMask[frame] = hasGlobal[b] != 0 ? 1f : 0f;
        }

        var root = _decoder.Forward( rootTokens, Width, tokens, dense, denseTarget, denseMask, cancel );
        return new Result( tokens, durationLogits, root );
    }

    private float[] Blend( float[] projected, string absentName, byte[] mask )
    {
        var absent = _w[absentName].Data;
        var width = absent.Length;
        for ( var f = 0; f < mask.Length; f++ )
        {
            var m = mask[f] != 0 ? 1f : 0f;
            for ( var c = 0; c < width; c++ )
            {
                var i = f * width + c;
                projected[i] = absent[c] + (projected[i] - absent[c]) * m;
            }
        }
        return projected;
    }

    private float[] LeakyMlp( ReadOnlySpan<float> input, int rows, string prefix )
    {
        var hidden = input.ToArray();
        for ( var layer = 0; layer < 2; layer++ )
        {
            hidden = NeuralOps.Linear( hidden, rows, _w[$"{prefix}.fc_layers.{layer}.weight"], _w[$"{prefix}.fc_layers.{layer}.bias"] );
            NeuralOps.LeakyRelu( hidden );
        }
        return NeuralOps.Linear( hidden, rows, _w[$"{prefix}.forward_projection.weight"], _w[$"{prefix}.forward_projection.bias"] );
    }
}
