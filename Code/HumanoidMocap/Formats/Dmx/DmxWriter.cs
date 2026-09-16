#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HumanoidMocap.Skeleton;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Formats.Dmx;

/// <summary>Options for <see cref="DmxWriter.Write"/>.</summary>
public sealed class DmxWriteOptions
{
    /// <summary>Model/clip name written into the DmeModel element (e.g. the sequence name).</summary>
    public string Name { get; set; } = "";

    /// <summary>Free-form provenance note written as the DmeDCCMakefile source name
    /// (fbx2dmx writes the source .fbx path here).</summary>
    public string SourceNote { get; set; } = "";

    /// <summary>When true (default, matching fbx2dmx output) the file declares a Y-up axis
    /// system; when false it declares Z-up. Data is written as-is either way.</summary>
    public bool UpAxisY { get; set; } = true;

    /// <summary>
    /// Skeleton bone indices that get NO DmeChannel pair: the bones keep their DmeJoint and
    /// bind (rest) transform, but no animation channels are written for them — the engine then
    /// drives them itself (e.g. ConstraintDriven twist/helper bones, design §3). Null (default)
    /// writes channels for every bone.
    /// </summary>
    public IReadOnlySet<int>? ChannelExcludedBones { get; set; }
}

/// <summary>
/// Writes an animation DMX in <c>keyvalues2_noids</c> text encoding, replicating the exact
/// element/attribute shape of fbx2dmx output (authoritative reference:
/// <c>dev/m0/ref_idlepose.dmx</c>): a root DmElement holding an inline DmeModel (joint GUID
/// refs + bind base state), a top-level DmeAnimationList with one DmeChannelsClip carrying a
/// position and an orientation channel per bone, and top-level DmeTransform/DmeJoint elements
/// the channels and joint lists reference by GUID. Output is fully deterministic: GUIDs are
/// MD5-derived from the options name and an element path, and export tags use fixed
/// placeholder strings.
/// </summary>
public static class DmxWriter
{
    private const string Header = "<!-- dmx encoding keyvalues2_noids 4 format model 22 -->";

