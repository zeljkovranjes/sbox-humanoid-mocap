#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Formats;
using HumanoidMocap.Formats.Ant;
using HumanoidMocap.Formats.Bvh;
using HumanoidMocap.Formats.Dmx;
using HumanoidMocap.Formats.Fbx;
using HumanoidMocap.Formats.Gltf;
using HumanoidMocap.Formats.Renderware;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Solve;
using HumanoidMocap.Target;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Engine-agnostic pipeline facade (design §5): bytes in → DMX + vmdl text out. Composes
/// import → mapping → solve → cleanup → IK baking → DMX → vmdl assembly. Batch is
/// first-class: N files × all takes per file → ONE combined vmdl, with per-request profile
/// detection (a batch may mix Mixamo + ActorCore + BVH sources) and per-clip failure
/// isolation. No file IO anywhere in this type — callers pass bytes/strings and write the
/// returned strings.
/// </summary>
/// <remarks>
/// Pipeline per clip:
/// <list type="number">
/// <item><b>Import</b> — format by file extension (<c>.fbx</c>/<c>.bvh</c>/<c>.glb</c>/
/// <c>.gltf</c>/<c>.vrm</c>/<c>.anm</c>/<c>.an5</c>/<c>.cba</c>), content sniff as fallback →
/// <see cref="SourceScene"/> (cm, native axes). RenderWare and EA ANT animations additionally
/// take companion skeleton bytes via <see cref="RetargetRequest.SkeletonData"/>.</item>
/// <item><b>Mapping</b> — per request: <see cref="RetargetRequest.MappingOverride"/> wins;
/// else <see cref="ProfileDetector.Detect"/> over the preset library (user presets are
/// loaded Editor-side and arrive as overrides); else <see cref="AutoMapper.Map"/> as best
/// effort with <see cref="MappingReportInfo.NeedsUserDecision"/> set when its confidence is
/// below <see cref="ProfileDetector.DetectionThreshold"/>.</item>
/// <item><b>Solve</b> — <see cref="GeometricSolver"/> per take (clip name = take name, or
/// <see cref="RetargetRequest.ClipNameOverride"/> + index).</item>
/// <item><b>Cleanup</b> (target space) — <see cref="FootPlant"/> when enabled (chains from
/// the target rig's UpperLeg/LowerLeg/Foot/Toe roles, up axis from the TARGET character
/// frame, fps from the clip); optional arm <see cref="EffectorIk"/> (off by default — the
/// solver already matches anatomical directions); <see cref="RootMotion"/> per mode (see
/// below).</item>
/// <item><b>IK baking</b> — <see cref="IkBoneBaker.Bake"/> when the rig has
/// <see cref="BoneClass.IkBaked"/> bones.</item>
/// <item><b>DMX</b> — <see cref="DmxWriter"/> over the target skeleton.</item>
/// <item><b>Assembly</b> — <see cref="VmdlWriter.GenerateStandalone"/> over all successful
/// clips, plus <see cref="VmdlAugmenter.Augment"/> when
/// <see cref="BatchOptions.AugmentVmdlText"/> is provided; clip-name collisions are
/// auto-suffixed (<c>_2</c>, <c>_3</c>, …) across the batch.</item>
/// </list>
/// <para><b>Root-motion ↔ target mapping.</b> <see cref="RootMotionMode.Extract"/> bakes the
/// ground-projected hips path onto the target's dedicated root bone — defined as a parentless
/// <see cref="BoneClass.Animated"/> bone that is an ancestor of (and distinct from) the Hips
/// bone. The s&amp;box rig has NO such bone (pelvis is itself parentless; root_IK is IkBaked),
/// so there Extract leaves the frames untouched, adds a report note, and instead sets
/// <see cref="AnimEntry.ExtractMotion"/> on the clip's vmdl entry: Source 2 extracts the
/// ground-plane translation from <see cref="RetargetTargetSpec.DefaultRootBone"/> at compile
/// time, which is the engine-native equivalent. The ExtractMotion flag is set for every
/// Extract request either way. <see cref="RootMotionMode.InPlace"/> always operates on the
/// hips channels directly and needs no dedicated root.</para>
/// </remarks>
public static class Retargeter
{
	/// <summary>
	/// Applies the same mapping, diagnostics, and mid-pose bind-rest selection used by
	/// <see cref="ConvertBatch"/>, while leaving the sampled clips available to an editor
	/// integration that needs an in-memory target rather than DMX output.
	/// </summary>
	public static ResolvedSource ResolveSource( SourceScene scene, MappingResult? mappingOverride = null,
		Func<string, MappingResult?>? userPresetLookup = null )
	{
		ArgumentNullException.ThrowIfNull( scene );
		var (map, report) = ResolveMapping( scene.Skeleton, mappingOverride, userPresetLookup, scene.AuthoredMapping );
		foreach ( var note in scene.Notes ) AddNote( report, note );
		scene = MaybeAdoptBindRest( scene, map, report );
		return new( scene, map, report );
	}

    /// <summary>Converts one source file — all takes, or only
    /// <see cref="RetargetRequest.TakeIndex"/> when set. Equivalent to a one-request batch.</summary>
    public static RetargetResult Convert(RetargetRequest request, RetargetTargetSpec target)
    {
        ArgumentNullException.ThrowIfNull(request);
        var batch = ConvertBatch(new[] { request }, target);
        return new RetargetResult
        {
            Clips = batch.Clips,
            StandaloneVmdl = batch.StandaloneVmdl,
            Errors = batch.Errors,
        };
    }

    /// <summary>
    /// Converts a batch of source files against one target. Per-request profile detection,
    /// per-clip failure isolation (a bad file yields a failed <see cref="ClipResult"/> and
    /// the batch continues), one standalone vmdl with every successful clip, optional
    /// augmentation of an existing vmdl.
    /// </summary>
    public static RetargetBatchResult ConvertBatch(
        IReadOnlyList<RetargetRequest> requests, RetargetTargetSpec target, BatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new BatchOptions();

        var context = new TargetContext(target.Rig);
        var result = new RetargetBatchResult();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AnimEntry>();

        // Augment mode: names already taken in the existing vmdl's AnimationList must not be
        // silently repointed — seed the collision set with every existing node name EXCEPT
        // our own AnimFiles (source_filename under DmxFolderRelative), which are replaceable
        // so re-running the same batch stays idempotent.
        if (options.AugmentVmdlText is not null)
            SeedUsedNamesFromExistingVmdl(options.AugmentVmdlText, options.DmxFolderRelative, usedNames);

        var mappedPinky = false;
        foreach (var request in requests)
        {
            if (request is null)
                continue;
            mappedPinky |= ProcessRequest(
                request, target, context, options, usedNames, result.Clips, entries);
        }

        foreach (var clip in result.Clips)
        {
            if (!clip.Success)
                result.Errors.Add($"{clip.SourceFileName}: {clip.Error}");
        }

        // Locomotion families are detected on the FINAL (collision-suffixed) clip names so
        // the blend grids reference exactly what the vmdl registers; folder/blend names are
        // made unique against the same name set the clips used.
        IReadOnlyList<LocomotionSetSpec>? locomotionSets = null;
        if (options.DetectLocomotionSets)
        {
            var (sets, reports) = LocomotionSetDetector.Detect(
                entries, usedNames, options.AutoSuffixCollisions);
            result.LocomotionSets.AddRange(reports);
            if (sets.Count > 0)
                locomotionSets = sets;
        }

        // The target's OWN embedded animations ride along every generated vmdl (an FBX
        // picked as target keeps its animation next to the retargeted ones). Skipped when
        // a same-named entry is already registered (idempotent re-runs / augment targets
        // that carry them from an earlier conversion); the retargeted clips claimed their
        // names first, so an embedded take never displaces a converted clip.
        if (target.ExtraAnimFiles is { Count: > 0 })
        {
            foreach (var extra in target.ExtraAnimFiles)
            {
                if (usedNames.Add(extra.Name))
                    entries.Add(extra);
            }
        }

        result.StandaloneVmdl = VmdlWriter.GenerateStandalone(
            target.BaseModelPath, entries, target.VmdlScale, target.DefaultRootBone,
            locomotionSets, target.MeshFilePath, target.MeshImportScale, target.MaterialRemaps,
            target.MeshImportNames);

        if (options.AugmentVmdlText is not null)
        {
            try
            {
                var removedSequences = new List<string>();
                result.AugmentedVmdl = VmdlAugmenter.Augment(
                    options.AugmentVmdlText, entries, out _,
                    new AugmentOptions
                    {
                        DefaultRootBone = target.DefaultRootBone,
                        // The citizen base model copies ring→pinky via its CopyPinky
                        // constraints; when this batch actually drives the pinky, the
                        // augmented vmdl must neutralize them or the exported pinky
                        // channels get overridden at runtime.
                        NeutralizePinkyConstraints = mappedPinky,
                        LocomotionSets = locomotionSets,
                        // Same ownership rule the name seeding above used: pipeline-owned
                        // locomotion folders (every AnimFile sourced under our DMX folder)
                        // are replaceable wholesale on re-runs.
                        DmxFolderRelative = options.DmxFolderRelative,
                        // Stale entries whose DMX the caller found missing on disk: keep
                        // them and the WHOLE vmdl stops compiling ("Node 'X' resolve
                        // failure"), new sequences included.
                        MissingSourceFiles = options.MissingAnimSources,
                    },
                    removedSequences);
                foreach (var name in removedSequences)
                {
                    result.Warnings.Add(
                        $"removed stale sequence '{name}' from the augmented vmdl: its "
                        + "animation source file no longer exists on disk (a missing source "
                        + "fails the whole model recompile)");
                }
            }
            catch (Exception e)
            {
                result.Errors.Add($"vmdl augmentation failed: {e.Message}");
            }
        }

        return result;
    }

    /// <summary>Collects names already present in the existing vmdl's AnimationList that the
    /// batch must not reuse (everything except our own replaceable AnimFiles).</summary>
    private static void SeedUsedNamesFromExistingVmdl(
        string vmdlText, string dmxFolderRelative, HashSet<string> usedNames)
    {
        Kv3Document doc;
        try
        {
            doc = Kv3.Parse(vmdlText);
        }
        catch (FormatException)
        {
            return; // the augmentation step itself surfaces the parse error
        }

        if (doc.Root is not KvObject root || root.GetOrNull("rootNode") is not KvObject rootNode
            || rootNode.GetOrNull("children") is not KvArray children)
            return;
        var animList = children.Items.OfType<KvObject>()
            .FirstOrDefault(o => o.GetString("_class") == "AnimationList");
        if (animList?.GetOrNull("children") is not KvArray items)
            return;

        var folder = dmxFolderRelative.Replace('\\', '/').TrimEnd('/');
        SeedNames(items, folder, usedNames);
    }

    /// <summary>
    /// Recursive seeding step: every named AnimationList node reserves its name EXCEPT our
    /// own replaceable output — AnimFiles whose source lives in the batch's DMX folder, and
    /// locomotion Folders consisting entirely of such AnimFiles (a re-run replaces the whole
    /// folder, so neither its name nor its content may block the new batch's names). Foreign
    /// folders are seeded by name and their content seeded recursively.
    /// </summary>
    private static void SeedNames(KvArray items, string dmxFolder, HashSet<string> usedNames)
    {
        foreach (var node in items.Items.OfType<KvObject>())
        {
            var name = node.GetString("name");
            var cls = node.GetString("_class");

            if (cls == "AnimFile")
            {
                if (!string.IsNullOrEmpty(name) && !IsOurAnimFile(node, dmxFolder))
                    usedNames.Add(name);
                continue;
            }

            if (cls == "Folder")
            {
                if (IsOurLocomotionFolder(node, dmxFolder))
                    continue; // replaceable wholesale — nothing inside reserves a name
                if (!string.IsNullOrEmpty(name))
                    usedNames.Add(name);
                if (node.GetOrNull("children") is KvArray nested)
                    SeedNames(nested, dmxFolder, usedNames);
                continue;
            }

            if (!string.IsNullOrEmpty(name))
                usedNames.Add(name);
        }
    }

