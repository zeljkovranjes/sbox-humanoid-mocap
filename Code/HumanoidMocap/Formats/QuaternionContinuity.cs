#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Formats;

/// <summary>
/// Temporal hemisphere alignment for quaternion tracks. <c>q</c> and <c>-q</c> encode the same
/// rotation, but per-frame conversions (e.g. <see cref="Quaternion.CreateFromRotationMatrix"/>
/// branch changes) can flip the sign between consecutive samples; consumers that interpolate
/// numerically between samples (the engine lerps between DMX log keys) then spin the long way
/// around. Aligning each sample onto the previous sample's hemisphere
/// (<c>Dot(prev, cur) &gt;= 0</c>) is semantically a no-op but interpolation-safe.
/// </summary>
public static class QuaternionContinuity
{
    /// <summary>
    /// Negates quaternions in place where needed so every consecutive pair of the track has a
    /// non-negative dot product. The first sample is kept as-is.
    /// </summary>
    public static void Align(Span<Quaternion> track)
    {
        for (int i = 1; i < track.Length; i++)
        {
            if (Quaternion.Dot(track[i - 1], track[i]) < 0f)
                track[i] = Quaternion.Negate(track[i]);
        }
    }

    /// <summary>
    /// Aligns every bone's orientation track across the clip frames, in place: for each bone
    /// index, consecutive frames' rotations end up on the same hemisphere.
    /// Frames must all have the same bone count.
    /// </summary>
    public static void AlignFrames(IReadOnlyList<XForm[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < 2)
            return;

        int boneCount = frames[0].Length;
        for (int bone = 0; bone < boneCount; bone++)
        {
            var prev = frames[0][bone].Rot;
            for (int f = 1; f < frames.Count; f++)
            {
                var q = frames[f][bone].Rot;
                if (Quaternion.Dot(prev, q) < 0f)
                {
                    q = Quaternion.Negate(q);
                    frames[f][bone].Rot = q;
                }
                prev = q;
            }
        }
    }
}
