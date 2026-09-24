using System;
using System.Linq;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Levels a capture that stays relative to its camera. Such a capture keeps the camera's own view,
/// so a phone held tilted leaves the whole body tilted: a performer sitting on the floor sat 20 degrees
/// askew. The body network estimates which way gravity points in the picture, and the worker records that
/// as the camera's <see cref="CameraObservation.Up"/>; the capture is turned so that direction points up.
/// Only pitch and roll change: the direction the camera faced is kept.</summary>
public static class CaptureLevel
{
    public const string NotePrefix = "Levelled with gravity";
    /// <summary>Tilts smaller than this, in degrees, are left alone.</summary>
    public const float MinimumDegrees = 1;

    /// <returns>The tilt removed, in degrees, or 0.</returns>
    public static float Apply( MotionDocument document )
    {
        if ( document.Space != MotionSpace.CameraRelative ) return 0;
        var camera = document.Cameras.FirstOrDefault( c => c.Id == "video" );
        if ( camera?.Up is not { Length: 3 } values ) return 0;
        var up = MotionDocument.V( values );
        if ( !(up.LengthSquared() > 1e-6f) ) return 0;
        up = Vector3.Normalize( up );
        var degrees = MathF.Acos( Math.Clamp( up.Y, -1f, 1f ) ) * 180 / MathF.PI;
        if ( degrees < MinimumDegrees || degrees > 80 ) return 0;
        var axis = Vector3.Cross( up, Vector3.UnitY );
        var level = Quaternion.CreateFromAxisAngle( Vector3.Normalize( axis ), degrees * MathF.PI / 180 );
        var roots = Enumerable.Range( 0, document.Bones.Count ).Where( i => document.Bones[i].Parent < 0 ).ToArray();
        foreach ( var frame in document.Frames )
            foreach ( var r in roots )
            {
                frame.Positions[r] = MotionDocument.A( Vector3.Transform( MotionDocument.V( frame.Positions[r] ), level ) );
                frame.Rotations[r] = MotionDocument.A( Quaternion.Normalize( level * MotionDocument.Q( frame.Rotations[r] ) ) );
            }
        camera.Up = new[] { 0f, 1f, 0f };
        document.Diagnostics.Add( FormattableString.Invariant( $"{NotePrefix}: the camera was tilted {degrees:F0} degrees from level; the capture was turned upright using the body network's estimate of gravity." ) );
        return degrees;
    }
}
