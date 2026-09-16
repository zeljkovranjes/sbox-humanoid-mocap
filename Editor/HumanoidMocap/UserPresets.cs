#nullable enable annotations

// User preset profiles - the "preset learning" half of the preview flow (design §6).
//
// The Code/ facade can do no file IO, so user presets live entirely Editor-side:
// when the user confirms a preview of a mapping that came from manual edits or the
// blind auto-mapper, the confirmed mapping is saved as a Profile JSON under
//   <project assets>/humanoid_mocap/profiles/user/<SkeletonSignature>.json
// and on every later file add the window looks the signature up FIRST and passes the
// loaded mapping to the facade as RetargetRequest.MappingOverride with
// MappingSource.UserPreset - so the same rig is recognized instantly and never asks
// again.

using System;
using System.IO;
using Editor;
using HumanoidMocap.Mapping;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Editor;

/// <summary>
/// Editor-side persistence of user preset profiles, keyed by
/// <see cref="SkeletonSignature"/>. Files use the standard schema-versioned
/// <see cref="Profile"/> JSON, so user presets and shipped presets are the same format.
/// </summary>
public static class UserPresets
{
	/// <summary>Assets-relative folder the presets are stored in.</summary>
	public const string FolderRelative = "humanoid_mocap/profiles/user";

	static string FolderAbsolute( string assetsPath )
		=> Path.Combine( assetsPath, FolderRelative.Replace( '/', Path.DirectorySeparatorChar ) );

	static string PresetPath( string assetsPath, string signature )
		=> Path.Combine( FolderAbsolute( assetsPath ), signature + ".json" );

	/// <summary>
	/// Loads the user preset for the given skeleton signature and applies it to the
	/// skeleton. Returns null when no preset exists or the stored file is unreadable.
	/// The returned mapping carries <see cref="MappingSource.UserPreset"/>.
	/// </summary>
	public static MappingResult TryLoad( string assetsPath, string signature, SkeletonModel skeleton )
	{
		try
		{
			var path = PresetPath( assetsPath, signature );
			if ( !File.Exists( path ) )
				return null;

			var profile = Profile.FromJson( File.ReadAllText( path ) );
			var applied = ProfileDetector.Apply( profile, skeleton );

			// Re-wrap with the UserPreset source (ProfileDetector.Apply always says Preset).
			var result = new MappingResult( profile.Name, MappingSource.UserPreset )
			{
				Confidence = applied.Confidence,
			};
			foreach ( var kv in applied.RoleToBone )
				result.RoleToBone[kv.Key] = kv.Value;
			result.Notes.AddRange( applied.Notes );
			return result;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sbox-humanoid-mocap] failed to load user preset for {signature}: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Saves a confirmed mapping as a user preset profile: one alias per role - the exact
	/// source bone name. Returns the absolute file path written.
	/// </summary>
	/// <param name="namePrefix">Profile-name prefix, default <c>user</c>; the DL flow passes
	/// <c>user_dl</c> so derived presets are recognizable in the profile chip.</param>
	public static string Save( string assetsPath, string signature, SkeletonModel skeleton,
		MappingResult mapping, string namePrefix = "user" )
	{
		var aliases = new System.Collections.Generic.Dictionary<BoneRole, string[]>();
		foreach ( var kv in mapping.RoleToBone )
			aliases[kv.Key] = new[] { skeleton[kv.Value].Name };

		var shortSig = signature.Length >= 8 ? signature.Substring( 0, 8 ) : signature;
		var profile = new Profile( $"{namePrefix}_{shortSig}", Array.Empty<string>(), aliases );

		Directory.CreateDirectory( FolderAbsolute( assetsPath ) );
		var path = PresetPath( assetsPath, signature );
		File.WriteAllText( path, profile.ToJson() );
		Log.Info( $"[sbox-humanoid-mocap] saved user preset profile -> {path}" );
		return path;
	}

	/// <summary>Whether a preset exists for the signature.</summary>
	public static bool Exists( string assetsPath, string signature )
		=> File.Exists( PresetPath( assetsPath, signature ) );
}
