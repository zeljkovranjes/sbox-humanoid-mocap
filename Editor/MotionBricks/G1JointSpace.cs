#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The G1's actual joint structure in MotionBricks' motion space (Y up, Z forward, X left):
/// every non-root, non-virtual joint is a hinge whose local rotation is
/// <c>BodyOffset * AxisAngle(Axis, angle)</c> (upstream <c>mujoco_qpos_converter</c>, taken from
/// <c>assets/skeletons/g1/g1.xml</c>). Real training motions only ever contain such rotations, so
/// poses built by retargeting (which rotate the first link of each limb freely) are projected
/// onto these hinges before they condition the network.
/// </summary>
public static class G1JointSpace
{
    private readonly record struct Hinge( Vector3 Axis, Quaternion Offset );


    private readonly record struct Objective( int Joint, float Weight );

    /// <summary>Hinges solved together (parents first) and what they must reproduce.</summary>
    private sealed record Group( int[] Joints, Objective[] Objectives );

    private static readonly Vector3 X = Vector3.UnitX; // MuJoCo Y (pitch)
    private static readonly Vector3 Y = Vector3.UnitY; // MuJoCo Z (yaw)
    private static readonly Vector3 Z = Vector3.UnitZ; // MuJoCo X (roll)

    /// <summary>Pull of every hinge toward zero, resolving twists that the objectives leave free.</summary>
    private const float NeutralPull = 0.05f;

    /// <summary>MuJoCo (w, x, y, z) body quaternion expressed in motion space.</summary>
    private static Quaternion Mj( float w, float x, float y, float z ) => Quaternion.Normalize( new Quaternion( y, z, x, w ) );

    private static readonly Hinge?[] Hinges = BuildHinges();
    private static readonly Group[] Groups = BuildGroups();

    private static Hinge?[] BuildHinges()
    {
        var h = new Hinge?[G1Skeleton.JointCount];
        var none = Quaternion.Identity;
        foreach ( var side in new[] { 0, 7 } )
        {
            h[1 + side] = new Hinge( X, none );                                   // hip pitch
            h[2 + side] = new Hinge( Z, Mj( 0.996179f, 0f, -0.0873386f, 0f ) );   // hip roll
            h[3 + side] = new Hinge( Y, none );                                   // hip yaw
            h[4 + side] = new Hinge( X, Mj( 0.996179f, 0f, 0.0873386f, 0f ) );    // knee
            h[5 + side] = new Hinge( X, none );                                   // ankle pitch
            h[6 + side] = new Hinge( Z, none );                                   // ankle roll
        }
        h[15] = new Hinge( Y, none );   // waist yaw
        h[16] = new Hinge( Z, none );   // waist roll
        h[17] = new Hinge( X, none );   // waist pitch
        h[18] = new Hinge( X, Mj( 0.990264f, 0.139201f, 1.38722e-05f, -9.86868e-05f ) );  // left shoulder pitch
        h[19] = new Hinge( Z, Mj( 0.990268f, -0.139172f, 0f, 0f ) );                        // left shoulder roll
        h[20] = new Hinge( Y, none );   // left shoulder yaw
        h[21] = new Hinge( X, none );   // left elbow
        h[22] = new Hinge( Z, none );   // left wrist roll
        h[23] = new Hinge( X, none );   // left wrist pitch
        h[24] = new Hinge( Y, none );   // left wrist yaw
        h[26] = new Hinge( X, Mj( 0.990264f, -0.139201f, 1.38722e-05f, 9.86868e-05f ) );  // right shoulder pitch
        h[27] = new Hinge( Z, Mj( 0.990268f, 0.139172f, 0f, 0f ) );                         // right shoulder roll
        h[28] = new Hinge( Y, none );
        h[29] = new Hinge( X, none );
        h[30] = new Hinge( Z, none );
        h[31] = new Hinge( X, none );
        h[32] = new Hinge( Y, none );
        return h;
    }

