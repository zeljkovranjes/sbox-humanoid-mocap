#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Target;
using Sandbox;
using SourceClip = HumanoidMocap.Skeleton.Clip;
using SourceSkeleton = HumanoidMocap.Skeleton.Skeleton;
using VecN = System.Numerics.Vector3;

namespace HumanoidMocap.Editor;

/// <summary>
/// Skinned preview of a retargeted clip BEFORE anything is compiled: the target's real
/// compiled model (e.g. <c>citizen_human_male.vmdl</c>) is loaded into a
/// <see cref="SceneModel"/> and its bones are driven directly from
/// <see cref="ClipResult.SolvedFrames"/> - per frame the solved locals (target skeleton
/// bone order, target units) are FK-composed to model-space transforms, converted to the
/// engine's axis convention and units, and applied via
/// <see cref="SceneModel.SetBoneOverride"/> followed by
/// <see cref="SceneModel.SetBoneWorldTransform"/> after model evaluation. The latter
/// preserves the baked pose when model constraints would replace it (e.g. CopyPinky).
/// Bones are matched to the engine model BY NAME; unrepresented bones remain model-owned.
/// </summary>
/// <remarks>
/// <para><b>Axis conversion.</b> <see cref="TargetUpAxis.YUpCm"/> rigs (the s&amp;box source
/// skeleton) are authored Y-up in centimeters while the compiled engine model is Z-up in
/// inches - the same conversion resourcecompiler applies to the Y-up DMX at compile time.
/// FK world transforms are therefore mapped with the +90° rotation about X taking Y-up to
/// Z-up - position (x, y, z) → (x, −z, y), rotation q → q_R ⊗ q - then scaled by
/// <c>positionScale</c> (0.3937 cm→inch). Empirically: the citizen pelvis rests at
/// y ≈ 93 cm → engine (0, 0, ≈36.6 in), which the UI smoke gate asserts.
/// <see cref="TargetUpAxis.ZUpEngine"/> rigs (custom compiled-model targets) are already in
/// engine space - no conversion.</para>
/// <para>This was chosen over building an in-memory <c>Model.Builder</c> model with
/// <c>AddAnimation</c>/<c>AddFrame</c>: a builder model carries bones but no mesh, so a
/// sequence playing on it renders nothing visible - driving the real skinned model shows
/// the actual character. Camera: front framing from the model bounds with left-drag
/// yaw orbit (same idiom as the editor's other preview widgets).</para>
/// </remarks>
public sealed partial class PreviewWidget : SceneRenderingWidget
{
	/// <summary>Rotation about +X by 90°, taking Y-up coordinates to Z-up (y→z, z→−y).</summary>
	static readonly System.Numerics.Quaternion YUpToZUp =
		System.Numerics.Quaternion.CreateFromAxisAngle( System.Numerics.Vector3.UnitX, MathF.PI * 0.5f );

	SceneModel _sceneModel;
	TargetRig _rig;
	float _positionScale = 1f;
	bool _convertYUpToZUp;
	int[] _rigToModelBone;
	Transform[] _modelBindByRig;
	Transform[] _renderPose;
	int _renderPoseCount;
	XForm[] _worldScratch;
	XForm[] _interpolatedScratch;

	HumanoidMocap.ClipResult _clip;
	float _time;
	float _yaw = 35f;
	float _lookYaw, _lookPitch;
	Vector3? _handViewDirection;
	public BoneRole[] FramingHands { get; set; } = new[] { BoneRole.HandL, BoneRole.HandR };
	Vector2 _lastMouse;

	// ---- target skeleton wireframe (the RETARGETED pose as stick skeleton) --------------
	// One SceneLineObject per skeleton CHAIN: the object holds a single polyline (every
	// StartLine restarts it - per-bone Start/End cycles left only the last segment on
	// screen, the user's "two bars"), and bridging disjoint segments through one strip
	// leaks connector streaks (the line shader ignores vertex alpha). A chain - spine,
	// each limb, each finger - IS a connected polyline, so this is the supported usage.
	readonly List<SceneLineObject> _skeletonChainObjects = new();
	int[][] _skeletonChains;
	bool _skeletonOnly;
	Vector3 _skeletonCenter = Vector3.Up * 32f;
	float _skeletonRadius = 40f;

	// ---- source ghost (stick-skeleton overlay of the SOURCE clip) -----------------------
	readonly List<SceneLineObject> _ghostChainObjects = new();
	int[][] _ghostChains;
	SourceSkeleton _ghostSkeleton;
	SourceClip _ghostClip;
	XForm[] _ghostScratch;
	bool _showSourceGhost;

	// source-side alignment (fixed once per SetSourceGhost)
	VecN _srcAnchor, _srcLat, _srcUp, _srcFwd;
	float _srcHipHeight;

	// target-side alignment (lazy, recomputed when the previewed clip changes)
	HumanoidMocap.ClipResult _ghostAlignedClip;
	VecN _tgtAnchor, _tgtLat, _tgtUp, _tgtFwd;
	float _ghostScale = 1f;

	static readonly Color GhostBoneColor = new( 1f, 0.75f, 0.25f, 0.5f );   // amber, ~50%
	static readonly Color GhostJointColor = new( 1f, 0.8f, 0.35f, 0.65f );

	static readonly Color SkeletonBoneColor = new( 0.35f, 0.9f, 1f, 0.95f ); // cyan, solid
	static readonly Color SkeletonJointColor = new( 0.6f, 1f, 1f, 1f );

	/// <summary>
	/// Builds a line overlay object ready to actually RENDER: without an explicit line
	/// material a SceneLineObject draws with the engine's error material (user report: the
	/// skeleton view and the source ghost showed up as a single purple blob), and without
	/// generous explicit Bounds a SceneCustomObject can be culled entirely (its default
	/// bounds don't follow the added line points).
	/// </summary>
	SceneLineObject CreateLineObject()
	{
		var lines = new SceneLineObject( Scene.SceneWorld );
		lines.Opaque = false;
		lines.Lighting = false;
		var material = Material.Load( "materials/default/default_line.vmat" );
		if ( material is not null )
			lines.Material = material;
		return lines;
	}

	/// <summary>Expands a scene object's bounds around freshly drawn line points so the
	/// renderer never culls the overlay (called after every redraw).</summary>
	static void FitBounds( SceneLineObject lines, Vector3 min, Vector3 max )
	{
		if ( lines is null || min.x > max.x )
			return;
		var bounds = new BBox( min, max );
		lines.Bounds = bounds.Grow( MathF.Max( bounds.Size.Length * 0.25f, 16f ) );
	}

