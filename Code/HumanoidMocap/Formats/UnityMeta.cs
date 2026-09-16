#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Formats;

/// <summary>
/// One clip definition from a Unity <c>&lt;file&gt;.fbx.meta</c> sidecar
/// (<c>ModelImporter → clipAnimations</c>): Unity animation packs ship FBX files whose
/// "animations" are sub-ranges of ONE source timeline, and these definitions carry the
/// per-clip name + frame range. Frame values are expressed in the source file's NATIVE
/// frame rate (<see cref="Clip.NativeFps"/>), not in the import resample rate.
/// </summary>
public sealed class ExternalClipDef
{
    /// <summary>Clip name shown in Unity (becomes the output clip name; sanitized like take names).</summary>
    public string Name { get; set; } = "";

    /// <summary>Source take the range refers to (e.g. <c>root|Animation</c>); empty = the file's only/first take.</summary>
    public string TakeName { get; set; } = "";

    /// <summary>First frame of the range, in source-native frames (may be fractional).</summary>
    public float FirstFrame { get; set; }

    /// <summary>Last frame of the range, in source-native frames (may be fractional).
    /// <see cref="float.PositiveInfinity"/> when the meta did not record one = "to the end of the take".</summary>
    public float LastFrame { get; set; } = float.PositiveInfinity;

    /// <summary>Unity's <c>loopTime</c> flag (the "Loop Time" checkbox).</summary>
    public bool Loop { get; set; }
}

