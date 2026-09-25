using HumanoidMocap.Inference;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;
using TorchSharp;
using static TorchSharp.torch;
using F=TorchSharp.torch.nn.functional;

namespace HumanoidMocap.Worker;

/// <summary>Moves a body capture's joints and root, a little, so that feet the contact tracks call planted stay put,
/// without changing what the motion says about its weight. Per-frame leg IK after retargeting pins each foot on
/// its own and can pop where a contact starts or ends; this adjusts the whole body over the whole clip at once.
/// An optimisation in LibTorch with automatic gradients, all terms in metres and seconds:
///  - planted heels and toes held at their position in the middle of each contact, weighted in and out at its ends;
///  - the root's velocity kept close to the original, so travel is not invented;
///  - the vertical ground-reaction forces the UnderPressure network reads from the motion kept close to those it read
///    from the original, so feet are not pinned by moves the body's weight would not allow;
///  - every joint rotation kept close to the original, and rotations kept unit length.</summary>
public static class FootContactCleanup
{
    public const string Version="foot-contact-cleanup-v1";
    const int Steps=100;const double LearningRate=1e-2,Rate=100;
    const double ContactWeight=1,VelocityWeight=.01,ForceWeight=.1,PoseWeight=1,UnitWeight=1e-3;
    /// <summary>Weight on the corrections' own acceleration (per frame squared): each frame's change is kept in step with its neighbours', so the clean-up adds no shake.</summary>
    const double SmoothWeight=100;
    const float MarginSeconds=.15f;
    static readonly BoneRole[] Roles={BoneRole.Hips,BoneRole.Spine0,BoneRole.Spine1,BoneRole.Spine2,BoneRole.Neck,BoneRole.Head,
        BoneRole.ClavicleR,BoneRole.UpperArmR,BoneRole.LowerArmR,BoneRole.HandR,BoneRole.ClavicleL,BoneRole.UpperArmL,BoneRole.LowerArmL,BoneRole.HandL,
        BoneRole.UpperLegR,BoneRole.LowerLegR,BoneRole.FootR,BoneRole.ToeR,BoneRole.UpperLegL,BoneRole.LowerLegL,BoneRole.FootL,BoneRole.ToeL};
    static readonly BoneRole[] Contacts={BoneRole.FootL,BoneRole.ToeL,BoneRole.FootR,BoneRole.ToeR};

