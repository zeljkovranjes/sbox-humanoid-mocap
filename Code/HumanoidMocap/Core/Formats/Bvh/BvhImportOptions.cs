#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core.Formats.Bvh;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>Options for <see cref="BvhImporter.Import"/>.</summary>
public sealed class BvhImportOptions
{
    /// <summary>Fixed resampling rate for the motion data, frames per second.</summary>
    public float SampleFps { get; init; } = 30f;
}
