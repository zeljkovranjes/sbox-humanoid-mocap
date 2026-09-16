// Architecture adapted from gmntu/mobilehand, commit
// 51c112364013b803c38955b55a1572b0d402894c (Lim et al., ICONIP 2020).
using System.Security.Cryptography;
using HumanoidMocap.Inference;
using TorchSharp;
using static TorchSharp.torch;
using F=TorchSharp.torch.nn.functional;

namespace HumanoidMocap.Worker;

/// <summary>MobileNetV3-Small and the released 39-parameter iterative hand head.
/// No world tracking or occlusion reconstruction is implied by this image model.</summary>
public sealed class MobileHandModel : IDisposable
{
    public const string CheckpointSha256="8587d8aae909c77fa07f382f6648eae4e366b3b4755aa993ca7711bfb35904cf";
    readonly HandModelWeights weights;
    readonly float[] angleBasis;
    int running;bool disposed;
    public sealed record Prediction(float[] RotationMatrices,float[] Shape,float[] WeakCamera,float[] Angles,float[] Parameters);

    public static ManoDecoder ReadDecoder(string checkpoint,CancellationToken cancellation=default)
    {
        using(var input=File.OpenRead(checkpoint))
            if(!Convert.ToHexString(SHA256.HashData(input)).Equals(CheckpointSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MobileHand checkpoint checksum mismatch.");
        var temporary=Path.Combine(Path.GetTempPath(),"hm-mobilehand-"+Guid.NewGuid().ToString("N")+".zip");
        try
        {
            TorchCheckpoint.ConvertLegacy(checkpoint,temporary,cancellation);
            using var parsed=new TorchCheckpoint(temporary);return new ManoDecoder(parsed,"mano.",true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    public MobileHandModel(string checkpoint,CancellationToken cancellation=default)
    {
        using(var input=File.OpenRead(checkpoint))
            if(!Convert.ToHexString(SHA256.HashData(input)).Equals(CheckpointSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MobileHand checkpoint checksum mismatch.");
        var temporary=Path.Combine(Path.GetTempPath(),"hm-mobilehand-"+Guid.NewGuid().ToString("N")+".zip");
        try
        {
            TorchCheckpoint.ConvertLegacy(checkpoint,temporary,cancellation);
            using var parsed=new TorchCheckpoint(temporary);angleBasis=parsed.ReadFloat("mano.Z_",cancellation);
            if(angleBasis.Length!=23*45||angleBasis.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid MobileHand angle basis.");
            using var bytes=File.OpenRead(temporary);var hash=Convert.ToHexString(SHA256.HashData(bytes));
            weights=new(temporary,hash,name=>name.StartsWith("encoder.features.")||name.StartsWith("encoder.conv.")||name.StartsWith("regressor."),cancellation);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    /// <summary>RGB CHW 224x224 in [0,1], as in the released FreiHAND demo.</summary>
    public Prediction Run(float[] image,CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(image.Length!=3*224*224||image.Any(v=>!float.IsFinite(v)||v<0||v>1))throw new ArgumentException("Invalid MobileHand image tensor.");
        if(Interlocked.Exchange(ref running,1)!=0)throw new InvalidOperationException("MobileHand is already processing a frame.");
        try
        {
            using var noGrad=no_grad();using var scope=NewDisposeScope();
            var x=HardSwish(weights.BatchNorm(weights.Conv(tensor(image).reshape(1,3,224,224),"encoder.features.0.0",2,1),"encoder.features.0.1"));
            (int Kernel,int Hidden,int Output,bool Se,bool Hs,int Stride)[] blocks={
                (3,16,16,true,false,2),(3,72,24,false,false,2),(3,88,24,false,false,1),
                (5,96,40,true,true,2),(5,240,40,true,true,1),(5,240,40,true,true,1),
                (5,120,48,true,true,1),(5,144,48,true,true,1),(5,288,96,true,true,2),
                (5,576,96,true,true,1),(5,576,96,true,true,1)};
            var inputChannels=16;
            for(var i=0;i<blocks.Length;i++)
            {
                cancellation.ThrowIfCancellationRequested();using var layer=NewDisposeScope();
                var b=blocks[i];var prefix=$"encoder.features.{i+1}.conv";Tensor y;
                if(inputChannels==b.Hidden)
                {
                    y=Activate(weights.BatchNorm(Depthwise(x,prefix+".0",b.Hidden,b.Kernel,b.Stride),prefix+".1"),b.Hs);
                    if(b.Se)y=SqueezeExcite(y,prefix+".3");
                    y=weights.BatchNorm(weights.Conv(y,prefix+".4"),prefix+".5");
                }
                else
                {
                    y=Activate(weights.BatchNorm(weights.Conv(x,prefix+".0"),prefix+".1"),b.Hs);
                    y=weights.BatchNorm(Depthwise(y,prefix+".3",b.Hidden,b.Kernel,b.Stride),prefix+".4");
                    if(b.Se)y=SqueezeExcite(y,prefix+".5");
                    y=Activate(y,b.Hs);y=weights.BatchNorm(weights.Conv(y,prefix+".7"),prefix+".8");
                }
                if(b.Stride==1&&inputChannels==b.Output)y=y+x;
                var previous=x;x=y.MoveToOuterDisposeScope();previous.Dispose();inputChannels=b.Output;
            }
            x=HardSwish(weights.BatchNorm(weights.Conv(x,"encoder.conv.0.0"),"encoder.conv.0.1"));
            x=SqueezeExcite(x,"encoder.conv.1");
            var features=HardSwish(F.adaptive_avg_pool2d(x,new long[]{1,1})).flatten(1);
            var parameters=weights["regressor.mean_param"].slice(0,0,1,1);
            for(var i=0;i<3;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var combined=cat(new[]{features,parameters},1);
                var hidden=F.relu(weights.Linear(combined,"regressor.fc_blocks.regressor_fc_0"));
                hidden=F.relu(weights.Linear(hidden,"regressor.fc_blocks.regressor_fc_1"));
                parameters=parameters+weights.Linear(hidden,"regressor.fc_blocks.regressor_fc_2");
            }
            var values=HandModelWeights.Array(parameters);
            if(values.Length!=39||values.Any(v=>!float.IsFinite(v)))throw new ArithmeticException("MobileHand produced invalid parameters.");
            var angles=values.Skip(16).ToArray();var pose=new float[48];Array.Copy(values,3,pose,0,3);
            for(var j=0;j<45;j++)for(var a=0;a<23;a++)pose[j+3]+=angles[a]*angleBasis[a*45+j];
            var matrices=new float[16*9];
            for(var j=0;j<16;j++)
            {
                var aa=new System.Numerics.Vector3(pose[j*3],pose[j*3+1],pose[j*3+2]);var angle=aa.Length();
                var q=angle<1e-8f?System.Numerics.Quaternion.Identity:System.Numerics.Quaternion.CreateFromAxisAngle(aa/angle,angle);
                var m=System.Numerics.Matrix4x4.CreateFromQuaternion(q);
                new[]{m.M11,m.M21,m.M31,m.M12,m.M22,m.M32,m.M13,m.M23,m.M33}.CopyTo(matrices,j*9);
            }
            return new(matrices,values.Skip(6).Take(10).ToArray(),values.Take(3).ToArray(),angles,values);
        }
        finally{Volatile.Write(ref running,0);}
    }
    static Tensor HardSwish(Tensor x)=>x*F.relu6(x+3)/6;
    static Tensor Activate(Tensor x,bool hardSwish)=>hardSwish?HardSwish(x):F.relu(x);
    Tensor SqueezeExcite(Tensor x,string prefix)
    {
        var pooled=F.adaptive_avg_pool2d(x,new long[]{1,1}).flatten(1);
        var scale=F.relu6(weights.Linear(F.relu(weights.Linear(pooled,prefix+".fc.0")),prefix+".fc.2")+3)/6;
        return x*scale.reshape(x.shape[0],x.shape[1],1,1);
    }
    Tensor Depthwise(Tensor x,string name,int channels,int kernel,int stride)
        =>F.conv2d(x,weights[name+".weight"],strides:new long[]{stride,stride},padding:new long[]{(kernel-1)/2,(kernel-1)/2},groups:channels);
    public void Dispose(){if(disposed)return;disposed=true;weights.Dispose();}
}
