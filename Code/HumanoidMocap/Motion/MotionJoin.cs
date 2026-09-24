using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Joins captures of consecutive shots of one video into a single motion. Each shot has its own
/// camera, so its placement means nothing next to the last shot's: every later shot is moved and turned on
/// the ground so the performer carries on from where and facing the way the previous shot left them. Height
/// is each shot's own, since every shot is grounded separately. Frame times stay the video's, so the joined
/// motion still lines up with the video; planted-joint tracks, contacts and camera frames are joined too.
/// Shots captured in a different space from the first (world-relative against camera-relative) cannot be
/// placed next to it and are left out, with a note.</summary>
public static class MotionJoin
{
    public const string NotePrefix = "Shots joined";

    public static MotionDocument Join( IReadOnlyList<MotionDocument> shots )
    {
        if ( shots.Count == 0 ) throw new ArgumentException( "No shots to join." );
        var result = shots[0].Copy();
        if ( shots.Count == 1 ) return result;
        var joined = new List<double> { result.Frames[0].Time };
        foreach ( var next in shots.Skip( 1 ) )
        {
            if ( next.Frames.Count == 0 ) continue;
            if ( next.Space != result.Space )
            {
                result.Diagnostics.Add( FormattableString.Invariant( $"{NotePrefix}: the shot from {next.Frames[0].Time:F2} s was captured {Describe( next.Space )} and the first {Describe( result.Space )}, so it was left out; capture it on its own under Advanced." ) );
                continue;
            }
            Append( result, next );
            joined.Add( next.Frames[0].Time );
        }
        if ( joined.Count > 1 )
            result.Diagnostics.Add( $"{NotePrefix}: {joined.Count} shots, starting at " + string.Join( ", ", joined.Select( t => t.ToString( "F2", System.Globalization.CultureInfo.InvariantCulture ) + " s" ) )
                + ". Each later shot continues on the ground from where and facing the way the previous one ended; the cut itself is a jump in pose." );
        result.Validate();
        return result;
    }

    static string Describe( MotionSpace space ) => space == MotionSpace.WorldRelative ? "relative to the world" : "relative to its camera";

    static void Append( MotionDocument into, MotionDocument next )
    {
        if ( next.Bones.Count != into.Bones.Count || next.Bones.Where( ( b, i ) => b.Name != into.Bones[i].Name || b.Parent != into.Bones[i].Parent ).Any() )
            throw new InvalidDataException( "The shots were captured with different skeletons." );
        if ( !(next.Frames[0].Time > into.Frames[^1].Time) ) throw new InvalidDataException( "Shots must follow each other in time." );
        var before = into.Frames.Count;
        var (endPosition, endLateral) = Placement( into, into.Frames[^1] );
        var (startPosition, startLateral) = Placement( next, next.Frames[0] );
        // Turn about the vertical so the hips face the same way across the cut.
        var turn = Quaternion.Identity;
        if ( endLateral.LengthSquared() > 1e-6f && startLateral.LengthSquared() > 1e-6f )
        {
            var a = Vector3.Normalize( startLateral ); var b = Vector3.Normalize( endLateral );
            var angle = MathF.Atan2( Vector3.Dot( Vector3.Cross( a, b ), Vector3.UnitY ), Vector3.Dot( a, b ) );
            turn = Quaternion.CreateFromAxisAngle( Vector3.UnitY, angle );
        }
        var roots = Enumerable.Range( 0, next.Bones.Count ).Where( i => next.Bones[i].Parent < 0 ).ToArray();
        foreach ( var source in next.Frames )
        {
            var frame = new MotionFrame
            {
                Time = source.Time,
                Positions = source.Positions.Select( p => (float[])p.ToArray() ).ToArray(),
                Rotations = source.Rotations.Select( r => (float[])r.ToArray() ).ToArray(),
                Evidence = source.Evidence.ToArray(),
                Confidence = source.Confidence?.ToArray(),
            };
            foreach ( var r in roots )
            {
                var p = MotionDocument.V( frame.Positions[r] );
                var moved = Vector3.Transform( p - startPosition, turn );
                frame.Positions[r] = MotionDocument.A( new Vector3( moved.X + endPosition.X, p.Y, moved.Z + endPosition.Z ) );
                frame.Rotations[r] = MotionDocument.A( Quaternion.Normalize( turn * MotionDocument.Q( frame.Rotations[r] ) ) );
            }
            into.Frames.Add( frame );
        }
        // Per-frame tracks: pad what one shot lacks with zero.
        var after = next.Frames.Count;
        foreach ( var track in into.StationaryJoints )
        {
            var match = next.StationaryJoints.FirstOrDefault( t => t.Bone == track.Bone && t.Source == track.Source );
            track.Probability = track.Probability.Concat( match?.Probability ?? new float[after] ).ToArray();
        }
        foreach ( var track in next.StationaryJoints.Where( t => !into.StationaryJoints.Any( o => o.Bone == t.Bone && o.Source == t.Source ) ) )
            into.StationaryJoints.Add( new StationaryJointTrack { Bone = track.Bone, Source = track.Source, Probability = new float[before].Concat( track.Probability ).ToArray() } );
        into.Contacts.AddRange( next.Contacts );
        foreach ( var camera in into.Cameras )
            if ( next.Cameras.FirstOrDefault( c => c.Id == camera.Id ) is { } other ) camera.Frames.AddRange( other.Frames );
        into.Corrections.AddRange( next.Corrections );
        into.Diagnostics.AddRange( next.Diagnostics.Select( d => FormattableString.Invariant( $"Shot from {next.Frames[0].Time:F2} s: {d}" ) ) );
    }

    /// <summary>The root's position and the hips' left-to-right direction, flattened onto the ground.</summary>
    static (Vector3 Position, Vector3 Lateral) Placement( MotionDocument doc, MotionFrame frame )
    {
        var count = doc.Bones.Count; var p = new Vector3[count]; var q = new Quaternion[count];
        for ( var i = 0; i < count; i++ )
        {
            var local = MotionDocument.V( frame.Positions[i] ); var rotation = MotionDocument.Q( frame.Rotations[i] ); var parent = doc.Bones[i].Parent;
            if ( parent < 0 ) { p[i] = local; q[i] = rotation; }
            else { p[i] = p[parent] + Vector3.Transform( local, q[parent] ); q[i] = Quaternion.Normalize( q[parent] * rotation ); }
        }
        var root = Enumerable.Range( 0, count ).First( i => doc.Bones[i].Parent < 0 );
        int Role( BoneRole role ) => doc.Bones.FindIndex( b => b.Role == role );
        var left = Role( BoneRole.UpperLegL ); var right = Role( BoneRole.UpperLegR );
        var lateral = left >= 0 && right >= 0 ? p[left] - p[right] : Vector3.Zero;
        lateral.Y = 0;
        return (p[root], lateral);
    }
}
