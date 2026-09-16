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
try
{
    if(args.Length==2&&args[0]=="download-samples")
        await SampleDownloads.Ensure(Path.GetFullPath(args[1]),cancellation.Token);
    else if(args.Length==2&&args[0]=="download-body-models")
        await BodyModelDownloads.Ensure(Path.GetFullPath(args[1]),cancellation.Token);
    else if(args.Length==3&&args[0]=="download-hand-models")
        await HandModelDownloads.Ensure(Path.GetFullPath(args[1]),args[2],cancellation.Token);
    else if(args.Length==2&&args[0]=="body-capture")
    {
        torch.set_num_threads(4);
        var request=JsonSerializer.Deserialize<BodyCaptureRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid body job request.");
        Console.WriteLine("HM_RESULT "+BodyCapture.Run(request,cancellation.Token,Console.WriteLine));
    }
    else if(args.Length==2&&args[0]=="hand-capture")
    {
        torch.set_num_threads(4);
        var request=JsonSerializer.Deserialize<HandCaptureRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid hand job request.");
        Console.WriteLine("HM_RESULT "+HandCapture.Run(request,cancellation.Token,Console.WriteLine));
    }
    else throw new ArgumentException("Usage: download-samples <video-folder> | download-body-models <model-folder> | download-hand-models <model-folder> <wildhands|wilor> | body-capture <job.json> | hand-capture <job.json>");
}
catch(OperationCanceledException){Console.Error.WriteLine("Cancelled. Cached reconstruction can be resumed.");Environment.ExitCode=2;}
catch(Exception e){Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
