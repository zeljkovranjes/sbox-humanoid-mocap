#nullable enable annotations

using System;
using System.Collections.Generic;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Formats;
using HumanoidMocap.Mapping;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;

namespace HumanoidMocap;

/// <summary>Which solver retargets a request's clips (design §10).</summary>
public enum SolverKind
{
    /// <summary>The deterministic <see cref="Solve.GeometricSolver"/> (default; better
    /// wherever a role mapping exists).</summary>
    Geometric,

    /// <summary>The experimental skeleton-agnostic deep-learning solver
    /// (<see cref="Dl.DlSolver"/>, SAME pretrained checkpoint) — the no-profile fallback.
    /// Requires <see cref="RetargetTargetSpec.DlWeights"/>; ignores per-role mapping
    /// (only hips/alignment heuristics consult it) and leaves fingers at rest.</summary>
    DeepLearning,
}

/// <summary>
/// One source animation file to retarget (engine-agnostic: bytes in, no file IO). Every
/// request runs its OWN profile detection, so a single batch may mix Mixamo + ActorCore +
/// BVH sources — unless <see cref="MappingOverride"/> supplies a mapping explicitly.
/// </summary>
public sealed class RetargetRequest
{
    /// <summary>Final target-proportion correction, before IK helper baking and export.</summary>
    public Motion.TargetCorrectionSettings? MocapCorrections { get; init; }
    /// <summary>Solver choice for this request's clips. <see cref="SolverKind.DeepLearning"/>
    /// requires the batch's <see cref="RetargetTargetSpec.DlWeights"/> to be set; the
    /// conversion fails per-clip with a clear error otherwise.</summary>
    public SolverKind Solver { get; init; } = SolverKind.Geometric;

    /// <summary>Raw bytes of the source file (.fbx, .bvh, .glb, .gltf, .vrm, .anm, .an5 or .cba).</summary>
    public required byte[] SourceData { get; init; }

    /// <summary>
    /// Source file name (used for the report and DMX provenance). The extension drives the
    /// format choice (<c>.fbx</c> / <c>.bvh</c> / <c>.glb</c> / <c>.gltf</c> / <c>.vrm</c> —
    /// a VRM is a glTF container whose authored humanoid bone map becomes the mapping — /
    /// <c>.anm</c> / <c>.an5</c> RenderWare animations and <c>.cba</c> EA ANT packages,
    /// which additionally need <see cref="SkeletonData"/>); when the extension is unknown the content is sniffed
    /// (FBX binary magic / "FBXHeaderExtension" / BVH "HIERARCHY" / GLB 'glTF' magic /
    /// glTF JSON / RenderWare 0x1B animation chunk / ANTSTM3b).
    /// </summary>
    public required string SourceFileName { get; init; }

    /// <summary>
    /// Raw bytes of a companion SKELETON file for formats whose animation files carry no
    /// skeleton of their own: RenderWare <c>.anm</c>/<c>.an5</c> sources require the
    /// character model's <c>.dff</c>; EA ANT <c>.cba</c> sources require an ordered joint-table
    /// JSON (either the root array or an extracted <c>{ "joints": [...] }</c> wrapper).
    /// Callers resolve the companion file; the facade does no file IO. Ignored by
    /// self-contained formats. A request without its required companion fails clearly.
    /// </summary>
    public byte[]? SkeletonData { get; init; }

    /// <summary>
    /// Optional external-buffer reader for plain <c>.gltf</c> input. IO-owning callers
    /// resolve the URI relative to the document; GLB and data-URI glTF leave this null.
    /// </summary>
    public Func<string, byte[]>? ExternalBufferResolver { get; init; }

    /// <summary>
    /// Caller-supplied identity of this request, echoed verbatim on every produced
    /// <see cref="ClipResult.SourceId"/> so callers can join results back to their own
    /// entries unambiguously (e.g. the editor window passes the FULL file path here, since
    /// two files in different folders may share the same <see cref="SourceFileName"/>).
    /// Null = <see cref="SourceFileName"/>.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// Import sample rate the source clips are resampled to (BVH native frames / FBX curves
    /// are evaluated on this grid). Null = the importer default (30 fps).
    /// </summary>
    public float? SampleFps { get; init; }

