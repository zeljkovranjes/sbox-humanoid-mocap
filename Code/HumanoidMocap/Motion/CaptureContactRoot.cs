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
    /// <returns>The largest horizontal correction, in centimetres.</returns>
    public static float Apply( List<XForm[]> frames, SourceScene source, MappingResult mapping, TargetRig target, TargetUpAxis axis )
    {
        if ( frames.Count < 3 || source.CaptureStationaryJoints is not { Count: > 0 } tracks ) return 0;
        var rig = target.Skeleton; var up = axis == TargetUpAxis.YUpCm ? Vector3.UnitY : Vector3.UnitZ;
        var toCm = axis == TargetUpAxis.ZUpEngine ? 2.54f : 1f;
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
        for ( var f = 0; f < frames.Count; f++ )
        {
            FkUtil.ToWorld( frames[f], rig, world );
            for ( var i = 0; i < feet.Count; i++ ) position[i][f] = world[feet[i].Joint].Pos;
        }
        bool Planted( int i, int f ) => Math.Max( feet[i].Heel[f], feet[i].Toe[f] ) >= .5f;
        var correction = Vector3.Zero; var largest = 0f; var shifts = new Vector3[frames.Count];
        for ( var f = 1; f < frames.Count; f++ )
        {
            var slide = Vector3.Zero; var planted = 0;
            for ( var i = 0; i < feet.Count; i++ )
            {
                if ( !Planted( i, f ) || !Planted( i, f - 1 ) ) continue;
                var step = position[i][f] - position[i][f - 1]; step -= up * Vector3.Dot( step, up );
                slide += step; planted++;
            }
            if ( planted > 0 ) correction -= slide / planted;
            shifts[f] = correction; largest = Math.Max( largest, correction.Length() * toCm );
        }
        if ( largest < 1 ) return 0;
        for ( var f = 0; f < frames.Count; f++ )
            for ( var b = 0; b < rig.Count; b++ )
                if ( rig[b].ParentIndex < 0 ) frames[f][b].Pos += shifts[f];
        return largest;
    }
}
