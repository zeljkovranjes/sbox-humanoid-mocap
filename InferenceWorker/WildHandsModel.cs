// Architecture ported from the authors' WildHands demo, commit
// f99dfea0d1fce970aed2d31d1018eda280e05f47 (Prakash et al., ECCV 2024).
// https://github.com/ap229997/hands/tree/demo/wildhands/models
using TorchSharp;
using static TorchSharp.torch;
using F=TorchSharp.torch.nn.functional;

namespace HumanoidMocap.Worker;

/// <summary>WildHands' two ResNet-50 encoders, camera-conditioned features and
/// separate iterative hand regressors. Outputs are predictions, not calibrated
/// confidence. Camera and MANO decoding are separate from this network.</summary>
public sealed class WildHandsModel : IDisposable
{
    public const string CheckpointSha256="cac3f9a9334da852f3993e95b4ec088dcc6c69f0337db63dacd83e4642880a7b";
    readonly HandModelWeights weights;
    int running;bool disposed;
    public sealed record HandParameters(float[] RotationMatrices,float[] Shape,float[] WeakCamera);
    public sealed record Prediction(HandParameters Right,HandParameters Left);
    public WildHandsModel(string checkpoint,CancellationToken cancellation=default)
    {
        weights=new(checkpoint,CheckpointSha256,name=>
            name.StartsWith("model.backbone.")||name.StartsWith("model.hand_backbone.")||
            name.StartsWith("model.feature_conv.")||name.StartsWith("model.head_"),cancellation);
    }
    /// <summary>Images are ImageNet-normalized RGB CHW 224x224. Angle inputs are
    /// two center ray angles and eight corner ray angles in radians, derived from
    /// the actual crops and supplied intrinsics following upstream preprocessing.</summary>
    public Prediction Run(float[] image,float[] rightCrop,float[] leftCrop,
        float[] rightCenter,float[] rightCorners,float[] leftCenter,float[] leftCorners,
        CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        Check(image,3*224*224);Check(rightCrop,3*224*224);Check(leftCrop,3*224*224);
        Check(rightCenter,2);Check(leftCenter,2);Check(rightCorners,8);Check(leftCorners,8);
        if(Interlocked.Exchange(ref running,1)!=0)throw new InvalidOperationException("WildHands is already processing a frame.");
        try
        {
            using var noGrad=no_grad();using var scope=NewDisposeScope();
            var global=ResNet(tensor(image).reshape(1,3,224,224),"model.backbone",cancellation);
            var right=Hand(global,rightCrop,rightCenter,rightCorners,"r",cancellation);
            var left=Hand(global,leftCrop,leftCenter,leftCorners,"l",cancellation);
            return new(right,left);
        }
        finally{Volatile.Write(ref running,0);}
    }
    static void Check(float[] data,int count)
    {if(data.Length!=count||data.Any(x=>!float.IsFinite(x)))throw new ArgumentException("Invalid WildHands input dimensions or values.");}