    private static Group[] BuildGroups()
    {
        static Objective R( int joint, float weight = 1f ) => new( joint, weight );
        return new[]
        {
            // Torso orientation.
            new Group( new[] { 15, 16, 17 }, new[] { R( 17 ) } ),
            // Each limb keeps the world orientation of the links the retargeter maps (the last
            // link of every hinge chain); a knee or elbow can only bend, so it matches in the
            // least-squares sense.
            new Group( new[] { 1, 2, 3, 4 }, new[] { R( 3 ), R( 4 ) } ),
            new Group( new[] { 5, 6 }, new[] { R( 6 ) } ),
            new Group( new[] { 8, 9, 10, 11 }, new[] { R( 10 ), R( 11 ) } ),
            new Group( new[] { 12, 13 }, new[] { R( 13 ) } ),
            new Group( new[] { 18, 19, 20, 21 }, new[] { R( 20 ), R( 21 ) } ),
            new Group( new[] { 22, 23, 24 }, new[] { R( 24 ) } ),
            new Group( new[] { 26, 27, 28, 29 }, new[] { R( 28 ), R( 29 ) } ),
            new Group( new[] { 30, 31, 32 }, new[] { R( 32 ) } ),
        };
    }

    /// <summary>Local rotations of the G1 neutral pose (all hinge angles zero).</summary>
    public static Quaternion[] NeutralLocals()
    {
        var locals = new Quaternion[G1Skeleton.JointCount];
        for ( var j = 0; j < locals.Length; j++ )
            locals[j] = Hinges[j]?.Offset ?? Quaternion.Identity;
        return locals;
    }

    /// <summary>Local rotation of hinge <paramref name="joint"/> at <paramref name="angle"/>.</summary>
    public static Quaternion Local( int joint, float angle )
    {
        var hinge = Hinges[joint] ?? throw new ArgumentException( $"Joint {joint} is not a hinge." );
        return Quaternion.Normalize( hinge.Offset * Quaternion.CreateFromAxisAngle( hinge.Axis, angle ) );
    }

    /// <summary>
    /// Replaces the locals of a pose with the closest hinge configuration: limb by limb (parents
    /// first) the hinge angles are fitted so the torso, thighs, shins, feet, upper arms, forearms
    /// and hands keep their world orientation. <paramref name="angles"/> (34 values, updated in
    /// place) warm-starts the fit, keeping consecutive frames coherent. Virtual endpoints get
    /// identity rotations.
    /// </summary>
    public static Quaternion[] Project( G1Skeleton skeleton, ReadOnlySpan<Quaternion> locals, float[] angles )
    {
        var target = WorldRotations( skeleton, locals );
        var result = NeutralLocals();
        result[0] = Quaternion.Normalize( locals[0] );
        for ( var j = 1; j < G1Skeleton.JointCount; j++ )
            if ( Hinges[j] is not null )
                result[j] = Local( j, angles[j] );
        foreach ( var group in Groups )
            SolveGroup( skeleton, group, result, angles, target );
        return result;
    }

    private static Quaternion[] WorldRotations( G1Skeleton skeleton, ReadOnlySpan<Quaternion> locals )
    {
        var world = new Quaternion[G1Skeleton.JointCount];
        for ( var j = 0; j < G1Skeleton.JointCount; j++ )
        {
            var parent = skeleton.Parents[j];
            world[j] = Quaternion.Normalize( parent < 0 ? locals[j] : world[parent] * locals[j] );
        }
        return world;
    }

