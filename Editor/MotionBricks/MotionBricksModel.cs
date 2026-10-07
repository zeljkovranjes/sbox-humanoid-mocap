#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Numerics;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The loaded MotionBricks G1 model: root planner, pose planner, VQ decoder and support data.
/// Loading reads ~730 MB of F32 weights; keep one instance per editor session and
/// <see cref="Dispose"/> it to release them. Runs are thread-safe for sequential callers only.
/// </summary>
public sealed class MotionBricksModel : IDisposable
{
    public const int MinTokens = 6;
    public const int MaxTokens = 16;
    public const int FramesPerToken = 4;

    /// <summary>Component files of the bundle with their pinned tensor and parameter counts.</summary>
    public static readonly (string Component, string File, int Tensors, long Parameters)[] Components =
    {
        ("support", "support.gguf", 4, 972),
        ("vq-decoder", "vq-decoder.gguf", 51, 12437277),
        ("root", "root.gguf", 150, 34122833),
        ("pose", "pose.gguf", 209, 136588272),
    };

    private RootPlanner? _root;
    private PosePlanner? _pose;
    private ConditionedConvDecoder? _decoder;
    private float[]? _codebook;
    private readonly object _gate = new();

    public G1Skeleton Skeleton { get; }
    public MotionRepresentation Representation { get; }
    public long ParameterCount { get; }
    public TransitionTiming? LastTiming { get; private set; }

    private MotionBricksModel( G1Skeleton skeleton, MotionRepresentation representation, RootPlanner root, PosePlanner pose,
        ConditionedConvDecoder decoder, float[] codebook, long parameters )
    {
        Skeleton = skeleton;
        Representation = representation;
        _root = root;
        _pose = pose;
        _decoder = decoder;
        _codebook = codebook;
        ParameterCount = parameters;
    }

    /// <summary>Loads and validates the bundle in <paramref name="directory"/>.</summary>
    public static MotionBricksModel Load( string directory, IProgress<float>? progress = null, CancellationToken cancel = default )
    {
        var sets = new Dictionary<string, WeightSet>();
        var totalBytes = Components.Sum( c => new FileInfo( System.IO.Path.Combine( directory, c.File ) ).Length );
        long done = 0;
        foreach ( var (component, file, tensors, expectedParameters) in Components )
        {
            cancel.ThrowIfCancellationRequested();
            var path = System.IO.Path.Combine( directory, file );
            if ( !File.Exists( path ) )
                throw new FileNotFoundException( $"The model file {file} is missing.", path );
            sets[component] = WeightSet.Load( path, component, tensors, expectedParameters, cancel );
            done += new FileInfo( path ).Length;
            progress?.Report( totalBytes > 0 ? done / (float)totalBytes : 1f );
        }

        var support = sets["support"];
        var names = (support.Metadata.TryGetValue( "motionbricks.joint_names", out var n ) ? n as string : null)?.Split( ',' )
            ?? throw new InvalidDataException( "support.gguf has no joint names." );
        var neutralData = support.Shaped( "neutral_joints", 3, G1Skeleton.JointCount ).Data;
        var neutral = Enumerable.Range( 0, G1Skeleton.JointCount ).Select( j => new Vector3( neutralData[j * 3], neutralData[j * 3 + 1], neutralData[j * 3 + 2] ) ).ToArray();
        var skeleton = new G1Skeleton( names, support.Ints( "joint_parents" ), neutral );
        var representation = new MotionRepresentation( skeleton, support.Shaped( "motion_mean", 418 ).Data, support.Shaped( "motion_std", 418 ).Data );

        var vq = sets["vq-decoder"];
        var codebook = vq.Shaped( "quantizer.vq._codebook.embed", 32, 10, 8 ).Data;
        vq.Shaped( "decoder.model.6.weight", 3, 512, 413 );
        var decoder = new ConditionedConvDecoder( vq, "decoder.", 2, MotionRepresentation.LocalMotionWidth );
        var root = new RootPlanner( sets["root"] );
        var pose = new PosePlanner( sets["pose"] );
        var parameters = sets.Values.Where( s => s.Component != "support" ).Sum( s => s.ParameterCount );
        return new MotionBricksModel( skeleton, representation, root, pose, decoder, codebook, parameters );
    }

