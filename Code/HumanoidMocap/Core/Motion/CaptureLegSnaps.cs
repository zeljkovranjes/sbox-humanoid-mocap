using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Core.Cleanup;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Target;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Removes leg snaps: a thigh, shin or foot that turns far more in one frame than in the frames around it. They came
/// from the body network losing an ankle where the legs cross and finding it again a step away (karate: the shin turned 52
/// degrees in one frame between frames of 9-15), from the foot lock and the floor sit, and from the thigh twisting about itself
/// for a frame. Each snap is spread over its neighbouring frames by interpolating across a window widened until it settles;
/// planted feet are put back where they were by leg IK, so the leg moves smoothly and the foot does not slide. A quick move
/// stays quick but no longer happens between two frames; sustained fast motion (a kick) turns fast in its neighbouring frames
/// as well and is left alone. On the test clips: 449 snaps to none.</summary>
public static class CaptureLegSnaps
{
	/// <summary>A snap turns more than this (degrees per frame at 30 frames per second)...</summary>
	public const float MinimumDegrees = 12;
	/// <summary>...and more than this many times the median turn of the four frames on each side, plus <see cref="MarginDegrees"/>.</summary>
	public const float NeighbourRatio = 3, MarginDegrees = 4;
	const int Neighbours = 4;
	const int MaximumHalfWindow = 8;
	/// <summary>A planted foot moving slower than this (metres per second) is held exactly where it was.</summary>
	const float StillSpeed = .3f;
	const int HoldEase = 3, MaximumRounds = 4;

	/// <returns>The number of leg-frame samples smoothed.</returns>
	/// <param name="planted">Frames where the capture has each joint planted: left ankle, left toe, right ankle, right toe.</param>
	/// <param name="unitsPerMetre">The target's units in a metre.</param>
	public static int Apply( List<XForm[]> frames, TargetRig target, float fps, float unitsPerMetre, bool[][] planted = null )
	{
		// Settling one snap can leave a smaller one beside it: passes repeat until nothing changes.
		var total = 0;
		for ( var round = 0; round < MaximumRounds; round++ )
		{
			var changed = Pass( frames, target, fps, unitsPerMetre, planted );
			total += changed; if ( changed == 0 ) break;
		}
		return total;
	}

