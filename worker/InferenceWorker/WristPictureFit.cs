using System.Numerics;
using System.Text.Json;
using HumanoidMocap.Core.Inference;
using HumanoidMocap.EditorTools.Inference;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Motion;

namespace HumanoidMocap.Worker;

/// <summary>Puts each wrist where the picture shows it. The body network's arms are smooth and plausible but do not
/// always follow the wrists the 2D pose sees: on a dance clip the performer crossed the wrists above the head and the
/// capture left them uncrossed, 34 px (about 17 cm) off; on the "Take the L" emote the hand held to the forehead stood
/// 34 cm in front of the head, because from one camera a hand on the forehead and a hand reaching toward the lens look
/// the same. Per frame, a confidently seen wrist more than <see cref="TolerancePicture"/> of the body's height off
/// its 2D position is moved across the picture, at its own distance from the camera, until it is only that far off
/// (at most <see cref="MaximumMetres"/>); a wrist the picture shows above the eyes and within the head's width is
/// then slid along its line of sight to within <see cref="FrontMetres"/> in front of the head (or
/// <see cref="BehindMetres"/> behind it). A boxing guard held before the chin keeps its reach. A wrist the picture
/// places inside the torso's outline but can barely see (score under <see cref="HiddenScore"/>, its elbow well seen) is
/// hidden by the body (unless the network put it clearly in front of the torso: a blurred hand swinging before the belly),
/// so it is slid along its line of sight to behind the torso's back at its height: in a floss dance
/// the hand swinging behind the hips stood at the hip's side, level with the body. The moves are smoothed over time and
/// the arm follows by two-bone IK; the hand keeps its orientation.</summary>
public static class WristPictureFit
{
    public const string File="wrist-picture-fit.json";
    /// <summary>A wrist inside the torso's outline seen less surely than this is behind the body (seen wrists score about .9;
    /// hidden ones .55–.7 on the floss clip, while a hand crossing in front kept .9).</summary>
    public const float HiddenScore=.75f,HiddenMinimumScore=.2f,HiddenElbowScore=.7f,BehindTorsoMargin=.05f;
    /// <summary>A hidden stretch counts as behind the body even where the network put the hand in front when the wrist moves slower than
    /// this (body heights per second; a blurred swing in front moved 2.4-3.7, the floss hand behind the hip about 1) and the performer is
    /// at least <see cref="HiddenBodyPixels"/> tall, enough for a vanishing wrist to mean something.</summary>
    public const float HiddenSlowSpeed=1.2f,HiddenBodyPixels=200,MaximumBehindMetres=.5f;
    public const float FrontMetres=.15f,BehindMetres=.1f,TolerancePicture=.015f,TogetherPicture=.15f,PairPicture=.12f,PairMetres=.2f,PairScore=.4f,MaximumMetres=.25f,MaximumMeasuredMetres=.6f,MinimumScore=.6f;
    /// <summary>The corrected local rotations of one arm (upper arm, forearm, hand) in one frame.</summary>
    public sealed record Correction(int Frame,bool Left,float[][] Rotations);

