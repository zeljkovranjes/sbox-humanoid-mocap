#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HumanoidMocap.Core.Target;

/// <summary>
/// One detected locomotion family to emit as a directional 2D blend: a <c>Folder</c> named by
/// the family stem grouping the member AnimFile entries plus one <c>2DBlend</c> node wired to
/// the citizen pose parameters (<c>move_x</c>/<c>move_y</c>). Produced by
/// <see cref="LocomotionSetDetector"/>; consumed by <see cref="VmdlWriter"/> and
/// <see cref="VmdlAugmenter"/>.
/// </summary>
public sealed class LocomotionSetSpec
{
    /// <summary>Name of the Folder node grouping the family (the stem, collision-suffixed).</summary>
    public required string FolderName { get; init; }

    /// <summary>Name of the 2DBlend node (<c>&lt;stem&gt;_2D</c>, collision-suffixed).</summary>
    public required string BlendName { get; init; }

    /// <summary>Looping flag of the 2DBlend node (true when every member loops; the shipped
    /// locomotion blends are all looping).</summary>
    public required bool Looping { get; init; }

    /// <summary>
    /// The 3×3 <c>blend_anim_list</c> grid, <c>[row][col]</c> with rows indexed by
    /// <c>move_x</c> (−1, 0, +1) and columns by <c>move_y</c> (−1, 0, +1) — the exact shipped
    /// citizen layout: row 0 = [SW, S, SE], row 1 = [W, center, E], row 2 = [NW, N, NE].
    /// </summary>
    public required string[][] BlendGrid { get; init; }

    /// <summary>Names of the batch AnimFile entries grouped under the Folder, in canonical
    /// direction order (N, NE, E, SE, S, SW, W, NW; absent diagonals skipped).</summary>
    public required IReadOnlyList<string> MemberNames { get; init; }
}
