using HumanoidMocap.Worker;
using HumanoidMocap.Inference;
using System.Text.Json;
using TorchSharp;

Console.OutputEncoding=new System.Text.UTF8Encoding(false);
Console.InputEncoding=System.Text.Encoding.UTF8;
using var cancellation=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
if(Console.IsInputRedirected)
{
    _=Task.Run(async()=>
    {
        try{while(await Console.In.ReadLineAsync() is { } line)if(line=="cancel")cancellation.Cancel();}
        catch(ObjectDisposedException){}
    });
}
// MediaPipe hand models run on ONNX Runtime in the worker; the editor keeps the managed interpreter.
LiteOnnx.Install(Console.WriteLine);
// One thread per physical core on typical SMT processors, leaving the rest to the
// editor. Four threads took 2.4 times longer than eight for WiLoR on a Ryzen 7 7800X3D.
static int InferenceThreads()=>int.TryParse(Environment.GetEnvironmentVariable("HUMANOID_MOCAP_THREADS"),out var threads)&&threads is >=1 and <=64
    ?threads:Math.Clamp(Environment.ProcessorCount/2,2,12);
try
{
    if(args.Length==2&&args[0]=="download-body-models")
        await BodyModelDownloads.Ensure(Path.GetFullPath(args[1]),cancellation.Token);
    else if(args.Length==3&&args[0]=="convert-video")
    {VideoConvert.Run(args[1],args[2],cancellation.Token,Console.WriteLine);Console.WriteLine("HM_RESULT "+args[2]);}
    else if(args.Length==3&&args[0]=="lens")
    {
        var metadata=HumanoidMocap.Editor.Mp4Metadata.Read(args[2]);var clock=System.Diagnostics.Stopwatch.StartNew();
        var fov=MogeLens.EstimateHorizontalFov(args[1],args[2],metadata.Times,metadata.Width,metadata.Height,5,cancellation.Token,Console.WriteLine);
        Console.WriteLine(fov is float f?$"FOV {f:F2} deg, focal {metadata.Width/2/Math.Tan(f*Math.PI/360):F1} px, {clock.Elapsed.TotalSeconds:F1}s":"no estimate");
    }
    else if(args.Length==2&&args[0]=="hands-bench")LiteOnnx.Bench(args[1],Console.WriteLine);
    else if(args.Length==1&&args[0]=="adapters")foreach(var a in GpuBackbone.ListAdapters())Console.WriteLine(a);
    else if(args.Length==2&&args[0]=="gpu-bench")GpuBackbone.Bench(args[1],Console.WriteLine);
    else if(args.Length==5&&args[0]=="htd-check")HtdRefine.Check(args[1],args[2],args[3],args[4],Console.WriteLine);
    else if(args.Length==5&&args[0]=="pva-check")PvaNet.Check(args[1],args[2],args[3],int.Parse(args[4]),Console.WriteLine);
    else if(args.Length==2&&args[0]=="download-motion-refiner")
        await PvaNet.EnsureDownloaded(Path.GetFullPath(args[1]),cancellation.Token);
    else if(args.Length==3&&args[0]=="download-hand-models")
        await HandModelDownloads.Ensure(Path.GetFullPath(args[1]),args[2],cancellation.Token);
    else if(args.Length==2&&args[0]=="import-hot3d")
    {
        var request=JsonSerializer.Deserialize<Hot3dImportRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid HOT3D import request.");
        Console.WriteLine("HM_RESULT "+Hot3dClipImport.Run(request,cancellation.Token,Console.WriteLine));
    }
    else if(args.Length==2&&args[0]=="landmark-capture")
    {
        var request=JsonSerializer.Deserialize<LandmarkCaptureRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid landmark job request.");
        Console.WriteLine("HM_RESULT "+LandmarkCapture.Run(request,cancellation.Token,Console.WriteLine));
    }
    else if(args.Length==2&&args[0]=="body-capture")
    {
        torch.set_num_threads(InferenceThreads());
        OpenCvSharp.Cv2.SetNumThreads(InferenceThreads());
        var request=JsonSerializer.Deserialize<BodyCaptureRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid body job request.");
        Console.WriteLine("HM_RESULT "+BodyCapture.Run(request,cancellation.Token,Console.WriteLine));
    }
    else if(args.Length==2&&args[0]=="body-refine")
    {
        var request=JsonSerializer.Deserialize<BodyRefinementRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid body refinement request.");
        Console.WriteLine("HM_RESULT "+BodyRefinement.Run(request,cancellation.Token,Console.WriteLine));
    }
    else if(args.Length==2&&args[0]=="hand-capture")
    {
        torch.set_num_threads(InferenceThreads());
        var request=JsonSerializer.Deserialize<HandCaptureRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid hand job request.");
        Console.WriteLine("HM_RESULT "+HandCapture.Run(request,cancellation.Token,Console.WriteLine));
    }
    else throw new ArgumentException("Usage: download-body-models <model-folder> | download-hand-models <model-folder> <mediapipe|mobilehand|wildhands|wilor> | body-capture <job.json> | body-refine <job.json> | hand-capture <job.json> | landmark-capture <job.json> | import-hot3d <job.json>");
}
catch(OperationCanceledException){Console.Error.WriteLine("Cancelled. Cached reconstruction can be resumed.");Environment.ExitCode=2;}
catch(Exception e){Console.Error.WriteLine(Environment.GetEnvironmentVariable("HUMANOID_MOCAP_TRACE")=="1"?e.ToString():e.Message);Environment.ExitCode=1;}
