using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HumanoidMocap.Editor;

/// <summary>Local C# inference process; only job metadata and local paths cross the process boundary.</summary>
internal static class NativeCapture
{
    static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sbox-humanoid-mocap");
    static readonly SemaphoreSlim WorkerBuild = new(1, 1);

    // Include every source linked by the worker project, not only its entry point.
    // Paths relative to the library keep identical installations/cache copies equivalent.
    static string WorkerFingerprint(string library)
    {
        var paths = Directory.EnumerateFiles(Path.Combine(library,"InferenceWorker"),"*.cs",SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(library,"Code","HumanoidMocap"),"*.cs",SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(library,"Editor","HumanoidMocap","Inference"),"*.cs",SearchOption.TopDirectoryOnly))
            .Append(Path.Combine(library,"Editor","HumanoidMocap","Mp4Metadata.cs"))
            .Append(Path.Combine(library,"InferenceWorker","HumanoidMocap.Worker.csproj"))
            .OrderBy(path=>Path.GetRelativePath(library,path),StringComparer.Ordinal);
        using var hash=SHA256.Create();var manifest=new StringBuilder("win-x64;Release;self-contained;v2\n");
        foreach(var path in paths)
        {
            using var stream=File.OpenRead(path);
            manifest.Append(Path.GetRelativePath(library,path).Replace('\\','/')).Append(':')
                .Append(Convert.ToHexString(hash.ComputeHash(stream))).Append('\n');
        }
        return Convert.ToHexString(hash.ComputeHash(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    static async Task<(string Worker,string Models)> Prepare(Action<string> progress,CancellationToken token)
    {
        var asset = EditorPipeline.FindLibraryAssetFile(EditorPipeline.TargetRigJsonRelative);
        var library = asset is null ? null : new DirectoryInfo(Path.GetDirectoryName(asset)).Parent?.Parent?.FullName;
        var cache = CacheRoot;
        var worker = Environment.GetEnvironmentVariable("HUMANOID_MOCAP_WORKER");
        if (string.IsNullOrWhiteSpace(worker))
        {
            var project = library is null ? "" : Path.Combine(library, "InferenceWorker", "HumanoidMocap.Worker.csproj");
            if (!File.Exists(project)) throw new FileNotFoundException("The C# inference worker is missing from this library installation. Install the complete Humanoid Mocap package.");
            var fingerprint=await Task.Run(()=>WorkerFingerprint(library),token);
            var output=Path.Combine(cache,"worker",fingerprint);
            var ready=Path.Combine(output,"build-complete.txt");
            worker=Path.Combine(output,"HumanoidMocap.Worker.exe");
            await WorkerBuild.WaitAsync(token);
            try
            {
                if(!File.Exists(worker)||!File.Exists(ready)||File.ReadAllText(ready)!=fingerprint)
                {
                    await Notify(progress,"Preparing local capture. First-time setup downloads native inference dependencies…");
                    await RunProcess("dotnet", new[] { "publish", project, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", output }, null, token);
                    if(!File.Exists(worker))throw new FileNotFoundException("Worker setup finished without an executable.",worker);
                    File.WriteAllText(ready,fingerprint);
                }
            }
            finally{WorkerBuild.Release();}
        }
        var models = Environment.GetEnvironmentVariable("HUMANOID_MOCAP_MODELS");
        if(!File.Exists(worker))throw new FileNotFoundException("The configured C# worker was not found. Rebuild it or update HUMANOID_MOCAP_WORKER.",worker);
        if (string.IsNullOrWhiteSpace(models)) models = library is not null && Directory.Exists(Path.Combine(library,"models")) ? Path.Combine(library,"models") : Path.Combine(cache,"models");
        return (worker,models);
    }

    /// <summary>Opens the video once before any build or download, so footage this PC cannot decode
    /// (typically HEVC from an iPhone) is reported in seconds rather than after gigabytes of models.</summary>
    static void EnsureDecodable(string video){using var decoder=new Inference.WindowsVideoDecoder(video);}

    public static async Task<string> BodyAsync(string video,double start,double end,int width,int height,Action<string> progress,CancellationToken token)
    {
        EnsureDecodable(video);
        var (worker,models)=await Prepare(progress,token);
        await RunProcess(worker, new[] { "download-body-models", models }, progress, token);
        // Fingers in a body capture come from WiLoR. Without it the body is still captured and
        // its hands hold a resting pose, so a failed or offline download must not stop the job.
        try{await RunProcess(worker,new[]{"download-hand-models",models,"wilor"},progress,token);}
        catch(OperationCanceledException){throw;}
        catch(Exception){progress?.Invoke("Finger model unavailable; capturing the body without finger motion");}
        // Omitting PersonCrop enables the worker's automatic image-space subject track.
        // This does not recover camera motion or calibrate world scale.
        return await Job(worker,"body",jobs=>new { Video = video, Models = models, Output = jobs, Start = start, End = end },progress,token);
    }

    public static async Task<string> HandsAsync(string video,string backend,double start,double end,int width,int height,Action<string> progress,CancellationToken token,float? recordingHorizontalFov=null)
    {
        if(backend is not ("mobilehand" or "wildhands" or "wilor"))throw new NotSupportedException("Choose MediaPipe, MobileHand, WildHands or WiLoR. ACE is not loaded automatically.");
        var focal=Motion.CaptureCameraFraming.EstimatedFocalLength(width,height,recordingHorizontalFov);
        EnsureDecodable(video);
        var (worker,models)=await Prepare(progress,token);
        await RunProcess(worker,new[]{"download-hand-models",models,backend},progress,token);
        // Estimated pinhole intrinsics; these are neither calibrated nor world-space recovery.
        // Without a supplied recording FOV the worker sizes the lens from the clip's hands;
        // this focal length is then only its fallback when too few hands are found.
        var camera=new { Fx=focal,Fy=focal,Cx=width*.5f,Cy=height*.5f,Calibrated=false };
        return await Job(worker,"hand",jobs=>new { Video=video,Models=models,Output=jobs,Backend=backend,Start=start,End=end,Camera=camera,EstimateFocal=recordingHorizontalFov is null },progress,token);
    }

    /// <param name="followedCameraRotation">Use the camera rotation the capture followed from the
    /// background instead of assuming the camera stood still.</param>
    public static async Task<string> RefineBodyAsync(string motion,Action<string> progress,CancellationToken token,bool followedCameraRotation=false)
    {
        var (worker,models)=await Prepare(progress,token);
        return await Job(worker,"body-refinement",jobs=>new {Motion=motion,Models=models,Output=jobs,AssumeStationaryCamera=!followedCameraRotation,UseCameraRotation=followedCameraRotation},
            progress,token,command:"body-refine");
    }

    public static async Task<string> ImportHot3dAsync(string archive,Action<string> progress,CancellationToken token)
    {
        var (worker,_)=await Prepare(progress,token);
        return await Job(worker,"hot3d",jobs=>new{Archive=archive,Output=jobs},progress,token,command:"import-hot3d");
    }

    public static async Task<string> LandmarksAsync(string video,string model,string output,double start,double? end,bool swapHands,Action<string> progress,CancellationToken token)
    {
        var template=EditorPipeline.FindLibraryAssetFile(EditorPipeline.TargetRigJsonRelative)
            ??throw new FileNotFoundException("The canonical hand skeleton is missing from this library.");
        var (worker,_)=await Prepare(progress,token);
        return await Job(worker,"landmark",jobs=>new{Video=video,Model=model,Output=jobs,Template=template,Start=start,End=end,SwapHands=swapHands},progress,token,output);
    }

    static async Task<string> Job(string worker,string kind,Func<string,object> createRequest,Action<string> progress,CancellationToken token,string output=null,string command=null)
    {
        var jobs=output??Path.Combine(CacheRoot,"jobs",kind);Directory.CreateDirectory(jobs);
        var request=Path.Combine(jobs,Guid.NewGuid().ToString("N")+".request.json");
        File.WriteAllText(request,JsonSerializer.Serialize(createRequest(jobs)));
        var result = await RunProcess(worker, new[] { command??kind+"-capture", request }, progress, token);
        if (string.IsNullOrWhiteSpace(result) || !File.Exists(result) || !Path.GetFullPath(result).StartsWith(Path.GetFullPath(jobs) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The inference worker finished without a valid motion file.");
        return result;
    }

    // Callbacks carry data only. The window consumes progress on its editor frame.
    static Task Notify(Action<string> progress, string text)
    {progress?.Invoke(text);return Task.CompletedTask;}

    static Task<string> RunProcess(string executable, IEnumerable<string> arguments, Action<string> progress, CancellationToken token)
        => Task.Run(()=>RunProcessBackground(executable,arguments,progress,token),token);

    static async Task<string> RunProcessBackground(string executable, IEnumerable<string> arguments, Action<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true,
            StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8,StandardInputEncoding=System.Text.Encoding.UTF8 };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        Process Start()
        {
            try{return Process.Start(info)??throw new InvalidOperationException("Could not start local inference.");}
            catch(System.ComponentModel.Win32Exception e) when(executable=="dotnet")
            {throw new InvalidOperationException("Install .NET 10 SDK to prepare the local C# worker, or set HUMANOID_MOCAP_WORKER to a prebuilt worker.",e);}
        }
        using var process = Start();
        string result = null, latestProgress=null; var errors = new Queue<string>();var outputErrors=new Queue<string>();
        async Task ReadOutput()
        {
            string line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (line.StartsWith("HM_RESULT ")) result = line.Substring(10);
                else if (line.Length < 2048)
                {
                    Interlocked.Exchange(ref latestProgress,line);
                    if(line.Contains("error ",StringComparison.OrdinalIgnoreCase)||line.Contains("failed",StringComparison.OrdinalIgnoreCase))
                    {if(outputErrors.Count==8)outputErrors.Dequeue();outputErrors.Enqueue(line);}
                }
            }
        }
        async Task ReadErrors()
        {
            string line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            { if (errors.Count == 12) errors.Dequeue(); errors.Enqueue(line.Length > 2048 ? line[..2048] : line); }
        }
        // Read streams on background threads. Dispatch only the latest status while
        // the process is running, without tying pipe draining to editor callbacks.
        var output = Task.Run(ReadOutput); var error = Task.Run(ReadErrors);
        try
        {
            var exited=process.WaitForExitAsync(token);
            while(!exited.IsCompleted)
            {
                await Task.WhenAny(exited,Task.Delay(200,token));token.ThrowIfCancellationRequested();
                if(Interlocked.Exchange(ref latestProgress,null) is { } message)await Notify(progress,message);
            }
            await exited;
        }
        catch (OperationCanceledException)
        {
            try { await process.StandardInput.WriteLineAsync("cancel"); await process.StandardInput.FlushAsync(); } catch (IOException) { }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree:true); await process.WaitForExitAsync(); }
            await Task.WhenAll(output,error); throw;
        }
        await Task.WhenAll(output,error);
        if(Interlocked.Exchange(ref latestProgress,null) is { } last)await Notify(progress,last);
        if (process.ExitCode != 0)
        {
            var details=errors.Concat(outputErrors).ToArray();
            // "dotnet" can exist as a runtime only, or as an SDK too old to build the worker.
            if(executable=="dotnet"&&details.Any(d=>d.Contains("No .NET SDKs were found",StringComparison.OrdinalIgnoreCase)||d.Contains("NETSDK1045",StringComparison.Ordinal)||
                d.Contains("does not support targeting .NET",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The local capture worker needs the .NET 10 SDK (the runtime alone, or an older SDK, cannot build it). Install it from https://dotnet.microsoft.com/download, restart the editor and upload again, or set HUMANOID_MOCAP_WORKER to a prebuilt worker.");
            if(executable=="dotnet"&&details.Any(d=>d.Contains("NU1301",StringComparison.Ordinal)||d.Contains("Unable to load the service index",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("First-time setup could not reach nuget.org to fetch the worker's inference libraries. Check the internet connection and upload again; nothing needs reinstalling.");
            throw new InvalidOperationException($"Local capture failed (exit {process.ExitCode}): " + string.Join("\n", details.Take(4)));
        }
        return result;
    }
}
