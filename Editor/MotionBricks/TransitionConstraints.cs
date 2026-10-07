#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Numerics;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Sparse boundary constraints of one transition: four context frames at the start and four
/// target frames at the end (unnormalized features, see <see cref="EncodedFrames"/>), which of
/// them are present, and the transition length in tokens (four frames each, 6–16).
/// </summary>
public sealed class TransitionConstraints
{
    public required EncodedFrames Context { get; init; }
    public required EncodedFrames Target { get; init; }
    public int Tokens { get; init; } = 10;
    public byte[] HasGlobalRoot { get; init; } = { 1, 1, 1, 1, 1, 1, 1, 1 };
    public byte[] HasLocalRoot { get; init; } = { 1, 1, 1, 0, 1, 1, 1, 1 };
    public byte[] HasPoses { get; init; } = { 1, 1, 1, 1, 1, 1, 1, 1 };
    public ulong Seed { get; init; } = 1;
    public bool Argmax { get; init; }
}
