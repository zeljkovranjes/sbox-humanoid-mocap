#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Target;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// A humanoid target rig: skeleton plus per-bone <see cref="BoneClass"/> and (for animated
/// bones) canonical <see cref="BoneRole"/> annotations. The shipped s&amp;box default is
/// loaded from the committed <c>Assets/humanoid_mocap/target_rig_sbox.json</c> produced
/// by <see cref="TargetRigGenerator"/> (see <see cref="SboxDefault"/>); arbitrary user-picked
/// targets are built from any <see cref="SkeletonModel"/> via <see cref="FromSkeleton"/>.
/// This type does no file IO — callers pass JSON text.
/// </summary>
public sealed class TargetRig
{
    private readonly BoneClass[] _classes;
    private readonly BoneRole?[] _roles;
    private readonly Dictionary<BoneRole, int> _boneByRole;
    private readonly Vector3?[]? _tailWorld;

    private TargetRig(string name, SkeletonModel skeleton, BoneClass[] classes, BoneRole?[] roles,
        Dictionary<BoneRole, int> boneByRole, Vector3?[]? tailWorld = null,
        bool helpersAreConstraintDriven = true)
    {
        Name = name;
        Skeleton = skeleton;
        _classes = classes;
        _roles = roles;
        _boneByRole = boneByRole;
        _tailWorld = tailWorld;
        HelpersAreConstraintDriven = helpersAreConstraintDriven;
    }

    /// <summary>Rig name (e.g. <c>sbox_human_male</c>).</summary>
    public string Name { get; }

    /// <summary>
    /// True when the target model's own vmdl re-drives the
    /// <see cref="BoneClass.ConstraintDriven"/> helpers at runtime (the shipped s&amp;box
    /// rigs: their AnimConstraintList drives twist/helper bones, so clips exclude those
    /// channels). False for rigs built from plain skeletons
    /// (<see cref="FromSkeleton"/> — custom FBX targets): the generated vmdl carries NO
    /// constraints, so twist-named bones must keep baked channels or they freeze at rest
    /// (candy-wrapped wrists on an Auto-Rig Pro export).
    /// </summary>
    public bool HelpersAreConstraintDriven { get; }

    /// <summary>The target skeleton (rest pose in centimeters, parents before children).</summary>
    public SkeletonModel Skeleton { get; }

    /// <summary>The class of the bone at <paramref name="boneIndex"/>.</summary>
    public BoneClass ClassOf(int boneIndex) => _classes[boneIndex];

    /// <summary>The canonical role of the bone at <paramref name="boneIndex"/>, or null
    /// for role-less (non-animated) bones.</summary>
    public BoneRole? RoleOf(int boneIndex) => _roles[boneIndex];

    /// <summary>The bone index carrying <paramref name="role"/>, or null when the rig has no
    /// bone for it (e.g. <see cref="BoneRole.Spine3"/>, <see cref="BoneRole.ThumbMetaL"/>).</summary>
    public int? BoneForRole(BoneRole role) => _boneByRole.TryGetValue(role, out var index) ? index : null;

    /// <summary>
    /// Rest tail position of the bone in rig world space (cm), when the rig definition
    /// carries one (<c>tail_world</c> in the generated JSON — the s&amp;box default rig
    /// does). Null for rigs built from plain skeletons (<see cref="FromSkeleton"/>), which
    /// have no tail data. Used by the DL solver's end-joint synthesis.
    /// </summary>
    public Vector3? TailWorldOf(int boneIndex) => _tailWorld?[boneIndex];

    /// <summary>Indices of all bones of the given class, in skeleton order.</summary>
    public IEnumerable<int> BonesOfClass(BoneClass boneClass)
    {
        for (var i = 0; i < _classes.Length; i++)
        {
            if (_classes[i] == boneClass)
                yield return i;
        }
    }

    /// <summary>
    /// Parses a target-rig definition produced by <see cref="TargetRigGenerator"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the JSON does not match the expected
    /// schema or violates invariants (e.g. a role on a non-animated bone, duplicate roles).</exception>
    public static TargetRig Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.GetProperty("name").GetString()
            ?? throw new ArgumentException("Target rig JSON has no 'name'.");

        var bonesJson = root.GetProperty("bones");
        var count = bonesJson.GetArrayLength();
        var definitions = new List<BoneDefinition>(count);
        var classByName = new Dictionary<string, BoneClass>(count, StringComparer.Ordinal);
        var roleByName = new Dictionary<string, BoneRole>(count, StringComparer.Ordinal);
        var tailByName = new Dictionary<string, Vector3>(count, StringComparer.Ordinal);

