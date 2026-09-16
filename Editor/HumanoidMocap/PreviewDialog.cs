#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Editor;

/// <summary>
/// Modal preview + confirmation step (design §6 "Preview + preset learning"): shows the
/// retargeted clip on the skinned <see cref="PreviewWidget"/> with play/pause + frame
/// scrubbing, then either confirms ("Looks good - Convert", which also offers saving the
/// mapping as a user preset when it came from manual edits or the blind auto-mapper) or
/// cancels. The conversion itself is the caller's job - this dialog only decides.
/// </summary>
public sealed class PreviewDialog : Dialog
{
	readonly PreviewWidget _preview;
	readonly FloatSlider _scrubber;
	readonly Label _frameLabel;
	readonly Button _playButton;
	readonly Button _ghostButton;
	readonly Button _skeletonButton;
	readonly Checkbox _savePresetCheckbox;
	readonly List<HumanoidMocap.ClipResult> _clips;

	/// <summary>Invoked on confirm with whether "Save as profile for this rig" was checked.</summary>
	public Action<bool> Confirmed { get; set; }

	/// <summary>Invoked when the user cancels (closes without converting).</summary>
	public Action Cancelled { get; set; }

	bool _confirmed;

	/// <summary>
	/// Creates the dialog over already-solved clips of one source file.
	/// <paramref name="target"/> supplies the rig + preview model;
	/// <paramref name="mappingSource"/> controls the preset-saving checkbox (shown for
	/// Manual / AutoName / AutoTopology mappings, checked by default).
	/// <paramref name="sourceSkeleton"/>/<paramref name="sourceClip"/>/<paramref name="sourceMapping"/>
	/// (all optional) feed the "Show source" stick-skeleton ghost overlay - the toggle is
	/// disabled when they are absent or the mapping cannot anchor the ghost.
	/// </summary>
	public PreviewDialog(
		Widget parent, string fileName, IReadOnlyList<HumanoidMocap.ClipResult> clips,
		TargetPickers.ResolvedTarget target, MappingSource mappingSource,
		HumanoidMocap.Skeleton.Skeleton sourceSkeleton = null,
		HumanoidMocap.Skeleton.Clip sourceClip = null,
		MappingResult sourceMapping = null ) : base( parent )
	{
		_clips = clips.Where( c => c.Success && c.SolvedFrames is { Count: > 0 } ).ToList();

		Window.WindowTitle = $"Preview - {fileName}";
		Window.SetWindowIcon( "preview" );
		Window.SetModal( true, true );
		Window.MinimumWidth = 560;
		Window.MinimumHeight = 560;

		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 8;

		// ---- preview viewport ----------------------------------------------------------
		_preview = new PreviewWidget(
			this, target.Spec.Rig, target.PreviewModelPath, target.PreviewPositionScale,
			target.Spec.UpAxis );
		Layout.Add( _preview, 1 );

		if ( sourceSkeleton is not null && sourceClip is not null && sourceMapping is not null )
			_preview.SetSourceGhost( sourceSkeleton, sourceClip, sourceMapping );

		if ( !_preview.HasModel )
		{
			Layout.Add( new Label( this )
			{
				Text = "No compiled preview model exists for this target - showing the retargeted animation as a wireframe skeleton.",
				WordWrap = true,
			} );
		}

		// ---- clip picker (multi-take files) ---------------------------------------------
		if ( _clips.Count > 1 )
		{
			var clipRow = Layout.AddRow();
			clipRow.Spacing = 8;
			clipRow.Add( new Label( this ) { Text = "Clip:" } );
			var combo = clipRow.Add( new ComboBox( this ) { MinimumWidth = 220 } );
			for ( var i = 0; i < _clips.Count; i++ )
			{
				var clip = _clips[i];
				combo.AddItem( clip.ClipName, "movie", () => SelectClip( clip ), selected: i == 0 );
			}
			clipRow.AddStretchCell();
		}

		// ---- transport ------------------------------------------------------------------
		var transport = Layout.AddRow();
		transport.Spacing = 8;

		_playButton = transport.Add( new Button( "", "pause" ) { FixedWidth = 28, FixedHeight = 24 } );
		_playButton.Clicked = TogglePlay;

		_scrubber = transport.Add( new FloatSlider( this ), 1 );
		_scrubber.Minimum = 0;
		_scrubber.OnValueEdited = () => _preview.Scrub( (int)_scrubber.Value );

		_frameLabel = transport.Add( new Label( this ) { Text = "0 / 0", FixedWidth = 80 } );

		// Source-ghost toggle lives ON the preview (transport bar), not under Options:
		// it is a per-preview inspection aid, not a conversion setting.
		_ghostButton = transport.Add( new Button( "Show source", "compare" ) { IsToggle = true, FixedHeight = 24 } );
		_ghostButton.ToolTip = "Overlay the SOURCE clip as a semi-transparent stick skeleton, "
			+ "root-aligned and hip-height-scaled onto the target, synced to the scrub position.";
		_ghostButton.Enabled = _preview.HasSourceGhost;
		_ghostButton.Clicked = () => _preview.ShowSourceGhost = _ghostButton.IsChecked;

		// Wireframe-skeleton view: switches the preview between the skinned model and the
		// retargeted animation drawn as a stick skeleton (and back). Targets with no
		// compiled preview model are locked to the skeleton view - it is all they have.
		_skeletonButton = transport.Add( new Button( "Skeleton", "polyline" ) { IsToggle = true, FixedHeight = 24 } );
		if ( _preview.HasModel )
		{
			_skeletonButton.ToolTip = "Switch to a wireframe view of the retargeted skeleton "
				+ "(click again to switch back to the skinned model).";
			_skeletonButton.Clicked = () => _preview.SkeletonOnly = _skeletonButton.IsChecked;
		}
		else
		{
			_skeletonButton.IsChecked = true;
			_skeletonButton.Enabled = false;
			_skeletonButton.ToolTip = "No compiled model exists for this target - the wireframe "
				+ "skeleton is the only available view.";
		}

		_preview.FrameChanged = frame =>
		{
			_scrubber.Value = frame;
			UpdateFrameLabel();
		};

		// ---- confirm row ------------------------------------------------------------------
		var confirm = Layout.AddRow();
		confirm.Spacing = 8;

		if ( mappingSource is MappingSource.Manual or MappingSource.AutoName or MappingSource.AutoTopology )
		{
			_savePresetCheckbox = confirm.Add( new Checkbox( "Save as profile for this rig" ) { Value = true } );
			_savePresetCheckbox.ToolTip =
				"Stores the confirmed mapping as a user preset (keyed by the skeleton's signature), "
				+ "so this rig is recognized automatically next time.";
		}

		confirm.AddStretchCell();

		var cancel = confirm.Add( new Button( "Cancel" ) );
		cancel.Clicked = Close;

		var ok = confirm.Add( new Button.Primary( "Looks good - Convert" ) { Icon = "check" } );
		ok.Tint = Theme.Green;
		ok.Enabled = _clips.Count > 0;
		ok.Clicked = () =>
		{
			_confirmed = true;
			Confirmed?.Invoke( _savePresetCheckbox?.Value ?? false );
			Close();
		};

		if ( _clips.Count > 0 )
			SelectClip( _clips[0] );

		Window.Size = new Vector2( 640, 720 );
	}

	void SelectClip( HumanoidMocap.ClipResult clip )
	{
		_preview.SetClip( clip );
		_preview.Playing = true;
		_playButton.Icon = "pause";
		_scrubber.Maximum = Math.Max( _preview.FrameCount - 1, 0 );
		_scrubber.Value = 0;
		UpdateFrameLabel();
	}

	void TogglePlay()
	{
		_preview.Playing = !_preview.Playing;
		_playButton.Icon = _preview.Playing ? "pause" : "play_arrow";
	}

	void UpdateFrameLabel()
	{
		_frameLabel.Text = $"{_preview.CurrentFrame + 1} / {_preview.FrameCount}";
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		if ( !_confirmed )
			Cancelled?.Invoke();
	}
}