	static int Pass( List<XForm[]> frames, TargetRig target, float fps, float unitsPerMetre, bool[][] planted )
	{
		var count = frames.Count;
		if ( count < 2 * Neighbours + 2 || !(fps > 0) ) return 0;
		var rig = target.Skeleton; var scale = 30 / fps; var smoothed = 0;
		var legs = new[]
		{
			new[] { BoneRole.UpperLegL, BoneRole.LowerLegL, BoneRole.FootL, BoneRole.ToeL },
			new[] { BoneRole.UpperLegR, BoneRole.LowerLegR, BoneRole.FootR, BoneRole.ToeR },
		}.Select( roles => roles.Select( target.BoneForRole ).Where( b => b is not null ).Select( b => b!.Value ).ToArray() ).Where( c => c.Length >= 3 ).ToArray();
		var world = frames.Select( f => { var w = new XForm[rig.Count]; FkUtil.ToWorld( f, rig, w ); return w; } ).ToArray();
		for ( var l = 0; l < legs.Length; l++ )
		{
			var chain = legs[l];
			// A planted joint is held where it was when it is still: its steps to the planted frames beside it are slow.
			// A planted joint that moves keeps its smoothed path, or its jump would stay.
			bool[] Held( int which, int bone )
			{
				var marked = planted is { Length: 4 } && planted[2 * l + which]?.Length == count ? planted[2 * l + which] : new bool[count];
				bool Still( int k ) => k >= 1 && k < count && marked[k - 1] && marked[k] && Vector3.Distance( world[k][bone].Pos, world[k - 1][bone].Pos ) * fps <= StillSpeed * unitsPerMetre;
				var held = new bool[count];
				for ( var f = 0; f < count; f++ )
					held[f] = marked[f] && (Still( f ) || Still( f + 1 )) && (f < 1 || !marked[f - 1] || Still( f )) && (f + 1 >= count || !marked[f + 1] || Still( f + 1 ));
				return held;
			}
			// How firmly each joint is put back: fully where it is held, easing off over HoldEase frames, so switching between the
			// held place and the smoothed path never jumps.
			float[] Ease( bool[] held )
			{
				var weight = new float[count];
				for ( var f = 0; f < count; f++ )
					for ( var k = Math.Max( 0, f - HoldEase ); k <= Math.Min( count - 1, f + HoldEase ); k++ )
						if ( held[k] ) { var x = 1 - Math.Abs( k - f ) / (HoldEase + 1f); weight[f] = Math.Max( weight[f], x * x * (3 - 2 * x) ); }
				return weight;
			}
			var ankleHeld = Ease( Held( 0, chain[2] ) ); var toeHeld = Ease( chain.Length > 3 ? Held( 1, chain[3] ) : new bool[count] );
			// Each bone's whole turn, and the direction it points (to the next joint down): a swing hidden in a twist counts.
			var measures = new List<Func<XForm[], XForm[], float>>();

			for ( var j = 0; j < chain.Length; j++ )
			{
				var bone = chain[j]; measures.Add( ( a, b ) => Angle( a[bone].Rot, b[bone].Rot ) );
				if ( j + 1 < chain.Length ) { var child = chain[j + 1]; measures.Add( ( a, b ) => Direction( a[child].Pos - a[bone].Pos, b[child].Pos - b[bone].Pos ) ); }
			}
			float Turn( Func<XForm[], XForm[], float> measure, int f ) => measure( world[f - 1], world[f] ) * scale;
			bool IsSnap( int f )
			{
				if ( f < 1 || f >= count ) return false;
				foreach ( var measure in measures )
				{
					var turn = Turn( measure, f ); if ( turn <= MinimumDegrees ) continue;
					var around = new List<float>();
					for ( var k = Math.Max( 1, f - Neighbours ); k <= Math.Min( count - 1, f + Neighbours ); k++ ) if ( k != f ) around.Add( Turn( measure, k ) );
					around.Sort(); var median = around.Count == 0 ? 0 : around.Count % 2 == 1 ? around[around.Count / 2] : (around[around.Count / 2 - 1] + around[around.Count / 2]) / 2;
					if ( turn > NeighbourRatio * median + MarginDegrees ) return true;
				}
				return false;
			}
			// One snap at a time: the frames around it are interpolated (world rotations, down the leg) between the two
			// frames just outside, over a window widened until no snap is left in or next to it. A widening window always
			// settles, where averaging neighbours only pushed part of the jump to the next frame.
			var skip = new HashSet<int>();
			for ( var guard = 0; guard < count; guard++ )
			{
				var t = Enumerable.Range( 1, count - 1 ).FirstOrDefault( f => !skip.Contains( f ) && IsSnap( f ) );
				if ( t == 0 ) break;
				var saved = new Dictionary<int, XForm[]>(); var fixedIt = false;
				// First keeping the planted ankle and toe in place; if no window settles that way, only the ankle (the heel rolls
				// over a toe that slid), then only the toe (the heel lifts), and last neither. Letting both go at once slid feet
				// that only needed to roll.
				for ( var attempt = 0; attempt < 4 * (MaximumHalfWindow - 1) && !fixedIt; attempt++ )
				{
					var hold = attempt / (MaximumHalfWindow - 1); var half = 2 + attempt % (MaximumHalfWindow - 1);
					int a = Math.Max( 0, t - half ), b = Math.Min( count - 1, t + half - 1 );
					for ( var f = a + 1; f < b; f++ )
					{
						if ( !saved.ContainsKey( f ) ) saved[f] = frames[f].ToArray();
						else frames[f] = saved[f].ToArray();
					}
					float AnkleWeight( int f ) => hold is 0 or 1 ? ankleHeld[f] : 0;
					float ToeWeight( int f ) => hold is 0 or 2 ? toeHeld[f] : 0;
					var befores = new Dictionary<int, XForm[]>();
					for ( var f = a + 1; f < b; f++ ) { befores[f] = new XForm[rig.Count]; FkUtil.ToWorld( saved[f], rig, befores[f] ); }
					// The foot turns between the frames where it is fully held too, not only between the window's ends: slerping
					// past them turned it away and the hold then turned it back in one frame.
					bool Key( int f ) => f <= a || f >= b || Math.Min( AnkleWeight( f ), ToeWeight( f ) ) >= 1;
					Quaternion KeyRotation( int f, int bone ) => f <= a ? world[a][bone].Rot : f >= b ? world[b][bone].Rot : befores[f][bone].Rot;
					for ( var f = a + 1; f < b; f++ )
					{
						var before = befores[f]; Array.Copy( before, world[f], rig.Count );
						int previous = f - 1, next = f + 1;
						while ( !Key( previous ) ) previous--;
						while ( !Key( next ) ) next++;
						foreach ( var bone in chain )
						{
							var foot = bone == chain[2] || chain.Length > 3 && bone == chain[3];
							var goal = !foot ? Quaternion.Slerp( world[a][bone].Rot, world[b][bone].Rot, (f - a) / (float)(b - a) )
								: Key( f ) ? before[bone].Rot
								: Quaternion.Slerp( KeyRotation( previous, bone ), KeyRotation( next, bone ), (f - previous) / (float)(next - previous) );
							var parent = rig[bone].ParentIndex;
							var parentRot = parent < 0 ? Quaternion.Identity : world[f][parent].Rot;
							frames[f][bone] = new XForm( frames[f][bone].Pos, MathQ.Normalize( Quaternion.Inverse( parentRot ) * goal ) );
							FkUtil.ToWorld( frames[f], rig, world[f] );
						}
						// A planted foot stays exactly where it was: the smoothed leg is brought back onto it.
						var ankleWeight = AnkleWeight( f ); var toeWeight = ToeWeight( f );
						if ( ankleWeight > 0 || toeWeight > 0 ) Replant( frames[f], rig, chain, before, world[f], ankleWeight, toeWeight );
					}
					fixedIt = Enumerable.Range( a, b - a + 2 ).All( f => !IsSnap( f ) );
					if ( fixedIt ) smoothed += b - a - 1;
				}
				// Left as it was when no window settles it (a turn at the clip's very edge).
				if ( !fixedIt )
				{
					foreach ( var (f, original) in saved ) { frames[f] = original; FkUtil.ToWorld( frames[f], rig, world[f] ); }
					skip.Add( t );
				}
			}
		}
		return smoothed;
	}

