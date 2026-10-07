#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>Progress of an install, for the progress bar and its caption.</summary>
public readonly record struct InstallProgress( long BytesDone, long BytesTotal, string CurrentFile, double BytesPerSecond, string Phase )
{
    public float Fraction => BytesTotal > 0 ? Math.Clamp( BytesDone / (float)BytesTotal, 0f, 1f ) : 0f;
}
