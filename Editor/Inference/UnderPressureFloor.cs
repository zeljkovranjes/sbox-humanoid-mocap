using System;
using System.Linq;
using HumanoidMocap.Core.Cleanup;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Motion;

namespace HumanoidMocap.EditorTools.Inference;

/// <summary>Separates reconstructed floor drift from foot height before pressure inference.</summary>
internal static class UnderPressureFloor
{
	internal static float[] Estimate( MotionDocument document, float[] lowest )
	{
		var count = lowest.Length;
		// World-relative describes an axis convention, not a measured floor. Even a
		// gravity-aligned camera reconstruction can drift vertically over a long walk.
		var footNames = document.Bones.Where( bone => bone.Role is BoneRole.FootL or BoneRole.ToeL
			or BoneRole.FootR or BoneRole.ToeR ).Select( bone => bone.Name ).ToHashSet();
		var tracks = document.StationaryJoints.Where( track => footNames.Contains( track.Bone )
			&& track.Probability.Length == count ).ToArray();
		var anchors = Enumerable.Range( 0, count ).Where( frame => tracks.Any( track => track.Probability[ frame ] >= .8f ) ).ToArray();
		var floor = new double[ count ];
		if ( anchors.Length >= Math.Max( 3, count / 20 ) )
		{
			// Bridge airborne intervals, including slowed jumps, instead of treating
			// their apex as a new floor. These are the backend's pre-pressure contacts.
			var after = 0;
			for ( var frame = 0; frame < count; frame++ )
			{
				while ( after < anchors.Length - 1 && anchors[ after ] < frame ) after++;
				var b = anchors[ after ];
				var a = anchors[ Math.Max( 0, after - 1 ) ];
				var weight = b == a ? 0 : Math.Clamp( (frame - a) / (double)(b - a), 0, 1 );
				floor[ frame ] = lowest[ a ] + (lowest[ b ] - lowest[ a ]) * weight;
			}
		}
		else
		{
			var radius = Math.Max( 1, (int)Math.Round( CaptureGround.WindowSeconds * document.SourceFps / 2 ) );
			var eroded = new float[ count ];
			for ( var frame = 0; frame < count; frame++ )
			{
				var minimum = float.PositiveInfinity;
				for ( var k = Math.Max( 0, frame - radius ); k <= Math.Min( count - 1, frame + radius ); k++ )
					minimum = Math.Min( minimum, lowest[ k ] );
				eroded[ frame ] = minimum;
			}
			for ( var frame = 0; frame < count; frame++ )
			{
				var maximum = float.NegativeInfinity;
				for ( var k = Math.Max( 0, frame - radius ); k <= Math.Min( count - 1, frame + radius ); k++ )
					maximum = Math.Max( maximum, eroded[ k ] );
				floor[ frame ] = maximum;
			}
		}
		if ( count >= 8 && document.SourceFps > 2 )
		{
			var (b, a) = MocapSmooth.ButterLowpass( 2, Math.Min( .5, document.SourceFps * .2 ), document.SourceFps );
			floor = MocapSmooth.FiltFilt( b, a, floor );
		}
		return floor.Select( height => (float)height ).ToArray();
	}
}
