#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace HumanoidMocap.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Boundary features of four consecutive frames, unnormalized:
/// global root <c>[4, 5]</c> (x, y, z, cos heading, sin heading), local root <c>[4, 4]</c>
/// (heading velocity, x velocity, z velocity, height) and poses <c>[4, 303]</c>
/// (33 root-relative joint positions, 34 global 6D rotations).
/// </summary>
public sealed class EncodedFrames
{
    public readonly float[] GlobalRoot = new float[4 * MotionRepresentation.GlobalRootWidth];
    public readonly float[] LocalRoot = new float[4 * MotionRepresentation.LocalRootWidth];
    public readonly float[] Poses = new float[4 * MotionRepresentation.ExternalPoseWidth];
}

/// <summary>
/// MotionBricks' motion representation: feature encoding of boundary frames, normalization
/// with the released statistics, and decoding of the network's local-motion output back to
/// root translations and local joint rotations. Port of <c>motion_rep.cpp</c>; matrices are
/// row-major 3x3 in <see cref="float"/>[9] exactly as upstream.
/// </summary>
public sealed class MotionRepresentation
{
    public const float Fps = 30f;
    public const int GlobalRootWidth = 5;
    public const int LocalRootWidth = 4;
    public const int ExternalPoseWidth = 303;
    public const int InternalPoseWidth = 304;
    public const int LocalMotionWidth = 413;
    private const float StatsEpsilon = 1e-5f;

    private readonly float[] _mean;
    private readonly float[] _std;
    public G1Skeleton Skeleton { get; }

    public MotionRepresentation( G1Skeleton skeleton, float[] mean, float[] std )
    {
        if ( mean.Length != 418 || std.Length != 418 )
            throw new InvalidDataException( "MotionBricks normalization statistics must have 418 values." );
        if ( std.Any( v => !float.IsFinite( v ) || v < 0f ) || mean.Any( v => !float.IsFinite( v ) ) )
            throw new InvalidDataException( "MotionBricks normalization statistics are invalid." );
        Skeleton = skeleton;
        _mean = mean;
        _std = std;
    }

    public float Normalize( float value, int dualIndex ) => (value - _mean[dualIndex]) / MathF.Sqrt( _std[dualIndex] * _std[dualIndex] + StatsEpsilon );

    public float Unnormalize( float value, int dualIndex ) => value * MathF.Sqrt( _std[dualIndex] * _std[dualIndex] + StatsEpsilon ) + _mean[dualIndex];

    // ------------------------------------------------------------------ encoding

    /// <summary>
    /// Encodes four frames given as root translations and local rotations (already in the
    /// canonical model frame). Virtual endpoints keep FK positions but read identity rotations,
    /// matching upstream's dummy-joint scheme.
    /// </summary>
    public EncodedFrames Encode( ReadOnlySpan<Vector3> roots, IReadOnlyList<Quaternion[]> locals, bool repeatLastVelocity )
    {
        if ( roots.Length != 4 || locals.Count != 4 )
            throw new ArgumentException( "Boundary encoding needs exactly four frames." );
        var positions = new Vector3[4 * G1Skeleton.JointCount];
        var rotations = new float[4 * G1Skeleton.JointCount * 9];
        var globalQ = new Quaternion[G1Skeleton.JointCount];
        var globalP = new Vector3[G1Skeleton.JointCount];
        for ( var f = 0; f < 4; f++ )
        {
            if ( locals[f].Length != G1Skeleton.JointCount )
                throw new ArgumentException( "Each frame needs 34 joint rotations." );
            Skeleton.Forward( roots[f], locals[f], globalQ, globalP );
            for ( var j = 0; j < G1Skeleton.JointCount; j++ )
            {
                positions[f * G1Skeleton.JointCount + j] = globalP[j];
                var m = Array.IndexOf( G1Skeleton.VirtualJoints, j ) >= 0 ? Identity : Matrix( globalQ[j] );
                m.CopyTo( rotations, (f * G1Skeleton.JointCount + j) * 9 );
            }
        }
        return EncodeGlobal( positions, rotations, repeatLastVelocity );
    }

