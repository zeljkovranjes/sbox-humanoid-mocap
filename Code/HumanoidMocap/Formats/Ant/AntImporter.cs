#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Formats.Ant;

/// <summary>Sampling and naming options for <see cref="AntImporter"/>.</summary>
public sealed class AntImportOptions
{
    /// <summary>Resample rate for the imported clips (Hz).</summary>
    public float SampleFps { get; init; } = 30f;

    /// <summary>
    /// Authored tick rate of the package. ANT key times are in TICKS; Fight Night's packages
    /// are authored at 30 ticks per second (a 66-tick punch is 2.2 s, which matches the
    /// shipped clip lengths).
    /// </summary>
    public float TicksPerSecond { get; init; } = 30f;

    /// <summary>Base name for clips (ANT clip chunks carry no name of their own —
    /// see <see cref="AntImporter"/> remarks). Takes become <c>{Base}_1</c>, <c>_2</c>, …</summary>
    public string ClipNameBase { get; init; } = "clip";
}

/// <summary>
/// Imports EA ANT animation packages (<c>.cba</c>) into a <see cref="SourceScene"/>.
/// Container decoding lives in <see cref="AntStream"/>; the joint table arrives as companion
/// bytes via <see cref="AntSkeletonJson"/>.
/// </summary>
/// <remarks>
/// <para><b>Where the rest pose comes from.</b> ANT stores NO bind pose: its
/// <c>SkeletonAsset</c> carries only <c>JointName</c> and <c>ParentIndex</c> per joint. This
/// importer therefore adopts the clip's FIRST FRAME as the rest reference — the same rule the
/// retargeter already applies to non-anatomical binds (see <c>RestNormalizer</c>'s reference
/// pose and the mid-pose FBX path), and the only rest information the format actually
/// contains. Joints the clip does not animate keep an IDENTITY rest local and no channels:
/// they are helper/simulation bones (<c>Muscle_*</c>, <c>*_Jiggle</c>, <c>Offset_*</c>) that
/// carry no humanoid role, and an identity local leaves them rigidly attached to their parent
/// rather than inventing a pose for them.</para>
/// <para><b>Consequence for sparse packages.</b> Fight Night's <c>package_proxy_*</c> files
/// animate only 13 IK-proxy joints — no upper arms, hands or thighs — so they import cleanly
/// but cannot satisfy the retargeter's 15 required humanoid slots and are correctly rejected
/// downstream as non-humanoid. The full-body packages (<c>package_main</c>,
/// <c>package_frontend</c>, the <c>package_nis_*</c> cutscenes) are the importable ones.</para>
/// <para><b>Clip names.</b> Clip chunks carry tag/marker strings but no authored clip name in
/// a position this decoder trusts, so takes are named from the file stem plus their index —
/// the same convention <c>RwAnmImporter</c> uses for RenderWare banks.</para>
/// </remarks>
public static class AntImporter
{
    /// <summary>
    /// Reads an ANT package. <paramref name="skeletonData"/> must carry the companion joint
    /// table (see <see cref="AntSkeletonJson"/>); the animation alone cannot name its joints.
    /// </summary>
    /// <exception cref="FormatException">Not an ANT stream, missing/invalid companion joint
    /// table, no clips, or channels referencing joints outside the table.</exception>
    public static SourceScene Import(
        byte[] data, byte[]? skeletonData, AntImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new AntImportOptions();
        if (!(options.SampleFps > 0f) || !float.IsFinite(options.SampleFps))
            throw new ArgumentOutOfRangeException(nameof(options), "SampleFps must be positive.");
        if (!(options.TicksPerSecond > 0f) || !float.IsFinite(options.TicksPerSecond))
            throw new ArgumentOutOfRangeException(nameof(options), "TicksPerSecond must be positive.");

        if (skeletonData is null)
            throw new FormatException(
                "EA ANT packages (.cba) bind animation channels to joints by INDEX and carry no "
                + "joint table. Supply the companion joint table (a JSON array of "
                + "{\"name\", \"parent\"} in joint-index order, as extracted from the rig "
                + "package) as the skeleton data.");

        var joints = AntSkeletonJson.Parse(skeletonData);
        var clipsData = AntStream.ParseClips(data);
        if (clipsData.Count == 0)
            throw new FormatException("EA ANT package contains no animation clips.");

        foreach (var clip in clipsData)
        {
            foreach (var channel in clip.Channels)
            {
                if (channel.JointIndex < 0 || channel.JointIndex >= joints.Count)
                    throw new FormatException(
                        $"Animation references joint index {channel.JointIndex} but the companion "
                        + $"joint table has {joints.Count} joints — mismatched rig package.");
            }
        }

        // Rest = first frame of the first clip that animates the joint (see remarks).
        var restLocals = new XForm[joints.Count];
        for (var i = 0; i < restLocals.Length; i++)
            restLocals[i] = XForm.Identity;
        var seeded = new bool[joints.Count];
        foreach (var clip in clipsData)
        {
            foreach (var channel in clip.Channels)
            {
                if (channel.Keys.Length > 0 && !seeded[channel.JointIndex])
                {
                    restLocals[channel.JointIndex] = channel.Keys[0];
                    seeded[channel.JointIndex] = true;
                }
            }
        }

        var definitions = new BoneDefinition[joints.Count];
        for (var i = 0; i < joints.Count; i++)
        {
            var parent = joints[i].Parent;
            definitions[i] = new BoneDefinition(
                joints[i].Name,
                parent >= 0 ? joints[parent].Name : null,
                restLocals[i]);
        }
        var skeleton = SkeletonModel.Create(definitions);

        // Skeleton.Create topologically sorts, so joint-table indices must be remapped.
        var boneOf = new int[joints.Count];
        for (var i = 0; i < joints.Count; i++)
            boneOf[i] = skeleton.IndexOf(joints[i].Name);

        var clips = new List<Clip>(clipsData.Count);
        for (var c = 0; c < clipsData.Count; c++)
        {
            var name = clipsData.Count > 1
                ? $"{options.ClipNameBase}_{c + 1}"
                : options.ClipNameBase;
            clips.Add(Sample(clipsData[c], skeleton, boneOf, restLocals, name, options));
        }

        var notes = new List<string>
        {
            $"EA ANT package: {clips.Count} clip(s) over {joints.Count} joints "
            + $"({CountAnimated(clipsData)} animated).",
            "ANT carries no bind pose (SkeletonAsset stores only JointName/ParentIndex); the "
            + "clip's first frame is the rest reference and unanimated joints keep an identity "
            + "rest local.",
        };

        // ANT authors Y-up, Z-forward (the joint tables and proxy tracks are consistent with
        // the Maya/MotionBuilder export convention the rig is built in). Units are the
        // package's own; the solver's hip-height normalization absorbs the absolute scale.
        return new SourceScene(skeleton, clips, unitScaleCm: 1f, notes: notes);
    }

