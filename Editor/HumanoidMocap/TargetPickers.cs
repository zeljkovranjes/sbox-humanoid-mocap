#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Numerics;
using Editor;
using HumanoidMocap.Formats.Fbx;
using HumanoidMocap.Formats.Gltf;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;
using Sandbox;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Editor;

/// <summary>
/// Builds <see cref="RetargetTargetSpec"/>s for the window's target picker
/// (Task 9.5.2): the shipped s&amp;box default, a custom compiled model/vmdl asset, or a
/// custom FBX, GLB, or glTF file. Custom targets run the same humanoid detection as sources and are
/// rejected with a clear message below the detection threshold.
/// </summary>
/// <remarks>
/// <b>Units.</b> The pipeline is unit-agnostic as long as one target is self-consistent:
/// the solver scales pelvis translation by the target/source hip-height RATIO (so cm
/// sources drive inch targets correctly) and all other channels are rotations; DMX
/// positions are emitted in whatever units the target skeleton uses.
/// <list type="bullet">
/// <item>Compiled model targets: <c>Model.Bones</c> bind pose is in engine units (inches)
/// → <c>VmdlScale = 1.0</c>, no conversion anywhere.</item>
/// <item>Source-model targets: imported in source units normalized to cm →
/// <c>VmdlScale = 0.3937</c> (cm → inch at compile time),
/// matching the s&amp;box citizen pipeline.</item>
/// </list>
/// </remarks>
public static class TargetPickers
{
	/// <summary>A resolved conversion target plus what the preview needs to render it.</summary>
	public sealed class ResolvedTarget
	{
		/// <summary>The spec handed to <see cref="Retargeter.ConvertBatch"/>.</summary>
		public RetargetTargetSpec Spec { get; set; }

		/// <summary>Short description for the window status line / target chip.</summary>
		public string Description { get; set; }

		/// <summary>Asset path of a compiled model whose bone names match the target rig -
		/// used by the skinned preview. Null before a source-model target's preview compiles:
		/// the preview then shows a "no preview model" notice.</summary>
		public string PreviewModelPath { get; set; }

		/// <summary>Multiplier taking target-skeleton positions to engine units for the
		/// preview (0.3937 for cm rigs, 1.0 for engine-unit rigs).</summary>
		public float PreviewPositionScale { get; set; } = 1.0f;

		/// <summary>Absolute path of the picked target model file (FBX/glTF/GLB; null
		/// otherwise). At convert time the editor copies it into the output folder and sets
		/// <see cref="RetargetTargetSpec.MeshFilePath"/> so the standalone vmdl embeds the
		/// mesh — without it the vmdl compiles into an empty model that plays nothing.</summary>
		public string ModelFilePath { get; set; }

		/// <summary>The importer's source-unit→cm factor for
		/// <see cref="ModelFilePath"/> (resourcecompiler reads raw values, so this
		/// becomes the RenderMeshFile <c>import_scale</c>).</summary>
		public float ModelUnitScaleCm { get; set; } = 1.0f;

		/// <summary>The skeleton imported directly from <see cref="ModelFilePath"/>.
		/// Kept when <see cref="Spec"/> is rebuilt from the compiled preview: glTF mesh
		/// conversion must continue using the source bind, not feed the compiler-converted
		/// bind back through the model-DMX writer a second time.</summary>
		internal SkeletonModel SourceModelSkeleton { get; set; }

		// Compatibility aliases for editor gate consumers written before GLB/glTF targets.
		public string FbxAbsolutePath { get => ModelFilePath; set => ModelFilePath = value; }
		public float FbxUnitScaleCm { get => ModelUnitScaleCm; set => ModelUnitScaleCm = value; }

		/// <summary>Non-fatal caveat about the pick, surfaced on the window status strip
		/// (e.g. a skeleton-only target FBX whose standalone vmdl will have no visible
		/// model). Null when there is nothing to warn about.</summary>
		public string Warning { get; set; }

