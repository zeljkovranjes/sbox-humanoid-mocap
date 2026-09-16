using System;
using System.Linq;
using HumanoidMocap.Formats.Fbx;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Motion;

public static class MotionFbxExport
{
    /// <summary>Export the reconstructed armature directly, without requiring a target rig
    /// or resampling the captured timestamps. The motion document retains observation evidence.</summary>
    public static byte[] Write(MotionDocument motion)
    {
        motion.Validate();
        if ((long)motion.Bones.Count * motion.Frames.Count > FbxAnimationWriter.MaximumTransformSamples)
            throw new ArgumentException("Animation exceeds the export budget. Select a shorter range.");
        var skeleton = Skeleton.Skeleton.Create(motion.Bones.Select(b => new BoneDefinition(b.Name,
            b.Parent < 0 ? null : motion.Bones[b.Parent].Name,
            new XForm(MotionDocument.V(b.RestPosition) * 100, MotionDocument.Q(b.RestRotation)))).ToArray());
        var indices = skeleton.Bones.Select(b => motion.Bones.FindIndex(source => source.Name == b.Name)).ToArray();
        var frames = motion.Frames.Select(f => indices.Select(i => new XForm(
            MotionDocument.V(f.Positions[i]) * 100, MotionDocument.Q(f.Rotations[i]))).ToArray()).ToArray();
        if(motion.Objects.Any(p=>p.Space!=motion.Space))throw new InvalidOperationException("Align object tracks to the capture coordinate space before exporting.");
        var times=motion.Frames.Select(f=>f.Time).ToArray();
        var combined=PropAnimation.Append(skeleton,frames,times,new PropContactMotion(motion),
            new CapturePlacement(100,System.Numerics.Quaternion.Identity,System.Numerics.Vector3.Zero));
        return FbxAnimationWriter.Write(combined.Skeleton, motion.Name, combined.Frames, times,
            motion.SourceFps, motionSpace: motion.Space.ToString());
    }
}
