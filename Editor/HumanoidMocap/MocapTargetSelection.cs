using System;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    ComboBox _previewTargetPicker, _advancedTargetPicker;
    bool _updatingTargetPickers;

    /// <summary>Set once the user picks a target; the workspace default then no longer replaces it.</summary>
    bool _targetChosen;
    /// <summary>The last target the user picked. The editor rebuilds this window at times (package mounts,
    /// code reloads); a rebuilt window restores the pick instead of falling back to the default.</summary>
    static TargetPickers.ResolvedTarget s_chosenTarget;
    void RememberChosenTarget(){if(_targetChosen&&_target is not null)s_chosenTarget=_target;}
    void SelectBuiltinPreviewTarget(bool citizen)
    {
        // Native ComboBox selection callbacks also run on programmatic index changes.
        if(_updatingTargetPickers)return;
        _targetChosen=true;UseBuiltinTarget(citizen);RememberChosenTarget();
    }
    void UseBuiltinTarget(bool citizen)
    {
        if(_processing is not null){RefreshTargetPickers();return;}
        _targetRequests++;
        if(citizen)TrySelectSboxCitizenTarget();else TrySelectSboxTarget();
        _=RefreshMocapPreviewAsync();
    }
    /// <summary>The first-person viewmodel arms s&amp;box games use (human 5-finger or citizen 4-finger).</summary>
    void SelectFirstPersonArms(bool citizen)
    {
        if(_updatingTargetPickers)return;
        _targetChosen=true;UseFirstPersonArms(citizen);
    }
    void UseFirstPersonArms(bool citizen)
    {
        if(_processing is not null){RefreshTargetPickers();return;}
        _=SelectFirstPersonArmsAsync(citizen);
    }
    int _targetRequests;
    async Task SelectFirstPersonArmsAsync(bool citizen)
    {
        // The arms may take a moment to mount; a target chosen meanwhile wins.
        var request=++_targetRequests;var before=_target;
        try
        {
            _captureStatus.Text="Loading the first-person arms…";
            var target=await TargetPickers.FirstPersonArmsAsync(citizen);
            await EditorPipeline.SwitchToMainThread();
            if(!this.IsValid()||request!=_targetRequests||_target!=before)return;
            _target=target;_targetError=null;FitMocapPlacementToTarget();RememberChosenTarget();
        }
        catch(Exception e)
        {
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid())return;
            _captureStatus.Text="The first-person arms could not be loaded: "+e.Message;
        }
        RefreshTargetPickers();RefreshStatus();_=RefreshMocapPreviewAsync();
    }
    static bool IsFirstPersonArms(string path)=>path==TargetPickers.HumanArmsPath||path==TargetPickers.CitizenArmsPath;

    void RefreshTargetPickers()
    {
        if(_updatingTargetPickers)return;
        _updatingTargetPickers=true;
        try
        {
            var path=_target?.PreviewModelPath;
            var index=path==RetargetTargetSpec.SboxHumanMalePath?0:path==RetargetTargetSpec.SboxCitizenPath?1:
                path==TargetPickers.HumanArmsPath?2:path==TargetPickers.CitizenArmsPath?3:4;
            if(_previewTargetPicker.IsValid())_previewTargetPicker.CurrentIndex=_target is null?-1:index;
            if(_advancedTargetPicker.IsValid())_advancedTargetPicker.CurrentIndex=_target is null?-1:
                index<4?index:_target.ModelFilePath is null?4:5;
        }
        finally{_updatingTargetPickers=false;}
    }
}