		/// <summary>Sequence names of the target model's own embedded animation takes
		/// (index-aligned with the imported clips). Converted alongside every batch as
		/// exact identity retargets so an animated FBX keeps its animations - AnimFile
		/// nodes referencing the FBX directly cannot rescale translations (no import
		/// scale on the node), which broke inch-authored files. Null/empty = none.</summary>
		public IReadOnlyList<string> EmbeddedTakeNames { get; set; }
	}

	/// <summary>
	/// A picked custom target whose skeleton could not be auto-recognized: everything the
	/// window's manual-mapping fallback needs to let the user assign the roles by hand and
	/// build the target anyway (any humanoid-LIKE model — a paw, one finger, a missing
	/// hand — is a valid target once its core bones are pointed out).
	/// </summary>
	public sealed class RejectedTarget
	{
		/// <summary>The candidate target skeleton (model bones / imported FBX).</summary>
		public SkeletonModel Skeleton { get; set; }

		/// <summary>The cascade's best-effort map — prefills the manual mapping editor.</summary>
		public MappingResult BestEffortMap { get; set; }

		/// <summary>Set for compiled-model picks (mutually exclusive with <see cref="FbxPath"/>).</summary>
		public Asset ModelAsset { get; set; }

		/// <summary>Set for source-model-file picks (absolute path).</summary>
		public string ModelFilePath { get; set; }

		/// <summary>Importer's source-unit→cm factor (source-model-file picks only).</summary>
		public float ModelUnitScaleCm { get; set; } = 1.0f;

		/// <summary>The source up-axis index (1 = Y, 2 = Z).</summary>
		public int ModelUpAxis { get; set; } = 1;

		public string FbxPath { get => ModelFilePath; set => ModelFilePath = value; }
		public float FbxUnitScaleCm { get => ModelUnitScaleCm; set => ModelUnitScaleCm = value; }
		public int FbxUpAxis { get => ModelUpAxis; set => ModelUpAxis = value; }

		/// <summary>Short name for dialog titles.</summary>
		public string DisplayName { get; set; }
	}

	/// <summary>
	/// Builds the target from a user-authored mapping over a previously rejected pick
	/// (the manual-mapping fallback's confirm path). Returns null with
	/// <paramref name="error"/> set when the mapping is structurally unusable (e.g.
	/// duplicate bone assignments).
	/// </summary>
	public static ResolvedTarget FromManualMapping(
		RejectedTarget rejected, MappingResult mapping, out string error )
	{
		error = null;
		try
		{
			return rejected.ModelAsset is not null
				? BuildModelTarget( rejected.ModelAsset, rejected.Skeleton, mapping )
				: BuildFileTarget( rejected.ModelFilePath, rejected.ModelUnitScaleCm, rejected.ModelUpAxis,
					rejected.Skeleton, mapping );
		}
		catch ( Exception e )
		{
			error = $"Could not build the target from the mapping: {e.Message}";
			return null;
		}
	}

	/// <summary>The shipped s&amp;box human target. Throws when the rig JSON is missing.</summary>
	public static ResolvedTarget SboxDefault()
	{
		var spec = EditorPipeline.LoadSboxDefaultTarget();
		return new ResolvedTarget
		{
			Spec = spec,
			Description = "s&box Human (default)",
			PreviewModelPath = RetargetTargetSpec.SboxHumanMalePath,
			PreviewPositionScale = RetargetTargetSpec.SboxSourceScale,
		};
	}

	/// <summary>The classic (4-finger) s&amp;box citizen target. Throws when the rig JSON is
	/// missing. Pinky roles stay unassigned on this rig (it has no pinky bones).</summary>
	public static ResolvedTarget SboxCitizen()
	{
		var spec = EditorPipeline.LoadSboxCitizenTarget();
		return new ResolvedTarget
		{
			Spec = spec,
			Description = "s&box Citizen (classic)",
			PreviewModelPath = RetargetTargetSpec.SboxCitizenPath,
			PreviewPositionScale = RetargetTargetSpec.SboxSourceScale,
		};
	}

