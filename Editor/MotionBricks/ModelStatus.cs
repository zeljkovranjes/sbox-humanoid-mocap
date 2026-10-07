#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;

namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>Result of inspecting an install folder.</summary>
public sealed record ModelStatus( ModelState State, string Directory, long BytesOnDisk, long TotalBytes, string? Problem )
{
    public bool IsUsable => State is ModelState.Ready or ModelState.Unverified;
}