    /// <returns>A note on what changed, or null when the capture had nothing to clean.</returns>
    public static string? Apply(MotionDocument document,string weightsPath,CancellationToken cancellation,Action<string>? progress=null)
    {
        var bones=document.Bones;var frames=document.Frames;var count=frames.Count;
        int Role(BoneRole role)=>bones.FindIndex(b=>b.Role==role);
        var mapped=Roles.Select(Role).ToArray();if(mapped.Any(i=>i<0)||count<8)return null;
        var contactBones=Contacts.Select(Role).ToArray();
        var tracks=contactBones.Select(b=>document.StationaryJoints.FirstOrDefault(t=>t.Bone==bones[b].Name)?.Probability).ToArray();
        if(tracks.Any(t=>t is null||t.Length!=count))return null;
        // The bones that move the network's joints and the contacts: those joints and their ancestors.
        var needed=new SortedSet<int>();
        foreach(var start in mapped.Concat(contactBones))for(var b=start;b>=0;b=bones[b].Parent)needed.Add(b);
        var order=needed.ToArray();var root=order.First(b=>bones[b].Parent<0);
        if(order.Any(b=>bones[b].Parent>=0&&!needed.Contains(bones[b].Parent)))return null;
        using var scope=NewDisposeScope();
        Tensor Series(Func<MotionFrame,float[]> pick,int width)=>tensor(frames.SelectMany(pick).ToArray()).reshape(count,width);
        var offsets=order.ToDictionary(b=>b,b=>Series(f=>f.Positions[b],3));
        var initial=order.ToDictionary(b=>b,b=>Series(f=>f.Rotations[b],4));
        var rotations=order.ToDictionary(b=>b,b=>new TorchSharp.Modules.Parameter(initial[b].clone(),true));
        var rootInitial=offsets[root];var rootPosition=new TorchSharp.Modules.Parameter(rootInitial.clone(),true);
        Dictionary<int,(Tensor Rotation,Tensor Position)> Forward()
        {
            var world=new Dictionary<int,(Tensor Rotation,Tensor Position)>();
            foreach(var b in order)
            {
                var parent=bones[b].Parent;
                if(parent<0){world[b]=(rotations[b],rootPosition);continue;}
                var (pr,pp)=world[parent];world[b]=(Multiply(pr,rotations[b]),pp+Rotate(pr,offsets[b]));
            }
            return world;
        }
        // The network's input: its 23 joints, Z up, resampled to 100 Hz, lifted so the feet's floor is 5 cm under the ankles.
        var startTime=frames[0].Time;var duration=frames[^1].Time-startTime;var samples=Math.Max(2,(int)Math.Floor(duration*Rate)+1);
        var lower=new long[samples];var upper=new long[samples];var blend=new float[samples];var source=0;
        for(var s=0;s<samples;s++)
        {
            var t=startTime+s/Rate;while(source+1<count-1&&frames[source+1].Time<t)source++;
            var a=frames[source].Time;var b=frames[Math.Min(count-1,source+1)].Time;
            lower[s]=source;upper[s]=Math.Min(count-1,source+1);blend[s]=(float)(b>a?Math.Clamp((t-a)/(b-a),0,1):0);
        }
        var lowerIndex=tensor(lower);var upperIndex=tensor(upper);var blendWeights=tensor(blend).reshape(samples,1,1);
        var zUp=tensor(new float[]{1,0,0,0,0,1,0,-1,0}).reshape(3,3); // (x,y,z) -> (x,-z,y) as a row-vector product
        Tensor NetworkInput(Dictionary<int,(Tensor Rotation,Tensor Position)> world,float lift)
        {
            Tensor At(int i)=>world[mapped[i]].Position;
            var joints=new List<Tensor>{At(0),At(1),At(2),(At(2)+At(3))*.5f,At(3),At(4),At(5)};
            for(var j=0;j<16;j++)joints.Add(At(6+j));
            var p=stack(joints,1).matmul(zUp);                                  // [frames,23,3]
            var resampled=p.index_select(0,lowerIndex)*(1-blendWeights)+p.index_select(0,upperIndex)*blendWeights;
            resampled=resampled+tensor(new float[]{0,0,lift});
            return resampled.reshape(samples,69).transpose(0,1).unsqueeze(0);  // [1,69,samples]
        }
        var net=new ForceNetwork(weightsPath);
        using(no_grad())
        {
            var world0=Forward();
            var ankles=world0[mapped[16]].Position.select(1,1).minimum(world0[mapped[20]].Position.select(1,1));
            var sortedAnkles=ankles.sort().Values.data<float>().ToArray();var lift=.05f-sortedAnkles[sortedAnkles.Length/20];
            var targetForces=net.Forces(NetworkInput(world0,lift)).detach().MoveToOuterDisposeScope();
            // Contact targets: each planted run holds its joint where it is at the run's middle.
            var margin=Math.Max(1,(int)Math.Round(MarginSeconds*count/Math.Max(1e-3,duration)));
            var goals=new List<(int Bone,Tensor Frames,Tensor Target,Tensor Weight)>();
            for(var c=0;c<4;c++)
            {
                var track=tracks[c]!;var bone=contactBones[c];var positions=world0[bone].Position;
                for(var f=0;f<count;)
                {
                    if(track[f]<.5f){f++;continue;}
                    var end=f;while(end+1<count&&track[end+1]>=.5f)end++;
                    var length=end-f+1;
                    if(length>=3)
                    {
                        var weights=Enumerable.Range(0,length).Select(k=>{var edge=Math.Min(k+1,length-k)/(float)(margin+1);var w=Math.Clamp(edge,0,1);return w*w*(3-2*w);}).ToArray();
                        goals.Add((bone,tensor(Enumerable.Range(f,length).Select(i=>(long)i).ToArray()),positions[(f+end)/2].clone().MoveToOuterDisposeScope(),tensor(weights).MoveToOuterDisposeScope()));
                    }
                    f=end+1;
                }
            }
            if(goals.Count==0)return null;
            var initialVelocity=(rootInitial[1..]-rootInitial[..^1])*(float)(count/Math.Max(1e-3,duration));
            var optimizer=torch.optim.Adam(rotations.Values.Append(rootPosition),LearningRate);
            double first=0,last=0;
            using(enable_grad())
            {
                for(var step=0;step<Steps;step++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var stepScope=NewDisposeScope();
                    optimizer.zero_grad();
                    var world=Forward();
                    var contact=zeros(1);
                    foreach(var (bone,indices,target,weight) in goals)
                        contact=contact+((world[bone].Position.index_select(0,indices)-target).square().sum(1)*weight).mean();
                    contact=contact/goals.Count;
                    var velocity=(rootPosition[1..]-rootPosition[..^1])*(float)(count/Math.Max(1e-3,duration));
                    var travel=(velocity-initialVelocity).square().sum(1).mean();
                    var forces=net.Forces(NetworkInput(world,lift));
                    var force=((forces+1).log()-(targetForces+1).log()).square().mean();
                    var pose=rotations.Keys.Select(b=>(rotations[b]-initial[b]).square().sum(1).mean()).Aggregate((a,b)=>a+b)/rotations.Count;
                    var unit=rotations.Values.Select(q=>(q.norm(1)-1).square().mean()).Aggregate((a,b)=>a+b)/rotations.Count;
                    // Corrections change smoothly from frame to frame.
                    Tensor Bend(Tensor change)=>(change[2..]-change[1..^1]*2+change[..^2]).square().sum(1).mean();
                    var smooth=rotations.Keys.Select(b=>Bend(rotations[b]-initial[b])).Aggregate((a,b)=>a+b)/rotations.Count+Bend(rootPosition-rootInitial);
                    var loss=contact*ContactWeight+travel*VelocityWeight+force*ForceWeight+pose*PoseWeight+unit*UnitWeight+smooth*SmoothWeight;
                    loss.backward();optimizer.step();
                    var value=loss.item<float>();if(step==0)first=value;last=value;
                    if(step%25==0)progress?.Invoke($"Cleaning foot contacts {step*100/Steps}%");
                }
            }
            // Written back: unit rotations of the optimised bones and the root's path.
            var rootValues=rootPosition.data<float>().ToArray();
            var written=order.ToDictionary(b=>b,b=>(rotations[b]/rotations[b].norm(1,true)).data<float>().ToArray());
            for(var f=0;f<count;f++)
            {
                frames[f].Positions[root]=new[]{rootValues[f*3],rootValues[f*3+1],rootValues[f*3+2]};
                foreach(var b in order)frames[f].Rotations[b]=new[]{written[b][f*4],written[b][f*4+1],written[b][f*4+2],written[b][f*4+3]};
            }
            return FormattableString.Invariant($"Foot contacts cleaned over the whole clip: {goals.Count} planted heel and toe runs held in place while keeping the body's travel and the weight UnderPressure reads from it (fit error {first:G3} to {last:G3}).");
        }
    }