	/// <summary>
	/// Builds a target from a compiled model asset (vmdl): skeleton from
	/// <c>Model.Bones</c> (engine units → VmdlScale 1.0), roles from preset detection /
	/// auto-mapping, bone classes from <see cref="BoneClassRules"/> name patterns.
	/// Returns null with <paramref name="error"/> set when the model fails to load or the
	/// armature is not recognized as humanoid.
	/// </summary>
	public static ResolvedTarget FromModelAsset( Asset asset, out string error )
		=> FromModelAsset( asset, out error, out _ );

	/// <summary>As <see cref="FromModelAsset(Asset, out string)"/>; additionally reports a
	/// <see cref="RejectedTarget"/> when the skeleton loaded fine but was not recognized —
	/// the window then offers manual mapping instead of a dead end.</summary>
	public static ResolvedTarget FromModelAsset( Asset asset, out string error, out RejectedTarget rejected )
	{
		error = null;
		rejected = null;
		var model = Model.Load( asset.Path );
		if ( model is null || model.IsError )
		{
			error = $"Could not load model '{asset.Path}'.";
			return null;
		}

		SkeletonModel skeleton;
		try
		{
			skeleton = SkeletonFromModel( model );
		}
		catch ( Exception e )
		{
			error = $"Could not read the model's skeleton: {e.Message}";
			return null;
		}

		var map = DetectHumanoid( skeleton, out error, out var bestEffort );
		if ( map is null )
		{
			rejected = new RejectedTarget
			{
				Skeleton = skeleton,
				BestEffortMap = bestEffort,
				ModelAsset = asset,
				DisplayName = asset.Name,
			};
			return null;
		}

		return BuildModelTarget( asset, skeleton, map );
	}

	/// <summary>The first-person viewmodel arms s&amp;box games use, as cloud packages.</summary>
	public const string HumanArmsPackage = "facepunch.v_first_person_arms_human";
	public const string CitizenArmsPackage = "facepunch.v_first_person_arms_citizen";
	public const string HumanArmsPath = "models/first_person/v_first_person_arms_human.vmdl";
	public const string CitizenArmsPath = "models/first_person/v_first_person_arms_citizen.vmdl";

	/// <summary>The human (5-finger) or citizen (4-finger) first-person arms, mounted from their package
	/// when needed. Arms-only rigs map as partial targets: clavicles, arms, hands and fingers.</summary>
	public static async Task<ResolvedTarget> FirstPersonArmsAsync( bool citizen )
	{
		var path = citizen ? CitizenArmsPath : HumanArmsPath;
		var model = Model.Load( path );
		if ( model is null || model.IsError || model.BoneCount == 0 )
		{
			await Package.MountAsync( citizen ? CitizenArmsPackage : HumanArmsPackage, false );
			await EditorPipeline.SwitchToMainThread();
		}
		var target = FromModelPath( path, citizen ? "s&box Citizen arms (first person)" : "s&box Human arms (first person)", out var error );
		return target ?? throw new InvalidOperationException( error ?? "Could not load the first-person arms." );
	}

	/// <summary>Builds a target from a compiled model loaded by path, such as a mounted cloud model that
	/// is not a project asset.</summary>
	public static ResolvedTarget FromModelPath( string path, string description, out string error )
	{
		error = null;
		var model = Model.Load( path );
		if ( model is null || model.IsError || model.BoneCount == 0 )
		{
			error = $"Could not load model '{path}'.";
			return null;
		}
		var skeleton = SkeletonFromModel( model );
		var map = DetectHumanoid( skeleton, out error, out _ );
		if ( map is null ) return null;
		var target = BuildModelTarget( path, description, skeleton, map );
		return target;
	}

	static ResolvedTarget BuildModelTarget( Asset asset, SkeletonModel skeleton, MappingResult map )
		=> BuildModelTarget( asset.Path, $"Custom model: {asset.Name}", skeleton, map );