    private static readonly float[] Identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    /// <summary>Encodes four frames of global joint positions and row-major rotation matrices.</summary>
    public static EncodedFrames EncodeGlobal( Vector3[] positions, float[] rotations, bool repeatLastVelocity )
    {
        var output = new EncodedFrames();
        var headings = new float[4];
        const int j34 = G1Skeleton.JointCount;
        for ( var f = 0; f < 4; f++ )
        {
            var root = positions[f * j34];
            var rootRotation = rotations.AsSpan( f * j34 * 9, 9 );
            headings[f] = MathF.Atan2( rootRotation[2], rootRotation[8] );
            var global = output.GlobalRoot.AsSpan( f * GlobalRootWidth, GlobalRootWidth );
            global[0] = root.X;
            global[1] = root.Y;
            global[2] = root.Z;
            global[3] = MathF.Cos( headings[f] );
            global[4] = MathF.Sin( headings[f] );

            var pose = output.Poses.AsSpan( f * ExternalPoseWidth, ExternalPoseWidth );
            for ( var j = 1; j < j34; j++ )
            {
                var p = positions[f * j34 + j];
                pose[(j - 1) * 3] = p.X - root.X;
                pose[(j - 1) * 3 + 1] = p.Y;
                pose[(j - 1) * 3 + 2] = p.Z - root.Z;
            }
            const int rotationOffset = (j34 - 1) * 3;
            for ( var j = 0; j < j34; j++ )
            {
                var m = rotations.AsSpan( (f * j34 + j) * 9, 9 );
                var six = pose.Slice( rotationOffset + j * 6, 6 );
                six[0] = m[0]; six[1] = m[3]; six[2] = m[6];
                six[3] = m[1]; six[4] = m[4]; six[5] = m[7];
            }
        }
        for ( var f = 0; f + 1 < 4; f++ )
        {
            var local = output.LocalRoot.AsSpan( f * LocalRootWidth, LocalRootWidth );
            var current = output.GlobalRoot.AsSpan( f * GlobalRootWidth, GlobalRootWidth );
            var next = output.GlobalRoot.AsSpan( (f + 1) * GlobalRootWidth, GlobalRootWidth );
            local[0] = WrapAngle( headings[f + 1] - headings[f] ) * Fps;
            local[1] = (next[0] - current[0]) * Fps;
            local[2] = (next[2] - current[2]) * Fps;
            local[3] = current[1];
        }
        if ( repeatLastVelocity )
        {
            output.LocalRoot.AsSpan( 2 * LocalRootWidth, LocalRootWidth ).CopyTo( output.LocalRoot.AsSpan( 3 * LocalRootWidth ) );
            output.LocalRoot[3 * LocalRootWidth + 3] = output.GlobalRoot[3 * GlobalRootWidth + 1];
        }
        return output;
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>
    /// Converts the decoder's normalized local motion (<c>[frames, 413]</c>) into root
    /// translations (integrated from <paramref name="initialX"/>, <paramref name="initialZ"/>) and
    /// local joint rotations.
    /// </summary>
    public G1Motion Decode( float[] normalizedLocalMotion, int frames, float initialX, float initialZ )
    {
        if ( normalizedLocalMotion.Length != frames * LocalMotionWidth )
            throw new InvalidDataException( "Decoded motion has an unexpected size." );
        var motion = new G1Motion( frames );
        var raw = new float[LocalMotionWidth];
        var global = new float[G1Skeleton.JointCount][];
        var x = initialX;
        var z = initialZ;
        for ( var f = 0; f < frames; f++ )
        {
            for ( var i = 0; i < LocalMotionWidth; i++ )
            {
                var dual = i < 4 ? 5 + i : 9 + i - 4;
                raw[i] = Unnormalize( normalizedLocalMotion[f * LocalMotionWidth + i], dual );
            }
            motion.Root[f] = new Vector3( x, raw[3], z );
            if ( f + 1 < frames )
            {
                x += raw[1] / Fps;
                z += raw[2] / Fps;
            }
            const int rotationOffset = 4 + 99;
            for ( var j = 0; j < G1Skeleton.JointCount; j++ )
                global[j] = Cont6dMatrix( raw.AsSpan( rotationOffset + j * 6, 6 ) );
            for ( var j = 0; j < G1Skeleton.JointCount; j++ )
            {
                var parent = Skeleton.Parents[j];
                var local = parent < 0 ? global[j] : Multiply( Transpose( global[parent] ), global[j] );
                motion.Local[f][j] = FromMatrix( local );
            }
        }
        return motion;
    }

    // ------------------------------------------------------------------ small maths (upstream conventions)

    public static float WrapAngle( float value ) => (float)Math.IEEERemainder( value, 2.0 * Math.PI );

    /// <summary>Row-major rotation matrix of a quaternion (upstream <c>quaternion_matrix</c>).</summary>
    public static float[] Matrix( Quaternion q )
    {
        var norm = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        if ( !(norm > 1e-12f) || !float.IsFinite( norm ) )
            return (float[])Identity.Clone();
        var s = 2f / norm;
        float w = q.W, x = q.X, y = q.Y, z = q.Z;
        return new[]
        {
            1f - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w),
            s * (x * y + z * w), 1f - s * (x * x + z * z), s * (y * z - x * w),
            s * (x * z - y * w), s * (y * z + x * w), 1f - s * (x * x + y * y),
        };
    }

