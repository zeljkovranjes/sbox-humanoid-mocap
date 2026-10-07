#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core.Formats;

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
