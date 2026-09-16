#nullable enable annotations

using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Solve;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Hand rest-geometry helpers shared by <see cref="CanonicalFrames"/> (finger secondary axes)
/// and <see cref="RestNormalizer"/> (palm-down roll correction). Everything derives from joint
/// positions only — bone local axes carry no anatomical meaning on the s&amp;box rig.
/// </summary>
internal static class HandGeometry
{
    private static readonly BoneRole[] LeftProximals =
    {
        BoneRole.ThumbProxL, BoneRole.IndexProxL, BoneRole.MiddleProxL, BoneRole.RingProxL, BoneRole.PinkyProxL,
    };

    private static readonly BoneRole[] RightProximals =
    {
        BoneRole.ThumbProxR, BoneRole.IndexProxR, BoneRole.MiddleProxR, BoneRole.RingProxR, BoneRole.PinkyProxR,
    };

    // Index → pinky order; the knuckle line is taken from the first and last mapped of these.
    private static readonly BoneRole[] LeftNonThumbProximals =
    {
        BoneRole.IndexProxL, BoneRole.MiddleProxL, BoneRole.RingProxL, BoneRole.PinkyProxL,
    };

    private static readonly BoneRole[] RightNonThumbProximals =
    {
        BoneRole.IndexProxR, BoneRole.MiddleProxR, BoneRole.RingProxR, BoneRole.PinkyProxR,
    };

    /// <summary>
    /// Midpoint of all mapped finger proximal heads of one hand (the hand's anatomical
    /// "chain child" point), or null when no finger proximal is mapped.
    /// </summary>
    public static Vector3? FingerProximalMidpoint(MappingResult map, IReadOnlyList<XForm> worldRest, bool left)
    {
        var sum = Vector3.Zero;
        var count = 0;
        foreach (var role in left ? LeftProximals : RightProximals)
        {
            if (map.RoleToBone.TryGetValue(role, out var index))
            {
                sum += worldRest[index].Pos;
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    /// <summary>
    /// Dorsal palm normal of one hand: the unit vector pointing out of the <b>back</b> of the
    /// hand (away from the palm), or null when the hand/finger geometry is unmapped or
    /// degenerate.
    /// </summary>
    /// <remarks>
    /// Formula (mirror-consistent by construction, verified on the ActorCore fixture by the
    /// finger-curl test): <c>dorsal = sideSign · cross(knuckle, fingerDir)</c> with
    /// <c>sideSign = +1</c> left / <c>−1</c> right, <c>knuckle = IndexProx.head −
    /// PinkyProx.head</c> (first/last mapped non-thumb proximal), and <c>fingerDir =
    /// FingerProximalMidpoint − Hand.head</c>. On every fixture rig the thumb proximal lies on
    /// the −dorsal (palmar) side of the hand plane, grounding the sign anatomically. A positive
    /// rotation about a finger frame's hinge axis (frame Y = cross(dorsal, fingerChainDir))
    /// curls the fingertip toward the palm on <b>both</b> hands.
    /// </remarks>
    public static Vector3? Dorsal(MappingResult map, IReadOnlyList<XForm> worldRest, bool left)
    {
        if (!map.RoleToBone.TryGetValue(left ? BoneRole.HandL : BoneRole.HandR, out var handIndex))
            return null;
        var hand = worldRest[handIndex].Pos;

        var nonThumb = left ? LeftNonThumbProximals : RightNonThumbProximals;
        Vector3? first = null, last = null;
        foreach (var role in nonThumb)
        {
            if (!map.RoleToBone.TryGetValue(role, out var index))
                continue;
            first ??= worldRest[index].Pos;
            last = worldRest[index].Pos;
        }
        if (first is null || last is null || (first.Value - last.Value).LengthSquared() < 1e-8f)
            return null;

        var midpoint = FingerProximalMidpoint(map, worldRest, left);
        if (midpoint is null)
            return null;

        var knuckle = first.Value - last.Value;
        var fingerDir = midpoint.Value - hand;
        var raw = Vector3.Cross(knuckle, fingerDir) * (left ? 1f : -1f);
        return raw.LengthSquared() < 1e-8f ? null : Vector3.Normalize(raw);
    }
}
