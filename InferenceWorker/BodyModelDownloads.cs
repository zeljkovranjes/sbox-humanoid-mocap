using System.Security.Cryptography;
using System.Text.Json;

namespace HumanoidMocap.Worker;

public static class BodyModelDownloads
{
    record Asset(string Path,string Url,long Bytes,string Sha256);
    static readonly Asset[] Assets={
        new("models/gvhmr/gvhmr_siga24_release.ckpt","https://huggingface.co/camenduru/GVHMR/resolve/21b32d5389e2e59c0737d4c4095bbc0b8c23f66b/gvhmr/gvhmr_siga24_release.ckpt",163508011,"4fae7da2de388d5da3514cb27a2d003f364dacb280e9cf88972b710e589c6b91"),
        new("models/hmr2/hmr2.ckpt","https://huggingface.co/camenduru/GVHMR/resolve/21b32d5389e2e59c0737d4c4095bbc0b8c23f66b/hmr2/epoch%3D10-step%3D25000.ckpt",2709494041,"2dcf79638109781d1ae5f5c44fee5f55bc83291c210653feead9b7f04fa6f20e"),
        new("models/vitpose/vitpose-h-multi-coco.pth","https://huggingface.co/camenduru/GVHMR/resolve/21b32d5389e2e59c0737d4c4095bbc0b8c23f66b/vitpose/vitpose-h-multi-coco.pth",2549075546,"50e33f4077ef2a6bcfd7110c58742b24c5859b7798fb0eedd6d2215e0a8980bc"),
        new("models/smplx/SMPLX_NEUTRAL.npz","https://huggingface.co/camenduru/SMPLer-X/resolve/9e5548b70b48efe6992faa3b2a418df108542bf7/SMPLX_NEUTRAL.npz",108752058,"376021446ddc86e99acacd795182bbef903e61d33b76b9d8b359c2b0865bd992")
    };
    public static async Task Ensure(string modelFolder)
    {
        using var cancellation=new CancellationTokenSource();
        ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;cancellation.Cancel();};Console.CancelKeyPress+=handler;
        using var client=new HttpClient{Timeout=TimeSpan.FromHours(1)};
        try
        {
            foreach(var pinned in Assets)
            {
                var asset=pinned with{Path=System.IO.Path.Combine(modelFolder,pinned.Path.Substring(7))};
                cancellation.Token.ThrowIfCancellationRequested();Directory.CreateDirectory(System.IO.Path.GetDirectoryName(asset.Path)!);
                if(File.Exists(asset.Path))await Verify(asset.Path,asset,cancellation.Token);
                else
                {
                    var partial=asset.Path+"."+Guid.NewGuid().ToString("N")+".partial";
                    try
                    {
                        Console.WriteLine($"Downloading {asset.Path} ({asset.Bytes:N0} bytes)");
                        using var response=await client.GetAsync(asset.Url,HttpCompletionOption.ResponseHeadersRead,cancellation.Token);response.EnsureSuccessStatusCode();
                        if(response.Content.Headers.ContentLength is long size&&size!=asset.Bytes)throw new InvalidDataException("Model download size mismatch.");
                        await using(var input=await response.Content.ReadAsStreamAsync(cancellation.Token))
                        await using(var output=File.Create(partial))
                        {
                            var buffer=new byte[1024*1024];long total=0;int read;
                            while((read=await input.ReadAsync(buffer,cancellation.Token))!=0)
                            {total+=read;if(total>asset.Bytes)throw new InvalidDataException("Model response exceeds its pinned size.");await output.WriteAsync(buffer.AsMemory(0,read),cancellation.Token);}
                        }
                        await Verify(partial,asset,cancellation.Token);File.Move(partial,asset.Path);
                    }
                    finally{if(File.Exists(partial))File.Delete(partial);}
                }
                Console.WriteLine("Verified "+asset.Path);
            }
            Directory.CreateDirectory(modelFolder);
            File.WriteAllText(System.IO.Path.Combine(modelFolder,"gvhmr-models.json"),JsonSerializer.Serialize(new{
                provenance="Pinned mirrors linked by the official GVHMR README's Hugging Face demo; assets remain local",assets=Assets,
                totalBytes=Assets.Sum(x=>x.Bytes),verifiedUtc=DateTime.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{Console.CancelKeyPress-=handler;}
    }
    static async Task Verify(string path,Asset asset,CancellationToken cancellation)
    {
        if(new FileInfo(path).Length!=asset.Bytes)throw new InvalidDataException("Unexpected model size: "+path);
        await using var input=File.OpenRead(path);
        var hash=Convert.ToHexString(await SHA256.HashDataAsync(input,cancellation));
        if(!hash.Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Model checksum mismatch; original file preserved: "+path);
    }
}