    public static float[] Multiply( float[] a, float[] b )
    {
        var r = new float[9];
        for ( var row = 0; row < 3; row++ )
            for ( var col = 0; col < 3; col++ )
                for ( var k = 0; k < 3; k++ )
                    r[row * 3 + col] += a[row * 3 + k] * b[k * 3 + col];
        return r;
    }

    public static float[] Transpose( float[] m ) => new[] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

    /// <summary>Quaternion of a row-major rotation matrix with non-negative W (upstream <c>matrix_quaternion</c>).</summary>
    public static Quaternion FromMatrix( float[] m )
    {
        float w, x, y, z;
        var trace = m[0] + m[4] + m[8];
        if ( trace > 0f )
        {
            var s = 2f * MathF.Sqrt( MathF.Max( 0f, trace + 1f ) );
            w = 0.25f * s; x = (m[7] - m[5]) / s; y = (m[2] - m[6]) / s; z = (m[3] - m[1]) / s;
        }
        else if ( m[0] > m[4] && m[0] > m[8] )
        {
            var s = 2f * MathF.Sqrt( MathF.Max( 0f, 1f + m[0] - m[4] - m[8] ) );
            w = (m[7] - m[5]) / s; x = 0.25f * s; y = (m[1] + m[3]) / s; z = (m[2] + m[6]) / s;
        }
        else if ( m[4] > m[8] )
        {
            var s = 2f * MathF.Sqrt( MathF.Max( 0f, 1f + m[4] - m[0] - m[8] ) );
            w = (m[2] - m[6]) / s; x = (m[1] + m[3]) / s; y = 0.25f * s; z = (m[5] + m[7]) / s;
        }
        else
        {
            var s = 2f * MathF.Sqrt( MathF.Max( 0f, 1f + m[8] - m[0] - m[4] ) );
            w = (m[3] - m[1]) / s; x = (m[2] + m[6]) / s; y = (m[5] + m[7]) / s; z = 0.25f * s;
        }
        var q = new Quaternion( x, y, z, w );
        var length = q.Length();
        if ( length > 0f )
            q = Quaternion.Multiply( q, 1f / length );
        if ( q.W < 0f )
            q = Quaternion.Negate( q );
        return q;
    }

    /// <summary>Rotation matrix from the continuous 6D representation (first two columns).</summary>
    public static float[] Cont6dMatrix( ReadOnlySpan<float> v )
    {
        var x = Normalize( new Vector3( v[0], v[1], v[2] ) );
        var yRaw = new Vector3( v[3], v[4], v[5] );
        var z = Normalize( Vector3.Cross( x, yRaw ) );
        var y = Vector3.Cross( z, x );
        return new[] { x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z };
    }

    private static Vector3 Normalize( Vector3 v )
    {
        var length = v.Length();
        return length > 1e-12f ? v / length : v;
    }

    /// <summary>Heading (rotation about +Y) of a root rotation, as upstream measures it.</summary>
    public static float Heading( Quaternion rootRotation )
    {
        var m = Matrix( rootRotation );
        return MathF.Atan2( m[2], m[8] );
    }
}
