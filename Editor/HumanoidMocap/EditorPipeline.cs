#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

/// <summary>
/// Editor-side glue around the engine-agnostic <see cref="Retargeter"/> facade: locates the
/// committed s&amp;box target-rig JSON, writes the facade's output strings to disk under the
/// project's Assets folder, registers them with the asset system and compiles, surfacing
/// compiler state. All file IO of the pipeline lives here - the facade itself is pure.
/// </summary>
/// <remarks>
/// Threading: <see cref="AssetSystem"/> / <see cref="Asset"/> calls are engine state and are
/// only safe on the editor main thread. Our async flows hop threads (<c>Task.Run</c> for the
/// pure conversion math, <c>Task.Delay</c> in poll loops), so every engine call below is
/// preceded by <see cref="SwitchToMainThread"/> - the awaitable queues its continuation via
/// the sanctioned <see cref="MainThread.Queue"/> dispatcher (the same pattern the official
/// tools use, e.g. AssetBrowser). Touching engine objects from a pool thread is a native
/// crash, not a managed exception - it must be prevented structurally.
/// </remarks>
public static class EditorPipeline
{
	// ============================================================== threading

	/// <summary>
	/// Awaitable hop to the editor main thread. Completes synchronously when already
	/// there; otherwise the continuation is queued via <see cref="MainThread.Queue"/>.
	/// Await this before touching engine objects (AssetSystem, Asset, widgets) in any
	/// flow that has been on a background task or resumed from <c>Task.Delay</c>.
	/// </summary>
	public static MainThreadAwaitable SwitchToMainThread() => default;

	/// <summary>Awaiter behind <see cref="SwitchToMainThread"/>.</summary>
	public readonly struct MainThreadAwaitable : INotifyCompletion
	{
		public MainThreadAwaitable GetAwaiter() => this;
		public bool IsCompleted => ThreadSafe.IsMainThread;
		public void OnCompleted( Action continuation ) => MainThread.Queue( continuation );
		public void GetResult() { }
	}

	// ====================================================== install-path guard

	/// <summary>Root folder of the s&amp;box installation (directory of the running
	/// sbox-dev.exe), or null when it cannot be determined.</summary>
	internal static string EngineRootPath
	{
		get
		{
			try
			{
				var exeDir = Path.GetDirectoryName( Environment.ProcessPath );
				return exeDir is null ? null : Path.GetFullPath( exeDir );
			}
			catch
			{
				return null;
			}
		}
	}

