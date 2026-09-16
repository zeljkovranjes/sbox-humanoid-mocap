#nullable enable annotations

using System;
using System.IO;
using Sandbox;

namespace HumanoidMocap.Editor;

/// <summary>
/// Editor-side access to the committed deep-learning model asset (Milestone 10): the SAME
/// weight blob shipped at <c>Assets/humanoid_mocap/dl/same_v1.weights</c> (CC BY-NC
/// 4.0 — see the ATTRIBUTION.md beside it). The <c>Code/</c> facade does no file IO, so
/// the editor reads the bytes here and passes them via
/// <see cref="RetargetTargetSpec.DlWeights"/>; the "Deep learning (experimental)" option
/// in the no-profile dialog is enabled exactly when this asset resolves.
/// </summary>
public static class DlAssets
{
	/// <summary>Assets-relative path of the committed weight blob.</summary>
	public const string WeightsRelative = "humanoid_mocap/dl/same_v1.weights";

	/// <summary>Whether the weight asset is reachable from the current project.</summary>
	public static bool Available => EditorPipeline.FindLibraryAssetFile( WeightsRelative ) is not null;

	/// <summary>
	/// Loads the weight blob bytes, or null when the asset is missing/unreadable (the DL
	/// option then stays disabled; everything else works without it).
	/// </summary>
	public static byte[] TryLoadWeights()
	{
		try
		{
			var path = EditorPipeline.FindLibraryAssetFile( WeightsRelative );
			return path is null ? null : File.ReadAllBytes( path );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] could not read DL weights: {e.Message}" );
			return null;
		}
	}
}
