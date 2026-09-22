using HumanoidMocap.Inference;
using System.Security.Cryptography;
using System.Text.Json;

namespace HumanoidMocap.Worker;

public static class BodyModelDownloads
{
    record Asset(string Path,string Url,long Bytes,string Sha256);
    static readonly Asset[] Assets={
        new("models/person/person_detection_mediapipe_2023mar.onnx","https://media.githubusercontent.com/media/opencv/opencv_zoo/47534e27c9851bb1128ccc0102f1145e27f23f98/models/person_detection_mediapipe/person_detection_mediapipe_2023mar.onnx",11990159,PersonDetector.CheckpointSha256),
        new("models/gvhmr/gvhmr_siga24_release.ckpt","https://huggingface.co/camenduru/GVHMR/resolve/21b32d5389e2e59c0737d4c4095bbc0b8c23f66b/gvhmr/gvhmr_siga24_release.ckpt",163508011,"4fae7da2de388d5da3514cb27a2d003f364dacb280e9cf88972b710e589c6b91"),
        new("models/hmr2/hmr2.ckpt","https://huggingface.co/camenduru/GVHMR/resolve/21b32d5389e2e59c0737d4c4095bbc0b8c23f66b/hmr2/epoch%3D10-step%3D25000.ckpt",2709494041,"2dcf79638109781d1ae5f5c44fee5f55bc83291c210653feead9b7f04fa6f20e"),
        new("models/vitpose/vitpose-h-multi-coco.pth","https://huggingface.co/camenduru/GVHMR/resolve/21b32d5389e2e59c0737d4c4095bbc0b8c23f66b/vitpose/vitpose-h-multi-coco.pth",2549075546,"50e33f4077ef2a6bcfd7110c58742b24c5859b7798fb0eedd6d2215e0a8980bc"),
        new("models/smplx/SMPLX_NEUTRAL.npz","https://huggingface.co/camenduru/SMPLer-X/resolve/9e5548b70b48efe6992faa3b2a418df108542bf7/SMPLX_NEUTRAL.npz",108752058,"376021446ddc86e99acacd795182bbef903e61d33b76b9d8b359c2b0865bd992")
    };
    public static async Task Ensure(string modelFolder,CancellationToken token=default)
    {
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;cancellation.Cancel();};Console.CancelKeyPress+=handler;
        using var client=new HttpClient{Timeout=TimeSpan.FromHours(1)};
        try
        {
            foreach(var pinned in Assets)
            {
                var asset=pinned with{Path=System.IO.Path.Combine(modelFolder,pinned.Path.Substring(7))};
                cancellation.Token.ThrowIfCancellationRequested();Directory.CreateDirectory(System.IO.Path.GetDirectoryName(asset.Path)!);
                if(File.Exists(asset.Path))await Verify(asset.Path,asset,cancellation.Token);
                else await ModelDownload.Fetch(client,asset.Url,asset.Path,asset.Bytes,asset.Sha256,Console.WriteLine,cancellation.Token);
                Console.WriteLine("Verified "+System.IO.Path.GetFileName(asset.Path));
            }
            Directory.CreateDirectory(modelFolder);
            File.WriteAllText(System.IO.Path.Combine(modelFolder,"gvhmr-models.json"),JsonSerializer.Serialize(new{
                provenance="Pinned GVHMR model mirrors and OpenCV Zoo MediaPipe person detector; assets remain local",assets=Assets,
                totalBytes=Assets.Sum(x=>x.Bytes),verifiedUtc=DateTime.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{Console.CancelKeyPress-=handler;}
    }
    static async Task Verify(string path,Asset asset,CancellationToken cancellation)
    {
        if(new FileInfo(path).Length!=asset.Bytes)throw new InvalidDataException("Unexpected model size: "+path);
        var hash=await Task.Run(()=>FileChecksum.Sha256(path),cancellation);
        if(!hash.Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Model checksum mismatch; original file preserved: "+path);
    }
}
