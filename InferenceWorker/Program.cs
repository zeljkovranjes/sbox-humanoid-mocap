using HumanoidMocap.Worker;
using HumanoidMocap.Inference;
using System.Text.Json;
using TorchSharp;

torch.set_num_threads(4);
if(args.Length==2&&args[0]=="download-body-models")
{await BodyModelDownloads.Ensure(Path.GetFullPath(args[1]));return;}
if(args.Length==2&&args[0]=="body-capture")
{
    var request=JsonSerializer.Deserialize<BodyCaptureRequest>(File.ReadAllText(args[1]))??throw new ArgumentException("Invalid body job request.");
    using var cancellation=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
    Console.WriteLine(BodyCapture.Run(request,cancellation.Token,Console.WriteLine));return;
}
throw new ArgumentException("Usage: download-body-models <model-folder> | body-capture <job.json>");