    /// <summary>
    /// Serializes <paramref name="clip"/> on <paramref name="skeleton"/> to DMX text.
    /// Frames must contain one local transform per bone in skeleton order.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the clip is empty or a frame's bone
    /// count does not match the skeleton.</exception>
    public static string Write(SkeletonModel skeleton, Clip clip, DmxWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(options);

        if (clip.FrameCount == 0)
            throw new ArgumentException("Clip has no frames.", nameof(clip));
        for (var f = 0; f < clip.FrameCount; f++)
        {
            if (clip.Frames[f].Length != skeleton.Count)
                throw new ArgumentException(
                    $"Frame {f} has {clip.Frames[f].Length} bone transforms, skeleton has {skeleton.Count}.",
                    nameof(clip));
        }

        var w = new Emitter();
        var animListGuid = GuidString(options.Name, "animationList");
        var jointGuids = new string[skeleton.Count];
        var transformGuids = new string[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
        {
            jointGuids[i] = GuidString(options.Name, "joint:" + skeleton[i].Name);
            transformGuids[i] = GuidString(options.Name, "transform:" + skeleton[i].Name);
        }

        w.Raw(Header);

        // ---- root DmElement -------------------------------------------------
        w.BeginTopLevel("DmElement");
        w.Attr("name", "string", "root");

        w.BeginInlineAttr("skeleton", "DmeModel");
        w.Attr("name", "string", options.Name);
        w.BeginInlineAttr("transform", "DmeTransform");
        w.Attr("position", "vector3", "0 0 0");
        w.Attr("orientation", "quaternion", "0 0 0 1");
        w.EndInlineAttr();
        w.Attr("shape", "element", "");
        w.Attr("visible", "bool", "1");

        w.BeginArray("children");
        var roots = new List<int>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (skeleton[i].ParentIndex < 0)
                roots.Add(i);
        }
        for (var r = 0; r < roots.Count; r++)
            w.ElementRef(jointGuids[roots[r]], last: r == roots.Count - 1);
        w.EndArray();

        w.BeginArray("jointList");
        for (var i = 0; i < skeleton.Count; i++)
            w.ElementRef(jointGuids[i], last: i == skeleton.Count - 1);
        w.EndArray();

        w.BeginArray("baseStates");
        w.BeginArrayElement("DmeTransformList");
        w.Attr("name", "string", "bind");
        w.BeginArray("transforms");
        for (var i = 0; i < skeleton.Count; i++)
        {
            w.BeginArrayElement("DmeTransform");
            w.Attr("name", "string", skeleton[i].Name);
            w.Attr("position", "vector3", Vec(skeleton[i].RestLocal));
            w.Attr("orientation", "quaternion", Quat(skeleton[i].RestLocal));
            w.EndArrayElement(last: i == skeleton.Count - 1);
        }
        w.EndArray();
        w.EndArrayElement(last: true);
        w.EndArray();

        w.Attr("upAxis", "string", options.UpAxisY ? "Y" : "Z");
        w.BeginInlineAttr("axisSystem", "DmeAxisSystem");
        w.Attr("upAxis", "int", options.UpAxisY ? "2" : "3");
        w.Attr("forwardParity", "int", "2");
        w.Attr("coordSys", "int", "0");
        w.EndInlineAttr();
        w.Attr("animationList", "element", animListGuid);
        w.EndInlineAttr(); // skeleton DmeModel

        w.BeginInlineAttr("makefile", "DmeDCCMakefile");
        w.Attr("name", "string", "makefile");
        w.BeginArray("sources");
        w.BeginArrayElement("DmeSource");
        w.Attr("name", "string", options.SourceNote);
        w.EndArrayElement(last: true);
        w.EndArray();
        w.EndInlineAttr();

        // Deterministic placeholders — never wall-clock/user data, so output is reproducible.
        w.BeginInlineAttr("exportTags", "DmeExportTags");
        w.Attr("name", "string", "exportTags");
        w.Attr("date", "string", "2026/01/01");
        w.Attr("time", "string", "12:00:00 am");
        w.Attr("user", "string", "retargeter");
        w.Attr("machine", "string", "retargeter");
        w.Attr("app", "string", "sbox-humanoid-mocap");
        w.Attr("appVersion", "string", "1.0");
        w.Attr("cmdLine", "string", "sbox-humanoid-mocap");
        w.Attr("pwd", "string", "");
        w.EndInlineAttr();

        w.Attr("animationList", "element", animListGuid);
        w.EndTopLevel();

        // ---- DmeAnimationList ----------------------------------------------
        w.BeginTopLevel("DmeAnimationList");
        w.Attr("id", "elementid", animListGuid);
        w.Attr("name", "string", "anim");
        w.BeginArray("animations");
        w.BeginArrayElement("DmeChannelsClip");
        w.Attr("name", "string", "anim");

        w.BeginInlineAttr("timeFrame", "DmeTimeFrame");
        w.Attr("start", "time", Time(0.0));
        w.Attr("duration", "time", Time((clip.FrameCount - 1) / (double)clip.Fps));
        w.Attr("offset", "time", Time(0.0));
        w.Attr("scale", "float", "1");
        w.EndInlineAttr();

        w.Attr("color", "color", "0 0 0 0");
        w.Attr("text", "string", "");
        w.Attr("mute", "bool", "0");
        w.BeginArray("trackGroups");
        w.EndArray();
        w.Attr("displayScale", "float", "1");

        var channelBones = new List<int>(skeleton.Count);
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (options.ChannelExcludedBones is null || !options.ChannelExcludedBones.Contains(i))
                channelBones.Add(i);
        }

        w.BeginArray("channels");
        for (var n = 0; n < channelBones.Count; n++)
        {
            var i = channelBones[n];
            WriteChannel(w, skeleton, clip, i, transformGuids[i], position: true, last: false);
            WriteChannel(w, skeleton, clip, i, transformGuids[i], position: false,
                last: n == channelBones.Count - 1);
        }
        w.EndArray();

