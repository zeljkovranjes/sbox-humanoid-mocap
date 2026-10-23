using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core.Formats.Fbx;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

/// <summary>FBX with the source-to-engine scale baked into every translation for direct ModelDoc import.</summary>
public static class ModelDocFbxExport
{
	/// <summary>Uses Humanoid Retargeter's source scale for centimetre rigs; compiled engine rigs already use inches.</summary>
	public static byte[] Write( SkeletonModel skeleton, string name, IReadOnlyList<XForm[]> frames,
		IReadOnlyList<double> times, double fps, TargetUpAxis axis, string motionSpace )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		ArgumentNullException.ThrowIfNull( frames );
		if ( frames.Count is < 1 or > 108000 || skeleton.Count is < 1 or > 1024
			|| (long)frames.Count * skeleton.Count > FbxAnimationWriter.MaximumTransformSamples )
			throw new ArgumentException( "Animation exceeds the export budget. Select a shorter range." );
		if ( axis is not (TargetUpAxis.YUpCm or TargetUpAxis.ZUpEngine) )
			throw new ArgumentOutOfRangeException( nameof( axis ) );

		// Like Retargeter's ModelModifier_ScaleAndMirror, this converts raw source values.
		// UnitScaleFactor alone is insufficient for ModelDoc's raw-unit import. Bake the
		// factor into bind positions, all local translations, root motion and appended props.
		if ( axis == TargetUpAxis.YUpCm )
		{
			XForm Scale( XForm pose ) => new( pose.Pos * RetargetTargetSpec.SboxSourceScale, pose.Rot );
			skeleton = SkeletonModel.Create( skeleton.Bones.Select( bone => new BoneDefinition(
				bone.Name, bone.ParentIndex < 0 ? null : skeleton[ bone.ParentIndex ].Name,
				Scale( bone.RestLocal ) ) ).ToArray() );
			frames = frames.Select( frame => frame.Select( Scale ).ToArray() ).ToArray();
		}

		return FbxAnimationWriter.Write( skeleton, name, frames, times, fps,
			axis == TargetUpAxis.YUpCm ? 1 : 2, 2.54, motionSpace );
	}
}
