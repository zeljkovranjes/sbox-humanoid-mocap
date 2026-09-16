#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HumanoidMocap.Target;

/// <summary>
/// One AnimEvent to attach as a child of a vmdl AnimFile node. The node shape replicates the
/// shipped citizen animation list prefab exactly (all 28 shipped events are
/// <c>AE_FOOTSTEP</c>): <c>_class = "AnimEvent"</c>, <c>event_class</c>, <c>event_frame</c>
/// (integer frame on the clip's grid), and an <c>event_keys</c> object carrying
/// <c>Attachment</c> (<c>"foot_L"</c>/<c>"foot_R"</c>), <c>Foot</c> (STRING <c>"0"</c> =
/// left, <c>"1"</c> = right) and <c>Volume</c> (<c>0.7</c> throughout the shipped data).
/// </summary>
public sealed class AnimEventEntry
{
    /// <summary>Event class name (e.g. <c>AE_FOOTSTEP</c>).</summary>
    public string EventClass { get; set; } = "";

    /// <summary>Frame the event fires on (the clip's own frame grid).</summary>
    public int Frame { get; set; }

    /// <summary>event_keys <c>Attachment</c> value (<c>"foot_L"</c>/<c>"foot_R"</c> in the
    /// shipped footstep data); null omits the key.</summary>
    public string? Attachment { get; set; }

    /// <summary>event_keys <c>Foot</c> value — the shipped data encodes the side as a STRING:
    /// <c>"0"</c> = left, <c>"1"</c> = right; null omits the key.</summary>
    public string? Foot { get; set; }

    /// <summary>event_keys <c>Volume</c> value (<c>0.7</c> in all shipped footstep events);
    /// null omits the key.</summary>
    public double? Volume { get; set; }

    /// <summary>
    /// Additional string event_keys emitted after the fixed keys, in insertion order.
    /// (W3b addition, southpaw project: punch clips carry semantic contact tags such as
    /// <c>Zone</c>/<c>Family</c>/<c>Hand</c> on their contact-frame AnimEvent; the shape
    /// stays the shipped citizen AnimEvent node shape, only extra keys appear.) Null or
    /// empty adds nothing, so existing callers are unaffected.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraKeys { get; set; }
}

/// <summary>One animation to register in a vmdl AnimationList.</summary>
public sealed class AnimEntry
{
    /// <summary>Sequence name (must be unique within the AnimationList).</summary>
    public string Name { get; set; } = "";

    /// <summary>Animation source path relative to the assets root
    /// (e.g. <c>models/x/animations/walk.dmx</c>).</summary>
    public string SourceFilename { get; set; } = "";

    /// <summary>Whether the sequence loops.</summary>
    public bool Looping { get; set; }

    /// <summary>Whether to add an ExtractMotion child node (ground-plane translation
    /// extraction, linear, matching the shipped citizen prefab usage).</summary>
    public bool ExtractMotion { get; set; }

    /// <summary>AnimEvent children to emit on the AnimFile node (e.g. generated
    /// <c>AE_FOOTSTEP</c> events); empty = no event nodes.</summary>
    public IReadOnlyList<AnimEventEntry> Events { get; set; } = Array.Empty<AnimEventEntry>();

    /// <summary>
    /// When set, the AnimFile node gets an <c>AnimSubtract</c> child (first child, like the
    /// shipped citizen data orders it) making the sequence an additive delta at compile time:
    /// <c>anim_name</c> = this value (the sequence whose reference frame is subtracted —
    /// the shipped <c>IdleLayer_01_delta</c> names its base sequence; other shipped entries
    /// self-reference), <c>frame</c> = <see cref="SubtractFrame"/>. The animation source is
    /// reused verbatim (<see cref="SourceFilename"/> points at the SAME file as the base
    /// entry) — resourcecompiler performs the per-frame subtraction; no frame math happens
    /// here. The AnimFile's own <c>delta</c> attribute stays <c>false</c>, exactly like every
    /// shipped <c>_delta</c> sequence. Null = plain (non-additive) sequence.
    /// </summary>
    public string? SubtractAnimName { get; set; }

