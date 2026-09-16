#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HumanoidMocap.Formats.Ant;

/// <summary>
/// Reads the companion JOINT TABLE that an ANT animation package needs but does not carry.
/// </summary>
/// <remarks>
/// <para>
/// An ANT clip binds its channels to joints by INDEX, never by name, and the joint table
/// lives in a separate rig package (<c>package_rig.cba</c>) — only 3 of Fight Night
/// Champion's 489 packages contain one at all. So, exactly like the RenderWare importer
/// needing the model <c>.dff</c> beside a <c>.anm</c>, this importer takes the joint table
/// as companion bytes (<see cref="RetargetRequest.SkeletonData"/>).
/// </para>
/// <para>
/// The accepted shape is the simple joint dump every ANT rig extractor emits — an ordered
/// array where the array position IS the joint index the clips reference. The extractor's
/// metadata wrapper <c>{ "name": "skeleton_boxer", "joints": [...] }</c> is accepted too:
/// </para>
/// <code>
/// [ { "name": "Reference",    "parent": -1 },
///   { "name": "AITrajectory", "parent":  0 },
///   { "name": "Hips",         "parent":  1 }, ... ]
/// </code>
/// <para>
/// Note what is NOT here: a bind pose. ANT's <c>SkeletonAsset</c> genuinely stores only
/// <c>JointName</c> and <c>ParentIndex</c> per joint, so no extractor can supply rest
/// transforms from the animation packages — see <see cref="AntImporter"/> for how the rest
/// pose is recovered instead.
/// </para>
/// </remarks>
public static class AntSkeletonJson
{
    /// <summary>One joint of the companion table: name plus parent index (-1 = root).</summary>
    public readonly record struct Joint(string Name, int Parent);

    /// <summary>True when the bytes look like a joint-table JSON document.</summary>
    public static bool Looks(byte[] data)
    {
        if (data is null)
            return false;
        for (var i = 0; i < data.Length && i < 64; i++)
        {
            if (data[i] is (byte)'[' or (byte)'{')
                return true;
            if (data[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF))
                return false;
        }
        return false;
    }

    /// <exception cref="FormatException">Thrown when the document is not a readable joint
    /// table, an entry lacks a name, or a parent index is out of range.</exception>
    public static List<Joint> Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException e)
        {
            throw new FormatException($"Companion joint table is not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            var jointArray = doc.RootElement;
            if (jointArray.ValueKind == JsonValueKind.Object
                && jointArray.TryGetProperty("joints", out var wrappedJoints))
                jointArray = wrappedJoints;

            if (jointArray.ValueKind != JsonValueKind.Array)
                throw new FormatException(
                    "Companion joint table must be a JSON ARRAY of {\"name\", \"parent\"} "
                    + "objects in joint-index order, or an object containing that array as "
                    + "its \"joints\" property.");

            var joints = new List<Joint>();
            foreach (var element in jointArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException(
                        $"Joint {joints.Count} has no string \"name\" property.");
                }

                var parent = -1;
                if (element.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Number)
                    p.TryGetInt32(out parent);

                var text = name.GetString();
                if (string.IsNullOrEmpty(text))
                    throw new FormatException($"Joint {joints.Count} has an empty name.");
                joints.Add(new Joint(text, parent));
            }

            if (joints.Count == 0)
                throw new FormatException("Companion joint table is empty.");

            for (var i = 0; i < joints.Count; i++)
            {
                var parent = joints[i].Parent;
                if (parent >= joints.Count || parent == i)
                    throw new FormatException(
                        $"Joint {i} ('{joints[i].Name}') has an invalid parent index {parent}.");
            }
            return joints;
        }
    }
}
