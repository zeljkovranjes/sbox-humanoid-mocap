#nullable enable annotations

using System;
using Editor;

namespace HumanoidMocap.Editor;

/// <summary>
/// The "No known profile found for this rig" decision dialog (design §6 no-profile flow),
/// shown per file when <c>NeedsUserDecision</c> is set after adding it: no preset profile
/// matched and the auto-mapper's confidence is below the detection threshold. Offers
/// auto-map (recommended), the deep-learning solver (enabled when the SAME weight asset
/// is installed - see <see cref="DlAssets"/>), and manual mapping.
/// </summary>
public sealed class NoProfileDialog : Dialog
{
	/// <summary>"Auto-map blindly" chosen: proceed with the best-effort auto mapping.</summary>
	public Action AutoMapChosen { get; set; }

	/// <summary>"Deep learning (experimental)" chosen: solve with the skeleton-agnostic
	/// DL solver and preview (design §6 option 2 / §10).</summary>
	public Action DeepLearningChosen { get; set; }

	/// <summary>"Manual mapping…" chosen: open the mapping editor.</summary>
	public Action ManualChosen { get; set; }

	/// <summary>Dialog dismissed without choosing (file stays in needs-review state).</summary>
	public Action Dismissed { get; set; }

	bool _chose;

	/// <summary>Creates the dialog for one source file. <paramref name="dlAvailable"/>
	/// enables the deep-learning option (the committed weight asset was found).</summary>
	public NoProfileDialog( Widget parent, string fileName, float autoConfidence, bool dlAvailable = false ) : base( parent )
	{
		Window.WindowTitle = "No known profile";
		Window.SetWindowIcon( "person_search" );
		Window.SetModal( true, true );
		Window.MinimumWidth = 480;

		Layout = Layout.Column();
		Layout.Margin = 20;
		Layout.Spacing = 12;

		var header = Layout.AddRow();
		header.Spacing = 12;
		var icon = header.Add( new Label( this ) { Text = "help_outline" } );
		icon.SetStyles( $"font-family: Material Icons; font-size: 34px; color: {Theme.Yellow.Hex};" );
		var headColumn = header.AddColumn( 1 );
		headColumn.Add( new Label.Subtitle( "No known profile found for this rig" ) );
		headColumn.Add( new Label( this )
		{
			Text = $"\"{fileName}\" does not match any preset bone-naming profile "
				+ $"(Mixamo, ActorCore/CC, UE Mannequin, Rokoko BVH) or saved user preset.",
			WordWrap = true,
		} );

		Layout.Add( new Label( this )
		{
			Text = $"The automatic mapper produced a best-effort mapping at {autoConfidence * 100f:0}% confidence. "
				+ "You can accept it, or assign the bones yourself.",
			WordWrap = true,
		} );

		Layout.AddSpacingCell( 4 );

		var buttons = Layout.AddRow();
		buttons.Spacing = 8;

		var auto = buttons.Add( new Button.Primary( "Auto-map blindly (recommended)" ) { Icon = "auto_fix_high" } );
		auto.Clicked = () => Choose( AutoMapChosen );

		var dl = buttons.Add( new Button( "Deep learning (experimental)", "psychology" ) );
		if ( dlAvailable )
		{
			dl.ToolTip = "Skeleton-agnostic neural retarget (SAME) - needs no bone mapping. "
				+ "Expect imperfect hands; review the preview before converting. "
				+ "Non-commercial model license (CC BY-NC 4.0).";
			dl.Clicked = () => Choose( DeepLearningChosen );
		}
		else
		{
			dl.Enabled = false;
			dl.ToolTip = "No model installed - Assets/humanoid_mocap/dl/same_v1.weights not found";
		}

		var manual = buttons.Add( new Button( "Manual mapping…", "edit" ) );
		manual.Clicked = () => Choose( ManualChosen );

		buttons.AddStretchCell();

		Window.AdjustSize();
	}

	void Choose( Action action )
	{
		_chose = true;
		action?.Invoke();
		Close();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		if ( !_chose )
			Dismissed?.Invoke();
	}
}
