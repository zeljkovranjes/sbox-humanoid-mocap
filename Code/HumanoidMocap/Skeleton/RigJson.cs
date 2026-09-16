#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Skeleton;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Loader for the rig ground-truth JSON produced by <c>research/extract_rig.py</c>
/// (headless Blender FBX import with <c>automatic_bone_orientation=False</c>).
/// </summary>
/// <remarks>
/// <para><b>Schema:</b> <c>armatures[0].bones[]</c> with <c>name</c>, <c>parent</c> (null for
/// roots — the s&amp;box rigs have two: <c>root_IK</c> and <c>pelvis</c>), <c>local_pos</c>,
/// <c>local_rot_xyzw</c>, <c>world_pos</c>, <c>world_rot_xyzw</c>, <c>head_local</c>,
/// <c>tail_local</c>. Quaternions are XYZW. "World"/"local-space" values are Blender
/// armature-space (<c>bone.matrix_local</c>), i.e. the FBX scene space of the rig.</para>
/// <para><b>Units:</b> Blender's FBX import puts a 0.01 scale on the armature object
/// (recorded as <c>object_scale</c>), so armature-space data x 0.01 is meters. This loader
/// converts every position to the project-wide centimeter convention by applying
/// <c>object_scale x 100</c> (meters → centimeters after the object scale) — a net factor of
/// ~1.0 for these files, confirming the raw armature-space values are source centimeters
/// (e.g. pelvis at y ≈ 93 cm).</para>
/// <para>This type touches no files itself; callers pass JSON text or a stream, keeping the
/// engine-agnostic core free of file IO.</para>
/// </remarks>
public static class RigJson
{
    /// <summary>
    /// Parses rig JSON text into a skeleton (rest pose, centimeters) plus per-bone rest
    /// geometry: bone head and tail positions in rig world (armature) space, centimeters.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the JSON does not match the expected schema.</exception>
    public static (Skeleton Skeleton, Dictionary<string, (Vector3 HeadWorld, Vector3 TailWorld)> Geometry)
        Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        return Load(doc);
    }

    /// <summary>Stream variant of <see cref="Load(string)"/>; the stream is read but not closed.</summary>
    public static (Skeleton Skeleton, Dictionary<string, (Vector3 HeadWorld, Vector3 TailWorld)> Geometry)
        Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var doc = JsonDocument.Parse(stream);
        return Load(doc);
    }

    private static (Skeleton, Dictionary<string, (Vector3, Vector3)>) Load(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("armatures", out var armatures) || armatures.GetArrayLength() == 0)
            throw new ArgumentException("Rig JSON has no 'armatures' entry.");

        var armature = armatures[0];
        var positionScale = ReadPositionScale(armature);

        var bones = armature.GetProperty("bones");
        var definitions = new List<BoneDefinition>(bones.GetArrayLength());
        var geometry = new Dictionary<string, (Vector3, Vector3)>(bones.GetArrayLength(), StringComparer.Ordinal);

        foreach (var bone in bones.EnumerateArray())
        {
            var definition = ReadBoneDefinition(bone, positionScale);
            definitions.Add(definition);

            geometry[definition.Name] = (
                ReadVector3(bone.GetProperty("head_local")) * positionScale,
                ReadVector3(bone.GetProperty("tail_local")) * positionScale);
        }

        return (Skeleton.Create(definitions), geometry);
    }

    /// <summary>
    /// Shared bone-geometry reader for the JSON bone shape used by both the rig ground-truth
    /// files and <see cref="Target.TargetRig"/> definitions: <c>name</c>, <c>parent</c>
    /// (null for roots), <c>local_pos</c> ([x,y,z]), <c>local_rot_xyzw</c> ([x,y,z,w],
    /// normalized on read). Positions are multiplied by <paramref name="positionScale"/>.
    /// </summary>
    internal static BoneDefinition ReadBoneDefinition(JsonElement bone, float positionScale = 1f)
    {
        var name = bone.GetProperty("name").GetString()
            ?? throw new ArgumentException("Bone with null name in rig JSON.");
        var parentProp = bone.GetProperty("parent");
        var parent = parentProp.ValueKind == JsonValueKind.Null ? null : parentProp.GetString();

        var localPos = ReadVector3(bone.GetProperty("local_pos")) * positionScale;
        var localRot = MathQ.Normalize(ReadQuaternion(bone.GetProperty("local_rot_xyzw")));
        return new BoneDefinition(name, parent, new XForm(localPos, localRot));
    }

    /// <summary>
    /// Position conversion factor to centimeters: the armature object scale (0.01, taking
    /// armature space to Blender meters) times 100 (meters to centimeters).
    /// </summary>
    private static float ReadPositionScale(JsonElement armature)
    {
        var objectScale = armature.GetProperty("object_scale");
        var sx = objectScale[0].GetDouble();
        var sy = objectScale[1].GetDouble();
        var sz = objectScale[2].GetDouble();
        if (Math.Abs(sy - sx) > 1e-6 * Math.Abs(sx) || Math.Abs(sz - sx) > 1e-6 * Math.Abs(sx))
            throw new ArgumentException($"Non-uniform armature object scale ({sx}, {sy}, {sz}) is unsupported.");
        return (float)(sx * 100.0);
    }

    private static Vector3 ReadVector3(JsonElement e)
        => new((float)e[0].GetDouble(), (float)e[1].GetDouble(), (float)e[2].GetDouble());

    private static Quaternion ReadQuaternion(JsonElement e)
        => new((float)e[0].GetDouble(), (float)e[1].GetDouble(), (float)e[2].GetDouble(), (float)e[3].GetDouble());
}
