using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Reviewable grip intervals from observed hand shape and imported rigid surfaces.
/// Geometry measures are heuristics, never backend confidence or object tracking.</summary>
public static class PropContactSuggestions
{
    sealed record Surface(string Prop,string Bone,int Index,ContactSurfaceIndex Search);
    public sealed record Result(List<ContactInterval> Contacts,int AmbiguousSamples,int UsableSamples);
    public static Result Suggest(MotionDocument motion,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        motion.Validate();
        if(!HandCaptureRetargeter.Supports(motion)||motion.Space!=MotionSpace.CameraRelative)
            throw new ArgumentException("Contact suggestions currently require camera-relative hand capture.");
        if(motion.ModelVersion.Contains("hand-forest-v2-parent-swing",StringComparison.Ordinal))
            throw new InvalidOperationException("Reprocess this older MediaPipe capture before suggesting contacts. The worker reuses cached observations and preserves the existing motion file.");
        var surfaces=motion.Objects.SelectMany(p=>p.Surfaces.GroupBy(s=>s.Bone).Select(g=>{
            var v=new List<float[]>();var t=new List<int>();foreach(var s in g){var offset=v.Count;v.AddRange(s.Vertices);t.AddRange(s.Triangles.Select(i=>i+offset));}
            return new Surface(p.Id,g.Key,p.Bones.FindIndex(b=>b.Name==g.Key),new(new(){Vertices=v.ToArray(),Triangles=t.ToArray()}));
        })).ToArray();
        if(surfaces.Length==0)throw new ArgumentException("No rigid contact surfaces were imported. Include a triangulated mesh with rigid bone weights in the prop FBX.");
        var tracks=new PropContactMotion(motion);var settings=new ContactSettings();var output=new List<ContactInterval>();
        var ambiguous=0;var usable=0;long budget=20_000_000;
        foreach(var left in new[]{true,false})
        {
            int Bone(string role)=>motion.Bones.FindIndex(b=>b.Role==Enum.Parse<BoneRole>(role+(left?"L":"R")));
            var hand=Bone("Hand");if(hand<0)continue;
            var fingers=new[]{"Index","Middle","Ring","Pinky"}.Select(n=>new[]{Bone(n+"Prox"),Bone(n+"Mid"),Bone(n+"Dist")}).Where(v=>v.All(i=>i>=0)).ToArray();
            if(fingers.Length<3)continue;
            var winners=new int[motion.Frames.Count];Array.Fill(winners,-1);var samples=new ContactSample[motion.Frames.Count];
            var world=new XForm[motion.Bones.Count];var valid=new bool[motion.Bones.Count];
            for(var f=0;f<motion.Frames.Count;f++)
            {
                cancellation.ThrowIfCancellationRequested();var frame=motion.Frames[f];
                if(frame.Evidence[hand]!=JointEvidence.Reconstructed)continue;
                for(var b=0;b<world.Length;b++)
                {var parent=motion.Bones[b].Parent;var local=new XForm(MotionDocument.V(frame.Positions[b]),MotionDocument.Q(frame.Rotations[b]));world[b]=parent<0?local:XForm.Compose(world[parent],local);
                    // A canonical metacarpal can bridge the observed wrist and finger.
                    // This is authored anatomy, never evidence of an observed finger.
                    var authoredMeta=frame.Evidence[b]==JointEvidence.Authored&&motion.Bones[b].Role is {} role&&role.ToString().Contains("Meta",StringComparison.Ordinal);
                    valid[b]=(frame.Evidence[b]==JointEvidence.Reconstructed||authoredMeta)&&(parent<0||valid[parent]);}
                if(!valid[hand])continue;
                var knuckles=Vector3.Zero;float curl=0;int observed=0;
                foreach(var finger in fingers)
                {
                    if(finger.Any(b=>!valid[b]||frame.Evidence[b]!=JointEvidence.Reconstructed))continue;
                    var a=world[finger[1]].Pos-world[finger[0]].Pos;var b=world[finger[2]].Pos-world[finger[1]].Pos;
                    if(a.LengthSquared()<1e-10f||b.LengthSquared()<1e-10f)continue;
                    curl+=(1-Math.Clamp(Vector3.Dot(Vector3.Normalize(a),Vector3.Normalize(b)),-1,1))*.5f;
                    knuckles+=world[finger[0]].Pos;observed++;
                }
                if(observed<3)continue;
                var palm=(world[hand].Pos+knuckles/observed)*.5f;curl/=observed;
                float best=settings.ExitDistance,second=float.PositiveInfinity;int winner=-1;Vector3 closest=default;XForm objectPose=default;
                var poses=new Dictionary<string,(XForm[] World,bool[] Valid)>();
                for(var s=0;s<surfaces.Length;s++)
                {
                    if(--budget<0)throw new InvalidOperationException("Contact search exceeded its work budget. Use a shorter clip or simpler prop geometry.");
                    var surface=surfaces[s];
                    if(!poses.TryGetValue(surface.Prop,out var pose))
                    {
                        if(!tracks.TrySample(surface.Prop,frame.Time,out var w,out var available))continue;
                        poses.Add(surface.Prop,pose=(w,available));
                    }
                    if(!pose.Valid[surface.Index])continue;
                    var obj=pose.World[surface.Index];var local=XForm.Compose(obj.Inverse(),new(palm,Quaternion.Identity)).Pos;
                    if(!surface.Search.TryClosest(local,settings.ExitDistance,ref budget,out var near))continue;
                    var distance=Vector3.Distance(local,near);
                    if(distance<best){if(winner>=0)second=best;best=distance;winner=s;closest=XForm.Compose(obj,new(near,Quaternion.Identity)).Pos;objectPose=obj;}
                    else second=Math.Min(second,distance);
                }
                if(winner<0)continue;
                // Two surfaces at almost the same distance are insufficient evidence
                // to choose an object or articulated part. Leave them for manual review.
                if(second-best<.005f){ambiguous++;continue;}
                winners[f]=winner;samples[f]=new(frame.Time,palm,objectPose,closest,curl,true,world[hand].Pos);usable++;
            }
            foreach(var index in winners.Where(i=>i>=0).Distinct())
            {
                cancellation.ThrowIfCancellationRequested();
                var stream=Enumerable.Range(0,winners.Length).Select(f=>winners[f]==index?samples[f]:new ContactSample(motion.Frames[f].Time,Vector3.Zero,XForm.Identity,Vector3.Zero,0,false)).ToArray();
                var surface=surfaces[index];var suggested=ContactSolver.Suggest(stream,motion.Bones[hand].Name,surface.Prop,settings);
                foreach(var contact in suggested)
                {
                    contact.ObjectBone=surface.Bone;
                    contact.Reason="Imported rigid surface proximity, observed finger bend, relative motion and persistence. Wrist anchor preserves the initial wrist offset. Uncertain heuristic; review against the video.";
                    // Existing reviewed or dismissed intervals are user decisions.
                    if(!motion.Contacts.Any(c=>c.Bone==contact.Bone&&c.End>=contact.Start&&c.Start<=contact.End))output.Add(contact);
                }
            }
        }
        return new(output.OrderBy(c=>c.Start).ThenBy(c=>c.Bone).ToList(),ambiguous,usable);
    }
}
