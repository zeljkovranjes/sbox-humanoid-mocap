#nullable enable annotations

using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

/// <summary>
/// Asset-browser integration: right-clicking .fbx/.bvh/.glb/.gltf/.vrm/.anm/.an5/.cba files
/// offers "Retarget to s&amp;box rig…", which opens the <see cref="RetargetWindow"/>
/// pre-loaded with the selected files.
/// </summary>
public static class RetargeterAssetContextMenu
{
	static readonly HashSet<string> SourceExtensions = new( System.StringComparer.OrdinalIgnoreCase )
	{
		"fbx",
		"hmotion",
		"bvh",
		"glb",
		"gltf",
		"vrm",
		"anm", // RenderWare single clip (skeleton from a companion .dff)
		"an5", // RenderWare/FSB2 animation bank (multi-take)
		"cba", // EA ANT animation package (companion joint-table JSON)
	};

	[Event( "asset.contextmenu", Priority = 60 )]
	public static void OnAssetContextMenu( AssetContextMenu e )
	{
		var files = e.SelectedList
			.Where( x => SourceExtensions.Contains( System.IO.Path.GetExtension( x.AbsolutePath ).TrimStart( '.' ) ) )
			.Select( x => x.AbsolutePath )
			.ToList();

		if ( files.Count == 0 )
			return;

		var label = files.Count == 1 ? "Humanoid Mocap · Edit Motion" : $"Humanoid Mocap · Edit {files.Count} Animations";
		e.Menu.AddOption( label, "sync_alt", () => RetargetWindow.Open().AddFiles( files ) );
	}
}