	/// <summary>Line width scaled to the character: the camera frames the whole skeleton,
	/// so a fixed sub-inch width collapses below one pixel on large rigs (measured: a
	/// 190-segment skeleton rendered ~5 pixels at 0.6in width - effectively invisible,
	/// which is what the user reported).</summary>
	float OverlayLineWidth => MathF.Max( 1.2f, _skeletonRadius * 0.03f );

	/// <summary>
	/// Splits a skeleton hierarchy into connected CHAINS (spine, each limb, each finger):
	/// runs of single-child bones, restarted at every branch point (the branch bone leads
	/// each child chain so the polylines connect). Each chain renders as its own
	/// SceneLineObject - the object holds a single polyline, so this is the only layout
	/// that draws a full skeleton (per-segment Start/End left one segment; alpha/width
	/// bridge tricks leak connector streaks).
	/// </summary>
	static int[][] BuildChains( Func<int, int> parentOf, int count )
	{
		var children = new List<int>[count];
		for ( var i = 0; i < count; i++ )
			children[i] = new List<int>();
		var roots = new List<int>();
		for ( var i = 0; i < count; i++ )
		{
			var parent = parentOf( i );
			if ( parent < 0 || parent >= count )
				roots.Add( i );
			else
				children[parent].Add( i );
		}

		var chains = new List<int[]>();
		var stack = new Stack<List<int>>();
		foreach ( var root in roots )
			stack.Push( new List<int> { root } );
		while ( stack.Count > 0 )
		{
			var path = stack.Pop();
			var node = path[^1];
			while ( children[node].Count == 1 )
			{
				node = children[node][0];
				path.Add( node );
			}
			if ( path.Count > 1 )
				chains.Add( path.ToArray() );
			foreach ( var child in children[node] )
				stack.Push( new List<int> { node, child } );
		}
		return chains.ToArray();
	}

	/// <summary>Grows/shrinks a chain-object pool to the chain count.</summary>
	void EnsureChainObjects( List<SceneLineObject> pool, int count )
	{
		while ( pool.Count < count )
			pool.Add( CreateLineObject() );
		while ( pool.Count > count )
		{
			pool[^1].Delete();
			pool.RemoveAt( pool.Count - 1 );
		}
	}

	/// <summary>Redraws one chain object as a single polyline through the given points.</summary>
	static void DrawChain( SceneLineObject lines, IReadOnlyList<Vector3> points, Color color, float width )
	{
		lines.Clear();
		if ( points.Count < 2 )
			return;
		lines.StartLine();
		foreach ( var point in points )
			lines.AddLinePoint( point, color, width );
		lines.EndLine();
	}

	/// <summary>Whether playback advances (play/pause).</summary>
	public bool Playing { get; set; } = true;

	/// <summary>Current frame (clamped to the clip).</summary>
	public int CurrentFrame { get; private set; }

	/// <summary>Frames in the current clip.</summary>
	public int FrameCount => _clip?.SolvedFrames?.Count ?? 0;

	/// <summary>Raised when playback advances to a new frame (drives the scrubber).</summary>
	public Action<int> FrameChanged { get; set; }

	/// <summary>True when a preview model could be loaded for the target.</summary>
	public bool HasModel => _sceneModel.IsValid();

	/// <summary>
	/// Wireframe-skeleton view of the RETARGETED animation: the target skeleton drawn as
	/// stick bones instead of (or, without a model, in place of) the skinned mesh. Targets
	/// with no compiled preview model (custom FBX targets) force this ON — the preview used
	/// to show nothing at all for them. Toggling back OFF restores the skinned view.
	/// </summary>
	public bool SkeletonOnly
	{
		get => _skeletonOnly || !HasModel;
		set
		{
			_skeletonOnly = value;
			if ( _sceneModel.IsValid() )
				_sceneModel.RenderingEnabled = !SkeletonOnly;
			foreach ( var chain in _skeletonChainObjects )
				chain.RenderingEnabled = SkeletonOnly;
		}
	}

	/// <summary>Line segments the skeleton view drew on its last update (bones + joint
	/// ticks). Exposed for the UI smoke gate's headless assertion.</summary>
	public int SkeletonLineCount { get; private set; }

	/// <summary>Geometry diagnostics of the last skeleton-view redraw (drawn extents,
	/// camera anchor, line width) for the UI smoke gate's notes.</summary>
	public string SkeletonDebug { get; private set; } = "";

	/// <summary>
	/// Creates the preview scene. <paramref name="previewModelPath"/> is the compiled
	/// model whose bone names match <paramref name="rig"/>;
	/// <paramref name="positionScale"/> converts rig positions to engine units
	/// (0.3937 for cm rigs like the s&amp;box source skeleton, 1.0 for engine-unit rigs);
	/// <paramref name="upAxis"/> is the rig's axis convention (<see cref="TargetUpAxis.YUpCm"/>
	/// rigs additionally get the Y-up→Z-up basis conversion, see class remarks).
	/// </summary>
	public PreviewWidget( Widget parent, TargetRig rig, string previewModelPath, float positionScale,
		TargetUpAxis upAxis = TargetUpAxis.YUpCm )
		: base( parent )
	{
		_rig = rig;
		_positionScale = positionScale;
		_convertYUpToZUp = upAxis == TargetUpAxis.YUpCm;
		MinimumSize = new Vector2( 240, 200 );
		MouseTracking = true;

		Scene = Scene.CreateEditorScene();
		using ( Scene.Push() )
		{
			Camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>( false );
			Camera.BackgroundColor = Theme.ControlBackground;
			Camera.ZNear = 1f;
			Camera.ZFar = 4096f;
			Camera.FieldOfView = 45f;
			Camera.Enabled = true;
		}

		var world = Scene.SceneWorld;
		new ScenePointLight( world, new Vector3( 120, 100, 120 ), 600, Color.White * 3.5f ).ShadowsEnabled = false;
		new ScenePointLight( world, new Vector3( -120, -100, 90 ), 600, Color.White * 2.0f ).ShadowsEnabled = false;

		if ( previewModelPath is not null )
		{
			var model = Model.Load( previewModelPath );
			if ( model is not null && !model.IsError )
			{
				_sceneModel = new SceneModel( world, model, Transform.Zero );
				_sceneModel.UseAnimGraph = false;
				ConfigureHeadVisibility();
				BuildBoneMap( model );
			}
		}

		_worldScratch = new XForm[rig.Skeleton.Count];
		_interpolatedScratch = new XForm[rig.Skeleton.Count];
		_renderPose = new Transform[rig.Skeleton.Count];

		// Camera anchor bones: the MAPPED role bones only. Character FBX files routinely
		// carry stray far-away nodes (Biped dummies, exporter helpers) - bounds taken over
		// every node put the camera fifty character-heights away, shrinking the wireframe
		// skeleton and the source ghost to a bar-sized smudge (user report: "it shows
		// like two bars"). The mapped roles ARE the character.
		var mapped = new List<int>();
		foreach ( var role in Enum.GetValues<HumanoidMocap.Mapping.BoneRole>() )
		{
			if ( rig.BoneForRole( role ) is { } index )
				mapped.Add( index );
		}
		_cameraBones = mapped.Count >= 4
			? mapped.ToArray()
			: Enumerable.Range( 0, rig.Skeleton.Count ).ToArray();

		// Rest-pose bounds in engine space: camera zoom for the wireframe-skeleton view
		// (kept fixed so the zoom doesn't pulse with the animation; the center follows).
		var restBounds = ComputeRestBounds();
		_skeletonCenter = restBounds.Center;
		_skeletonRadius = MathF.Max( restBounds.Size.Length * 0.5f, 8f );
	}