    /// <summary>Reference frame index the AnimSubtract child subtracts (the shipped data uses
    /// 0 for loops and mid-clip indices for aim matrices); ignored while
    /// <see cref="SubtractAnimName"/> is null.</summary>
    public int SubtractFrame { get; set; }
}

/// <summary>
/// One detected locomotion family to emit as a directional 2D blend: a <c>Folder</c> named by
/// the family stem grouping the member AnimFile entries plus one <c>2DBlend</c> node wired to
/// the citizen pose parameters (<c>move_x</c>/<c>move_y</c>). Produced by
/// <see cref="LocomotionSetDetector"/>; consumed by <see cref="VmdlWriter"/> and
/// <see cref="VmdlAugmenter"/>.
/// </summary>
public sealed class LocomotionSetSpec
{
    /// <summary>Name of the Folder node grouping the family (the stem, collision-suffixed).</summary>
    public required string FolderName { get; init; }

    /// <summary>Name of the 2DBlend node (<c>&lt;stem&gt;_2D</c>, collision-suffixed).</summary>
    public required string BlendName { get; init; }

    /// <summary>Looping flag of the 2DBlend node (true when every member loops; the shipped
    /// locomotion blends are all looping).</summary>
    public required bool Looping { get; init; }

    /// <summary>
    /// The 3×3 <c>blend_anim_list</c> grid, <c>[row][col]</c> with rows indexed by
    /// <c>move_x</c> (−1, 0, +1) and columns by <c>move_y</c> (−1, 0, +1) — the exact shipped
    /// citizen layout: row 0 = [SW, S, SE], row 1 = [W, center, E], row 2 = [NW, N, NE].
    /// </summary>
    public required string[][] BlendGrid { get; init; }

    /// <summary>Names of the batch AnimFile entries grouped under the Folder, in canonical
    /// direction order (N, NE, E, SE, S, SW, W, NW; absent diagonals skipped).</summary>
    public required IReadOnlyList<string> MemberNames { get; init; }
}

/// <summary>
/// Generates standalone Base-Model vmdl files (KV3 text) that reference an existing model and
/// register retargeted animation DMX files, following the shipped citizen vmdl conventions
/// (field set proven to compile in M0 via <c>m0_test.vmdl</c>).
/// </summary>
public static class VmdlWriter
{
    /// <summary>The KV3 header line used by shipped modeldoc vmdl files.</summary>
    public const string Kv3Header =
        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->";