    Tensor ResNet(Tensor image,string prefix,CancellationToken cancellation)
    {
        using var scope=NewDisposeScope();
        var x=F.relu(weights.BatchNorm(weights.Conv(image,prefix+".conv1",2,3),prefix+".bn1"));
        x=F.max_pool2d(x,3,stride:2,padding:1);
        int[] blocks={3,4,6,3};
        for(var stage=0;stage<4;stage++)for(var block=0;block<blocks[stage];block++)
        {
            cancellation.ThrowIfCancellationRequested();using var layer=NewDisposeScope();
            var name=$"{prefix}.layer{stage+1}.{block}";var stride=stage>0&&block==0?2:1;
            var y=F.relu(weights.BatchNorm(weights.Conv(x,name+".conv1"),name+".bn1"));
            y=F.relu(weights.BatchNorm(weights.Conv(y,name+".conv2",stride,1),name+".bn2"));
            y=weights.BatchNorm(weights.Conv(y,name+".conv3"),name+".bn3");
            var identity=weights.Contains(name+".downsample.0.weight")
                ?weights.BatchNorm(weights.Conv(x,name+".downsample.0",stride),name+".downsample.1"):x;
            var previous=x;x=F.relu(y+identity).MoveToOuterDisposeScope();previous.Dispose();
        }
        return x.MoveToOuterDisposeScope();
    }
    static Tensor Encode(float[] angles)
    {
        var encoded=new float[angles.Length*4*2];var index=0;
        for(var frequency=0;frequency<4;frequency++)foreach(var angle in angles)
        {encoded[index++]=MathF.Sin((1<<frequency)*angle);encoded[index++]=MathF.Cos((1<<frequency)*angle);}
        return tensor(encoded).reshape(1,encoded.Length,1,1).expand(1,encoded.Length,7,7);
    }
    HandParameters Hand(Tensor global,float[] crop,float[] center,float[] corners,string side,CancellationToken cancellation)
    {
        using var scope=NewDisposeScope();
        var local=ResNet(tensor(crop).reshape(1,3,224,224),"model.hand_backbone",cancellation);
        var x=cat(new[]{local+global,Encode(center),Encode(corners)},1);
        x=F.relu(weights.Conv(x,"model.feature_conv.0"));
        x=F.relu(weights.Conv(x,"model.feature_conv.2"));
        x=F.relu(weights.Conv(x,"model.feature_conv.4"));
        var features=F.relu(weights.Linear(x.flatten(1),"model.feature_conv.7"));
        var prefix="model.head_"+side;
        var cam=weights.Linear(F.relu(weights.Linear(F.relu(weights.Linear(features,prefix+".cam_init.0")),prefix+".cam_init.2")),prefix+".cam_init.4");
        var pose=tensor(Enumerable.Range(0,16).SelectMany(_=>new float[]{1,0,0,0,1,0}).ToArray()).reshape(1,96);
        var shape=zeros(1,10);
        for(var iteration=0;iteration<3;iteration++)
        {
            cancellation.ThrowIfCancellationRequested();
            // init_vector_dict preserves pose, shape, camera insertion order;
            // this differs from the decoder ModuleDict's pose, camera, shape order.
            var state=cat(new[]{features,pose,shape,cam},1);
            var refined=F.relu(weights.Linear(F.relu(weights.Linear(state,prefix+".hmr_layer.refine.0")),prefix+".hmr_layer.refine.3"));
            pose=pose+weights.Linear(refined,prefix+".hmr_layer.decoders.pose_6d");
            cam=cam+weights.Linear(refined,prefix+".hmr_layer.decoders.cam_t/wp");
            shape=shape+weights.Linear(refined,prefix+".hmr_layer.decoders.shape");
        }
        var six=pose.reshape(16,6);var a=six.slice(1,0,3,1);var b=six.slice(1,3,6,1);
        var first=F.normalize(a,dim:1);var second=F.normalize(b-(first*b).sum(1,keepdim:true)*first,dim:1);
        var third=stack(new[]{
            first.select(1,1)*second.select(1,2)-first.select(1,2)*second.select(1,1),
            first.select(1,2)*second.select(1,0)-first.select(1,0)*second.select(1,2),
            first.select(1,0)*second.select(1,1)-first.select(1,1)*second.select(1,0)},1);
        var rotation=stack(new[]{first,second,third},1);
        var result=new HandParameters(HandModelWeights.Array(rotation),HandModelWeights.Array(shape),HandModelWeights.Array(cam));
        if(result.RotationMatrices.Concat(result.Shape).Concat(result.WeakCamera).Any(v=>!float.IsFinite(v)))
            throw new ArithmeticException("WildHands returned a non-finite prediction.");
        return result;
    }
    public void Dispose(){if(disposed)return;disposed=true;weights.Dispose();}
}
