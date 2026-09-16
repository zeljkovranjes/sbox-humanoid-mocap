using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    ComboBox _previewTargetPicker, _advancedTargetPicker;
    bool _updatingTargetPickers;

    void SelectBuiltinPreviewTarget(bool citizen)
    {
        // Native ComboBox selection callbacks also run on programmatic index changes.
        if(_updatingTargetPickers)return;
        if(_processing is not null){RefreshTargetPickers();return;}
        if(citizen)TrySelectSboxCitizenTarget();else TrySelectSboxTarget();
        _=RefreshMocapPreviewAsync();
    }

    void RefreshTargetPickers()
    {
        if(_updatingTargetPickers)return;
        _updatingTargetPickers=true;
        try
        {
            var path=_target?.PreviewModelPath;
            var index=path==RetargetTargetSpec.SboxHumanMalePath?0:path==RetargetTargetSpec.SboxCitizenPath?1:2;
            if(_previewTargetPicker.IsValid())_previewTargetPicker.CurrentIndex=_target is null?-1:index;
            if(_advancedTargetPicker.IsValid())_advancedTargetPicker.CurrentIndex=_target is null?-1:
                index<2?index:_target.ModelFilePath is null?2:3;
        }
        finally{_updatingTargetPickers=false;}
    }
}