	/// <summary>
	/// True when <paramref name="path"/> resolves to somewhere inside the s&amp;box
	/// installation (the running editor's exe directory, with a literal
	/// <c>steamapps\common\sbox</c> fallback). Writing/compiling assets there has the
	/// engine's own content watcher fighting our writes and has crashed the editor
	/// natively - conversion outputs and augment targets must live in a user project.
	/// </summary>
	/// <remarks>
	/// EXEMPTION (deliberate, see also <see cref="WriteAndCompileAsync"/>): paths under the
	/// CURRENT project are never reported as engine-install paths, even when the project
	/// physically lives inside the install tree (the shipped sample projects under
	/// <c>&lt;sbox&gt;/samples/...</c> - users do convert there, e.g. samples/sweeper). The
	/// guard exists to protect ENGINE content (core/, addons/, citizen, ...), not the
	/// project the user deliberately opened. This re-opens engine-watcher exposure for
	/// install-resident projects - the original native-crash class - so the write/compile
	/// path does NOT rely on this guard alone: when the project root itself is under the
	/// install (<see cref="IsUnderEngineInstallIgnoringProject"/>), WriteAndCompileAsync
	/// switches to a defensive profile (longer input settle + an extra verified-compile
	/// retry). The crash itself was fixed by main-thread sequencing; this is defense in
	/// depth, not user blocking.
	/// </remarks>
	public static bool IsUnderEngineInstall( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;

		try
		{
			var full = Path.GetFullPath( path );

			// The CURRENT PROJECT is always writable (see remarks above).
			var project = Sandbox.Project.Current?.GetRootPath();
			if ( !string.IsNullOrWhiteSpace( project ) )
			{
				var projPrefix = Path.GetFullPath( project ).TrimEnd( Path.DirectorySeparatorChar )
					+ Path.DirectorySeparatorChar;
				if ( full.StartsWith( projPrefix, StringComparison.OrdinalIgnoreCase ) )
					return false;
			}

			return IsUnderEngineInstallIgnoringProject( full );
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// The raw install-tree test behind <see cref="IsUnderEngineInstall"/>, WITHOUT the
	/// current-project exemption. Used by <see cref="WriteAndCompileAsync"/> to detect
	/// install-resident projects (samples/...) and harden the compile sequencing there -
	/// such paths are allowed (the user opened that project) but the engine's content
	/// watcher still watches them.
	/// </summary>
	internal static bool IsUnderEngineInstallIgnoringProject( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;

		try
		{
			var full = Path.GetFullPath( path );

			var root = EngineRootPath;
			if ( root is not null )
			{
				var prefix = root.TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar;
				if ( full.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
					return true;
			}

			// Fallback: a Steam-installed s&box that is not the running process (or the
			// process path was unavailable).
			return full.Replace( '/', '\\' ).IndexOf(
				@"steamapps\common\sbox\", StringComparison.OrdinalIgnoreCase ) >= 0;
		}
		catch
		{
			return false;
		}
	}
	/// <summary>Assets-relative path of the committed s&amp;box target rig definition.</summary>
	public const string TargetRigJsonRelative = "humanoid_mocap/target_rig_sbox.json";

	/// <summary>Assets-relative path of the committed classic (4-finger) citizen target rig definition.</summary>
	public const string CitizenTargetRigJsonRelative = "humanoid_mocap/target_rig_sbox_citizen.json";

	/// <summary>
	/// Finds a file shipped in this library's Assets folder. Works both when the library
	/// is the open project and when it is installed under <c>Libraries/</c> of a game
	/// project (the editor mounts libraries from there). Returns null when not found.
	/// </summary>
	public static string FindLibraryAssetFile( string assetsRelativePath )
	{
		var relative = assetsRelativePath.Replace( '/', Path.DirectorySeparatorChar );
		var root = Project.Current?.GetRootPath();
		if ( root is null )
			return null;

		var candidates = new List<string>
		{
			Path.Combine( root, "Assets", relative ),
		};

		var librariesDir = Path.Combine( root, "Libraries" );
		if ( Directory.Exists( librariesDir ) )
		{
			foreach ( var lib in Directory.EnumerateDirectories( librariesDir ) )
				candidates.Add( Path.Combine( lib, "Assets", relative ) );
		}

		return candidates.FirstOrDefault( File.Exists );
	}

	/// <summary>
	/// Loads the shipped s&amp;box default target (rig JSON → <see cref="RetargetTargetSpec.SboxDefault"/>).
	/// Throws <see cref="FileNotFoundException"/> when the rig JSON is not reachable from
	/// the current project.
	/// </summary>
	public static RetargetTargetSpec LoadSboxDefaultTarget()
	{
		var path = FindLibraryAssetFile( TargetRigJsonRelative )
			?? throw new FileNotFoundException(
				$"Target rig definition not found ({TargetRigJsonRelative}). Is the humanoid_mocap library installed?" );
		// DL weights ride along when the committed model asset exists (enables the
		// deep-learning fallback solver; null keeps everything else working without it).
		return RetargetTargetSpec.SboxDefault( File.ReadAllText( path ), DlAssets.TryLoadWeights() );
	}

	/// <summary>
	/// Loads the classic (4-finger) s&amp;box citizen target
	/// (rig JSON → <see cref="RetargetTargetSpec.SboxCitizen"/>). Throws
	/// <see cref="FileNotFoundException"/> when the rig JSON is not reachable from
	/// the current project.
	/// </summary>
	public static RetargetTargetSpec LoadSboxCitizenTarget()
	{
		var path = FindLibraryAssetFile( CitizenTargetRigJsonRelative )
			?? throw new FileNotFoundException(
				$"Citizen target rig definition not found ({CitizenTargetRigJsonRelative}). Is the humanoid_mocap library installed?" );
		return RetargetTargetSpec.SboxCitizen( File.ReadAllText( path ), DlAssets.TryLoadWeights() );
	}

	/// <summary>
	/// Compatibility entry point for source-model target preparation. Copies the model into
	/// <c>Assets/&lt;outputFolderRelative&gt;/</c> (skipped when the
	/// source and destination are the same file) and points
	/// <see cref="RetargetTargetSpec.MeshFilePath"/>/<see cref="RetargetTargetSpec.MeshImportScale"/>
	/// at it, so the generated standalone vmdl embeds the mesh as a RenderMeshFile node.
	/// Without this the vmdl has no base model and no mesh — it compiles into an empty
	/// model (0 bones, 0 sequences) and playing it does nothing. Returns false with
	/// <paramref name="error"/> set when the copy fails; no-op for compiled-model targets.
	/// <paramref name="report"/> (optional) collects user-facing notes about what the
	/// preparation did (e.g. a mid-pose export repaired at embed time).
	/// </summary>
	public static bool PrepareFbxTargetMesh(
		TargetPickers.ResolvedTarget target, string outputFolderRelative, out string error,
		List<string> report = null )
		=> PrepareModelTargetMesh( target, outputFolderRelative, out error, report );

	/// <summary>Prepares an FBX, GLB, or glTF custom target mesh for ModelDoc.</summary>
	public static bool PrepareModelTargetMesh(
		TargetPickers.ResolvedTarget target, string outputFolderRelative, out string error,
		List<string> report = null )
	{
		error = null;
		if ( target?.ModelFilePath is null )
			return true;

		var assetsPath = Project.Current?.GetAssetsPath();
		if ( assetsPath is null )
		{
			error = "No project is open.";
			return false;
		}

		try
		{
			// Sanitized destination name: spaces and exotic characters in source-file names
			// travel into the vmdl's RenderMeshFile node and the compiled asset path -
			// underscores keep both on the safe side of ModelDoc's node naming.
			var stem = Path.GetFileNameWithoutExtension( target.ModelFilePath );
			var safeStem = new string( stem.Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
			var extension = Path.GetExtension( target.ModelFilePath ).ToLowerInvariant();
			var fileName = safeStem + extension;
			var relative = string.IsNullOrEmpty( outputFolderRelative )
				? fileName
				: outputFolderRelative.TrimEnd( '/' ) + "/" + fileName;
			var destination = Path.GetFullPath( Path.Combine(
				assetsPath, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );
			var modelBytes = File.ReadAllBytes( target.ModelFilePath );

			if ( !string.Equals( Path.GetFullPath( target.ModelFilePath ), destination,
				StringComparison.OrdinalIgnoreCase ) )
			{
				Directory.CreateDirectory( Path.GetDirectoryName( destination ) );

				// Repair mid-pose exports on the way in: files whose node transforms hold
				// an animation snapshot instead of the bind (common on Auto-Rig Pro / store
				// assets saved with IK'd hands or feet posed) compile into a skeleton whose
				// "rest" is that snapshot - self-consistent skin, so it LOOKS fine, but the
				// solver's anatomy (leg chains down, mirrored hands) is silently wrong and
				// the retarget mangles exactly the posed limbs. The BindPose section holds
				// the truth; rewrite the node transforms to it.
				if ( extension == ".fbx" )
				{
					var repaired = HumanoidMocap.Formats.Fbx.FbxBindPoseFixer.TryFix(
						modelBytes, out var bindReport );
					File.WriteAllBytes( destination, repaired ?? modelBytes );
					Log.Info( $"[sbox-humanoid-mocap] target FBX bind check: {bindReport}"
						+ (repaired is not null ? " - repaired copy embedded" : "") );
					if ( repaired is not null )
						report?.Add( $"Target FBX was exported mid-pose ({bindReport}) - "
							+ "the embedded copy was repaired from the file's own bind data." );
				}
				else
				{
					File.WriteAllBytes( destination, modelBytes );
				}
			}

			// Sidecar textures FIRST: model exports commonly ship a "textures" folder next to
			// the file (or next to its parent "source" folder) plus loose images - copy them
			// along so the compiled model has a chance to resolve its skins (user report:
			// "it has a source with the .fbx and a folder named textures"). Best-effort:
			// references the model makes to files that do not exist anywhere (e.g. .tga names
			// shipped as .png conversions) cannot be resolved by any importer.
			CopySidecarTextures( Path.GetDirectoryName( target.ModelFilePath ),
				Path.GetDirectoryName( destination ), extension == ".fbx"
					? ExtractFbxMaterials( modelBytes ).SelectMany( m => m.TextureReferences ) : null );
			if ( extension == ".gltf" )
				CopyGltfDependencies( target.ModelFilePath, destination );

			// Auto-generate a vmat per source material that has none: the compiler otherwise
			// reports 'Missing vmat "<name>.vmat"' per mesh and the model renders with
			// placeholder materials. Authored material links are preferred, with the existing
			// name-token matching retained for incomplete exports. This must
			// happen BEFORE the FBX is registered - registration can kick off the mesh
			// compile immediately, baking placeholder materials in. The returned remaps
			// go into every vmdl generated for this target: source materials can be BARE names
			// the compiler cannot resolve as resource paths ("Trying to load an illegal
			// resource name X.vmat"), so the vmdl must remap them to the real files - the
			// same MaterialGroupList mechanism the shipped citizen vmdl uses.
			target.Spec.MaterialRemaps = GenerateMissingVmats( destination );

			// ModelDoc cannot consume glTF directly as a RenderMeshFile (its supported
			// skinned sources are FBX and model-DMX). Bridge glTF to model-DMX in-process;
			// the original file remains beside it for provenance and embedded-take import.
			var meshDestination = destination;
			var meshRelative = relative;
			var meshImportScale = target.ModelUnitScaleCm;
			if ( extension is ".glb" or ".gltf" )
			{
				// Embedded takes may share the source filename (e.g. scene). Keep model
				// DMX separate so writing animation DMX cannot overwrite the mesh.
				meshDestination = Path.Combine( Path.GetDirectoryName( destination )!, "mesh", Path.GetFileNameWithoutExtension( destination ) + ".dmx" );
				meshRelative = Path.Combine( Path.GetDirectoryName( relative )!, "mesh", Path.GetFileNameWithoutExtension( relative ) + ".dmx" ).Replace( '\\', '/' );
				Directory.CreateDirectory( Path.GetDirectoryName( meshDestination )! );
				// Preview compilation rebuilds Spec.Rig from the engine's converted bind.
				// Re-emitting the mesh with that rebuilt skeleton converts the bind twice:
				// the preview remains correct, but the final vmdl skins animated hands/limbs
				// against a different bind and stretches them. The mesh must always be written
				// from the model's original, sanitized source skeleton.
				var dmx = HumanoidMocap.Formats.Gltf.GltfModelDmxWriter.Write(
					File.ReadAllBytes( destination ),
					target.SourceModelSkeleton ?? target.Spec.Rig.Skeleton, safeStem,
					extension == ".gltf"
						? uri => TargetPickers.ReadGltfDependency( destination, uri )
						: null );
				File.WriteAllText( meshDestination, dmx );
				meshImportScale = 1f; // model-DMX was emitted in the rig's centimeter space
			}

			// The compiler resolves the RenderMeshFile input through the asset system - a
			// freshly copied file it has never seen fails the whole vmdl compile with
			// "Node 'X' resolve failure" (observed in the custom-target gate). Register it
			// like WriteAndCompileAsync registers the DMX outputs.
			Try( () => AssetSystem.RegisterFile( meshDestination ) );

			target.Spec.MeshFilePath = meshRelative;
			target.Spec.MeshImportScale = meshImportScale;
			target.Spec.MeshImportNames = extension == ".fbx"
				? HumanoidMocap.Formats.Fbx.FbxMeshParts.ReadNames( modelBytes ) : null;

			// The target model's own embedded animations must survive the conversion (user report:
			// "if I import an fbx with an animation and retarget another one onto it, it
			// should have two animations"). Each take is converted alongside the batch as
			// an exact IDENTITY retarget (see BuildEmbeddedTakeRequests) - AnimFile nodes
			// referencing the FBX directly cannot rescale translations (the node has no
			// import scale), which sank inch-authored animations to 40% height ("the
			// animation that came with the fbx is messed up").
			try
			{
				var takes = extension == ".fbx"
					? ExtractFbxTakeNames( File.ReadAllBytes( destination ) )
					: Retargeter.ImportSource(
						File.ReadAllBytes( destination ), fileName,
						externalBufferResolver: extension == ".gltf"
							? uri => TargetPickers.ReadGltfDependency( destination, uri )
							: null )
						.Clips.Select( clip => clip.Name ).ToList();
				if ( takes.Count > 0 )
				{
					target.EmbeddedTakeNames = takes
						.Select( take => takes.Count == 1
							? safeStem
							: safeStem + "_" + new string(
								take.Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() ) )
						.ToArray();
					Log.Info( $"[sbox-humanoid-mocap] preserving {takes.Count} embedded animation(s) "
						+ $"from the target model: {string.Join( ", ", target.EmbeddedTakeNames )}" );
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"[sbox-humanoid-mocap] could not read embedded takes: {e.Message}" );
			}

			return true;
		}
		catch ( Exception e )
		{
			error = $"Could not copy the target model into the project: {e.Message}";
			return false;
		}
	}

	// ====================================================== auto-vmat generation

	sealed class SourceMaterialInfo
	{
		public string Name { get; init; }
		public string ImportName { get; init; }
		public IEnumerable<string> TextureReferences => new[] { ColorTexture, NormalTexture,
			RoughnessTexture, MetalnessTexture, OcclusionTexture, EmissiveTexture, OpacityTexture }
			.Where( path => !string.IsNullOrWhiteSpace( path ) );
		public string ColorTexture { get; set; }
		public System.Numerics.Vector3? ColorFactor { get; set; }
		public bool VertexColors { get; set; }
		public string NormalTexture { get; set; }
		public string RoughnessTexture { get; set; }
		public string MetalnessTexture { get; set; }
		public string OcclusionTexture { get; set; }
		public string EmissiveTexture { get; set; }
		public string OpacityTexture { get; set; }
		public bool AlphaTest { get; set; }
		public bool Translucent { get; set; }
		public bool DoubleSided { get; set; }
		public float AlphaCutoff { get; set; } = 0.5f;
	}

	/// <summary>Reads the material→texture links authored in an FBX. Matching through
	/// object IDs makes texture filenames irrelevant (TrumpLPmat → tumpLPcolors.png).</summary>
	static List<SourceMaterialInfo> ExtractFbxMaterials( byte[] data )
	{
		var root = HumanoidMocap.Formats.Fbx.FbxTokenizer.Parse( data );
		var objects = root.Child( "Objects" );
		var connections = root.Child( "Connections" );
		var materials = new Dictionary<long, SourceMaterialInfo>();
		var textures = new Dictionary<long, string>();
		var videos = new Dictionary<long, string>();
		var doubleSidedModels = new HashSet<long>();

		if ( objects is not null )
		{
			foreach ( var node in objects.Children )
			{
				if ( node.Properties.Count < 2 || node.Properties[0] is not (long or int)
					|| node.Properties[1] is not string rawName )
					continue;
				var id = node.Prop<long>( 0 );
				if ( node.Name == "Model" && string.Equals(
					node.Child( "Culling" )?.Properties.FirstOrDefault() as string,
					"CullingOff", StringComparison.OrdinalIgnoreCase ) )
					doubleSidedModels.Add( id );
				if ( node.Name == "Material" )
				{
					materials[id] = new SourceMaterialInfo
					{
						Name = HumanoidMocap.Formats.Fbx.FbxNode.SplitName( rawName ).Name,
						// Native FBX import retains literal Class:: prefixes in binary exports.
						ImportName = rawName.Split( '\0' )[0],
						ColorFactor = HumanoidMocap.Formats.Fbx.FbxMaterialColor.Read( node ),
					};
				}
				else if ( node.Name is "Texture" or "Video" )
				{
					var file = node.Children.FirstOrDefault( child =>
						child.Name.Equals( "RelativeFilename", StringComparison.OrdinalIgnoreCase ) )
						?? node.Children.FirstOrDefault( child =>
							child.Name.Equals( "FileName", StringComparison.OrdinalIgnoreCase )
							|| child.Name.Equals( "Filename", StringComparison.OrdinalIgnoreCase ) );
					if ( file?.Properties.FirstOrDefault() is string path )
					{
						if ( node.Name == "Texture" ) textures[id] = path;
						else videos[id] = path;
					}
				}
			}
		}

		if ( connections is null )
			return materials.Values.ToList();

		var coloredGeometry = objects?.Children.Where( n => n.Name == "Geometry"
			&& HumanoidMocap.Formats.Fbx.FbxMaterialColor.HasVertexColors( n ) )
			.Select( n => n.Prop<long>( 0 ) ).ToHashSet() ?? new HashSet<long>();
		var coloredModels = connections.ChildrenNamed( "C" )
			.Where( n => n.Properties.Count >= 3 && n.Properties[0] is "OO"
				&& n.Properties[1] is long or int && n.Properties[2] is long or int
				&& coloredGeometry.Contains( n.Prop<long>( 1 ) ) )
			.Select( n => n.Prop<long>( 2 ) ).ToHashSet();

		// Video objects commonly carry the only usable filename and parent a Texture.
		foreach ( var connection in connections.ChildrenNamed( "C" ) )
		{
			if ( connection.Properties.Count < 3 || connection.Properties[0] is not string kind
				|| kind != "OO" || connection.Properties[1] is not (long or int)
				|| connection.Properties[2] is not (long or int) )
				continue;
			var source = connection.Prop<long>( 1 );
			var target = connection.Prop<long>( 2 );
			if ( videos.TryGetValue( source, out var file ) && !textures.ContainsKey( target ) )
				textures[target] = file;
		}

		foreach ( var connection in connections.ChildrenNamed( "C" ) )
		{
			if ( connection.Properties.Count < 3 || connection.Properties[0] is not string kind
				|| connection.Properties[1] is not (long or int)
				|| connection.Properties[2] is not (long or int) )
				continue;
			var source = connection.Prop<long>( 1 );
			var target = connection.Prop<long>( 2 );
			// FBX stores sidedness on the mesh model, not its material.
			if ( kind == "OO" && coloredModels.Contains( target )
				&& materials.TryGetValue( source, out var coloredMaterial ) )
				coloredMaterial.VertexColors = true;
			if ( kind == "OO" && doubleSidedModels.Contains( target )
				&& materials.TryGetValue( source, out var boundMaterial ) )
				boundMaterial.DoubleSided = true;
			if ( !textures.TryGetValue( source, out var file )
				|| !materials.TryGetValue( target, out var material ) )
				continue;
			var channel = kind == "OP" && connection.Properties.Count >= 4
				&& connection.Properties[3] is string property ? property : "DiffuseColor";
			if ( channel.Contains( "transparent", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "transparency", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "opacity", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "alpha", StringComparison.OrdinalIgnoreCase ) )
			{
				material.OpacityTexture ??= file;
				material.Translucent = true;
			}
			else if ( channel.Contains( "normal", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "bump", StringComparison.OrdinalIgnoreCase ) )
				material.NormalTexture ??= file;
			else if ( channel.Contains( "rough", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "gloss", StringComparison.OrdinalIgnoreCase ) )
				material.RoughnessTexture ??= file;
			else if ( channel.Contains( "metal", StringComparison.OrdinalIgnoreCase ) )
				material.MetalnessTexture ??= file;
			else if ( channel.Contains( "occlusion", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "ambient", StringComparison.OrdinalIgnoreCase ) )
				material.OcclusionTexture ??= file;
			else if ( channel.Contains( "emissive", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "emission", StringComparison.OrdinalIgnoreCase ) )
				material.EmissiveTexture ??= file;
			else if ( channel.Contains( "diffuse", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "color", StringComparison.OrdinalIgnoreCase ) )
				material.ColorTexture ??= file;
		}
		return materials.Values.ToList();
	}

	/// <summary>Reads glTF material links and extracts embedded images beside the copied
	/// model so generated vmats can reference ordinary project texture assets.</summary>
	static List<SourceMaterialInfo> ExtractGltfMaterials( byte[] data, string directory )
	{
		using var document = ParseGltfJson( data, out var binaryChunk );
		var root = document.RootElement;
		if ( !root.TryGetProperty( "materials", out var materialArray )
			|| materialArray.ValueKind != JsonValueKind.Array )
			return new List<SourceMaterialInfo>();

		var images = new List<string>();
		if ( root.TryGetProperty( "images", out var imageArray )
			&& imageArray.ValueKind == JsonValueKind.Array )
		{
			var imageIndex = 0;
			foreach ( var image in imageArray.EnumerateArray() )
			{
				string reference = null;
				if ( image.TryGetProperty( "uri", out var uriProperty ) )
				{
					var uri = uriProperty.GetString();
					if ( uri?.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ) == true )
						reference = WriteEmbeddedGltfImage( directory, image, imageIndex,
							DecodeDataUri( uri ) );
					else
						reference = uri;
				}
				else if ( image.TryGetProperty( "bufferView", out var viewProperty ) )
				{
					var bytes = ReadGltfBufferView(
						root, viewProperty.GetInt32(), binaryChunk, directory );
					reference = WriteEmbeddedGltfImage( directory, image, imageIndex, bytes );
				}
				images.Add( reference );
				imageIndex++;
			}
		}

		var textureSources = new List<int>();
		if ( root.TryGetProperty( "textures", out var textureArray )
			&& textureArray.ValueKind == JsonValueKind.Array )
		{
			foreach ( var texture in textureArray.EnumerateArray() )
				textureSources.Add( texture.TryGetProperty( "source", out var source )
					? source.GetInt32() : -1 );
		}

		string TextureOf( JsonElement owner, string property )
		{
			if ( !owner.TryGetProperty( property, out var textureInfo )
				|| !textureInfo.TryGetProperty( "index", out var indexProperty ) )
				return null;
			var textureIndex = indexProperty.GetInt32();
			if ( textureIndex < 0 || textureIndex >= textureSources.Count )
				return null;
			var imageIndex = textureSources[textureIndex];
			return imageIndex >= 0 && imageIndex < images.Count ? images[imageIndex] : null;
		}

		var result = new List<SourceMaterialInfo>();
		var index = 0;
		foreach ( var material in materialArray.EnumerateArray() )
		{
			var alphaMode = material.TryGetProperty( "alphaMode", out var alpha )
				? alpha.GetString() : "OPAQUE";
			var info = new SourceMaterialInfo
			{
				Name = material.TryGetProperty( "name", out var name )
					? name.GetString() ?? $"material_{index}" : $"material_{index}",
				NormalTexture = TextureOf( material, "normalTexture" ),
				OcclusionTexture = TextureOf( material, "occlusionTexture" ),
				EmissiveTexture = TextureOf( material, "emissiveTexture" ),
				AlphaTest = string.Equals( alphaMode, "MASK", StringComparison.OrdinalIgnoreCase ),
				Translucent = string.Equals( alphaMode, "BLEND", StringComparison.OrdinalIgnoreCase ),
				DoubleSided = material.TryGetProperty( "doubleSided", out var doubleSided )
					&& doubleSided.GetBoolean(),
				AlphaCutoff = material.TryGetProperty( "alphaCutoff", out var cutoff )
					? cutoff.GetSingle() : 0.5f,
			};
			if ( material.TryGetProperty( "pbrMetallicRoughness", out var pbr ) )
				info.ColorTexture = TextureOf( pbr, "baseColorTexture" );
			if ( info.AlphaTest || info.Translucent )
				info.OpacityTexture = info.ColorTexture; // glTF alpha lives in baseColorTexture.A
			result.Add( info );
			index++;
		}
		return result;
	}

	static JsonDocument ParseGltfJson( byte[] data, out byte[] binaryChunk )
	{
		binaryChunk = null;
		byte[] json = data;
		if ( data.Length >= 12 && ReadU32( data, 0 ) == 0x46546C67 ) // glTF
		{
			var declaredLength = ReadU32( data, 8 );
			if ( declaredLength > data.Length )
				throw new FormatException( "GLB header length exceeds the file size." );
			json = null;
			var offset = 12;
			while ( offset + 8 <= declaredLength )
			{
				var length = checked((int)ReadU32( data, offset ));
				var type = ReadU32( data, offset + 4 );
				offset += 8;
				if ( length < 0 || offset + (long)length > declaredLength )
					throw new FormatException( "GLB contains a truncated chunk." );
				if ( type == 0x4E4F534A && json is null ) // JSON
					json = data.AsSpan( offset, length ).ToArray();
				else if ( type == 0x004E4942 && binaryChunk is null ) // BIN
					binaryChunk = data.AsSpan( offset, length ).ToArray();
				offset += length;
			}
			if ( json is null )
				throw new FormatException( "GLB contains no JSON chunk." );
		}

		return JsonDocument.Parse( Encoding.UTF8.GetString( json ).TrimEnd( '\0', ' ' ) );
	}

	static uint ReadU32( byte[] data, int offset )
		=> (uint)(data[offset] | data[offset + 1] << 8
			| data[offset + 2] << 16 | data[offset + 3] << 24);

	static byte[] DecodeDataUri( string uri )
	{
		var comma = uri.IndexOf( ',' );
		if ( comma < 0 || !uri[..comma].EndsWith( ";base64", StringComparison.OrdinalIgnoreCase ) )
			throw new FormatException( "glTF image data URI is not base64 encoded." );
		return Convert.FromBase64String( uri[(comma + 1)..] );
	}

	static byte[] ReadGltfBufferView(
		JsonElement root, int viewIndex, byte[] binaryChunk, string gltfDirectory )
	{
		var views = root.GetProperty( "bufferViews" );
		if ( viewIndex < 0 || viewIndex >= views.GetArrayLength() )
			throw new FormatException( $"glTF image references invalid bufferView {viewIndex}." );
		var view = views[viewIndex];
		var bufferIndex = view.GetProperty( "buffer" ).GetInt32();
		var buffers = root.GetProperty( "buffers" );
		if ( bufferIndex < 0 || bufferIndex >= buffers.GetArrayLength() )
			throw new FormatException( $"glTF bufferView references invalid buffer {bufferIndex}." );
		var buffer = buffers[bufferIndex];
		byte[] bytes;
		if ( !buffer.TryGetProperty( "uri", out var uriProperty ) )
			bytes = binaryChunk ?? throw new FormatException( "glTF image buffer has no data." );
		else
		{
			var uri = uriProperty.GetString() ?? "";
			bytes = uri.StartsWith( "data:", StringComparison.OrdinalIgnoreCase )
				? DecodeDataUri( uri )
				: TargetPickers.ReadGltfDependency( Path.Combine( gltfDirectory, "model.gltf" ), uri );
		}
		var offset = view.TryGetProperty( "byteOffset", out var offsetProperty )
			? offsetProperty.GetInt32() : 0;
		var length = view.GetProperty( "byteLength" ).GetInt32();
		if ( offset < 0 || length < 0 || offset + (long)length > bytes.Length )
			throw new FormatException( "glTF image bufferView exceeds its buffer." );
		return bytes.AsSpan( offset, length ).ToArray();
	}

	static string WriteEmbeddedGltfImage(
		string directory, JsonElement image, int index, byte[] bytes )
	{
		var mime = image.TryGetProperty( "mimeType", out var mimeProperty )
			? mimeProperty.GetString() : null;
		var extension = mime?.ToLowerInvariant() switch
		{
			"image/jpeg" => ".jpg",
			"image/webp" => ".webp",
			"image/tga" => ".tga",
			_ => ".png",
		};
		var name = image.TryGetProperty( "name", out var nameProperty )
			? nameProperty.GetString() : null;
		var safeName = new string( (name ?? $"image_{index}")
			.Select( c => char.IsLetterOrDigit( c ) || c == '_' ? c : '_' ).ToArray() );
		var relative = $"textures/{safeName}_{index}{extension}";
		var path = Path.Combine( directory, relative.Replace( '/', Path.DirectorySeparatorChar ) );
		Directory.CreateDirectory( Path.GetDirectoryName( path ) );
		File.WriteAllBytes( path, bytes );
		Try( () => AssetSystem.RegisterFile( path ) );
		return relative;
	}

	static void CopyGltfDependencies( string sourceGltf, string destinationGltf )
	{
		using var document = ParseGltfJson( File.ReadAllBytes( sourceGltf ), out _ );
		var root = document.RootElement;
		foreach ( var arrayName in new[] { "buffers", "images" } )
		{
			if ( !root.TryGetProperty( arrayName, out var entries )
				|| entries.ValueKind != JsonValueKind.Array )
				continue;
			foreach ( var entry in entries.EnumerateArray() )
			{
				if ( !entry.TryGetProperty( "uri", out var uriProperty ) )
					continue;
				var uri = uriProperty.GetString();
				if ( string.IsNullOrEmpty( uri )
					|| uri.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ) )
					continue;
				var relative = Uri.UnescapeDataString( uri.Split( '?', '#' )[0] )
					.Replace( '/', Path.DirectorySeparatorChar );
				var bytes = TargetPickers.ReadGltfDependency( sourceGltf, uri );
				var destinationDirectory = Path.GetFullPath( Path.GetDirectoryName( destinationGltf ) );
				var destination = Path.GetFullPath( Path.Combine( destinationDirectory, relative ) );
				if ( !destination.StartsWith( destinationDirectory.TrimEnd( Path.DirectorySeparatorChar )
					+ Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
					throw new FormatException( $"glTF dependency escapes the output folder: '{uri}'." );
				Directory.CreateDirectory( Path.GetDirectoryName( destination ) );
				File.WriteAllBytes( destination, bytes );
				Try( () => AssetSystem.RegisterFile( destination ) );
			}
		}
	}

	/// <summary>
	/// Writes a <c>.vmat</c> next to the copied target model for every source material
	/// that has none: the compiler otherwise logs 'Missing vmat "&lt;name&gt;.vmat"' per
	/// mesh and the model renders with placeholder materials. Texture assignment follows
	/// authored material links first, then falls back to name-token overlap against sidecars
	/// (<c>mi_danteDark_lowerBody</c> → <c>t_danteDark_lowerBody_d.png</c> color +
	/// <c>…_n.png</c> normal). Existing vmats are never touched; failures never fail the
	/// conversion. Returns the material remap table for the generated vmdls (bare
	/// material reference → assets-relative vmat path), or null when there is nothing
	/// to remap.
	/// </summary>
	static IReadOnlyDictionary<string, string> GenerateMissingVmats( string modelAbsolutePath )
	{
		try
		{
			var assetsPath = Project.Current?.GetAssetsPath();
			if ( assetsPath is null )
				return null;

			var modelBytes = File.ReadAllBytes( modelAbsolutePath );
			var directory = Path.GetDirectoryName( modelAbsolutePath );
			var extension = Path.GetExtension( modelAbsolutePath ).ToLowerInvariant();
			var materialInfo = extension == ".fbx"
				? ExtractFbxMaterials( modelBytes )
				: ExtractGltfMaterials( modelBytes, directory );
			var materials = materialInfo.Select( m => m.Name ).ToList();
			if ( materials.Count == 0 )
			{
				// An FBX with NO material objects at all (bare Blender export): the engine
				// derives the material slot from the GEOMETRY name and then wants
				// '<geometry>.vmat' (observed: mesh 'Cube' -> Missing vmat "cube.vmat",
				// an unsatisfiable illegal-resource lookup). Stub those instead.
				materials = extension == ".fbx"
					? ExtractFbxObjectNames( modelBytes, "Geometry" )
					.Select( n => n.ToLowerInvariant() )
					.Distinct()
					.ToList()
					: new List<string>();
				if ( materials.Count == 0 )
					return null;
				Log.Info( $"[sbox-humanoid-mocap] FBX carries no materials - stubbing vmats "
					+ $"for its geometry slots: {string.Join( ", ", materials )}" );
			}

			var textures = new List<string>();
			foreach ( var pattern in new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.dds", "*.webp" } )
			{
				textures.AddRange( Directory.GetFiles( directory, pattern ) );
				var texturesDir = Path.Combine( directory, "textures" );
				if ( Directory.Exists( texturesDir ) )
					textures.AddRange( Directory.GetFiles( texturesDir, pattern, SearchOption.AllDirectories ) );
			}

			textures.RemoveAll( path => path.EndsWith( "_hr.png", StringComparison.OrdinalIgnoreCase )
				|| path.EndsWith( "_hr_alpha.png", StringComparison.OrdinalIgnoreCase ) );

			// Tokens shared by most of the texture set (the character's base name, e.g.
			// dante+dark) must never decide a match on their own - "mi_danteDark_vest"
			// would otherwise take the hair texture purely on those (observed: the hair
			// mask ended up on the eyes). A match needs at least one DISTINCTIVE token.
			var tokenCounts = new Dictionary<string, int>( StringComparer.Ordinal );
			foreach ( var candidate in textures )
			{
				foreach ( var token in NameTokens( Path.GetFileNameWithoutExtension( candidate ) ) )
					tokenCounts[token] = tokenCounts.GetValueOrDefault( token ) + 1;
			}
			var ubiquitous = tokenCounts
				.Where( kv => kv.Value >= 2 && kv.Value * 2 >= textures.Count )
				.Select( kv => kv.Key )
				.ToHashSet( StringComparer.Ordinal );

			var remaps = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var material in materials )
			{
				var safeMaterial = new string( material
					.Select( c => char.IsLetterOrDigit( c ) || c == '_' ? c : '_' ).ToArray() );
				if ( string.IsNullOrEmpty( safeMaterial ) )
					safeMaterial = "material";
				var vmatPath = Path.Combine( directory, safeMaterial + ".vmat" );
				// Remap the BARE reference the mesh carries to the real file, generated or
				// pre-existing (resource paths are lowercase by engine convention).
				remaps[material.ToLowerInvariant() + ".vmat"] =
					Path.GetRelativePath( assetsPath, vmatPath ).Replace( '\\', '/' ).ToLowerInvariant();
				var authored = materialInfo.FirstOrDefault( m =>
					string.Equals( m.Name, material, StringComparison.OrdinalIgnoreCase ) );
				if ( !string.IsNullOrEmpty( authored?.ImportName ) )
					remaps[authored.ImportName.ToLowerInvariant() + ".vmat"] = remaps[material.ToLowerInvariant() + ".vmat"];
				if ( File.Exists( vmatPath ) )
				{
					// Files still carrying the auto-generated header are ours to UPGRADE -
					// vmats from an older library version keep old defects forever
					// otherwise (user report: opaque eyelashes generated before alpha-test
					// support existed). Deleting the header line makes manual edits
					// permanent.
					try
					{
						using var reader = new StreamReader( vmatPath );
						if ( reader.ReadLine()?.Contains( "Auto-generated by sbox-humanoid-mocap" ) != true )
							continue;
					}
					catch
					{
						continue;
					}
				}

				// Suffix conventions collected from real exports (Sketchfab rips, Unity
				// packs, Blender/Substance/Marmoset outputs) - the user's assets keep
				// arriving with new ones, so every known spelling is listed.
				var color = FindAuthoredTexture( authored?.ColorTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{
						"_d", "_dm", "_dif", "_diff", "_diffuse", "diffuse",
						"_alb", "_albedo", "albedo", "_basecolor", "_base_color", "basecolor",
						"_bc", "_col", "_color", "_colour", "color", "_clr", "_base",
					} )
					// Stand-in when the set ships no diffuse for this material (real case:
					// a vest with only a specular map): a distinctively NAMED non-normal
					// map carries the garment's actual detail and reads far better than
					// flat placeholder white.
					?? BestTextureMatch( material, textures, new[]
					{
						"_s", "_spec", "_specular", "_m", "_metal", "_metallic", "_metalness",
						"_ao", "_occlusion", "_mask", "_e", "_emissive", "_emission", "_glow",
					} )
					// Last resort: ANY distinctively named non-normal image. Plain-named
					// texture sets carry no suffix at all (real case: material "homer"
					// shipping "homer.png" - both suffix passes skipped it and the model
					// rendered untextured).
					?? BestTextureMatch( material, textures
						.Where( t => !Path.GetFileNameWithoutExtension( t )
							.ToLowerInvariant().EndsWith( "_n" ) )
						.ToList(), new[] { "" } )
					?? SingleColorTexture( textures );
				var normal = FindAuthoredTexture( authored?.NormalTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_n", "_nrm", "_nm", "_nor", "_norm", "_normal", "normal", "_normalmap", "_bump" } );
				var rough = FindAuthoredTexture( authored?.RoughnessTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_r", "_rough", "_roughness", "roughness", "_g", "_gloss", "_glossiness" } );
				var metal = FindAuthoredTexture( authored?.MetalnessTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_m", "_metal", "_metallic", "_metalness", "metallic" } );
				var occlusion = FindAuthoredTexture( authored?.OcclusionTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_ao", "_occlusion", "_ambientocclusion" } );
				var emissive = FindAuthoredTexture( authored?.EmissiveTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_e", "_emissive", "_emission", "_glow" } );
				var opacity = FindAuthoredTexture( authored?.OpacityTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_a", "_alpha", "_opacity", "_trans", "_transparency" } );

				// Card/strand geometry (lashes, hair, brows, anything the modeler named
				// "masked") is authored for alpha testing - rendered opaque it shows as
				// solid white sheets (user report: "the makeup around the eye is white").
				var materialTokens = NameTokens( material );
				var alphaTest = authored?.AlphaTest == true || (authored?.Translucent != true
					&& materialTokens.Any( token =>
						token is "mask" or "masked" or "lash" or "lashes" or "eyelash" or "eyelashes"
							or "hair" or "hairs" or "brow" or "brows" or "eyebrow" or "eyebrows"
							or "fur" or "feather" or "feathers" ));
				var translucent = authored?.Translucent == true || opacity is not null;
				if ((alphaTest || translucent) && opacity is null)
					opacity = color; // common packed RGBA texture (including glTF baseColor)
				if (opacity is not null && string.Equals(opacity, color, StringComparison.OrdinalIgnoreCase))
				{
					opacity = ExtractPackedAlpha(opacity);
					// Exporters often connect the diffuse image to opacity even when every
					// pixel is opaque. Do not put that surface in the translucent sorting pass.
					if ( opacity is null )
					{
						alphaTest = false;
						translucent = false;
					}
				}

				color = PrepareTexture( color );
				normal = PrepareTexture( normal );
				rough = PrepareTexture( rough );
				metal = PrepareTexture( metal );
				occlusion = PrepareTexture( occlusion );
				emissive = PrepareTexture( emissive );
				opacity = PrepareTexture( opacity );

				var builder = new System.Text.StringBuilder();
				builder.AppendLine( "// Auto-generated by sbox-humanoid-mocap from the target model's material list." );
				builder.AppendLine( "// Regenerated on conversion while this header stays - DELETE THE LINE ABOVE to make manual edits permanent." );
				builder.AppendLine( "Layer0" );
				builder.AppendLine( "{" );
				builder.AppendLine( authored?.VertexColors == true && color is null
					? "\tshader \"shaders/vertex_color.shader\""
					: "\tshader \"shaders/complex.shader\"" );
				if ( alphaTest )
				{
					builder.AppendLine( "\tF_ALPHA_TEST 1" );
					builder.AppendLine( $"\tg_flAlphaTestReference \"{(authored?.AlphaCutoff ?? 0.5f).ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture )}\"" );
				}
				if ( translucent )
					builder.AppendLine( "\tF_TRANSLUCENT 1" );
				if ( authored?.DoubleSided == true )
					builder.AppendLine( "\tF_RENDER_BACKFACES 1" );
				builder.AppendLine( $"\tTextureColor \"{(color ?? "materials/default/default_color.tga")}\"" );
				if ( color is null && authored?.ColorFactor is { } tint )
					builder.AppendLine( FormattableString.Invariant(
						$"\tg_vColorTint \"[{tint.X:R} {tint.Y:R} {tint.Z:R} 1]\"" ) );
				if ( opacity is not null )
					builder.AppendLine( $"\tTextureTranslucency \"{opacity}\"" );
				builder.AppendLine( $"\tTextureNormal \"{(normal ?? "materials/default/default_normal.tga")}\"" );
				builder.AppendLine( $"\tTextureRoughness \"{(rough ?? "materials/default/default_rough.tga")}\"" );
				if ( metal is not null )
				{
					builder.AppendLine( "\tF_METALNESS_TEXTURE 1" );
					builder.AppendLine( $"\tTextureMetalness \"{metal}\"" );
				}
				if ( occlusion is not null )
					builder.AppendLine( $"\tTextureAmbientOcclusion \"{occlusion}\"" );
				if ( emissive is not null )
				{
					builder.AppendLine( "\tF_SELF_ILLUM 1" );
					builder.AppendLine( $"\tTextureSelfIllumMask \"{emissive}\"" );
				}
				builder.AppendLine( "}" );
				File.WriteAllText( vmatPath, builder.ToString() );
				Try( () => AssetSystem.RegisterFile( vmatPath ) );
				Log.Info( $"[sbox-humanoid-mocap] generated material {Path.GetFileName( vmatPath )} "
					+ $"(color: {color ?? "default"}, normal: {normal ?? "default"}, rough: {rough ?? "default"})" );
			}

			return remaps.Count > 0 ? remaps : null;

			// The texture compiler rejects JPEG's .jpeg extension and WebP. Keep matching
			// against the authored files, then convert the selected image for every channel.
			string PrepareTexture( string relative )
			{
				if ( relative is null || Path.GetExtension( relative ).ToLowerInvariant() is not (".jpeg" or ".webp") )
					return relative;
				var source = Path.Combine( assetsPath, relative );
				var output = source + "_hr.png";
				using var bitmap = SkiaSharp.SKBitmap.Decode( source )
					?? throw new FormatException( $"Cannot decode texture '{relative}'." );
				using var image = SkiaSharp.SKImage.FromBitmap( bitmap );
				using var data = image.Encode( SkiaSharp.SKEncodedImageFormat.Png, 100 );
				using ( var stream = File.Open( output, FileMode.Create, FileAccess.Write, FileShare.Read ) )
					data.SaveTo( stream );
				Try( () => AssetSystem.RegisterFile( output ) );
				return Path.GetRelativePath( assetsPath, output ).Replace( '\\', '/' );
			}

			string FindAuthoredTexture( string reference, List<string> candidates )
			{
				if ( string.IsNullOrEmpty( reference ) )
					return null;
				var decoded = Uri.UnescapeDataString( reference.Split( '?', '#' )[0] )
					.Replace( '/', Path.DirectorySeparatorChar );
				if ( !Path.IsPathRooted( decoded ) )
				{
					var direct = Path.GetFullPath( Path.Combine( directory, decoded ) );
					var root = Path.GetFullPath( directory ).TrimEnd( Path.DirectorySeparatorChar )
						+ Path.DirectorySeparatorChar;
					if ( direct.StartsWith( root, StringComparison.OrdinalIgnoreCase ) && File.Exists( direct ) )
						return Path.GetRelativePath( assetsPath, direct ).Replace( '\\', '/' );
				}
				var name = Path.GetFileName( decoded );
				var stem = Path.GetFileNameWithoutExtension( name );
				var match = candidates.FirstOrDefault( candidate =>
					string.Equals( Path.GetFileName( candidate ), name, StringComparison.OrdinalIgnoreCase ) )
					?? candidates.FirstOrDefault( candidate => string.Equals(
						Path.GetFileNameWithoutExtension( candidate ), stem,
						StringComparison.OrdinalIgnoreCase ) );
				return match is null ? null
					: Path.GetRelativePath( assetsPath, match ).Replace( '\\', '/' );
			}

			// complex.shader's TextureTranslucency input reads a grayscale image; it does
			// not implicitly select TextureColor.A. Preserve packed-RGBA materials by
			// extracting that authored alpha channel beside the copied source texture.
			string ExtractPackedAlpha( string relative )
			{
				try
				{
					var source = Path.GetFullPath( Path.Combine(
						assetsPath, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );
					using var bitmap = SkiaSharp.SKBitmap.Decode( source );
					if ( bitmap is null || bitmap.Width == 0 || bitmap.Height == 0 )
						return null;

					var hasAlpha = false;
					for ( var y = 0; y < bitmap.Height && !hasAlpha; y++ )
					{
						for ( var x = 0; x < bitmap.Width; x++ )
						{
							if ( bitmap.GetPixel( x, y ).Alpha < 255 )
							{
								hasAlpha = true;
								break;
							}
						}
					}
					if ( !hasAlpha )
						return null;

					using var mask = new SkiaSharp.SKBitmap(
						bitmap.Width, bitmap.Height, SkiaSharp.SKColorType.Rgba8888,
						SkiaSharp.SKAlphaType.Opaque );
					for ( var y = 0; y < bitmap.Height; y++ )
					{
						for ( var x = 0; x < bitmap.Width; x++ )
						{
							var alpha = bitmap.GetPixel( x, y ).Alpha;
							mask.SetPixel( x, y, new SkiaSharp.SKColor( alpha, alpha, alpha ) );
						}
					}

					var output = Path.Combine( Path.GetDirectoryName( source ),
						Path.GetFileNameWithoutExtension( source ) + "_hr_alpha.png" );
					using var image = SkiaSharp.SKImage.FromBitmap( mask );
					using var data = image.Encode( SkiaSharp.SKEncodedImageFormat.Png, 100 );
					using ( var stream = File.Open( output, FileMode.Create, FileAccess.Write, FileShare.Read ) )
						data.SaveTo( stream );
					Try( () => AssetSystem.RegisterFile( output ) );
					return Path.GetRelativePath( assetsPath, output ).Replace( '\\', '/' );
				}
				catch ( Exception e )
				{
					Log.Warning( $"[sbox-humanoid-mocap] packed alpha extraction failed: {e.Message}" );
					return null;
				}
			}

			string SingleColorTexture( List<string> candidates )
			{
				var plausible = candidates.Where( candidate =>
				{
					var tokens = NameTokens( Path.GetFileNameWithoutExtension( candidate ) );
					return !tokens.Any( token => token is "n" or "nrm" or "normal" or "bump"
						or "rough" or "roughness" or "gloss" or "metal" or "metallic"
						or "metalness" or "ao" or "occlusion" );
				} ).ToList();
				return plausible.Count == 1
					? Path.GetRelativePath( assetsPath, plausible[0] ).Replace( '\\', '/' )
					: null;
			}

			string BestTextureMatch( string material, List<string> candidates, string[] suffixes )
			{
				var materialTokens = NameTokens( material );
				var scored = new List<(string Path, int Score)>();
				foreach ( var candidate in candidates )
				{
					// Tokenize the RAW stem: lower-casing first would erase its camelCase
					// boundaries ("t_danteDark_head_d" -> one "dantedark" token that can
					// never match the material's dante+dark tokens - observed as the head
					// and lower body rendering untextured white while the arms worked).
					var stem = Path.GetFileNameWithoutExtension( candidate );
					// Numbered layer variants ("eye_diff", "eye_diff2", "eye_diff3") are
					// all diffuse CANDIDATES - suffixes match with trailing digits ignored.
					var stemNoDigits = stem.TrimEnd( '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' );
					if ( !suffixes.Any( s => stem.EndsWith( s, StringComparison.OrdinalIgnoreCase )
						|| stemNoDigits.EndsWith( s, StringComparison.OrdinalIgnoreCase ) ) )
						continue;
					var shared = NameTokens( stem ).Where( materialTokens.Contains ).ToList();
					var distinctive = shared.Count( t => !ubiquitous.Contains( t ) );
					scored.Add( (candidate, distinctive * 10 + shared.Count) );
				}
				if ( scored.Count == 0 )
					return null;

				var bestScore = scored.Max( c => c.Score );
				var top = scored.Where( c => c.Score == bestScore ).ToList();
				var variantSet = top.All( c => SameVariantFamily( top[0].Path, c.Path ) );

				// A DISTINCTIVE shared token (score >= 10) always wins. Base-name-only
				// overlap is accepted only when unambiguous: single-set exports name
				// everything '<character>_*' ("sonic_mat" + "sonic_diff" is the only
				// diffuse that shares anything - a correct match the old distinctive-only
				// rule rejected, body rendered untextured). Base-only ties across
				// DIFFERENT names stay rejected (the case that mapped hair onto the
				// eyes); numbered variants of ONE name are a layer set, decided below.
				if ( bestScore == 0 || (bestScore < 10 && !variantSet) )
					return null;

				// Composite-shader layer sets ship several same-named images (a mobile
				// eye: gray ball with black pupil + white sclera mask + catchlight dot;
				// the game blends them in a custom shader). A single stand-in must be the
				// layer a viewer would call "the texture": the one with the BRIGHTEST
				// CENTRAL region - masks and catchlights are black-centered, and the
				// pupil-hole layer rendered Sonic's eyes solid black.
				var best = variantSet && top.Count > 1
					? top.OrderByDescending( CenterBrightness ).First().Path
					: top[0].Path;
				if ( variantSet && top.Count > 1 )
					Log.Info( $"[sbox-humanoid-mocap] '{material}': picked "
						+ $"{Path.GetFileName( best )} from {top.Count} layer variants by center brightness" );
				return Path.GetRelativePath( assetsPath, best ).Replace( '\\', '/' );
			}

			static bool SameVariantFamily( string a, string b )
			{
				static string Family( string p ) => Path.GetFileNameWithoutExtension( p )
					.TrimEnd( '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' )
					.ToLowerInvariant();
				return Family( a ) == Family( b );
			}

			// Mean luminance of the central half of the image, sparsely sampled.
			static float CenterBrightness( (string Path, int Score) candidate )
			{
				try
				{
					using var bitmap = SkiaSharp.SKBitmap.Decode( candidate.Path );
					if ( bitmap is null || bitmap.Width == 0 || bitmap.Height == 0 )
						return -1f;
					float sum = 0;
					var samples = 0;
					var stepX = Math.Max( 1, bitmap.Width / 32 );
					var stepY = Math.Max( 1, bitmap.Height / 32 );
					for ( var y = bitmap.Height / 4; y < bitmap.Height * 3 / 4; y += stepY )
					{
						for ( var x = bitmap.Width / 4; x < bitmap.Width * 3 / 4; x += stepX )
						{
							var c = bitmap.GetPixel( x, y );
							sum += (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue)
								* (c.Alpha / 255f) / 255f;
							samples++;
						}
					}
					return samples > 0 ? sum / samples : -1f;
				}
				catch
				{
					return -1f; // undecodable: rank below anything readable
				}
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] vmat generation failed: {e.Message}" );
			return null;
		}
	}

	/// <summary>Lower-case name tokens split on separators and camelCase boundaries,
	/// with generic prefixes (mi_/m_/t_/tex_) dropped.</summary>
	static HashSet<string> NameTokens( string name )
	{
		var tokens = new HashSet<string>( StringComparer.Ordinal );
		var current = new System.Text.StringBuilder();
		void Commit()
		{
			if ( current.Length > 0 )
			{
				var token = current.ToString().ToLowerInvariant();
				if ( token is not ("mi" or "m" or "t" or "tex") )
					tokens.Add( token );
				current.Clear();
			}
		}
		for ( var i = 0; i < name.Length; i++ )
		{
			var c = name[i];
			if ( !char.IsLetterOrDigit( c ) )
			{
				Commit();
				continue;
			}
			if ( char.IsUpper( c ) && current.Length > 0 && char.IsLower( name[i - 1] ) )
				Commit();
			current.Append( c );
		}
		Commit();
		return tokens;
	}

	/// <summary>Material object names from an FBX (see <see cref="ExtractFbxObjectNames"/>).</summary>
	internal static List<string> ExtractFbxMaterialNames( byte[] data )
		=> ExtractFbxObjectNames( data, "Material" );

	/// <summary>Animation take (AnimStack) names from an FBX.</summary>
	internal static List<string> ExtractFbxTakeNames( byte[] data )
		=> ExtractFbxObjectNames( data, "AnimStack" );

	/// <summary>
	/// Object names of one FBX class: binary FBX stores object names as
	/// <c>"&lt;name&gt;\0\x01&lt;Class&gt;"</c>, ASCII as <c>"&lt;Class&gt;::&lt;name&gt;"</c>.
	/// </summary>
	internal static List<string> ExtractFbxObjectNames( byte[] data, string className )
	{
		var names = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		void Add( string name )
		{
			name = name.Trim();
			if ( name.Length is > 0 and <= 80 && seen.Add( name ) )
				names.Add( name );
		}

		// Binary: <name> 0x00 0x01 <Class>
		var marker = System.Text.Encoding.ASCII.GetBytes( "\0\u0001" + className );
		for ( var i = IndexOfBytes( data, marker, 0 ); i >= 0; i = IndexOfBytes( data, marker, i + 1 ) )
		{
			var start = i;
			while ( start > 0 && data[start - 1] >= 0x20 && data[start - 1] < 0x7f )
				start--;
			if ( i - start is > 0 and <= 80 )
				Add( System.Text.Encoding.ASCII.GetString( data, start, i - start ) );
		}

		// ASCII: <Class>::<name>
		var ascii = System.Text.Encoding.ASCII.GetBytes( className + "::" );
		for ( var i = IndexOfBytes( data, ascii, 0 ); i >= 0; i = IndexOfBytes( data, ascii, i + 1 ) )
		{
			var start = i + ascii.Length;
			var end = start;
			while ( end < data.Length && data[end] != (byte)'"' && data[end] >= 0x20 && data[end] < 0x7f )
				end++;
			if ( end - start is > 0 and <= 80 )
				Add( System.Text.Encoding.ASCII.GetString( data, start, end - start ) );
		}

		return names;
	}

	static int IndexOfBytes( byte[] haystack, byte[] needle, int from )
	{
		for ( var i = Math.Max( from, 0 ); i <= haystack.Length - needle.Length; i++ )
		{
			var match = true;
			for ( var j = 0; j < needle.Length; j++ )
			{
				if ( haystack[i + j] != needle[j] ) { match = false; break; }
			}
			if ( match )
				return i;
		}
		return -1;
	}

	/// <summary>
	/// Requests converting the target model's own embedded takes onto the target rig — an
	/// exact identity retarget (source and target are the same skeleton), emitted as
	/// unit-correct DMX so the preserved animations play at the right scale. The mapping
	/// is taken from the target rig itself (index-aligned with the import), so this works
	/// even when the rig was only accepted best-effort. Returns an empty list for compiled
	/// targets or take-less source files.
	/// </summary>
	public static List<RetargetRequest> BuildEmbeddedTakeRequests( TargetPickers.ResolvedTarget target )
	{
		var requests = new List<RetargetRequest>();
		if ( target?.ModelFilePath is null || target.EmbeddedTakeNames is not { Count: > 0 } names )
			return requests;

		try
		{
			var bytes = File.ReadAllBytes( target.ModelFilePath );
			var fileName = Path.GetFileName( target.ModelFilePath );
			var isGltf = Path.GetExtension( target.ModelFilePath )
				.Equals( ".gltf", StringComparison.OrdinalIgnoreCase );

			// Role-based mapping via the normal cascade: the target rig may be rebuilt
			// from the COMPILED model (different bone order/count than the raw import),
			// so index-aligned identity would mis-bind - the same-anatomy role retarget
			// is near-identity anyway.
			for ( var take = 0; take < names.Count; take++ )
			{
				requests.Add( new RetargetRequest
				{
					SourceData = bytes,
					SourceFileName = fileName,
					SourceId = target.ModelFilePath + "#embedded" + take,
					ExternalBufferResolver = isGltf
						? uri => TargetPickers.ReadGltfDependency( target.ModelFilePath, uri )
						: null,
					TakeIndex = names.Count > 1 ? take : null,
					ClipNameOverride = names[take],
					RootMotion = Cleanup.RootMotionMode.Off,
					FootPlantCleanup = false,   // authored data - transfer exactly, clean nothing
					ArmEffectorIk = false,
					// A Biped take animates local translations (spine sway, thigh shifts);
					// without this they pin to rest and the authored motion stiffens.
					PreserveSourceTranslations = true,
					LoopingOverride = null,
				} );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] embedded takes could not be converted: {e.Message}" );
		}

		return requests;
	}

	static readonly string[] TextureExtensions =
		{ ".png", ".jpg", ".jpeg", ".tga", ".dds", ".webp", ".vmat", ".vtex" };

	/// <summary>Copies texture sidecars of a picked target model into the output folder:
	/// loose image files next to it, and a "textures" folder next to it or next to its
	/// parent (the source/-plus-textures/ layout). Per-file best effort - a failed texture
	/// must never fail the conversion.</summary>
	static void CopySidecarTextures( string sourceDir, string destDir, IEnumerable<string> references = null )
	{
		try
		{
			if ( sourceDir is null || destDir is null )
				return;
			sourceDir = Path.GetFullPath( sourceDir );
			destDir = Path.GetFullPath( destDir );
			if ( string.Equals( sourceDir, destDir, StringComparison.OrdinalIgnoreCase ) )
				return;

			// Preserve model-local authored paths; do not flatten filenames or copy an
			// arbitrary parent directory when an exporter supplies an external path.
			foreach ( var reference in references ?? Enumerable.Empty<string>() )
			{
				var sourceFile = Path.GetFullPath( Path.Combine( sourceDir, reference.Replace( '\\', '/' ) ) );
				var relative = Path.GetRelativePath( sourceDir, sourceFile );
				if ( Path.IsPathRooted( relative ) || relative == ".."
					|| relative.StartsWith( ".." + Path.DirectorySeparatorChar ) || !File.Exists( sourceFile ) )
					continue;
				var destFile = Path.Combine( destDir, relative );
				Try( () =>
				{
					Directory.CreateDirectory( Path.GetDirectoryName( destFile ) );
					File.Copy( sourceFile, destFile, true );
					AssetSystem.RegisterFile( destFile );
					return true;
				} );
			}

			// Every copied file must be REGISTERED: assets copied onto disk mid-session are
			// unknown to the asset system, so the material chain cannot generate their vtex
			// resources - the renderer then logs "Texture manager doesn't know about
			// texture ...generated.vtex" MANY TIMES PER FRAME, which is both the
			// purple/black flicker and a preview running at ~2 fps (user report).
			foreach ( var file in Directory.GetFiles( sourceDir ) )
			{
				if ( !TextureExtensions.Contains( Path.GetExtension( file ).ToLowerInvariant() ) )
					continue;
				var destFile = Path.Combine( destDir, Path.GetFileName( file ) );
				Try( () => { File.Copy( file, destFile, true ); return true; } );
				Try( () => AssetSystem.RegisterFile( destFile ) );
			}

			foreach ( var candidate in new[]
			{
				Path.Combine( sourceDir, "textures" ),
				Path.Combine( Path.GetDirectoryName( sourceDir ) ?? sourceDir, "textures" ),
			} )
			{
				if ( !Directory.Exists( candidate ) )
					continue;
				var destTextures = Path.Combine( destDir, "textures" );
				Directory.CreateDirectory( destTextures );
				foreach ( var file in Directory.GetFiles( candidate, "*", SearchOption.AllDirectories ) )
				{
					var relative = Path.GetRelativePath( candidate, file );
					var destFile = Path.Combine( destTextures, relative );
					Try( () =>
					{
						Directory.CreateDirectory( Path.GetDirectoryName( destFile ) );
						File.Copy( file, destFile, true );
						return true;
					} );
					Try( () => AssetSystem.RegisterFile( destFile ) );
				}
				break; // first existing candidate wins
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] sidecar texture copy failed: {e.Message}" );
		}
	}

	/// <summary>
	/// Compatibility entry point for compiling a skinned source-model preview. Copies the
	/// model into the output folder
	/// (<see cref="PrepareFbxTargetMesh"/>), writes a mesh-only vmdl next to it
	/// (<c>&lt;stem&gt;_preview.vmdl</c> — RenderMeshFile + ScaleAndMirror, no animations),
	/// compiles it, and on success points the target's
	/// <see cref="TargetPickers.ResolvedTarget.PreviewModelPath"/> at it so the preview
	/// dialog shows the actual skinned model instead of the wireframe skeleton. False when
	/// anything failed (the caller keeps the skeleton-view fallback); no-op true for
	/// compiled-model targets.
	/// </summary>
	public static async Task<bool> CompileFbxTargetPreviewAsync(
		TargetPickers.ResolvedTarget target, string outputFolderRelative )
		=> await CompileModelTargetPreviewAsync( target, outputFolderRelative );

	/// <summary>Compiles the mesh-only preview for an FBX, GLB, or glTF target.</summary>
	public static async Task<bool> CompileModelTargetPreviewAsync(
		TargetPickers.ResolvedTarget target, string outputFolderRelative )
	{
		if ( target?.ModelFilePath is null )
			return true;

		if ( !PrepareModelTargetMesh( target, outputFolderRelative, out var error ) )
		{
			Log.Warning( $"[sbox-humanoid-mocap] target model preview: {error}" );
			return false;
		}

		var assetsPath = Project.Current?.GetAssetsPath();
		if ( assetsPath is null )
			return false;

		try
		{
			var meshRelative = target.Spec.MeshFilePath;
			var meshAbsolute = Path.GetFullPath( Path.Combine(
				assetsPath, meshRelative.Replace( '/', Path.DirectorySeparatorChar ) ) );
			var meshHash = Convert.ToHexString(
				System.Security.Cryptography.SHA256.HashData( File.ReadAllBytes( meshAbsolute ) ) )
				.Substring( 0, 8 ).ToLowerInvariant();
			var vmdlRelative = meshRelative.Substring( 0, meshRelative.LastIndexOf( '.' ) )
				+ $"_preview_bind_{meshHash}.vmdl";
			var vmdlAbsolute = Path.GetFullPath( Path.Combine(
				assetsPath, vmdlRelative.Replace( '/', Path.DirectorySeparatorChar ) ) );

			// A one-frame channel set keeps unweighted roots/helpers in the preview skeleton;
			// otherwise ModelDoc culls them and the preview no longer matches final output.
			var bindRelative = Path.ChangeExtension( vmdlRelative, ".dmx" );
			var bindAbsolute = Path.GetFullPath( Path.Combine(
				assetsPath, bindRelative.Replace( '/', Path.DirectorySeparatorChar ) ) );
			var bindFrame = target.Spec.Rig.Skeleton.Bones.Select( bone => bone.RestLocal ).ToArray();
			File.WriteAllText( bindAbsolute, HumanoidMocap.Formats.Dmx.DmxWriter.Write(
				target.Spec.Rig.Skeleton,
				new HumanoidMocap.Skeleton.Clip( "preview_bind", 30f, false,
					new List<HumanoidMocap.Maths.XForm[]> { bindFrame } ),
				new HumanoidMocap.Formats.Dmx.DmxWriteOptions
				{
					Name = "preview_bind",
					UpAxisY = target.Spec.UpAxis == TargetUpAxis.YUpCm,
				} ) );
			Try( () => AssetSystem.RegisterFile( bindAbsolute ) );

			var text = HumanoidMocap.Target.VmdlWriter.GenerateStandalone(
				"", new[] { new HumanoidMocap.Target.AnimEntry
				{
					Name = "preview_bind",
					SourceFilename = bindRelative.Replace( '\\', '/' ),
				} },
				target.Spec.VmdlScale, target.Spec.DefaultRootBone,
				meshFilePath: meshRelative, meshImportScale: target.Spec.MeshImportScale,
				materialRemaps: target.Spec.MaterialRemaps, meshImportNames: target.Spec.MeshImportNames );
			File.WriteAllText( vmdlAbsolute, text );
			// A compiled artifact from an OLDER library version has placeholder materials
			// baked in (pre-remap era) - never let it satisfy anything; the fresh compile
			// below replaces it.
			Try( () => { File.Delete( vmdlAbsolute + "_c" ); return true; } );
			var sourceWriteUtc = File.GetLastWriteTimeUtc( vmdlAbsolute );

			await SwitchToMainThread();
			var asset = AssetSystem.RegisterFile( vmdlAbsolute );
			if ( asset is null )
				return false;

			// The asset system often auto-compiles a freshly registered vmdl BEFORE we ask:
			// CompileAndWaitAsync's stale-file protection (result must be newer than its
			// own start) then treats the legitimate output as pre-existing and waits out
			// the whole timeout (observed with the user's 27 MB character - the compile had
			// finished in 2 s). A compiled file newer than OUR source write IS this run's.
			bool CompiledFresh()
			{
				try
				{
					var compiledPath = vmdlAbsolute + "_c";
					return File.Exists( compiledPath )
						&& File.GetLastWriteTimeUtc( compiledPath ) >= sourceWriteUtc;
				}
				catch
				{
					return false;
				}
			}

			await Task.Delay( InputSettleDelayMs );
			if ( !CompiledFresh() )
			{
				// Short triggering poll only: the result usually comes from the asset
				// system's own register-time compile, which CompileAndWaitAsync's
				// stale-file guard can never credit (it demands a file newer than its own
				// start; observed as a full-timeout wait while the compiled file already
				// existed). Watch for a FRESH file ourselves for the remainder.
				var triggered = await CompileAndWaitAsync( asset, timeoutSeconds: 20 );
				var stopwatch = Stopwatch.StartNew();
				while ( !triggered && !CompiledFresh()
					&& stopwatch.Elapsed.TotalSeconds < MeshCompileTimeoutSeconds )
				{
					await Task.Delay( 1000 );
				}
				if ( !triggered && !CompiledFresh() )
				{
					Log.Warning( $"[sbox-humanoid-mocap] target FBX preview vmdl did not compile: {asset.Path}" );
					return false;
				}
			}

			target.PreviewModelPath = asset.Path;

			// THE flawless-FBX-target keystone: once the engine has compiled the mesh,
			// its skeleton is the authority the sequences will play on - rebuild the rig
			// from it so rig and compiled bind can never disagree (per-exporter pivot/
			// scale quirks stretched fingers when the importer-derived rig drifted).
			TargetPickers.TryRebuildFromCompiledPreview( target, asset.Path );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] target FBX preview failed: {e.Message}" );
			return false;
		}
	}

	/// <summary>Compile-poll timeout for vmdls that embed a mesh source (the compiler
	/// imports the whole FBX - skin and skeleton - on top of the animation work).</summary>
	public const float MeshCompileTimeoutSeconds = 300f;

	/// <summary>Disk + asset-system outcome of one conversion run.</summary>
	public sealed class WriteResult
	{
		/// <summary>Absolute path of the written vmdl (standalone or augmented original).</summary>
		public string VmdlPath { get; set; }

		/// <summary>The registered vmdl asset; null when registration failed.</summary>
		public Asset VmdlAsset { get; set; }

		/// <summary>Whether the vmdl compiled to a .vmdl_c.</summary>
		public bool Compiled { get; set; }

		/// <summary>Absolute path of the compiled file when known.</summary>
		public string CompiledFile { get; set; }

		/// <summary>Number of DMX files written.</summary>
		public int DmxFilesWritten { get; set; }

		/// <summary>IO / registration / compile problems (conversion errors are reported
		/// separately on the batch result).</summary>
		public List<string> Errors { get; } = new();
	}

	/// <summary>Delay between the last output write and triggering the vmdl compile. The
	/// engine refuses to compile while an asset's input files keep changing ("Waited too
	/// long (1270ms) for quiet inputs ... abandoning recompile!"); waiting longer than that
	/// watcher window after our final write guarantees a quiet compile.</summary>
	const int InputSettleDelayMs = 2000;

	/// <summary>Settle delay used when the open project physically lives inside the s&amp;box
	/// installation (samples/...): the engine's own content watcher also watches those paths
	/// (the current-project exemption in <see cref="IsUnderEngineInstall"/> deliberately lets
	/// writes through there), so give the watcher extra room before compiling. Defense in
	/// depth - see the exemption remarks on <see cref="IsUnderEngineInstall"/>.</summary>
	const int DefensiveInputSettleDelayMs = 4000;

	/// <summary>
	/// Writes a batch result to disk and compiles it. DMX files go to
	/// <c>Assets/&lt;dmxFolderRelative&gt;/</c> (must match the
	/// <see cref="BatchOptions.DmxFolderRelative"/> the batch ran with, since the vmdl's
	/// AnimFile entries reference them by that assets-relative path). In standalone mode a
	/// new vmdl is written next to the DMX files; in augment mode (<paramref name="augmentVmdlPath"/>
	/// non-null and the batch produced <see cref="RetargetBatchResult.AugmentedVmdl"/>) the
	/// ORIGINAL vmdl is overwritten non-destructively: a <c>.vmdl.bak</c> backup is written
	/// next to it first (design §9).
	/// Sequencing: ALL files are written first, then registered once, then - after
	/// <see cref="InputSettleDelayMs"/> so the engine's input watcher goes quiet - the vmdl
	/// is compiled once (with a single retry if the engine still abandoned the recompile).
	/// Safe to call from any thread; engine calls are marshalled to the main thread.
	/// </summary>
	public static async Task<WriteResult> WriteAndCompileAsync(
		RetargetBatchResult batch, string dmxFolderRelative, string augmentVmdlPath = null,
		string standaloneVmdlName = "retargeted_animations", float compileTimeoutSeconds = 120f )
	{
		var result = new WriteResult();
		var assetsPath = Project.Current?.GetAssetsPath();
		if ( assetsPath is null )
		{
			result.Errors.Add( "No project is open." );
			return result;
		}

		// ---- 0. never write into the s&box installation ---------------------------------
		// The engine owns and watches that content; compiling our writes there fought the
		// watcher ("quiet inputs" abandons) and ended in a native editor crash.
		if ( augmentVmdlPath is not null && IsUnderEngineInstall( augmentVmdlPath ) )
		{
			result.Errors.Add(
				$"Cannot modify models inside the s&box installation ({augmentVmdlPath}). "
				+ "Copy the model into your project's Assets folder and pick that copy instead." );
			return result;
		}

		// POLICY (replaces a former hard reject of install-resident assetsPath, which the
		// current-project exemption in IsUnderEngineInstall had made unreachable dead code):
		// the open project's paths are always allowed - users genuinely work in the shipped
		// sample projects under <sbox>/samples/ - but those paths are also watched by the
		// engine's own content watcher, the original native-crash class. The crash was fixed
		// by strict main-thread write→register→settle→compile sequencing; as defense in
		// depth, install-resident projects get a LONGER settle and one EXTRA verified
		// (timestamp-checked) compile retry instead of being blocked.
		var installResident = IsUnderEngineInstallIgnoringProject( assetsPath )
			|| (augmentVmdlPath is not null && IsUnderEngineInstallIgnoringProject( augmentVmdlPath ));
		var settleDelayMs = installResident ? DefensiveInputSettleDelayMs : InputSettleDelayMs;

		var successful = batch.Clips.Where( c => c.Success ).ToList();
		if ( successful.Count == 0 )
		{
			result.Errors.Add( "No clip converted successfully - nothing written." );
			return result;
		}

		// Augment requested but the batch produced no augmented vmdl (parse failure, name
		// collision, ...): FAIL the whole operation before anything touches disk. Silently
		// falling back to a standalone vmdl would look green while ignoring the user's
		// explicit "add to existing vmdl" choice.
		if ( augmentVmdlPath is not null && batch.AugmentedVmdl is null )
		{
			result.Errors.Add(
				$"Augmenting {Path.GetFileName( augmentVmdlPath )} failed - nothing was written "
				+ "(standalone fallback is disabled when augmenting was requested)." );
			result.Errors.AddRange( batch.Errors.Where(
				e => e.Contains( "augment", StringComparison.OrdinalIgnoreCase ) ) );
			return result;
		}

		// ---- 1. write ALL files first (DMX, then .bak, then the vmdl LAST) -------------
		// Nothing may touch the asset system until every input file is final - the engine
		// abandons recompiles whose inputs keep changing.
		var dmxPaths = new List<string>();
		try
		{
			var dmxDir = Path.Combine( assetsPath, dmxFolderRelative.Replace( '/', Path.DirectorySeparatorChar ) );
			Directory.CreateDirectory( dmxDir );
			foreach ( var clip in successful )
			{
				var dmxPath = Path.Combine( dmxDir, clip.DmxFileName );
				File.WriteAllText( dmxPath, clip.DmxContent );
				dmxPaths.Add( dmxPath );
				result.DmxFilesWritten++;
			}

			if ( augmentVmdlPath is not null )
			{
				File.Copy( augmentVmdlPath, augmentVmdlPath + ".bak", overwrite: true );
				File.WriteAllText( augmentVmdlPath, batch.AugmentedVmdl );
				result.VmdlPath = augmentVmdlPath;
			}
			else
			{
				result.VmdlPath = Path.Combine( dmxDir, standaloneVmdlName + ".vmdl" );
				File.WriteAllText( result.VmdlPath, batch.StandaloneVmdl );
			}
		}
		catch ( Exception e )
		{
			result.Errors.Add( $"Writing outputs failed: {e.Message}" );
			return result;
		}

		// ---- 2. register each new file once (engine state - main thread only) ----------
		await SwitchToMainThread();

		foreach ( var dmxPath in dmxPaths )
			Try( () => AssetSystem.RegisterFile( dmxPath ) );

		result.VmdlAsset = AssetSystem.RegisterFile( result.VmdlPath );
		if ( result.VmdlAsset is null )
		{
			result.Errors.Add( $"Asset registration failed for {result.VmdlPath}." );
			return result;
		}

		// ---- 3. settle, then compile ONCE (retry on abandoned recompile: one normally,
		// two for install-resident projects - see the policy comment above) ---------------
		await Task.Delay( settleDelayMs );

		var vmdlFileName = Path.GetFileName( result.VmdlPath );
		var maxAbandonRetries = installResident ? 2 : 1;
		var logOffset = SboxLogLength();
		result.Compiled = await CompileAndWaitAsync( result.VmdlAsset,
			timeoutSeconds: compileTimeoutSeconds, logOffset: logOffset,
			watchFileName: vmdlFileName );

		for ( var retry = 0; retry < maxAbandonRetries && !result.Compiled
			&& LogSliceShowsAbandonedRecompile( logOffset, vmdlFileName ); retry++ )
		{
			Log.Warning( $"[sbox-humanoid-mocap] engine abandoned the recompile of {vmdlFileName} "
				+ $"(inputs not quiet) - retrying ({retry + 1}/{maxAbandonRetries}) after a settle delay" );
			await Task.Delay( settleDelayMs );
			logOffset = SboxLogLength();
			result.Compiled = await CompileAndWaitAsync( result.VmdlAsset,
				timeoutSeconds: compileTimeoutSeconds, logOffset: logOffset,
				watchFileName: vmdlFileName );
		}

		await SwitchToMainThread();
		result.CompiledFile = Try( () => result.VmdlAsset.GetCompiledFile( true ) );
		if ( !result.Compiled )
		{
			var detail = TryReadCompileErrors( logOffset,
				successful.Select( c => c.DmxFileName )
					.Append( vmdlFileName ).ToList() );
			result.Errors.Add( detail is null
				? $"vmdl did not compile: {result.VmdlAsset.Path} (see console for resourcecompiler output)."
				: $"vmdl did not compile: {result.VmdlAsset.Path}\n{detail}" );
		}

		return result;
	}

	// ---- compile-error capture --------------------------------------------------------
	// The asset system exposes no Asset.LastCompileError-style API (verified against
	// Sandbox.Tools.xml), but resourcecompiler output lands in <sbox>/logs/sbox-dev.log -
	// the same per-run log slice dev/editor-rig/run_ui_smoke.ps1 scrapes. Reading the slice
	// written since our Compile() call gives the actual error text for the UI.

	static string SboxLogFile()
	{
		try
		{
			// The editor runs with the s&box root as working directory; the exe also lives
			// directly under it (sbox-dev.exe), so probe both.
			var candidates = new List<string>
			{
				Path.Combine( Environment.CurrentDirectory, "logs", "sbox-dev.log" ),
			};
			var exeDir = Path.GetDirectoryName( Environment.ProcessPath );
			if ( exeDir is not null )
				candidates.Add( Path.Combine( exeDir, "logs", "sbox-dev.log" ) );
			return candidates.FirstOrDefault( File.Exists );
		}
		catch
		{
			return null;
		}
	}

	internal static long SboxLogLength()
	{
		try
		{
			var file = SboxLogFile();
			return file is null ? -1 : new FileInfo( file ).Length;
		}
		catch
		{
			return -1;
		}
	}

	/// <summary>Reads the raw log slice written since <paramref name="fromOffset"/>, or null
	/// when the log is unreachable.</summary>
	internal static string ReadLogSlice( long fromOffset )
	{
		try
		{
			var file = SboxLogFile();
			if ( file is null || fromOffset < 0 )
				return null;

			using var fs = File.Open( file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite );
			if ( fs.Length < fromOffset )
				fromOffset = 0; // log was truncated/recreated
			fs.Seek( fromOffset, SeekOrigin.Begin );
			using var reader = new StreamReader( fs );
			return reader.ReadToEnd();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>True when the per-run log slice since <paramref name="fromOffset"/> shows
	/// the engine abandoning the recompile of <paramref name="vmdlFileName"/> because its
	/// inputs would not go quiet.</summary>
	internal static bool LogSliceShowsAbandonedRecompile( long fromOffset, string vmdlFileName )
	{
		var slice = ReadLogSlice( fromOffset );
		if ( slice is null )
			return false;

		return slice.Split( '\n' ).Any( l =>
			l.Contains( "abandoning recompile", StringComparison.OrdinalIgnoreCase )
			&& l.Contains( vmdlFileName, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>Reads the log slice written since <paramref name="fromOffset"/> and returns
	/// the lines that look like compiler errors for our files, or null when none found.</summary>
	static string TryReadCompileErrors( long fromOffset, IReadOnlyList<string> fileNames )
	{
		try
		{
			var raw = ReadLogSlice( fromOffset );
			if ( raw is null )
				return null;
			var slice = raw.Split( '\n' );

			var interesting = slice
				.Select( l => l.TrimEnd( '\r' ) )
				.Where( l => l.Length > 0
					&& (l.Contains( "error", StringComparison.OrdinalIgnoreCase )
						|| l.Contains( "failed", StringComparison.OrdinalIgnoreCase ))
					&& (fileNames.Any( f => l.Contains( f, StringComparison.OrdinalIgnoreCase ) )
						|| l.Contains( "resourcecompiler", StringComparison.OrdinalIgnoreCase )
						|| l.Contains( "ModelDoc", StringComparison.OrdinalIgnoreCase )) )
				.ToList();

			if ( interesting.Count == 0 )
				return null;
			return string.Join( "\n", interesting.TakeLast( 12 ) );
		}
		catch
		{
			return null; // error capture must never take the pipeline down
		}
	}

	/// <summary>Kicks a full compile and polls until THIS compile demonstrably produced a
	/// compiled file, or the asset reports failure. The most honest signal is the .vmdl_c
	/// on disk - but its mere EXISTENCE proves nothing: augment targets always come with a
	/// pre-existing .vmdl_c, which would satisfy a naive existence poll and report a stale
	/// (possibly abandoned) compile as success. The pre-existing file's LastWriteTimeUtc is
	/// therefore snapshotted BEFORE the compile is triggered, and success requires the
	/// compiled file to be NEWER than that snapshot (or to newly exist). Safe to call from
	/// any thread: the compile trigger and every <see cref="Asset"/> state query are
	/// marshalled to the editor main thread (<see cref="SwitchToMainThread"/>) because
	/// <c>Task.Delay</c> continuations may resume on a pool thread, and engine objects must
	/// never be touched there.</summary>
	/// <param name="asset">The vmdl asset to compile.</param>
	/// <param name="timeoutSeconds">Poll timeout.</param>
	/// <param name="logOffset">Optional <see cref="SboxLogLength"/> snapshot taken before
	/// the compile run: with <paramref name="watchFileName"/>, the poll exits early when the
	/// engine logs an abandoned recompile for that file (so the caller's retry path runs
	/// without waiting out the full timeout).</param>
	/// <param name="watchFileName">File name to watch for in abandoned-recompile log lines.</param>
	public static async Task<bool> CompileAndWaitAsync(
		Asset asset, float timeoutSeconds = 120, long logOffset = -1, string watchFileName = null )
	{
		await SwitchToMainThread();

		// Resolves the compiled-file path: GetCompiledFile when the asset system can answer
		// (it may resolve to null until a compile has run), else the engine's
		// <source>_c sibling convention (file.vmdl -> file.vmdl_c) so a PRE-EXISTING
		// compiled file is still seen and snapshotted before the compile is triggered.
		string ResolveCompiledPath()
		{
			var path = Try( () => asset.GetCompiledFile( true ) );
			if ( path is not null )
				return path;
			var source = Try( () => asset.AbsolutePath );
			return string.IsNullOrEmpty( source ) ? null : source + "_c";
		}

		// Snapshot the pre-existing compiled file BEFORE triggering the compile.
		var compiledAbs = ResolveCompiledPath();
		DateTime? preexistingWriteUtc = null;
		try
		{
			if ( compiledAbs is not null && File.Exists( compiledAbs ) )
				preexistingWriteUtc = File.GetLastWriteTimeUtc( compiledAbs );
		}
		catch
		{
			// unreadable timestamp: treat as no pre-existing file (existence will satisfy)
		}

		try
		{
			asset.Compile( full: true );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] asset.Compile threw: {e.Message}" );
		}

		bool CompiledFileFresh()
		{
			try
			{
				if ( compiledAbs is null || !File.Exists( compiledAbs ) )
					return false;
				return preexistingWriteUtc is not { } stamp
					|| File.GetLastWriteTimeUtc( compiledAbs ) > stamp;
			}
			catch
			{
				return false;
			}
		}

		var sw = Stopwatch.StartNew();
		while ( sw.Elapsed.TotalSeconds < timeoutSeconds )
		{
			await SwitchToMainThread();
			if ( Try( () => asset.IsCompileFailed ) )
				return false;
			// GetCompiledFile may only become authoritative once the compile has run -
			// prefer it over the convention fallback as soon as it resolves. The freshness
			// snapshot stays valid either way (both spellings name the same sibling file).
			compiledAbs = Try( () => asset.GetCompiledFile( true ) ) ?? compiledAbs;
			if ( CompiledFileFresh() )
				return true;
			// The asset's own compiled flags are trusted only when no stale .vmdl_c could
			// be feeding them (no pre-existing file and no resolvable compiled path).
			if ( compiledAbs is null && preexistingWriteUtc is null
				&& Try( () => asset.IsCompiled && asset.HasCompiledFile ) )
				return true;
			// Abandoned recompile ("quiet inputs"): the file will never freshen this run -
			// bail out so the caller's settle-and-retry path is actually reachable.
			if ( logOffset >= 0 && watchFileName is not null
				&& LogSliceShowsAbandonedRecompile( logOffset, watchFileName ) )
				return false;

			await Task.Delay( 250 );
		}

		return CompiledFileFresh();
	}

	static T Try<T>( Func<T> getter )
	{
		try { return getter(); }
		catch { return default; }
	}
}
