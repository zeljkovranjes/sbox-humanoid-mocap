using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using HumanoidMocap.Core.Motion;
using HumanoidMocap.EditorTools.MotionBricks;
using Sandbox;

namespace HumanoidMocap.EditorTools;

/// <summary>Optional MotionBricks rebuild of a body capture (see <see cref="MotionBricksRebuild"/>): shown beside the capture
/// as BEFORE / AFTER, and exported beside it with a <c>_rebuilt</c> suffix. Needs the Humanoid Retargeter library and the
/// MotionBricks model, which is shared with AI Animator and downloaded once on request.</summary>
public sealed partial class RetargetWindow
{
    /// <summary>Added to the exported file name of the rebuilt animation.</summary>
    public const string RebuiltSuffix="_rebuilt";
    const string MotionBricksTip="Reconstruct the animation with AI (MotionBricks, NVIDIA's motion model) and compare it side by side with the capture. Key poses come from the capture; the motion between them is generated. Fingers, wrists, neck and head keep the capture. Export then writes both.";
    global::Editor.Button _motionBricksButton;
    /// <summary>The rebuilt capture and the capture it was built from; dropped when the capture changes.</summary>
    MotionDocument _rebuiltMotion,_rebuiltFrom;
    PreviewWidget _rebuiltPreview;
    BakedPreview _rebuiltBaked;
    CancellationTokenSource _rebuilding;
    static MotionBricksModel _motionBricksModel;
    static readonly SemaphoreSlim MotionBricksLoad=new(1,1);

    /// <summary>The rebuilt animation for the current capture, or null.</summary>
    MotionDocument CurrentRebuilt=>_rebuiltMotion is not null&&_rebuiltFrom==_editedMotion?_rebuiltMotion:null;
    internal PreviewWidget GateRebuiltPreview=>_rebuiltPreview;
    internal bool GateRebuiltShown=>_rebuiltPreview.IsValid()&&_rebuiltBaked is not null;
    internal string GateRebuiltNote=>_rebuiltMotion?.Diagnostics.LastOrDefault(d=>d.StartsWith("Rebuilt with MotionBricks",StringComparison.Ordinal));
    internal Task GateRebuildAsync()=>RebuildWithMotionBricksAsync(confirmDownload:false);

    void RefreshMotionBricksButton()
    {
        if(!_motionBricksButton.IsValid())return;
        // Whole-body captures only: the model has no hand-only mode.
        var body=_editedMotion?.Bones.Any(b=>b.Role==HumanoidMocap.Core.Mapping.BoneRole.UpperLegL)==true;
        _motionBricksButton.Visible=body;
        _motionBricksButton.Enabled=body&&_processing is null&&!_exportingMotion;
        var shown=CurrentRebuilt is not null;
        _motionBricksButton.Text=_rebuilding is not null?"Cancel reconstruction":shown?"Remove AI version":"Reconstruct with AI";
        _motionBricksButton.Icon=_rebuilding is not null?"cancel":shown?"close":"auto_awesome";
        _motionBricksButton.ToolTip=shown?"Remove the AI version and export only the capture.":MotionBricksTip;
    }

    void MotionBricksClicked()
    {
        if(_rebuilding is not null){_rebuilding.Cancel();return;}
        if(CurrentRebuilt is not null)
        {
            _rebuiltMotion=null;_rebuiltFrom=null;
            _captureStatus.Text="AI version removed. Export writes the capture only.";
            UpdateExportAvailability();_=RefreshMocapPreviewAsync();return;
        }
        _=RebuildWithMotionBricksAsync(confirmDownload:true);
    }

