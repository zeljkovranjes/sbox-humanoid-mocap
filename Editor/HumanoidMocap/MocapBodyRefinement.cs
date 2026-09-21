using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HumanoidMocap.Motion;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    /// <summary>Written by the worker's background-feature test; see InferenceWorker/CameraMotionCheck.cs.</summary>
    const string StationaryCameraPrefix="Stationary recording camera:";
    global::Editor.Button _bodyRefinementButton;
    void RefreshBodyRefinementButton()
    {
        if(!_bodyRefinementButton.IsValid())return;
        if(_stabilizeFeetControl.IsValid())
            _stabilizeFeetControl.Enabled=_rawMotion is {Space:MotionSpace.WorldRelative,StationaryJoints.Count:>0};
        var restore=_rawMotion?.OriginalReconstruction is not null;
        _bodyRefinementButton.Text=restore?"Restore original capture":"Refine · stationary camera";
        _bodyRefinementButton.Enabled=restore||_rawMotion is {Space:MotionSpace.CameraRelative}&&_rawMotion.Backend.StartsWith("GVHMR",StringComparison.Ordinal);
        _bodyRefinementButton.ToolTip=restore?"Reopen the unchanged original reconstruction. Adjustments remain saved separately with each motion file.":
            "Applied automatically when the recording measured as still. Use it yourself only when you know the camera did not move. Reuses saved GVHMR predictions; keeps the original and opens a separate result. Review contacts before exporting.";
    }
    async Task RefineStationaryBodyAsync()
    {
        if(_processing is not null)return;
        var origin=_rawMotion?.OriginalReconstruction;
        if(origin is null&&(_rawMotion is null||_rawMotion.Space!=MotionSpace.CameraRelative||!_rawMotion.Backend.StartsWith("GVHMR",StringComparison.Ordinal)))
        {_captureStatus.Text="Open an original GVHMR body capture to refine stationary-camera contacts.";return;}
        var original=_editSession?.State.RawPath??_motionPath;
        _processing=new CancellationTokenSource();var token=_processing.Token;
        SetCaptureBusy(true);UpdateExportAvailability();
        try
        {
            string refined;
            if(origin is not null)
            {
                await Task.Run(()=>
                {
                    token.ThrowIfCancellationRequested();var bytes=File.ReadAllBytes(origin.Path);
                    if(!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),origin.Sha256,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The original reconstruction changed. Restore the original file before reverting refinement.");
                },token);
                refined=origin.Path;
            }
            else refined=await NativeCapture.RefineBodyAsync(original,ReceiveWorkerProgress,token);
            token.ThrowIfCancellationRequested();await EditorPipeline.SwitchToMainThread();
            if(!this.IsValid())return;
            await LoadMotionAsync(refined);
            if(this.IsValid()&&_motionPath==refined&&_bakedPreview is not null)
                _captureStatus.Text=origin is null?"Stationary-camera refinement ready · original capture preserved. Review foot contacts and fast movement.":"Original capture restored.";
        }
        catch(OperationCanceledException)
        {await EditorPipeline.SwitchToMainThread();_queuedVideos.Clear();if(this.IsValid())_captureStatus.Text="Refinement cancelled. Original capture preserved.";}
        catch(Exception error)
        {await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=error.Message;}
        finally
        {
            Interlocked.Exchange(ref _workerMessage,null);await EditorPipeline.SwitchToMainThread();
            _processing?.Dispose();_processing=null;
            if(this.IsValid()){SetCaptureBusy(false);UpdateExportAvailability();StartNextQueuedVideo();}
        }
    }
}
