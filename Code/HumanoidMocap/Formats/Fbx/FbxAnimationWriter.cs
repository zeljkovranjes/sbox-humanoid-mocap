#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Formats.Fbx;
using Vector3 = System.Numerics.Vector3;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

/// <summary>Portable FBX armature and sampled animation. No mesh, materials or skin deformers.</summary>
public static class FbxAnimationWriter
{
    public const int MaximumTransformSamples = 4_000_000;
    public static byte[] Write(SkeletonModel skeleton, Clip clip, int upAxis = 1, double unitScaleCm = 1)
        => Write(skeleton, clip.Name, clip.Frames,
            Enumerable.Range(0, clip.FrameCount).Select(i => i / (double)clip.Fps).ToArray(), clip.Fps, upAxis, unitScaleCm);

    /// <summary>Writes local transforms at their actual timestamps. FBX time starts at zero;
    /// the original start time is retained as capture metadata. Translations use the specified units.</summary>
    public static byte[] Write(SkeletonModel skeleton, string name, IReadOnlyList<XForm[]> frames,
        IReadOnlyList<double> times, double fps, int upAxis = 1, double unitScaleCm = 1,
        string motionSpace = "Unspecified")
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(times);
        if (frames.Count is < 1 or > 108000 || skeleton.Count is < 1 or > 1024 || (long)frames.Count * skeleton.Count > MaximumTransformSamples)
            throw new ArgumentException("Animation exceeds the export budget. Select a shorter range.");
        if (times.Count != frames.Count || !double.IsFinite(fps) || fps <= 0 || fps > 1000
            || upAxis is not (1 or 2) || !double.IsFinite(unitScaleCm) || unitScaleCm <= 0)
            throw new ArgumentException("Invalid animation timing, axes or units.");
        var ticks = new long[times.Count];
        for (int f = 0; f < frames.Count; f++)
        {
            double relative = times[f] - times[0];
            if (!double.IsFinite(times[f]) || !double.IsFinite(relative) || relative < 0 || relative > 86400
                || (f > 0 && times[f] <= times[f - 1])) throw new ArgumentException("Invalid sample timestamps.");
            ticks[f] = (long)Math.Round(relative * FbxAnimCurve.TicksPerSecond);
            if (f > 0 && ticks[f] <= ticks[f - 1]) throw new ArgumentException("Samples are too close for FBX time precision.");
            if (frames[f].Length != skeleton.Count) throw new ArgumentException("Frame does not match the armature.");
            foreach (var pose in frames[f]) CheckTransform(pose);
        }
        foreach (var bone in skeleton.Bones)
        {
            if (bone.Name.Contains('\0')) throw new ArgumentException("Bone names cannot contain null characters.");
            CheckTransform(bone.RestLocal);
        }
        name = string.IsNullOrWhiteSpace(name) ? "capture" : name;
        if (name.Contains('\0')) throw new ArgumentException("Animation names cannot contain null characters.");
        var root = N("");
        root.Children.Add(Node("FBXHeaderExtension", N("FBXHeaderVersion", 1003), N("FBXVersion", 7400), N("Creator", "Humanoid Mocap")));
        root.Children.Add(Node("GlobalSettings", N("Version", 1000), Node("Properties70",
            P("UpAxis", "int", upAxis), P("UpAxisSign", "int", 1), P("FrontAxis", "int", upAxis == 1 ? 2 : 1),
            P("FrontAxisSign", "int", upAxis == 1 ? 1 : -1), P("CoordAxis", "int", 0), P("CoordAxisSign", "int", 1),
            P("OriginalUpAxis", "int", upAxis), P("OriginalUpAxisSign", "int", 1),
            P("UnitScaleFactor", "double", unitScaleCm), P("OriginalUnitScaleFactor", "double", unitScaleCm),
            P("TimeMode", "enum", 14), P("CustomFrameRate", "double", fps),
            P("TimeSpanStart", "KTime", 0L), P("TimeSpanStop", "KTime", ticks[^1]))));
        long next = 100;
        long Id() => next++;
        var objects = N("Objects"); var links = N("Connections");
        void Connect(long from, long to, string? property = null)
            => links.Children.Add(property is null ? N("C", "OO", from, to) : N("C", "OP", from, to, property));
        var ids = skeleton.Bones.Select(_ => Id()).ToArray();
        var bind = N("Pose", Id(), "BindPose\0\u0001Pose", "BindPose");
        bind.Children.AddRange(new[] { N("Type", "BindPose"), N("Version", 100), N("NbPoseNodes", skeleton.Count) });
        for (int b = 0; b < skeleton.Count; b++)
        {
            var bone = skeleton[b]; var rest = bone.RestLocal; var rotation = Euler(rest.Rot, null);
            var model = N("Model", ids[b], bone.Name + "\0\u0001Model", "LimbNode");
            model.Children.AddRange(new[] { N("Version", 232), Node("Properties70",
                V("Lcl Translation", rest.Pos), V("Lcl Rotation", rotation), V("Lcl Scaling", Vector3.One),
                P("RotationActive", "bool", true), P("RotationOrder", "enum", 0), P("InheritType", "enum", 1)) });
            objects.Children.Add(model); Connect(ids[b], bone.ParentIndex < 0 ? 0 : ids[bone.ParentIndex]);
            long attributeId = Id();
            var attribute = N("NodeAttribute", attributeId, bone.Name + "\0\u0001NodeAttribute", "LimbNode");
            attribute.Children.Add(N("TypeFlags", "Skeleton")); objects.Children.Add(attribute); Connect(attributeId, ids[b]);
            var world = skeleton.RestWorld[b];
            var matrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(world.Rot)) * Matrix4x4.CreateTranslation(world.Pos);
            bind.Children.Add(Node("PoseNode", N("Node", ids[b]), N("Matrix", new double[] {
                matrix.M11,matrix.M12,matrix.M13,matrix.M14,matrix.M21,matrix.M22,matrix.M23,matrix.M24,
                matrix.M31,matrix.M32,matrix.M33,matrix.M34,matrix.M41,matrix.M42,matrix.M43,matrix.M44 })));
        }
        objects.Children.Add(bind);
        long stackId = Id(), layerId = Id();
        var stack = N("AnimationStack", stackId, name + "\0\u0001AnimStack", "");
        stack.Children.Add(Node("Properties70", P("LocalStart", "KTime", 0L), P("LocalStop", "KTime", ticks[^1]),
            P("ReferenceStart", "KTime", 0L), P("ReferenceStop", "KTime", ticks[^1]),
            P("HM_SourceStartSeconds", "double", times[0]), P("HM_MotionSpace", "KString", motionSpace)));
        objects.Children.Add(stack);
        var layer = N("AnimationLayer", layerId, "BaseLayer\0\u0001AnimLayer", "");
        layer.Children.Add(Node("Properties70", P("Weight", "Number", 100.0), P("BlendMode", "enum", 1)));
        objects.Children.Add(layer); Connect(layerId, stackId);
        for (int b = 0; b < skeleton.Count; b++)
        {
            var rotations = new Vector3[frames.Count]; Vector3? previous = null;
            for (int f = 0; f < frames.Count; f++) previous = rotations[f] = Euler(frames[f][b].Rot, previous);
            AddChannels(b, "T", "Lcl Translation", f => frames[f][b].Pos);
            AddChannels(b, "R", "Lcl Rotation", f => rotations[f]);
        }
        void AddChannels(int bone, string label, string property, Func<int, Vector3> sample)
        {
            long nodeId = Id(); var node = N("AnimationCurveNode", nodeId, label + "\0\u0001AnimCurveNode", "");
            var defaults = sample(0);
            node.Children.Add(Node("Properties70", P("d|X", "Number", (double)defaults.X), P("d|Y", "Number", (double)defaults.Y), P("d|Z", "Number", (double)defaults.Z)));
            objects.Children.Add(node); Connect(nodeId, layerId); Connect(nodeId, ids[bone], property);
            for (int axis = 0; axis < 3; axis++)
            {
                var values = new float[frames.Count];
                for (int f = 0; f < frames.Count; f++) { var v = sample(f); values[f] = axis == 0 ? v.X : axis == 1 ? v.Y : v.Z; }
                long curveId = Id(); var curve = N("AnimationCurve", curveId, "\0\u0001AnimCurve", "");
                curve.Children.AddRange(new[] { N("Default", (double)values[0]), N("KeyVer", 4008), N("KeyTime", ticks), N("KeyValueFloat", values),
                    N("KeyAttrFlags", new[] { 4 }), N("KeyAttrDataFloat", new float[4]), N("KeyAttrRefCount", new[] { frames.Count }) });
                objects.Children.Add(curve); Connect(curveId, nodeId, "d|" + "XYZ"[axis]);
            }
        }
        var definitions = Node("Definitions", N("Version", 100), N("Count", objects.Children.Count));
        foreach (var group in objects.Children.GroupBy(n => n.Name))
        { var type = N("ObjectType", group.Key); type.Children.Add(N("Count", group.Count())); definitions.Children.Add(type); }
        root.Children.Add(definitions); root.Children.Add(objects); root.Children.Add(links);
        var take = N("Take", name); take.Children.AddRange(new[] { N("FileName", ""), N("LocalTime", 0L, ticks[^1]), N("ReferenceTime", 0L, ticks[^1]) });
        root.Children.Add(Node("Takes", N("Current", name), take));
        return FbxBinaryWriter.Write(root);
    }

    static void CheckTransform(XForm transform)
    {
        var p = transform.Pos; var q = transform.Rot;
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)
            || !float.IsFinite(q.LengthSquared()) || Math.Abs(q.LengthSquared() - 1) > .01f)
            throw new ArgumentException("Animation contains an invalid bone transform.");
    }

    // Equivalent XYZ solutions are unwrapped against the previous sample. At a
    // gimbal singularity, retain the previous Z angle and solve the coupled X angle.
    internal static Vector3 Euler(Quaternion rotation, Vector3? previous)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        double cy = Math.Sqrt((double)m.M11 * m.M11 + (double)m.M12 * m.M12);
        double y = Math.Atan2(-m.M13, cy), x, z;
        if (cy > 1e-6) { x = Math.Atan2(m.M23, m.M33); z = Math.Atan2(m.M12, m.M11); }
        else { z = (previous?.Z ?? 0) * Math.PI / 180; x = Math.Atan2(-m.M32, m.M22) + (y > 0 ? z : -z); }
        var euler = new Vector3((float)x, (float)y, (float)z) * (180 / MathF.PI);
        if (previous is not { } p) return euler;
        Vector3 Near(Vector3 v) => new(Unwrap(v.X, p.X), Unwrap(v.Y, p.Y), Unwrap(v.Z, p.Z));
        var a = Near(euler); var b = Near(new(euler.X + 180, 180 - euler.Y, euler.Z + 180));
        return Vector3.DistanceSquared(a, p) <= Vector3.DistanceSquared(b, p) ? a : b;
    }
    static float Unwrap(float value, float previous) => value + 360 * MathF.Round((previous - value) / 360);
    static FbxNode N(string name, params object[] properties) { var node = new FbxNode(name); node.Properties.AddRange(properties); return node; }
    static FbxNode Node(string name, params FbxNode[] children) { var node = N(name); node.Children.AddRange(children); return node; }
    static FbxNode P(string name, string type, params object[] values) => N("P", new object[] { name, type, "", "A" }.Concat(values).ToArray());
    static FbxNode V(string name, Vector3 v) => P(name, name, (double)v.X, (double)v.Y, (double)v.Z);
}