    async Task RebuildWithMotionBricksAsync(bool confirmDownload)
    {
        if(_editedMotion is null||_rebuilding is not null)return;
        var library=RetargeterLibrary.Find(out var problem);
        if(library is null){_captureStatus.Text=problem;return;}
        var store=new ModelStore(ModelPackage.MotionBricks);
        if(!store.Inspect().IsUsable&&_motionBricksModel is null)
        {
            if(!confirmDownload){await DownloadMotionBricksAsync(store);}
            else
            {
                var size=$"{ModelPackage.MotionBricks.TotalBytes/1e6:F0} MB";
                Dialog.AskConfirm(()=>_=DownloadThenRebuildAsync(store),
                    $"Reconstruct with AI needs the MotionBricks model ({size}, downloaded once from Hugging Face and shared with AI Animator). Download it now?",
                    "Download AI model","Download","Cancel");
                return;
            }
        }
        var source=_editedMotion;
        _rebuilding=new CancellationTokenSource();var token=_rebuilding.Token;
        UpdateExportAvailability();
        var rebuiltShown=false;
        try
        {
            ShowRebuildLoader("Loading the AI model…");
            var model=await LoadMotionBricksAsync(store,token);
            var progress=new Progress<float>(f=>{if(this.IsValid()&&_rebuilding is not null)ShowRebuildStatus($"Reconstructing with AI… {f*100:F0}%");});
            var result=await Task.Run(()=>MotionBricksRebuild.Rebuild(source,model,library,progress,token),token);
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid())return;
            if(source!=_editedMotion){_captureStatus.Text="The capture changed while reconstructing; press Reconstruct with AI again.";return;}
            _rebuiltMotion=result.Motion;_rebuiltFrom=source;
            _captureStatus.Text=$"Reconstructed with AI in {result.Elapsed.TotalSeconds:F0} s · {result.AiShare*100:F0}% of the motion made by the AI, {result.KeyPoses} key poses from the capture{(result.AiShare<.95f?"; moves too quick for it keep the capture":"")}. Compare BEFORE and AFTER; Export writes both.";
            rebuiltShown=true;
            await RefreshMocapPreviewAsync();
        }
        catch(OperationCanceledException){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="Reconstruction cancelled. The capture is unchanged.";}
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="AI reconstruction failed: "+e.Message;}
        finally
        {
            await EditorPipeline.SwitchToMainThread();
            _rebuilding?.Dispose();_rebuilding=null;
            if(this.IsValid()){UpdateExportAvailability();if(!rebuiltShown)RestorePreviewAfterRebuild();}
        }
    }

    async Task DownloadThenRebuildAsync(ModelStore store)
    {
        if(await DownloadMotionBricksAsync(store))await RebuildWithMotionBricksAsync(confirmDownload:false);
    }

    async Task<bool> DownloadMotionBricksAsync(ModelStore store)
    {
        _rebuilding=new CancellationTokenSource();var token=_rebuilding.Token;
        UpdateExportAvailability();
        var ready=false;
        try
        {
            ShowRebuildLoader("Downloading the AI model…");
            // This wording drives the loader's progress bar (see ProcessingIndicator).
            var progress=new Progress<InstallProgress>(p=>{if(this.IsValid())ShowRebuildStatus($"Downloading AI model · {p.Fraction*100:F0}% · {Math.Max(0,p.BytesTotal-p.BytesDone)/1e6:F0} MB left");});
            await Task.Run(()=>new ModelInstaller(store).InstallAsync(progress,token),token);
            return ready=store.Inspect().IsUsable;
        }
        catch(OperationCanceledException){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="AI model download cancelled. It resumes where it stopped next time.";return false;}
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="AI model download failed: "+e.Message;return false;}
        finally
        {
            await EditorPipeline.SwitchToMainThread();
            _rebuilding?.Dispose();_rebuilding=null;
            if(this.IsValid()){UpdateExportAvailability();if(!ready)RestorePreviewAfterRebuild();}
        }
    }

    /// <summary>While the model downloads and the rebuild runs, the loader stands in the animation's place, as during a capture.</summary>
    void ShowRebuildLoader(string message)
    {
        if(!_loader.IsValid()||!_loader.Visible)
        {
            _targetHost.Layout.Clear(true);_mocapPreview=null;_rebuiltPreview=null;
            _loader=_targetHost.Layout.Add(new ProcessingIndicator(_targetHost),1);_loaderStatus=null;
        }
        _loader.Busy=true;ShowRebuildStatus(message);
    }
    void ShowRebuildStatus(string message)
    {
        _captureStatus.Text=message;
        if(_loader.IsValid())_loader.SetMessage(message);
    }
    /// <summary>A failed or cancelled rebuild puts the capture's preview back (keeping the status message).</summary>
    async void RestorePreviewAfterRebuild()
    {
        if(_mocapPreview.IsValid())return;
        var message=_captureStatus.Text;
        await RefreshMocapPreviewAsync();
        await EditorPipeline.SwitchToMainThread();
        if(this.IsValid())_captureStatus.Text=message;
    }

    /// <summary>One model per editor session (about 730 MB of weights, loaded in about a second).</summary>
    static async Task<MotionBricksModel> LoadMotionBricksAsync(ModelStore store,CancellationToken token)
    {
        await MotionBricksLoad.WaitAsync(token);
        try{return _motionBricksModel??=await Task.Run(()=>MotionBricksModel.Load(store.Directory,cancel:token),token);}
        finally{MotionBricksLoad.Release();}
    }

    /// <summary>The preview area: the capture alone, or BEFORE and AFTER side by side with plain labels at their top left.</summary>
    PreviewWidget AddPreviewPane(Widget host,string label,Func<Widget,PreviewWidget> create)
    {
        if(label is null)return host.Layout.Add(create(host),1);
        var pane=host.Layout.Add(new Widget(host),1);pane.Layout=Layout.Column();pane.Layout.Spacing=2;
        var text=pane.Layout.Add(new Label(label,pane){FixedHeight=18});
        text.SetStyles($"font-family: Consolas; font-size: 12px; font-weight: 700; color: {(label==AfterLabel?Theme.Green:Theme.TextLight).Hex}; padding-left: 4px;");
        return pane.Layout.Add(create(pane),1);
    }
    const string BeforeLabel="BEFORE",AfterLabel="AFTER";
}
