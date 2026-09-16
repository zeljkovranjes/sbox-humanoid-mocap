#nullable enable annotations
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Mapping;
using Sandbox;

namespace HumanoidMocap.Editor;

/// <summary>Video capture, synchronized preview and armature animation export.</summary>
public sealed partial class RetargetWindow : Widget
{
    public const string DockTitle = "Humanoid Mocap";
    static RetargetWindow _instance;
    public static RetargetWindow Instance => _instance.IsValid() ? _instance : null;
    TargetPickers.ResolvedTarget _target;
    string _targetError;
    RootMotionMode _rootMotion = RootMotionMode.Off;

    public RetargetWindow(Widget parent) : base(parent)
    {
        if (!_instance.IsValid()) _instance = this;
        Name = "HumanoidMocap"; WindowTitle = DockTitle; SetWindowIcon("videocam");
        MinimumSize = new Vector2(760, 480);
        Size = new Vector2(960, 640);
        Layout = Layout.Column();
        BuildMocapUi(); TrySelectSboxTarget();
    }
    [Event("tools.editorwindow.createview")]
    static void RegisterViewMenu(Menu menu) => EditorWindow.DockManager.RegisterDockType(new DockManager.DockInfo
    {
        Title=DockTitle, Icon="videocam", CreateAction=()=>{Open();return null;}
    });
    [Event("tools.editorwindow.postcreateview")]
    static void ConfigureViewMenu(Menu menu)
    {
        var option=menu.GetOption(DockTitle);if(option is null)return;
        option.Toggled=null;option.Checkable=false;option.Triggered=()=>Open();
    }
    public static RetargetWindow Open()
    {
        if (Instance is { } existing)
        {existing.GetWindow().Show();existing.Show();existing.GetWindow().Raise();return existing;}
        var dialog=new Dialog(null);dialog.Window.Title=DockTitle;dialog.Window.SetWindowIcon("videocam");
        dialog.Layout=Layout.Column();var window=dialog.Layout.Add(new RetargetWindow(dialog),1);
        dialog.Window.MinimumSize=new Vector2(760,480);dialog.Window.Size=new Vector2(960,640);
        dialog.Show();return window;
    }
    public override void OnDestroyed()
    {
        _processing?.Cancel();
        if (_adjustments.IsValid()) _adjustments.Window.Destroy();
        if (_instance == this) _instance = null;
        base.OnDestroyed();
    }
    string NormalizedOutputFolder() => "animations/humanoid_mocap";
    protected override void OnPaint()
    {
        // Paint only this surface; an inherited CSS background obscures the native
        // segmented control's selected option and the video drop area's hover state.
        Paint.ClearPen();
        Paint.SetBrush(Theme.SurfaceBackground);
        Paint.DrawRect(LocalRect);
    }
    void RefreshStatus() { if (_targetError is not null) SetStatus(_targetError, Theme.Red); }
    void SetStatus(string message, Color color)
    {
        if (!_captureStatus.IsValid()) return;
        _captureStatus.Text = message;
        _captureStatus.SetStyles($"color: {color.Hex};");
    }

	void TrySelectSboxTarget()
	{
		try
		{
			_target = TargetPickers.SboxDefault();
			FitMocapPlacementToTarget();
			_targetError = null;
		}
		catch ( Exception e )
		{
			_target = null;
			_targetError = e.Message;
		}
		RefreshTargetPickers();RefreshStatus();
	}

	void TrySelectSboxCitizenTarget()
	{
		try
		{
			_target = TargetPickers.SboxCitizen();
			FitMocapPlacementToTarget();
			_targetError = null;
		}
		catch ( Exception e )
		{
			_target = null;
			_targetError = e.Message;
		}
		RefreshTargetPickers();RefreshStatus();
	}

	void PickCustomModelTarget()
	{
		var picker = AssetPicker.Create( this, AssetType.Model );
		picker.Window.Title = "Select target model";
		picker.OnAssetPicked = assets =>
		{
			var asset = assets.FirstOrDefault();
			if ( asset is null )
				return;
			var resolved = TargetPickers.FromModelAsset( asset, out var error, out var rejected );
			ApplyPickedTarget( resolved, error, rejected );
		};
		picker.Show();
	}

	void PickCustomFbxTarget()
	{
		var path = EditorUtility.OpenFileDialog( "Select target model",
			"3D Model Files (*.fbx *.glb *.gltf)", null );
		if ( string.IsNullOrEmpty( path ) )
			return;
		var resolved = TargetPickers.FromModelFile( path, out var error, out var rejected );
		ApplyPickedTarget( resolved, error, rejected );
	}