	int[] _cameraBones;

	BBox ComputeRestBounds()
	{
		var skeleton = _rig.Skeleton;
		if ( skeleton.Count == 0 || _cameraBones.Length == 0 )
			return BBox.FromPositionAndSize( Vector3.Up * 32f, 64f );
		var first = RigWorldToEngine( skeleton.RestWorld[_cameraBones[0]] ).Position;
		var bounds = new BBox( first, first );
		foreach ( var index in _cameraBones )
			bounds = bounds.AddPoint( RigWorldToEngine( skeleton.RestWorld[index] ).Position );
		return bounds;
	}

	/// <summary>Switches the clip being previewed (restarts playback).</summary>
	public void SetClip( HumanoidMocap.ClipResult clip )
	{
		_clip = clip;
		_renderPoseCount = 0;
		_time = 0;
		CurrentFrame = 0;
		_ghostAlignedClip = null; // ghost anchor depends on this clip's frame 0 - recompute
		_handViewDirection = null;
	}

	/// <summary>Frame the captured hands from eye height using a fixed direction for the
	/// entire clip. This only changes the preview camera, never the captured motion.</summary>
	public void ResetView()
	{
		_yaw = 35; _lookYaw = 0; _lookPitch = 0; _handViewDirection = null;
		if (_clip?.SolvedFrames is not { Count: > 0 } frames) return;
		var skeleton = _rig.Skeleton;
		if (TryCharacterBasis(_rig.BoneForRole,skeleton.RestWorld,out _,out _,out var facing,out _,out _))
		{
			var origin=RigWorldToEngine(XForm.Identity).Position;
			var direction=RigWorldToEngine(new XForm(facing,System.Numerics.Quaternion.Identity)).Position-origin;
			// Inspect proportions straight-on by default. An oblique view shortens
			// the apparent clavicle span, even when every joint is at rig-rest width.
			_yaw=MathF.Atan2(direction.y,direction.x)*180/MathF.PI;
		}
		var scratch = new XForm[skeleton.Count];
		var hands = FramingHands.Select(_rig.BoneForRole).Where(i=>i.HasValue).Select(i=>i.Value).ToArray();
		var sum = Vector3.Zero; var count = 0;
		for (var f = 0; f < frames.Count; f += Math.Max(1, frames.Count / 120))
		{
			for (var b=0;b<scratch.Length;b++)
				scratch[b]=skeleton[b].ParentIndex<0?frames[f][b]:XForm.Compose(scratch[skeleton[b].ParentIndex],frames[f][b]);
			var eye = _rig.BoneForRole(BoneRole.Head) is int head ? RigWorldToEngine(scratch[head]).Position : new Vector3(0,0,64);
			if(CaptureView is { } capture)
			{
				var p=VecN.Transform(capture.CaptureCameraPosition*39.3700787f,YUpToZUp);
				eye=new Vector3(p.X,p.Y,p.Z);
			}
			foreach(var hand in hands)
			{
				var direction=RigWorldToEngine(scratch[hand]).Position-eye;
				if(direction.Length>1){sum+=direction.Normal;count++;}
			}
		}
		if(count>0&&sum.Length>.01f)_handViewDirection=sum.Normal;
		UpdateCamera();
	}

	/// <summary>Jumps to a frame (scrubber); pauses playback.</summary>
	public void Scrub( int frame )
	{
		if ( FrameCount == 0 )
			return;
		Playing = false;
		CurrentFrame = Math.Clamp( frame, 0, FrameCount - 1 );
		_time = CurrentFrame;
	}

	void BuildBoneMap( Model model )
	{
		_rigToModelBone = new int[_rig.Skeleton.Count];
		_modelBindByRig = new Transform[_rig.Skeleton.Count];
		var missing = 0;
		var scaled = 0;
		for ( var i = 0; i < _rig.Skeleton.Count; i++ )
		{
			var bone = model.Bones.GetBone( _rig.Skeleton[i].Name );
			_rigToModelBone[i] = bone?.Index ?? -1;
			if ( _rigToModelBone[i] < 0 )
				missing++;
			// The bind transform carries the bone's SCALE (cartoon/Sketchfab rigs bake
			// non-uniform node scales into the bind). SetBoneOverride replaces the whole
			// transform - overriding with the default scale 1 collapsed/inflated the
			// skin around scaled bones (finger spike fans on a scaled-finger rig).
			_modelBindByRig[i] = bone?.LocalTransform ?? Transform.Zero;
			if ( bone is not null && !_modelBindByRig[i].Scale.AlmostEqual( Vector3.One, 0.001f ) )
				scaled++;
		}

		if ( missing > 0 )
			Log.Info( $"[sbox-humanoid-mocap] preview: {missing} rig bones have no match on the preview model (kept at bind pose)." );
		if ( scaled > 0 )
			Log.Info( $"[sbox-humanoid-mocap] preview: {scaled} model bones carry non-unit bind scale (preserved through pose overrides)." );
	}

	protected override void PreFrame()
	{
		Scene.EditorTick( RealTime.Now, RealTime.Delta );
		UpdateCamera();

		if ( _clip?.SolvedFrames is not { Count: > 0 } frames )
			return;

		if ( Playing )
		{
			_time += RealTime.Delta * Math.Max( _clip.Fps, 1f );
			var duration = Math.Max( frames.Count - 1, 1 );
			while ( _time >= duration )
				_time -= duration; // preview always loops
			var frame = Math.Clamp( (int)MathF.Floor( _time ), 0, frames.Count - 1 );
			if ( frame != CurrentFrame )
			{
				CurrentFrame = frame;
				FrameChanged?.Invoke( frame );
			}
		}

		if ( _sceneModel.IsValid() )
			_sceneModel.Update( RealTime.Delta );
		if ( Playing && frames.Count > 1 )
			ApplyInterpolatedFrame( frames );
		else
			ApplyCurrentFrame();
		DrawTargetBones();
	}

