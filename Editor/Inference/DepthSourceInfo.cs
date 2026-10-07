using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HumanoidMocap.EditorTools.Inference;

/// <summary>What <see cref="DepthDetection"/> found in a file.</summary>
public sealed record DepthSourceInfo(DepthKind Kind,string Format,string Detail)
{
    public static readonly DepthSourceInfo None=new(DepthKind.None,"","");
    /// <summary>The badge shown over the preview.</summary>
    public string Label=>Kind switch{DepthKind.Full=>"Full LiDAR detected",DepthKind.Partial=>"Partial depth detected",_=>""};
}