	void ApplyPickedTarget( TargetPickers.ResolvedTarget resolved, string error,
		TargetPickers.RejectedTarget rejected = null )
	{
		if ( resolved is null )
		{
			// Skeleton loaded but wasn't auto-recognized: any humanoid-LIKE rig (a paw,
			// one finger, a missing hand) is still a valid target once its core bones are
			// pointed out - open the manual mapper instead of dead-ending.
			if ( rejected is not null )
			{
				OfferManualTargetMapping( rejected, error );
				return;
			}

			_targetError = error ?? "Target rejected.";
			SetStatus( _targetError, Theme.Red );
			return;
		}

		_target = resolved;
		RefreshTargetPickers();
		FitMocapPlacementToTarget();
		_targetError = null;
		if ( resolved.Warning is not null )
			SetStatus( resolved.Warning, Theme.Yellow );
		else
			RefreshStatus();

		// Custom FBX target with a skin: compile a mesh-only preview vmdl in the background
		// so the preview shows the actual model (the wireframe skeleton covers the wait and
		// stays the fallback). Skeleton-only FBX picks (Warning set) have nothing to skin.
		RefreshFbxTargetPreview( resolved );
		_=RefreshMocapPreviewAsync();
	}

	/// <summary>(Re)compiles the FBX target's preview model in the background. Also run
	/// after every conversion: the preview vmdl otherwise only refreshes on re-pick, so a
	/// project carrying a preview compiled by an older library version kept showing white
	/// placeholder materials in the preview while ModelDoc showed the fixed output.</summary>
	void RefreshFbxTargetPreview( TargetPickers.ResolvedTarget resolved )
	{
		if ( resolved?.ModelFilePath is not null && resolved.Warning is null )
			_fbxPreviewTask = CompileFbxTargetPreviewAsync( resolved );
	}

	/// <summary>Pending FBX-target preview compile; conversions await it because it also
	/// rebuilds the rig from the compiled model (the engine-authoritative skeleton).</summary>
	Task _fbxPreviewTask;

	async Task CompileFbxTargetPreviewAsync( TargetPickers.ResolvedTarget resolved )
	{
		SetStatus( $"Target: {resolved.Description}   ·   compiling its preview model…", Theme.Blue );
		var ok = await EditorPipeline.CompileModelTargetPreviewAsync( resolved, NormalizedOutputFolder() );
		await EditorPipeline.SwitchToMainThread();
		if ( !ReferenceEquals( _target, resolved ) )
			return; // user picked something else meanwhile
		if ( ok )
		{
			// Compilation can rebuild the rig in engine coordinates. Restore settings
			// against that final skeleton, rather than the pre-import fingerprint.
			FitMocapPlacementToTarget();
			RefreshStatus();
		}
		else
			SetStatus( $"Target: {resolved.Description}   ·   preview model could not be compiled - "
				+ "the preview will show the wireframe skeleton instead.", Theme.Yellow );
	}

	/// <summary>Manual-mapping fallback for unrecognized custom targets: the same
	/// <see cref="MappingEditor"/> sources use, prefilled with the cascade's best-effort
	/// map. Confirming builds the target AND saves the mapping as a user preset, so the
	/// rig resolves automatically on every future pick.</summary>
	void OfferManualTargetMapping( TargetPickers.RejectedTarget rejected, string detectError )
	{
		SetStatus( $"{detectError} Map the target's bones manually to use it.", Theme.Yellow );

		var editor = new MappingEditor( this, rejected.DisplayName, rejected.Skeleton, rejected.BestEffortMap )
		{
			Applied = mapping =>
			{
				var resolved = TargetPickers.FromManualMapping( rejected, mapping, out var buildError );
				if ( resolved is null )
				{
					_targetError = buildError ?? "Target rejected.";
					SetStatus( _targetError, Theme.Red );
					return;
				}

				try
				{
					var assetsPath = Project.Current?.GetAssetsPath();
					if ( assetsPath is not null )
					{
						UserPresets.Save( assetsPath,
							HumanoidMocap.Mapping.SkeletonSignature.Compute( rejected.Skeleton ),
							rejected.Skeleton, mapping );
					}
				}
				catch ( Exception e )
				{
					Log.Warning( $"[sbox-humanoid-mocap] could not save the target mapping as a preset: {e.Message}" );
				}

				ApplyPickedTarget( resolved, null );
			},
		};
		editor.Show();
	}

	sealed class Chip : Widget
	{
		readonly string _text;
		readonly Color _color;

		public Chip( Widget parent, string text, Color color ) : base( parent )
		{
			_text = text;
			_color = color;
			FixedHeight = 20;
			FixedWidth = 7.2f * text.Length + 18;
			ToolTip = "Profile · mapping confidence";
		}

		protected override void OnPaint()
		{
			DrawPill( LocalRect, _text, _color );
		}

		internal static void DrawPill( Rect rect, string text, Color color )
		{
			Paint.ClearPen();
			Paint.SetBrush( color.WithAlpha( 0.18f ) );
			Paint.DrawRect( rect, rect.Height * 0.5f );
			Paint.SetPen( color );
			Paint.SetDefaultFont( 7, 600 );
			Paint.DrawText( rect, text );
		}
	}
}
