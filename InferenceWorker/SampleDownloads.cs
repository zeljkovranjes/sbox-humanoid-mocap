using System.Security.Cryptography;
using System.Text.Json;
using HumanoidMocap.Editor;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>Optional upstream footage, downloaded on demand and verified by actual decoding.</summary>
internal static class SampleDownloads
{
    record Sample(string Name,string Url,long Bytes,string Sha256);
    static readonly Sample[] Sources=
    {
        new("video_0.mp4","https://raw.githubusercontent.com/ThunderVVV/HaWoR/main/example/video_0.mp4",3950398,"2728d0732f81e46320f09fe0e6017b63aeec11078ba36fcfd9bef443992dbdde"),
        new("segment_018.mp4","https://raw.githubusercontent.com/ThunderVVV/HaWoR/main/example/segment_018.mp4",4467016,"badd5696424dbbe9f3686647a4fb8500cbcc473c5e125c70717e93ef84e40533"),
        new("segment_037.mp4","https://raw.githubusercontent.com/ThunderVVV/HaWoR/main/example/segment_037.mp4",4685174,"36c75c982bb48b4b66b97850309057805a93b84281f75d6240a0f85bef3d1c74"),
        new("tennis.mp4","https://raw.githubusercontent.com/zju3dv/GVHMR/main/docs/example_video/tennis.mp4",2116761,"9fb3c7170b4b1afcf1750e5b6506d97745a16841f855a7cfb3ec869601cc5447")
    };
    public static async Task Ensure(string folder,CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        using var client=new HttpClient{Timeout=TimeSpan.FromMinutes(5)};
        var receipts=new List<object>();
        foreach(var sample in Sources)
        {
            token.ThrowIfCancellationRequested();
            var destination=Path.Combine(folder,sample.Name);
            var temporary=destination+"."+Guid.NewGuid().ToString("N")+".partial";
            try
            {
                var existing=File.Exists(destination);var validated=existing?destination:temporary;
                if(!existing)
                {
                    Console.WriteLine("Downloading "+sample.Name);
                    using var response=await client.GetAsync(sample.Url,HttpCompletionOption.ResponseHeadersRead,token);
                    response.EnsureSuccessStatusCode();
                    await using var input=await response.Content.ReadAsStreamAsync(token);
                    await using var output=File.Create(temporary);
                    var buffer=new byte[65536];long total=0;int count;
                    while((count=await input.ReadAsync(buffer,token))>0)
                    {
                        total+=count;if(total>sample.Bytes)throw new InvalidDataException("Sample download exceeds its pinned size: "+sample.Name);
                        await output.WriteAsync(buffer.AsMemory(0,count),token);
                    }
                }
                using(var stream=File.OpenRead(validated))
                    if(stream.Length!=sample.Bytes||!Convert.ToHexString(await SHA256.HashDataAsync(stream,token)).Equals(sample.Sha256,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Sample checksum mismatch; existing files were preserved: "+sample.Name);
                var metadata=Mp4Metadata.Read(validated);var decoded=0;double first=0,last=double.NegativeInfinity;
                using(var decoder=new WindowsVideoDecoder(validated))
                    while(decoder.Read(token) is { } frame)
                    {
                        if(!double.IsFinite(frame.Time)||frame.Time<=last)throw new InvalidDataException("Sample has invalid decoded timestamps: "+sample.Name);
                        if(decoded==0)first=frame.Time;last=frame.Time;decoded++;
                    }
                if(decoded!=metadata.Times.Length||decoded==0)throw new InvalidDataException("Decoded frame count differs from the sample table: "+sample.Name);
                if(!existing)File.Move(temporary,destination);
                receipts.Add(new{file=sample.Name,source_url=sample.Url,sha256=sample.Sha256,bytes=sample.Bytes,
                    metadata.Duration,metadata.Width,metadata.Height,metadata.FrameRate,decoded_frames=decoded,first_timestamp=first,last_timestamp=last,
                    validation="Pinned size and SHA-256; MP4 sample tables; every frame decoded with Windows Media Foundation"});
                Console.WriteLine($"Verified {sample.Name}: {metadata.Width}x{metadata.Height}, {metadata.Duration:F3}s, {decoded} decoded frames");
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        var receipt=Path.Combine(folder,"download-receipts.json");
        var staged=receipt+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await File.WriteAllTextAsync(staged,JsonSerializer.Serialize(receipts,new JsonSerializerOptions{WriteIndented=true}),token);
            File.Move(staged,receipt,true);
        }
        finally{if(File.Exists(staged))File.Delete(staged);}
    }
}
