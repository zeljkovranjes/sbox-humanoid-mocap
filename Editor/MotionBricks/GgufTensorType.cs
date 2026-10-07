#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>Element type of a GGUF tensor (the subset of <c>ggml_type</c> MotionBricks uses).</summary>
public enum GgufTensorType : uint
{
    F32 = 0,
    F16 = 1,
    I32 = 26,
    BF16 = 30,
}