    /// <summary>
    /// Builds a standalone vmdl: RootNode with <c>base_model_name</c> =
    /// <paramref name="baseModelPath"/>, an optional ModelModifierList/ScaleAndMirror node
    /// (omitted entirely when <paramref name="scale"/> is 1.0 — engine-unit sources need no
    /// rescale; 0.3937 converts cm sources like the citizen rig), and an AnimationList with
    /// one AnimFile per entry. When <paramref name="locomotionSets"/> is non-empty, each
    /// set's member entries are grouped under a Folder node (appended after the loose
    /// entries) together with the set's 2DBlend node, replicating the shipped citizen
    /// locomotion layout. When <paramref name="meshFilePath"/> is non-empty a
    /// RenderMeshList/RenderMeshFile node embeds that mesh source (assets-relative,
    /// <paramref name="meshImportScale"/> raw-units→skeleton-units) — this is what gives
    /// custom-FBX-target vmdls a skeleton and skin to play on (an empty
    /// <c>base_model_name</c> with no mesh compiles into a model with 0 bones and 0
    /// sequences).
    /// </summary>
    public static string GenerateStandalone(string baseModelPath, IEnumerable<AnimEntry> anims,
        float scale, string defaultRootBone,
        IReadOnlyList<LocomotionSetSpec>? locomotionSets = null,
        string meshFilePath = "", float meshImportScale = 1.0f,
        IReadOnlyDictionary<string, string>? materialRemaps = null,
        IReadOnlyList<string>? meshImportNames = null)
    {
        ArgumentNullException.ThrowIfNull(baseModelPath);
        ArgumentNullException.ThrowIfNull(anims);
        ArgumentNullException.ThrowIfNull(defaultRootBone);

        var children = new KvArray();

        if (!string.IsNullOrEmpty(meshFilePath))
        {
            children.Items.Add(BuildRenderMeshListNode(meshFilePath, meshImportScale, meshImportNames));
            // Retarget channels may drive unweighted root/helper joints. ModelDoc otherwise
            // culls them from imported meshes, breaking the hierarchy the animation targets.
            children.Items.Add(new KvObject
            {
                ["_class"] = new KvString("BoneMarkupList"),
                ["children"] = new KvArray(),
                ["bone_cull_type"] = new KvString("None"),
            });
        }
        if (materialRemaps is { Count: > 0 })
            children.Items.Add(BuildMaterialGroupListNode(materialRemaps));

        if (scale != 1.0f)
        {
            var modifier = new KvObject
            {
                ["_class"] = new KvString("ModelModifier_ScaleAndMirror"),
                // float -> shortest-round-trip string -> double keeps "0.3937" exact in text.
                ["scale"] = new KvDouble(
                    double.Parse(scale.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
                ["mirror_x"] = new KvBool(false),
                ["mirror_y"] = new KvBool(false),
                ["mirror_z"] = new KvBool(false),
                ["flip_bone_forward"] = new KvBool(false),
                ["swap_left_and_right_bones"] = new KvBool(false),
            };
            var modifierChildren = new KvArray();
            modifierChildren.Items.Add(modifier);
            children.Items.Add(new KvObject
            {
                ["_class"] = new KvString("ModelModifierList"),
                ["children"] = modifierChildren,
            });
        }

        var sets = locomotionSets ?? Array.Empty<LocomotionSetSpec>();
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            foreach (var member in set.MemberNames)
                grouped.Add(member);
        }

        var animList = anims as IReadOnlyList<AnimEntry> ?? new List<AnimEntry>(anims);
        var animChildren = new KvArray();
        foreach (var anim in animList)
        {
            if (!grouped.Contains(anim.Name))
                animChildren.Items.Add(BuildAnimFileNode(anim, defaultRootBone));
        }
        foreach (var set in sets)
            animChildren.Items.Add(BuildLocomotionFolderNode(set, animList, defaultRootBone));
        children.Items.Add(new KvObject
        {
            ["_class"] = new KvString("AnimationList"),
            ["children"] = animChildren,
            ["default_root_bone_name"] = new KvString(defaultRootBone),
        });

        var root = new KvObject
        {
            ["rootNode"] = new KvObject
            {
                ["_class"] = new KvString("RootNode"),
                ["children"] = children,
                ["model_archetype"] = new KvString(""),
                ["primary_associated_entity"] = new KvString(""),
                ["anim_graph_name"] = new KvString(""),
                ["base_model_name"] = new KvString(baseModelPath),
            },
        };

        return Kv3.Serialize(new Kv3Document(Kv3Header, root));
    }

    /// <summary>
    /// Builds one AnimFile KV3 node (full attribute set as compiled in M0). When the entry
    /// requests motion extraction, an ExtractMotion child extracting ground-plane translation
    /// on <paramref name="motionRootBone"/> is included; <see cref="AnimEntry.Events"/>
    /// become AnimEvent children (shipped-prefab node shape, see
    /// <see cref="AnimEventEntry"/>); <see cref="AnimEntry.SubtractAnimName"/> adds the
    /// AnimSubtract FIRST child the shipped <c>_delta</c> sequences carry
    /// (<c>_class</c>/<c>anim_name</c>/<c>frame</c>, nothing else).
    /// </summary>
    internal static KvObject BuildAnimFileNode(AnimEntry entry, string motionRootBone)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var node = new KvObject
        {
            ["_class"] = new KvString("AnimFile"),
            ["name"] = new KvString(entry.Name),
        };

        var nodeChildren = new KvArray();
        if (entry.SubtractAnimName is not null)
        {
            nodeChildren.Items.Add(new KvObject
            {
                ["_class"] = new KvString("AnimSubtract"),
                ["anim_name"] = new KvString(entry.SubtractAnimName),
                ["frame"] = new KvLong(entry.SubtractFrame),
            });
        }
        if (entry.ExtractMotion)
        {
            var extract = new KvObject
            {
                ["_class"] = new KvString("ExtractMotion"),
                ["extract_tx"] = new KvBool(true),
                ["extract_ty"] = new KvBool(true),
                ["extract_tz"] = new KvBool(false),
                ["extract_rz"] = new KvBool(false),
                ["linear"] = new KvBool(true),
                ["quadratic"] = new KvBool(false),
                ["root_bone_name"] = new KvString(motionRootBone),
                ["motion_type"] = new KvString("Single"),
            };
            nodeChildren.Items.Add(extract);
        }
        foreach (var animEvent in entry.Events)
            nodeChildren.Items.Add(BuildAnimEventNode(animEvent));
        if (nodeChildren.Items.Count > 0)
            node["children"] = nodeChildren;

        node["activity_name"] = new KvString("");
        node["activity_weight"] = new KvLong(1);
        node["weight_list_name"] = new KvString("");
        node["fade_in_time"] = new KvDouble(0.2);
        node["fade_out_time"] = new KvDouble(0.2);
        node["looping"] = new KvBool(entry.Looping);
        node["delta"] = new KvBool(false);
        node["worldSpace"] = new KvBool(false);
        node["hidden"] = new KvBool(false);
        node["anim_markup_ordered"] = new KvBool(false);
        node["disable_compression"] = new KvBool(false);
        node["disable_interpolation"] = new KvBool(false);
        node["enable_scale"] = new KvBool(false);
        node["source_filename"] = new KvString(entry.SourceFilename);
        node["start_frame"] = new KvLong(-1);
        node["end_frame"] = new KvLong(-1);
        node["framerate"] = new KvDouble(-1.0);
        node["take"] = new KvLong(0);
        node["reverse"] = new KvBool(false);
        return node;
    }