	static ResolvedTarget BuildModelTarget( string path, string description, SkeletonModel skeleton, MappingResult map )
	{
		var rig = TargetRig.FromSkeleton( skeleton, map );
		return new ResolvedTarget
		{
			Spec = new RetargetTargetSpec
			{
				Rig = rig,
				VmdlScale = 1.0f,                       // engine units already
				BaseModelPath = path,
				DefaultRootBone = RootBoneName( skeleton, map ),
				UpAxis = TargetUpAxis.ZUpEngine,        // Model.Bones bind pose is engine space
				DlWeights = DlAssets.TryLoadWeights(),
			},
			Description = description,
			PreviewModelPath = path,
			PreviewPositionScale = 1.0f,
		};
	}

	/// <summary>
	/// Compatibility entry point for building a target from an FBX file.
	/// </summary>
	public static ResolvedTarget FromFbxFile( string filePath, out string error )
		=> FromModelFile( filePath, out error, out _ );

	/// <summary>Builds a custom target from an FBX, GLB, or glTF source model.</summary>
	public static ResolvedTarget FromModelFile( string filePath, out string error )
		=> FromModelFile( filePath, out error, out _ );

	/// <summary>As <see cref="FromFbxFile(string, out string)"/>; additionally reports a
	/// <see cref="RejectedTarget"/> when the file imported fine but the skeleton was not
	/// recognized — the window then offers manual mapping instead of a dead end.</summary>
	public static ResolvedTarget FromFbxFile( string filePath, out string error, out RejectedTarget rejected )
		=> FromModelFile( filePath, out error, out rejected );

	/// <summary>As <see cref="FromModelFile(string, out string)"/>; also returns the
	/// best-effort mapping state when the model needs manual role assignment.</summary>
	public static ResolvedTarget FromModelFile(
		string filePath, out string error, out RejectedTarget rejected )
	{
		error = null;
		rejected = null;
		SkeletonModel skeleton;
		float unitScaleCm;
		int upAxis;
		bool hasMesh;
		try
		{
			var bytes = File.ReadAllBytes( filePath );
			var extension = Path.GetExtension( filePath ).ToLowerInvariant();
			var imported = extension switch
			{
				".fbx" => FbxImporter.Import( bytes ),
				".glb" => GltfImporter.Import( bytes, new GltfImportOptions { UseSkinBindPose = true } ),
				".gltf" => GltfImporter.Import( bytes, new GltfImportOptions
				{
					UseSkinBindPose = true,
					ExternalBufferResolver = uri => ReadGltfDependency( filePath, uri ),
				} ),
				_ => throw new FormatException( "Expected an .fbx, .glb, or .gltf model." ),
			};
			skeleton = imported.Skeleton;
			unitScaleCm = imported.UnitScaleCm;
			upAxis = imported.UpAxis;
			// Both tokens live uncompressed in their respective container metadata.
			hasMesh = extension == ".fbx"
				? ContainsToken( bytes, "Geometry" )
				: ContainsToken( bytes, "\"meshes\"" );
		}
		catch ( Exception e )
		{
			error = $"Could not import '{Path.GetFileName( filePath )}': {e.Message}";
			return null;
		}

		// The engine sanitizes bone names when importing the mesh (observed: 3ds Max
		// Biped's "Bip01 R Finger22" compiles to "Bip01_R_Finger22"), and DMX animation
		// channels bind BY NAME - a rig built on the raw names produces sequences that
		// play ZERO motion on the compiled model (the user's "animation doesn't play").
		// Build the whole target on the sanitized names; preset detection is separator-
		// insensitive, so recognition is unaffected.
		skeleton = SanitizeBoneNamesLikeEngine( skeleton );

		var map = DetectHumanoid( skeleton, out error, out var bestEffort );
		if ( map is null )
		{
			rejected = new RejectedTarget
			{
				Skeleton = skeleton,
				BestEffortMap = bestEffort,
				ModelFilePath = Path.GetFullPath( filePath ),
				ModelUnitScaleCm = unitScaleCm,
				ModelUpAxis = upAxis,
				DisplayName = Path.GetFileName( filePath ),
			};
			return null;
		}

		var resolved = BuildFileTarget( Path.GetFullPath( filePath ), unitScaleCm, upAxis, skeleton, map );
		if ( !hasMesh )
		{
			resolved.Warning = $"'{Path.GetFileName( filePath )}' contains no mesh (skeleton-only "
				+ "export): a NEW vmdl generated for it will have nothing visible to play. Add the "
				+ "animations to an existing vmdl of this model instead, or re-export it with skin.";
		}
		return resolved;
	}