    /// <summary>
    /// Generates one transition. Returns <c>Tokens * 4</c> frames whose first four stand for the
    /// context and last four for the target, in the (canonical) frame the constraints use.
    /// Port of <c>run_transition</c> in motion-bricks.cpp.
    /// </summary>
    public G1Motion RunTransition( TransitionConstraints c, CancellationToken cancel = default )
    {
        lock ( _gate )
        {
            var root = _root ?? throw new ObjectDisposedException( nameof( MotionBricksModel ) );
            var tokens = Math.Clamp( c.Tokens, MinTokens, MaxTokens );
            var rep = Representation;
            var watch = Stopwatch.StartNew();

            var initialX = c.Context.GlobalRoot[0];
            var initialZ = c.Context.GlobalRoot[2];
            var global = new float[8 * MotionRepresentation.GlobalRootWidth];
            var local = new float[8 * MotionRepresentation.LocalRootWidth];
            var poses = new float[8 * MotionRepresentation.InternalPoseWidth];
            for ( var b = 0; b < 8; b++ )
            {
                var source = b < 4 ? c.Context : c.Target;
                var f = b % 4;
                for ( var i = 0; i < MotionRepresentation.GlobalRootWidth; i++ )
                {
                    var value = source.GlobalRoot[f * MotionRepresentation.GlobalRootWidth + i];
                    if ( i == 0 ) value -= initialX;
                    if ( i == 2 ) value -= initialZ;
                    global[b * MotionRepresentation.GlobalRootWidth + i] = rep.Normalize( value, i );
                }
                for ( var i = 0; i < MotionRepresentation.LocalRootWidth; i++ )
                    local[b * MotionRepresentation.LocalRootWidth + i] = rep.Normalize( source.LocalRoot[f * MotionRepresentation.LocalRootWidth + i], 5 + i );
                poses[b * MotionRepresentation.InternalPoseWidth] = rep.Normalize( source.GlobalRoot[f * MotionRepresentation.GlobalRootWidth + 1], 8 );
                for ( var i = 0; i < MotionRepresentation.ExternalPoseWidth; i++ )
                    poses[b * MotionRepresentation.InternalPoseWidth + 1 + i] = rep.Normalize( source.Poses[f * MotionRepresentation.ExternalPoseWidth + i], 9 + i );
            }

            var planned = root.Run( global, c.HasGlobalRoot, local, c.HasLocalRoot, poses, c.HasPoses, tokens, cancel );
            var frames = tokens * FramesPerToken;
            var rootMs = watch.Elapsed.TotalMilliseconds;

            // Local root features (heading velocity, x/z velocity, height) of the planned path.
            var rawGlobal = new float[frames * MotionRepresentation.GlobalRootWidth];
            var heading = new float[frames];
            for ( var f = 0; f < frames; f++ )
            {
                for ( var i = 0; i < MotionRepresentation.GlobalRootWidth; i++ )
                    rawGlobal[f * 5 + i] = rep.Unnormalize( planned.GlobalRoot[f * 5 + i], i );
                heading[f] = MathF.Atan2( rawGlobal[f * 5 + 4], rawGlobal[f * 5 + 3] );
            }
            var predictedLocal = new float[frames * MotionRepresentation.LocalRootWidth];
            for ( var f = 0; f < frames; f++ )
            {
                var from = f + 1 < frames ? f : f - 1;
                var to = f + 1 < frames ? f + 1 : f;
                var raw = new[]
                {
                    MotionRepresentation.WrapAngle( heading[to] - heading[from] ) * MotionRepresentation.Fps,
                    (rawGlobal[to * 5] - rawGlobal[from * 5]) * MotionRepresentation.Fps,
                    (rawGlobal[to * 5 + 2] - rawGlobal[from * 5 + 2]) * MotionRepresentation.Fps,
                    rawGlobal[f * 5 + 1],
                };
                for ( var i = 0; i < 4; i++ )
                    predictedLocal[f * 4 + i] = rep.Normalize( raw[i], 5 + i );
            }
            if ( c.HasLocalRoot[7] != 0 )
                local.AsSpan( 7 * 4, 4 ).CopyTo( predictedLocal.AsSpan( (frames - 1) * 4 ) );

            var poseCondition = new float[frames * MotionRepresentation.InternalPoseWidth];
            var hasPoseCondition = new byte[frames];
            for ( var b = 0; b < 8; b++ )
            {
                var frame = b < 4 ? b : frames - 8 + b;
                poses.AsSpan( b * MotionRepresentation.InternalPoseWidth, MotionRepresentation.InternalPoseWidth )
                    .CopyTo( poseCondition.AsSpan( frame * MotionRepresentation.InternalPoseWidth ) );
                hasPoseCondition[frame] = c.HasPoses[b];
            }
            var poseRoot = new float[frames * 4];
            for ( var f = 0; f < frames; f++ )
            {
                poseRoot[f * 4] = planned.GlobalRoot[f * 5];
                poseRoot[f * 4 + 1] = planned.GlobalRoot[f * 5 + 2];
                poseRoot[f * 4 + 2] = planned.GlobalRoot[f * 5 + 3];
                poseRoot[f * 4 + 3] = planned.GlobalRoot[f * 5 + 4];
            }

            watch.Restart();
            var codes = new int[tokens * PosePlanner.Heads];
            Array.Fill( codes, PosePlanner.MaskToken );
            var logits = _pose!.Run( codes, poseRoot, poseCondition, hasPoseCondition, tokens, cancel );
            if ( logits.Any( v => !float.IsFinite( v ) ) )
                throw new InvalidDataException( "The pose network produced invalid values." );
            codes = PosePlanner.Sample( logits, c.Seed, c.Argmax );
            var poseMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            var codebook = _codebook!;
            var quantized = new float[tokens * 256];
            for ( var p = 0; p < tokens; p++ )
                for ( var h = 0; h < PosePlanner.Heads; h++ )
                    for ( var d = 0; d < 32; d++ )
                        quantized[(h * 32 + d) * tokens + p] = codebook[(h * PosePlanner.Codes + codes[p * PosePlanner.Heads + h]) * 32 + d];
            var external = new float[frames * 2];
            for ( var f = 0; f < frames; f++ )
            {
                external[f * 2] = predictedLocal[f * 4 + 1];
                external[f * 2 + 1] = predictedLocal[f * 4 + 2];
            }
            var mask = hasPoseCondition.Select( v => v != 0 ? 1f : 0f ).ToArray();
            var decoded = _decoder!.Forward( quantized, 256, tokens, external, poseCondition, mask, cancel );
            var decoderMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            if ( decoded.Any( v => !float.IsFinite( v ) ) )
                throw new InvalidDataException( "The motion decoder produced invalid values." );
            var motion = rep.Decode( decoded, frames, initialX, initialZ );
            LastTiming = new TransitionTiming( rootMs, poseMs, decoderMs, watch.Elapsed.TotalMilliseconds );
            return motion;
        }
    }

    public void Dispose()
    {
        lock ( _gate )
        {
            _root = null;
            _pose = null;
            _decoder = null;
            _codebook = null;
        }
    }
}