        foreach (var bone in bonesJson.EnumerateArray())
        {
            // Shared bone-geometry shape (name/parent/local_pos/local_rot_xyzw) — one reader
            // for rig ground-truth JSON and target-rig JSON (RigJson owns it).
            var definition = RigJson.ReadBoneDefinition(bone);
            var boneName = definition.Name;
            definitions.Add(definition);

            var boneClass = Enum.Parse<BoneClass>(bone.GetProperty("class").GetString()!);
            classByName[boneName] = boneClass;

            if (bone.TryGetProperty("role", out var roleProp))
            {
                if (boneClass != BoneClass.Animated)
                    throw new ArgumentException(
                        $"Bone '{boneName}' is {boneClass} but carries a role — only Animated bones may have roles.");
                roleByName[boneName] = Enum.Parse<BoneRole>(roleProp.GetString()!);
            }

            if (bone.TryGetProperty("tail_world", out var tailProp)
                && tailProp.ValueKind == JsonValueKind.Array && tailProp.GetArrayLength() == 3)
            {
                tailByName[boneName] = new Vector3(
                    (float)tailProp[0].GetDouble(),
                    (float)tailProp[1].GetDouble(),
                    (float)tailProp[2].GetDouble());
            }
        }

        var skeleton = SkeletonModel.Create(definitions);

        var classes = new BoneClass[skeleton.Count];
        var roles = new BoneRole?[skeleton.Count];
        var boneByRole = new Dictionary<BoneRole, int>();
        var tails = tailByName.Count > 0 ? new Vector3?[skeleton.Count] : null;
        for (var i = 0; i < skeleton.Count; i++)
        {
            var boneName = skeleton[i].Name;
            classes[i] = classByName[boneName];
            if (roleByName.TryGetValue(boneName, out var role))
            {
                roles[i] = role;
                if (!boneByRole.TryAdd(role, i))
                    throw new ArgumentException($"Role {role} is assigned to more than one bone.");
            }
            if (tails is not null && tailByName.TryGetValue(boneName, out var tail))
                tails[i] = tail;
        }

        return new TargetRig(name, skeleton, classes, roles, boneByRole, tails);
    }

    /// <summary>
    /// Parses the shipped s&amp;box default target rig from the committed
    /// <c>Assets/humanoid_mocap/target_rig_sbox.json</c> text (callers do the file IO).
    /// Alias of <see cref="Load"/>, named per design §1 to distinguish the curated default
    /// target from <see cref="FromSkeleton"/> custom targets.
    /// </summary>
    public static TargetRig SboxDefault(string targetRigJson) => Load(targetRigJson);

    /// <summary>
    /// Builds a target rig from an arbitrary humanoid skeleton (design §1 "Custom targets"):
    /// roles come from <paramref name="map"/> (the same detection/mapping used for sources),
    /// bone classes from <paramref name="rules"/> name patterns (default
    /// <see cref="BoneClassRules"/>). Any bone that carries a mapped role is forced
    /// <see cref="BoneClass.Animated"/> — a role always wins over a twist-like name. That is
    /// what makes ActorCore rigs work: <c>CC_Base_NeckTwist01</c> matches the twist pattern
    /// but IS the neck bone (the actorcore_cc profile maps it to <see cref="BoneRole.Neck"/>),
    /// so it ends up Animated.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the mapping references a bone index
    /// outside the skeleton or assigns two roles to the same bone.</exception>
    public static TargetRig FromSkeleton(SkeletonModel skeleton, MappingResult map, BoneClassRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(map);
        rules ??= new BoneClassRules();

        var roles = new BoneRole?[skeleton.Count];
        var boneByRole = new Dictionary<BoneRole, int>(map.RoleToBone.Count);
        foreach (var (role, boneIndex) in map.RoleToBone)
        {
            if (boneIndex < 0 || boneIndex >= skeleton.Count)
                throw new ArgumentException(
                    $"Mapping assigns role {role} to bone index {boneIndex}, outside the skeleton (count {skeleton.Count}).",
                    nameof(map));
            if (roles[boneIndex] is { } existing)
                throw new ArgumentException(
                    $"Bone '{skeleton[boneIndex].Name}' carries both roles {existing} and {role}.", nameof(map));
            roles[boneIndex] = role;
            boneByRole.Add(role, boneIndex);
        }

        var classes = new BoneClass[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
            classes[i] = roles[i] is not null ? BoneClass.Animated : rules.Classify(skeleton[i].Name);

        // No curated vmdl backs this rig: whatever we generate for it carries no
        // AnimConstraintList, so its helper bones need real baked channels.
        return new TargetRig(map.ProfileName, skeleton, classes, roles, boneByRole,
            tailWorld: null, helpersAreConstraintDriven: false);
    }

}