    /// <summary>
    /// Builds one AnimEvent KV3 node in the shipped citizen prefab shape:
    /// <c>_class</c>/<c>event_class</c>/<c>event_frame</c> plus an <c>event_keys</c> object
    /// (key order <c>Attachment</c>, <c>Foot</c>, <c>Volume</c>, exactly like the shipped
    /// data; null-valued keys are omitted, and an all-null key set omits the object).
    /// </summary>
    /// <summary>A <c>RenderMeshList</c> node with one <c>RenderMeshFile</c> child (field
    /// set mirrors the shipped citizen_human_male_staging.vmdl). Shared with
    /// <see cref="VmdlAugmenter.EnsureMeshFile"/> so accumulated pipeline-owned vmdls that
    /// predate mesh embedding can be healed in place.</summary>
    internal static KvObject BuildRenderMeshListNode(string meshFilePath, float meshImportScale,
        IReadOnlyList<string>? meshImportNames = null)
    {
        var meshChildren = new KvArray();
        // Keep source mesh instances separate: merging a large multi-part FBX into one
        // skin buffer can corrupt rendered vertices despite correct bones and weights.
        IEnumerable<string?> parts = meshImportNames is { Count: > 1 }
            ? (IEnumerable<string?>)meshImportNames.Distinct(StringComparer.OrdinalIgnoreCase) : new string?[] { null };
        foreach (var part in parts)
        {
            var names = new KvArray();
            if (part is not null)
                names.Items.Add(new KvString(part));
            // Do not reuse an input mesh name as a ModelDoc node name (self-reference).
            var name = Path.GetFileNameWithoutExtension(meshFilePath);
            if (part is not null)
                name += $"_part_{meshChildren.Items.Count}";
            var node = new KvObject
            {
                ["_class"] = new KvString("RenderMeshFile"),
                ["name"] = new KvString(name),
                ["filename"] = new KvString(meshFilePath),
                ["import_translation"] = MakeVector3(0, 0, 0),
                ["import_rotation"] = MakeVector3(0, 0, 0),
                ["import_scale"] = new KvDouble(
                    double.Parse(meshImportScale.ToString("R", CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture)),
                ["align_origin_x_type"] = new KvString("None"),
                ["align_origin_y_type"] = new KvString("None"),
                ["align_origin_z_type"] = new KvString("None"),
                ["parent_bone"] = new KvString(""),
                ["import_filter"] = new KvObject
                {
                    ["exclude_by_default"] = new KvBool(part is not null),
                    ["exception_list"] = names,
                },
            };
            meshChildren.Items.Add(node);
        }
        return new KvObject
        {
            ["_class"] = new KvString("RenderMeshList"),
            ["children"] = meshChildren,
        };
    }

    /// <summary>
    /// A <c>MaterialGroupList</c> with one <c>DefaultMaterialGroup</c> carrying material
    /// remaps (bare mesh material reference → assets-relative vmat path) — the shipped
    /// citizen vmdl's own mechanism. FBX materials are bare names the compiler cannot
    /// resolve as resource paths; without the remap every mesh logs
    /// 'Missing vmat "&lt;name&gt;.vmat"' and renders placeholder materials.
    /// </summary>
    internal static KvObject BuildMaterialGroupListNode(IReadOnlyDictionary<string, string> remaps)
    {
        var remapArray = new KvArray();
        foreach (var (from, to) in remaps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            remapArray.Items.Add(new KvObject
            {
                ["from"] = new KvString(from),
                ["to"] = new KvString(to),
            });
        }

        var groupChildren = new KvArray();
        groupChildren.Items.Add(new KvObject
        {
            ["_class"] = new KvString("DefaultMaterialGroup"),
            ["remaps"] = remapArray,
            ["use_global_default"] = new KvBool(false),
            ["global_default_material"] = new KvString(""),
        });
        return new KvObject
        {
            ["_class"] = new KvString("MaterialGroupList"),
            ["children"] = groupChildren,
        };
    }

    /// <summary>KV3 <c>[ x, y, z ]</c> double array (RenderMeshFile import vectors).</summary>
    private static KvArray MakeVector3(double x, double y, double z)
    {
        var array = new KvArray();
        array.Items.Add(new KvDouble(x));
        array.Items.Add(new KvDouble(y));
        array.Items.Add(new KvDouble(z));
        return array;
    }

    private static KvObject BuildAnimEventNode(AnimEventEntry animEvent)
    {
        var node = new KvObject
        {
            ["_class"] = new KvString("AnimEvent"),
            ["event_class"] = new KvString(animEvent.EventClass),
            ["event_frame"] = new KvLong(animEvent.Frame),
        };

        var keys = new KvObject();
        if (animEvent.Attachment is not null)
            keys["Attachment"] = new KvString(animEvent.Attachment);
        if (animEvent.Foot is not null)
            keys["Foot"] = new KvString(animEvent.Foot);
        if (animEvent.Volume is { } volume)
            keys["Volume"] = new KvDouble(volume);
        if (animEvent.ExtraKeys is not null)
        {
            foreach (var kv in animEvent.ExtraKeys)
                keys[kv.Key] = new KvString(kv.Value);
        }
        if (keys.Count > 0)
            node["event_keys"] = keys;

        return node;
    }

    /// <summary>
    /// Builds a locomotion Folder node in the shipped citizen shape (<c>_class</c>,
    /// <c>name</c>, <c>children</c>): the set's 2DBlend node first, then the member AnimFile
    /// nodes (in <see cref="LocomotionSetSpec.MemberNames"/> order) — mirroring how the
    /// shipped <c>CrouchWalk</c>/<c>Walk</c>/<c>Run</c> folders lead with their blend nodes.
    /// </summary>
    internal static KvObject BuildLocomotionFolderNode(
        LocomotionSetSpec set, IReadOnlyList<AnimEntry> allEntries, string motionRootBone)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(allEntries);

        var folderChildren = new KvArray();
        folderChildren.Items.Add(Build2DBlendNode(set));
        foreach (var member in set.MemberNames)
        {
            AnimEntry? entry = null;
            foreach (var candidate in allEntries)
            {
                if (string.Equals(candidate.Name, member, StringComparison.Ordinal))
                {
                    entry = candidate;
                    break;
                }
            }
            if (entry is null)
                throw new ArgumentException(
                    $"Locomotion set '{set.FolderName}' references unknown entry '{member}'.");
            folderChildren.Items.Add(BuildAnimFileNode(entry, motionRootBone));
        }

        return new KvObject
        {
            ["_class"] = new KvString("Folder"),
            ["name"] = new KvString(set.FolderName),
            ["children"] = folderChildren,
        };
    }

