using System.Threading.Tasks;
using Sandbox;
using HumanoidMocap.Core;

namespace HumanoidMocap.EditorTools;

public sealed partial class RetargetWindow
{
    sealed record BakedPreview(ClipResult Clip,RetargetTargetSpec Target,string Space,int Revision,bool FirstPerson,
        Core.Motion.PropContactMotion Props,Core.Motion.CapturePlacement Placement,bool SupportsProps,
        Core.Motion.MotionDocument Source,System.Collections.Generic.IReadOnlyList<Core.Motion.WristPositionOffset> WristOffsets);
    BakedPreview _bakedPreview;
    Task _mocapPreviewTask=Task.CompletedTask;
    int _previewRevision,_motionLoadRevision;
    bool _previewPending;

    Task RefreshMocapPreviewAsync()
        =>_mocapPreviewTask=BuildMocapPreviewAsync(++_previewRevision);

    void InvalidateMocapPreview()
    {
        ++_previewRevision;_bakedPreview=null;_rebuiltBaked=null;_previewPending=false;
        UpdateTrackingStatus();
        UpdateExportAvailability();
    }
    void UpdateExportAvailability()
    {
        if(_exportMotionButton.IsValid())
            _exportMotionButton.Enabled=_editedMotion is not null&&_processing is null&&!_exportingMotion&&_rebuilding is null
                &&(_exportCaptured||(!_previewPending&&_bakedPreview is not null));
        RefreshMotionBricksButton();
    }
}
