#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Target;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Generates <c>AE_FOOTSTEP</c> <see cref="AnimEventEntry"/> lists from foot-plant detection
/// on a solved TARGET clip: each detected plant interval's START frame is the touchdown
/// moment, so it becomes one footstep event for that foot.
/// </summary>
/// <remarks>
/// <para><b>Shipped-data findings</b> (dev fixture
/// <c>citizen_animationlist.vmdl_prefab</c>): every one of the 28 shipped AnimEvent nodes is
/// <c>event_class = "AE_FOOTSTEP"</c> with an <c>event_keys</c> object of exactly
/// <c>Attachment = "foot_L"</c>/<c>"foot_R"</c>, <c>Foot = "0"</c> (left) / <c>"1"</c>
/// (right, a STRING) and <c>Volume = 0.7</c>. The generated events replicate that shape
/// verbatim, including the side encoding.</para>
/// <para><b>Frame-0 plants are skipped</b>: a plant interval that begins on the clip's first
/// frame means the foot was ALREADY planted when the clip starts — no touchdown happened
/// inside the clip, and on looping clips the same physical step would otherwise fire twice
/// (once at the wrap-around touchdown near the clip end, once at frame 0).</para>
/// </remarks>
public static class FootstepEvents
{
    /// <summary>The Source 2 footstep anim-event class (the only event class present in the
    /// shipped citizen animation data).</summary>
    public const string FootstepEventClass = "AE_FOOTSTEP";

    /// <summary>The constant volume used by every shipped footstep event.</summary>
    public const double FootstepVolume = 0.7;

    /// <summary>
    /// Detects plant intervals on <paramref name="frames"/> (via
    /// <see cref="FootPlant.DetectPlantIntervals"/>) and returns one <c>AE_FOOTSTEP</c> event
    /// per plant START (frame-0 plants skipped, see class remarks), merged across both feet
    /// in frame order.
    /// </summary>
    /// <param name="frames">Solved per-frame local transforms (target skeleton bone order).</param>
    /// <param name="skeleton">Target skeleton the frames are expressed against.</param>
    /// <param name="left">Left leg chain (target bone indices).</param>
    /// <param name="right">Right leg chain (target bone indices).</param>
    /// <param name="up">Target character up direction.</param>
    /// <param name="fps">Clip sample rate.</param>
    /// <param name="options">Plant-detection tunables; null = defaults (callers rescale the
    /// cm-tuned thresholds for engine-space rigs, like the foot-plant cleanup does).</param>
    public static List<AnimEventEntry> Generate(
        List<XForm[]> frames,
        SkeletonModel skeleton,
        FootChain left,
        FootChain right,
        Vector3 up,
        float fps,
        FootPlantOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var (plantsL, plantsR) = FootPlant.DetectPlantIntervals(
            frames, skeleton, left, right, up, fps, options);

        var events = new List<AnimEventEntry>();
        AddFoot(events, plantsL, attachment: "foot_L", foot: "0");
        AddFoot(events, plantsR, attachment: "foot_R", foot: "1");
        // Stable frame ordering across both feet (List.Sort is unstable; the comparer breaks
        // frame ties on the side key so the result is deterministic).
        events.Sort((a, b) => a.Frame != b.Frame
            ? a.Frame.CompareTo(b.Frame)
            : string.CompareOrdinal(a.Foot, b.Foot));
        return events;
    }

    private static void AddFoot(
        List<AnimEventEntry> events, List<FrameRange> plants, string attachment, string foot)
    {
        foreach (var plant in plants)
        {
            if (plant.Start == 0)
                continue; // already planted at clip start: no touchdown inside the clip
            events.Add(new AnimEventEntry
            {
                EventClass = FootstepEventClass,
                Frame = plant.Start,
                Attachment = attachment,
                Foot = foot,
                Volume = FootstepVolume,
            });
        }
    }
}
