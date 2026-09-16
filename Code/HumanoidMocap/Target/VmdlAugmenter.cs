#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

namespace HumanoidMocap.Target;

/// <summary>
/// Thrown by <see cref="VmdlAugmenter.Augment"/> when an animation name collides with an
/// existing AnimationList node that is not an AnimFile (replacing it would destroy user data).
/// </summary>
public sealed class VmdlAugmentException : Exception
{
    /// <summary>One message per colliding entry, naming the sequence and the existing
    /// node's class.</summary>
    public IReadOnlyList<string> Collisions { get; }

    /// <summary>Creates the exception from the collected collision messages.</summary>
    public VmdlAugmentException(IReadOnlyList<string> collisions)
        : base("Cannot augment vmdl, name collisions with non-AnimFile nodes: "
            + string.Join("; ", collisions))
    {
        Collisions = collisions;
    }
}

/// <summary>Options for <see cref="VmdlAugmenter.Augment"/>.</summary>
public sealed class AugmentOptions
{
    /// <summary>
    /// <c>default_root_bone_name</c> used when the vmdl has NO AnimationList yet (the value
    /// also seeds ExtractMotion nodes' <c>root_bone_name</c>) — same value the standalone
    /// writer uses (<see cref="RetargetTargetSpec.DefaultRootBone"/>). An existing
    /// AnimationList's own non-empty value always wins.
    /// </summary>
    public string DefaultRootBone { get; init; } = "";

    /// <summary>
    /// When true, every CopyPinky ring→pinky constraint in the vmdl (the citizen base
    /// model's transitional <c>AnimConstraintOrient</c> folder, or any constraint whose
    /// driven bone is a <c>finger_pinky_*</c>) gets its weights set to 0, so the exported
    /// pinky channels are no longer overridden at runtime. Idempotent; everything else in
    /// the document is untouched.
    /// </summary>
    public bool NeutralizePinkyConstraints { get; init; }

    /// <summary>
    /// Detected locomotion families to splice as Folder + 2DBlend groups (see
    /// <see cref="LocomotionSetDetector"/>). Each set's member entries are grouped under a
    /// Folder named <see cref="LocomotionSetSpec.FolderName"/> instead of being spliced at
    /// the AnimationList top level. Pipeline-owned locomotion folders are rebuilt AS A UNIT:
    /// a previous run's same-named folder is replaced wholesale when every AnimFile inside
    /// it is pipeline-owned per <see cref="DmxFolderRelative"/> — even when the new batch is
    /// a SHRUNKEN family (e.g. 8-way re-run as 4-way), whose stale members are simply
    /// dropped with the old folder. A hand-edited or foreign folder is never destroyed (it
    /// throws <see cref="VmdlAugmentException"/> instead). Null/empty = no grouping.
    /// </summary>
    public IReadOnlyList<LocomotionSetSpec>? LocomotionSets { get; init; }

    /// <summary>
    /// Assets-relative folder this batch's DMX files live in (back- or forward slashes,
    /// trailing slash ignored). Decides which existing nodes are PIPELINE-OWNED — an
    /// AnimFile whose <c>source_filename</c> sits under this folder — mirroring the
    /// name-seeding ownership rules in <c>Retargeter.ConvertBatch</c>, so a folder the
    /// batch's collision seeding treated as replaceable is also replaceable here. Empty
    /// (default) = only sources with no directory component count as ours.
    /// </summary>
    public string DmxFolderRelative { get; init; } = "";

    /// <summary>
    /// Animation source paths (<c>source_filename</c> values, assets-relative, either slash
    /// direction) the IO-owning caller knows are MISSING on disk. Existing AnimFile nodes
    /// referencing them are removed from the augmented output — a single unresolvable source
    /// fails the whole vmdl recompile (<c>Node 'X' resolve failure</c>), so a stale entry
    /// left over from an earlier batch (its DMX deleted or moved) would take every animation
    /// in the model down with it. Nodes whose name this batch (re)writes are exempt — their
    /// DMX is about to exist. 2DBlend nodes referencing a pruned sequence and Folder nodes
    /// emptied by the pruning are removed with it. Null/empty = keep everything (default;
    /// the augmenter itself never touches the filesystem).
    /// </summary>
    public IReadOnlyCollection<string>? MissingSourceFiles { get; init; }
}

