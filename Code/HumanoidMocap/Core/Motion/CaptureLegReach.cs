using System;

namespace HumanoidMocap.Core.Motion;

using Vector3 = System.Numerics.Vector3;

/// <summary>Leaves a small knee bend when moving the pelvis to reach captured foot targets.</summary>
internal static class CaptureLegReach
{
	// Exact full extension is singular: a millimetre of ankle travel can unfold the knee
	// by centimetres. Reserve half a percent of reach and enter the correction smoothly.
	internal const float ReachFraction = .995f;

	internal static float Lowering( Vector3 hipToFoot, Vector3 up, float length )
	{
		var vertical = Vector3.Dot( hipToFoot, up );
		var horizontal = hipToFoot - up * vertical;
		var reach = length * ReachFraction;
		if ( length <= 0 || vertical <= 0 || horizontal.LengthSquared() >= reach * reach ) return 0;

		var deficit = vertical - MathF.Sqrt( reach * reach - horizontal.LengthSquared() );
		var ease = length * .01f;
		// Smooth positive part, evaluated without subtracting two nearly equal numbers.
		var diagonal = MathF.Sqrt( deficit * deficit + ease * ease );
		return deficit >= 0 ? (deficit + diagonal) * .5f : ease * ease / (2 * (diagonal - deficit));
	}
}