	/// <summary>Puts the planted joints back where they were: the ankle by two-bone IK (bending in the smoothed knee's plane),
	/// the toe by turning the foot about the ankle. A joint that is not planted keeps its smoothed motion, so a foot that
	/// snapped flat in one frame on its heel now rolls over a few.</summary>
	static void Replant( XForm[] frame, HumanoidMocap.Core.Skeleton.Skeleton rig, int[] chain, XForm[] before, XForm[] world, float ankleHeld, float toeHeld )
	{
		int hip = chain[0], knee = chain[1], ankle = chain[2]; int? toe = chain.Length > 3 ? chain[3] : null;
		var smoothedFoot = world[ankle].Rot;
		var footVector = toe is int t0 ? world[t0].Pos - world[ankle].Pos : Vector3.Zero;
		// The ankle goes to its held place, or where the smoothed foot puts it behind a held toe, as firmly as each is held.
		var behindToe = toe is int t1 ? before[t1].Pos - footVector : world[ankle].Pos;
		var goal = Vector3.Lerp( Vector3.Lerp( world[ankle].Pos, behindToe, toeHeld ), before[ankle].Pos, ankleHeld );
		var bend = Vector3.Cross( world[knee].Pos - world[hip].Pos, world[ankle].Pos - world[knee].Pos );
		var ik = TwoBoneIk.Solve( world[hip].Pos, world[knee].Pos, world[ankle].Pos, goal, soften: 0, stableBendAxis: bend.LengthSquared() > 1e-10f ? bend : null );
		EffectorIk.ApplyWorldDeltas( frame, rig, hip, knee, ankle, ik.UpperWorldDelta, ik.LowerWorldDelta, world );
		FkUtil.ToWorld( frame, rig, world );
		var footRotation = smoothedFoot;
		if ( toe is int t2 && footVector.LengthSquared() > 1e-10f && Math.Min( ankleHeld, toeHeld ) > 0 )
			footRotation = MathQ.Normalize( Quaternion.Slerp( Quaternion.Identity, MathQ.FromTo( footVector, before[t2].Pos - world[ankle].Pos ), Math.Min( ankleHeld, toeHeld ) ) * smoothedFoot );
		var parent = rig[ankle].ParentIndex;
		frame[ankle] = new XForm( frame[ankle].Pos, MathQ.Normalize( Quaternion.Inverse( parent < 0 ? Quaternion.Identity : world[parent].Rot ) * footRotation ) );
		FkUtil.ToWorld( frame, rig, world );
	}

	static float Direction( Vector3 a, Vector3 b )
		=> a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f ? 0
			: MathF.Acos( Math.Clamp( Vector3.Dot( Vector3.Normalize( a ), Vector3.Normalize( b ) ), -1f, 1f ) ) * 180 / MathF.PI;
	static float Angle( Quaternion a, Quaternion b )
		=> 2 * MathF.Acos( Math.Clamp( MathF.Abs( Quaternion.Dot( MathQ.Normalize( a ), MathQ.Normalize( b ) ) ), 0f, 1f ) ) * 180 / MathF.PI;
}
