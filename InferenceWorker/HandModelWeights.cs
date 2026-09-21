using System.Security.Cryptography;
using HumanoidMocap.Inference;
using TorchSharp;
using static TorchSharp.torch;
using F=TorchSharp.torch.nn.functional;

namespace HumanoidMocap.Worker;

/// <summary>Data-only checkpoint loading for the pinned native C# hand models.</summary>
internal sealed class HandModelWeights : IDisposable
{
    readonly Dictionary<string,Tensor> values=new(StringComparer.Ordinal);
    /// <param name="reduce">Tensors to keep only in a reduced type. They are converted as they
    /// are read, so a second full-precision copy never exists.</param>
    public HandModelWeights(string path,string sha256,Func<string,bool> include,CancellationToken cancellation,Func<string,ScalarType?>? reduce=null)
    {
        using(var input=File.OpenRead(path))
            if(!Convert.ToHexString(SHA256.HashData(input)).Equals(sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hand model checkpoint checksum mismatch.");
        using var checkpoint=new TorchCheckpoint(path);var loaded=0;
        try
        {
            foreach(var (name,info) in checkpoint.Tensors.Where(p=>include(p.Key)))
            {
                cancellation.ThrowIfCancellationRequested();
                var data=checkpoint.ReadFloat(name,cancellation);
                if(data.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Non-finite hand model weights: "+name);
                using var flat=tensor(data);using var shaped=flat.reshape(info.Shape.Select(x=>(long)x).ToArray());
                values.Add(name,(reduce?.Invoke(name) is { } type?shaped.to(type):shaped.clone()).DetachFromDisposeScope());
                // Checkpoint arrays are large-object garbage; collect before the next ones pile up.
                if(++loaded%48==0)GC.Collect();
            }
        }
        catch{Dispose();throw;}
    }
    public Tensor this[string name]=>values.TryGetValue(name,out var value)?value:throw new InvalidDataException("Missing hand model tensor: "+name);
    public bool Contains(string name)=>values.ContainsKey(name);
    public Tensor Linear(Tensor input,string name)=>F.linear(input,this[name+".weight"],values.GetValueOrDefault(name+".bias"));
    public Tensor Norm(Tensor input,string name,int width,double epsilon=1e-6)=>F.layer_norm(input,new long[]{width},this[name+".weight"],this[name+".bias"],epsilon);
    public Tensor Conv(Tensor input,string name,int stride=1,int padding=0)=>F.conv2d(input,this[name+".weight"],values.GetValueOrDefault(name+".bias"),strides:new long[]{stride,stride},padding:new long[]{padding,padding});
    public Tensor BatchNorm(Tensor input,string name)=>F.batch_norm(input,this[name+".running_mean"],this[name+".running_var"],this[name+".weight"],this[name+".bias"],training:false,eps:1e-5);
    public static float[] Array(Tensor value)=>value.contiguous().data<float>().ToArray();
    public void Dispose(){foreach(var value in values.Values)value.Dispose();values.Clear();}
}
