using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    internal Task OpenCaptureFileAsync(string path)
    {
        if(_processing is not null)return Task.CompletedTask;
        return string.Equals(Path.GetExtension(path),".tar",StringComparison.OrdinalIgnoreCase)
            ?ImportHot3dClipAsync(path):LoadMotionAsync(path);
    }

    async Task ImportHot3dClipAsync(string path)
    {
        var loadRevision=_motionLoadRevision;
        _processing=new CancellationTokenSource();var token=_processing.Token;
        SetCaptureBusy(true);_retryCaptureButton.Visible=false;
        // Keep the existing preview until a valid imported capture is ready.
        // Close the optional settings window so progress and Cancel remain visible.
        if(_adjustments.IsValid())_adjustments.Close();
        _captureStatus.Text="Importing HOT3D annotations locally. No reconstruction model is run.";
        try
        {
            var result=await NativeCapture.ImportHot3dAsync(Path.GetFullPath(path),ReceiveWorkerProgress,token);
            await EditorPipeline.SwitchToMainThread();token.ThrowIfCancellationRequested();
            if(!this.IsValid())return;
            if(loadRevision!=_motionLoadRevision)
            {_captureStatus.Text="HOT3D import finished; current capture kept. Open the result: "+result;return;}
            Interlocked.Exchange(ref _workerMessage,null);
            SetWorkspace(true);await LoadMotionAsync(result);
            if(this.IsValid()&&_motionPath==result&&_bakedPreview is not null)
                _captureStatus.Text="HOT3D annotations imported · review the supplied hand and object tracks before exporting. No neural inference was run.";
        }
        catch(OperationCanceledException)
        {
            await EditorPipeline.SwitchToMainThread();_queuedVideos.Clear();
            if(this.IsValid())_captureStatus.Text="HOT3D import cancelled. Reopen the archive to restart; the previous capture is preserved.";
        }
        catch(Exception error)
        {await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=error.Message;}
        finally
        {
            Interlocked.Exchange(ref _workerMessage,null);await EditorPipeline.SwitchToMainThread();
            _processing?.Dispose();_processing=null;
            if(this.IsValid()){SetCaptureBusy(false);StartNextQueuedVideo();}
        }
    }
}