    /// <summary>
    /// Restricts the conversion to ONE take of the source file (0-based index into the
    /// imported scene's clips). Null = convert all takes. Out of range fails the request's
    /// clip result with a clear error (the batch continues). UI listings that expand a
    /// multi-take file into one entry per take submit one request per selected take.
    /// When <see cref="ClipDefinitions"/> is set this index addresses the DEFINITIONS
    /// instead (each definition is what a UI row represents then).
    /// </summary>
    public int? TakeIndex { get; init; }

    /// <summary>
    /// Optional external clip definitions, parsed from a Unity <c>&lt;file&gt;.fbx.meta</c>
    /// sidecar (<see cref="UnityMeta.ParseClipAnimations"/>): Unity animation packs ship FBX
    /// files whose clips are sub-ranges of ONE source timeline. When set (non-empty), the
    /// conversion produces one output clip per definition instead of one per take: the
    /// definition's take (matched by <see cref="ExternalClipDef.TakeName"/>, falling back to
    /// the file's first take) is sliced to the definition's native-frame range
    /// (<see cref="UnityMeta.Slice"/>), named <see cref="ExternalClipDef.Name"/> (sanitized
    /// like take names, collision-suffixed across the batch) and looped per
    /// <see cref="ExternalClipDef.Loop"/> unless <see cref="LoopingOverride"/> is set.
    /// <see cref="TakeIndex"/> then indexes INTO this list. Null = no definitions.
    /// </summary>
    public IReadOnlyList<ExternalClipDef>? ClipDefinitions { get; init; }

    /// <summary>
    /// UI-supplied mapping (manual mapping table or a user preset loaded Editor-side).
    /// Null = auto-detect per request: preset profiles via <see cref="ProfileDetector"/>,
    /// then the <see cref="AutoMapper"/> as best-effort fallback.
    /// </summary>
    public MappingResult? MappingOverride { get; init; }

    /// <summary>Solver tunables (hip scales, finger transfer). ClipIndex/ClipName are managed
    /// by the pipeline per take and ignored here. Null = defaults.</summary>
    public SolveOptions? Solve { get; init; }

    /// <summary>
    /// Root-motion handling. <see cref="RootMotionMode.Extract"/> on a target without a
    /// dedicated animated root bone (the s&amp;box rig: pelvis is parentless, root_IK is
    /// IkBaked) leaves the frames untouched and instead sets the ExtractMotion flag on the
    /// clip's vmdl AnimFile entry — Source 2's compile-time extraction replaces the missing
    /// bone-level extraction. <see cref="RootMotionMode.InPlace"/> always operates on the
    /// hips directly.
    /// </summary>
    public RootMotionMode RootMotion { get; init; } = RootMotionMode.Off;

    /// <summary>Run the Kovar foot-plant cleanup pass on the solved frames (default on).</summary>
    public bool FootPlantCleanup { get; init; } = true;

    /// <summary>
    /// Copy the source clip's per-frame LOCAL translations onto same-named target bones
    /// (hips and its ancestors excluded — trajectory stays solver-owned). For SAME-RIG
    /// conversions of authored takes (a target FBX's own embedded animations): the solver
    /// pins every non-hips bone to its rest translation, silently dropping a Biped take's
    /// animated spine/thigh translations (~19cm of authored body sway on a death fall).
    /// Meaningless across different rigs — leave off (default) for real retargets.
    /// </summary>
    public bool PreserveSourceTranslations { get; init; }

    /// <summary>
    /// Optional arm end-effector IK pass pulling the wrists onto limb-length-normalized
    /// source hand positions. Default OFF: the geometric solver already matches anatomical
    /// directions, so arm IK only helps reach-critical work (props, contact poses) and can
    /// otherwise disturb elbow styling.
    /// </summary>
    public bool ArmEffectorIk { get; init; }

    /// <summary>
    /// Generate <c>AE_FOOTSTEP</c> AnimEvent nodes on each produced clip's vmdl AnimFile
    /// entry (default OFF). After solving and cleanup, foot-plant intervals are detected on
    /// the SOLVED target clip (<see cref="Cleanup.FootPlant.DetectPlantIntervals"/>); each
    /// plant's start frame is a touchdown and becomes one footstep event, in the exact node
    /// shape the shipped citizen data uses (see <see cref="Target.FootstepEvents"/>).
    /// Skipped (with a report note) when the target rig lacks complete leg chains.
    /// </summary>
    public bool GenerateFootstepEvents { get; init; }

