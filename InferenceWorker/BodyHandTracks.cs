using System.Numerics;
using HumanoidMocap.Inference;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Worker;

/// <summary>Finger motion for a third-person body capture. GVHMR reconstructs no fingers, so each
/// hand is cropped where the body's own 2D wrist and elbow put it and reconstructed with WiLoR;
/// only its finger articulation, relative to the wrist, is attached beneath the body's hand
/// bone. Wrist placement and orientation stay the body model's. Hands too small in the image, or
/// whose forearm was not confidently seen, are skipped and labelled unobserved rather than guessed.</summary>
public static class BodyHandTracks
{
    public const string Version="body-wilor-fingers-v6-smooth-edges";
    /// <summary>A forearm shorter than this many source pixels gives WiLoR too little hand to read.</summary>
    public const float MinimumForearmPixels=36;
    static readonly string[] Roles={"IndexProx","IndexMid","IndexDist","MiddleProx","MiddleMid","MiddleDist",
        "PinkyProx","PinkyMid","PinkyDist","RingProx","RingMid","RingDist","ThumbProx","ThumbMid","ThumbDist"};
    static readonly int[] Parents={-1,0,1,2,0,4,5,0,7,8,0,10,11,0,13,14};
    /// <param name="LocalRotations">Fifteen finger joints, xyzw, already reflected for a left hand.</param>
    /// <param name="RestOffsets">Fifteen parent-relative rest offsets in metres, already reflected for a left hand.</param>
    public sealed record Sample(float[] LocalRotations,float[] RestOffsets);

