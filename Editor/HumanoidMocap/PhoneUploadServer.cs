using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HumanoidMocap.Editor;

/// <summary>Bounded local HTTP receiver, inside the editor process. Pairing lasts one hour.
/// No cookies, cloud service, external worker, shell command or executable upload handling.</summary>
public sealed class PhoneUploadServer : IDisposable
{
    readonly Func<DateTime> utcNow;
    readonly TcpListener listener;
    readonly CancellationTokenSource stopped=new();
    readonly SemaphoreSlim connections=new(4,4),uploads=new(1,1);
    readonly string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    readonly string directory;
    readonly string host;
    public DateTime ExpiresUtc { get; }
    public string PairingUrl => $"http://{host}/#{token}";
    public long MaximumBytes { get; set; } = 2L*1024*1024*1024;
    public Action<string> Received { get; set; }
    public Action<long,long> Progress { get; set; }
    public Action<string> Error { get; set; }
    public string ThemeCss { get; set; } = "";
    public PhoneUploadServer(IPAddress address,string directory,int port=0)
        : this(address,directory,port,()=>DateTime.UtcNow) {}
    internal PhoneUploadServer(IPAddress address,string directory,int port,Func<DateTime> utcNow)
    {
        this.utcNow=utcNow;ExpiresUtc=utcNow().AddHours(1);
        if(address.AddressFamily!=AddressFamily.InterNetwork)throw new ArgumentException("Select an IPv4 LAN address.");
        this.directory=Path.GetFullPath(directory);Directory.CreateDirectory(this.directory);
        listener=new TcpListener(address,port);listener.Start(4);
        host=$"{address}:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _=AcceptLoop();_=ExpireAsync();
    }
    async Task ExpireAsync()
    {
        try{await Task.Delay(TimeSpan.FromHours(1),stopped.Token);Dispose();}catch(OperationCanceledException){}
    }
    async Task AcceptLoop()
    {
        try
        {
            while(!stopped.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stopped.Token);
                if(!await connections.WaitAsync(0)){client.Dispose();continue;}
                _=Handle(client);
            }
        }
        catch(OperationCanceledException){}
        catch(ObjectDisposedException){}
        catch(Exception e){if(!stopped.IsCancellationRequested)Error?.Invoke(e.Message);}
    }
    async Task Handle(TcpClient client)
    {
        string partial=null;bool ownsUpload=false;
        using(client)
        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stopped.Token))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(20));var cancellation=deadline.Token;
            try
            {
                var stream=client.GetStream();var bytes=new List<byte>();var one=new byte[1];
                while(bytes.Count<16384)
                {
                    if(await stream.ReadAsync(one.AsMemory(),cancellation)==0)return;
                    bytes.Add(one[0]);var n=bytes.Count;
                    if(n>=4 && bytes[n-4]==13 && bytes[n-3]==10 && bytes[n-2]==13 && bytes[n-1]==10)break;
                }
                if(bytes.Count>=16384){await Reply(stream,431,"Headers too large",cancellation);return;}
                var lines=Encoding.ASCII.GetString(bytes.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);
                var request=lines[0].Split(' ');if(request.Length!=3){await Reply(stream,400,"Invalid request",cancellation);return;}
                var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var line in lines.Skip(1))
                {
                    var colon=line.IndexOf(':');if(colon<0)continue;
                    var key=line.Substring(0,colon).Trim();
                    if(headers.ContainsKey(key)){await Reply(stream,400,"Duplicate header",cancellation);return;}
                    headers[key]=line.Substring(colon+1).Trim();
                }
                if(!headers.TryGetValue("Host",out var requestHost)||!string.Equals(requestHost,host,StringComparison.OrdinalIgnoreCase))
                {await Reply(stream,403,"Unexpected host",cancellation);return;}
                if(utcNow()>=ExpiresUtc){await Reply(stream,410,"Pairing expired. Scan a new code in the editor.",cancellation);return;}
                if(request[0]=="GET" && request[1]=="/")
                {await Reply(stream,200,Page.Replace("/*EDITOR_THEME*/",ThemeCss),cancellation,"text/html; charset=utf-8");return;}
                if(!headers.TryGetValue("X-Mocap-Token",out var supplied)||!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied),Encoding.ASCII.GetBytes(token)))
                {await Reply(stream,403,"Pairing required. Scan the editor QR code.",cancellation);return;}
                if(request[0]=="GET" && request[1]=="/session")
                {await Reply(stream,200,((int)(ExpiresUtc-utcNow()).TotalSeconds).ToString(),cancellation);return;}
                if(request[0]!="POST"||request[1]!="/upload"){await Reply(stream,404,"Not found",cancellation);return;}
                if(headers.ContainsKey("Transfer-Encoding")||!headers.TryGetValue("Content-Length",out var lengthText)||!long.TryParse(lengthText,out var length)||length<12)
                {await Reply(stream,411,"A video file with a known length is required.",cancellation);return;}
                if(length>MaximumBytes){await Reply(stream,413,"Video exceeds the 2 GB upload limit.",cancellation);return;}
                var original=Uri.UnescapeDataString(headers.GetValueOrDefault("X-Filename","video.mp4"));
                original=Path.GetFileName(original.Replace('\\','/'));var extension=Path.GetExtension(original).ToLowerInvariant();
                // Only containers the capture pipeline reads; refusing here saves uploading a file that cannot be processed.
                if(!new[]{".mp4",".mov",".m4v"}.Contains(extension))
                {await Reply(stream,415,"Choose an MP4, MOV or M4V video. Phone cameras record these; convert other formats to H.264 MP4 first.",cancellation);return;}
                ownsUpload=await uploads.WaitAsync(0,cancellation);
                if(!ownsUpload){await Reply(stream,409,"Another upload is in progress. Try again shortly.",cancellation);return;}
                var safe=new string(Path.GetFileNameWithoutExtension(original).Where(c=>char.IsLetterOrDigit(c)||c=='-'||c=='_').Take(70).ToArray());
                var destination=Path.Combine(directory,$"{safe}_{Guid.NewGuid():N}{extension}");partial=destination+".partial";
                using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
                {
                    var buffer=new byte[65536];long remaining=length;var progressClock=System.Diagnostics.Stopwatch.StartNew();
                    while(remaining>0)
                    {
                        var count=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),cancellation);
                        if(count==0)throw new IOException("Upload interrupted. Select the video again to retry.");
                        await output.WriteAsync(buffer.AsMemory(0,count),cancellation);remaining-=count;
                        if(remaining==0||progressClock.ElapsedMilliseconds>=100){Progress?.Invoke(length-remaining,length);progressClock.Restart();}
                    }
                }
                var header=new byte[12];using(var input=File.OpenRead(partial))input.ReadExactly(header);
                var mp4=Encoding.ASCII.GetString(header,4,4)=="ftyp";
                if(!mp4)
                {await Reply(stream,415,"The file is not a recognized video container.",cancellation);return;}
                File.Move(partial,destination);partial=null;Received?.Invoke(destination);
                await Reply(stream,200,"Received. You can choose another video; pairing remains active for one hour.",cancellation);
            }
            catch(OperationCanceledException){}
            catch(Exception e){Error?.Invoke(e.Message);}
            finally
            {
                // Finish the response before discarding a rejected request body. Closing a
                // socket with unread incoming bytes can turn the useful HTTP error into RST.
                try
                {
                    client.Client.Shutdown(SocketShutdown.Send);
                    using var drainDeadline=new CancellationTokenSource(250);
                    var drain=new byte[4096];var budget=65536;
                    while(budget>0)
                    {
                        var count=await client.GetStream().ReadAsync(drain.AsMemory(0,Math.Min(drain.Length,budget)),drainDeadline.Token);
                        if(count==0)break;budget-=count;
                    }
                }
                catch(Exception){}
                try{if(partial is not null && File.Exists(partial))File.Delete(partial);}
                catch(IOException e){Error?.Invoke("Could not remove an interrupted upload: "+e.Message);}
                finally{if(ownsUpload)uploads.Release();connections.Release();}
            }
        }
    }
    static async Task Reply(NetworkStream stream,int status,string body,CancellationToken token,string type="text/plain; charset=utf-8")
    {
        var bytes=Encoding.UTF8.GetBytes(body);
        var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n\r\n");
        await stream.WriteAsync(header.AsMemory(),token);await stream.WriteAsync(bytes.AsMemory(),token);
    }
    public void Dispose(){if(stopped.IsCancellationRequested)return;stopped.Cancel();listener.Stop();}
    const string Page="""
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Humanoid Mocap · Send video</title><style>
:root{--window:#181818;--surface:#2a2a2a;--border:#3e3e3e;--text:#fff;--muted:#9e9e9e;--primary:#2e70ea}/*EDITOR_THEME*/body{margin:0;background:var(--window);color:var(--text);font:16px Inter,Segoe UI,system-ui,sans-serif}main{max-width:440px;margin:8vh auto;padding:24px}h1{font-size:26px;margin-bottom:8px}p{color:var(--muted);line-height:1.5}label,button{display:block;background:var(--primary);color:white;border:0;border-radius:4px;padding:16px;text-align:center;font:inherit;cursor:pointer}input{box-sizing:border-box;width:100%;margin:20px 0;padding:12px;background:var(--surface);border:1px solid var(--border);border-radius:4px;color:var(--text)}input::file-selector-button{padding:8px 12px;background:var(--surface);color:var(--text);border:1px solid var(--border);border-radius:4px;margin-right:12px}button{width:100%;margin-top:20px}button:disabled{opacity:.45}progress{width:100%;height:12px;margin-top:24px}#status{white-space:pre-wrap}small{color:var(--muted)}</style>
<main><h1>Humanoid Mocap</h1><p>Send a video from your photo library to the editor on your PC. Stay on the same Wi-Fi network.</p>
<small id="session">Checking pairing…</small><input id="file" type="file" accept="video/*"><button id="send" disabled>Upload video</button>
<progress id="progress" value="0" max="100"></progress><p id="status" role="status" aria-live="polite"></p></main><script>
const token=location.hash.slice(1),file=document.querySelector('#file'),send=document.querySelector('#send'),status=document.querySelector('#status'),progress=document.querySelector('#progress');let active=false,busy=false;
async function session(){try{const r=await fetch('/session',{headers:{'X-Mocap-Token':token}});if(!r.ok)throw Error(await r.text());const seconds=Number(await r.text());active=seconds>0;document.querySelector('#session').textContent='Paired · '+Math.ceil(seconds/60)+' minutes remaining';send.disabled=busy||!active||!file.files.length;}catch(e){active=false;send.disabled=true;document.querySelector('#session').textContent='Pairing ended. Scan a new QR code in the editor.';}}
file.onchange=()=>{send.disabled=busy||!active||!file.files.length;progress.value=0;};send.onclick=()=>{const f=file.files[0];if(!f||busy||!active)return;if(f.size>2147483648){status.textContent='Choose a video smaller than 2 GB.';return;}busy=true;file.disabled=true;send.disabled=true;const xhr=new XMLHttpRequest();xhr.open('POST','/upload');xhr.setRequestHeader('X-Mocap-Token',token);xhr.setRequestHeader('X-Filename',encodeURIComponent(f.name));xhr.setRequestHeader('Content-Type','application/octet-stream');xhr.upload.onprogress=e=>{if(e.lengthComputable)progress.value=e.loaded/e.total*100;};xhr.onload=()=>{busy=false;file.disabled=false;status.textContent=xhr.responseText;if(xhr.status===200){file.value='';progress.value=100;}session();};xhr.onerror=()=>{busy=false;file.disabled=false;status.textContent='Connection lost. Check Wi-Fi and retry; the original video is unchanged.';session();};status.textContent='Uploading '+f.name+'…';xhr.send(f);};session();setInterval(session,30000);
</script></html>
""";
}