    /// <summary>
    /// Builds one 2DBlend KV3 node replicating the shipped citizen locomotion blends
    /// (<c>CrouchWalk_Default_2D</c>, <c>Run_Default_2D</c>, …) EXACTLY: the common sequence
    /// attribute set (with <c>activity_name</c> left empty — the shipped activity wiring is
    /// model-specific), then <c>row_pose_param_name = "move_x"</c>,
    /// <c>col_pose_param_name = "move_y"</c>, <c>row_weight_list</c>/<c>col_weight_list</c>
    /// of <c>[-1.0, 0.0, 1.0]</c> and the 3×3 <c>blend_anim_list</c> grid. The pose
    /// parameters are NOT declared here: the citizen base models pull them in via their
    /// PoseParamList prefab (<c>citizen_poseparamlist.vmdl_prefab</c>), which Base-Model
    /// children inherit — custom (non-citizen) targets must declare <c>move_x</c>/<c>move_y</c>
    /// on their base model for the blend to be drivable.
    /// </summary>
    internal static KvObject Build2DBlendNode(LocomotionSetSpec set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.BlendGrid.Length != 3 || Array.Exists(set.BlendGrid, row => row.Length != 3))
            throw new ArgumentException("Locomotion blend grid must be 3x3.", nameof(set));