	internal static byte[] ReadGltfDependency( string gltfPath, string uri )
	{
		var relative = Uri.UnescapeDataString( uri.Split( '?', '#' )[0] )
			.Replace( '/', Path.DirectorySeparatorChar );
		if ( Path.IsPathRooted( relative ) )
			throw new FormatException( $"glTF dependency must be relative: '{uri}'." );
		var directory = Path.GetFullPath( Path.GetDirectoryName( gltfPath ) );
		var path = Path.GetFullPath( Path.Combine( directory, relative ) );
		if ( !path.StartsWith( directory.TrimEnd( Path.DirectorySeparatorChar )
			+ Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			throw new FormatException( $"glTF dependency escapes the model folder: '{uri}'." );
		return File.ReadAllBytes( path );
	}

	/// <summary>
	/// Rebuilds an FBX target's rig from its COMPILED preview model — the engine's own
	/// import is the single source of truth the sequences will play on, so deriving the
	/// rig from it makes rig ≡ compiled bind BY CONSTRUCTION for any FBX, killing the
	/// per-exporter drift our importer can never fully chase (measured: 6 finger bones up
	/// to 6in/8° off on an Auto-Rig Pro export — stretched fingers — while a Biped export
	/// matched exactly). The engine skeleton (Z-up inches) is re-expressed in the vmdl's
	/// source space (Y-up cm) so the DMX and the ScaleAndMirror-carrying vmdl stay unit-
	/// and axis-consistent with the embedded mesh. False = keep the importer-derived rig.
	/// </summary>
	internal static bool TryRebuildFromCompiledPreview( ResolvedTarget target, string modelPath )
	{
		try
		{
			var model = Model.Load( modelPath );
			if ( model is null || model.IsError || model.BoneCount == 0 )
				return false;

			var engine = SkeletonFromModel( model );

			// Engine (Z-up, inches) → vmdl source space (Y-up, cm): uniform inverse scale
			// on every local position; the inverse basis rotation folds into the roots.
			var definitions = new List<HumanoidMocap.Skeleton.BoneDefinition>( engine.Count );
			foreach ( var bone in engine.Bones )
			{
				var local = CompiledRigSourceSpace.FromEngineLocal(
					bone.RestLocal, bone.ParentIndex < 0, target.Spec.UpAxis );
				definitions.Add( new HumanoidMocap.Skeleton.BoneDefinition(
					bone.Name, bone.ParentIndex < 0 ? null : engine[bone.ParentIndex].Name,
					local ) );
			}
			var skeleton = SkeletonModel.Create( definitions );

			var map = DetectHumanoid( skeleton, out var error, out _ );
			if ( map is null )
			{
				Log.Warning( $"[sbox-humanoid-mocap] compiled-preview rig rebuild skipped: {error}" );
				return false;
			}

			target.Spec.Rig = TargetRig.FromSkeleton( skeleton, map );
			target.Spec.DefaultRootBone = RootBoneName( skeleton, map );
			Log.Info( "[sbox-humanoid-mocap] target rig rebuilt from the compiled preview model "
				+ $"({skeleton.Count} bones) - rig now matches the engine bind exactly" );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] compiled-preview rig rebuild failed: {e.Message}" );
			return false;
		}
	}

	/// <summary>Resourcecompiler's bone-name sanitization (non-alphanumeric → underscore),
	/// applied to a candidate TARGET skeleton so the generated animation channels match the
	/// compiled model's bones exactly. Kept as-is when nothing changes or when sanitizing
	/// would collide two names (e.g. "a b" vs "a_b" - better recognizable than broken).</summary>
	static SkeletonModel SanitizeBoneNamesLikeEngine( SkeletonModel skeleton )
	{
		var changed = false;
		var definitions = new List<HumanoidMocap.Skeleton.BoneDefinition>( skeleton.Count );
		foreach ( var bone in skeleton.Bones )
		{
			var safe = SanitizeBoneName( bone.Name );
			changed |= safe != bone.Name;
			definitions.Add( new HumanoidMocap.Skeleton.BoneDefinition(
				safe,
				bone.ParentIndex >= 0 ? SanitizeBoneName( skeleton[bone.ParentIndex].Name ) : null,
				bone.RestLocal ) );
		}

		if ( !changed )
			return skeleton;

		try
		{
			return SkeletonModel.Create( definitions );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] could not sanitize target bone names ({e.Message}) - keeping the originals" );
			return skeleton;
		}
	}

