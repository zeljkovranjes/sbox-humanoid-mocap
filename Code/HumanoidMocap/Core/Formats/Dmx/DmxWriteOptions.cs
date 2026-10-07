#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HumanoidMocap.Core.Skeleton;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Formats.Dmx;

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