        var node = new KvObject
        {
            ["_class"] = new KvString("2DBlend"),
            ["name"] = new KvString(set.BlendName),
            ["activity_name"] = new KvString(""),
            ["activity_weight"] = new KvLong(1),
            ["weight_list_name"] = new KvString(""),
            ["fade_in_time"] = new KvDouble(0.2),
            ["fade_out_time"] = new KvDouble(0.2),
            ["looping"] = new KvBool(set.Looping),
            ["delta"] = new KvBool(false),
            ["worldSpace"] = new KvBool(false),
            ["hidden"] = new KvBool(false),
            ["anim_markup_ordered"] = new KvBool(false),
            ["disable_compression"] = new KvBool(false),
            ["disable_interpolation"] = new KvBool(false),
            ["enable_scale"] = new KvBool(false),
            ["row_pose_param_name"] = new KvString("move_x"),
            ["col_pose_param_name"] = new KvString("move_y"),
            ["row_weight_list"] = AxisWeights(),
            ["col_weight_list"] = AxisWeights(),
        };

        var grid = new KvArray();
        foreach (var row in set.BlendGrid)
        {
            var rowArray = new KvArray();
            foreach (var cell in row)
                rowArray.Items.Add(new KvString(cell));
            grid.Items.Add(rowArray);
        }
        node["blend_anim_list"] = grid;
        return node;

        static KvArray AxisWeights()
        {
            var weights = new KvArray();
            weights.Items.Add(new KvDouble(-1.0));
            weights.Items.Add(new KvDouble(0.0));
            weights.Items.Add(new KvDouble(1.0));
            return weights;
        }
    }
}