    static Tensor ELU(Tensor x)=>F.elu(x,1.0,false);
    static Tensor Multiply(Tensor a,Tensor b)
    {
        var (ax,ay,az,aw)=(a.select(1,0),a.select(1,1),a.select(1,2),a.select(1,3));var (bx,by,bz,bw)=(b.select(1,0),b.select(1,1),b.select(1,2),b.select(1,3));
        return stack(new[]{aw*bx+ax*bw+ay*bz-az*by,aw*by-ax*bz+ay*bw+az*bx,aw*bz+ax*by-ay*bx+az*bw,aw*bw-ax*bx-ay*by-az*bz},1);
    }
    static Tensor Rotate(Tensor q,Tensor v)
    {
        var u=q.narrow(1,0,3);var w=q.narrow(1,3,1);
        return v+torch.linalg.cross(u,torch.linalg.cross(u,v)+w*v)*2;
    }

    /// <summary>UnderPressure's network (see <see cref="UnderPressureContacts"/>) in LibTorch, so gradients flow through it.</summary>
    sealed class ForceNetwork
    {
        readonly Tensor[] convWeight=new Tensor[4],convBias=new Tensor[4];readonly Tensor fc1W,fc1B,fc2W,fc2B,outW;
        public ForceNetwork(string path)
        {
            using var file=new TorchCheckpoint(path,"model");
            Tensor Load(string name)=>tensor(file.ReadFloat(name)).reshape(file.Tensors[name].Shape.Select(v=>(long)v).ToArray()).MoveToOuterDisposeScope();
            var layers=new[]{3,6,9,12};for(var i=0;i<4;i++){convWeight[i]=Load($"{layers[i]}.weight");convBias[i]=Load($"{layers[i]}.bias");}
            fc1W=Load("16.weight");fc1B=Load("16.bias");fc2W=Load("19.weight");fc2B=Load("19.bias");outW=Load("22.weight");
        }
        /// <summary>[1,69,samples] joints in, [samples,32] cell forces out.</summary>
        public Tensor Forces(Tensor x)
        {
            for(var i=0;i<4;i++)x=ELU(F.conv1d(F.pad(x,new long[]{3,3},PaddingModes.Replicate),convWeight[i],convBias[i]));
            var h=x.squeeze(0).transpose(0,1);
            h=ELU(F.linear(h,fc1W,fc1B));h=ELU(F.linear(h,fc2W,fc2B));
            return F.softplus(F.linear(h,outW));
        }
    }
}