	static string SanitizeBoneName( string name )
	{
		var builder = new System.Text.StringBuilder( name.Length );
		foreach ( var c in name )
			builder.Append( char.IsLetterOrDigit( c ) || c == '_' ? c : '_' );
		return builder.ToString();
	}

	/// <summary>Byte-level token scan (works for binary and ASCII FBX alike).</summary>
	static bool ContainsToken( byte[] data, string token )
	{
		var needle = System.Text.Encoding.ASCII.GetBytes( token );
		for ( var i = 0; i <= data.Length - needle.Length; i++ )
		{
			var match = true;
			for ( var j = 0; j < needle.Length; j++ )
			{
				if ( data[i + j] != needle[j] ) { match = false; break; }
			}
			if ( match )
				return true;
		}
		return false;
	}

	static ResolvedTarget BuildFileTarget(
		string filePath, float unitScaleCm, int upAxis, SkeletonModel skeleton, MappingResult map )
	{
		var rig = TargetRig.FromSkeleton( skeleton, map );
		return new ResolvedTarget
		{
			Spec = new RetargetTargetSpec
			{
				Rig = rig,
				VmdlScale = RetargetTargetSpec.SboxSourceScale, // cm-authored skeleton
				BaseModelPath = "",
				DefaultRootBone = RootBoneName( skeleton, map ),
				// UE / 3ds Max FBX exports are Z-up: the DMX must declare Z-up so the
				// compiler applies no rotation (the embedded mesh is in the same space) -
				// declaring Y-up would compile the animation lying on its back.
				UpAxis = upAxis == 2 ? TargetUpAxis.ZUpCm : TargetUpAxis.YUpCm,
				DlWeights = DlAssets.TryLoadWeights(),
			},
			Description = $"Custom {Path.GetExtension( filePath ).TrimStart( '.' ).ToUpperInvariant()}: {Path.GetFileName( filePath )}",
			PreviewModelPath = null,
			PreviewPositionScale = RetargetTargetSpec.SboxSourceScale,
			ModelFilePath = filePath,
			ModelUnitScaleCm = unitScaleCm,
			SourceModelSkeleton = skeleton,
		};
	}

	/// <summary>
	/// The facade's single mapping cascade (presets → saved user presets → auto/topology),
	/// with this picker's own acceptance rule on top. A below-threshold map used to be
	/// rejected outright, which made every odd-but-humanoid model — a paw instead of
	/// fingers, one finger, a missing hand — unusable as a target (and, before the sbox
	/// preset existed, even the citizen skeleton itself scored 5%). The solver skips
	/// unmapped roles cleanly (the classic citizen ships without pinky roles), so a
	/// best-effort map is accepted when it covers the body structural minimum or usable
	/// hand geometry. Hand capture can drive an arms/hands-only rig without a body.
	/// </summary>
	static MappingResult DetectHumanoid(
		SkeletonModel skeleton, out string error, out MappingResult bestEffort )
	{
		error = null;
		var assetsPath = Project.Current?.GetAssetsPath();
		var (map, report) = Retargeter.ResolveMapping( skeleton,
			userPresetLookup: assetsPath is null
				? null
				: signature => UserPresets.TryLoad( assetsPath, signature, skeleton ) );
		bestEffort = map;

		if ( !report.NeedsUserDecision )
			return map;

		// Full-body profile scoring penalizes missing hips and legs. For viewmodel
		// rigs, retain exact finger aliases instead of falling back to generic
		// numbering (e.g. s&box's proximal 0 must not become a metacarpal).
		if ( !HasStructuralMinimum( map ) )
		{
			var handMap = HumanoidMocap.Motion.HandCaptureRetargeter.DetectTargetHandMapping( skeleton, out var ambiguous );
			if ( handMap is not null ) return handMap;
			if ( ambiguous )
			{
				error = "Hand presets disagree about this rig's bone roles. Review the wrist and finger mapping.";
				return null;
			}
		}

		if ( HasStructuralMinimum( map ) || HumanoidMocap.Motion.HandCaptureRetargeter.HasTargetHandGeometry( skeleton, map ) )
		{
			map.Notes.Add(
				$"Target accepted best-effort (mapping confidence {map.Confidence * 100f:0}%): "
				+ $"{map.RoleToBone.Count} roles mapped; unmapped bones keep the model's rest pose." );
			return map;
		}

		error = "Armature needs bone mapping (mapping confidence "
			+ $"{map.Confidence * 100f:0}%): map a body, or at least one hand with finger "
			+ "joints defining its palm and finger chains for hand capture.";
		return null;
	}

