#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;
using SrcClip = HumanoidMocap.Skeleton.Clip;

namespace HumanoidMocap.Solve;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Mirrors a SOURCE clip across the source character's sagittal plane, on the source
/// skeleton, BEFORE solving. The mirrored take then runs through the completely
/// unmodified primary conversion pipeline (solver, cleanups, IK bake, DMX, compile), so
/// the emitted twin is a first-class primary clip in every representational respect.
/// </summary>
/// <remarks>
/// <para><b>Why source-side (southpaw G8 mirror fix).</b> Target-space mirroring of the
/// SOLVED channels produces data that is geometrically exact (bit-scale reflections,
/// verified) yet renders broken in the engine: the render-side sequence evaluation
/// applies conversions to the data that do not commute with reflection, and the breakage
/// is invisible to every CPU-side API (bone transforms sample as perfect mirrors while
/// the skinned mesh collapses). Empirical bisect: injecting the mirrored PELVIS channel
/// alone flips the whole render upside down while CPU FK stays upright; helper, arm and
/// face channel injections behave differently in-engine than on the CPU. A clip whose
/// body was mirrored BEFORE the solve carries no such alien data: it is
/// indistinguishable from a clip an animator authored mirrored, and the engine renders
/// primary-shaped data correctly for any pose.</para>
/// <para><b>Math.</b> Identical conjugation to <see cref="ClipMirror"/> (locals
/// conjugated by the reflection, bones permuted by their L/R partner), with the plane
/// normal computed from the SOURCE character frame (roles from the source mapping) and
/// snapped to an exact axis when near one. The plane passes through the source origin;
/// source clips are authored near the origin, so the residual lateral placement shift is
/// the rest hips offset and the downstream placement passes own it.</para>
/// <para><b>Pairing.</b> Mapped bones pair role-to-mirrored-role. Unmapped bones pair by
/// side tokens supporting underscore/dot separators (<c>arm_l</c>, <c>thigh.r</c>) and
/// Left/Right name words (<c>mixamorig:LeftArm</c>). Anything unpaired mirrors in place.
/// Pairs whose parents are not themselves mirror partners would corrupt under local
/// conjugation, so they fall back to mirroring in place and are counted in the report
/// note.</para>
/// </remarks>
public static class SourceMirror
{
    private const float AxisSnapTolerance = 1e-3f;

    /// <summary>
    /// Returns a scene identical to <paramref name="scene"/> except that clip
    /// <paramref name="take"/> is replaced by its sagittal mirror (other clips are
    /// shared, the skeleton is shared).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the source character frame is not
    /// computable or a sided role maps without its counterpart.</exception>
    public static SourceScene MirrorTake(SourceScene scene, MappingResult map, int take)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(map);
        if (take < 0 || take >= scene.Clips.Count)
            throw new ArgumentOutOfRangeException(nameof(take));

        var skeleton = scene.Skeleton;
        var lateral = LateralAxis(skeleton, map);
        var pair = BuildPairing(skeleton, map, out var inPlaceFallbacks);

        var clip = scene.Clips[take];
        var frames = new List<XForm[]>(clip.Frames.Count);
        foreach (var locals in clip.Frames)
        {
            if (locals.Length != skeleton.Count)
                throw new ArgumentException(
                    $"Frame has {locals.Length} bones but the source skeleton has {skeleton.Count}.");
            var mirrored = new XForm[locals.Length];
            for (var i = 0; i < locals.Length; i++)
            {
                var source = locals[pair[i]];
                mirrored[i] = new XForm(
                    ReflectPoint(source.Pos, lateral),
                    ReflectRotation(source.Rot, lateral));
            }
            frames.Add(mirrored);
        }

        // The REST skeleton mirrors with the clip. Mid-pose packs whose adopted rest IS
        // an asymmetric pose (a boxing stance kept as rest) would otherwise interpret
        // the mirrored clip against the unmirrored rest, corrupting every rest-relative
        // transfer (measured 25 cm of reflection error on step clips). With rest and
        // clip conjugated by the same pairing the solve is fully mirror-covariant; a
        // symmetric rest round-trips unchanged up to float dirt.
        var definitions = new List<BoneDefinition>(skeleton.Count);
        for (var i = 0; i < skeleton.Count; i++)
        {
            var rest = skeleton[pair[i]].RestLocal;
            definitions.Add(new BoneDefinition(
                skeleton[i].Name,
                skeleton[i].ParentIndex < 0 ? null : skeleton[skeleton[i].ParentIndex].Name,
                new XForm(ReflectPoint(rest.Pos, lateral), ReflectRotation(rest.Rot, lateral))));
        }
        var mirroredSkeleton = SkeletonModel.Create(definitions);

        var clips = new List<SrcClip>(scene.Clips);
        clips[take] = new SrcClip(clip.Name + "_mirrored", clip.Fps, clip.Looping, frames);