	/// <summary>Interpolates solved samples at the editor render rate. Output animation
	/// samples stay untouched; this only removes visible frame stepping in the preview.</summary>
	void ApplyInterpolatedFrame( IReadOnlyList<XForm[]> frames )
	{
		var lo = Math.Clamp( (int)MathF.Floor( _time ), 0, frames.Count - 1 );
		var hi = Math.Min( lo + 1, frames.Count - 1 );
		var blend = _time - lo;
		var count = Math.Min( _interpolatedScratch.Length,
			Math.Min( frames[lo].Length, frames[hi].Length ) );
		for ( var i = 0; i < count; i++ )
		{
			_interpolatedScratch[i] = new XForm(
				System.Numerics.Vector3.Lerp( frames[lo][i].Pos, frames[hi][i].Pos, blend ),
				System.Numerics.Quaternion.Slerp( frames[lo][i].Rot, frames[hi][i].Rot, blend ) );
		}
		ApplyPose( _interpolatedScratch );
	}

	/// <summary>Applies the current frame's solved pose (no-op without a clip), then syncs
	/// the wireframe-skeleton view and the source ghost overlay. Works with or without a
	/// preview model — the skeleton view is all a model-less target (custom FBX) shows.
	/// Public so the UI smoke gate can drive a frame headlessly.</summary>
	public void ApplyCurrentFrame()
	{
		if ( _clip?.SolvedFrames is { Count: > 0 } frames )
			ApplyPose( frames[Math.Clamp( CurrentFrame, 0, frames.Count - 1 )] );
		UpdateGhost();
	}

	/// <summary>
	/// Applies an arbitrary pose (local transforms in target-skeleton bone order) to the
	/// scene model and the wireframe-skeleton view. Public so the UI smoke gate can drive
	/// the rig's rest pose headlessly and assert engine-space bone positions.
	/// </summary>
	public void ApplyPose( XForm[] locals )
	{
		if ( locals is null )
			return;

		var skeleton = _rig.Skeleton;
		var count = Math.Min( locals.Length, skeleton.Count );
		for ( var i = 0; i < count; i++ )
		{
			var parent = skeleton[i].ParentIndex;
			_worldScratch[i] = parent < 0 ? locals[i] : XForm.Compose( _worldScratch[parent], locals[i] );
		}

		if ( _sceneModel.IsValid() )
		{
			for ( var i = 0; i < count; i++ )
			{
				var modelBone = _rigToModelBone[i];
				if ( modelBone < 0 )
					continue;

				// Keep the bind's SCALE: the rig carries no scale, but scaled-bind rigs
				// (cartoon exports) need it or the skin collapses around those bones.
				var engineTransform = RigWorldToEngine( _worldScratch[i] );
				engineTransform.Scale = _modelBindByRig[i].Scale;
				_renderPose[i] = engineTransform;
				_sceneModel.SetBoneOverride( modelBone, engineTransform );
			}

			// Flush the overrides into the model's bone state NOW: SetBoneOverride only takes
			// effect on the model's next Update, so without this the rendered pose lags one
			// frame and headless readers (the UI smoke gate's GetModelBoneTransform asserts)
			// would read the previous pose. Verified empirically: before the flush the gate read
			// the bind pose back; with it, the overridden pose.
			_sceneModel.Update( 0f );
			_renderPoseCount = count;
			ApplyRenderPose();
		}

		UpdateSkeletonLines( count );
	}

	// Model constraints run after physics-style bone overrides. Pin the final render
	// transforms to the same solved pose written into the armature-only FBX. Cache
	// transforms independently of FK scratch, which source-ghost alignment also uses.
	void ApplyRenderPose()
	{
		if ( !_sceneModel.IsValid() || _renderPose is null ) return;
		for ( var i = 0; i < _renderPoseCount; i++ )
		{
			var bone = _rigToModelBone[i];
			if ( bone >= 0 ) _sceneModel.SetBoneWorldTransform( bone, _sceneModel.Transform.ToWorld( _renderPose[i] ) );
		}
	}

	/// <summary>Redraws the wireframe-skeleton view from the freshly FK'd pose: one
	/// polyline object per skeleton chain, engine-space, and moves the model-less camera
	/// anchor with the posed bounds (fixed rest-pose zoom so it doesn't pulse).</summary>
	void UpdateSkeletonLines( int count )
	{
		_skeletonChains ??= BuildChains( i => _rig.Skeleton[i].ParentIndex, _rig.Skeleton.Count );
		EnsureChainObjects( _skeletonChainObjects, _skeletonChains.Length );
		foreach ( var chain in _skeletonChainObjects )
			chain.RenderingEnabled = SkeletonOnly;
		if ( !SkeletonOnly )
			return;

		SkeletonLineCount = 0;
		var boneWidth = OverlayLineWidth;
		// Culling bounds cover EVERYTHING drawn; the camera center tracks only the
		// mapped character bones (stray far-away FBX nodes must not drag it off).
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		var cameraMin = new Vector3( float.MaxValue );
		var cameraMax = new Vector3( float.MinValue );
		foreach ( var index in _cameraBones )
		{
			if ( index >= count )
				continue;
			var cameraPos = RigWorldToEngine( _worldScratch[index] ).Position;
			cameraMin = Vector3.Min( cameraMin, cameraPos );
			cameraMax = Vector3.Max( cameraMax, cameraPos );
		}

		var engine = new Vector3[count];
		for ( var i = 0; i < count; i++ )
		{
			engine[i] = RigWorldToEngine( _worldScratch[i] ).Position;
			min = Vector3.Min( min, engine[i] );
			max = Vector3.Max( max, engine[i] );
		}

		var points = new List<Vector3>();
		for ( var c = 0; c < _skeletonChains.Length; c++ )
		{
			points.Clear();
			foreach ( var index in _skeletonChains[c] )
			{
				if ( index < count )
					points.Add( engine[index] );
			}
			DrawChain( _skeletonChainObjects[c], points, SkeletonBoneColor, boneWidth );
			FitBounds( _skeletonChainObjects[c], min, max );
			SkeletonLineCount += Math.Max( points.Count - 1, 0 );
		}

		if ( count > 0 )
		{
			if ( cameraMin.x <= cameraMax.x )
				_skeletonCenter = (cameraMin + cameraMax) * 0.5f;
			SkeletonDebug = $"drawn=[{min} .. {max}] cam=[{cameraMin} .. {cameraMax}] "
				+ $"center={_skeletonCenter} radius={_skeletonRadius:0.#} width={boneWidth:0.##} "
				+ $"count={count} chains={_skeletonChains.Length}";
		}
	}