    /// <summary>Levenberg-Marquardt on one group's hinge angles.</summary>
    private static void SolveGroup( G1Skeleton skeleton, Group group, Quaternion[] result, float[] angles, Quaternion[] target )
    {
        var n = group.Joints.Length;
        var m = group.Objectives.Length * 3 + n;
        var residual = new float[m];
        var trial = new float[m];
        var jacobian = new float[m, n];
        const float step = 1e-3f;
        var damping = 1e-3f;

        float Evaluate( float[] into )
        {
            foreach ( var j in group.Joints )
                result[j] = Local( j, angles[j] );
            var world = WorldRotations( skeleton, result );
            var k = 0;
            var cost = 0f;
            foreach ( var o in group.Objectives )
            {
                var e = RotationError( world[o.Joint], target[o.Joint] ) * o.Weight;
                into[k++] = e.X;
                into[k++] = e.Y;
                into[k++] = e.Z;
                cost += e.LengthSquared();
            }
            foreach ( var j in group.Joints )
            {
                var prior = angles[j] * NeutralPull;
                into[k++] = prior;
                cost += prior * prior;
            }
            return cost;
        }

        var cost = Evaluate( residual );
        for ( var iteration = 0; iteration < 20 && cost > 1e-10f; iteration++ )
        {
            for ( var i = 0; i < n; i++ )
            {
                var joint = group.Joints[i];
                var saved = angles[joint];
                angles[joint] = saved + step;
                Evaluate( trial );
                angles[joint] = saved;
                for ( var r = 0; r < m; r++ )
                    jacobian[r, i] = (trial[r] - residual[r]) / step;
            }
            var jtj = new float[n, n];
            var jte = new float[n];
            for ( var a = 0; a < n; a++ )
            {
                for ( var r = 0; r < m; r++ )
                    jte[a] -= jacobian[r, a] * residual[r];
                for ( var b = 0; b < n; b++ )
                {
                    var sum = 0f;
                    for ( var r = 0; r < m; r++ )
                        sum += jacobian[r, a] * jacobian[r, b];
                    jtj[a, b] = sum;
                }
                jtj[a, a] += damping * (1f + jtj[a, a]);
            }
            var delta = SolveSmall( jtj, jte );
            var saved2 = group.Joints.Select( j => angles[j] ).ToArray();
            for ( var i = 0; i < n; i++ )
                angles[group.Joints[i]] = WrapAngle( angles[group.Joints[i]] + Math.Clamp( delta[i], -0.5f, 0.5f ) );
            var newCost = Evaluate( trial );
            if ( newCost < cost )
            {
                cost = newCost;
                trial.CopyTo( residual, 0 );
                damping = MathF.Max( 1e-6f, damping * 0.3f );
                if ( delta.All( d => MathF.Abs( d ) < 1e-5f ) )
                    break;
            }
            else
            {
                for ( var i = 0; i < n; i++ )
                    angles[group.Joints[i]] = saved2[i];
                damping *= 10f;
                Evaluate( residual );
                if ( damping > 1e4f )
                    break;
            }
        }
        foreach ( var j in group.Joints )
            result[j] = Local( j, angles[j] );
    }

    /// <summary>Rotation vector taking <paramref name="current"/> to <paramref name="target"/> (world frame).</summary>
    private static Vector3 RotationError( Quaternion current, Quaternion target )
    {
        var delta = Quaternion.Normalize( target * Quaternion.Conjugate( Quaternion.Normalize( current ) ) );
        if ( delta.W < 0f )
            delta = Quaternion.Negate( delta );
        var sinHalf = MathF.Sqrt( delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z );
        if ( sinHalf < 1e-8f )
            return Vector3.Zero;
        var angle = 2f * MathF.Atan2( sinHalf, delta.W );
        return new Vector3( delta.X, delta.Y, delta.Z ) * (-angle / sinHalf);
    }

    private static float[] SolveSmall( float[,] a, float[] b )
    {
        var n = b.Length;
        var m = (float[,])a.Clone();
        var x = (float[])b.Clone();
        for ( var col = 0; col < n; col++ )
        {
            var pivot = col;
            for ( var r = col + 1; r < n; r++ )
                if ( MathF.Abs( m[r, col] ) > MathF.Abs( m[pivot, col] ) )
                    pivot = r;
            if ( MathF.Abs( m[pivot, col] ) < 1e-12f )
                return new float[n];
            if ( pivot != col )
            {
                for ( var c = 0; c < n; c++ )
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            for ( var r = col + 1; r < n; r++ )
            {
                var f = m[r, col] / m[col, col];
                for ( var c = col; c < n; c++ )
                    m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for ( var row = n - 1; row >= 0; row-- )
        {
            for ( var c = row + 1; c < n; c++ )
                x[row] -= m[row, c] * x[c];
            x[row] /= m[row, row];
        }
        return x;
    }

    private static float WrapAngle( float a ) => (float)Math.IEEERemainder( a, 2.0 * Math.PI );
}
