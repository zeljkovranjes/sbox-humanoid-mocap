#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Formats.Dmx;
using HumanoidMocap.Formats.Fbx;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Formats.Gltf;

using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Converts the skinned meshes in a glTF/GLB model to Source 2 model-DMX. ModelDoc does
/// not accept glTF as a RenderMeshFile, while DMX preserves the same skeleton, vertices,
/// materials and four-weight skinning without an external converter.
/// </summary>
public static class GltfModelDmxWriter
{
    private const float MetersToCentimeters = 100f;

    /// <summary>Writes a Y-up, centimeter model-DMX for the already imported target rig.</summary>
    public static string Write(
        byte[] data, SkeletonModel skeleton, string name,
        Func<string, byte[]>? externalBufferResolver = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(name);

        var document = GltfDocument.Parse(data, externalBufferResolver);
        var parts = ReadMeshParts(document, skeleton);
        if (parts.Count == 0)
            throw new FormatException("glTF contains no supported mesh primitives.");
        return Emit(skeleton, name, parts);
    }

    private sealed class MeshPart
    {
        public required string Name;
        public required string Material;
        public required Vector3[] Positions;
        public required Vector3[] Normals;
        public required Vector2[] TexCoords;
        public required int[] Triangles;
        public required float[] Weights;
        public required int[] Joints;
    }

    private static List<MeshPart> ReadMeshParts(GltfDocument document, SkeletonModel skeleton)
    {
        var root = document.Root;
        if (!root.TryGetProperty("nodes", out var nodeArray)
            || !root.TryGetProperty("meshes", out var meshArray))
            return new List<MeshPart>();

        root.TryGetProperty("skins", out var skinArray);
        root.TryGetProperty("materials", out var materialArray);
        var worlds = NodeWorlds(document);
        var bonesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < skeleton.Count; i++)
            bonesByName[skeleton[i].Name] = i;