    /// <summary>Whether an AnimFile was written by this pipeline: its source_filename sits
    /// inside the batch's DMX folder.</summary>
    private static bool IsOurAnimFile(KvObject node, string dmxFolder)
    {
        var source = (node.GetString("source_filename") ?? "").Replace('\\', '/');
        return dmxFolder.Length == 0
            ? !source.Contains('/')
            : source.StartsWith(dmxFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a Folder is a locomotion group this pipeline spliced: it contains at
    /// least one AnimFile and every AnimFile inside it (recursively) is ours.</summary>
    private static bool IsOurLocomotionFolder(KvObject folderNode, string dmxFolder)
    {
        var sawAnimFile = false;
        return Walk(folderNode) && sawAnimFile;

        bool Walk(KvObject node)
        {
            if (node.GetString("_class") == "AnimFile")
            {
                sawAnimFile = true;
                return IsOurAnimFile(node, dmxFolder);
            }
            if (node.GetOrNull("children") is KvArray children)
            {
                foreach (var child in children.Items.OfType<KvObject>())
                {
                    if (!Walk(child))
                        return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Detection-only entry point for UI listings: imports the file and runs the same
    /// preset-then-auto mapping as conversion, without solving anything. Also reports the
    /// file's take metadata (clip names, in take-index order) so listings can expand a
    /// multi-take file into one entry per take. Throws <see cref="FormatException"/> when
    /// the file is unreadable.
    /// </summary>
    public static InspectResult Inspect(byte[] sourceData, string fileName, byte[]? skeletonData = null)
    {
        ArgumentNullException.ThrowIfNull(sourceData);
        ArgumentNullException.ThrowIfNull(fileName);
        var scene = ImportSource(sourceData, fileName, skeletonData: skeletonData);
        var takeNames = new List<string>(scene.Clips.Count);
        foreach (var clip in scene.Clips)
            takeNames.Add(clip.Name);
        return new InspectResult
        {
            Mapping = ResolveMapping(scene.Skeleton, authoredMapping: scene.AuthoredMapping).Report,
            TakeNames = takeNames,
        };
    }

    // ================================================================ per-request pipeline

    /// <summary>Runs one request end to end. Returns true when at least one clip converted
    /// successfully with a mapping that drives a pinky role (CopyPinky handling).</summary>
    private static bool ProcessRequest(
        RetargetRequest request, RetargetTargetSpec target, TargetContext context,
        BatchOptions options, HashSet<string> usedNames,
        List<ClipResult> clips, List<AnimEntry> entries)
    {
        var sourceId = request.SourceId ?? request.SourceFileName;
        SourceScene scene;
        MappingReportInfo report;
        MappingResult map;
        try
        {
            scene = ImportSource(
                request.SourceData, request.SourceFileName, request.SampleFps, request.SkeletonData,
                request.ExternalBufferResolver);
            (map, report) = ResolveMapping(
                scene.Skeleton, request.MappingOverride, authoredMapping: scene.AuthoredMapping);
            // Import diagnostics (mid-pose exports, cross-stack static disagreements, ...)
            // ride the mapping report so UIs can surface them next to the conversion result.
            foreach (var note in scene.Notes)
                AddNote(report, note);
            scene = MaybeAdoptBindRest(scene, map, report);
        }
        catch (Exception e)
        {
            clips.Add(new ClipResult
            {
                ClipName = FileStem(request.SourceFileName),
                SourceFileName = request.SourceFileName,
                SourceId = sourceId,
                Success = false,
                Error = e.Message,
            });
            return false;
        }

        if (scene.Clips.Count == 0)
        {
            clips.Add(new ClipResult
            {
                ClipName = FileStem(request.SourceFileName),
                SourceFileName = request.SourceFileName,
                SourceId = sourceId,
                Mapping = report,
                Success = false,
                Error = "Source file contains no animation takes.",
            });
            return false;
        }

        var mapsPinky = MapsPinkyRole(map);
        if (mapsPinky && options.AugmentVmdlText is null
            && target.BaseModelPath == RetargetTargetSpec.SboxHumanMalePath)
        {
            // A standalone child vmdl cannot override the base model's AnimConstraintList:
            // the citizen's CopyPinky (ring → pinky orient copy) keeps driving the pinky at
            // runtime even though the DMX carries real pinky channels.
            AddNote(report,
                "Pinky channels are exported, but the base model's CopyPinky constraints "
                + "(ring → pinky copy) cannot be overridden from a standalone vmdl, so the "
                + "pinky will visibly mirror the ring finger's motion in engine — use "
                + "augment mode (add to the existing vmdl) to neutralize them.");
        }
        if (target.UpAxis == TargetUpAxis.ZUpEngine)
        {
            AddNote(report,
                "Target rig is engine-space (Z-up, inches): the DMX declares a Z-up axis "
                + "system so resourcecompiler performs no Y-up conversion (best-effort — "
                + "verify the compiled sequence on engine-space targets).");
        }

        // External clip definitions (Unity .fbx.meta clipAnimations): one output clip per
        // DEFINITION, sliced out of its take; TakeIndex addresses the definitions then
        // (see RetargetRequest.ClipDefinitions).
        if (request.ClipDefinitions is { Count: > 0 } clipDefs)
        {
            return ProcessClipDefinitions(
                request, target, context, options, usedNames, clips, entries,
                scene, map, report, sourceId, mapsPinky, clipDefs);
        }

        // TakeIndex narrows the conversion to a single take (UI per-take entries submit one
        // request per selected take); null keeps the historical convert-all-takes behavior.
        var takeStart = 0;
        var takeEnd = scene.Clips.Count;
        if (request.TakeIndex is { } takeIndex)
        {
            if (takeIndex < 0 || takeIndex >= scene.Clips.Count)
            {
                clips.Add(new ClipResult
                {
                    ClipName = FileStem(request.SourceFileName),
                    SourceFileName = request.SourceFileName,
                    SourceId = sourceId,
                    Mapping = report,
                    Success = false,
                    Error = $"Take index {takeIndex} is out of range: the source file "
                        + $"contains {scene.Clips.Count} take(s).",
                });
                return false;
            }
            takeStart = takeIndex;
            takeEnd = takeIndex + 1;
        }

        var anyPinkySuccess = false;
        for (var take = takeStart; take < takeEnd; take++)
        {
            var clipName = UniqueClipName(
                SanitizeClipName(RequestedClipName(request, scene, take)),
                usedNames, options.AutoSuffixCollisions);
            anyPinkySuccess |= ConvertOne(
                request, target, context, options, usedNames, clips, entries,
                scene, map, report, sourceId, mapsPinky, take, clipName);
        }

        return anyPinkySuccess;
    }

    /// <summary>
    /// The definitions variant of the take loop: each <see cref="ExternalClipDef"/> becomes
    /// its own clip result — the definition's take is located by
    /// <see cref="ExternalClipDef.TakeName"/> (falling back to the file's first take), sliced
    /// to the definition's native-frame range via <see cref="UnityMeta.Slice"/> and solved as
    /// a single-clip scene. Returns true when at least one clip converted successfully with a
    /// pinky-driving mapping.
    /// </summary>
    private static bool ProcessClipDefinitions(
        RetargetRequest request, RetargetTargetSpec target, TargetContext context,
        BatchOptions options, HashSet<string> usedNames,
        List<ClipResult> clips, List<AnimEntry> entries,
        SourceScene scene, MappingResult map, MappingReportInfo report,
        string sourceId, bool mapsPinky, IReadOnlyList<ExternalClipDef> clipDefs)
    {
        var defStart = 0;
        var defEnd = clipDefs.Count;
        if (request.TakeIndex is { } defIndex)
        {
            if (defIndex < 0 || defIndex >= clipDefs.Count)
            {
                clips.Add(new ClipResult
                {
                    ClipName = FileStem(request.SourceFileName),
                    SourceFileName = request.SourceFileName,
                    SourceId = sourceId,
                    Mapping = report,
                    Success = false,
                    Error = $"Clip-definition index {defIndex} is out of range: the request "
                        + $"carries {clipDefs.Count} clip definition(s).",
                });
                return false;
            }
            defStart = defIndex;
            defEnd = defIndex + 1;
        }

        var anyPinkySuccess = false;
        for (var d = defStart; d < defEnd; d++)
        {
            var def = clipDefs[d];
            var requestedName = !string.IsNullOrWhiteSpace(def.Name)
                ? def.Name
                : clipDefs.Count > 1 ? $"{FileStem(request.SourceFileName)}_{d + 1}" : FileStem(request.SourceFileName);
            var clipName = UniqueClipName(
                SanitizeClipName(requestedName), usedNames, options.AutoSuffixCollisions);

            var take = TakeForDefinition(scene, def);
            var sliced = UnityMeta.Slice(scene.Clips[take], def, clipName);

            // Same skeleton/axes, ONE clip: the regular pipeline then solves "take 0" of it.
            var defScene = new SourceScene(
                scene.Skeleton, new[] { sliced }, scene.UnitScaleCm,
                scene.UpAxis, scene.UpAxisSign,
                scene.FrontAxis, scene.FrontAxisSign,
                scene.CoordAxis, scene.CoordAxisSign,
                scene.OriginalUpAxis, scene.Notes)
            {
                AuthoredMapping = scene.AuthoredMapping,
                RestPlacementAuthored = scene.RestPlacementAuthored,
            };

            anyPinkySuccess |= ConvertOne(
                request, target, context, options, usedNames, clips, entries,
                defScene, map, report, sourceId, mapsPinky, take: 0, clipName);
        }

        return anyPinkySuccess;
    }

    /// <summary>The take a definition's frame range refers to: matched by take name when the
    /// definition records one (Unity's <c>takeName</c>, e.g. <c>root|Animation</c>), else —
    /// and when nothing matches — the file's first take.</summary>
    private static int TakeForDefinition(SourceScene scene, ExternalClipDef def)
    {
        if (!string.IsNullOrWhiteSpace(def.TakeName))
        {
            for (var i = 0; i < scene.Clips.Count; i++)
            {
                if (string.Equals(scene.Clips[i].Name, def.TakeName, StringComparison.Ordinal))
                    return i;
            }
        }
        return 0;
    }

    /// <summary>Solves ONE clip (take <paramref name="take"/> of <paramref name="scene"/>)
    /// end to end — solve + cleanup + DMX, plus the optional mirrored twin
    /// (<see cref="RetargetRequest.CreateMirroredVariant"/>) — appending success or failure
    /// <see cref="ClipResult"/>s (failures never abort the batch). Returns true on success
    /// with a pinky-driving mapping.</summary>
    private static bool ConvertOne(
        RetargetRequest request, RetargetTargetSpec target, TargetContext context,
        BatchOptions options, HashSet<string> usedNames,
        List<ClipResult> clips, List<AnimEntry> entries,
        SourceScene scene, MappingResult map, MappingReportInfo report,
        string sourceId, bool mapsPinky, int take, string clipName)
    {
        Clip clip;
        try
        {
            clip = SolveAndClean(request, target, context, scene, map, report, take, clipName);
            EmitClip(request, target, context, options, usedNames, clips, entries, report,
                sourceId, clipName, clip.Frames, clip.Fps, clip.Looping, isMirrored: false);
        }
        catch (Exception e)
        {
            clips.Add(new ClipResult
            {
                ClipName = clipName,
                SourceFileName = request.SourceFileName,
                SourceId = sourceId,
                Mapping = report,
                Success = false,
                Error = e.Message,
            });
            return false;
        }

        if (request.CreateMirroredVariant)
        {
            // The twin gets its own collision-suffixed name and its own failure isolation:
            // a mirror problem (asymmetric rig) fails ONLY the twin, never the primary clip.
            var mirroredName = UniqueClipName(
                SanitizeClipName(clipName + "_M"), usedNames, options.AutoSuffixCollisions);
            try
            {
                // SOURCE-SIDE MIRROR (southpaw G8 mirror fix, gate3_review.md 3.4 and the
                // G8 bisect evidence): the SOURCE clip is mirrored across the source
                // character's sagittal plane and the twin then runs through the COMPLETE
                // unmodified primary pipeline (solver, cleanups, ground alignment, IK
                // bake, orphan re-anchor). The emitted _M clip is a first-class primary
                // clip in every representational respect, indistinguishable from a clip
                // an animator authored mirrored. Target-space channel mirroring, however
                // geometrically exact, produced data the ENGINE's render-side sequence
                // evaluation mangles (measured: a mirrored pelvis channel alone renders
                // the whole model upside down while every CPU-side bone API reports a
                // perfect upright mirror); no data conjugation convention survived that
                // stage, so the mirror moved to the source side where no alien data
                // shape can exist.
                var mirroredScene = SourceMirror.MirrorTake(scene, map, take);
                var mirroredClip = SolveAndClean(
                    request, target, context, mirroredScene, map, report, take, mirroredName);
                EmitClip(request, target, context, options, usedNames, clips, entries, report,
                    sourceId, mirroredName, mirroredClip.Frames, mirroredClip.Fps,
                    clip.Looping, isMirrored: true);
            }
            catch (Exception e)
            {
                clips.Add(new ClipResult
                {
                    ClipName = mirroredName,
                    SourceFileName = request.SourceFileName,
                    SourceId = sourceId,
                    Mapping = report,
                    Success = false,
                    Error = $"mirrored variant: {e.Message}",
                });
            }
        }

        return mapsPinky;
    }

    /// <summary>Shared tail of clip production (primary and mirrored twin): optional footstep
    /// events, DMX serialization, the <see cref="ClipResult"/> and the vmdl
    /// <see cref="AnimEntry"/> — plus the additive (<c>_delta</c>) companion entry when
    /// <see cref="RetargetRequest.CreateAdditiveVariant"/> is on (a second AnimFile REUSING
    /// the clip's DMX with an AnimSubtract child, shipped-citizen shape; no separate
    /// <see cref="ClipResult"/> since no separate DMX exists).</summary>
    private static void EmitClip(
        RetargetRequest request, RetargetTargetSpec target, TargetContext context,
        BatchOptions options, HashSet<string> usedNames,
        List<ClipResult> clips, List<AnimEntry> entries,
        MappingReportInfo report, string sourceId,
        string clipName, List<XForm[]> frames, float fps, bool looping, bool isMirrored)
    {
        var events = GenerateFootsteps(request, target, context, report, frames, fps);
        var dmxFileName = SanitizeFileName(clipName) + ".dmx";
        // Embedded-mesh vmdls and compiled Z-up targets compile root channels 90° about the declared
        // up axis away from the mesh bind; child channels are unaffected. Compensate the
        // serialized copy only so compiled playback matches the solved preview.
        var dmxFrames = !string.IsNullOrEmpty(target.MeshFilePath) || target.UpAxis == TargetUpAxis.ZUpEngine
            ? CompensateEmbeddedMeshRootYaw(frames, target.Rig, target.UpAxis)
            : frames;
        var dmx = DmxWriter.Write(
            target.Rig.Skeleton, new Clip(clipName, fps, looping, dmxFrames), new DmxWriteOptions
            {
                Name = clipName,
                SourceNote = isMirrored ? request.SourceFileName + " (mirrored)" : request.SourceFileName,
                // Design §3: ConstraintDriven (twist/helper) bones keep their joints +
                // bind in the DMX but get NO channels — the model's AnimConstraintList
                // drives them. Face bones are exempt from the exclusion (rest-local
                // channels): nothing drives them in a compiled sequence, and channel-less
                // face joints bake statically (eyes out of sockets in ModelDoc). Custom
                // rigs (FromSkeleton) have NO constraint list — their helpers keep baked
                // channels or twist bones freeze (candy-wrapped wrists).
                // MIRRORED clips use the IDENTICAL exclusion set (data-shape parity with
                // primary clips, southpaw G8 mirror fix): a channel-less constraint-driven
                // bone on a mirrored-body clip is no different from one on any leftward
                // primary pose; the model's AnimConstraintList drives it at runtime either
                // way. (ClipMirror.MirrorSafeExclusions remains available for consumers
                // that need reflection-exact data for excluded bones.)
                ChannelExcludedBones = target.Rig.HelpersAreConstraintDriven
                    ? context.ConstraintDrivenBones
                    : null,
                UpAxisY = target.UpAxis == TargetUpAxis.YUpCm,
            });

        var extractMotion = request.RootMotion == RootMotionMode.Extract;

        // Additive variant: '<clip>_delta' (shipped naming), collision-suffixed like every
        // other batch name. Only the NAME is produced here — the vmdl entry below reuses
        // the base clip's DMX (resourcecompiler does the reference-frame subtraction).
        var deltaName = request.CreateAdditiveVariant
            ? UniqueClipName(
                SanitizeClipName(clipName + "_delta"), usedNames, options.AutoSuffixCollisions)
            : null;

        clips.Add(new ClipResult
        {
            ClipName = clipName,
            SourceFileName = request.SourceFileName,
            SourceId = sourceId,
            DmxFileName = dmxFileName,
            DmxContent = dmx,
            Mapping = report,
            Success = true,
            SolvedFrames = frames,
            Fps = fps,
            Looping = looping,
            ExtractMotion = extractMotion,
            FootstepEvents = events,
            IsMirroredVariant = isMirrored,
            HasAdditiveVariant = deltaName is not null,
            AdditiveVariantName = deltaName,
        });
        var sourceFilename = JoinAssetPath(options.DmxFolderRelative, dmxFileName);
        entries.Add(new AnimEntry
        {
            Name = clipName,
            SourceFilename = sourceFilename,
            Looping = looping,
            ExtractMotion = extractMotion,
            Events = events,
        });
        if (deltaName is not null)
        {
            // The shipped _delta sequences carry the AnimSubtract child and nothing else
            // (no motion extraction, no events) — an additive layer fires no footsteps and
            // extracting root motion from a delta makes no sense.
            entries.Add(new AnimEntry
            {
                Name = deltaName,
                SourceFilename = sourceFilename,
                Looping = looping,
                SubtractAnimName = clipName,
                SubtractFrame = 0,
            });
        }
    }

    /// <summary>
    /// Generates the <c>AE_FOOTSTEP</c> events for a solved clip when the request asks for
    /// them (<see cref="RetargetRequest.GenerateFootstepEvents"/>): plant intervals are
    /// detected on the SOLVED TARGET frames (this is where the engine plays the clip, so
    /// touchdowns must be measured here), each interval start = one footstep
    /// (<see cref="FootstepEvents"/>). Empty when the feature is off or the target rig lacks
    /// the leg chains / character up (noted on the report then).
    /// </summary>
    private static IReadOnlyList<AnimEventEntry> GenerateFootsteps(
        RetargetRequest request, RetargetTargetSpec target, TargetContext context,
        MappingReportInfo report, List<XForm[]> frames, float fps)
    {
        if (!request.GenerateFootstepEvents)
            return Array.Empty<AnimEventEntry>();
        if (context.Up is not { } up || context.FootChains is not { } feet)
        {
            AddNote(report, "Footstep events skipped: " + context.UpOrChainProblem);
            return Array.Empty<AnimEventEntry>();
        }
        return FootstepEvents.Generate(
            frames, target.Rig.Skeleton, feet.Left, feet.Right, up, fps,
            ScaledPlantOptions(target));
    }

    /// <summary>Default plant thresholds are cm-tuned; engine-space rigs are in inches, so
    /// they are scaled by the cm→inch factor to keep the same physical sensitivity (shared by
    /// the foot-plant cleanup and the footstep-event detection).</summary>
    private static FootPlantOptions ScaledPlantOptions(RetargetTargetSpec target)
    {
        var options = new FootPlantOptions();
        if (target.UpAxis == TargetUpAxis.ZUpEngine)
        {
            options.SpeedThresholdCmPerSec *= RetargetTargetSpec.SboxSourceScale;
            options.HeightThresholdCm *= RetargetTargetSpec.SboxSourceScale;
        }
        return options;
    }

    private static bool MapsPinkyRole(MappingResult map)
    {
        foreach (var role in map.RoleToBone.Keys)
        {
            if (role.ToString().StartsWith("Pinky", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Solve one take and run the target-space cleanup + IK baking passes.</summary>
    private static Clip SolveAndClean(
        RetargetRequest request, RetargetTargetSpec target, TargetContext context,
        SourceScene scene, MappingResult map, MappingReportInfo report, int take, string clipName)
    {
        var requested = request.Solve ?? new SolveOptions();
        var handCapture = Motion.HandCaptureRetargeter.Supports(scene, map);
        if(request.MocapCorrections is {} wristSettings)
        {
            Motion.WristPositionOffsets.Validate(wristSettings.WristOffsets);
            if(!handCapture&&wristSettings.WristOffsets.Any(e=>e.Enabled))
                throw new NotSupportedException("Manual wrist offsets currently require camera-relative hand capture.");
            if(handCapture&&wristSettings.WristOffsets.Any(e=>e.Enabled))
                AddNote(report,"Manual wrist position offsets are target edits, not reconstructed observations. Captured rotations and finger motion are retained; confirmed prop contacts and target reach limits take priority. Original missing-hand labels are preserved.");
        }
        if(!handCapture&&scene.CaptureContacts?.Contacts.Any(c=>c.Review==Motion.ContactReview.Confirmed&&!string.IsNullOrEmpty(c.Object))==true)
            throw new NotSupportedException("Confirmed prop constraints currently require camera-relative hand capture. Disable those constraints to preview body motion; object tracks remain available in captured-skeleton export.");
        var captureClavicleDirections = !handCapture && scene.CaptureSpace is not null && requested.TransferModes is null;
        if (captureClavicleDirections)
        {
            // Body-model collar joints are not the target's anatomical rest carriage.
            // SMPL's zero-pose collar->shoulder slopes upward; its reconstructed neutral
            // pose rotates that line down to level. Replaying the same delta onto the
            // Human's already-level collar line lowers its shoulders a second time.
            AddNote(report, "Full-body capture transfers observed clavicle directions while retaining target bone lengths and attachment positions.");
        }
        var solved = handCapture
            ? Motion.HandCaptureRetargeter.Solve(scene, map, target.Rig, target.UpAxis,
                request.MocapCorrections ?? Motion.TargetCorrectionSettings.ForRig(target.Rig,target.UpAxis), take, clipName, requested.TransferFingers,
                diagnostic: note=>AddNote(report,note))
            : ResolveSolver(request, target, context, report).Solve(scene, map, target.Rig, new SolveOptions
        {
            GroundedLegDirections = request.FootPlantCleanup,
            HipScaleHorizontal = requested.HipScaleHorizontal,
            HipScaleVertical = requested.HipScaleVertical,
            TransferFingers = requested.TransferFingers,
            TransferModes = requested.TransferModes,
            CaptureClavicleDirections = captureClavicleDirections,
            ClipIndex = take,
            ClipName = clipName,
        });
        var frames = solved.Frames;

        // ---- embedded-take fidelity: authored local translations pass through ----------
        if (request.PreserveSourceTranslations)
            PassThroughSourceTranslations(scene, target.Rig, frames, take, report);

        // ---- foot-plant cleanup (target space; up axis from the TARGET character frame) ----
        if (request.FootPlantCleanup && !handCapture)
        {
            if (context.Up is { } up && context.FootChains is { } feet)
            {
                // Grounded stance alignment first: levels planted soles against the ground
                // (removes the stance offset a non-stance source rest leaves in the solver's
                // rest-relative foot transfer). Plants are detected on the SOURCE clip —
                // source-space candidates; hip-height rescaling can push the solved target trajectories
                // outside the cm-tuned Kovar thresholds. Composes with the position pinning
                // below (this rotates feet about their own joints; the pinning preserves
                // foot world rotations).
                // Reconstructed bodies: restore the performer's stance width on differently
                // proportioned hips before anything is levelled or anchored.
                if(scene.CaptureSpace is not null)
                {
                    var stance=Motion.CaptureStanceProportion.Apply(frames,scene,map,target.Rig,feet.Left,feet.Right);
                    if(stance.Samples>0)AddNote(report,$"Stance proportion: each foot moved {stance.HalfWidthCorrection:F2} target units toward the body's centre line, because this target's hip joints are wider relative to its legs than the performer's. Leg lengths and foot orientation are preserved.");
                }
                GroundAlignFeet(frames, scene, map, target.Rig.Skeleton, feet, up, solved.Fps, take);

                var plantOptions=ScaledPlantOptions(target);
                // Reconstructed contacts can be unreachable on different proportions.
                // Keep captured target limb lengths fixed instead of stretching to a guess.
                if(scene.CaptureSpace is not null)plantOptions.MaxStretch=0f;
                FootPlant.Apply(
                    frames, target.Rig.Skeleton, feet.Left, feet.Right, up, solved.Fps,
                    plantOptions);

                // Support ground alignment: one constant vertical offset per clip that
                // restores the SOURCE-authored foot-to-ground relationship (southpaw
                // final regeneration package, gate4_review.md 3.1 / DEFERRED.md f2).
                AlignSupportToGround(frames, scene, map, target.Rig, feet, up, report, take);
                MatchSourceFootHeights(frames, scene, map, target.Rig, feet, up, take);
            }
            else
            {
                AddNote(report, "Foot-plant cleanup skipped: " + context.UpOrChainProblem);
            }
        }

        // A reconstructed body has no finger tracks; give its hands a resting shape, not the bind pose.
        if(scene.CaptureSpace is not null&&!handCapture)
        {
            var shoulders=Motion.CaptureShoulderCarriage.Apply(frames,scene.Clips[take].Frames,scene.Skeleton,map,target.Rig);
            if(shoulders>0)AddNote(report,$"Shoulders: {shoulders} clavicle samples carry the performer's change from their own rest shoulder line onto this target's rest shoulder line, instead of copying a differently built clavicle's direction. Arms keep their solved orientation.");
            // After the shoulders are placed: restore the performer's hand spacing on differently proportioned shoulders.
            var arms=Motion.CaptureArmProportion.Apply(frames,scene.Skeleton,map,target.Rig);
            if(arms.Samples>0)AddNote(report,$"Hand spacing: wrists in front of or across the body moved up to {arms.HalfWidthCorrection:F2} target units toward the centre line, because this target's shoulders are wider relative to its arms than the performer's. Arm lengths and hand orientation are preserved; outstretched arms are left alone.");
            var relaxed=Motion.RelaxedHands.Apply(frames,map,target.Rig);
            if(relaxed>0)AddNote(report,$"Hands: this body capture has no finger tracks, so {relaxed} target finger joints hold one authored, slightly curled resting pose instead of the bind pose. It is not captured finger motion.");
        }

        // ---- optional arm effector IK (default off: the solver already matches anatomical
        // directions; arm IK is only for reach-critical work) ----
        if (request.ArmEffectorIk && !handCapture)
        {
            var problem = ArmIkCleanup.Apply(frames, scene, map, target.Rig, context, take);
            if (problem is not null)
                AddNote(report, "Arm effector IK skipped: " + problem);
        }

        // ---- root motion (see class remarks for the Extract ↔ ExtractMotion mapping) ----
        if(request.FootPlantCleanup&&!handCapture&&(request.MocapCorrections?.StabilizeFeet??true))
        {
            var anchored=Motion.CaptureContactRoot.Apply(frames,scene,map,target.Rig,target.UpAxis);
            if(anchored>0)AddNote(report,FormattableString.Invariant($"Travel from contacts: planted feet no longer slide; the body's travel was corrected by up to {anchored:F0} cm to match them."));
            var locking=Motion.CaptureFootLock.Apply(frames,scene,map,target.Rig,target.UpAxis);
            if(locking.ConstrainedSamples>0)AddNote(report,$"Final target foot anchors: {locking.ConstrainedSamples} leg samples; maximum reach residual {locking.MaximumReachResidual:F4} target units; level-floor height drift of up to {locking.MaximumFloorDrift:F2} target units removed from the root. Backend static probabilities are contact suggestions, not measured ground truth.");
            // Keep the whole clip on the floor, including camera-relative captures and dance with few plants.
            if(scene.CaptureSpace is not null)
            {
                var grounded=Motion.CaptureGround.Apply(frames,target.Rig,target.UpAxis,solved.Fps);
                if(grounded>0)AddNote(report,FormattableString.Invariant($"Floor followed through the clip: up to {grounded:F1} cm of drift toward or away from the floor removed."));
                var hovering=Motion.CaptureHover.Apply(frames,target.Rig,target.UpAxis,solved.Fps);
                if(hovering>0)AddNote(report,$"Kept on the floor in {hovering} frames: brief hovering above it or sinking into it removed.");
                var seatedFrames=Motion.CaptureSeat.Apply(frames,scene,map,target.Rig,target.UpAxis);
                if(seatedFrames>0)AddNote(report,$"Seated on the floor in {seatedFrames} frames: hips lowered onto the floor, feet and resting hands held in place.");
            }
        }
        ApplyRootMotion(request.RootMotion, frames, context, report);

        if (request.MocapCorrections is { } corrections)
        {
            if (!handCapture || !corrections.FirstPerson)
                Motion.TargetCorrections.Apply(frames, target.Rig, target.UpAxis, corrections);
            if (corrections.FirstPerson) AddNote(report, "Shoulders and elbows are generated target-rig IK, not measured joints.");
        }

        if (handCapture) AddNote(report, "Camera-relative hand capture uses editable camera placement and target-proportion arm IK. Shoulders and elbows are estimated; unreachable wrists are clamped without stretching bones. Unobserved hands hold their last pose.");
        if(handCapture&&scene.CaptureContacts?.Contacts.Any(c=>c.FingerTargets.Count>0)==true)
            AddNote(report,"Authored finger points apply bounded hinge corrections after arm IK only on their matching target rig and confirmed intervals. Other fingers retain captured articulation. Points are not measured skin surfaces; residual gaps and penetration may remain.");

        // ---- IK helper bones (root_IK, IK targets, ikrule) need real baked channels ----
        if (context.HasIkBakedBones)
            IkBoneBaker.Bake(frames, target.Rig);

        // ---- unmapped limb twist bones follow their joint's roll ------------------------
        // Mocap exports a portable baked armature, so it cannot rely on the target
        // model's runtime constraints. Its preview overrides those helpers too.
        // Keep runtime-owned helpers only for the ordinary model-constraint path.
        var twistCount = TwistBoneFollow.Apply(frames, target.Rig,
            request.MocapCorrections is null && target.Rig.HelpersAreConstraintDriven ? context.ConstraintDrivenBones : null);
        if (twistCount > 0)
            AddNote(report, $"{twistCount} limb deform helper bone(s) follow their "
                + "neighboring joints (left at rest they pinch or candy-wrap the skin).");

        // ---- orphan helper bones ride the hips (LAST: anchored to the FINAL hips, after
        // foot-plant pinning / root motion have settled the trajectory) -------------------
        FollowHipsWithOrphanBones(target.Rig, frames, report);

        var looping = request.LoopingOverride ?? solved.Looping;
        return new Clip(clipName, solved.Fps, looping, frames);
    }

    /// <summary>
    /// Unmapped bone subtrees OUTSIDE the mapped skeleton (no mapped ancestor — cloth and
    /// physics helpers parented to the scene root; real case: a skirt of 48 kilt bones on
    /// a Sketchfab catgirl, siblings of the body root) ride the character rigidly. Left
    /// at their rest locals they freeze at the bind spot while the body animates away
    /// ("one part of the body doesn't move at all") and the mesh skinned across the
    /// boundary shears ("completely stretched"). Each subtree's topmost bone anchors to
    /// the HIPS — or to an extremity (hand/foot/head) when its rest sits clearly closer
    /// to it (a sword resting in the hand follows the hand, not the pelvis). Descendants
    /// ride along on their rest locals; bones on the hips' own ancestor path stay
    /// solver-owned (trajectory). A mapped body branch on a separate control tree is
    /// similarly anchored to the nearest hips-connected mapped joint; its solved rotation
    /// remains unchanged.
    /// </summary>
    private static void FollowHipsWithOrphanBones(
        TargetRig rig, List<XForm[]> frames, MappingReportInfo report)
    {
        var skeleton = rig.Skeleton;
        if (rig.BoneForRole(BoneRole.Hips) is not { } hips || frames.Count == 0)
            return;

        var hipsPath = new HashSet<int>();
        for (var b = hips; b >= 0; b = skeleton[b].ParentIndex)
            hipsPath.Add(b);

        bool HasMappedAncestor(int bone)
        {
            for (var b = skeleton[bone].ParentIndex; b >= 0; b = skeleton[b].ParentIndex)
            {
                if (rig.RoleOf(b) is not null)
                    return true;
            }
            return false;
        }

        var rest = skeleton.RestWorld;

        bool DescendsFrom(int bone, int ancestor)
        {
            for (var b = bone; b >= 0; b = skeleton[b].ParentIndex)
                if (b == ancestor)
                    return true;
            return false;
        }

        // Extremity anchors: adopted only when the orphan rests at less than half its
        // hips distance — near-torso helpers (skirts, tails, capes) stay on the hips,
        // which never mistakes a skirt panel brushing a thigh for hand luggage.
        var extremities = new List<int>();
        foreach (var role in new[]
        {
            BoneRole.HandL, BoneRole.HandR, BoneRole.FootL, BoneRole.FootR, BoneRole.Head,
        })
        {
            if (rig.BoneForRole(role) is { } bone)
                extremities.Add(bone);
        }

        // Topmost orphans: unmapped, off the hips' ancestor path, no mapped ancestor,
        // and the parent is either the scene root path or absent (children of deeper
        // orphans ride along unchanged).
        var tops = new List<(int Bone, int Anchor)>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (rig.RoleOf(i) is not null || hipsPath.Contains(i) || HasMappedAncestor(i))
                continue;
            var parent = skeleton[i].ParentIndex;
            if (parent >= 0 && !hipsPath.Contains(parent))
                continue;

            var anchor = hips;
            var hipsDistance = (rest[i].Pos - rest[hips].Pos).Length();
            var best = hipsDistance * 0.5f;
            foreach (var candidate in extremities)
            {
                var distance = (rest[i].Pos - rest[candidate].Pos).Length();
                if (distance < best)
                {
                    best = distance;
                    anchor = candidate;
                }
            }
            tops.Add((i, anchor));
        }

        // A few constraint-oriented exports place a real mapped body branch on a parallel
        // control hierarchy. The common case is a skinned head under CONTROL_NECK while
        // the deform cervical chain is under the hips. glTF/FBX does not preserve the DCC
        // constraint that joined those trees, so rotations solve but the branch stays at
        // its bind position. Anchor each topmost detached mapped branch to the nearest
        // mapped joint that really descends from the hips, preserving its solved rotation.
        var connectedMapped = Enumerable.Range(0, skeleton.Count)
            .Where(i => rig.RoleOf(i) is not null && DescendsFrom(i, hips))
            .ToList();
        var detachedMapped = new List<(int Bone, int Anchor)>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (rig.RoleOf(i) is null || hipsPath.Contains(i) || DescendsFrom(i, hips)
                || HasMappedAncestor(i) || connectedMapped.Count == 0)
                continue;
            var anchor = connectedMapped
                .OrderBy(candidate => (rest[i].Pos - rest[candidate].Pos).LengthSquared())
                .First();
            detachedMapped.Add((i, anchor));
        }

        // A detached deform head may retain a small control-space bind pitch after the DCC
        // constraint is lost. Infer its gaze only when one local axis already agrees closely
        // with character-forward, then remove the small vertical component. Applying the
        // correction in rest-local space preserves every solved animation delta.
        var neutralHead = -1;
        var neutralHeadCorrection = Quaternion.Identity;
        foreach (var (bone, _) in detachedMapped)
        {
            if (rig.RoleOf(bone) == BoneRole.Head
                && TryDetachedHeadNeutralCorrection(rig, bone, out neutralHeadCorrection))
            {
                neutralHead = bone;
                break;
            }
        }

        if (tops.Count == 0 && detachedMapped.Count == 0)
            return;

        var anchorRestInverse = new Dictionary<int, XForm>();
        foreach (var (_, anchor) in tops)
            anchorRestInverse[anchor] = rest[anchor].Inverse();
        foreach (var (_, anchor) in detachedMapped)
            anchorRestInverse[anchor] = rest[anchor].Inverse();

        foreach (var frame in frames)
        {
            var world = new Skeleton.Pose(frame).ToWorld(skeleton);
            foreach (var (top, anchor) in tops)
            {
                var delta = XForm.Compose(world[anchor], anchorRestInverse[anchor]);
                var parent = skeleton[top].ParentIndex;
                var parentWorld = parent >= 0 ? world[parent] : XForm.Identity;
                frame[top] = XForm.ToLocal(
                    parentWorld, XForm.Compose(delta, rest[top]));
            }

            if (detachedMapped.Count == 0)
                continue;

            // Re-evaluate after moving orphan control roots (the detached branch's actual
            // parent may be inside one). Only position is anchored: its mapped rotation is
            // the solver's result and must remain authoritative.
            world = new Skeleton.Pose(frame).ToWorld(skeleton);
            foreach (var (bone, anchor) in detachedMapped)
            {
                var delta = XForm.Compose(world[anchor], anchorRestInverse[anchor]);
                var rotation = bone == neutralHead
                    ? MathQ.Normalize(world[bone].Rot * neutralHeadCorrection)
                    : world[bone].Rot;
                var desired = new XForm(
                    XForm.Compose(delta, rest[bone]).Pos,
                    rotation);
                var parent = skeleton[bone].ParentIndex;
                frame[bone] = parent < 0
                    ? desired
                    : XForm.ToLocal(world[parent], desired);
            }
        }
        if (tops.Count > 0)
            AddNote(report,
                $"{tops.Count} helper bone subtree(s) outside the mapped skeleton ride the "
                + "character (cloth/physics/prop bones would otherwise freeze at their bind position).");
        if (detachedMapped.Count > 0)
            AddNote(report,
                $"{detachedMapped.Count} mapped body branch(es) on separate control hierarchies "
                + "follow the connected skeleton (exported DCC constraints are not available at runtime).");
        if (neutralHead >= 0)
            AddNote(report,
                "Detached head bind pitch was leveled to the character facing plane while preserving animation.");
    }

    private static void MatchSourceFootHeights(
        List<XForm[]> frames, SourceScene scene, MappingResult map, TargetRig rig,
        (FootChain Left, FootChain Right) feet, Vector3 up, int take)
    {
        if (frames.Count == 0 || !map.RoleToBone.TryGetValue(BoneRole.Hips, out var srcHips)
            || rig.BoneForRole(BoneRole.Hips) is not int tgtHips)
            return;
        var source = scene.Skeleton;
        var target = rig.Skeleton;
        if (!scene.RestPlacementAuthored && !RestNormalizer.IsAnatomicalRest(source, map, source.RestWorld))
            return; // Bone-length-only rests cannot calibrate an anatomical support height.
        var srcFrames = scene.Clips[take].Frames;
        if (srcFrames.Count != frames.Count)
            return;
        var srcUp = scene.UpAxis switch
        {
            0 => new Vector3(scene.UpAxisSign, 0, 0),
            2 => new Vector3(0, 0, scene.UpAxisSign),
            _ => new Vector3(0, scene.UpAxisSign, 0),
        };
        up = GroundUp(up);
        var worlds = new XForm[frames.Count][];
        var restGround = float.PositiveInfinity;
        var motionGround = float.PositiveInfinity;
        foreach (var rest in source.RestWorld)
            restGround = MathF.Min(restGround, Vector3.Dot(rest.Pos, srcUp));
        for (var f = 0; f < frames.Count; f++)
        {
            worlds[f] = new Skeleton.Pose(srcFrames[f]).ToWorld(source);
            foreach (var bone in worlds[f])
                motionGround = MathF.Min(motionGround, Vector3.Dot(bone.Pos, srcUp));
        }
        var placement = scene.RestPlacementAuthored ? 0f : motionGround - restGround;
        var corrections = new List<(FootChain Chain, Vector3[] Goals)>();
        var lowerPelvis = new float[frames.Count];
        foreach (var (footRole, toeRole, chain) in new[]
        {
            (BoneRole.FootL, BoneRole.ToeL, feet.Left), (BoneRole.FootR, BoneRole.ToeR, feet.Right),
        })
        {
            if (!map.RoleToBone.TryGetValue(footRole, out var foot)
                || !map.RoleToBone.TryGetValue(toeRole, out var toe) || chain.Toe is not int tgtToe)
                continue;
            float Support(IReadOnlyList<XForm> pose, int ankle, int tip, Vector3 vertical)
                => MathF.Min(Vector3.Dot(pose[ankle].Pos, vertical), Vector3.Dot(pose[tip].Pos, vertical));
            var srcRest = Support(source.RestWorld, foot, toe, srcUp);
            var tgtRest = Support(target.RestWorld, chain.Ankle, tgtToe, up);
            // Animation-only FBXs can store a mid-stride rest, with one foot lifted.
            // That raised foot is not a ground reference: subtracting it drives its
            // planted frames below the floor. An authored rest never needs a higher
            // support baseline than the lowest support actually reached by that foot.
            if (scene.RestPlacementAuthored)
                foreach (var pose in worlds)
                    srcRest = MathF.Min(srcRest, Support(pose, foot, toe, srcUp));
            var srcHeight = Vector3.Dot(source.RestWorld[srcHips].Pos, srcUp) - srcRest;
            if (srcHeight <= .001f)
                continue;
            var ratio = Math.Clamp((Vector3.Dot(target.RestWorld[tgtHips].Pos, up) - tgtRest) / srcHeight, .25f, 4f);
            var goals = new Vector3[frames.Count];
            for (var f = 0; f < frames.Count; f++)
            {
                var world = new Skeleton.Pose(frames[f]).ToWorld(target);
                var desired = tgtRest + (Support(worlds[f], foot, toe, srcUp) - srcRest - placement) * ratio;
                goals[f] = world[chain.Ankle].Pos + up * (desired - Support(world, chain.Ankle, tgtToe, up));
                var reach = Vector3.Distance(world[chain.Hip].Pos, world[chain.Knee].Pos)
                    + Vector3.Distance(world[chain.Knee].Pos, world[chain.Ankle].Pos);
                var toHip = world[chain.Hip].Pos - goals[f];
                var vertical = Vector3.Dot(toHip, up);
                var horizontal = toHip - up * vertical;
                if (horizontal.LengthSquared() < reach * reach)
                    lowerPelvis[f] = MathF.Max(lowerPelvis[f], vertical
                        - MathF.Sqrt(reach * reach - horizontal.LengthSquared()));
            }
            corrections.Add((chain, goals));
        }
        // At full extension, lower the pelvis only as far as needed to reach BOTH
        // contacts. Stretching bones or accepting an unreachable goal leaves a hover.
        for (var f = 0; f < frames.Count; f++)
        {
            if (lowerPelvis[f] <= 0f) continue;
            var parent = target[tgtHips].ParentIndex;
            var shift = -up * lowerPelvis[f];
            if (parent >= 0)
                shift = Vector3.Transform(shift, Quaternion.Conjugate(
                    new Skeleton.Pose(frames[f]).ToWorld(target)[parent].Rot));
            frames[f][tgtHips].Pos += shift;
        }
        foreach (var (chain, goals) in corrections)
        {
            // Vertical IK preserves horizontal travel and foot rotation; it never scales bones.
            EffectorIk.ApplyGoals(frames, target,
                new LimbChain { Upper = chain.Hip, Lower = chain.Knee, End = chain.Ankle },
                goals, ArmIkCleanup.RestBendAxis(target, chain.Hip, chain.Knee, chain.Ankle), soften: 0f);
        }
    }

    private static bool TryDetachedHeadNeutralCorrection(
        TargetRig rig, int head, out Quaternion localCorrection)
    {
        localCorrection = Quaternion.Identity;
        CharacterFrame frame;
        try
        {
            frame = CharacterFrame.Compute(
                rig.Skeleton, rig.ToMappingResult(), rig.Skeleton.RestWorld);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var restRotation = MathQ.Normalize(rig.Skeleton.RestWorld[head].Rot);
        var gaze = Vector3.Zero;
        var agreement = float.NegativeInfinity;
        foreach (var localAxis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var axis = Vector3.Transform(localAxis, restRotation);
            var dot = Vector3.Dot(axis, frame.Forward);
            if (MathF.Abs(dot) <= agreement)
                continue;
            agreement = MathF.Abs(dot);
            gaze = dot < 0f ? -axis : axis;
        }

        var vertical = Vector3.Dot(gaze, frame.Up);
        const float minAgreement = 0.94f; // local axis must be within 20° of facing
        const float minPitch = 0.5f * MathF.PI / 180f;
        const float maxPitch = 12f * MathF.PI / 180f;
        var pitch = MathF.Asin(Math.Clamp(vertical, -1f, 1f));
        if (agreement < minAgreement || MathF.Abs(pitch) < minPitch || MathF.Abs(pitch) > maxPitch)
            return false;

        var levelGaze = gaze - frame.Up * vertical;
        if (levelGaze.LengthSquared() < 1e-8f)
            return false;
        levelGaze = Vector3.Normalize(levelGaze);

        var worldCorrection = MathQ.FromTo(gaze, levelGaze);
        localCorrection = MathQ.Normalize(
            Quaternion.Conjugate(restRotation) * worldCorrection * restRotation);
        return true;
    }

    /// <summary>
    /// The engine-side yaw correction for embedded meshes and compiled Z-up targets (see
    /// <see cref="EmitClip"/>): the compiler's source-axis conversion for ROOT-LEVEL
    /// animation channels lands 90° about up away from where it puts the mesh bind.
    /// Pre-rotating root locals by the inverse importer yaw (−90° for Y-up,
    /// +90° for Z-up) makes compiled playback match the bind; children are parent-relative.
    /// </summary>
    private static List<XForm[]> CompensateEmbeddedMeshRootYaw(
        IReadOnlyList<XForm[]> frames, TargetRig rig, TargetUpAxis upAxis)
    {
        var yUp = upAxis == TargetUpAxis.YUpCm;
        var axis = yUp ? Vector3.UnitY : Vector3.UnitZ;
        var yaw = Quaternion.CreateFromAxisAngle(axis, (yUp ? -1f : 1f) * MathF.PI * 0.5f);
        var skeleton = rig.Skeleton;
        var result = new List<XForm[]>(frames.Count);
        foreach (var frame in frames)
        {
            var copy = new XForm[frame.Length];
            for (var i = 0; i < frame.Length; i++)
            {
                copy[i] = i < skeleton.Count && skeleton[i].ParentIndex < 0
                    ? new XForm(
                        Vector3.Transform(frame[i].Pos, yaw),
                        MathQ.Normalize(yaw * frame[i].Rot))
                    : frame[i];
            }
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Test seam for <see cref="CompensateEmbeddedMeshRootYaw"/> (gate bisects
    /// re-serialize mutated frames with the same compensation the pipeline applies).</summary>
    public static List<XForm[]> TestHook_CompensateEmbeddedMeshRootYaw(
        IReadOnlyList<XForm[]> frames, RetargetTargetSpec target)
        => !string.IsNullOrEmpty(target.MeshFilePath) || target.UpAxis == TargetUpAxis.ZUpEngine
            ? CompensateEmbeddedMeshRootYaw(frames, target.Rig, target.UpAxis)
            : frames.ToList();

    /// <summary>Test seam for <see cref="FollowHipsWithOrphanBones"/>.</summary>
    public static void TestHook_FollowOrphans(TargetRig rig, List<XForm[]> frames)
        => FollowHipsWithOrphanBones(rig, frames, new MappingReportInfo
        {
            ProfileName = "test",
            Source = MappingSource.Manual,
            Confidence = 1f,
            NeedsUserDecision = false,
            MappedRoleCount = 0,
            SkeletonSignature = "",
        });

    /// <summary>
    /// <see cref="RetargetRequest.PreserveSourceTranslations"/>: per-frame source local
    /// translations onto same-named target bones. Names are compared engine-sanitized
    /// (non-alphanumerics → <c>_</c>: the compiled-model rig the editor rebuilds carries
    /// <c>Bip01_R_Hand</c> for the file's <c>Bip01 R Hand</c>). A matched bone whose REST
    /// translation disagrees between the rigs is skipped — the name collision is then
    /// coincidence, not the same joint. The hips and its ancestors always stay
    /// solver-owned (trajectory, root-motion modes, hip rescale).
    /// </summary>
    private static void PassThroughSourceTranslations(
        SourceScene scene, TargetRig rig, List<XForm[]> frames, int take,
        MappingReportInfo report)
    {
        var src = scene.Skeleton;
        var tgt = rig.Skeleton;
        var srcClip = scene.Clips[take];
        if (srcClip.Frames.Count != frames.Count)
        {
            AddNote(report, "Source translations not preserved: frame counts differ "
                + $"({srcClip.Frames.Count} source vs {frames.Count} solved).");
            return;
        }

        static string Key(string name)
        {
            Span<char> c = stackalloc char[name.Length];
            for (var i = 0; i < name.Length; i++)
                c[i] = char.IsLetterOrDigit(name[i]) ? char.ToLowerInvariant(name[i]) : '_';
            return new string(c);
        }

        var srcByName = new Dictionary<string, int>();
        for (var i = 0; i < src.Count; i++)
        {
            // Duplicate keys are ambiguous - poison them rather than guess.
            var key = Key(src[i].Name);
            srcByName[key] = srcByName.ContainsKey(key) ? -1 : i;
        }

        var excluded = new HashSet<int>();
        if (rig.BoneForRole(BoneRole.Hips) is { } hips)
            for (var b = hips; b >= 0; b = tgt[b].ParentIndex)
                excluded.Add(b);

        var applied = 0;
        for (var t = 0; t < tgt.Count; t++)
        {
            if (excluded.Contains(t)
                || !srcByName.TryGetValue(Key(tgt[t].Name), out var s) || s < 0)
                continue;
            // Same joint check: rest translations must agree (same rig, same space).
            var restDelta = (src[s].RestLocal.Pos - tgt[t].RestLocal.Pos).Length();
            if (restDelta > MathF.Max(1f, 0.1f * tgt[t].RestLocal.Pos.Length()))
                continue;
            for (var f = 0; f < frames.Count; f++)
                frames[f][t] = new XForm(srcClip.Frames[f][s].Pos, frames[f][t].Rot);
            applied++;
        }
        if (applied > 0)
            AddNote(report, $"Embedded take: authored local translations preserved on {applied} bones.");
    }

    /// <summary>
    /// Picks the solver for a request (design §10 routing): the
    /// <see cref="GeometricSolver"/> unless the request selects
    /// <see cref="SolverKind.DeepLearning"/>, which needs the spec's
    /// <see cref="RetargetTargetSpec.DlWeights"/> and is built once per batch (the parsed
    /// model is cached on the <see cref="TargetContext"/>). A DL request also notes that
    /// per-role mapping is ignored by that solver.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown (and surfaced as the clip's
    /// error) when DL is requested without weights.</exception>
    private static IRetargetSolver ResolveSolver(
        RetargetRequest request, RetargetTargetSpec target, TargetContext context,
        MappingReportInfo report)
    {
        if (request.Solver != SolverKind.DeepLearning)
            return new GeometricSolver();

        if (target.DlWeights is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                "Deep-learning solver requested but RetargetTargetSpec.DlWeights is not set "
                + "(read Assets/humanoid_mocap/dl/same_v1.weights and pass its bytes).");
        }

        AddNote(report,
            "Deep-learning solver (SAME, experimental): skeleton-agnostic — per-role mapping "
            + "is not used (hips/alignment heuristics only); finger bones stay at rest.");
        return context.GetDlSolver(target.DlWeights);
    }

    /// <summary>
    /// Normalized-rest foot pitch beyond which (toe approaching/above ankle level) the rest
    /// cannot be a flat stance — measured stance rests sit at −11°…−51°; the repro artifact
    /// rig measured +2.7° on one foot.
    /// </summary>
    private const float StancePitchMaxDeg = -5f;

    /// <summary>Left/right normalized-rest foot pitch asymmetry beyond which the rest is not
    /// a (symmetric) stance — measured stance rests differ ≤ 2.4°; the repro artifact rig
    /// 13.8°.</summary>
    private const float StancePitchAsymmetryDeg = 6f;

    /// <summary>
    /// The grounded-foot stance recalibration step (<see cref="FootGroundAlign"/>). Runs ONLY
    /// when the source's NORMALIZED rest — the solver's delta reference — is implausible as a
    /// flat stance (a foot's toe at/above ankle level, or the two feet resting asymmetrically;
    /// both measured on the repro rig, where T-pose normalization of a bent-leg rest swings
    /// the feet 14–26° away from the rig's true stance). On plausible stance rests the solver's
    /// rest-relative foot transfer is already faithful, and planted-sole deviations are
    /// genuine articulation (boxing stances, heel rolls) that must NOT be flattened. Plant
    /// intervals are detected on the SOURCE clip (cm space, default Kovar thresholds).
    /// Best-effort — silently skipped when the source maps no complete leg chains + toes or a
    /// character frame is not computable (the foot transfer is then simply left as solved).
    /// </summary>
    private static void GroundAlignFeet(
        List<XForm[]> frames, SourceScene scene, MappingResult map, SkeletonModel targetSkeleton,
        (FootChain Left, FootChain Right) targetFeet, Vector3 targetUp, float fps, int take)
    {
        FootChain? SourceChain(BoneRole upper, BoneRole lower, BoneRole foot, BoneRole toe)
            => map.RoleToBone.TryGetValue(upper, out var hip)
               && map.RoleToBone.TryGetValue(lower, out var knee)
               && map.RoleToBone.TryGetValue(foot, out var ankle)
                ? new FootChain
                {
                    Hip = hip,
                    Knee = knee,
                    Ankle = ankle,
                    Toe = map.RoleToBone.TryGetValue(toe, out var t) ? t : null,
                }
                : null;

        var srcLeft = SourceChain(BoneRole.UpperLegL, BoneRole.LowerLegL, BoneRole.FootL, BoneRole.ToeL);
        var srcRight = SourceChain(BoneRole.UpperLegR, BoneRole.LowerLegR, BoneRole.FootR, BoneRole.ToeR);
        if (srcLeft?.Toe is not int srcToeL || srcRight?.Toe is not int srcToeR)
            return; // toe-less sources take the solver's virtual-foot fallback instead

        // ---- rest-stance plausibility (normalized rest, the solver's delta reference) ----
        float pitchL, pitchR;
        Vector3 srcUp;
        try
        {
            var (srcNorm, _) = RestNormalizer.Normalize(scene.Skeleton, map);
            var srcCanon = CanonicalFrames.Build(scene.Skeleton, map, srcNorm.WorldRest);
            srcUp = Vector3.Normalize(srcCanon.CharacterUp);

            float RestPitchDeg(int foot, int toe)
            {
                var dir = srcNorm.WorldRest[toe].Pos - srcNorm.WorldRest[foot].Pos;
                if (dir.LengthSquared() < 1e-8f)
                    return 0f;
                var s = Vector3.Dot(Vector3.Normalize(dir), srcUp);
                return MathF.Asin(Math.Clamp(s, -1f, 1f)) * (180f / MathF.PI);
            }

            pitchL = RestPitchDeg(srcLeft.Ankle, srcToeL);
            pitchR = RestPitchDeg(srcRight.Ankle, srcToeR);
        }
        catch (ArgumentException)
        {
            return;
        }

        var restIsStance = pitchL < StancePitchMaxDeg && pitchR < StancePitchMaxDeg
            && MathF.Abs(pitchL - pitchR) <= StancePitchAsymmetryDeg;
        if (restIsStance)
            return;

        var srcFrames = scene.Clips[take].Frames;
        var (plantsL, plantsR) = FootPlant.DetectPlantIntervals(
            srcFrames, scene.Skeleton, srcLeft, srcRight, srcUp, fps);
        if (plantsL.Count == 0 && plantsR.Count == 0)
            return;

        FootGroundAlign.Apply(
            frames, targetSkeleton, targetFeet.Left, targetFeet.Right, targetUp,
            plantsL, plantsR);
    }

    /// <summary>
    /// Restores the clip's SOURCE-authored foot-to-ground relationship with ONE constant
    /// vertical offset per clip (southpaw final regeneration package: DEFERRED.md f2 and
    /// gate4_review.md 3.1). The solver's vertical reference is the source REST pelvis;
    /// mid-pose exports whose kept node rest erased the authored crouch leave every solved
    /// frame hovering by exactly the erased crouch depth (measured: stance +5.9 cm, steps
    /// +7.8 to +10.3, slips ~+9, while the source FBX is grounded everywhere). Fix, the
    /// generalization of the BVH ClipPlacementOffset vertical logic to every source: measure
    /// the clip's minimum foot support (ankles + toes) in the SOLVED target frames and shift
    /// the whole clip vertically so that support equals the TARGET rest support plus the
    /// source clip's own authored support delta (source clip support minus source rest
    /// support, hip-height scaled). One constant per clip preserves every within-clip lift,
    /// heel raise and step cycle exactly: authored articulation is never flattened and an
    /// authored jump stays airborne (its source support delta rides along).
    /// </summary>
    private static void AlignSupportToGround(
        List<XForm[]> frames, SourceScene scene, MappingResult map, TargetRig rig,
        (FootChain Left, FootChain Right) feet, Vector3 up, MappingReportInfo report, int take)
    {
        if (frames.Count == 0)
            return;
        var skeleton = rig.Skeleton;
        var rest = skeleton.RestWorld;
        var srcClip = scene.Clips[take];
        var src = scene.Skeleton;

        // The GROUND normal is the rig-space vertical axis, not the character-lean up:
        // the character frame's up tilts a few degrees with the rest posture, and a
        // tilted dot-metric slants the ground line across a traveling clip (measured:
        // 28 cm of false offset on a long corpus walk). Snap to the dominant axis.
        up = GroundUp(up);

        // ---- support-bone set: the roles mapped on BOTH rigs (metrics must compare
        // the same anatomical points or anthropometric offsets leak into the gap) ----
        var supportRoles = new List<BoneRole>();
        foreach (var role in new[] { BoneRole.FootL, BoneRole.FootR, BoneRole.ToeL, BoneRole.ToeR })
        {
            if (map.RoleToBone.ContainsKey(role) && rig.BoneForRole(role) is not null)
                supportRoles.Add(role);
        }
        var sourceMeasured = supportRoles.Contains(BoneRole.FootL) && supportRoles.Contains(BoneRole.FootR)
            && srcClip.Frames.Count > 0 && srcClip.Frames[0].Length == src.Count;
        if (!sourceMeasured)
        {
            // No reliable common support metric: fall back to the ankles the target
            // chains provide and a zero source gap (best guess: grounded).
            supportRoles.Clear();
        }

        var tgtSupportBones = new List<int>();
        foreach (var role in supportRoles)
            tgtSupportBones.Add(rig.BoneForRole(role)!.Value);
        if (tgtSupportBones.Count == 0)
        {
            tgtSupportBones.Add(feet.Left.Ankle);
            tgtSupportBones.Add(feet.Right.Ankle);
        }

        var tgtRestSupport = float.MaxValue;
        foreach (var b in tgtSupportBones)
            tgtRestSupport = MathF.Min(tgtRestSupport, Vector3.Dot(rest[b].Pos, up));

        var solvedSupport = float.MaxValue;
        foreach (var frame in frames)
        {
            var world = new Skeleton.Pose(frame).ToWorld(skeleton);
            foreach (var b in tgtSupportBones)
                solvedSupport = MathF.Min(solvedSupport, Vector3.Dot(world[b].Pos, up));
        }

        // ---- source-authored support gap --------------------------------------
        float sourceRel = 0f;
        if (sourceMeasured)
        {
            var srcSupportBones = new List<int>();
            foreach (var role in supportRoles)
                srcSupportBones.Add(map.RoleToBone[role]);

            var srcUp = scene.UpAxis switch
            {
                0 => new Vector3(scene.UpAxisSign, 0f, 0f),
                2 => new Vector3(0f, 0f, scene.UpAxisSign),
                _ => new Vector3(0f, scene.UpAxisSign, 0f),
            };
            var srcHips = map.RoleToBone.TryGetValue(BoneRole.Hips, out var sh) ? sh : -1;

            var srcRestSupport = float.MaxValue;
            var srcRestGround = float.MaxValue;
            for (var b = 0; b < src.Count; b++)
            {
                var h = Vector3.Dot(src.RestWorld[b].Pos, srcUp);
                srcRestGround = MathF.Min(srcRestGround, h);
            }
            foreach (var b in srcSupportBones)
                srcRestSupport = MathF.Min(srcRestSupport, Vector3.Dot(src.RestWorld[b].Pos, srcUp));

            var srcClipSupport = float.MaxValue;
            var srcMotionGround = float.MaxValue;
            foreach (var frame in srcClip.Frames)
            {
                var world = new Skeleton.Pose(frame).ToWorld(src);
                foreach (var b in srcSupportBones)
                    srcClipSupport = MathF.Min(srcClipSupport, Vector3.Dot(world[b].Pos, srcUp));
                if (!scene.RestPlacementAuthored)
                {
                    for (var b = 0; b < src.Count; b++)
                        srcMotionGround = MathF.Min(srcMotionGround, Vector3.Dot(world[b].Pos, srcUp));
                }
            }

            // AUTHORED placements (FBX): the rest stands on the authored ground, so the
            // gap is simply clip support minus rest support (same bones, same space).
            // UN-PLACED sources (BVH capture volumes): absolute placement is meaningless,
            // so both terms go placement-free: the clip's support above its OWN motion
            // ground (lowest joint anywhere, incl. static origin markers) compared to the
            // rest pose's support above ITS lowest joint. A grounded walk reads ~0, a
            // jump reads its true airborne height, a crawl reads its raised feet.
            float gap;
            if (scene.RestPlacementAuthored)
                gap = srcClipSupport - srcRestSupport;
            else
                gap = (srcClipSupport - srcMotionGround) - (srcRestSupport - srcRestGround);

            // Hip-height ratio scales the (small) source gap into target units,
            // clamped against degenerate rest measurements.
            var ratio = 1f;
            if (srcHips >= 0 && rig.BoneForRole(BoneRole.Hips) is { } tgtHips)
            {
                var srcHipHeight = Vector3.Dot(src.RestWorld[srcHips].Pos, srcUp) - srcRestSupport;
                var tgtHipHeight = Vector3.Dot(rest[tgtHips].Pos, up) - tgtRestSupport;
                if (srcHipHeight > 1e-3f && float.IsFinite(tgtHipHeight / srcHipHeight))
                    ratio = Math.Clamp(tgtHipHeight / srcHipHeight, 0.25f, 4f);
            }
            sourceRel = gap * ratio;
        }

        var delta = tgtRestSupport + sourceRel - solvedSupport;
        if (!float.IsFinite(delta) || MathF.Abs(delta) > 100f)
        {
            AddNote(report, $"Support ground alignment skipped: implausible offset {delta:0.0}.");
            return;
        }
        if (MathF.Abs(delta) < 0.01f)
            return; // already aligned

        var shift = up * delta;
        foreach (var frame in frames)
        {
            for (var i = 0; i < skeleton.Count; i++)
            {
                if (skeleton[i].ParentIndex < 0)
                    frame[i] = new XForm(frame[i].Pos + shift, frame[i].Rot);
            }
        }
        AddNote(report, sourceMeasured
            ? $"Support ground alignment: {delta:0.00} vertical offset applied "
              + $"(source-authored support delta {sourceRel:0.00})."
            : $"Support ground alignment: {delta:0.00} vertical offset applied "
              + "(source feet unmapped; clip support aligned to target rest support).");
    }

    private static Vector3 GroundUp(Vector3 up)
    {
        var absolute = Vector3.Abs(up);
        return absolute.X >= absolute.Y && absolute.X >= absolute.Z
            ? new Vector3(MathF.Sign(up.X), 0, 0)
            : absolute.Y >= absolute.Z
                ? new Vector3(0, MathF.Sign(up.Y), 0)
                : new Vector3(0, 0, MathF.Sign(up.Z));
    }

    private static void ApplyRootMotion(
        RootMotionMode mode, List<XForm[]> frames, TargetContext context, MappingReportInfo report)
    {
        if (mode == RootMotionMode.Off)
            return;

        if (context.HipsIndex is not { } hips)
        {
            AddNote(report, $"Root motion ({mode}) skipped: target rig maps no Hips role.");
            return;
        }
        if (context.Up is not { } up)
        {
            AddNote(report, $"Root motion ({mode}) skipped: {context.UpOrChainProblem}");
            return;
        }

        if (mode == RootMotionMode.Extract && context.DedicatedRootIndex is null)
        {
            // s&box-style target: pelvis is parentless and there is no separate translating
            // root bone (root_IK is IkBaked). Bone-level extraction is impossible — the vmdl
            // AnimFile entry carries an ExtractMotion node instead (compile-time extraction).
            AddNote(report,
                "Root-motion extract: target has no dedicated animated root bone distinct from "
                + "the hips; frames left as solved and motion extraction delegated to the vmdl "
                + "AnimFile ExtractMotion node.");
            return;
        }

        var root = context.DedicatedRootIndex ?? hips;
        var worldUp = GroundUp(up);
        // Skeleton-aware overload: hips world via real parent-chain FK, locals re-derived
        // against the actual parent (HipsParentIsRoot is ignored by this overload).
        RootMotion.Apply(frames, context.Rig.Skeleton, new RootMotionAxes
        {
            // Match the world vertical used by support-height cleanup. A slightly
            // leaning target torso must not tilt the plane used to remove travel;
            // otherwise this final pass reintroduces vertical drift into grounded feet.
            Up = worldUp,
            RootIndex = root,
            HipsIndex = hips,
            HipsParentIsRoot = context.HipsParentIsRoot,
        }, mode);
        if (mode == RootMotionMode.InPlace && frames.Count > 0)
        {
            // Animation-only files may start far from their static reference pose.
            // Removing travel alone freezes that offset into the clip, so blending
            // directions moves the mesh away from its controller. Center every clip
            // on the target bind hips, preserving vertical motion and local rotations.
            var skeleton = context.Rig.Skeleton;
            var center = Vector3.Zero;
            foreach (var frame in frames)
                center += FkUtil.BoneWorld(frame, skeleton, hips).Pos;
            var offset = skeleton.RestWorld[hips].Pos - center / frames.Count;
            offset -= Vector3.Dot(offset, worldUp) * worldUp;
            foreach (var frame in frames)
                for (var b = 0; b < skeleton.Count; b++)
                    if (skeleton[b].ParentIndex < 0)
                        frame[b] = new XForm(frame[b].Pos + offset, frame[b].Rot);
        }
        AddNote(report, mode == RootMotionMode.Extract
            ? $"Root motion extracted onto dedicated root bone (index {root})."
            : "Root motion removed (in-place clip).");
    }

    private static void AddNote(MappingReportInfo report, string note)
    {
        if (!report.Notes.Contains(note))
            report.Notes.Add(note);
    }

    /// <summary>
    /// Mid-pose FBX sources: adopts the importer's bind-rest alternate skeleton when it
    /// makes the character stand STRAIGHTER along the file's own up axis. Solving on a
    /// posed rest breaks the solver's anatomical reasoning on exactly the posed limbs
    /// (measured 48° mean world-rot off ground truth on a real rig, left foot 124°); but
    /// a bind rest can also tilt the character-up estimate (shoulders behind hips on one
    /// real rig tilted it 7°, starving plant/stance detection), so the straighter rest
    /// wins. Clips are authored playback either way — same bones, same order.
    /// </summary>
    private static SourceScene MaybeAdoptBindRest(
        SourceScene scene, MappingResult map, MappingReportInfo report)
    {
        if (scene.MidPoseBindSkeleton is not { } bindSkeleton)
            return scene;

        var fileUp = scene.UpAxis switch
        {
            0 => new Vector3(scene.UpAxisSign, 0, 0),
            2 => new Vector3(0, 0, scene.UpAxisSign),
            _ => new Vector3(0, scene.UpAxisSign, 0),
        };

        float UpTiltDeg(SkeletonModel skeleton)
        {
            var (norm, _) = RestNormalizer.Normalize(skeleton, map);
            var up = Vector3.Normalize(
                CanonicalFrames.Build(skeleton, map, norm.WorldRest).CharacterUp);
            return MathF.Acos(Math.Clamp(Vector3.Dot(up, fileUp), -1f, 1f)) * 180f / MathF.PI;
        }

        try
        {
            float tiltStatics = UpTiltDeg(scene.Skeleton);
            float tiltBind = UpTiltDeg(bindSkeleton);
            if (tiltBind > tiltStatics + 1f)
            {
                AddNote(report,
                    $"mid-pose export: kept the exported pose as rest - the file's bind "
                    + $"data stands {tiltBind:0.#} deg off the file up axis "
                    + $"(vs {tiltStatics:0.#} deg)");
                return scene;
            }

            AddNote(report,
                "mid-pose export: rest pose restored from the file's own bind data "
                + "(clips keep their authored playback)");
            return new SourceScene(
                bindSkeleton, scene.Clips, scene.UnitScaleCm,
                scene.UpAxis, scene.UpAxisSign,
                scene.FrontAxis, scene.FrontAxisSign,
                scene.CoordAxis, scene.CoordAxisSign,
                scene.OriginalUpAxis, scene.Notes)
            {
                AuthoredMapping = scene.AuthoredMapping,
                RestPlacementAuthored = scene.RestPlacementAuthored,
            };
        }
        catch (Exception)
        {
            // Anatomy not measurable (mapping lacks the frame's roles, degenerate rest):
            // keep today's behavior.
            return scene;
        }
    }

    // ================================================================ import + mapping

    /// <summary>
    /// Imports source-file bytes exactly like conversion does: the extension picks the
    /// importer; unknown extensions fall back to content sniffing (FBX binary magic / ASCII
    /// header token / BVH "HIERARCHY" / GLB 'glTF' magic / glTF JSON). Public so UI listings
    /// inspect files through the SAME import path the pipeline uses (no duplicate sniffing
    /// logic caller-side).
    /// </summary>
    /// <param name="data">Raw file bytes.</param>
    /// <param name="fileName">File name; only the extension is consulted.</param>
    /// <param name="sampleFps">Resample rate for the imported clips; null = importer default
    /// (30 fps).</param>
    /// <param name="skeletonData">Companion skeleton bytes for animation-only formats
    /// (RenderWare .anm/.an5 need the model .dff — see
    /// <see cref="RetargetRequest.SkeletonData"/>); ignored by self-contained formats.</param>
    /// <exception cref="FormatException">Thrown when the bytes are not a readable
    /// FBX/BVH/glTF/RenderWare animation.</exception>
    public static SourceScene ImportSource(
        byte[] data, string fileName, float? sampleFps = null, byte[]? skeletonData = null,
        Func<string, byte[]>? externalBufferResolver = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(fileName);

        var fbxOptions = sampleFps is { } fbxFps ? new FbxImportOptions { SampleFps = fbxFps } : null;
        var bvhOptions = sampleFps is { } bvhFps ? new BvhImportOptions { SampleFps = bvhFps } : null;
        var gltfOptions = sampleFps is not null || externalBufferResolver is not null
            ? new GltfImportOptions
            {
                SampleFps = sampleFps ?? 30f,
                ExternalBufferResolver = externalBufferResolver,
            }
            : null;
        // RenderWare banks carry no clip names — takes are named from the file stem.
        var rwOptions = new RwAnmImportOptions
        {
            SampleFps = sampleFps ?? 30f,
            ClipNameBase = FileStem(fileName),
        };
        // EA ANT packages carry no clip names and bind channels to joints by index; the
        // companion joint table arrives the same way RenderWare's .dff does.
        var antOptions = new AntImportOptions
        {
            SampleFps = sampleFps ?? 30f,
            ClipNameBase = FileStem(fileName),
        };
        var ext = ExtensionOf(fileName);
        return ext switch
        {
            "hmotion" => Motion.MotionDocument.Parse(data).ToSourceScene(sampleFps),
            "fbx" => FbxImporter.Import(data, fbxOptions),
            "bvh" => BvhImporter.Import(data, bvhOptions),
            // .vrm = glTF 2.0 GLB container + VRM extension (the importer reads the authored
            // humanoid bone map into SourceScene.AuthoredMapping); unknown-extension VRM
            // bytes also land here via the GLB magic sniff below.
            "glb" or "gltf" or "vrm" => GltfImporter.Import(data, gltfOptions),
            // RenderWare animations (FSB2 .anm single clips / .an5 banks); the skeleton
            // comes from the companion model .dff (a missing skeleton throws the
            // importer's instructive error).
            "anm" or "an5" => RwAnmImporter.Import(data, skeletonData, rwOptions),
            // EA ANT animation packages (EA Canada titles; verified on Fight Night Champion).
            "cba" => AntImporter.Import(data, skeletonData, antOptions),
            _ => SniffFormat(data) switch
            {
                "fbx" => FbxImporter.Import(data, fbxOptions),
                "bvh" => BvhImporter.Import(data, bvhOptions),
                "gltf" => GltfImporter.Import(data, gltfOptions),
                "rwanim" => RwAnmImporter.Import(data, skeletonData, rwOptions),
                "ant" => AntImporter.Import(data, skeletonData, antOptions),
                _ => throw new FormatException(
                    $"Unrecognized source format for '{fileName}' (expected .fbx, .bvh, .glb, .gltf, .vrm, .anm, .an5 or .cba)."),
            },
        };
    }

    private static string? SniffFormat(byte[] data)
    {
        // Binary FBX magic: "Kaydara FBX Binary  \0".
        const string fbxMagic = "Kaydara FBX Binary";
        if (StartsWithAscii(data, fbxMagic))
            return "fbx";

        // EA ANT stream magic (.cba animation packages).
        if (StartsWithAscii(data, AntStream.StreamTag))
            return "ant";

        // GLB magic: 'glTF' (0x46546C67 little-endian).
        if (StartsWithAscii(data, "glTF"))
            return "gltf";

        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 4096));
        if (head.Contains("FBXHeaderExtension", StringComparison.Ordinal))
            return "fbx"; // ASCII FBX
        var trimmed = head.TrimStart();
        if (trimmed.StartsWith("HIERARCHY", StringComparison.OrdinalIgnoreCase))
            return "bvh";
        if (trimmed.StartsWith("{", StringComparison.Ordinal)
            && head.Contains("\"asset\"", StringComparison.Ordinal))
            return "gltf"; // plain-JSON glTF

        // RenderWare animation stream (.anm single clip / .an5 take container).
        if (RwAnmImporter.LooksLikeRenderwareAnim(data))
            return "rwanim";
        return null;
    }

    private static bool StartsWithAscii(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != (byte)prefix[i])
                return false;
        }
        return true;
    }

    /// <summary>
    /// THE mapping cascade, shared by conversion, UI file listings, and custom-target
    /// detection: explicit override → authored mapping (from the file itself, e.g. a VRM's
    /// humanoid bone map — authoritative ground truth at confidence 1.0) → user preset (via
    /// <paramref name="userPresetLookup"/>, keyed by <see cref="Mapping.SkeletonSignature"/>)
    /// → shipped preset detection → best-effort auto-map. The report's
    /// <see cref="MappingReportInfo.NeedsUserDecision"/> is true only on the auto path below
    /// the preset detection threshold — conversion still proceeds with that map; callers
    /// decide whether to ask/reject.
    /// </summary>
    /// <param name="skeleton">Source (or candidate target) skeleton.</param>
    /// <param name="mappingOverride">Explicit mapping (manual table / already-resolved user
    /// preset); wins outright when non-null — a deliberate user decision beats even the
    /// file's own bone map.</param>
    /// <param name="userPresetLookup">Editor-side user-preset hook: receives the skeleton's
    /// signature, returns the stored mapping or null. The facade itself can do no file IO.</param>
    /// <param name="authoredMapping">A mapping authored INSIDE the source file
    /// (<see cref="SourceScene.AuthoredMapping"/>, <see cref="MappingSource.Authored"/>);
    /// consulted before user presets because the file itself is authoritative.</param>
    public static (MappingResult Map, MappingReportInfo Report) ResolveMapping(
        SkeletonModel skeleton, MappingResult? mappingOverride = null,
        Func<string, MappingResult?>? userPresetLookup = null,
        MappingResult? authoredMapping = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);

        if (mappingOverride is not null)
            return (mappingOverride, BuildReport(mappingOverride, needsUserDecision: false, skeleton));

        if (authoredMapping is not null)
            return (authoredMapping, BuildReport(authoredMapping, needsUserDecision: false, skeleton));

        if (userPresetLookup is not null
            && userPresetLookup(Mapping.SkeletonSignature.Compute(skeleton)) is { } userPreset)
            return (userPreset, BuildReport(userPreset, needsUserDecision: false, skeleton));

        if (ProfileDetector.Detect(skeleton) is { } detected)
        {
            AutoMapper.CompleteSingleJointThumbs(skeleton, detected.Result);
            AutoMapper.CompleteFingersByTopology(skeleton, detected.Result);
            AutoMapper.PruneNonArticulatedFingerStubs(skeleton, detected.Result);
            VetoImpossibleHead(skeleton, detected.Result);
            return (detected.Result, BuildReport(detected.Result, needsUserDecision: false, skeleton));
        }

        var auto = AutoMapper.Map(skeleton);
        VetoImpossibleHead(skeleton, auto);
        var needsUserDecision = auto.Confidence < ProfileDetector.DetectionThreshold;
        return (auto, BuildReport(auto, needsUserDecision, skeleton));
    }

    /// <summary>
    /// Drops a detected/auto-mapped Head whose bone cannot anatomically drive a skull: its
    /// rest sits clearly BELOW the neck (or the topmost mapped spine joint). Real case: an
    /// Auto-Rig Pro export parks <c>head.x</c> at chest height ~90cm under <c>neck.x</c>
    /// with the skin bind compensating — the mesh is correct at rest, but every skull
    /// rotation replayed onto that bone sweeps the head geometry on the lever arm ("the
    /// head is stretched and it stretched more when I played the animation"). With the
    /// role unmapped the neck — whose pivot IS at the skull base on such rigs — carries
    /// head motion, and the bone holds its rest like any other unmapped helper.
    /// Neutral-rest corpus rigs measure the neck→head segment −8°…33° from character up
    /// (dot ≥ 0.55 even on the posed Defenses bind at 40.7°); the veto fires only past
    /// 101° (dot &lt; −0.2), an impossible carriage on any working rig. Explicit,
    /// authored and user-preset mappings are authoritative and never second-guessed.
    /// </summary>
    private static void VetoImpossibleHead(SkeletonModel skeleton, MappingResult map)
    {
        if (!map.RoleToBone.TryGetValue(BoneRole.Head, out var head)
            || !map.RoleToBone.TryGetValue(BoneRole.Hips, out var hips))
            return;

        var reference = map.RoleToBone.TryGetValue(BoneRole.Neck, out var neck)
            ? neck
            : SpineTop(map);
        if (reference is not { } refBone || refBone == hips)
            return;

        var rest = skeleton.RestWorld;
        // Axis-free "up": the body chain ascends hips → spine → neck by construction, so
        // hips→reference is the rig's own vertical regardless of world axis convention.
        var up = rest[refBone].Pos - rest[hips].Pos;
        var seg = rest[head].Pos - rest[refBone].Pos;
        if (up.LengthSquared() < 1e-6f || seg.Length() < 0.05f * up.Length())
            return; // degenerate spine / head stacked on the neck: nothing to judge

        if (Vector3.Dot(Vector3.Normalize(seg), Vector3.Normalize(up)) >= -0.2f)
            return;

        map.RoleToBone.Remove(BoneRole.Head);
        // The neck of a broken skull region rests with the head. On the real case the
        // neck bone skins the skull AND a torso column (its weights are where the head's
        // should be), so any solved neck bend shears half the body: the source spreads
        // its idle hunch through its spine while this target's spine stays put, and the
        // faithful neck world replay turned into a ~19° LOCAL kink rendered as a hump
        // grafted onto the character's back. Skull attitude rides the spine instead.
        var neckNote = "";
        if (map.RoleToBone.Remove(BoneRole.Neck))
            neckNote = $" Its neck '{skeleton[neck].Name}' rests too (on such exports it "
                + "carries the skull/torso skin the head joint should).";
        map.Notes.Add(
            $"Head '{skeleton[head].Name}' unmapped: its rest sits below "
            + $"'{skeleton[refBone].Name}' - not a skull joint the solver can drive "
            + "(rotations would sweep the head geometry on the offset lever arm)."
            + neckNote + " The spine carries the upper body; map manually to override.");
    }

    private static int? SpineTop(MappingResult map)
    {
        foreach (var role in new[]
        {
            BoneRole.Spine4, BoneRole.Spine3, BoneRole.Spine2, BoneRole.Spine1, BoneRole.Spine0,
        })
        {
            if (map.RoleToBone.TryGetValue(role, out var bone))
                return bone;
        }
        return null;
    }

    private static MappingReportInfo BuildReport(
        MappingResult map, bool needsUserDecision, SkeletonModel skeleton)
    {
        var report = new MappingReportInfo
        {
            ProfileName = map.ProfileName,
            Source = map.Source,
            Confidence = map.Confidence,
            NeedsUserDecision = needsUserDecision,
            MappedRoleCount = map.RoleToBone.Count,
            SkeletonSignature = Mapping.SkeletonSignature.Compute(skeleton),
        };
        report.Notes.AddRange(map.Notes);
        if (needsUserDecision)
            report.Notes.Add(
                $"No preset profile matched and auto-map confidence {map.Confidence:0.00} is below "
                + $"{ProfileDetector.DetectionThreshold:0.00}; proceeding with the best-effort auto map.");
        return report;
    }

    // ================================================================ naming

    private static string RequestedClipName(RetargetRequest request, SourceScene scene, int take)
    {
        var multipleTakes = scene.Clips.Count > 1;
        if (!string.IsNullOrWhiteSpace(request.ClipNameOverride))
            return multipleTakes ? $"{request.ClipNameOverride}_{take + 1}" : request.ClipNameOverride!;

        var takeName = scene.Clips[take].Name;
        if (!string.IsNullOrWhiteSpace(takeName) && takeName != "motion"
            && !takeName.Equals("mixamo.com", StringComparison.OrdinalIgnoreCase)
            && !IsExporterPlaceholderTake(takeName))
            return takeName;

        // BVH files (clip always named "motion"), Mixamo takes (always named
        // "mixamo.com"), Unreal exports (every stack named "Unreal Take") and
        // unnamed takes use the file stem.
        var stem = FileStem(request.SourceFileName);
        return multipleTakes ? $"{stem}_{take + 1}" : stem;
    }

    /// <summary>
    /// True for take names that identify the EXPORTER rather than the animation. Unreal's
    /// FBX exporter stamps <c>Unreal Take</c> into every AnimStack it writes (numbered —
    /// <c>Unreal Take 001</c> — when a sequence exports several), so a folder of UE clips
    /// arrives with one identical take name on every file. Treated as authored, that name
    /// beats the file stem in <see cref="RequestedClipName"/> and the whole batch collapses
    /// onto one clip name, which <see cref="UniqueClipName"/> then has to pull apart into
    /// <c>Unreal_Take</c>, <c>Unreal_Take_2</c>, <c>Unreal_Take_3</c>… — every file's real
    /// name lost, and the resulting sequence names carry no meaning in the vmdl. Falling
    /// through to the file stem restores the same per-file naming BVH and Mixamo sources
    /// already get. Matched as a PREFIX, and against the underscored spelling too, so the
    /// numbered variants and any already-sanitized re-import are covered.
    /// </summary>
    private static bool IsExporterPlaceholderTake(string takeName)
    {
        var trimmed = takeName.Trim();
        return trimmed.StartsWith("unreal take", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("unreal_take", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collision auto-suffixing across the batch: <c>name</c>, <c>name_2</c>, …
    /// (case-insensitive — the names also become DMX file names).</summary>
    private static string UniqueClipName(string name, HashSet<string> usedNames, bool autoSuffix)
    {
        if (usedNames.Add(name) || !autoSuffix)
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name}_{i}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Clip names become ModelDoc AnimFile node names and Source 2 sequence names, which
    /// must stay within <c>[A-Za-z0-9_]</c> — a take name like <c>mixamo.com</c> otherwise
    /// fails the vmdl compile with "Node 'mixamo.com' resolve failure". Runs of invalid
    /// characters collapse to a single underscore; case is preserved. Public so UIs that
    /// dry-run name-based detection (e.g. the locomotion-set scan over raw take names) see
    /// the SAME spelling conversion will produce — a take named <c>Walk N</c> becomes the
    /// clip <c>Walk_N</c>.
    /// </summary>
    public static string SanitizeClipName(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasUnderscore = false;
        foreach (var c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
            {
                builder.Append(c);
                lastWasUnderscore = c == '_';
            }
            else if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }
        var sanitized = builder.ToString().Trim('_');
        return sanitized.Length > 0 ? sanitized : "clip";
    }

    private static string FileStem(string fileName)
    {
        var start = Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\')) + 1;
        var dot = fileName.LastIndexOf('.');
        var end = dot > start ? dot : fileName.Length;
        var stem = fileName.Substring(start, end - start);
        return stem.Length > 0 ? stem : "clip";
    }

    private static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
            return "";
        var ext = fileName.Substring(dot + 1);
        return ext.IndexOfAny(new[] { '/', '\\' }) >= 0 ? "" : ext.ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(
                c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c
                : c is >= 'A' and <= 'Z' ? char.ToLowerInvariant(c)
                : '_');
        }
        var sanitized = builder.ToString().Trim('_');
        return sanitized.Length > 0 ? sanitized : "clip";
    }

    private static string JoinAssetPath(string folder, string file)
    {
        var f = folder.Replace('\\', '/').TrimEnd('/');
        return f.Length == 0 ? file : f + "/" + file;
    }

    // ================================================================ target context

    /// <summary>Everything derived once per batch from the target rig: character up axis,
    /// foot chains, hips/root indices, IK-bone presence.</summary>
    internal sealed class TargetContext
    {
        public TargetRig Rig { get; }

        /// <summary>Target character up direction (midShoulders − midHips on the rest pose,
        /// via <see cref="CharacterFrame"/>); null when the rig lacks the bones for it.</summary>
        public Vector3? Up { get; }

        /// <summary>Target character frame (for the arm-IK basis change); null when not computable.</summary>
        public CharacterFrame? Frame { get; }

        /// <summary>Left/right leg chains from the rig's UpperLeg/LowerLeg/Foot/Toe roles;
        /// null when either leg is incompletely mapped.</summary>
        public (FootChain Left, FootChain Right)? FootChains { get; }

        /// <summary>Why <see cref="Up"/> / <see cref="FootChains"/> are unavailable (report note text).</summary>
        public string UpOrChainProblem { get; } = "";

        /// <summary>Target bone carrying the Hips role.</summary>
        public int? HipsIndex { get; }

        /// <summary>Dedicated translating root: a parentless Animated bone that is an
        /// ancestor of (and distinct from) the hips. Null on the s&amp;box rig (pelvis is
        /// itself parentless; root_IK is IkBaked).</summary>
        public int? DedicatedRootIndex { get; }

        /// <summary>True when the hips bone's direct parent is the dedicated root.</summary>
        public bool HipsParentIsRoot { get; }

        public bool HasIkBakedBones { get; }

        /// <summary>ConstraintDriven bone indices (twist/helper) — excluded from DMX
        /// channels per design §3; null when the rig has none. Face bones
        /// (<see cref="SboxBoneClassifier.IsFaceBone"/>) are NOT in this set even though
        /// their class is ConstraintDriven: they keep rest-local channels, because no
        /// constraint re-drives them in a compiled sequence (channel-less face joints
        /// bake statically — eyes detach from the moving head in ModelDoc).</summary>
        public IReadOnlySet<int>? ConstraintDrivenBones { get; }

        private Dl.DlSolver? _dlSolver;

        /// <summary>The batch-shared deep-learning solver, parsing <paramref name="weights"/>
        /// once on first use (the batch runs requests sequentially — no locking needed).</summary>
        public IRetargetSolver GetDlSolver(byte[] weights)
            => _dlSolver ??= new Dl.DlSolver(weights);

        public TargetContext(TargetRig rig)
        {
            Rig = rig;
            HipsIndex = rig.BoneForRole(BoneRole.Hips);
            DedicatedRootIndex = FindDedicatedRoot(rig, HipsIndex);
            HipsParentIsRoot = HipsIndex is { } hips && DedicatedRootIndex is { } root
                && rig.Skeleton[hips].ParentIndex == root;
            foreach (var _ in rig.BonesOfClass(BoneClass.IkBaked))
            {
                HasIkBakedBones = true;
                break;
            }

            var constraintDriven = new HashSet<int>(rig.BonesOfClass(BoneClass.ConstraintDriven));
            // Face bones (eye_/ear_/face_) are ConstraintDriven by class but, unlike the
            // twist/helper bones, nothing re-drives them when a compiled sequence plays:
            // the model's AnimConstraintList never references them and the engine's eye
            // look-at / blinking only runs in game. They must KEEP their rest-local DMX
            // channels (the shipped fbx2dmx clips carry them too) or ModelDoc bakes the
            // channel-less joints statically and the eyes detach from the moving head
            // (see SboxBoneClassifier.IsFaceBone).
            constraintDriven.RemoveWhere(i => SboxBoneClassifier.IsFaceBone(rig.Skeleton[i].Name));
            ConstraintDrivenBones = constraintDriven.Count > 0 ? constraintDriven : null;

            try
            {
                Frame = CharacterFrame.Compute(rig.Skeleton, rig.ToMappingResult(), rig.Skeleton.RestWorld);
                Up = Frame.Up;
            }
            catch (ArgumentException e)
            {
                UpOrChainProblem = $"target character frame not computable ({e.Message})";
                return;
            }

            var left = LegChain(rig, BoneRole.UpperLegL, BoneRole.LowerLegL, BoneRole.FootL, BoneRole.ToeL);
            var right = LegChain(rig, BoneRole.UpperLegR, BoneRole.LowerLegR, BoneRole.FootR, BoneRole.ToeR);
            if (left is not null && right is not null)
                FootChains = (left, right);
            else
                UpOrChainProblem = "target rig does not map a complete UpperLeg/LowerLeg/Foot chain on both sides";
        }

        private static FootChain? LegChain(TargetRig rig, BoneRole upper, BoneRole lower, BoneRole foot, BoneRole toe)
            => rig.BoneForRole(upper) is { } hip
               && rig.BoneForRole(lower) is { } knee
               && rig.BoneForRole(foot) is { } ankle
                ? new FootChain { Hip = hip, Knee = knee, Ankle = ankle, Toe = rig.BoneForRole(toe) }
                : null;

        private static int? FindDedicatedRoot(TargetRig rig, int? hipsIndex)
        {
            if (hipsIndex is not { } hips)
                return null;
            // Walk up from the hips; the topmost ancestor qualifies when it is a parentless
            // Animated bone distinct from the hips itself.
            var top = hips;
            while (rig.Skeleton[top].ParentIndex >= 0)
                top = rig.Skeleton[top].ParentIndex;
            return top != hips && rig.ClassOf(top) == BoneClass.Animated ? top : null;
        }
    }

    // ================================================================ arm effector IK

    /// <summary>
    /// Optional wrist-reach cleanup: per frame, the source hand offset from its shoulder is
    /// limb-length-normalized, re-expressed in the target's character basis, and used as a
    /// two-bone IK goal for the target arm (<see cref="EffectorIk.ApplyGoals"/>).
    /// </summary>
    private static class ArmIkCleanup
    {
        /// <summary>Runs both arms; returns a problem note (and does nothing) when the
        /// prerequisites are missing, null on success.</summary>
        public static string? Apply(
            List<XForm[]> frames, SourceScene scene, MappingResult map,
            TargetRig rig, TargetContext context, int take)
        {
            if (context.Frame is not { } targetFrame)
                return context.UpOrChainProblem;

            CharacterFrame sourceFrame;
            try
            {
                sourceFrame = CharacterFrame.Compute(scene.Skeleton, map, scene.Skeleton.RestWorld);
            }
            catch (ArgumentException e)
            {
                return $"source character frame not computable ({e.Message})";
            }

            // Change of basis source space → target space (this library's convention:
            // a * b applies b first).
            var sourceToTarget = MathQ.Normalize(
                BasisRotation(targetFrame) * Quaternion.Conjugate(BasisRotation(sourceFrame)));

            var appliedAny = false;
            appliedAny |= ApplyArm(frames, scene, map, rig, sourceToTarget, take,
                BoneRole.UpperArmL, BoneRole.LowerArmL, BoneRole.HandL);
            appliedAny |= ApplyArm(frames, scene, map, rig, sourceToTarget, take,
                BoneRole.UpperArmR, BoneRole.LowerArmR, BoneRole.HandR);
            return appliedAny ? null : "no complete UpperArm/LowerArm/Hand chain mapped on source and target";
        }

        private static bool ApplyArm(
            List<XForm[]> frames, SourceScene scene, MappingResult map, TargetRig rig,
            Quaternion sourceToTarget, int take, BoneRole upperRole, BoneRole lowerRole, BoneRole handRole)
        {
            if (!map.RoleToBone.TryGetValue(upperRole, out var srcUpper)
                || !map.RoleToBone.TryGetValue(lowerRole, out var srcLower)
                || !map.RoleToBone.TryGetValue(handRole, out var srcHand))
                return false;
            if (rig.BoneForRole(upperRole) is not { } tgtUpper
                || rig.BoneForRole(lowerRole) is not { } tgtLower
                || rig.BoneForRole(handRole) is not { } tgtHand)
                return false;

            var src = scene.Skeleton;
            var tgt = rig.Skeleton;
            var srcLen = Vector3.Distance(src.RestWorld[srcUpper].Pos, src.RestWorld[srcLower].Pos)
                       + Vector3.Distance(src.RestWorld[srcLower].Pos, src.RestWorld[srcHand].Pos);
            var tgtLen = Vector3.Distance(tgt.RestWorld[tgtUpper].Pos, tgt.RestWorld[tgtLower].Pos)
                       + Vector3.Distance(tgt.RestWorld[tgtLower].Pos, tgt.RestWorld[tgtHand].Pos);
            if (srcLen < 1e-4f || tgtLen < 1e-4f)
                return false;
            var scale = tgtLen / srcLen;

            var sourceFrames = scene.Clips[take].Frames;
            var count = Math.Min(frames.Count, sourceFrames.Count);
            var goals = new Vector3[frames.Count];
            for (var f = 0; f < frames.Count; f++)
            {
                var srcWorld = new Pose(sourceFrames[Math.Min(f, count - 1)]).ToWorld(src);
                var tgtWorld = new Pose(frames[f]).ToWorld(tgt);
                var reach = srcWorld[srcHand].Pos - srcWorld[srcUpper].Pos;
                goals[f] = tgtWorld[tgtUpper].Pos + Vector3.Transform(reach, sourceToTarget) * scale;
            }

            EffectorIk.ApplyGoals(
                frames, tgt,
                new LimbChain { Upper = tgtUpper, Lower = tgtLower, End = tgtHand },
                goals, RestBendAxis(tgt, tgtUpper, tgtLower, tgtHand));
            return true;
        }

        /// <summary>Hinge-axis fallback from the rest bend plane (elbow), like the foot pass.</summary>
        internal static Vector3 RestBendAxis(SkeletonModel skeleton, int upper, int lower, int end)
        {
            var a = skeleton.RestWorld[upper].Pos;
            var b = skeleton.RestWorld[lower].Pos;
            var c = skeleton.RestWorld[end].Pos;
            var axis = Vector3.Cross(c - a, b - a);
            if (axis.LengthSquared() > 1e-6f)
                return Vector3.Normalize(axis);
            var limb = c - a;
            axis = Vector3.Cross(limb, Vector3.UnitY);
            if (axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(limb, Vector3.UnitZ);
            return axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitX;
        }

        /// <summary>Rotation taking canonical character axes (X=lateral, Y=up, Z=forward)
        /// to the rig's world axes.</summary>
        private static Quaternion BasisRotation(CharacterFrame frame)
        {
            var m = new Matrix4x4(
                frame.Lateral.X, frame.Lateral.Y, frame.Lateral.Z, 0f,
                frame.Up.X, frame.Up.Y, frame.Up.Z, 0f,
                frame.Forward.X, frame.Forward.Y, frame.Forward.Z, 0f,
                0f, 0f, 0f, 1f);
            return MathQ.Normalize(Quaternion.CreateFromRotationMatrix(m));
        }
    }
}