    private static int CountAnimated(List<AntStream.ClipData> clips)
    {
        var set = new HashSet<int>();
        foreach (var clip in clips)
        {
            foreach (var channel in clip.Channels)
                set.Add(channel.JointIndex);
        }
        return set.Count;
    }

    private static Clip Sample(
        AntStream.ClipData data, SkeletonModel skeleton, int[] boneOf, XForm[] restLocals,
        string name, AntImportOptions options)
    {
        var seconds = Math.Max(data.DurationTicks, 0) / options.TicksPerSecond;
        var frameCount = Math.Max(1, (int)Math.Round(seconds * options.SampleFps) + 1);

        var frames = new List<XForm[]>(frameCount);
        for (var f = 0; f < frameCount; f++)
        {
            var tick = f / options.SampleFps * options.TicksPerSecond;
            var locals = new XForm[skeleton.Count];
            for (var i = 0; i < locals.Length; i++)
                locals[i] = skeleton[i].RestLocal;

            foreach (var channel in data.Channels)
            {
                var bone = boneOf[channel.JointIndex];
                if (bone >= 0)
                    locals[bone] = SampleAt(channel, tick);
            }
            frames.Add(locals);
        }

        QuaternionContinuity.AlignFrames(frames);
        return new Clip(name, options.SampleFps, looping: false, frames);
    }

    /// <summary>Linear/slerp interpolation of a channel at a tick (keys are sparse and
    /// unevenly spaced — a 66-tick punch typically carries 4 keys).</summary>
    private static XForm SampleAt(AntStream.Channel channel, float tick)
    {
        var times = channel.Times;
        var keys = channel.Keys;
        if (keys.Length == 1 || tick <= times[0])
            return keys[0];
        if (tick >= times[^1])
            return keys[^1];

        var hi = 1;
        while (hi < times.Length - 1 && times[hi] < tick)
            hi++;
        var lo = hi - 1;
        var span = times[hi] - times[lo];
        var u = span > 1e-6f ? Math.Clamp((tick - times[lo]) / span, 0f, 1f) : 0f;

        return new XForm(
            Vector3.Lerp(keys[lo].Pos, keys[hi].Pos, u),
            MathQ.Normalize(Quaternion.Slerp(keys[lo].Rot, keys[hi].Rot, u)));
    }
}
