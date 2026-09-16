#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using HumanoidMocap.Mapping;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Editor;

/// <summary>
/// Manual bone-mapping editor: one row per canonical <see cref="BoneRole"/>, grouped
/// anatomically (Body / Arms / Legs / Fingers L / Fingers R), each with a combo of the
/// source skeleton's bone names (plus <c>&lt;none&gt;</c>), pre-filled from the entry's
/// current mapping. Apply produces a <see cref="MappingSource.Manual"/>
/// <see cref="MappingResult"/> that the window installs as the file's mapping override.
/// </summary>
public sealed class MappingEditor : Dialog
{
	static readonly (string Group, BoneRole[] Roles)[] Groups =
	{
		("Body", new[]
		{
			BoneRole.Hips, BoneRole.Spine0, BoneRole.Spine1, BoneRole.Spine2,
			BoneRole.Spine3, BoneRole.Spine4, BoneRole.Neck, BoneRole.Head,
		}),
		("Arms", new[]
		{
			BoneRole.ClavicleL, BoneRole.UpperArmL, BoneRole.LowerArmL, BoneRole.HandL,
			BoneRole.ClavicleR, BoneRole.UpperArmR, BoneRole.LowerArmR, BoneRole.HandR,
		}),
		("Legs", new[]
		{
			BoneRole.UpperLegL, BoneRole.LowerLegL, BoneRole.FootL, BoneRole.ToeL,
			BoneRole.UpperLegR, BoneRole.LowerLegR, BoneRole.FootR, BoneRole.ToeR,
		}),
		("Fingers (left)", FingerRoles( "L" )),
		("Fingers (right)", FingerRoles( "R" )),
	};

	static BoneRole[] FingerRoles( string side )
		=> Enum.GetValues<BoneRole>()
			.Where( r => r.ToString().EndsWith( side, StringComparison.Ordinal )
				&& (r.ToString().StartsWith( "Thumb" ) || r.ToString().StartsWith( "Index" )
					|| r.ToString().StartsWith( "Middle" ) || r.ToString().StartsWith( "Ring" )
					|| r.ToString().StartsWith( "Pinky" )) )
			.ToArray();

	readonly SkeletonModel _skeleton;
	readonly Dictionary<BoneRole, int> _selection;

	/// <summary>Invoked with the manual mapping when the user applies.</summary>
	public Action<MappingResult> Applied { get; set; }

	/// <summary>Creates the editor pre-filled from <paramref name="current"/>.</summary>
	public MappingEditor( Widget parent, string fileName, SkeletonModel skeleton, MappingResult current )
		: base( parent )
	{
		_skeleton = skeleton;
		_selection = new Dictionary<BoneRole, int>( current?.RoleToBone ?? new Dictionary<BoneRole, int>() );

		Window.WindowTitle = $"Bone Mapping - {fileName}";
		Window.SetWindowIcon( "device_hub" );
		Window.SetModal( true, true );
		Window.MinimumWidth = 460;
		Window.MinimumHeight = 600;

		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 8;

		Layout.Add( new Label( this )
		{
			Text = "Assign bones to their roles; leave absent bones at <none>. For hand capture, "
				+ "map each wrist and its finger chains. Arms are optional; a torso and legs are not required.",
			WordWrap = true,
		} );

		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.Canvas = new Widget( scroll );
		scroll.Canvas.Layout = Layout.Column();
		scroll.Canvas.Layout.Margin = new Sandbox.UI.Margin( 4, 4, 16, 4 );
		scroll.Canvas.Layout.Spacing = 4;
		var canvas = scroll.Canvas.Layout;

		foreach ( var (group, roles) in Groups )
		{
			var header = canvas.Add( new Label( this ) { Text = group } );
			header.SetStyles( $"font-weight: 600; color: {Theme.Blue.Hex}; margin-top: 8px;" );

			foreach ( var role in roles )
				canvas.Add( BuildRoleRow( role ) );
		}

		canvas.AddStretchCell();

		var buttons = Layout.AddRow();
		buttons.Spacing = 8;
		buttons.AddStretchCell();
		buttons.Add( new Button( "Cancel" ) { Clicked = Close } );
		var apply = buttons.Add( new Button.Primary( "Apply Mapping" ) { Icon = "check" } );
		apply.Clicked = Apply;

		Window.Size = new Vector2( 520, 720 );
	}

	Widget BuildRoleRow( BoneRole role )
	{
		var row = new Widget( this );
		row.Layout = Layout.Row();
		row.Layout.Spacing = 8;

		row.Layout.Add( new Label( this ) { Text = role.ToString(), FixedWidth = 130 } );

		var combo = row.Layout.Add( new ComboBox( this ), 1 );
		combo.AddItem( "<none>", "block",
			() => _selection.Remove( role ),
			selected: !_selection.ContainsKey( role ) );

		for ( var i = 0; i < _skeleton.Count; i++ )
		{
			var boneIndex = i;
			combo.AddItem( _skeleton[i].Name, null,
				() => _selection[role] = boneIndex,
				selected: _selection.TryGetValue( role, out var sel ) && sel == boneIndex );
		}

		return row;
	}

	void Apply()
	{
		// Reject duplicate assignments up front (the target rig builder would throw later).
		var duplicates = _selection.GroupBy( kv => kv.Value ).Where( g => g.Count() > 1 ).ToList();
		if ( duplicates.Count > 0 )
		{
			var first = duplicates[0];
			var roles = string.Join( ", ", first.Select( kv => kv.Key ) );
			new PopupWindow( "Duplicate assignment",
				$"Bone \"{_skeleton[first.Key].Name}\" is assigned to multiple roles: {roles}." )
				.Show();
			return;
		}

		var result = new MappingResult( "manual", MappingSource.Manual ) { Confidence = 1f };
		foreach ( var kv in _selection )
			result.RoleToBone[kv.Key] = kv.Value;
		result.Notes.Add( "Mapping assigned by hand in the mapping editor." );

		Applied?.Invoke( result );
		Close();
	}
}