	/// <summary>The least anatomy a usable conversion target needs: hips + both upper legs
	/// (pelvis line and ground reference) and one source of upper-body direction — the same
	/// bones the character-frame construction consumes. Everything else (hands, fingers,
	/// toes, even whole arms one side) may be missing and is simply skipped.</summary>
	static bool HasStructuralMinimum( MappingResult map )
	{
		bool Has( BoneRole role ) => map.RoleToBone.ContainsKey( role );
		var upperBody = (Has( BoneRole.UpperArmL ) && Has( BoneRole.UpperArmR ))
			|| (Has( BoneRole.ClavicleL ) && Has( BoneRole.ClavicleR ))
			|| Has( BoneRole.Neck );
		return Has( BoneRole.Hips ) && Has( BoneRole.UpperLegL ) && Has( BoneRole.UpperLegR )
			&& upperBody;
	}

	/// <summary>Converts <c>Model.Bones</c> to the library's skeleton model.
	/// <c>BoneCollection.Bone.LocalTransform</c> is the bind transform in MODEL space
	/// despite its name (measured: treating it as parent-relative compounds the citizen's
	/// upper body until the head FKs to 220in from the pelvis — the exploded "stretched
	/// mesh" class; near-root bones like the pelvis stay coincidentally correct, which is
	/// why position asserts on the pelvis alone never caught it). Convert to the
	/// parent-relative locals <see cref="SkeletonModel.Create"/> expects.</summary>
	static SkeletonModel SkeletonFromModel( Model model )
	{
		static XForm ToXForm( Transform transform ) => new(
			new System.Numerics.Vector3( transform.Position.x, transform.Position.y, transform.Position.z ),
			new System.Numerics.Quaternion(
				transform.Rotation.x, transform.Rotation.y, transform.Rotation.z, transform.Rotation.w ) );

		var definitions = new List<HumanoidMocap.Skeleton.BoneDefinition>();
		foreach ( var bone in model.Bones.AllBones )
		{
			var world = ToXForm( bone.LocalTransform );
			var local = bone.Parent is null
				? world
				: XForm.ToLocal( ToXForm( bone.Parent.LocalTransform ), world );
			definitions.Add( new HumanoidMocap.Skeleton.BoneDefinition(
				bone.Name, bone.Parent?.Name, local ) );
		}

		return SkeletonModel.Create( definitions );
	}

	/// <summary>The topmost ancestor of the hips bone - the bone vmdl ExtractMotion nodes
	/// should operate on (<c>default_root_bone_name</c>). Falls back to the first root.</summary>
	static string RootBoneName( SkeletonModel skeleton, MappingResult map )
	{
		var index = map.RoleToBone.TryGetValue( BoneRole.Hips, out var hips ) ? hips : 0;
		while ( skeleton[index].ParentIndex >= 0 )
			index = skeleton[index].ParentIndex;
		return skeleton[index].Name;
	}
}