        // NOTE: only clip[take] is consistent with the mirrored skeleton; the shared
        // remaining clips are untouched primaries. Callers consume exactly the mirrored
        // take (ConvertOne does).
        return new SourceScene(
            mirroredSkeleton, clips, scene.UnitScaleCm,
            scene.UpAxis, scene.UpAxisSign,
            scene.FrontAxis, scene.FrontAxisSign,
            scene.CoordAxis, scene.CoordAxisSign,
            scene.OriginalUpAxis, scene.Notes)
        {
            AuthoredMapping = scene.AuthoredMapping,
            RestPlacementAuthored = scene.RestPlacementAuthored,
        };
    }

    // ---------------------------------------------------------------- plane

    private static Vector3 LateralAxis(SkeletonModel skeleton, MappingResult map)
    {
        Vector3 lateral;
        try
        {
            lateral = CharacterFrame.Compute(skeleton, map, skeleton.RestWorld).Lateral;
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException(
                $"Cannot mirror source: character frame not computable ({e.Message}).", e);
        }

        var a = Vector3.Abs(lateral);
        if (a.Y <= AxisSnapTolerance && a.Z <= AxisSnapTolerance)
            return Vector3.UnitX;
        if (a.X <= AxisSnapTolerance && a.Z <= AxisSnapTolerance)
            return Vector3.UnitY;
        if (a.X <= AxisSnapTolerance && a.Y <= AxisSnapTolerance)
            return Vector3.UnitZ;
        return lateral;
    }

    private static Vector3 ReflectPoint(Vector3 p, Vector3 n)
        => p - 2f * Vector3.Dot(p, n) * n;

    private static Quaternion ReflectRotation(Quaternion q, Vector3 n)
    {
        var v = new Vector3(q.X, q.Y, q.Z);
        var reflected = 2f * Vector3.Dot(v, n) * n - v;
        return new Quaternion(reflected.X, reflected.Y, reflected.Z, q.W);
    }

    // ---------------------------------------------------------------- pairing

    private static int[] BuildPairing(
        SkeletonModel skeleton, MappingResult map, out int inPlaceFallbacks)
    {
        var pair = new int[skeleton.Count];
        for (var i = 0; i < pair.Length; i++)
            pair[i] = i;

        // roles first: UpperArmL pairs with the bone mapped to UpperArmR, etc.
        var boneRole = new Dictionary<int, BoneRole>();
        foreach (var (role, bone) in map.RoleToBone)
            boneRole[bone] = role;
        foreach (var (role, bone) in map.RoleToBone)
        {
            if (MirrorRole(role) is not { } mirroredRole)
                continue; // center role mirrors in place
            if (!map.RoleToBone.TryGetValue(mirroredRole, out var partner))
                throw new ArgumentException(
                    $"Cannot mirror source: mapping has role {role} but not its counterpart {mirroredRole}.");
            pair[bone] = partner;
        }

        // name tokens for everything unmapped
        for (var i = 0; i < skeleton.Count; i++)
        {
            if (boneRole.ContainsKey(i))
                continue;
            var partnerName = SwapSideTokens(skeleton[i].Name);
            if (partnerName is null)
                continue;
            var partner = skeleton.IndexOf(partnerName);
            if (partner >= 0)
                pair[i] = partner;
        }

        // validation: involution strictly; hierarchy consistency falls back in place
        inPlaceFallbacks = 0;
        for (var i = 0; i < pair.Length; i++)
        {
            if (pair[pair[i]] != i)
            {
                // asymmetric mapping artifact: safer to mirror both in place
                pair[pair[i]] = pair[i];
                pair[i] = i;
                inPlaceFallbacks++;
                continue;
            }
            if (pair[i] == i)
                continue;
            var parent = skeleton[i].ParentIndex;
            var partnerParent = skeleton[pair[i]].ParentIndex;
            var consistent = parent < 0
                ? partnerParent < 0
                : partnerParent == pair[parent];
            if (!consistent)
            {
                pair[pair[i]] = pair[i];
                pair[i] = i;
                inPlaceFallbacks++;
            }
        }
        return pair;
    }

    private static BoneRole? MirrorRole(BoneRole role)
    {
        var name = role.ToString();
        var mirroredName = name[^1] switch
        {
            'L' => name[..^1] + "R",
            'R' => name[..^1] + "L",
            _ => null,
        };
        return mirroredName is not null && Enum.TryParse<BoneRole>(mirroredName, out var mirrored)
            ? mirrored
            : null;
    }

    /// <summary>Swaps side tokens: underscore or dot delimited <c>l</c>/<c>r</c> tokens
    /// (case preserved), or Left/Right words anywhere in the name. Null when the name
    /// carries no side marker.</summary>
    internal static string? SwapSideTokens(string name)
    {
        static string PairSwap(string s, string a, string b)
        {
            const string placeholder = "";
            return s.Replace(a, placeholder).Replace(b, a).Replace(placeholder, b);
        }

        // word-level Left/Right (mixamo, UE rigs): pairwise swap
        if (name.Contains("Left", StringComparison.Ordinal)
            || name.Contains("Right", StringComparison.Ordinal))
        {
            var swapped = PairSwap(name, "Left", "Right");
            if (!string.Equals(swapped, name, StringComparison.Ordinal))
                return swapped;
        }
        if (name.Contains("left", StringComparison.Ordinal)
            || name.Contains("right", StringComparison.Ordinal))
        {
            var swapped = PairSwap(name, "left", "right");
            if (!string.Equals(swapped, name, StringComparison.Ordinal))
                return swapped;
        }

        var tokens = name.Split('_', '.');
        var separators = new List<char>();
        foreach (var c in name)
        {
            if (c is '_' or '.')
                separators.Add(c);
        }
        var changed = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            var swapped = tokens[i] switch
            {
                "L" => "R", "R" => "L", "l" => "r", "r" => "l",
                _ => tokens[i],
            };
            if (swapped != tokens[i])
            {
                tokens[i] = swapped;
                changed = true;
            }
        }
        if (!changed)
            return null;
        var sb = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            sb.Append(tokens[i]);
            if (i < separators.Count)
                sb.Append(separators[i]);
        }
        return sb.ToString();
    }

}
