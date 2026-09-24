using HumanoidMocap.Inference;
using System.Security.Cryptography;
using System.Text.Json;

namespace HumanoidMocap.Worker;

/// <summary>Only the explicitly selected backend and its crop detector are downloaded.</summary>
public static class HandModelDownloads
{
    public sealed record Asset(string Path,string Url,long Bytes,string Sha256);
    static readonly Asset Detector=new("hand_landmarker.task",
        "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task",
        7819105,"fbc2a30080c3c557093b5ddfc334698132eb341044ccee322ccf8bcf3607cde1");
    static readonly Asset WildHands=new("wildhands/wildhands.ckpt",
        "https://drive.usercontent.google.com/download?id=1FJWBrMmTKjKAo6j5DQS1KYpqqFbAbJ9Q&export=download&confirm=t",
        855094722,"cac3f9a9334da852f3993e95b4ec088dcc6c69f0337db63dacd83e4642880a7b");
    static readonly Asset Wilor=new("wilor/wilor_final.ckpt",
        "https://huggingface.co/spaces/rolpotamias/WiLoR/resolve/99fe3d7acff8104ecca1055df7467709506c2fa6/pretrained_models/wilor_final.ckpt",
        2564989533,"3e97aafc7dd08d883a4cc5a027df61fdb6fda6136dbd1319405413862ada6bb2");
    static readonly Asset MobileHand=new("mobilehand/hmr_model_freihand_auc.pth",
        "https://raw.githubusercontent.com/gmntu/mobilehand/51c112364013b803c38955b55a1572b0d402894c/model/hmr_model_freihand_auc.pth",
        15152098,MobileHandModel.CheckpointSha256);

    public static async Task Ensure(string folder,string backend,CancellationToken token)
    {
        var assets=backend switch{"mediapipe"=>new[]{Detector},"mobilehand"=>new[]{Detector,MobileHand},"wildhands"=>new[]{Detector,WildHands},"wilor"=>new[]{Detector,Wilor},_=>throw new NotSupportedException("Select MediaPipe, MobileHand, WildHands or WiLoR. ACE is not downloaded or loaded by this worker.")};
        using var http=new HttpClient{Timeout=TimeSpan.FromHours(1)};
        var later=assets.Where(a=>!File.Exists(Path.Combine(folder,a.Path))).Sum(a=>a.Bytes);
        foreach(var asset in assets)
        {
            var path=Path.Combine(folder,asset.Path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if(!File.Exists(path)){later-=asset.Bytes;await ModelDownload.Fetch(http,asset.Url,path,asset.Bytes,asset.Sha256,Console.WriteLine,token,later);}
            else await Verify(path,asset,token);
            Console.WriteLine("Verified "+Path.GetFileName(path));
        }
        File.WriteAllText(Path.Combine(folder,backend+"-models.json"),JsonSerializer.Serialize(new{backend,assets,verifiedUtc=DateTime.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
    }
    static async Task Verify(string path,Asset asset,CancellationToken token)
    {
        if(new FileInfo(path).Length!=asset.Bytes)throw new InvalidDataException("Unexpected model size; original preserved: "+path);
        var hash=await Task.Run(()=>FileChecksum.Sha256(path),token);
        if(!hash.Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Model checksum mismatch; original preserved: "+path);
    }
}