    /// <param name="observations">Per frame, COCO 2D joints (x, y, score) in the picture.</param>
    /// <returns>The corrections made, already applied to <paramref name="motion"/> (camera-relative).</returns>
    /// <param name="measured">Per side (left, right) and frame, the wrist's distance from the camera measured by a depth
    /// sensor (see <see cref="DepthCorrection"/>), or null; a measurement replaces the head, paired-hands and torso rules.</param>
    public static List<Correction> Apply(MotionDocument motion,IReadOnlyList<float[]?> observations,GvhmrDecoder.Camera camera,float?[][]? measured=null)
    {
        var result=new List<Correction>();var bones=motion.Bones;var count=motion.Frames.Count;
        int Role(BoneRole role)=>bones.FindIndex(b=>b.Role==role);
        var head=Role(BoneRole.Head);if(head<0||observations.Count!=count||count==0)return result;
        // The body's height in the picture (nose to lower ankle), its median over the clip, sets the tolerance.
        var heights=observations.Where(o=>o is {Length:>=51}&&o[2]>.5f&&(o[15*3+2]>.5f||o[16*3+2]>.5f))
            .Select(o=>Math.Max(o![15*3+2]>.5f?o[15*3+1]:0,o[16*3+2]>.5f?o[16*3+1]:0)-o[1]).Where(h=>h>0).OrderBy(h=>h).ToArray();
        if(heights.Length==0)return result;
        var bodyHeight=heights[heights.Length/2];var tolerance=TolerancePicture*bodyHeight;
        // Wrists the picture shows together (arms folded across the chest seen from the side) are brought to within
        // PairMetres of each other's distance from the camera, half each: the network put one 60 cm behind the other.
        // Only their being together is used, so a less certain sighting (PairScore) counts.
        int handL=Role(BoneRole.HandL),handR=Role(BoneRole.HandR);
        int hips=Role(BoneRole.Hips),neck=Role(BoneRole.Neck),shoulderL=Role(BoneRole.UpperArmL),shoulderR=Role(BoneRole.UpperArmR);var pairShift=new[]{new float[count],new float[count]};
        if(handL>=0&&handR>=0)for(var t=0;t<count;t++)
        {
            var o=observations[t];if(o is null||o.Length<51||o[9*3+2]<PairScore||o[10*3+2]<PairScore)continue;
            if(MathF.Sqrt((o[9*3]-o[10*3])*(o[9*3]-o[10*3])+(o[9*3+1]-o[10*3+1])*(o[9*3+1]-o[10*3+1]))>PairPicture*bodyHeight)continue;
            var (positions,_)=World(motion,t);var apart=-positions[handR].Z-(-positions[handL].Z);
            var excess=MathF.Abs(apart)-PairMetres;if(excess<=0)continue;
            pairShift[0][t]=MathF.Sign(apart)*excess/2;pairShift[1][t]=-MathF.Sign(apart)*excess/2;
        }
        // Held two frames past each end, so the smoothing below does not thin it where the hands first come together.
        foreach(var shift in pairShift)
        {
            var grown=(float[])shift.Clone();
            for(var t=0;t<count;t++)if(shift[t]==0)
                for(var k=1;k<=2;k++){if(t-k>=0&&shift[t-k]!=0){grown[t]=shift[t-k];break;}if(t+k<count&&shift[t+k]!=0){grown[t]=shift[t+k];break;}}
            Array.Copy(grown,shift,count);
        }
        foreach(var left in new[]{true,false})
        {
            int upper=Role(left?BoneRole.UpperArmL:BoneRole.UpperArmR),lower=Role(left?BoneRole.LowerArmL:BoneRole.LowerArmR),hand=Role(left?BoneRole.HandL:BoneRole.HandR);
            if(upper<0||lower<0||hand<0||bones[lower].Parent!=upper||bones[hand].Parent!=lower)continue;
            var wristJoint=left?9:10;var elbowJoint=left?7:8;
            // Frames where the picture hides this wrist behind the torso, held two frames past each end so the smoothing
            // below does not thin a short pass behind the hips.
            var hidden=new bool[count];
            for(var t=0;t<count;t++)
            {
                var o=observations[t];if(o is null||o.Length<51)continue;
                var score=o[wristJoint*3+2];
                hidden[t]=score>=HiddenMinimumScore&&score<HiddenScore&&o[elbowJoint*3+2]>=HiddenElbowScore&&InsideTorsoOutline(o,wristJoint);
            }
            // Each hidden stretch that is slow (see HiddenSlowSpeed) is trusted over the network's depth.
            var trusted=new bool[count];
            for(var t=0;t<count;)
            {
                if(!hidden[t]){t++;continue;}
                var end=t;while(end+1<count&&hidden[end+1])end++;
                var speeds=new List<float>();
                for(var k=t;k<=end;k++)
                {
                    int a=Math.Max(0,k-1),b=Math.Min(count-1,k+1);var span=motion.Frames[b].Time-motion.Frames[a].Time;
                    if(observations[a] is not {Length:>=51} oa||observations[b] is not {Length:>=51} ob||span<=0)continue;
                    speeds.Add(MathF.Sqrt((ob[wristJoint*3]-oa[wristJoint*3])*(ob[wristJoint*3]-oa[wristJoint*3])+(ob[wristJoint*3+1]-oa[wristJoint*3+1])*(ob[wristJoint*3+1]-oa[wristJoint*3+1]))/bodyHeight/(float)span);
                }
                speeds.Sort();
                if(bodyHeight>=HiddenBodyPixels&&speeds.Count>0&&speeds[speeds.Count/2]<HiddenSlowSpeed)for(var k=t;k<=end;k++)trusted[k]=true;
                t=end+1;
            }
            var behind=(bool[])hidden.Clone();var sure=(bool[])trusted.Clone();
            for(var t=0;t<count;t++)for(var k=-2;k<=2;k++)if(t+k>=0&&t+k<count){if(hidden[t])behind[t+k]=true;if(trusted[t])sure[t+k]=true;}
            // Where each wrist should go, as a move from where the network put it.
            var move=new Vector3[count];
            for(var t=0;t<count;t++)
            {
                var o=observations[t];if(o is null||o.Length<51)continue;
                float Score(int k)=>o[k*3+2];
                var seen=Score(wristJoint)>=MinimumScore;
                if(!seen&&!behind[t])continue;
                var (positions,_)=World(motion,t);var wrist=positions[hand];
                // Camera-relative documents are the camera's axes turned half a turn about x: depth is -z.
                var z=-wrist.Z;if(z<.05f)continue;
                float u=camera.CenterX+camera.FocalLength*wrist.X/z,v=camera.CenterY-camera.FocalLength*wrist.Y/z;
                float ou=o[wristJoint*3],ov=o[wristJoint*3+1];var error=MathF.Sqrt((ou-u)*(ou-u)+(ov-v)*(ov-v));
                var goal=wrist;
                // Wrists close together in the picture (crossed, clasped) keep less slack: their order is what shows.
                var slack=tolerance;var otherJoint=left?10:9;
                if(Score(otherJoint)>=MinimumScore)
                {
                    var apart=MathF.Sqrt((o[otherJoint*3]-ou)*(o[otherJoint*3]-ou)+(o[otherJoint*3+1]-ov)*(o[otherJoint*3+1]-ov));
                    slack*=Math.Clamp(apart/(TogetherPicture*bodyHeight),.25f,1);
                }
                var pushedBehind=false;
                if(seen&&error>slack)
                {
                    var keep=slack/error;var tu=ou+(u-ou)*keep;var tv=ov+(v-ov)*keep;
                    goal=new Vector3((tu-camera.CenterX)/camera.FocalLength*z,-(tv-camera.CenterY)/camera.FocalLength*z,-z);
                }
                var target=measured?[left?0:1][t];
                if(target is float distance&&distance>.05f)goal*=distance/-goal.Z;
                else
                {
                    if(Score(1)>=.5f&&Score(2)>=.5f)
                    {
                        var eyeY=Math.Min(o[1*3+1],o[2*3+1]);var eyeX=(o[1*3]+o[2*3])/2;
                        var width=Score(3)>.3f&&Score(4)>.3f?MathF.Abs(o[3*3]-o[4*3]):2.5f*MathF.Abs(o[1*3]-o[2*3]);
                        if(width>=3&&ov<eyeY&&MathF.Abs(ou-eyeX)<1.2f*width&&ov>eyeY-2.5f*width)
                        {
                            var depth=-goal.Z-(-positions[head].Z);var shift=Math.Clamp(depth,-FrontMetres,BehindMetres)-depth;
                            goal*=(-goal.Z+shift)/-goal.Z;
                        }
                    }
                    if(pairShift[left?0:1][t] is var pair&&pair!=0)goal*=(-goal.Z+pair)/-goal.Z;
                    // Hidden by the body: behind the torso's back at the hand's height. Only where the network did not put the hand
                    // clearly in front of the torso: a hand swinging before the belly, blurred, is also hard to see (Orange Justice:
                    // 21-41 cm in front, scored .66-.73), while the floss hand the body hid stood level with it. A slow hidden stretch on a
                    // large enough performer is trusted even so: the floss's other hand went behind the hip with the network 25 cm in front.
                    if(behind[t]&&TorsoDepths(positions,hips,neck,shoulderL,shoulderR,goal) is var (front,back)&&(-wrist.Z>=front||sure[t])&&-goal.Z<back){goal*=back/-goal.Z;pushedBehind=true;}
                    // Never deeper into the torso than the network had it: pulled toward the other hand or the head, a
                    // hand went 5 cm into the chest. It slides along its line of sight, so the picture does not change.
                    if(Torso(positions,hips,neck,shoulderL,shoulderR) is { } torso)
                    {
                        var need=Math.Min(1,torso.Clearance(wrist));
                        if(torso.Clearance(goal)<need)
                        {
                            Vector3? best=null;
                            for(var k=1;k<=80&&best is null;k++)foreach(var scale in new[]{1+k*.005f,1-k*.005f})
                                if(best is null&&torso.Clearance(goal*scale)>=need)best=goal*scale;
                            goal=best??wrist;
                        }
                    }
                }
                // A hand passing behind the body may have to cross the whole torso (the floss hand the network put 25 cm in front).
                var limit=target is not null?MaximumMeasuredMetres:pushedBehind?MaximumBehindMetres:MaximumMetres;
                var change=goal-wrist;if(change.Length()>limit)change=Vector3.Normalize(change)*limit;
                move[t]=change;
            }
            // Smoothed so the arm does not snap, and so a single doubtful 2D frame barely moves it.
            var smooth=new Vector3[count];const float sigma=1.3f;
            for(var t=0;t<count;t++)
            {
                var sum=Vector3.Zero;float weights=0;
                for(var k=-6;k<=6;k++){var i=Math.Clamp(t+k,0,count-1);var w=MathF.Exp(-k*k/(2*sigma*sigma));sum+=move[i]*w;weights+=w;}
                smooth[t]=sum/weights;
            }
            for(var t=0;t<count;t++)
            {
                if(smooth[t].Length()<.005f)continue;
                var (positions,rotations)=World(motion,t);
                var parent=bones[upper].Parent;var parentWorld=parent<0?Quaternion.Identity:rotations[parent];
                Vector3 s=positions[upper],e=positions[lower],w=positions[hand];
                var goal=w+smooth[t];
                float a=Vector3.Distance(s,e),b=Vector3.Distance(e,w);if(a<1e-4f||b<1e-4f)continue;
                var toGoal=goal-s;var d=Math.Clamp(toGoal.Length(),MathF.Abs(a-b)+1e-4f,a+b-1e-4f);var direction=Vector3.Normalize(toGoal);
                // The elbow stays on the side it was on.
                var pole=e-s;pole-=direction*Vector3.Dot(pole,direction);
                if(pole.LengthSquared()<1e-10f)continue;
                pole=Vector3.Normalize(pole);
                var along=(a*a-b*b+d*d)/(2*d);var elbow=s+direction*along+pole*MathF.Sqrt(Math.Max(0,a*a-along*along));var wrist=s+direction*d;
                var upperWorld=Quaternion.Normalize(Between(e-s,elbow-s)*rotations[upper]);
                var turnedLower=Quaternion.Normalize(Between(e-s,elbow-s)*rotations[lower]);
                var lowerWorld=Quaternion.Normalize(Between(Vector3.Transform(w-e,Between(e-s,elbow-s)),wrist-elbow)*turnedLower);
                var locals=new[]{Quaternion.Normalize(Quaternion.Inverse(parentWorld)*upperWorld),Quaternion.Normalize(Quaternion.Inverse(upperWorld)*lowerWorld),
                    Quaternion.Normalize(Quaternion.Inverse(lowerWorld)*rotations[hand])};
                var correction=new Correction(t,left,locals.Select(q=>new[]{q.X,q.Y,q.Z,q.W}).ToArray());
                Replay(motion,new[]{correction});result.Add(correction);
            }
        }
        return result;
    }
    /// <summary>Applies saved corrections to another document built on the same skeleton (the world-relative
    /// refinement rebuilds the arms from the network's output and would otherwise lose them).</summary>
    public static int Replay(MotionDocument motion,IEnumerable<Correction> corrections)
    {
        var bones=motion.Bones;int Role(BoneRole role)=>bones.FindIndex(b=>b.Role==role);var applied=0;
        foreach(var c in corrections)
        {
            if(c.Frame<0||c.Frame>=motion.Frames.Count)continue;
            var chain=c.Left?new[]{BoneRole.UpperArmL,BoneRole.LowerArmL,BoneRole.HandL}:new[]{BoneRole.UpperArmR,BoneRole.LowerArmR,BoneRole.HandR};
            var indices=chain.Select(Role).ToArray();if(indices.Any(i=>i<0))continue;
            for(var k=0;k<3;k++)motion.Frames[c.Frame].Rotations[indices[k]]=c.Rotations[k].ToArray();
            applied++;
        }
        return applied;
    }
    public static void Save(string folder,List<Correction> corrections)
    {
        var path=Path.Combine(folder,File);
        if(corrections.Count==0){System.IO.File.Delete(path);return;}
        System.IO.File.WriteAllText(path,JsonSerializer.Serialize(corrections));
    }
    public static List<Correction> Load(string folder)
    {
        var path=Path.Combine(folder,File);
        return System.IO.File.Exists(path)?JsonSerializer.Deserialize<List<Correction>>(System.IO.File.ReadAllText(path))??new():new();
    }
    /// <summary>The torso as an upright oval tube from the hips to the neck, sized from the shoulders; Clearance is 1
    /// on its surface (widened by a forearm's thickness), below 1 inside and above 1 outside.</summary>
    sealed record TorsoTube(Vector3 Bottom,Vector3 Up,float Height,Vector3 Across,Vector3 Forward,float Width,float Depth)
    {
        public float Clearance(Vector3 p)
        {
            var v=p-Bottom;var along=Vector3.Dot(v,Up)/Height;if(along<.05f||along>.95f)return float.PositiveInfinity;
            float x=Vector3.Dot(v,Across)/Width,z=Vector3.Dot(v,Forward)/Depth;return MathF.Sqrt(x*x+z*z);
        }
    }
    static TorsoTube? Torso(Vector3[] p,int hips,int neck,int shoulderL,int shoulderR)
    {
        if(hips<0||neck<0||shoulderL<0||shoulderR<0)return null;
        var axis=p[neck]-p[hips];var height=axis.Length();if(height<1e-4f)return null;var up=axis/height;
        var across=p[shoulderL]-p[shoulderR];var span=across.Length()/2;across-=up*Vector3.Dot(across,up);if(across.LengthSquared()<1e-8f||span<1e-4f)return null;
        across=Vector3.Normalize(across);
        return new(p[hips],up,height,across,Vector3.Cross(across,up),(.7f+.18f)*span,(.55f+.18f)*span);
    }
    /// <summary>Whether 2D joint <paramref name="k"/> lies inside the shoulders-and-hips outline (all four seen).</summary>
    static bool InsideTorsoOutline(float[] o,int k)
    {
        int[] corners={5,6,12,11};
        if(corners.Any(c=>o[c*3+2]<.5f))return false;
        float x=o[k*3],y=o[k*3+1];var inside=false;
        for(var a=0;a<4;a++)
        {
            float x1=o[corners[a]*3],y1=o[corners[a]*3+1],x2=o[corners[(a+1)%4]*3],y2=o[corners[(a+1)%4]*3+1];
            if((y1>y)!=(y2>y)&&x<(x2-x1)*(y-y1)/(y2-y1)+x1)inside=!inside;
        }
        return inside;
    }
    /// <summary>Distances from the camera just in front of and just behind the torso at the height of <paramref name="p"/>: the
    /// torso's middle there (between hips and neck), less or plus its half-depth (from the shoulders) and <see cref="BehindTorsoMargin"/>.
    /// Both infinite when the joints are missing, so no hand counts as behind.</summary>
    static (float Front,float Back) TorsoDepths(Vector3[] positions,int hips,int neck,int shoulderL,int shoulderR,Vector3 p)
    {
        if(hips<0||neck<0||shoulderL<0||shoulderR<0)return(float.PositiveInfinity,float.PositiveInfinity);
        var low=positions[hips];var high=positions[neck];if(MathF.Abs(high.Y-low.Y)<1e-3f)return(float.PositiveInfinity,float.PositiveInfinity);
        var f=Math.Clamp((p.Y-low.Y)/(high.Y-low.Y),0,1);var middle=-Vector3.Lerp(low,high,f).Z;
        var halfDepth=.55f*Vector3.Distance(positions[shoulderL],positions[shoulderR])/2;
        return(middle-halfDepth-BehindTorsoMargin,middle+halfDepth+BehindTorsoMargin);
    }
    static (Vector3[] Positions,Quaternion[] Rotations) World(MotionDocument motion,int t)
    {
        var bones=motion.Bones;var frame=motion.Frames[t];var p=new Vector3[bones.Count];var q=new Quaternion[bones.Count];
        for(var i=0;i<bones.Count;i++)
        {
            var parent=bones[i].Parent;var local=Quaternion.Normalize(MotionDocument.Q(frame.Rotations[i]));var offset=MotionDocument.V(frame.Positions[i]);
            if(parent<0){p[i]=offset;q[i]=local;}else{p[i]=p[parent]+Vector3.Transform(offset,q[parent]);q[i]=Quaternion.Normalize(q[parent]*local);}
        }
        return (p,q);
    }
    static Quaternion Between(Vector3 from,Vector3 to)
    {
        from=Vector3.Normalize(from);to=Vector3.Normalize(to);var dot=Vector3.Dot(from,to);
        if(dot>.999999f)return Quaternion.Identity;
        if(dot<-.999999f){var axis=Vector3.Cross(Vector3.UnitX,from);if(axis.LengthSquared()<1e-6f)axis=Vector3.Cross(Vector3.UnitY,from);return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),MathF.PI);}
        var c=Vector3.Cross(from,to);return Quaternion.Normalize(new Quaternion(c.X,c.Y,c.Z,1+dot));
    }
}
