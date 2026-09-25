using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Takes a planted foot's slide out of the body's travel. A reconstructed body often travels while a
/// foot the contact track calls planted slides with it: on a step-dance clip planted toes still moved about
/// 1 m/s, and pinning such a foot alone would only over-stretch the leg. While a foot is planted, how far it
/// slides across the floor from one frame to the next is removed from the root's horizontal motion (averaged
/// over the planted feet); the body then travels only as far as its planted feet allow, and
/// <see cref="CaptureFootLock"/> holds what remains. In the air nothing is removed, so flights keep their
/// momentum; the correction carries on unchanged until the next contact. Height is left to the floor steps.</summary>
public static class CaptureContactRoot
{
    public const float UprightFraction = .4f;
    /// <summary>Centimetres per second above which a "planted" foot is taken as moving, not sliding.</summary>
    public const float MaximumPlantedSpeedCm = 400;
    /// <summary>How quickly the travel correction returns to the captured path: its e-folding time, seconds.</summary>
    public const float LeakSeconds = 2f;
    /// <summary>Hips staying this close to their middle position (90% of the time, centimetres) mark a performance in
    /// place: its feet glide, pivot and kick while the body stays put, so their slide is not the body drifting. The
    /// correction fades out below InPlaceFullCm and is off entirely below InPlaceCm.</summary>
    public const float InPlaceCm = 15, InPlaceFullCm = 30;
    /// <summary>Per frame, whether the capture's contact tracks put some foot on the floor; null without tracks.</summary>
    public static bool[] ContactMask( int count, SourceScene source, MappingResult mapping )
    {
        if ( source.CaptureStationaryJoints is not { Count: > 0 } tracks ) return null;
        var feet = new[] { BoneRole.FootL, BoneRole.ToeL, BoneRole.FootR, BoneRole.ToeR }
            .Select( r => mapping.RoleToBone.TryGetValue( r, out var b ) && tracks.TryGetValue( source.Skeleton[b].Name, out var p ) && p.Length == count ? p : null )
            .Where( p => p is not null ).ToArray();
        if ( feet.Length == 0 ) return null;
        return Enumerable.Range( 0, count ).Select( f => feet.Any( p => p[f] >= .5f ) ).ToArray();
    }
    static float Median( IEnumerable<float> values ) { var sorted = values.OrderBy( v => v ).ToArray(); return sorted[sorted.Length / 2]; }
    /// <returns>The largest horizontal correction, in centimetres.</returns>
    public static float Apply( List<XForm[]> frames, SourceScene source, MappingResult mapping, TargetRig target, TargetUpAxis axis )
    {
        if ( frames.Count < 3 || source.CaptureStationaryJoints is not { Count: > 0 } tracks ) return 0;
        var rig = target.Skeleton; var up = axis == TargetUpAxis.YUpCm ? Vector3.UnitY : Vector3.UnitZ;
        var toCm = axis == TargetUpAxis.ZUpEngine ? 2.54f : 1f;
        var fps = source.Clips.Count > 0 && source.Clips[0].Fps > 0 ? source.Clips[0].Fps : 30f;
        float[] Track( BoneRole role )
            => mapping.RoleToBone.TryGetValue( role, out var b ) && tracks.TryGetValue( source.Skeleton[b].Name, out var p ) && p.Length == frames.Count ? p : null;
        var feet = new List<(int Joint, float[] Heel, float[] Toe)>();
        foreach ( var (ankle, toe) in new[] { (BoneRole.FootL, BoneRole.ToeL), (BoneRole.FootR, BoneRole.ToeR) } )
        {
            var heel = Track( ankle ); var ball = Track( toe );
            if ( heel is null && ball is null ) continue;
            // The toe joint, where there is one, is the part of the foot that stays put through a step.
            if ( (target.BoneForRole( toe ) ?? target.BoneForRole( ankle )) is not int joint ) continue;
            feet.Add( (joint, heel ?? new float[frames.Count], ball ?? new float[frames.Count]) );
        }
        if ( feet.Count == 0 ) return 0;
        var world = new XForm[rig.Count]; var position = new Vector3[feet.Count][];
        for ( var i = 0; i < feet.Count; i++ ) position[i] = new Vector3[frames.Count];
        // A planted foot carries the body only when the body stands over it. In rolls, handstands and handsprings
        // the contact tracks still marked feet planted, and taking their slide out of the travel pushed a tumbler
        // 30 cm sideways off his line: the hips must be at least UprightFraction of their standing height above it.
        var hipsBone = target.BoneForRole( BoneRole.Hips ); var standing = hipsBone is int h0 ? Vector3.Dot( rig.RestWorld[h0].Pos, up ) : 0f;
        var over = new bool[feet.Count][]; for ( var i = 0; i < feet.Count; i++ ) over[i] = new bool[frames.Count];
        var hips = new Vector3[frames.Count];
        for ( var f = 0; f < frames.Count; f++ )
        {
            FkUtil.ToWorld( frames[f], rig, world );
            if ( hipsBone is int hb ) { hips[f] = world[hb].Pos; hips[f] -= up * Vector3.Dot( hips[f], up ); }
            for ( var i = 0; i < feet.Count; i++ )
            {
                position[i][f] = world[feet[i].Joint].Pos;
                over[i][f] = hipsBone is not int h || Vector3.Dot( world[h].Pos - position[i][f], up ) >= UprightFraction * standing;
            }
        }
        bool Planted( int i, int f ) => Math.Max( feet[i].Heel[f], feet[i].Toe[f] ) >= .5f && over[i][f];
        // How much this clip travels: an in-place dance (an emote's hips within 7 cm) walked 3.8 m when every glide of a
        // "planted" foot was taken out of the body's travel.
        var travel = 1f;
        if ( hipsBone is int )
        {
            var middle = new Vector3( Median( hips.Select( p => p.X ) ), Median( hips.Select( p => p.Y ) ), Median( hips.Select( p => p.Z ) ) );
            var spread = hips.Select( p => (p - middle).Length() * toCm ).OrderBy( d => d ).ElementAt( (int)(frames.Count * .9f) );
            travel = Math.Clamp( (spread - InPlaceCm) / (InPlaceFullCm - InPlaceCm), 0, 1 );
            if ( travel <= 0 ) return 0;
        }
        var correction = Vector3.Zero; var largest = 0f; var shifts = new Vector3[frames.Count];
        for ( var f = 1; f < frames.Count; f++ )
        {
            var slide = Vector3.Zero; var planted = 0;
            for ( var i = 0; i < feet.Count; i++ )
            {
                if ( !Planted( i, f ) || !Planted( i, f - 1 ) ) continue;
                var step = position[i][f] - position[i][f - 1]; step -= up * Vector3.Dot( step, up );
                // A foot moving this fast is not bearing weight, whatever the contact track says.
                if ( step.Length() * toCm > MaximumPlantedSpeedCm / fps ) continue;
                slide += step; planted++;
            }
            // The correction eases back toward the captured path (LeakSeconds), so a foot that keeps sliding under a body
            // that stays put cannot add up into travel: taking every slide out walked an in-place Fortnite emote 3.8 m
            // across the floor in 15 s. Within a step the planted foot still holds.
            correction *= MathF.Exp( -1f / (fps * LeakSeconds) );
            if ( planted > 0 ) correction -= slide / planted * travel;
            shifts[f] = correction; largest = Math.Max( largest, correction.Length() * toCm );
        }
        if ( largest < 1 ) return 0;
        for ( var f = 0; f < frames.Count; f++ )
            for ( var b = 0; b < rig.Count; b++ )
                if ( rig[b].ParentIndex < 0 ) frames[f][b].Pos += shifts[f];
        return largest;
    }
}