/// <summary>
/// Non-destructively splices AnimFile nodes into an existing vmdl's AnimationList. The rest
/// of the document tree is preserved (semantically — the file is re-serialized through
/// <see cref="Kv3"/>). Re-running with the same entries replaces the previously spliced
/// nodes, making augmentation idempotent.
/// </summary>
public static class VmdlAugmenter
{
    /// <summary>Returns the model sources owned by the vmdl root: its inherited base model
    /// and first embedded RenderMeshFile (either may be empty).</summary>
    public static (string BaseModel, string? RenderMesh) GetModelSource(string vmdlText)
    {
        ArgumentNullException.ThrowIfNull(vmdlText);
        var doc = Kv3.Parse(vmdlText);
        if (doc.Root is not KvObject root || root.GetOrNull("rootNode") is not KvObject rootNode)
            throw new FormatException("vmdl has no rootNode object.");
        if (rootNode.GetOrNull("children") is not KvArray children)
            return (rootNode.GetString("base_model_name") ?? "", null);

        var mesh = children.Items.OfType<KvObject>()
            .Where(o => o.GetString("_class") == "RenderMeshList")
            .SelectMany(o => (o.GetOrNull("children") as KvArray)?.Items
                .OfType<KvObject>() ?? Enumerable.Empty<KvObject>())
            .FirstOrDefault(o => o.GetString("_class") == "RenderMeshFile")
            ?.GetString("filename");
        return (rootNode.GetString("base_model_name") ?? "", mesh);
    }