	/// <summary>
	/// Rig-space world transform → engine model space: optional Y-up→Z-up basis rotation
	/// (position (x, y, z) → (x, −z, y); rotation q → q_R ⊗ q), then cm→inch position
	/// scaling. Identity + scale for engine-space rigs.
	/// </summary>
	Transform RigWorldToEngine( in XForm w )
	{
		var pos = w.Pos;
		var rot = w.Rot;
		if ( _convertYUpToZUp )
		{
			pos = new System.Numerics.Vector3( pos.X, -pos.Z, pos.Y );
			rot = System.Numerics.Quaternion.Normalize( YUpToZUp * rot );
		}

		return new Transform(
			new Vector3( pos.X, pos.Y, pos.Z ) * _positionScale,
			new Rotation( rot.X, rot.Y, rot.Z, rot.W ) );
	}

	/// <summary>
	/// Renders the preview camera to an offscreen bitmap and counts the pixels matching
	/// <paramref name="predicate"/>. Used by the UI smoke gate to prove the overlays
	/// actually REACH THE SCREEN — line counts alone cannot see a missing line material
	/// (user report: skeleton view and source ghost rendered as a single purple blob).
	/// </summary>
	public int CountRenderedPixels( Func<Color, bool> predicate, int size = 384 )
	{
		if ( !Camera.IsValid() )
			return 0;

		// Fixed small tick: headless probe calls arrive with arbitrarily large real-time
		// deltas, which destabilizes procedural bones (citizen jiggle exploded into
		// stretched geometry in a gate render).
		Scene.EditorTick( RealTime.Now, 0.016f );
		ApplyRenderPose();
		UpdateCamera();

		var bitmap = new Bitmap( size, size );
		using ( GizmoInstance.Push() )
		{
			DrawTargetBones();
			Camera.RenderToBitmap( bitmap );
		}

		var count = 0;
		foreach ( var pixel in bitmap.GetPixels() )
		{
			if ( predicate( pixel ) )
				count++;
		}
		return count;
	}

	/// <summary>Renders the preview camera to a PNG (gate diagnostics — lets the harness
	/// LOOK at what the user sees instead of inferring from pixel counts). Null when the
	/// camera is unavailable.</summary>
	public byte[] RenderToPng( int size = 512 )
	{
		if ( !Camera.IsValid() )
			return null;

		Scene.EditorTick( RealTime.Now, 0.016f ); // fixed tick - see CountRenderedPixels
		ApplyRenderPose();
		UpdateCamera();

		var bitmap = new Bitmap( size, size );
		using ( GizmoInstance.Push() )
		{
			DrawTargetBones();
			Camera.RenderToBitmap( bitmap );
		}
		return bitmap.ToPng();
	}

	/// <summary>Renders a COMPILED model playing one of its own sequences (the ModelDoc
	/// ground truth) through this widget's camera — gate diagnostics for when the
	/// pose-override preview and the compiled result disagree. The widget's own model is
	/// hidden for the shot. Null when the model/camera is unavailable.</summary>
	public byte[] RenderCompiledSequencePng(
		string modelPath, string sequenceName, float fraction, int size = 512 )
	{
		if ( !Camera.IsValid() || !_sceneModel.IsValid() )
		{
			Log.Info( $"[sbox-humanoid-mocap] compiled render unavailable: camera={Camera.IsValid()} model={_sceneModel.IsValid()}" );
			return null;
		}
		var compiled = Model.Load( modelPath );
		if ( compiled is null || compiled.IsError )
		{
			Log.Info( $"[sbox-humanoid-mocap] compiled render unavailable: load '{modelPath}' null={compiled is null} error={compiled?.IsError}" );
			return null;
		}

		var sequenceModel = new SceneModel( _sceneModel.World, compiled, Transform.Zero );
		try
		{
			sequenceModel.UseAnimGraph = false;
			sequenceModel.CurrentSequence.Name = sequenceName;
			sequenceModel.CurrentSequence.Time =
				sequenceModel.CurrentSequence.Duration * Math.Clamp( fraction, 0f, 1f );
			sequenceModel.Update( 0.016f );
			_sceneModel.RenderingEnabled = false;

			// RenderToPng ticks the scene with WALL-CLOCK time - re-pin the sequence
			// time right before the draw so the render can't sample a different frame
			// than the probes (diagnosed: bone queries read the correct pose while the
			// rendered skin showed a ~90° different one).
			Scene.EditorTick( RealTime.Now, 0.016f );
			sequenceModel.CurrentSequence.Time =
				sequenceModel.CurrentSequence.Duration * Math.Clamp( fraction, 0f, 1f );
			sequenceModel.Update( 0.001f );
			var png = RenderToPng( size );

			// Full bone-state dump of the RENDER-world model (gate forensics: this world
			// evaluates sequences differently from a bare SceneWorld; the dump names
			// exactly which bones diverge). Written to the given folder when armed.
			var dumpDir = Environment.GetEnvironmentVariable( "HM_RENDER_MODEL_DUMP_DIR" );
			if ( !string.IsNullOrEmpty( dumpDir ) && sequenceModel.Model?.Bones?.AllBones is { } dumpBones )
			{
				try
				{
					var lines = dumpBones.Select( b =>
					{
						var t = sequenceModel.GetBoneWorldTransform( b.Name );
						return $"{{\"name\":\"{b.Name}\",\"rot\":[{t.Rotation.x},{t.Rotation.y},{t.Rotation.z},{t.Rotation.w}],"
							+ $"\"pos\":[{t.Position.x},{t.Position.y},{t.Position.z}]}}";
					} );
					System.IO.File.WriteAllText(
						System.IO.Path.Combine( dumpDir, $"render_model_bones_{(int)(fraction * 100)}.json" ),
						"[" + string.Join( ",\n", lines ) + "]" );
				}
				catch ( Exception e )
				{
					Log.Info( $"[sbox-humanoid-mocap] render-model dump failed: {e.Message}" );
				}
			}
			return png;
		}
		finally
		{
			_sceneModel.RenderingEnabled = true;
			sequenceModel.Delete();
		}
	}

	/// <summary>Renders a close-up of one bone (gate diagnostics for hand/finger quality
	/// reports — full-body renders are too small to judge a wrist). The camera frames a
	/// sphere of <paramref name="radius"/> around the CURRENTLY POSED bone. Null when the
	/// model or bone is unavailable.</summary>
	public byte[] RenderBoneCloseUpPng( string boneName, float radius, int size = 512 )
	{
		if ( !Camera.IsValid() )
			return null;
		Scene.EditorTick( RealTime.Now, 0.016f );
		ApplyRenderPose();
		var bone = GetModelBoneTransform( boneName );
		if ( bone is null ) return null;

		var center = bone.Value.Position;
		var distance = MathX.SphereCameraDistance( radius, Camera.FieldOfView ) * 1.05f;
		var yawRad = MathX.DegreeToRadian( _yaw );
		var dir = new Vector3( MathF.Cos( yawRad ), MathF.Sin( yawRad ), 0.25f ).Normal;
		Camera.WorldPosition = center + dir * distance;
		Camera.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );

		var bitmap = new Bitmap( size, size );
		Camera.RenderToBitmap( bitmap );
		return bitmap.ToPng();
	}

	/// <summary>
	/// Per-bone disagreement between the RIG's rest locals and the compiled model's bind
	/// locals (worst offenders first). The DMX skeleton must match the engine's own import
	/// of the same file — a mismatch (PreRotation/pivot convention differences) plays as a
	/// CONSTANT offset on every frame: twisted wrists, fingers off their sockets.
	/// Non-root local rotations are convention-independent and local positions differ only
	/// by the uniform unit scale, so they compare directly.
	/// </summary>
	public string BindMismatchReport( int top = 12 )
	{
		if ( !_sceneModel.IsValid() )
			return "(no model)";
		var bones = _sceneModel.Model?.Bones;
		if ( bones is null )
			return "(no bones)";

		var skeleton = _rig.Skeleton;
		var entries = new List<(float Score, string Line)>();
		for ( var i = 0; i < skeleton.Count; i++ )
		{
			if ( skeleton[i].ParentIndex < 0 )
				continue; // the root's local carries the axis conversion - not comparable
			var bone = bones.GetBone( skeleton[i].Name );
			if ( bone?.Parent is null )
				continue;

			// Model bind local from the MODEL-space bind transforms.
			var world = bone.LocalTransform;
			var parent = bone.Parent.LocalTransform;
			var modelLocal = new XForm(
				new System.Numerics.Vector3( world.Position.x, world.Position.y, world.Position.z ),
				new System.Numerics.Quaternion( world.Rotation.x, world.Rotation.y, world.Rotation.z, world.Rotation.w ) );
			var parentLocal = new XForm(
				new System.Numerics.Vector3( parent.Position.x, parent.Position.y, parent.Position.z ),
				new System.Numerics.Quaternion( parent.Rotation.x, parent.Rotation.y, parent.Rotation.z, parent.Rotation.w ) );
			var bind = XForm.ToLocal( parentLocal, modelLocal );

			var rig = skeleton[i].RestLocal;
			var posDelta = (rig.Pos * _positionScale - bind.Pos).Length();
			var dot = MathF.Min( MathF.Abs( System.Numerics.Quaternion.Dot(
				System.Numerics.Quaternion.Normalize( rig.Rot ),
				System.Numerics.Quaternion.Normalize( bind.Rot ) ) ), 1f );
			var angle = 2f * MathF.Acos( dot ) * 180f / MathF.PI;
			var score = angle + posDelta * 10f;
			if ( angle > 2f || posDelta > 0.25f )
				entries.Add( (score, $"{skeleton[i].Name}: rot {angle:0.#}deg pos {posDelta:0.##}in") );
		}

		if ( entries.Count == 0 )
			return "(rig matches the compiled bind)";
		return $"{entries.Count} bones differ; worst: "
			+ string.Join( "; ", entries.OrderByDescending( e => e.Score ).Take( top ).Select( e => e.Line ) );
	}

	/// <summary>Names and positions of model bones sitting farther than
	/// <paramref name="radius"/> from the given anchor bone — gate diagnostics for
	/// stretched-mesh reports (skinning webs point at bones left at wild transforms).</summary>
	public string WildBoneReport( string anchorBoneName, float radius )
	{
		if ( !_sceneModel.IsValid() )
			return "(no model)";
		var anchor = GetModelBoneTransform( anchorBoneName )?.Position ?? Vector3.Zero;
		var wild = new List<string>();
		var bones = _sceneModel.Model?.Bones?.AllBones;
		if ( bones is null )
			return "(no bones)";
		foreach ( var bone in bones )
		{
			var position = _sceneModel.GetBoneWorldTransform( bone.Index ).Position;
			if ( position.Distance( anchor ) > radius )
				wild.Add( $"{bone.Name}@{position}" );
		}
		return wild.Count == 0 ? "(none)" : string.Join( "; ", wild );
	}

	/// <summary>World transform of a model bone (by name) as currently posed; null when the
	/// model is missing or has no such bone. Used by the UI smoke gate's pose assertions.</summary>
	public Transform? GetModelBoneTransform( string boneName )
	{
		if ( !_sceneModel.IsValid() )
			return null;
		var bone = _sceneModel.Model?.Bones?.GetBone( boneName );
		if ( bone is null )
			return null;
		return _sceneModel.GetBoneWorldTransform( bone.Index );
	}

	// ============================================================================ source ghost

	/// <summary>True when source-ghost data was installed (drives the dialog's toggle).</summary>
	public bool HasSourceGhost => _ghostClip is not null;

	/// <summary>Show the source clip as a semi-transparent stick-skeleton overlay (off by
	/// default; the preview dialog's "Show source" toggle drives this).</summary>
	public bool ShowSourceGhost
	{
		get => _showSourceGhost;
		set
		{
			_showSourceGhost = value;
			foreach ( var chain in _ghostChainObjects )
				chain.RenderingEnabled = value && HasSourceGhost;
		}
	}

	/// <summary>Line segments the ghost drew on its last update (bones + joint ticks).
	/// Exposed for the UI smoke gate's headless assertion.</summary>
	public int GhostLineCount { get; private set; }

	/// <summary>
	/// Installs the SOURCE clip for the ghost overlay: <paramref name="skeleton"/> /
	/// <paramref name="clip"/> are the imported source scene's (cm, native axes);
	/// <paramref name="mapping"/> locates hips/legs/shoulders for the alignment. The ghost is
	/// root-aligned to the target (the source's hips ground-projection is moved onto the
	/// target's hips ground-projection at frame 0) and scaled by the hip-height ratio so the
	/// two rigs compare at the same size. No-op (ghost unavailable) when the mapping lacks
	/// the bones the character frame needs.
	/// </summary>
	public void SetSourceGhost( SourceSkeleton skeleton, SourceClip clip, MappingResult mapping )
	{
		if ( skeleton is null || clip is null || clip.Frames.Count == 0 || mapping is null )
			return;

		int? SrcBone( BoneRole role )
			=> mapping.RoleToBone.TryGetValue( role, out var index ) ? index : null;

		// The source basis comes from the clip's FRAME 0 pose, not the rest skeleton:
		// stick-bind rigs (NVIDIA SOMA - every rest bone along one axis) have no usable
		// rest geometry, and a near-degenerate basis normalizes into garbage axes that
		// smear the ghost into long streaks (user report). Frame 0 of a real clip is a
		// real pose on every rig.
		var scratch = new XForm[skeleton.Count];
		var hipsIndex = SrcBone( BoneRole.Hips ) ?? 0;
		var hips0 = FkPosition( skeleton, clip.Frames[0], scratch, hipsIndex );
		if ( !TryCharacterBasis( SrcBone, scratch, out _srcLat, out _srcUp, out _srcFwd,
			out _srcHipHeight, out var srcGround ) )
		{
			Log.Info( "[sbox-humanoid-mocap] preview: source ghost unavailable (mapping lacks the hips/legs/shoulder bones the alignment needs)." );
			return;
		}

		_ghostSkeleton = skeleton;
		_ghostClip = clip;
		_ghostScratch = scratch;
		_ghostAlignedClip = null;

		// Anchor = the source hips' ground projection at frame 0 (clips that start offset
		// from the origin - BVH mocap especially - must not push the ghost away).
		_srcAnchor = hips0 + _srcUp * (srcGround - VecN.Dot( hips0, _srcUp ));

		_ghostChains = BuildChains( i => skeleton[i].ParentIndex, skeleton.Count );
		EnsureChainObjects( _ghostChainObjects, _ghostChains.Length );
		foreach ( var chain in _ghostChainObjects )
			chain.RenderingEnabled = _showSourceGhost;
	}

	/// <summary>Target-side alignment: anchor on the TARGET's hips ground-projection at
	/// frame 0 of the previewed clip, hip-height-ratio scale. Lazy - both clips must be
	/// known; recomputed when the previewed clip changes.</summary>
	bool EnsureGhostAlignment()
	{
		if ( _clip?.SolvedFrames is not { Count: > 0 } frames )
			return false;
		if ( ReferenceEquals( _ghostAlignedClip, _clip ) )
			return true;

		int? TgtBone( BoneRole role ) => _rig.BoneForRole( role );

		if ( !TryCharacterBasis( TgtBone, _rig.Skeleton.RestWorld, out _tgtLat, out _tgtUp, out _tgtFwd,
			out var tgtHipHeight, out var tgtGround ) )
			return false;

		var hipsIndex = _rig.BoneForRole( BoneRole.Hips ) ?? 0;
		var hips0 = FkPosition( _rig.Skeleton, frames[0], _worldScratch, hipsIndex );
		_tgtAnchor = hips0 + _tgtUp * (tgtGround - VecN.Dot( hips0, _tgtUp ));
		_ghostScale = _srcHipHeight > 1e-3f ? tgtHipHeight / _srcHipHeight : 1f;
		_ghostAlignedClip = _clip;
		return true;
	}

	/// <summary>Redraws the ghost at the scrub position: the source frame is picked by
	/// NORMALIZED time (source and target clips may differ in frame count), FK'd, mapped
	/// through the character-basis alignment into target rig space and drawn as parent→child
	/// line segments plus small joint ticks.</summary>
	void UpdateGhost()
	{
		if ( _ghostChains is null || _ghostClip is null )
			return;

		var visible = _showSourceGhost && EnsureGhostAlignment();
		foreach ( var chain in _ghostChainObjects )
			chain.RenderingEnabled = visible;
		if ( !visible )
			return;

		// Normalized-time sync (fence-post frames: first→first, last→last).
		var ghostFrames = _ghostClip.Frames;
		var t = FrameCount > 1 ? CurrentFrame / (float)(FrameCount - 1) : 0f;
		var gi = Math.Clamp( (int)MathF.Round( t * (ghostFrames.Count - 1) ), 0, ghostFrames.Count - 1 );

		var skeleton = _ghostSkeleton;
		var locals = ghostFrames[gi];
		var count = Math.Min( locals.Length, skeleton.Count );
		for ( var i = 0; i < count; i++ )
		{
			var parent = skeleton[i].ParentIndex;
			_ghostScratch[i] = parent < 0 ? locals[i] : XForm.Compose( _ghostScratch[parent], locals[i] );
		}

		GhostLineCount = 0;
		var boneWidth = OverlayLineWidth * 0.8f;
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		var engine = new Vector3[count];
		for ( var i = 0; i < count; i++ )
		{
			engine[i] = GhostToEngine( _ghostScratch[i].Pos );
			min = Vector3.Min( min, engine[i] );
			max = Vector3.Max( max, engine[i] );
		}

		var points = new List<Vector3>();
		for ( var c = 0; c < _ghostChains.Length; c++ )
		{
			points.Clear();
			foreach ( var index in _ghostChains[c] )
			{
				if ( index < count )
					points.Add( engine[index] );
			}
			DrawChain( _ghostChainObjects[c], points, GhostBoneColor, boneWidth );
			FitBounds( _ghostChainObjects[c], min, max );
			GhostLineCount += Math.Max( points.Count - 1, 0 );
		}
	}

	/// <summary>Source world position (cm, native axes) → engine space: express the offset
	/// from the source anchor in the source character basis, re-emit it in the target's
	/// character basis at the target anchor (hip-ratio scaled), then run the widget's normal
	/// rig→engine conversion.</summary>
	Vector3 GhostToEngine( VecN p )
	{
		var d = p - _srcAnchor;
		var a = new VecN( VecN.Dot( d, _srcLat ), VecN.Dot( d, _srcUp ), VecN.Dot( d, _srcFwd ) ) * _ghostScale;
		var rigPos = _tgtAnchor + _tgtLat * a.X + _tgtUp * a.Y + _tgtFwd * a.Z;
		return RigWorldToEngine( new XForm( rigPos, System.Numerics.Quaternion.Identity ) ).Position;
	}

	/// <summary>FK of one frame down to every bone, returning <paramref name="boneIndex"/>'s
	/// world position (scratch is filled as a side effect).</summary>
	static VecN FkPosition( SourceSkeleton skeleton, XForm[] locals, XForm[] scratch, int boneIndex )
	{
		var count = Math.Min( locals.Length, skeleton.Count );
		for ( var i = 0; i < count; i++ )
		{
			var parent = skeleton[i].ParentIndex;
			scratch[i] = parent < 0 ? locals[i] : XForm.Compose( scratch[parent], locals[i] );
		}
		return scratch[Math.Clamp( boneIndex, 0, count - 1 )].Pos;
	}

	/// <summary>
	/// Character-level basis of a rig from rest GEOMETRY (the same definition the solver's
	/// CharacterFrame uses, duplicated here because that type is internal to the core
	/// assembly): up = mid-hips→mid-shoulders, lateral = left-positive hip line ⊥ up,
	/// forward = cross(lateral, up); hip height/ground measured along up over the mapped
	/// feet (all bones when no feet are mapped). False when the rig lacks the needed bones.
	/// </summary>
	static bool TryCharacterBasis(
		Func<BoneRole, int?> boneOf, IReadOnlyList<XForm> restWorld,
		out VecN lateral, out VecN up, out VecN forward, out float hipHeight, out float ground )
	{
		lateral = up = forward = default;
		hipHeight = ground = 0f;

		VecN? Pos( BoneRole role ) => boneOf( role ) is { } index && index >= 0 && index < restWorld.Count
			? restWorld[index].Pos : null;
		VecN? Mid( VecN? a, VecN? b ) => a is not null && b is not null ? (a.Value + b.Value) * 0.5f : null;

		var legL = Pos( BoneRole.UpperLegL );
		var legR = Pos( BoneRole.UpperLegR );
		if ( legL is null || legR is null )
			return false;
		var midHips = (legL.Value + legR.Value) * 0.5f;

		var midShoulders = Mid( Pos( BoneRole.UpperArmL ), Pos( BoneRole.UpperArmR ) )
			?? Mid( Pos( BoneRole.ClavicleL ), Pos( BoneRole.ClavicleR ) )
			?? Pos( BoneRole.Neck );
		if ( midShoulders is null )
			return false;

		var upRaw = midShoulders.Value - midHips;
		if ( upRaw.LengthSquared() < 1e-8f )
			return false;
		up = VecN.Normalize( upRaw );

		var acrossHips = legL.Value - legR.Value;
		var latRaw = acrossHips - up * VecN.Dot( acrossHips, up );
		if ( latRaw.LengthSquared() < 1e-8f )
			return false;
		lateral = VecN.Normalize( latRaw );
		forward = VecN.Normalize( VecN.Cross( lateral, up ) );

		ground = float.PositiveInfinity;
		foreach ( var role in new[] { BoneRole.FootL, BoneRole.FootR, BoneRole.ToeL, BoneRole.ToeR } )
		{
			if ( Pos( role ) is { } foot )
				ground = MathF.Min( ground, VecN.Dot( foot, up ) );
		}
		if ( float.IsPositiveInfinity( ground ) )
		{
			foreach ( var world in restWorld )
				ground = MathF.Min( ground, VecN.Dot( world.Pos, up ) );
		}

		hipHeight = VecN.Dot( midHips, up ) - ground;
		return hipHeight > 1e-3f;
	}

	void UpdateCamera()
	{
		if ( !Camera.IsValid() )
			return;
		Camera.FieldOfView = FirstPerson ? Math.Clamp( ViewmodelFov, 35, 120 ) : 45;
		Camera.ZNear = FirstPerson ? Math.Clamp( ViewmodelNearClipCm, .1f, 50 ) * .3937008f : 1f;
		if ( FirstPerson )
		{
			var head = _rig.BoneForRole( BoneRole.Head );
			var position = head is int h && _worldScratch is not null
				? RigWorldToEngine( _worldScratch[h] ).Position : new Vector3( 0, 0, 64 );
			var forward = Vector3.Forward;
			var up = Vector3.Up;
			if ( _worldScratch is not null && TryCharacterBasis( _rig.BoneForRole, _worldScratch,
				out _, out var rigUp, out var rigForward, out _, out _ ) )
			{
				var origin = RigWorldToEngine( XForm.Identity ).Position;
				forward = (RigWorldToEngine( new XForm( rigForward, System.Numerics.Quaternion.Identity ) ).Position - origin).Normal;
				up = (RigWorldToEngine( new XForm( rigUp, System.Numerics.Quaternion.Identity ) ).Position - origin).Normal;
			}
			if ( CaptureView is { } capture )
			{
				var cameraRotation = YUpToZUp * System.Numerics.Quaternion.CreateFromYawPitchRoll(
					capture.CaptureCameraYawDegrees * MathF.PI / 180, capture.CaptureCameraPitchDegrees * MathF.PI / 180, 0 );
				var p = VecN.Transform( capture.CaptureCameraPosition * 39.3700787f, YUpToZUp );
				var f = VecN.Transform( -VecN.UnitZ, cameraRotation );
				var u = VecN.Transform( VecN.UnitY, cameraRotation );
				position = new Vector3( p.X, p.Y, p.Z );
				forward = new Vector3( f.X, f.Y, f.Z ); up = new Vector3( u.X, u.Y, u.Z );
			}
			Camera.WorldPosition = CaptureView is null ? position + forward * 3 : position;
			forward = _handViewDirection ?? forward;
			var yaw = _lookYaw * MathF.PI / 180f;
			var right = Vector3.Cross(forward,up).Normal;
			forward = (forward*MathF.Cos(yaw)+right*MathF.Sin(yaw)).Normal;
			var pitch = Math.Clamp( ViewmodelPitch + _lookPitch, -70, 70 ) * MathF.PI / 180f;
			Camera.WorldRotation = Rotation.LookAt( (forward * MathF.Cos( pitch ) - up * MathF.Sin( pitch )).Normal, up );
			return;
		}

		// Frame the POSED character, not the model asset: Model.Bounds is the bind-pose box
		// anchored at the scene origin, so clips that start offset or travel (BVH mocap
		// especially) would walk out of a frame built from it. SceneObject.Bounds follows
		// the current pose; the bind-pose SIZE is kept for the zoom so it doesn't pulse
		// with the animation (arms out ≠ zoom out). In the wireframe-skeleton view (and
		// for model-less targets) the same policy runs off the FK'd skeleton instead.
		var useModelBounds = _sceneModel.IsValid() && !SkeletonOnly;
		var center = useModelBounds ? _sceneModel.Bounds.Center : _skeletonCenter;
		var radius = useModelBounds
			? MathF.Max( _sceneModel.Model.Bounds.Size.Length * 0.5f, 8f )
			: _skeletonRadius;
		var distance = MathX.SphereCameraDistance( radius, Camera.FieldOfView ) * 1.05f;

		var yawRad = MathX.DegreeToRadian( _yaw );
		var dir = new Vector3( MathF.Cos( yawRad ), MathF.Sin( yawRad ), 0 );
		Camera.WorldPosition = center + dir * distance;
		Camera.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );
	}

	public float ViewmodelFov { get; set; } = 75;
	public float ViewmodelPitch { get; set; } = 35;
	public float ViewmodelNearClipCm { get; set; } = 15;
	/// <summary>Optional user-assumed capture placement for camera-relative hand clips.
	/// The viewmodel FOV and additional preview pitch remain independently editable.</summary>
	public Motion.TargetCorrectionSettings CaptureView { get; set; }

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		var delta = e.LocalPosition - _lastMouse;
		_lastMouse = e.LocalPosition;
		if ( (e.ButtonState & MouseButtons.Left) != 0 )
		{
			if(FirstPerson){_lookYaw=Math.Clamp(_lookYaw+delta.x*.25f,-100,100);_lookPitch=Math.Clamp(_lookPitch+delta.y*.25f,-70,70);}
			else _yaw -= delta.x * 0.4f;
		}
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		Scene?.Destroy();
		Scene = null;
	}
}