    /// <summary>
    /// Additionally produce a mirrored twin of every converted clip (default OFF), named
    /// <c>&lt;clip&gt;_M</c> (collision-suffixed across the batch as usual). Mirroring runs
    /// in TARGET space on the solved clip (<see cref="Solve.ClipMirror"/>): left/right role
    /// bone channels swap and everything is reflected across the target character's sagittal
    /// plane; IK-baked helper bones are re-baked from the mirrored body afterwards.
    /// </summary>
    public bool CreateMirroredVariant { get; init; }

    /// <summary>
    /// Additionally register an additive (delta) twin of every converted clip in the
    /// generated/augmented vmdl (default OFF), named <c>&lt;clip&gt;_delta</c> (the shipped
    /// citizen naming; collision-suffixed across the batch as usual). The twin is a second
    /// AnimFile entry REUSING the clip's DMX with an <c>AnimSubtract</c> child
    /// (<c>anim_name</c> = the base sequence, <c>frame</c> = 0) — exactly the shipped
    /// <c>IdleLayer_01</c>/<c>IdleLayer_01_delta</c> pattern, where resourcecompiler
    /// subtracts the reference frame at compile time (no frame math happens here). The
    /// resulting <c>_delta</c> sequence is what s&amp;box layered animation additively
    /// blends on top of a base pose.
    /// </summary>
    public bool CreateAdditiveVariant { get; init; }

    /// <summary>Output clip name override; with multiple takes an index suffix is appended.
    /// Null = the source take name.</summary>
    public string? ClipNameOverride { get; init; }

    /// <summary>Force the looping flag on the output sequence(s); null = the source clip's flag.</summary>
    public bool? LoopingOverride { get; init; }
}

/// <summary>
/// Axis/unit convention of a <see cref="RetargetTargetSpec"/>'s rig data — drives the DMX
/// axis-system declaration, foot-plant threshold units, and the editor preview's
/// rig-space → engine-space conversion.
/// </summary>
public enum TargetUpAxis
{
    /// <summary>
    /// The s&amp;box source convention: rig authored in centimeters, Y-up (the shipped
    /// citizen rig, FBX targets). The vmdl's ScaleAndMirror 0.3937 + resourcecompiler's
    /// Y-up→Z-up conversion take it to engine space at compile time. Default.
    /// </summary>
    YUpCm,

    /// <summary>
    /// Engine space already: rig read from a compiled model's <c>Model.Bones</c>
    /// (inches, Z-up). The DMX declares a Z-up axis system so the compiler performs no
    /// further axis conversion.
    /// </summary>
    ZUpEngine,

    /// <summary>
    /// A Z-up rig authored in centimeters: FBX targets whose GlobalSettings declare a Z
    /// up-axis (UE and 3ds Max exports; Maya/Blender exports are Y-up). The DMX declares
    /// Z-up (no compile-time rotation — the mesh source is in the same Z-up space) while
    /// the vmdl's ScaleAndMirror 0.3937 still converts cm→inches. Without this, a Z-up
    /// FBX target compiles lying on its back.
    /// </summary>
    ZUpCm,
}

/// <summary>
/// The conversion target shared by all requests of one <see cref="Retargeter.Convert"/> /
/// <see cref="Retargeter.ConvertBatch"/> call: the rig plus the vmdl generation parameters.
/// </summary>
public sealed class RetargetTargetSpec
{
    /// <summary>The s&amp;box-source → engine-units vmdl scale (cm rigs like the citizen).</summary>
    public const float SboxSourceScale = 0.3937f;

    /// <summary>The committed asset path of the s&amp;box human male model.</summary>
    public const string SboxHumanMalePath = "models/citizen_human/citizen_human_male.vmdl";

    /// <summary>The committed asset path of the classic (4-finger) s&amp;box citizen model.</summary>
    public const string SboxCitizenPath = "models/citizen/citizen.vmdl";

    /// <summary>Target rig (skeleton + bone classes + roles).</summary>
    public required TargetRig Rig { get; set; }

    /// <summary>ModelModifier_ScaleAndMirror scale written into standalone vmdls:
    /// <c>0.3937</c> for cm-authored s&amp;box-source rigs, <c>1.0</c> for engine-unit rigs
    /// (the modifier node is omitted at 1.0).</summary>
    public required float VmdlScale { get; init; }