    /// <summary>
    /// Returns <paramref name="vmdlText"/> with one AnimFile per entry inserted into the
    /// RootNode's AnimationList (created and appended when absent). Entries whose name
    /// matches an existing AnimFile replace it in place; a name match against any other
    /// node class throws <see cref="VmdlAugmentException"/> before anything is modified.
    /// </summary>
    /// <param name="vmdlText">The current vmdl file content.</param>
    /// <param name="anims">Animations to insert.</param>
    /// <param name="backupOfOriginal">Receives <paramref name="vmdlText"/> verbatim so
    /// callers can write a backup before overwriting the file.</param>
    /// <param name="options">Optional behavior knobs; null = defaults.</param>
    /// <param name="removedSequences">Optional sink receiving the names of stale nodes
    /// removed because their animation source is gone
    /// (<see cref="AugmentOptions.MissingSourceFiles"/>).</param>
    /// <exception cref="FormatException">Thrown when the text is not parseable KV3 or has no
    /// rootNode object.</exception>
    /// <exception cref="VmdlAugmentException">Thrown on name collisions with non-AnimFile
    /// nodes.</exception>
    /// <summary>
    /// Ensures a PIPELINE-OWNED standalone vmdl embeds <paramref name="meshFilePath"/> as a
    /// RenderMeshList/RenderMeshFile node. Standalone outputs generated before mesh
    /// embedding existed carry <c>base_model_name = ""</c> and no mesh — and because the
    /// standalone output ACCUMULATES (each conversion augments the existing file in place),
    /// such a vmdl would stay an empty model (0 bones, 0 playable sequences) forever, no
    /// matter how many new conversions run against it. A RenderMeshList referencing a
    /// DIFFERENT mesh is replaced wholesale (the user switched target FBX; the file is
    /// pipeline-owned). Never use on a user's own model vmdl — augment mode passes real
    /// models through untouched. Returns the (possibly unchanged) vmdl text.
    /// </summary>
    public static string EnsureMeshFile(string vmdlText, string meshFilePath, float meshImportScale,
        IReadOnlyDictionary<string, string>? materialRemaps = null,
        IReadOnlyList<string>? meshImportNames = null)
    {
        ArgumentNullException.ThrowIfNull(vmdlText);
        if (string.IsNullOrEmpty(meshFilePath))
            return vmdlText;

        var doc = Kv3.Parse(vmdlText);
        if (doc.Root is not KvObject root || root.GetOrNull("rootNode") is not KvObject rootNode)
            throw new FormatException("vmdl has no rootNode object.");

        if (rootNode.GetOrNull("children") is not KvArray children)
        {
            children = new KvArray();
            rootNode["children"] = children;
        }

        var changed = false;

        var meshList = children.Items.OfType<KvObject>()
            .FirstOrDefault(o => o.GetString("_class") == "RenderMeshList");
        var replacement = VmdlWriter.BuildRenderMeshListNode(meshFilePath, meshImportScale, meshImportNames);
        var alreadyEmbedded = meshList?.GetOrNull("children") is KvArray existing
            && existing.Items.OfType<KvObject>().Any(o =>
                o.GetString("_class") == "RenderMeshFile"
                && string.Equals(o.GetString("filename"), meshFilePath, StringComparison.OrdinalIgnoreCase))
            && (meshImportNames is not { Count: > 1 }
                || KvValue.DeepEquals(existing, replacement.GetOrNull("children")));
        if (!alreadyEmbedded)
        {
            if (meshList is null)
                children.Items.Insert(0, replacement);
            else
                meshList["children"] = replacement.GetOrNull("children")!;
            changed = true;
        }

        // Material remaps ride along the same healing pass: a pre-remap output would keep
        // logging 'Missing vmat' / rendering placeholders forever. MERGED into an existing
        // DefaultMaterialGroup: entries for materials it does not know yet are appended
        // (an output vmdl written before a texture-matching improvement otherwise keeps
        // the stale table forever - observed as textured preview but untextured ModelDoc);
        // existing entries are left alone (they may carry user edits).
        if (materialRemaps is { Count: > 0 })
        {
            var materialList = children.Items.OfType<KvObject>()
                .FirstOrDefault(o => o.GetString("_class") == "MaterialGroupList");
            if (materialList is null)
            {
                children.Items.Add(VmdlWriter.BuildMaterialGroupListNode(materialRemaps));
                changed = true;
            }
            else if (materialList.GetOrNull("children") is KvArray groups
                && groups.Items.OfType<KvObject>()
                    .FirstOrDefault(o => o.GetString("_class") == "DefaultMaterialGroup") is { } group
                && group.GetOrNull("remaps") is KvArray remapArray)
            {
                var known = new HashSet<string>(
                    remapArray.Items.OfType<KvObject>()
                        .Select(o => o.GetString("from") ?? "")
                        .Where(f => f.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (from, to) in materialRemaps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (known.Contains(from))
                        continue;
                    remapArray.Items.Add(new KvObject
                    {
                        ["from"] = new KvString(from),
                        ["to"] = new KvString(to),
                    });
                    changed = true;
                }
            }
        }

        return changed ? Kv3.Serialize(doc) : vmdlText;
    }

    public static string Augment(string vmdlText, IEnumerable<AnimEntry> anims,
        out string backupOfOriginal, AugmentOptions? options = null,
        ICollection<string>? removedSequences = null)
    {
        ArgumentNullException.ThrowIfNull(vmdlText);
        ArgumentNullException.ThrowIfNull(anims);
        backupOfOriginal = vmdlText;
        options ??= new AugmentOptions();

        var entries = anims.ToList();
        var doc = Kv3.Parse(vmdlText);
        if (doc.Root is not KvObject root || root.GetOrNull("rootNode") is not KvObject rootNode)
            throw new FormatException("vmdl has no rootNode object.");

        if (rootNode.GetOrNull("children") is not KvArray children)
        {
            children = new KvArray();
            rootNode["children"] = children;
        }

        var animList = children.Items.OfType<KvObject>()
            .FirstOrDefault(o => o.GetString("_class") == "AnimationList");
        if (animList is null)
        {
            animList = new KvObject
            {
                ["_class"] = new KvString("AnimationList"),
                ["children"] = new KvArray(),
                ["default_root_bone_name"] = new KvString(options.DefaultRootBone),
            };
            children.Items.Add(animList);
        }

        if (animList.GetOrNull("children") is not KvArray listChildren)
        {
            listChildren = new KvArray();
            animList["children"] = listChildren;
        }

        var motionRootBone = animList.GetString("default_root_bone_name") ?? "";
        if (motionRootBone.Length == 0)
            motionRootBone = options.DefaultRootBone;

        var sets = options.LocomotionSets ?? Array.Empty<LocomotionSetSpec>();
        var groupedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            foreach (var member in set.MemberNames)
                groupedNames.Add(member);
        }
        var dmxFolder = options.DmxFolderRelative.Replace('\\', '/').TrimEnd('/');

        // Validate all entries and set names first so a collision throws before any mutation.
        var collisions = new List<string>();
        foreach (var entry in entries)
        {
            var existing = FindByName(listChildren, entry.Name);
            if (existing is not null && existing.GetString("_class") != "AnimFile")
            {
                collisions.Add(
                    $"'{entry.Name}' already exists as {existing.GetString("_class") ?? "<unknown class>"}");
            }
        }
        foreach (var set in sets)
        {
            if (FindByName(listChildren, set.FolderName) is { } folderNode
                && !IsReplaceableLocomotionFolder(folderNode, dmxFolder))
            {
                collisions.Add(
                    $"'{set.FolderName}' already exists as "
                    + $"{folderNode.GetString("_class") ?? "<unknown class>"} whose content was "
                    + "not produced by this pipeline");
            }
            if (FindByName(listChildren, set.BlendName) is { } blendNode
                && blendNode.GetString("_class") != "2DBlend")
            {
                collisions.Add(
                    $"'{set.BlendName}' already exists as {blendNode.GetString("_class") ?? "<unknown class>"}");
            }
        }
        if (collisions.Count > 0)
            throw new VmdlAugmentException(collisions);

        // Loose entries (not grouped into a locomotion folder): replace in place wherever
        // they live — top level or inside a previously spliced folder — or append.
        foreach (var entry in entries)
        {
            if (groupedNames.Contains(entry.Name))
                continue;
            var node = VmdlWriter.BuildAnimFileNode(entry, motionRootBone);
            var (parent, index) = LocateByName(listChildren, entry.Name);
            if (parent is not null)
                parent.Items[index] = node; // idempotent re-run: replace same-named AnimFile
            else
                listChildren.Items.Add(node);
        }

        // Locomotion sets: rebuild each family's folder (replacing a previous run's folder
        // in place) and remove superseded loose copies of the members and blend node.
        foreach (var set in sets)
        {
            var insertAt = RemoveFolder(listChildren, set.FolderName);
            foreach (var member in set.MemberNames)
                RemoveEverywhere(listChildren, member, "AnimFile");
            RemoveEverywhere(listChildren, set.BlendName, "2DBlend");

            var folder = VmdlWriter.BuildLocomotionFolderNode(set, entries, motionRootBone);
            if (insertAt >= 0 && insertAt <= listChildren.Items.Count)
                listChildren.Items.Insert(insertAt, folder);
            else
                listChildren.Items.Add(folder);
        }

        // Stale entries whose animation source is gone from disk: prune them (plus blends
        // referencing them and folders they empty) — the compiler otherwise fails the WHOLE
        // vmdl on the first "Node 'X' resolve failure", newly added sequences included.
        if (options.MissingSourceFiles is { Count: > 0 })
        {
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in options.MissingSourceFiles)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    missing.Add(path.Replace('\\', '/'));
            }
            var batchNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
                batchNames.Add(entry.Name);

