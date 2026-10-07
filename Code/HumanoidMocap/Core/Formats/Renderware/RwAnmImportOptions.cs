#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core.Formats.Renderware;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Options for <see cref="RwAnmImporter.Import"/>.</summary>
public sealed class RwAnmImportOptions
{
    /// <summary>Fixed resampling rate for the motion data, frames per second.</summary>
    public float SampleFps { get; init; } = 30f;

    /// <summary>
    /// Base clip name. RenderWare animations carry no take names, so a single-take file
    /// yields one clip named exactly this and a multi-take bank (.an5) yields
    /// <c>&lt;base&gt;_1</c>, <c>&lt;base&gt;_2</c>, … The pipeline passes the source file
    /// stem here. Default "motion" (the BVH importer's convention — the facade's clip
    /// naming then substitutes the file stem).
    /// </summary>
    public string ClipNameBase { get; init; } = "motion";
}