    /// <summary>base_model_name of generated standalone vmdls (the model that owns the mesh).</summary>
    public string BaseModelPath { get; init; } = "";

    /// <summary>
    /// Assets-relative mesh source file (FBX, GLB, or glTF) embedded in generated
    /// standalone vmdls as a <c>RenderMeshList/RenderMeshFile</c> node. Source-file targets
    /// have no compiled base model to point <see cref="BaseModelPath"/> at — without a mesh
    /// source their standalone vmdl compiles into an EMPTY model (0 bones, 0 sequences) and
    /// playing it does nothing. Callers own copying the file into the project (this type
    /// does no IO); settable so the editor can fill it at convert time once the output
    /// folder is known. Empty (default) = no mesh node.
    /// </summary>
    public string MeshFilePath { get; set; } = "";

    /// <summary>
    /// Import scale of <see cref="MeshFilePath"/> (raw mesh-file units → the target
    /// skeleton's units). resourcecompiler reads mesh files' raw values ignoring their unit
    /// metadata, while the importer normalizes the target skeleton to centimeters — a
    /// meters-authored model therefore needs 100 here (the importer's recorded
    /// source-unit→cm factor) for the mesh to match the animation skeleton.
    /// </summary>
    public float MeshImportScale { get; set; } = 1.0f;

    /// <summary>Source mesh instances to import separately, preserving their skin buffers.
    /// Null or a single name keeps the default whole-file import.</summary>
    public IReadOnlyList<string>? MeshImportNames { get; set; }

    /// <summary>
    /// Material remaps written into generated standalone vmdls as a
    /// MaterialGroupList/DefaultMaterialGroup (bare mesh material reference → assets-relative
    /// vmat path, e.g. <c>"mi_dante_head.vmat" → "animations/retargeted/mi_dante_head.vmat"</c>).
    /// FBX materials carry bare names the compiler cannot resolve as resource paths
    /// ("Trying to load an illegal resource name X.vmat"); this remap table — the same
    /// mechanism the shipped citizen vmdl uses — points them at real files. Null/empty =
    /// no material group node (default).
    /// </summary>
    public IReadOnlyDictionary<string, string>? MaterialRemaps { get; set; }

    /// <summary>
    /// Additional AnimFile entries appended to generated/augmented vmdls verbatim —
    /// the target FBX's OWN embedded animations (an FBX with an animation on it must keep
    /// that animation when new ones are retargeted onto it; the AnimFile references the
    /// FBX directly, exactly like the shipped citizen animation list references its
    /// Citizen@*.fbx files, so the import is lossless). Augmentation skips entries the
    /// existing vmdl already carries (idempotent re-runs). Null/empty = none (default).
    /// </summary>
    public IReadOnlyList<Target.AnimEntry>? ExtraAnimFiles { get; set; }

    /// <summary>default_root_bone_name of the generated AnimationList (also the bone vmdl
    /// ExtractMotion nodes operate on).</summary>
    public string DefaultRootBone { get; set; } = "pelvis";

    /// <summary>
    /// Axis/unit convention of <see cref="Rig"/>. <see cref="TargetUpAxis.YUpCm"/> (default)
    /// for cm Y-up source-space rigs (DMX declares Y-up, compiler converts);
    /// <see cref="TargetUpAxis.ZUpEngine"/> for rigs read from compiled engine models
    /// (DMX declares Z-up so no double conversion happens at compile, and cm-tuned cleanup
    /// thresholds are rescaled to inches).
    /// </summary>
    public TargetUpAxis UpAxis { get; init; } = TargetUpAxis.YUpCm;

    /// <summary>
    /// Raw bytes of the committed SAME weight blob
    /// (<c>Assets/humanoid_mocap/dl/same_v1.weights</c>; callers do the file IO).
    /// Required only when a request selects <see cref="SolverKind.DeepLearning"/>; the
    /// solver instance is built once per batch from these bytes.
    /// </summary>
    public byte[]? DlWeights { get; init; }

