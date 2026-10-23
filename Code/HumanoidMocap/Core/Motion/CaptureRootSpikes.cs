using System;
using System.Linq;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Rejects brief large translation excursions before filtering a reconstructed body.</summary>
internal static class CaptureRootSpikes
{
	const double HalfWindowSeconds = .08;
	const float MinimumExcursionMetres = .15f;

	internal static Vector3[] Clean( Vector3[] positions, double fps )
	{
		var result = positions.ToArray();
		var radius = Math.Max( 1, (int)Math.Round( HalfWindowSeconds * fps ) );
		var values = new float[ radius * 2 + 1 ];
		for ( var frame = 0; frame < positions.Length; frame++ )
		{
			float Median( int axis )
			{
				for ( var offset = -radius; offset <= radius; offset++ )
				{
					var position = positions[ Math.Clamp( frame + offset, 0, positions.Length - 1 ) ];
					values[ offset + radius ] = axis == 0 ? position.X : axis == 1 ? position.Y : position.Z;
				}
				Array.Sort( values );
				return values[ radius ];
			}
			var median = new Vector3( Median( 0 ), Median( 1 ), Median( 2 ) );
			// A monotonic trajectory, however fast, is its own temporal median. A
			// short depth excursion that returns immediately is not sustained travel.
			// Leave ordinary gait oscillation and small jump-apex curvature untouched.
			if ( Vector3.Distance( positions[ frame ], median ) > MinimumExcursionMetres )
				result[ frame ] = median;
		}
		return result;
	}
}