        var parts = new List<MeshPart>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var nodeIndex = 0; nodeIndex < nodeArray.GetArrayLength(); nodeIndex++)
        {
            var node = nodeArray[nodeIndex];
            if (!node.TryGetProperty("mesh", out var meshProperty))
                continue;
            var meshIndex = meshProperty.GetInt32();
            if (meshIndex < 0 || meshIndex >= meshArray.GetArrayLength())
                throw new FormatException($"glTF node {nodeIndex} references invalid mesh {meshIndex}.");
            var mesh = meshArray[meshIndex];
            if (!mesh.TryGetProperty("primitives", out var primitives))
                continue;

            var skinIndex = node.TryGetProperty("skin", out var skinProperty)
                ? skinProperty.GetInt32() : -1;
            var skinJoints = MapSkinJoints(
                document, skeleton, bonesByName, skinArray, skinIndex);
            var skinTransforms = SkinTransforms(document, skinArray, skinIndex, worlds, skinJoints, skeleton);
            var normalMatrix = NormalMatrix(worlds[nodeIndex]);
            var primitiveIndex = 0;
            foreach (var primitive in primitives.EnumerateArray())
            {
                if (!primitive.TryGetProperty("attributes", out var attributes)
                    || !attributes.TryGetProperty("POSITION", out var positionProperty))
                {
                    primitiveIndex++;
                    continue;
                }

                var positions = new Accessor(document, positionProperty.GetInt32(), 3);
                var vertexCount = positions.Count;
                if (vertexCount == 0)
                {
                    primitiveIndex++;
                    continue;
                }

                var transformedPositions = new Vector3[vertexCount];
                ReadSkinning(document, attributes, vertexCount, skinJoints,
                    out var weights, out var joints);
                var vertexTransforms = new Matrix4x4[vertexCount];
                for (var i = 0; i < vertexCount; i++)
                {
                    var transform = worlds[nodeIndex];
                    if (skinTransforms is not null)
                    {
                        transform = default;
                        for (var influence = 0; influence < 4; influence++)
                        {
                            var at = i * 4 + influence;
                            if (weights[at] > 0f)
                                transform += skinTransforms[joints[at]] * weights[at];
                        }
                    }
                    vertexTransforms[i] = transform;
                    var value = new Vector3(
                        positions.Float(i, 0), positions.Float(i, 1), positions.Float(i, 2));
                    transformedPositions[i] = Vector3.Transform(value, transform)
                        * MetersToCentimeters;
                }

                var rawIndices = ReadIndices(document, primitive, vertexCount);
                var mode = primitive.TryGetProperty("mode", out var modeProperty)
                    ? modeProperty.GetInt32() : 4;
                var triangles = Triangulate(rawIndices, mode);

                var normals = new Vector3[vertexCount];
                if (attributes.TryGetProperty("NORMAL", out var normalProperty))
                {
                    var source = new Accessor(document, normalProperty.GetInt32(), 3);
                    RequireCount(source, vertexCount, "NORMAL");
                    for (var i = 0; i < vertexCount; i++)
                    {
                        var value = new Vector3(
                            source.Float(i, 0), source.Float(i, 1), source.Float(i, 2));
                        var transform = skinTransforms is null ? normalMatrix : NormalMatrix(vertexTransforms[i]);
                        normals[i] = NormalizeOr(Vector3.TransformNormal(value, transform), Vector3.UnitY);
                    }
                }
                else
                {
                    GenerateNormals(transformedPositions, triangles, normals);
                }

                var texCoords = new Vector2[vertexCount];
                if (attributes.TryGetProperty("TEXCOORD_0", out var texCoordProperty))
                {
                    var source = new Accessor(document, texCoordProperty.GetInt32(), 2);
                    RequireCount(source, vertexCount, "TEXCOORD_0");
                    for (var i = 0; i < vertexCount; i++)
                        texCoords[i] = new Vector2(source.Float(i, 0), source.Float(i, 1));
                }

                var baseName = node.TryGetProperty("name", out var nodeName)
                    ? nodeName.GetString()
                    : mesh.TryGetProperty("name", out var meshName) ? meshName.GetString() : null;
                var partName = UniqueName(
                    Sanitize(baseName ?? $"mesh_{meshIndex}") + $"_{primitiveIndex}", usedNames);
                parts.Add(new MeshPart
                {
                    Name = partName,
                    Material = MaterialName(materialArray, primitive),
                    Positions = transformedPositions,
                    Normals = normals,
                    TexCoords = texCoords,
                    Triangles = triangles,
                    Weights = weights,
                    Joints = joints,
                });
                primitiveIndex++;
            }
        }
        return parts;
    }

    private static Matrix4x4[] NodeWorlds(GltfDocument document)
    {
        var result = new Matrix4x4[document.Nodes.Count];
        var state = new byte[document.Nodes.Count];

        Matrix4x4 Visit(int index)
        {
            if (state[index] == 2)
                return result[index];
            if (state[index] == 1)
                throw new FormatException("glTF node graph contains a cycle.");
            state[index] = 1;
            var node = document.Nodes[index];
            var local = Matrix4x4.CreateScale(node.Scale)
                * Matrix4x4.CreateFromQuaternion(node.Rotation)
                * Matrix4x4.CreateTranslation(node.Translation);
            result[index] = node.Parent < 0 ? local : local * Visit(node.Parent);
            state[index] = 2;
            return result[index];
        }

        for (var i = 0; i < result.Length; i++)
            Visit(i);
        return result;
    }

    private static Matrix4x4 NormalMatrix(Matrix4x4 world)
    {
        if (!Matrix4x4.Invert(world, out var inverse))
            return Matrix4x4.Identity;
        return Matrix4x4.Transpose(inverse);
    }

    // Bake the authored skin into the node rest pose before DMX generates new inverse
    // binds. glTF skinned vertices use inverseBind * jointWorld, NOT meshNodeWorld.
    // Keeping the full matrices here also bakes inherited scale into the rigid DMX rig.
    private static Dictionary<int, Matrix4x4>? SkinTransforms(
        GltfDocument document, JsonElement skins, int skinIndex,
        Matrix4x4[] worlds, int[] mappedJoints, SkeletonModel skeleton)
    {
        if (mappedJoints.Length == 0)
            return null;
        var skin = skins[skinIndex];
        var nodes = skin.GetProperty("joints");
        var inverseBinds = skin.TryGetProperty("inverseBindMatrices", out var property)
            ? new Accessor(document, property.GetInt32(), 16) : null;
        if (inverseBinds is not null)
            RequireCount(inverseBinds, mappedJoints.Length, "inverseBindMatrices");
        var result = new Dictionary<int, Matrix4x4>();
        for (var i = 0; i < mappedJoints.Length; i++)
        {
            var inverse = Matrix4x4.Identity;
            if (inverseBinds is not null)
                inverse = ReadMatrix(inverseBinds, i);
            // The target can use the authored skin bind instead of the posed scene TRS.
            // Rebind the mesh to that exact skeleton; retain scale omitted by XForm.
            Matrix4x4.Decompose(worlds[nodes[i].GetInt32()], out var scale, out _, out _);
            var rest = skeleton.RestWorld[mappedJoints[i]];
            var jointWorld = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rest.Rot)
                * Matrix4x4.CreateTranslation(rest.Pos / MetersToCentimeters);
            result[mappedJoints[i]] = inverse * jointWorld;
        }
        return result;
    }

    internal static SkeletonModel WithSkinBindPose(GltfDocument document, SkeletonModel skeleton)
    {
        if (!document.Root.TryGetProperty("skins", out var skins))
            return skeleton;
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < skeleton.Count; i++)
            byName[Sanitize(skeleton[i].Name)] = i;
        var binds = new Dictionary<int, XForm>();
        var nodeWorlds = NodeWorlds(document);
        for (var s = 0; s < skins.GetArrayLength(); s++)
        {
            if (!skins[s].TryGetProperty("inverseBindMatrices", out var property))
                continue;
            var joints = MapSkinJoints(document, skeleton, byName, skins, s);
            var accessor = new Accessor(document, property.GetInt32(), 16);
            RequireCount(accessor, joints.Length, "inverseBindMatrices");
            for (var j = 0; j < joints.Length; j++)
            {
                if (!Matrix4x4.Invert(ReadMatrix(accessor, j), out var matrix))
                    throw new FormatException("glTF skin has a singular inverse bind matrix.");
                // Some exporters fold a bind-shape scale into these matrices. Those
                // are valid for skinning but cannot replace the scene's rigid rest.
                // Zero-offset scaffold joints do not describe the character's scale.
                if (skeleton[joints[j]].RestLocal.Pos.LengthSquared() > 1e-6f)
                {
                    Matrix4x4.Decompose(matrix, out var bindScale, out _, out _);
                    var node = skins[s].GetProperty("joints")[j].GetInt32();
                    Matrix4x4.Decompose(nodeWorlds[node], out var sceneScale, out _, out _);
                    if (Vector3.Distance(bindScale, sceneScale) > .001f * sceneScale.Length())
                        return skeleton;
                }
                var bind = FbxTransform.ToRigid(matrix);
                bind.Pos *= MetersToCentimeters;
                if (binds.TryGetValue(joints[j], out var previous)
                    && (Vector3.Distance(previous.Pos, bind.Pos) > .01f
                        || MathQ.AngleBetween(previous.Rot, bind.Rot) > .001f))
                    return skeleton; // Different per-mesh bind spaces cannot define one rig rest.
                binds[joints[j]] = bind;
            }
        }
        if (binds.Count == 0)
            return skeleton;
        // Bind matrices may use an origin below the displayed scene. Preserve the
        // scene's floor placement (glTF is Y-up), rather than burying the new rest.
        var sceneFloor = float.PositiveInfinity;
        var bindFloor = float.PositiveInfinity;
        foreach (var pair in binds)
        {
            sceneFloor = MathF.Min(sceneFloor, skeleton.RestWorld[pair.Key].Pos.Y);
            bindFloor = MathF.Min(bindFloor, pair.Value.Pos.Y);
        }
        var placement = new Vector3(0f, sceneFloor - bindFloor, 0f);
        var world = new XForm[skeleton.Count];
        var definitions = new List<BoneDefinition>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            var bone = skeleton[i];
            var parent = bone.ParentIndex;
            world[i] = binds.TryGetValue(i, out var bind) ? bind
                : parent < 0 ? bone.RestLocal : XForm.Compose(world[parent], bone.RestLocal);
            if (binds.ContainsKey(i))
                world[i].Pos += placement;
            var local = parent < 0 ? world[i] : XForm.ToLocal(world[parent], world[i]);
            definitions.Add(new BoneDefinition(bone.Name, parent < 0 ? null : skeleton[parent].Name, local));
        }
        return SkeletonModel.Create(definitions);
    }

    private static Matrix4x4 ReadMatrix(Accessor source, int i) => new(
        source.Float(i, 0), source.Float(i, 1), source.Float(i, 2), source.Float(i, 3),
        source.Float(i, 4), source.Float(i, 5), source.Float(i, 6), source.Float(i, 7),
        source.Float(i, 8), source.Float(i, 9), source.Float(i, 10), source.Float(i, 11),
        source.Float(i, 12), source.Float(i, 13), source.Float(i, 14), source.Float(i, 15));

    private static int[] MapSkinJoints(
        GltfDocument document, SkeletonModel skeleton, Dictionary<string, int> bonesByName,
        JsonElement skinArray, int skinIndex)
    {
        if (skinIndex < 0)
            return Array.Empty<int>();
        if (skinArray.ValueKind != JsonValueKind.Array || skinIndex >= skinArray.GetArrayLength())
            throw new FormatException($"glTF mesh references invalid skin {skinIndex}.");
        var skin = skinArray[skinIndex];
        if (!skin.TryGetProperty("joints", out var joints))
            return Array.Empty<int>();

        var result = new int[joints.GetArrayLength()];
        for (var i = 0; i < result.Length; i++)
        {
            var nodeIndex = joints[i].GetInt32();
            if (nodeIndex < 0 || nodeIndex >= document.Nodes.Count)
                throw new FormatException($"glTF skin references invalid joint node {nodeIndex}.");
            var raw = document.Nodes[nodeIndex].Name ?? $"node_{nodeIndex}";
            var safe = Sanitize(raw);
            if (!bonesByName.TryGetValue(safe, out var bone)
                && !bonesByName.TryGetValue(Sanitize(raw + "#" + nodeIndex), out bone))
            {
                throw new FormatException(
                    $"glTF skin joint '{raw}' is absent from the imported target skeleton.");
            }
            if (bone < 0 || bone >= skeleton.Count)
                throw new FormatException($"glTF skin joint '{raw}' mapped outside the target skeleton.");
            result[i] = bone;
        }
        return result;
    }

    private static void ReadSkinning(
        GltfDocument document, JsonElement attributes, int vertexCount, int[] skinJoints,
        out float[] weights, out int[] joints)
    {
        weights = new float[checked(vertexCount * 4)];
        joints = new int[checked(vertexCount * 4)];
        if (skinJoints.Length == 0
            || !attributes.TryGetProperty("JOINTS_0", out var jointProperty)
            || !attributes.TryGetProperty("WEIGHTS_0", out var weightProperty))
        {
            for (var i = 0; i < vertexCount; i++)
            {
                weights[i * 4] = 1f;
                joints[i * 4] = skinJoints.Length > 0 ? skinJoints[0] : 0;
            }
            return;
        }

        var jointSource = new Accessor(document, jointProperty.GetInt32(), 4);
        var weightSource = new Accessor(document, weightProperty.GetInt32(), 4);
        RequireCount(jointSource, vertexCount, "JOINTS_0");
        RequireCount(weightSource, vertexCount, "WEIGHTS_0");
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var total = 0f;
            for (var influence = 0; influence < 4; influence++)
            {
                var skinJoint = jointSource.Unsigned(vertex, influence);
                if (skinJoint < 0 || skinJoint >= skinJoints.Length)
                    throw new FormatException($"glTF JOINTS_0 references invalid skin joint {skinJoint}.");
                var at = vertex * 4 + influence;
                joints[at] = skinJoints[skinJoint];
                var weight = weightSource.Float(vertex, influence);
                weights[at] = float.IsFinite(weight) && weight > 0f ? weight : 0f;
                total += weights[at];
            }
            if (total <= 1e-8f)
            {
                weights[vertex * 4] = 1f;
                joints[vertex * 4] = skinJoints[0];
                continue;
            }
            for (var influence = 0; influence < 4; influence++)
                weights[vertex * 4 + influence] /= total;
        }
    }

    private static int[] ReadIndices(
        GltfDocument document, JsonElement primitive, int vertexCount)
    {
        if (!primitive.TryGetProperty("indices", out var indexProperty))
        {
            var sequential = new int[vertexCount];
            for (var i = 0; i < sequential.Length; i++)
                sequential[i] = i;
            return sequential;
        }

        var source = new Accessor(document, indexProperty.GetInt32(), 1);
        var result = new int[source.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = source.Unsigned(i, 0);
            if (result[i] < 0 || result[i] >= vertexCount)
                throw new FormatException($"glTF index {result[i]} exceeds vertex count {vertexCount}.");
        }
        return result;
    }

    private static int[] Triangulate(int[] indices, int mode)
    {
        var triangles = new List<int>();
        if (mode == 4) // TRIANGLES
        {
            if (indices.Length % 3 != 0)
                throw new FormatException("glTF triangle index count is not divisible by three.");
            triangles.AddRange(indices);
        }
        else if (mode == 5) // TRIANGLE_STRIP
        {
            for (var i = 2; i < indices.Length; i++)
            {
                var a = indices[i - 2];
                var b = indices[i - 1];
                var c = indices[i];
                if ((i & 1) != 0)
                    (a, b) = (b, a);
                if (a != b && b != c && a != c)
                {
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                }
            }
        }
        else if (mode == 6) // TRIANGLE_FAN
        {
            for (var i = 2; i < indices.Length; i++)
            {
                if (indices[0] == indices[i - 1] || indices[i - 1] == indices[i]
                    || indices[0] == indices[i])
                    continue;
                triangles.Add(indices[0]);
                triangles.Add(indices[i - 1]);
                triangles.Add(indices[i]);
            }
        }
        else
        {
            throw new FormatException($"glTF primitive mode {mode} is not a triangle mesh.");
        }
        return triangles.ToArray();
    }

    private static void GenerateNormals(Vector3[] positions, int[] triangles, Vector3[] normals)
    {
        for (var i = 0; i + 2 < triangles.Length; i += 3)
        {
            var a = triangles[i];
            var b = triangles[i + 1];
            var c = triangles[i + 2];
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }
        for (var i = 0; i < normals.Length; i++)
            normals[i] = NormalizeOr(normals[i], Vector3.UnitY);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;

    private static void RequireCount(Accessor accessor, int expected, string semantic)
    {
        if (accessor.Count != expected)
            throw new FormatException(
                $"glTF {semantic} has {accessor.Count} entries; expected {expected}.");
    }

    private static string MaterialName(JsonElement materials, JsonElement primitive)
    {
        if (!primitive.TryGetProperty("material", out var materialProperty))
            return "default";
        var index = materialProperty.GetInt32();
        if (materials.ValueKind != JsonValueKind.Array || index < 0 || index >= materials.GetArrayLength())
            throw new FormatException($"glTF primitive references invalid material {index}.");
        var material = materials[index];
        return material.TryGetProperty("name", out var name) && !string.IsNullOrEmpty(name.GetString())
            ? name.GetString()!
            : $"material_{index}";
    }

    private static string Sanitize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value)
            result.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return result.Length > 0 ? result.ToString() : "unnamed";
    }

    private static string UniqueName(string value, HashSet<string> used)
    {
        var candidate = value;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = value + "_" + suffix++;
        return candidate;
    }

    private sealed class Accessor
    {
        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _stride;
        private readonly int _componentSize;
        private readonly int _componentType;
        private readonly bool _normalized;

        public int Count { get; }
        public int Components { get; }

        public Accessor(GltfDocument document, int index, int expectedComponents)
        {
            var root = document.Root;
            if (!root.TryGetProperty("accessors", out var accessors)
                || index < 0 || index >= accessors.GetArrayLength())
                throw new FormatException($"glTF accessor {index} does not exist.");
            var accessor = accessors[index];
            if (accessor.TryGetProperty("sparse", out _))
                throw new FormatException("Sparse glTF mesh accessors are not supported.");

            Components = accessor.GetProperty("type").GetString() switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT4" => 16,
                var type => throw new FormatException($"Unsupported glTF accessor type '{type}'."),
            };
            if (Components != expectedComponents)
                throw new FormatException(
                    $"glTF accessor {index} has {Components} components; expected {expectedComponents}.");

            Count = accessor.GetProperty("count").GetInt32();
            if (Count < 0)
                throw new FormatException($"glTF accessor {index} has a negative count.");
            _componentType = accessor.GetProperty("componentType").GetInt32();
            _componentSize = _componentType switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                5125 or 5126 => 4,
                _ => throw new FormatException(
                    $"Unsupported glTF accessor component type {_componentType}."),
            };
            _normalized = accessor.TryGetProperty("normalized", out var normalized)
                && normalized.GetBoolean();

            if (!accessor.TryGetProperty("bufferView", out var viewProperty))
            {
                _buffer = Array.Empty<byte>();
                _start = 0;
                _stride = checked(Components * _componentSize);
                return;
            }

            var views = root.GetProperty("bufferViews");
            var viewIndex = viewProperty.GetInt32();
            if (viewIndex < 0 || viewIndex >= views.GetArrayLength())
                throw new FormatException($"glTF bufferView {viewIndex} does not exist.");
            var view = views[viewIndex];
            var bufferIndex = view.GetProperty("buffer").GetInt32();
            if (bufferIndex < 0 || bufferIndex >= document.Buffers.Count)
                throw new FormatException($"glTF buffer {bufferIndex} does not exist.");
            _buffer = document.Buffers[bufferIndex];
            var viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
            var accessorOffset = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
            _start = checked(viewOffset + accessorOffset);
            var elementSize = checked(Components * _componentSize);
            _stride = view.TryGetProperty("byteStride", out var stride)
                ? stride.GetInt32() : elementSize;
            if (_stride < elementSize)
                throw new FormatException("glTF accessor stride is smaller than its element.");
            var end = Count == 0 ? _start : (long)_start + (long)(Count - 1) * _stride + elementSize;
            if (_start < 0 || end > _buffer.Length)
                throw new FormatException($"glTF accessor {index} reads beyond its buffer.");
        }

        public float Float(int element, int component)
        {
            if (_buffer.Length == 0)
                return 0f;
            var offset = Offset(element, component);
            return _componentType switch
            {
                5120 => _normalized
                    ? MathF.Max(unchecked((sbyte)_buffer[offset]) / 127f, -1f)
                    : unchecked((sbyte)_buffer[offset]),
                5121 => _normalized ? _buffer[offset] / 255f : _buffer[offset],
                5122 => _normalized
                    ? MathF.Max(BitConverter.ToInt16(_buffer, offset) / 32767f, -1f)
                    : BitConverter.ToInt16(_buffer, offset),
                5123 => _normalized
                    ? BitConverter.ToUInt16(_buffer, offset) / 65535f
                    : BitConverter.ToUInt16(_buffer, offset),
                5125 => BitConverter.ToUInt32(_buffer, offset),
                _ => BitConverter.ToSingle(_buffer, offset),
            };
        }

        public int Unsigned(int element, int component)
        {
            if (_buffer.Length == 0)
                return 0;
            var offset = Offset(element, component);
            return _componentType switch
            {
                5121 => _buffer[offset],
                5123 => BitConverter.ToUInt16(_buffer, offset),
                5125 => checked((int)BitConverter.ToUInt32(_buffer, offset)),
                _ => throw new FormatException(
                    $"glTF indices require an unsigned integer accessor, got {_componentType}."),
            };
        }

        private int Offset(int element, int component)
        {
            if (element < 0 || element >= Count || component < 0 || component >= Components)
                throw new FormatException("glTF accessor index is out of range.");
            return checked(_start + element * _stride + component * _componentSize);
        }
    }

    private static string Emit(SkeletonModel skeleton, string name, IReadOnlyList<MeshPart> parts)
    {
        var writer = new Kv2Writer();
        var modelId = Id(name, "model");
        var jointIds = new string[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
            jointIds[i] = Id(name, "joint:" + skeleton[i].Name);
        var dagIds = new string[parts.Count];
        var meshIds = new string[parts.Count];
        var vertexIds = new string[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            dagIds[i] = Id(name, $"dag:{i}:{parts[i].Name}");
            meshIds[i] = Id(name, $"mesh:{i}:{parts[i].Name}");
            vertexIds[i] = Id(name, $"vertices:{i}:{parts[i].Name}");
        }

        writer.Raw("<!-- dmx encoding keyvalues2_noids 4 format model 22 -->");
        writer.BeginTop("DmElement");
        writer.Attr("name", "string", "root");
        writer.Attr("model", "element", modelId);
        writer.Attr("skeleton", "element", modelId);
        writer.EndTop();

        writer.BeginTop("DmeModel");
        writer.Attr("id", "elementid", modelId);
        writer.Attr("name", "string", name);
        WriteTransform(writer, "transform", Vector3.Zero, Quaternion.Identity);
        writer.Attr("visible", "bool", "1");
        var children = new List<string>();
        for (var i = 0; i < skeleton.Count; i++)
            if (skeleton[i].ParentIndex < 0)
                children.Add(jointIds[i]);
        children.AddRange(dagIds);
        WriteRefs(writer, "children", children);
        WriteRefs(writer, "jointList", jointIds);
        writer.Attr("upAxis", "string", "Y");
        writer.BeginInline("axisSystem", "DmeAxisSystem");
        writer.Attr("upAxis", "int", "2");
        writer.Attr("forwardParity", "int", "2");
        writer.Attr("coordSys", "int", "0");
        writer.EndInline();
        writer.EndTop();

        for (var i = 0; i < skeleton.Count; i++)
        {
            var bone = skeleton[i];
            writer.BeginTop("DmeJoint");
            writer.Attr("id", "elementid", jointIds[i]);
            writer.Attr("name", "string", bone.Name);
            WriteTransform(writer, "transform", bone.RestLocal.Pos, bone.RestLocal.Rot);
            writer.Attr("visible", "bool", "1");
            var boneChildren = new List<string>();
            for (var child = 0; child < skeleton.Count; child++)
                if (skeleton[child].ParentIndex == i)
                    boneChildren.Add(jointIds[child]);
            if (boneChildren.Count > 0)
                WriteRefs(writer, "children", boneChildren);
            writer.EndTop();
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            writer.BeginTop("DmeDag");
            writer.Attr("id", "elementid", dagIds[i]);
            writer.Attr("name", "string", part.Name);
            WriteTransform(writer, "transform", Vector3.Zero, Quaternion.Identity);
            writer.Attr("shape", "element", meshIds[i]);
            writer.Attr("visible", "bool", "1");
            writer.EndTop();

            writer.BeginTop("DmeMesh");
            writer.Attr("id", "elementid", meshIds[i]);
            writer.Attr("name", "string", part.Name);
            writer.Attr("visible", "bool", "1");
            writer.Attr("currentState", "element", vertexIds[i]);
            WriteRefs(writer, "baseStates", new[] { vertexIds[i] });
            writer.BeginArray("faceSets");
            writer.BeginArrayElement("DmeFaceSet");
            writer.Attr("name", "string", part.Material);
            writer.BeginArray("faces", "int_array");
            for (var index = 0; index < part.Triangles.Length; index++)
            {
                writer.Value(part.Triangles[index].ToString(CultureInfo.InvariantCulture), false);
                if (index % 3 == 2)
                    writer.Value("-1", index == part.Triangles.Length - 1);
            }
            writer.EndArray();
            writer.BeginInline("material", "DmeMaterial");
            writer.Attr("name", "string", part.Material);
            writer.Attr("mtlName", "string", part.Material);
            writer.EndInline();
            writer.EndArrayElement(true);
            writer.EndArray();
            writer.EndTop();

            writer.BeginTop("DmeVertexData");
            writer.Attr("id", "elementid", vertexIds[i]);
            writer.Attr("name", "string", "bind");
            writer.BeginArray("vertexFormat", "string_array");
            var formats = new[]
                { "position$0", "normal$0", "texcoord$0", "blendweights$0", "blendindices$0" };
            for (var format = 0; format < formats.Length; format++)
                writer.Value(formats[format], format == formats.Length - 1);
            writer.EndArray();
            writer.Attr("jointCount", "int", "4");
            writer.Attr("flipVCoordinates", "bool", "0");
            WriteVectors(writer, "position$0", "vector3_array", part.Positions,
                value => Vec(value));
            WriteIdentityIndices(writer, "position$0Indices", part.Positions.Length);
            WriteVectors(writer, "normal$0", "vector3_array", part.Normals,
                value => Vec(value));
            WriteIdentityIndices(writer, "normal$0Indices", part.Normals.Length);
            WriteVectors(writer, "texcoord$0", "vector2_array", part.TexCoords,
                value => $"{F(value.X)} {F(value.Y)}");
            WriteIdentityIndices(writer, "texcoord$0Indices", part.TexCoords.Length);
            WriteScalars(writer, "blendweights$0", "float_array", part.Weights,
                value => F(value));
            WriteScalars(writer, "blendindices$0", "int_array", part.Joints,
                value => value.ToString(CultureInfo.InvariantCulture));
            writer.EndTop();
        }
        return writer.ToString();
    }

    private static void WriteTransform(
        Kv2Writer writer, string name, Vector3 position, Quaternion orientation)
    {
        writer.BeginInline(name, "DmeTransform");
        writer.Attr("name", "string", name);
        writer.Attr("position", "vector3", Vec(position));
        writer.Attr("orientation", "quaternion",
            $"{F(orientation.X)} {F(orientation.Y)} {F(orientation.Z)} {F(orientation.W)}");
        writer.Attr("scale", "float", "1");
        writer.EndInline();
    }

    private static void WriteRefs(Kv2Writer writer, string name, IReadOnlyList<string> ids)
    {
        writer.BeginArray(name);
        for (var i = 0; i < ids.Count; i++)
            writer.ElementRef(ids[i], i == ids.Count - 1);
        writer.EndArray();
    }

    private static void WriteVectors<T>(
        Kv2Writer writer, string name, string type, T[] values, Func<T, string> format)
    {
        writer.BeginArray(name, type);
        for (var i = 0; i < values.Length; i++)
            writer.Value(format(values[i]), i == values.Length - 1);
        writer.EndArray();
    }

    private static void WriteScalars<T>(
        Kv2Writer writer, string name, string type, T[] values, Func<T, string> format)
        => WriteVectors(writer, name, type, values, format);

    private static void WriteIdentityIndices(Kv2Writer writer, string name, int count)
    {
        writer.BeginArray(name, "int_array");
        for (var i = 0; i < count; i++)
            writer.Value(i.ToString(CultureInfo.InvariantCulture), i == count - 1);
        writer.EndArray();
    }

    private static string Id(string name, string path)
        => DmxWriter.ElementGuid(name, "gltf-model:" + path)
            .ToString("D", CultureInfo.InvariantCulture);

    private static string F(float value)
        => value == 0f ? "0" : ((double)value).ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Vec(Vector3 value) => $"{F(value.X)} {F(value.Y)} {F(value.Z)}";

    private sealed class Kv2Writer
    {
        private readonly StringBuilder _text = new();
        private int _indent;

        public void Raw(string value) => _text.Append(value).Append("\r\n");

        private void Line(string value)
            => _text.Append('\t', _indent).Append(value).Append("\r\n");

        public void Attr(string name, string type, string value)
            => Line($"\"{Escape(name)}\" \"{type}\" \"{Escape(value)}\"");

        public void BeginTop(string type)
        {
            Line($"\"{type}\"");
            Line("{");
            _indent++;
        }

        public void EndTop()
        {
            _indent--;
            Line("}");
            _text.Append("\r\n");
        }

        public void BeginInline(string name, string type)
        {
            Line($"\"{Escape(name)}\" \"{type}\"");
            Line("{");
            _indent++;
        }

        public void EndInline()
        {
            _indent--;
            Line("}");
        }

        public void BeginArray(string name, string type = "element_array")
        {
            Line($"\"{Escape(name)}\" \"{type}\"");
            Line("[");
            _indent++;
        }

        public void EndArray()
        {
            _indent--;
            Line("]");
        }

        public void BeginArrayElement(string type)
        {
            Line($"\"{type}\"");
            Line("{");
            _indent++;
        }

        public void EndArrayElement(bool last)
        {
            _indent--;
            Line(last ? "}" : "},");
        }

        public void ElementRef(string id, bool last)
            => Line($"\"element\" \"{id}\"" + (last ? "" : ","));

        public void Value(string value, bool last)
            => Line($"\"{Escape(value)}\"" + (last ? "" : ","));

        private static string Escape(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", " ").Replace("\n", " ");

        public override string ToString() => _text.ToString();
    }
}