            var pruned = new List<string>();
            var touched = new HashSet<KvArray>();
            PruneMissingSourceAnimFiles(listChildren, missing, batchNames, pruned, touched);
            if (pruned.Count > 0)
            {
                var prunedSet = new HashSet<string>(pruned, StringComparer.Ordinal);
                PruneBlendsReferencing(listChildren, prunedSet, pruned, touched);
                PruneEmptiedFolders(listChildren, pruned, touched);
                if (removedSequences is not null)
                {
                    foreach (var name in pruned)
                        removedSequences.Add(name);
                }
            }
        }

        if (options.NeutralizePinkyConstraints)
            NeutralizePinky(rootNode);

        return Kv3.Serialize(doc);
    }

    /// <summary>
    /// Every distinct AnimFile <c>source_filename</c> in the vmdl's own AnimationList
    /// (recursively through Folder nodes; prefab-provided entries are not part of this
    /// document and are not reported). IO-owning callers probe these against their content
    /// roots to build <see cref="AugmentOptions.MissingSourceFiles"/> /
    /// <see cref="BatchOptions.MissingAnimSources"/>. Unparseable text yields an empty list —
    /// the augmentation itself surfaces the parse error.
    /// </summary>
    public static IReadOnlyList<string> CollectAnimSourcePaths(string vmdlText)
    {
        ArgumentNullException.ThrowIfNull(vmdlText);

        Kv3Document doc;
        try
        {
            doc = Kv3.Parse(vmdlText);
        }
        catch (FormatException)
        {
            return Array.Empty<string>();
        }

        if (doc.Root is not KvObject root || root.GetOrNull("rootNode") is not KvObject rootNode
            || rootNode.GetOrNull("children") is not KvArray children)
            return Array.Empty<string>();
        var animList = children.Items.OfType<KvObject>()
            .FirstOrDefault(o => o.GetString("_class") == "AnimationList");
        if (animList?.GetOrNull("children") is not KvArray items)
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        Collect(items);
        return paths;

        void Collect(KvArray array)
        {
            foreach (var node in array.Items.OfType<KvObject>())
            {
                if (node.GetString("_class") == "AnimFile")
                {
                    var source = node.GetString("source_filename") ?? "";
                    if (source.Length > 0 && seen.Add(source.Replace('\\', '/')))
                        paths.Add(source);
                }
                if (node.GetOrNull("children") is KvArray nested)
                    Collect(nested);
            }
        }
    }

    // ------------------------------------------------------- missing-source pruning

    /// <summary>Removes every AnimFile (recursively through Folders) whose
    /// <c>source_filename</c> is in <paramref name="missing"/>, except nodes named by this
    /// batch (their DMX is about to be written). Removed names collect in
    /// <paramref name="pruned"/>; arrays a node was removed from in <paramref name="touched"/>.</summary>
    private static void PruneMissingSourceAnimFiles(
        KvArray items, HashSet<string> missing, HashSet<string> batchNames,
        List<string> pruned, HashSet<KvArray> touched)
    {
        for (var i = items.Items.Count - 1; i >= 0; i--)
        {
            if (items.Items[i] is not KvObject node)
                continue;
            if (node.GetString("_class") == "AnimFile")
            {
                var name = node.GetString("name") ?? "";
                var source = (node.GetString("source_filename") ?? "").Replace('\\', '/');
                if (source.Length > 0 && missing.Contains(source) && !batchNames.Contains(name))
                {
                    items.Items.RemoveAt(i);
                    touched.Add(items);
                    if (name.Length > 0)
                        pruned.Add(name);
                }
                continue;
            }
            if (node.GetOrNull("children") is KvArray nested)
                PruneMissingSourceAnimFiles(nested, missing, batchNames, pruned, touched);
        }
    }

    /// <summary>Removes 2DBlend nodes whose <c>blend_anim_list</c> references a sequence
    /// pruned by the missing-source pass — the blend cannot resolve its member either. Blends
    /// referencing only intact sequences (including prefab-provided ones) are untouched.</summary>
    private static void PruneBlendsReferencing(
        KvArray items, HashSet<string> prunedSet, List<string> pruned, HashSet<KvArray> touched)
    {
        for (var i = items.Items.Count - 1; i >= 0; i--)
        {
            if (items.Items[i] is not KvObject node)
                continue;
            if (node.GetString("_class") == "2DBlend" && BlendReferencesAny(node, prunedSet))
            {
                items.Items.RemoveAt(i);
                touched.Add(items);
                var name = node.GetString("name") ?? "";
                if (name.Length > 0)
                    pruned.Add(name);
                continue;
            }
            if (node.GetOrNull("children") is KvArray nested)
                PruneBlendsReferencing(nested, prunedSet, pruned, touched);
        }
    }

    /// <summary>Whether any non-empty cell of the blend's grid names a pruned sequence.</summary>
    private static bool BlendReferencesAny(KvObject blendNode, HashSet<string> prunedSet)
    {
        if (blendNode.GetOrNull("blend_anim_list") is not KvArray grid)
            return false;
        foreach (var row in grid.Items.OfType<KvArray>())
        {
            foreach (var cell in row.Items.OfType<KvString>())
            {
                if (cell.Value.Length > 0 && prunedSet.Contains(cell.Value))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Removes Folder nodes the pruning EMPTIED (children array now empty AND a
    /// node was actually removed from it — pre-existing empty folders are user data and
    /// survive). Removing an inner folder marks the outer array touched, so folders that
    /// emptied transitively fall too.</summary>
    private static void PruneEmptiedFolders(KvArray items, List<string> pruned, HashSet<KvArray> touched)
    {
        for (var i = items.Items.Count - 1; i >= 0; i--)
        {
            if (items.Items[i] is not KvObject node || node.GetString("_class") != "Folder")
                continue;
            if (node.GetOrNull("children") is not KvArray nested)
                continue;
            PruneEmptiedFolders(nested, pruned, touched);
            if (nested.Items.Count > 0 || !touched.Contains(nested))
                continue;
            items.Items.RemoveAt(i);
            touched.Add(items);
            var name = node.GetString("name") ?? "";
            if (name.Length > 0)
                pruned.Add(name);
        }
    }

    // ---------------------------------------------------------------- locomotion folders

    /// <summary>
    /// Whether an existing node may be replaced by a locomotion folder splice: it must be a
    /// Folder containing at least one AnimFile, and every AnimFile anywhere inside it must
    /// be pipeline-owned (<c>source_filename</c> under <paramref name="dmxFolder"/>) — i.e.
    /// the folder holds nothing of the user's own. Ownership deliberately ignores
    /// current-batch membership: a previous run's 8-way family re-run as a 4-way batch is
    /// still OUR folder, rebuilt as a unit with its stale members dropped. This mirrors
    /// <c>Retargeter</c>'s name-seeding ownership rules so a folder whose name the seeding
    /// released to this batch is never refused here.
    /// </summary>
    private static bool IsReplaceableLocomotionFolder(KvObject node, string dmxFolder)
    {
        if (node.GetString("_class") != "Folder")
            return false;
        var sawAnimFile = false;
        return AllAnimFilesOurs(node) && sawAnimFile;

        bool AllAnimFilesOurs(KvObject current)
        {
            if (current.GetString("_class") == "AnimFile")
            {
                sawAnimFile = true;
                if (!IsPipelineAnimFile(current, dmxFolder))
                    return false;
            }
            if (current.GetOrNull("children") is KvArray children)
            {
                foreach (var child in children.Items.OfType<KvObject>())
                {
                    if (!AllAnimFilesOurs(child))
                        return false;
                }
            }
            return true;
        }
    }

    /// <summary>Whether an AnimFile was written by this pipeline: its
    /// <c>source_filename</c> sits inside the batch's DMX folder (empty folder = a source
    /// with no directory component). Same rule <c>Retargeter</c> seeds collision names with.</summary>
    private static bool IsPipelineAnimFile(KvObject node, string dmxFolder)
    {
        var source = (node.GetString("source_filename") ?? "").Replace('\\', '/');
        return dmxFolder.Length == 0
            ? !source.Contains('/')
            : source.StartsWith(dmxFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes the Folder named <paramref name="name"/>; returns its top-level
    /// index (the position the rebuilt folder is re-inserted at) or −1 when it was absent
    /// or nested.</summary>
    private static int RemoveFolder(KvArray listChildren, string name)
    {
        var (parent, index) = LocateByName(listChildren, name);
        if (parent is null || ((KvObject)parent.Items[index]).GetString("_class") != "Folder")
            return -1;
        parent.Items.RemoveAt(index);
        return ReferenceEquals(parent, listChildren) ? index : -1;
    }

    /// <summary>Removes every node of class <paramref name="className"/> named
    /// <paramref name="name"/>, anywhere in the AnimationList tree (top level or inside
    /// Folder nodes) — superseded copies from previous runs with different grouping.</summary>
    private static void RemoveEverywhere(KvArray items, string name, string className)
    {
        for (var i = items.Items.Count - 1; i >= 0; i--)
        {
            if (items.Items[i] is not KvObject node)
                continue;
            if (node.GetString("_class") == className
                && string.Equals(node.GetString("name"), name, StringComparison.Ordinal))
            {
                items.Items.RemoveAt(i);
                continue;
            }
            if (node.GetString("_class") == "Folder" && node.GetOrNull("children") is KvArray nested)
                RemoveEverywhere(nested, name, className);
        }
    }

    // ---------------------------------------------------------------- CopyPinky neutralization

    /// <summary>
    /// Zeroes the weights of every pinky-driving constraint reachable from the root node:
    /// the citizen vmdl's CopyPinky Folder (a folder of AnimConstraintOrient nodes copying
    /// finger_ring_* onto finger_pinky_*) and any other AnimConstraint* node whose
    /// AnimConstraintSlave drives a <c>finger_pinky_*</c> bone. Other weights in the
    /// document (e.g. WeightList entries) are never touched.
    /// </summary>
    private static void NeutralizePinky(KvObject node)
    {
        var cls = node.GetString("_class") ?? "";

        var isCopyPinkyFolder = cls == "Folder"
            && string.Equals(node.GetString("name"), "CopyPinky", StringComparison.Ordinal);
        var isPinkyConstraint = cls.StartsWith("AnimConstraint", StringComparison.Ordinal)
            && cls != "AnimConstraintList"
            && DrivesPinkyBone(node);

        if (isCopyPinkyFolder || isPinkyConstraint)
        {
            ZeroWeights(node);
            return; // whole subtree handled
        }

        if (node.GetOrNull("children") is KvArray children)
        {
            foreach (var child in children.Items.OfType<KvObject>())
                NeutralizePinky(child);
        }
    }

    /// <summary>Whether any descendant AnimConstraintSlave drives a finger_pinky_* bone.</summary>
    private static bool DrivesPinkyBone(KvObject node)
    {
        if (node.GetString("_class") == "AnimConstraintSlave"
            && (node.GetString("parent_bone") ?? "")
                .StartsWith("finger_pinky_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (node.GetOrNull("children") is KvArray children)
        {
            foreach (var child in children.Items.OfType<KvObject>())
            {
                if (DrivesPinkyBone(child))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Sets every <c>weight</c> attribute in the subtree to 0.0 (idempotent).</summary>
    private static void ZeroWeights(KvObject node)
    {
        if (node.GetOrNull("weight") is not null)
            node["weight"] = new KvDouble(0.0);

        if (node.GetOrNull("children") is KvArray children)
        {
            foreach (var child in children.Items.OfType<KvObject>())
                ZeroWeights(child);
        }
    }

    private static KvObject? FindByName(KvArray items, string name)
    {
        var (parent, index) = LocateByName(items, name);
        return parent is not null ? (KvObject)parent.Items[index] : null;
    }

    /// <summary>Locates a node by name at the AnimationList top level or inside Folder
    /// nodes (recursively — previously spliced locomotion folders contain our AnimFiles):
    /// the owning array + index, or (null, −1) when absent.</summary>
    private static (KvArray? Parent, int Index) LocateByName(KvArray items, string name)
    {
        for (var i = 0; i < items.Items.Count; i++)
        {
            if (items.Items[i] is not KvObject o)
                continue;
            if (string.Equals(o.GetString("name"), name, StringComparison.Ordinal))
                return (items, i);
            if (o.GetString("_class") == "Folder" && o.GetOrNull("children") is KvArray nested)
            {
                var found = LocateByName(nested, name);
                if (found.Parent is not null)
                    return found;
            }
        }
        return (null, -1);
    }
}
