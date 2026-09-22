using HumanoidMocap.Inference;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HumanoidMocap.Worker;

/// <summary>One pinned model file: resumable, size- and checksum-verified. Model files run to several
/// gigabytes, so an interrupted transfer continues from its partial file instead of starting again,
/// disk space is checked first, and network failures say what to do. A partial that fails its
/// checksum is discarded; the destination only ever receives a verified file.</summary>
public static class ModelDownload
{
    public static async Task Fetch(HttpClient http,string url,string destination,long bytes,string sha256,Action<string> report,CancellationToken token)
    {
        var name=Path.GetFileName(destination);var partial=destination+".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var existing=File.Exists(partial)?new FileInfo(partial).Length:0;
        if(existing>bytes){File.Delete(partial);existing=0;}
        var root=Path.GetPathRoot(Path.GetFullPath(destination));
        if(root is not null&&new DriveInfo(root) is {IsReady:true} drive&&drive.AvailableFreeSpace<bytes-existing+64L*1024*1024)
            throw new IOException($"Not enough free space on {drive.Name} for {name}: {(bytes-existing)/1e9:0.0} GB needed, {drive.AvailableFreeSpace/1e9:0.0} GB free.");
        if(existing<bytes)
        {
            try
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,url);
                if(existing>0)request.Headers.Range=new RangeHeaderValue(existing,null);
                using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
                // A server that ignores the range sends the whole file again.
                if(existing>0&&response.StatusCode!=HttpStatusCode.PartialContent){existing=0;}
                response.EnsureSuccessStatusCode();
                if(response.Content.Headers.ContentLength is long length&&length!=bytes-existing)throw new InvalidDataException("Unexpected model download size for "+name+".");
                report(existing>0?$"Resuming {name} at {existing*100/bytes}% of {bytes/1e6:0.#} MB":$"Downloading {name} · {bytes/1e6:0.#} MB");
                await using var source=await response.Content.ReadAsStreamAsync(token);
                await using var output=new FileStream(partial,existing>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None);
                var buffer=new byte[1024*1024];var received=existing;var lastPercent=(int)(received*100/bytes);int count;
                while((count=await source.ReadAsync(buffer,token))>0)
                {
                    received+=count;if(received>bytes)throw new InvalidDataException("Model response exceeds its pinned size: "+name);
                    await output.WriteAsync(buffer.AsMemory(0,count),token);
                    var percent=(int)(received*100/bytes);if(percent>=lastPercent+5){report($"Downloading {name} · {percent}%");lastPercent=percent;}
                }
            }
            catch(Exception error) when(error is HttpRequestException or IOException&&error is not FileNotFoundException||error is TaskCanceledException&&!token.IsCancellationRequested)
            {
                // The partial file stays: the next attempt continues from it.
                throw new IOException($"Could not finish downloading {name} ({bytes/1e9:0.00} GB). Check the internet connection and choose Retry; the download continues where it stopped. ({error.Message})",error);
            }
        }
        if(new FileInfo(partial).Length!=bytes||!await Matches(partial,sha256,token))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{name} did not match its pinned checksum and was discarded. Choose Retry to download it again.");
        }
        File.Move(partial,destination);
        // The checksum just verified travels with the file, so later jobs need not hash it again.
        if(File.Exists(partial+".sha256"))File.Move(partial+".sha256",destination+".sha256",true);
    }
    public static Task<bool> Matches(string path,string sha256,CancellationToken token)=>Task.Run(()=>FileChecksum.Matches(path,sha256),token);
}
