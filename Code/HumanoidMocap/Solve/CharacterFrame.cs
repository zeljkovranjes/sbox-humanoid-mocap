#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Solve;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// The character-level reference frame of a rig, derived purely from rest <b>geometry</b>
/// (joint positions) — never from bone local axes, which encode no anatomy on the s&amp;box
/// Citizen rig (bone-Y points chest-forward there, not along the limb).
/// </summary>
/// <remarks>
/// Definition (identical for every rig, so canonical deltas transfer between rigs):
/// <list type="bullet">
/// <item><see cref="Up"/> = normalize(midShoulders − midHips), where midHips is the midpoint
/// of the UpperLeg heads and midShoulders the midpoint of the UpperArm heads (falling back to
/// the Clavicle heads, then the Neck head, when arms are unmapped).</item>
/// <item><see cref="Lateral"/> = normalize((UpperLegL.head − UpperLegR.head) ⊥ Up) —
/// left-positive.</item>
/// <item><see cref="Forward"/> = normalize(cross(Lateral, Up)). Verified against all three
/// fixture rigs: with left-positive lateral and a right-handed cross this points the way the
/// toes point (s&amp;box human male: +Z in its Y-up armature space; Mixamo: +Z Y-up;
/// ActorCore/CC: −Y in its Z-up space).</item>
/// <item><see cref="HipHeight"/> = (midHips − ground) · Up, where ground is the smallest
/// Up-projection over the mapped Foot/Toe heads (all bones when no feet are mapped).</item>
/// </list>
/// </remarks>
internal sealed class CharacterFrame
{
    /// <summary>Character up: hips toward shoulders, unit length.</summary>
    public Vector3 Up { get; private init; }

    /// <summary>Character forward: the direction the toes point, unit length, ⊥ <see cref="Up"/>.</summary>
    public Vector3 Forward { get; private init; }

    /// <summary>Character lateral, left-positive (right hip → left hip), unit length, ⊥ <see cref="Up"/>.</summary>
    public Vector3 Lateral { get; private init; }

    /// <summary>Midpoint of the upper-leg heads (the hip line center), rig world space, cm.</summary>
    public Vector3 MidHips { get; private init; }

    /// <summary>Rest height of <see cref="MidHips"/> above the lowest foot/toe point, along <see cref="Up"/>, cm.</summary>
    public float HipHeight { get; private init; }

    private CharacterFrame() { }

    /// <summary>
    /// Computes the character frame of <paramref name="skeleton"/> from the given rest world
    /// transforms (pass <c>skeleton.RestWorld</c> for the bind rest, or a normalized rest).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the mapping lacks the bones the frame
    /// needs (both upper legs, plus arms/clavicles/neck for the shoulder line).</exception>
    public static CharacterFrame Compute(
        SkeletonModel skeleton, MappingResult map, IReadOnlyList<XForm> worldRest, Vector3? worldUp = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(worldRest);

        Vector3? Pos(BoneRole role)
            => map.RoleToBone.TryGetValue(role, out var index) ? worldRest[index].Pos : null;

        var legL = Pos(BoneRole.UpperLegL);
        var legR = Pos(BoneRole.UpperLegR);
        if (legL is null || legR is null)
            throw new ArgumentException("Character frame requires both UpperLegL and UpperLegR to be mapped.");

        var midHips = (legL.Value + legR.Value) * 0.5f;

        var midShoulders = Midpoint(Pos(BoneRole.UpperArmL), Pos(BoneRole.UpperArmR))
            ?? Midpoint(Pos(BoneRole.ClavicleL), Pos(BoneRole.ClavicleR))
            ?? Pos(BoneRole.Neck)
            ?? throw new ArgumentException(
                "Character frame requires UpperArmL/R, ClavicleL/R or Neck to be mapped for the shoulder line.");

        var upRaw = midShoulders - midHips;
        if (upRaw.LengthSquared() < 1e-8f)
            throw new ArgumentException("Degenerate rig: shoulder line coincides with hip line.");
        // A locomotion export may store a crouched animation frame as its rest.
        // Its shoulder-to-hip vector describes body lean, not the ground normal.
        var up = Vector3.Normalize(worldUp ?? upRaw);

        var acrossHips = legL.Value - legR.Value;
        var lateralRaw = acrossHips - up * Vector3.Dot(acrossHips, up);
        if (lateralRaw.LengthSquared() < 1e-8f)
            throw new ArgumentException("Degenerate rig: hip line is parallel to the up direction.");
        var lateral = Vector3.Normalize(lateralRaw);

        // Right-handed completion; left-positive lateral makes this the toe direction
        // (verified on the s&box human male, Mixamo and ActorCore fixture rigs).
        var forward = Vector3.Normalize(Vector3.Cross(lateral, up));

        return new CharacterFrame
        {
            Up = up,
            Forward = forward,
            Lateral = lateral,
            MidHips = midHips,
            HipHeight = Vector3.Dot(midHips, up) - GroundLevel(map, worldRest, up),
        };
    }

    private static float GroundLevel(MappingResult map, IReadOnlyList<XForm> worldRest, Vector3 up)
    {
        var ground = float.PositiveInfinity;
        foreach (var role in new[] { BoneRole.FootL, BoneRole.FootR, BoneRole.ToeL, BoneRole.ToeR })
        {
            if (map.RoleToBone.TryGetValue(role, out var index))
                ground = MathF.Min(ground, Vector3.Dot(worldRest[index].Pos, up));
        }

        if (float.IsPositiveInfinity(ground))
        {
            // No feet mapped: fall back to the lowest mapped bone of any role.
            foreach (var index in map.RoleToBone.Values)
                ground = MathF.Min(ground, Vector3.Dot(worldRest[index].Pos, up));
        }

        return ground;
    }

    private static Vector3? Midpoint(Vector3? a, Vector3? b)
        => a is not null && b is not null ? (a.Value + b.Value) * 0.5f : null;
}