    /// <summary>
    /// The shipped s&amp;box default target: rig parsed from the committed
    /// <c>Assets/humanoid_mocap/target_rig_sbox.json</c> text (callers do the file IO),
    /// 0.3937 vmdl scale, citizen human male base model, pelvis root. Pass the committed
    /// SAME weight bytes as <paramref name="dlWeights"/> to enable the deep-learning solver.
    /// </summary>
    public static RetargetTargetSpec SboxDefault(string targetRigJson, byte[]? dlWeights = null) => new()
    {
        Rig = TargetRig.SboxDefault(targetRigJson),
        VmdlScale = SboxSourceScale,
        BaseModelPath = SboxHumanMalePath,
        DefaultRootBone = "pelvis",
        DlWeights = dlWeights,
    };

    /// <summary>
    /// The classic (4-finger) s&amp;box citizen target: rig parsed from the committed
    /// <c>Assets/humanoid_mocap/target_rig_sbox_citizen.json</c> text (callers do the
    /// file IO), 0.3937 vmdl scale, citizen base model, pelvis root, Y-up cm. The rig has no
    /// pinky bones, so pinky roles stay unassigned — the engine's own constraints handle the
    /// pinky at runtime for models that have one. Pass the committed SAME weight bytes as
    /// <paramref name="dlWeights"/> to enable the deep-learning solver.
    /// </summary>
    public static RetargetTargetSpec SboxCitizen(string targetRigJson, byte[]? dlWeights = null) => new()
    {
        Rig = TargetRig.Load(targetRigJson),
        VmdlScale = SboxSourceScale,
        BaseModelPath = SboxCitizenPath,
        DefaultRootBone = "pelvis",
        UpAxis = TargetUpAxis.YUpCm,
        DlWeights = dlWeights,
    };
}

/// <summary>Options for <see cref="Retargeter.ConvertBatch"/> output assembly.</summary>
public sealed class BatchOptions
{
    /// <summary>
    /// When set, the batch additionally augments this existing vmdl text (all successful
    /// clips spliced into its AnimationList via <see cref="VmdlAugmenter"/>) and returns the
    /// result in <see cref="RetargetBatchResult.AugmentedVmdl"/>.
    /// </summary>
    public string? AugmentVmdlText { get; init; }

    /// <summary>Assets-relative folder the DMX files will be written to by the caller; used
    /// to build each AnimFile's <c>source_filename</c>.</summary>
    public string DmxFolderRelative { get; init; } = "animations/retargeted";

    /// <summary>
    /// Animation source paths (<c>source_filename</c> values of the augment target's existing
    /// AnimFile nodes, assets-relative) that the IO-owning caller has determined NO LONGER
    /// EXIST on disk. Stale AnimFile entries referencing them are REMOVED from the augmented
    /// vmdl (reported on <see cref="RetargetBatchResult.Warnings"/>) — one unresolvable
    /// source otherwise fails the ENTIRE vmdl recompile ("Node 'X' resolve failure"), taking
    /// every newly added animation down with it. Entries this batch overwrites (their DMX is
    /// about to be written) are never pruned. The facade itself never touches the filesystem:
    /// callers probe <see cref="Target.VmdlAugmenter.CollectAnimSourcePaths"/> results against
    /// their content roots and pass the missing ones here. Null/empty = keep everything.
    /// </summary>
    public IReadOnlyCollection<string>? MissingAnimSources { get; init; }

    /// <summary>Auto-suffix colliding clip names (<c>_2</c>, <c>_3</c>, …) across the whole
    /// batch (default on). When off, duplicate names are kept as-is.</summary>
    public bool AutoSuffixCollisions { get; init; } = true;

    /// <summary>
    /// After conversion, scan the batch's successful clip names for directional locomotion
    /// families (default OFF): <c>_N</c>/<c>_NE</c>/…/<c>_NW</c> compass suffixes and
    /// <c>_Forward</c>/<c>_Backward</c>(/<c>_Back</c>)/<c>_Left</c>/<c>_Right</c> word forms
    /// sharing a stem. Each complete family (all four cardinals) is grouped under a Folder
    /// node with a <c>2DBlend</c> wired to the citizen <c>move_x</c>/<c>move_y</c> pose
    /// parameters, replicating the shipped citizen locomotion layout (see
    /// <see cref="Target.LocomotionSetDetector"/>); detection results land on
    /// <see cref="RetargetBatchResult.LocomotionSets"/>. Custom (non-citizen) base models
    /// must declare <c>move_x</c>/<c>move_y</c> pose parameters themselves for the blends to
    /// be drivable.
    /// </summary>
    public bool DetectLocomotionSets { get; init; }
}
