#nullable enable annotations

using System;
using System.Collections.Generic;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Skeleton;

/// <summary>
/// A sampled animation clip: a fixed-rate sequence of frames, each holding one
/// parent-relative local transform per bone (skeleton bone order). Clips are always
/// resampled at ingest — no key data is preserved.
/// </summary>
public sealed class Clip
{
    /// <summary>Clip (sequence) name.</summary>
    public string Name { get; }

    /// <summary>Sample rate in frames per second.</summary>
    public float Fps { get; }

    /// <summary>
    /// Native frame rate of the take in the SOURCE file (FBX GlobalSettings
    /// TimeMode/CustomFrameRate, BVH 1/FrameTime). External frame ranges — Unity
    /// <c>.fbx.meta</c> <c>clipAnimations</c> definitions — are expressed in THIS rate, so
    /// they must be rescaled by <c>Fps / NativeFps</c> to index the resampled
    /// <see cref="Frames"/>. Equals <see cref="Fps"/> when the importer records no native rate.
    /// </summary>
    public float NativeFps { get; }

    /// <summary>Whether the clip is authored to loop.</summary>
    public bool Looping { get; }

    /// <summary>Frames in playback order; each entry is one local transform per bone.</summary>
    public List<XForm[]> Frames { get; }

    /// <summary>Number of frames currently in the clip.</summary>
    public int FrameCount => Frames.Count;

    /// <summary>
    /// Clip duration in seconds at <see cref="Fps"/>: the time span between the first and the
    /// last sample, <c>(FrameCount - 1) / Fps</c> (frames are fence posts, intervals are the
    /// spans between them — matching the DMX timeFrame this clip serializes to). Zero for
    /// empty and single-frame clips.
    /// </summary>
    public float Duration => FrameCount <= 1 ? 0f : (FrameCount - 1) / Fps;

    /// <summary>Creates an empty clip.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="fps"/> is not positive.</exception>
    public Clip(string name, float fps, bool looping)
        : this(name, fps, looping, new List<XForm[]>())
    {
    }

    /// <summary>Creates a clip wrapping an existing frame list (not copied).</summary>
    /// <param name="nativeFps">Source-file native frame rate (<see cref="NativeFps"/>);
    /// null = same as <paramref name="fps"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="fps"/> (or a
    /// provided <paramref name="nativeFps"/>) is not positive.</exception>
    public Clip(string name, float fps, bool looping, List<XForm[]> frames, float? nativeFps = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(frames);
        if (!(fps > 0f) || !float.IsFinite(fps))
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "Fps must be a positive finite number.");
        if (nativeFps is { } native && (!(native > 0f) || !float.IsFinite(native)))
            throw new ArgumentOutOfRangeException(nameof(nativeFps), native, "NativeFps must be a positive finite number.");

        Name = name;
        Fps = fps;
        NativeFps = nativeFps ?? fps;
        Looping = looping;
        Frames = frames;
    }
}
