using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace HumanoidMocap.Editor;

/// <summary>Downloads only the model, never footage. The pinned artifact is verified before use.</summary>
public static class HandModelStore
{
    const string Url="https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task";
    const string Sha256="fbc2a30080c3c557093b5ddfc334698132eb341044ccee322ccf8bcf3607cde1";
    const long ExpectedBytes=7819105;
    public static async Task<string> EnsureAsync(CancellationToken token)
    {
        var fs=global::Editor.FileSystem.ProjectTemporary;fs.CreateDirectory("humanoid_mocap/models");
        var path=fs.GetFullPath("humanoid_mocap/models/hand_landmarker.task");
        bool Valid(string file)=>File.Exists(file)&&new FileInfo(file).Length==ExpectedBytes&&
            string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),Sha256,StringComparison.OrdinalIgnoreCase);
        if(await Task.Run(()=>Valid(path),token))return path;
        var partial=path+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(5)};
            using var response=await http.GetAsync(Url,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long length&&length!=ExpectedBytes)throw new IOException("Unexpected hand-model download size.");
            using(var input=await response.Content.ReadAsStreamAsync(token))
            using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                var buffer=new byte[65536];long total=0;int count;
                while((count=await input.ReadAsync(buffer.AsMemory(),token))>0)
                {total+=count;if(total>ExpectedBytes)throw new IOException("Hand-model download exceeded its expected size.");await output.WriteAsync(buffer.AsMemory(0,count),token);}
            }
            if(!await Task.Run(()=>Valid(partial),token))throw new IOException("Hand-model checksum mismatch; retry the download.");
            File.Move(partial,path,true);return path;
        }
        finally{if(File.Exists(partial))File.Delete(partial);}
    }
}