/// <summary>
/// Minimal hand-rolled parser for the Unity <c>.meta</c> YAML subset this library needs:
/// the <c>clipAnimations:</c> list under <c>ModelImporter → animations</c>. Unity writes
/// 2-space-indented YAML where each list item starts with <c>"- key: value"</c> at the list
/// key's own indentation and continues with <c>"key: value"</c> lines two spaces deeper
/// (see a real sidecar: <c>- serializedVersion: 16</c> / <c>name: Stance</c> /
/// <c>takeName: root|Animation</c> / <c>firstFrame: 0</c> / <c>lastFrame: 43</c> /
/// <c>loopTime: 1</c> plus many irrelevant fields). Everything but the five fields above is
/// ignored; missing fields keep their defaults. Never throws: malformed input yields an
/// empty list — sidecar parsing is an optional enhancement, not a failure mode.
/// </summary>
public static class UnityMeta
{
    /// <summary>
    /// Extracts the <c>clipAnimations</c> definitions from Unity sidecar text. Returns an
    /// empty list when the text has none (or is not a Unity meta file at all); never throws.
    /// </summary>
    public static List<ExternalClipDef> ParseClipAnimations(string? metaText)
    {
        var result = new List<ExternalClipDef>();
        if (string.IsNullOrEmpty(metaText))
            return result;
        try
        {
            ParseInto(metaText, result);
        }
        catch (Exception)
        {
            // Tolerant by contract: a sidecar we cannot read is the same as no sidecar.
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// Slices a resampled clip per one definition. The definition's frame range is in the
    /// take's NATIVE frame rate (<see cref="Clip.NativeFps"/>); the importer resampled the
    /// take onto the <see cref="Clip.Fps"/> grid, so range endpoints map as
    /// <c>round(frame / nativeFps · sampleFps)</c>, clamped into the clip. A range that
    /// collapses (first ≥ last after clamping) yields a 1-frame clip. The result reuses the
    /// source frame arrays (the pipeline never mutates source frames) and carries
    /// <paramref name="name"/> (default: the definition's name) and the definition's loop flag.
    /// </summary>
    public static Clip Slice(Clip source, ExternalClipDef def, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(def);
        var clipName = name ?? (string.IsNullOrWhiteSpace(def.Name) ? source.Name : def.Name);

        if (source.FrameCount == 0)
            return new Clip(clipName, source.Fps, def.Loop, new List<XForm[]>(), source.NativeFps);

        // Samples per native frame; NativeFps is validated positive at Clip construction.
        double scale = source.Fps / (double)source.NativeFps;
        int last = source.FrameCount - 1;

        double firstFrame = float.IsNaN(def.FirstFrame) ? 0.0 : def.FirstFrame;
        double lastFrame = float.IsNaN(def.LastFrame) ? float.PositiveInfinity : def.LastFrame;

        int start = (int)Math.Round(Math.Clamp(firstFrame * scale, 0.0, last));
        int end = (int)Math.Round(Math.Clamp(lastFrame * scale, start, last));

        var frames = source.Frames.GetRange(start, end - start + 1);
        return new Clip(clipName, source.Fps, def.Loop, frames, source.NativeFps);
    }

    // ====================================================================== line parser

    private static void ParseInto(string metaText, List<ExternalClipDef> result)
    {
        var lines = metaText.Split('\n');

        // ---- locate the clipAnimations key and record its indentation ------------------
        int i = 0;
        int keyIndent = -1;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart(' ');
            if (trimmed == "clipAnimations:")
            {
                keyIndent = line.Length - trimmed.Length;
                i++;
                break;
            }
            if (trimmed.StartsWith("clipAnimations:", StringComparison.Ordinal))
                return; // inline value, e.g. "clipAnimations: []" — no definitions
        }
        if (keyIndent < 0)
            return;

        // ---- walk the indentation-scoped list ------------------------------------------
        // Items start with "- " at the key's own indentation (Unity style); their fields
        // continue exactly two spaces deeper. Deeper content (nested lists/maps such as
        // events/curves entries) is ignored, and the first line at or above the key's
        // indentation that is not a list item ends the list (e.g. "isReadable: 0").
        ExternalClipDef? current = null;
        int fieldIndent = keyIndent + 2;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var content = line.Trim();
            if (content.Length == 0)
                continue;
            int indent = line.Length - line.TrimStart(' ').Length;

            if (content.StartsWith("- ", StringComparison.Ordinal))
            {
                if (indent < keyIndent)
                    break; // dedented out of the list
                if (indent > keyIndent)
                    continue; // nested list entry inside an item (events/curves) — ignore
                current = new ExternalClipDef();
                result.Add(current);
                ApplyField(current, content.Substring(2));
            }
            else
            {
                if (indent <= keyIndent)
                    break; // sibling key — end of the list
                if (current is not null && indent == fieldIndent)
                    ApplyField(current, content);
                // deeper lines belong to nested structures we do not care about
            }
        }

        // Items that carried none of the recognized fields are noise, not clips.
        result.RemoveAll(d => d.Name.Length == 0 && float.IsPositiveInfinity(d.LastFrame)
            && d.FirstFrame == 0f && d.TakeName.Length == 0);
    }

    /// <summary>Applies one <c>key: value</c> line to a definition; unknown keys are ignored.</summary>
    private static void ApplyField(ExternalClipDef def, string text)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0)
            return;
        var key = text.Substring(0, colon).Trim();
        var value = colon + 1 < text.Length ? Unquote(text.Substring(colon + 1).Trim()) : "";

        switch (key)
        {
            case "name":
                def.Name = value;
                break;
            case "takeName":
                def.TakeName = value;
                break;
            case "firstFrame":
            case "first":
                if (TryParseFloat(value, out var first))
                    def.FirstFrame = first;
                break;
            case "lastFrame":
            case "last":
                if (TryParseFloat(value, out var lastValue))
                    def.LastFrame = lastValue;
                break;
            case "loopTime":
                def.Loop = value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                break;
        }
    }

    /// <summary>Strips one layer of matching single or double quotes (Unity quotes names
    /// containing YAML-special characters).</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
        {
            var inner = value.Substring(1, value.Length - 2);
            // YAML single-quote escaping doubles the quote character.
            return value[0] == '\'' ? inner.Replace("''", "'") : inner;
        }
        return value;
    }

    private static bool TryParseFloat(string value, out float parsed)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
           && float.IsFinite(parsed);
}