        w.Attr("frameRate", "int",
            ((int)MathF.Round(clip.Fps)).ToString(CultureInfo.InvariantCulture));
        w.EndArrayElement(last: true);
        w.EndArray();
        w.EndTopLevel();

        // ---- top-level channel-target DmeTransforms (rest values) -----------
        for (var i = 0; i < skeleton.Count; i++)
        {
            w.BeginTopLevel("DmeTransform");
            w.Attr("id", "elementid", transformGuids[i]);
            w.Attr("name", "string", skeleton[i].Name);
            w.Attr("position", "vector3", Vec(skeleton[i].RestLocal));
            w.Attr("orientation", "quaternion", Quat(skeleton[i].RestLocal));
            w.EndTopLevel();
        }

        // ---- top-level DmeJoints --------------------------------------------
        for (var i = 0; i < skeleton.Count; i++)
        {
            w.BeginTopLevel("DmeJoint");
            w.Attr("id", "elementid", jointGuids[i]);
            w.Attr("name", "string", skeleton[i].Name);
            w.Attr("transform", "element", transformGuids[i]);
            w.Attr("shape", "element", "");
            w.Attr("visible", "bool", "1");
            w.BeginArray("children");
            var children = new List<int>();
            for (var c = 0; c < skeleton.Count; c++)
            {
                if (skeleton[c].ParentIndex == i)
                    children.Add(c);
            }
            for (var c = 0; c < children.Count; c++)
                w.ElementRef(jointGuids[children[c]], last: c == children.Count - 1);
            w.EndArray();
            w.EndTopLevel();
        }

