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
/// the ground so the performer carries on from where and facing the way the previous shot left them, and raised
/// or lowered so its floor (where its feet are lowest) is the previous shot's floor. A camera-relative shot is
/// not grounded on its own: a 0.6 s last shot of a tumbling clip joined 40 to 60 cm up and passed for a flight. Frame times stay the video's, so the joined
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
            if ( !(next.Frames[0].Time > result.Frames[^1].Time) )
            {
                result.Diagnostics.Add( FormattableString.Invariant( $"{NotePrefix}: the shot from {next.Frames[0].Time:F2} s overlaps the one before it and was left out." ) );
                continue;
            }
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
        next = Conform( next, into );
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
        var lift = Floor( into ) - Floor( next );
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
                frame.Positions[r] = MotionDocument.A( new Vector3( moved.X + endPosition.X, p.Y + lift, moved.Z + endPosition.Z ) );
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

    /// <summary>Makes <paramref name="next"/> use <paramref name="into"/>'s bones, by name. A shot's finger bones
    /// exist only where the hand model found hands, so a close-up of the legs has none: bones a shot lacks hold
    /// their rest pose, unobserved, and bones only it has are added to the joined skeleton at rest before it.</summary>
    static MotionDocument Conform( MotionDocument next, MotionDocument into )
    {
        string ParentName( MotionDocument d, MotionBone b ) => b.Parent < 0 ? null : d.Bones[b.Parent].Name;
        foreach ( var bone in next.Bones )
        {
            if ( into.Bones.Any( b => b.Name == bone.Name ) ) continue;
            var parentName = ParentName( next, bone );
            var parent = parentName is null ? -1 : into.Bones.FindIndex( b => b.Name == parentName );
            if ( parentName is not null && parent < 0 ) throw new InvalidDataException( "The shots were captured with different skeletons." );
            into.Bones.Add( new MotionBone { Name = bone.Name, Parent = parent, Role = bone.Role, Group = bone.Group, RestPosition = bone.RestPosition.ToArray(), RestRotation = bone.RestRotation.ToArray() } );
            foreach ( var frame in into.Frames )
            {
                frame.Positions = frame.Positions.Append( bone.RestPosition.ToArray() ).ToArray();
                frame.Rotations = frame.Rotations.Append( bone.RestRotation.ToArray() ).ToArray();
                frame.Evidence = frame.Evidence.Append( JointEvidence.Unobserved ).ToArray();
                if ( frame.Confidence is not null ) frame.Confidence = frame.Confidence.Append( null ).ToArray();
            }
        }
        foreach ( var bone in into.Bones )
        {
            var match = next.Bones.FirstOrDefault( b => b.Name == bone.Name );
            if ( match is not null && ParentName( next, match ) != (bone.Parent < 0 ? null : into.Bones[bone.Parent].Name) )
                throw new InvalidDataException( "The shots were captured with different skeletons." );
        }
        var map = into.Bones.Select( b => next.Bones.FindIndex( n => n.Name == b.Name ) ).ToArray();
        var result = next.Copy(); result.Bones = into.Bones.Select( b => new MotionBone { Name = b.Name, Parent = b.Parent, Role = b.Role, Group = b.Group, RestPosition = b.RestPosition.ToArray(), RestRotation = b.RestRotation.ToArray() } ).ToList();
        for ( var f = 0; f < next.Frames.Count; f++ )
        {
            var source = next.Frames[f]; var frame = result.Frames[f];
            frame.Positions = map.Select( ( m, i ) => m >= 0 ? source.Positions[m].ToArray() : into.Bones[i].RestPosition.ToArray() ).ToArray();
            frame.Rotations = map.Select( ( m, i ) => m >= 0 ? source.Rotations[m].ToArray() : into.Bones[i].RestRotation.ToArray() ).ToArray();
            frame.Evidence = map.Select( m => m >= 0 ? source.Evidence[m] : JointEvidence.Unobserved ).ToArray();
            frame.Confidence = source.Confidence is null ? null : map.Select( m => m >= 0 ? source.Confidence[m] : null ).ToArray();
        }
        return result;
    }
    /// <summary>Height of the floor under a shot: the 5th percentile of its lowest foot joint.</summary>
    static float Floor( MotionDocument doc )
    {
        var feet = new[] { BoneRole.FootL, BoneRole.FootR, BoneRole.ToeL, BoneRole.ToeR }.Select( r => doc.Bones.FindIndex( b => b.Role == r ) ).Where( i => i >= 0 ).ToArray();
        if ( feet.Length == 0 || doc.Frames.Count == 0 ) return 0;
        var lows = doc.Frames.Select( f => { var p = Positions( doc, f ); return feet.Min( i => p[i].Y ); } ).OrderBy( v => v ).ToArray();
        return lows[lows.Length / 20];
    }
    static Vector3[] Positions( MotionDocument doc, MotionFrame frame )
    {
        var count = doc.Bones.Count; var p = new Vector3[count]; var q = new Quaternion[count];
        for ( var i = 0; i < count; i++ )
        {
            var local = MotionDocument.V( frame.Positions[i] ); var rotation = MotionDocument.Q( frame.Rotations[i] ); var parent = doc.Bones[i].Parent;
            if ( parent < 0 ) { p[i] = local; q[i] = rotation; }
            else { p[i] = p[parent] + Vector3.Transform( local, q[parent] ); q[i] = Quaternion.Normalize( q[parent] * rotation ); }
        }
        return p;
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
