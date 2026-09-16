#nullable enable
using System;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace HumanoidMocap.Inference;
using Vector3=System.Numerics.Vector3;

/// <summary>Upstream GVHMR root/contact-target post-processing. This is source-model processing;
/// target-proportion IK still belongs after retargeting. Preserve raw network output separately.
/// Adapted from postprocess.py at ee960bb6; see Gvhmr.LICENSE.</summary>
public static class GvhmrContactProcessing
{
    static readonly int[] contactJoints={7,10,8,11,20,21};
    public static ReadOnlySpan<int> ContactJoints=>contactJoints;
    public sealed record Result(GvhmrDecoder.Root Root,Vector3[] JointPositions,Vector3[] ContactTargets);

    public static Result CorrectRoot(SmplxSkeleton skeleton,GvhmrDecoder.Pose pose,GvhmrDecoder.Root root,
        float[] staticLogits,Vector3[]? cameraTranslation=null,CancellationToken cancellation=default)
    {
        var n=pose.Frames;
        if(n<1||n>GvhmrTemporalNetwork.MaximumFrames||root.Orientation.Length!=n||root.Translation.Length!=n||
            pose.BodyRotations.Length!=n*21||pose.Betas.Length!=n*10||staticLogits.Length!=n*6||staticLogits.Any(x=>!float.IsFinite(x)))
            throw new ArgumentException("Invalid GVHMR contact tracks.");
        var original=new Vector3[n*22];
        for(var t=0;t<n;t++)
        {
            cancellation.ThrowIfCancellationRequested();
            var frame=skeleton.Forward(pose.Betas.AsSpan(t*10,10),pose.BodyRotations.AsSpan(t*21,21),root.Orientation[t],root.Translation[t]);
            frame.Position.CopyTo(original,t*22);
        }
        // A supplied camera-translation track explicitly selects the static-camera prior.
        // Never infer a static camera from a lack of odometry.
        var corrected=cameraTranslation is null?DynamicRoot(root.Translation,original,staticLogits):StaticRoot(skeleton,pose,root,original,staticLogits,cameraTranslation,cancellation);
        var joints=new Vector3[original.Length];var ground=float.PositiveInfinity;
        for(var t=0;t<n;t++)for(var j=0;j<22;j++)
        {joints[t*22+j]=original[t*22+j]+corrected[t]-root.Translation[t];ground=Math.Min(ground,joints[t*22+j].Y);}
        for(var t=0;t<n;t++)
        {corrected[t].Y-=ground;for(var j=0;j<22;j++)joints[t*22+j].Y-=ground;}
        var targets=CreateTargets(joints,staticLogits,n);
        return new(new((Quaternion[])root.Orientation.Clone(),corrected),joints,targets);
    }
    static Vector3[] DynamicRoot(Vector3[] root,Vector3[] joints,float[] logits)
    {
        var corrected=new Vector3[root.Length];corrected[0]=root[0];
        for(var t=1;t<root.Length;t++)
        {
            var maximum=float.NegativeInfinity;
            for(var j=0;j<6;j++)if(logits[(t-1)*6+j]>0)maximum=Math.Max(maximum,logits[(t-1)*6+j]);
            var drift=Vector3.Zero;float weightSum=0;
            if(float.IsFinite(maximum))for(var j=0;j<6;j++)
            {
                var confidence=logits[(t-1)*6+j];if(confidence<=0)continue;
                var weight=MathF.Exp(confidence-maximum);weightSum+=weight;
                drift+=(joints[t*22+contactJoints[j]]-joints[(t-1)*22+contactJoints[j]])*weight;
            }
            if(weightSum>0)drift/=weightSum;
            corrected[t]=corrected[t-1]+root[t]-root[t-1]-drift;
        }
        // Match the original x/z root filter before IK; no filtering of finished contacts.
        var smooth=GvhmrDecoder.Gaussian(corrected,3);
        for(var t=0;t<root.Length;t++){corrected[t].X=smooth[t].X;corrected[t].Z=smooth[t].Z;}
        return corrected;
    }
    static Vector3[] StaticRoot(SmplxSkeleton skeleton,GvhmrDecoder.Pose pose,GvhmrDecoder.Root root,
        Vector3[] world,float[] logits,Vector3[] cameraTranslation,CancellationToken cancellation)
    {
        var n=pose.Frames;
        if(cameraTranslation.Length!=n||cameraTranslation.Any(v=>!GvhmrDecoder.Finite(v)))throw new ArgumentException("Invalid static-camera translation track.");
        var smoothCamera=GvhmrDecoder.Gaussian(cameraTranslation,5);var cameraPelvis=new Vector3[n];
        for(var t=0;t<n;t++)
        {
            cancellation.ThrowIfCancellationRequested();
            cameraPelvis[t]=skeleton.Forward(pose.Betas.AsSpan(t*10,10),pose.BodyRotations.AsSpan(t*21,21),pose.CameraOrientation[t],smoothCamera[t]).Position[0];
        }
        var cameraToWorld=Quaternion.Normalize(root.Orientation[0]*Quaternion.Conjugate(pose.CameraOrientation[0]));
        var offset=world[0]-Vector3.Transform(cameraPelvis[0],cameraToWorld);
        var corrected=(Vector3[])root.Translation.Clone();var positions=(Vector3[])world.Clone();var accumulated=Vector3.Zero;
        for(var t=1;t<n;t++)
        {
            var reference=Vector3.Transform(cameraPelvis[t],cameraToWorld)+offset;
            var error=world[t*22]+accumulated-reference;
            static float Correction(float value)=>value>-.25f&&value<.25f?0:Math.Clamp(value,-.02f,.02f);
            accumulated-=new Vector3(Correction(error.X),Correction(error.Y),Correction(error.Z));
            corrected[t]+=accumulated;for(var j=0;j<22;j++)positions[t*22+j]+=accumulated;
        }
        accumulated=Vector3.Zero;
        for(var t=1;t<n;t++)
        {
            var drift=Vector3.Zero;var count=0;
            for(var j=0;j<6;j++)if(Sigmoid(logits[(t-1)*6+j])>.8f)
            {drift+=positions[t*22+contactJoints[j]]-positions[(t-1)*22+contactJoints[j]];count++;}
            if(count>0)drift/=count;drift.Y=0;accumulated-=drift;corrected[t]+=accumulated;
        }
        return corrected;
    }
    /// <summary>Targets for the upstream two-iteration limb IK. Values are weighted by the
    /// model's static-contact logits, not fabricated pose-visibility confidence.</summary>
    public static Vector3[] CreateTargets(Vector3[] joints,float[] staticLogits,int frames)
    {
        if(frames<1||joints.Length!=frames*22||staticLogits.Length!=frames*6)throw new ArgumentException("Invalid contact target dimensions.");
        var targets=new Vector3[frames*6];
        for(var j=0;j<6;j++)targets[j]=joints[contactJoints[j]];
        for(var t=1;t<frames;t++)for(var j=0;j<6;j++)
        {var previousProbability=Sigmoid(staticLogits[(t-1)*6+j]);targets[t*6+j]=targets[(t-1)*6+j]*previousProbability+joints[t*22+contactJoints[j]]*(1-previousProbability);}
        return targets;
    }
    static float Sigmoid(float x)=>1/(1+MathF.Exp(-x));
}