        return w.ToString();
    }

    /// <summary>
    /// Deterministic element GUID: MD5 over <c>"&lt;name&gt;\n&lt;path&gt;"</c> (UTF-8)
    /// interpreted as <see cref="Guid"/> bytes. Exposed so tests can verify the scheme.
    /// </summary>
    public static Guid ElementGuid(string name, string path)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(name + "\n" + path)));

    private static string GuidString(string name, string path)
        => ElementGuid(name, path).ToString("D", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- channels

    private static void WriteChannel(Emitter w, SkeletonModel skeleton, Clip clip, int bone,
        string transformGuid, bool position, bool last)
    {
        var logClass = position ? "DmeVector3Log" : "DmeQuaternionLog";
        var layerClass = position ? "DmeVector3LogLayer" : "DmeQuaternionLogLayer";
        var logName = position ? "vector3 log" : "quaternion log";

        w.BeginArrayElement("DmeChannel");
        w.Attr("name", "string", skeleton[bone].Name + (position ? "_p" : "_o"));
        w.Attr("fromElement", "element", "");
        w.Attr("fromAttribute", "string", "");
        w.Attr("fromIndex", "int", "0");
        w.Attr("toElement", "element", transformGuid);
        w.Attr("toAttribute", "string", position ? "position" : "orientation");
        w.Attr("toIndex", "int", "0");
        w.Attr("mode", "int", "3");

        w.BeginInlineAttr("log", logClass);
        w.Attr("name", "string", logName);
        w.BeginArray("layers");
        w.BeginArrayElement(layerClass);
        w.Attr("name", "string", logName);

        w.BeginArray("times", "time_array");
        for (var f = 0; f < clip.FrameCount; f++)
            w.ArrayValue(Time(f / (double)clip.Fps), last: f == clip.FrameCount - 1);
        w.EndArray();

        w.BeginArray("curvetypes", "int_array");
        w.EndArray();

        w.BeginArray("values", position ? "vector3_array" : "quaternion_array");
        // Orientation values are hemisphere-aligned on the fly (q and -q are the same
        // rotation, but the engine interpolates between DMX samples numerically — see
        // QuaternionContinuity). The clip itself is never mutated.
        var prev = System.Numerics.Quaternion.Identity;
        for (var f = 0; f < clip.FrameCount; f++)
        {
            var x = clip.Frames[f][bone];
            string value;
            if (position)
            {
                value = Vec(x);
            }
            else
            {
                var q = x.Rot;
                if (f > 0 && System.Numerics.Quaternion.Dot(prev, q) < 0f)
                    q = System.Numerics.Quaternion.Negate(q);
                prev = q;
                value = Quat(q);
            }
            w.ArrayValue(value, last: f == clip.FrameCount - 1);
        }
        w.EndArray();

        w.EmptyBinaryAttr("compressed");
        w.EndArrayElement(last: true);
        w.EndArray(); // layers

        w.Attr("curveinfo", "element", "");
        w.Attr("usedefaultvalue", "bool", "0");
        w.Attr("defaultvalue", position ? "vector3" : "quaternion", position ? "0 0 0" : "0 0 0 1");
        w.BeginArray("bookmarksX", "time_array");
        w.EndArray();
        w.BeginArray("bookmarksY", "time_array");
        w.EndArray();
        w.BeginArray("bookmarksZ", "time_array");
        w.EndArray();
        w.EndInlineAttr(); // log

        w.EndArrayElement(last);
    }

    // ---------------------------------------------------------------- formatting

    /// <summary>fbx2dmx float style: up to 10 decimal places, trailing zeros stripped,
    /// invariant culture, negative zero normalized.</summary>
    private static string F(float value)
    {
        if (value == 0f)
            return "0";
        return ((double)value).ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static string Time(double seconds)
        => seconds.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Vec(in Maths.XForm x)
        => $"{F(x.Pos.X)} {F(x.Pos.Y)} {F(x.Pos.Z)}";

    private static string Quat(in Maths.XForm x) => Quat(x.Rot);

    private static string Quat(in System.Numerics.Quaternion q)
        => $"{F(q.X)} {F(q.Y)} {F(q.Z)} {F(q.W)}";

    // ---------------------------------------------------------------- emitter

    /// <summary>
    /// Low-level keyvalues2 text emitter reproducing fbx2dmx layout quirks: CRLF endings,
    /// tab indentation, a trailing space after array-typed attribute names, and an
    /// indentation-only line after every inline element attribute closes.
    /// </summary>
    private sealed class Emitter
    {
        private readonly StringBuilder _sb = new();
        private int _indent;

        public void Raw(string text)
        {
            _sb.Append(text).Append("\r\n");
        }

        private void Line(string text)
        {
            _sb.Append('\t', _indent).Append(text).Append("\r\n");
        }

        public void Attr(string name, string type, string value)
            => Line($"\"{name}\" \"{type}\" \"{value}\"");

        public void BeginTopLevel(string className)
        {
            Line($"\"{className}\"");
            Line("{");
            _indent++;
        }

        public void EndTopLevel()
        {
            _indent--;
            Line("}");
            _sb.Append("\r\n"); // blank separator after every top-level element (incl. the last)
        }

        public void BeginInlineAttr(string name, string className)
        {
            Line($"\"{name}\" \"{className}\"");
            Line("{");
            _indent++;
        }

        public void EndInlineAttr()
        {
            _indent--;
            Line("}");
            Line(""); // indentation-only line, as fbx2dmx emits
        }

        public void BeginArrayElement(string className)
        {
            Line($"\"{className}\"");
            Line("{");
            _indent++;
        }

        public void EndArrayElement(bool last)
        {
            _indent--;
            Line(last ? "}" : "},");
        }

        public void BeginArray(string name, string type = "element_array")
        {
            Line($"\"{name}\" \"{type}\" ");
            Line("[");
            _indent++;
        }

        public void EndArray()
        {
            _indent--;
            Line("]");
        }

        public void ElementRef(string guid, bool last)
            => Line($"\"element\" \"{guid}\"" + (last ? "" : ","));

        public void ArrayValue(string value, bool last)
            => Line($"\"{value}\"" + (last ? "" : ","));

        public void EmptyBinaryAttr(string name)
        {
            Line($"\"{name}\" \"binary\" ");
            Line("\"");
            Line("\"");
        }

        public override string ToString() => _sb.ToString();
    }
}