    /// <summary>Image box around a hand from COCO-17 body joints (x, y, score), or null when the forearm
    /// was not confidently seen, is too short, or the hand would lie mostly outside the picture.</summary>
    public static WildHandsCrop.Box? Region(float[] joints,bool left,int width,int height)
    {
        if(joints.Length!=51)throw new ArgumentException("Expected COCO-17 joints.");
        var w=left?9:10;var e=left?7:8;
        if(!(joints[w*3+2]>=.5f)||!(joints[e*3+2]>=.5f))return null;
        var wrist=new Vector2(joints[w*3],joints[w*3+1]);var elbow=new Vector2(joints[e*3],joints[e*3+1]);
        var forearm=Vector2.Distance(wrist,elbow);if(!(forearm>=MinimumForearmPixels))return null;
        // The hand continues the forearm; its knuckles sit roughly a third of a forearm past the wrist.
        var centre=wrist+(wrist-elbow)*.35f;var half=forearm*.5f;
        if(centre.X<0||centre.Y<0||centre.X>=width||centre.Y>=height)return null;
        return new(centre.X-half,centre.Y-half,centre.X+half,centre.Y+half);
    }
    public static Sample Reconstruct(WilorModel model,DecodedVideoFrame frame,WildHandsCrop.Box box,bool left,CancellationToken cancellation)
    {
        var hand=model.Run(WilorCrop.Prepare(frame,box,!left).Image,cancellation).Hand;var sign=left?-1:1;
        var rotations=new float[60];var offsets=new float[45];
        for(var j=1;j<16;j++)
        {
            var q=hand.LocalRotations[j];if(left)q=new(q.X,-q.Y,-q.Z,q.W); // S R S, S=diag(-1,1,1)
            rotations[(j-1)*4]=q.X;rotations[(j-1)*4+1]=q.Y;rotations[(j-1)*4+2]=q.Z;rotations[(j-1)*4+3]=q.W;
            var offset=hand.RestJoints[j]-hand.RestJoints[Parents[j]];
            offsets[(j-1)*3]=sign*offset.X;offsets[(j-1)*3+1]=offset.Y;offsets[(j-1)*3+2]=offset.Z;
        }
        return new(rotations,offsets);
    }
    /// <summary>Adds finger bones beneath HandL/HandR for each side seen in at least a tenth of the frames.
    /// Short losses glide into the reacquired pose and are labelled inferred; the rest stay unobserved.</summary>
    /// <returns>Observed samples per side, left then right.</returns>
    /// <summary>Consecutive frames a hand must be seen for its fingers to be used, about a third of a second at 25 fps.</summary>
    public const int MinimumRunFrames=8;
    static int RunLength(IReadOnlyList<Sample?[]> samples,int side,int t)
    {
        int start=t,end=t;while(start>0&&samples[start-1][side] is not null)start--;while(end+1<samples.Count&&samples[end+1][side] is not null)end++;
        return end-start+1;
    }
    public static int[] Append(MotionDocument document,IReadOnlyList<Sample?[]> samples)
    {
        if(samples.Count!=document.Frames.Count)throw new ArgumentException("Hand samples do not match the motion sample count.");
        var counts=new int[2];
        for(var side=0;side<2;side++)
        {
            var suffix=side==0?"L":"R";var wrist=document.Bones.FindIndex(b=>b.Role==(side==0?BoneRole.HandL:BoneRole.HandR));
            // Sightings shorter than MinimumRunFrames are dropped: a small hand seen in scattered moments gave
            // fingers that disagreed by 25-60 degrees from frame to frame on the kata sample. Longer runs are kept
            // and the gaps glide between them.
            var seen=Enumerable.Range(0,samples.Count).Where(t=>samples[t][side] is not null).ToArray();
            var observed=seen.Where(t=>RunLength(samples,side,t)>=MinimumRunFrames).ToArray();counts[side]=observed.Length;
            if(wrist<0||observed.Length<Math.Max(5,samples.Count/10))continue;
            var rest=new float[45];
            foreach(var t in observed)for(var i=0;i<45;i++)rest[i]+=samples[t][side]!.RestOffsets[i]/observed.Length;
            var first=document.Bones.Count;
            for(var j=0;j<15;j++)document.Bones.Add(new(){Name=Roles[j]+suffix,Role=Enum.Parse<BoneRole>(Roles[j]+suffix),Parent=Parents[j+1]==0?wrist:first+Parents[j+1]-1,Group="fingers",
                RestPosition=new[]{rest[j*3],rest[j*3+1],rest[j*3+2]}});
            for(var j=0;j<15;j++)
            {
                Quaternion At(int t){var r=samples[t][side]!.LocalRotations;return Quaternion.Normalize(new(r[j*4],r[j*4+1],r[j*4+2],r[j*4+3]));}
                // Reconstructed is the enum's zero value, so unseen frames must be marked explicitly.
                var track=new Quaternion[samples.Count];var evidence=Enumerable.Repeat(JointEvidence.Unobserved,samples.Count).ToArray();
                foreach(var t in observed){track[t]=At(t);evidence[t]=JointEvidence.Reconstructed;}
                // Single-frame flips go first, within each run of observations.
                for(var start=0;start<samples.Count;)
                {
                    if(evidence[start]!=JointEvidence.Reconstructed){start++;continue;}
                    var end=start;while(end<samples.Count&&evidence[end]==JointEvidence.Reconstructed)end++;
                    var raw=track[start..end];MocapSmooth.RemoveSpikes(raw);Array.Copy(raw,0,track,start,raw.Length);start=end;
                }
                for(var t=0;t<samples.Count;t++)
                {
                    if(evidence[t]==JointEvidence.Reconstructed)continue;
                    var before=observed.LastOrDefault(o=>o<t,-1);var after=observed.FirstOrDefault(o=>o>t,-1);
                    evidence[t]=JointEvidence.Unobserved;
                    if(before<0){track[t]=track[after];continue;}
                    if(after<0){track[t]=track[before];continue;}
                    // Hold, then glide into the reacquired pose over at most 1.5 s, as hand captures do.
                    var end=document.Frames[after].Time;var start=Math.Max(document.Frames[before].Time,end-1.5);
                    var amount=(float)Math.Clamp((document.Frames[t].Time-start)/(end-start),0,1);amount=amount*amount*(3-2*amount);
                    track[t]=Quaternion.Slerp(track[before],track[after],amount);evidence[t]=JointEvidence.InferredGap;
                }
                // Then zero-phase Butterworth at the First Person default (Rokoko strength 7, 3.5 Hz) across each
                // continuous stretch, glides included, so the noisy first frames after a loss are smoothed too.
                var rate=1/Math.Max(1e-3,(document.Frames[^1].Time-document.Frames[0].Time)/Math.Max(1,samples.Count-1));
                for(var start=0;start<samples.Count;)
                {
                    if(evidence[start]==JointEvidence.Unobserved){start++;continue;}
                    var end=start;while(end<samples.Count&&evidence[end]!=JointEvidence.Unobserved)end++;
                    if(end-start>=8&&rate>7.8)
                    {
                        var run=MocapSmooth.Quaternions(track[start..end],Math.Min(3.5,rate*.45),rate);
                        Array.Copy(run,0,track,start,run.Length);
                    }
                    start=end;
                }
                // Before the first and after the last sighting the hand holds its nearest filtered pose.
                var firstSeen=observed[0];var lastSeen=observed[^1];
                for(var t=0;t<firstSeen;t++)track[t]=track[firstSeen];
                for(var t=lastSeen+1;t<samples.Count;t++)track[t]=track[lastSeen];
                for(var t=0;t<samples.Count;t++)Extend(document.Frames[t],document.Bones[first+j].RestPosition,track[t],evidence[t]);
            }
        }
        document.Diagnostics.RemoveAll(d=>d.StartsWith("GVHMR provides no detailed finger capture",StringComparison.Ordinal));
        document.Diagnostics.Add(FormattableString.Invariant(
            $"Fingers: WiLoR reconstructed the left hand in {counts[0]} and the right in {counts[1]} of {samples.Count} frames, cropped where the body's 2D wrist and elbow placed it. Only finger articulation relative to the wrist is used; wrist placement and orientation remain GVHMR's. Hands under {MinimumForearmPixels:F0} px of forearm, or seen in under a tenth of the frames, are not reconstructed. No per-joint confidence."));
        document.Validate();return counts;
    }
    static void Extend(MotionFrame frame,float[] restPosition,Quaternion rotation,JointEvidence evidence)
    {
        frame.Positions=frame.Positions.Append((float[])restPosition.Clone()).ToArray();
        frame.Rotations=frame.Rotations.Append(MotionDocument.A(rotation)).ToArray();
        frame.Evidence=frame.Evidence.Append(evidence).ToArray();
        if(frame.Confidence is { } confidence)frame.Confidence=confidence.Append(null).ToArray();
    }
    /// <summary>A document rebuilt from a capture keeps its finger bones (the builder copies the whole
    /// source) but writes fresh notes; carry the finger note across and drop the claim that none exist.</summary>
    public static void CarryFingerNotes(MotionDocument source,MotionDocument destination)
    {
        // Notes about how the capture was made, which the editor status and the reader rely on.
        string[] kept={"Lens:","Vision transformers","High frame rate footage","Performer not in view","Footage cuts to another shot"};
        destination.Diagnostics.AddRange(source.Diagnostics.Where(d=>kept.Any(p=>d.StartsWith(p,StringComparison.Ordinal))&&!destination.Diagnostics.Contains(d)));
        var notes=source.Diagnostics.Where(d=>d.StartsWith("Fingers:",StringComparison.Ordinal)).ToArray();
        if(notes.Length==0||destination.Bones.Count<=22)return;
        for(var i=0;i<destination.Diagnostics.Count;i++)
            destination.Diagnostics[i]=destination.Diagnostics[i].Replace(" Detailed fingers and object motion are not captured."," Object motion is not captured.");
        destination.Diagnostics.AddRange(notes.Where(n=>!destination.Diagnostics.Contains(n)));
    }
}
