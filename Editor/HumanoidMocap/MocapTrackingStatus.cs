using System;
using System.Linq;
using Editor;
using Sandbox;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    Label _trackingStatus;
    BakedPreview _trackingPreview;
    int _trackingFrame=-1;
    int _trackingLeft=-1,_trackingRight=-1;

    void UpdateTrackingStatus()
    {
        if(!_trackingStatus.IsValid())return;
        var preview=_bakedPreview;
        if(preview is null||!HandCaptureRetargeter.Supports(preview.Source))
        {_trackingStatus.Visible=false;_trackingPreview=null;_trackingFrame=-1;return;}
        var frame=_mocapPreview.CurrentFrame;
        if(ReferenceEquals(preview,_trackingPreview)&&frame==_trackingFrame)return;
        var motion=preview.Source;
        if(!ReferenceEquals(preview,_trackingPreview))
        {
            _trackingLeft=motion.Bones.FindIndex(b=>b.Role==BoneRole.HandL);
            _trackingRight=motion.Bones.FindIndex(b=>b.Role==BoneRole.HandR);
        }
        _trackingPreview=preview;_trackingFrame=frame;
        var start=motion.Frames[0].Time;var end=motion.Frames[^1].Time;
        var time=Math.Min(start+frame/(double)preview.Clip.Fps,end);
        var left=_trackingLeft<0?(JointEvidence?)null:MotionEvidence.At(motion,_trackingLeft,time);
        var right=_trackingRight<0?(JointEvidence?)null:MotionEvidence.At(motion,_trackingRight,time);
        string State(JointEvidence? evidence)=>evidence switch{
            JointEvidence.Reconstructed=>"reconstructed",JointEvidence.InferredGap=>"inferred gap",
            JointEvidence.GeneratedIk=>"IK estimate",JointEvidence.Authored=>"authored",
            JointEvidence.Unobserved=>"not observed",_=>"not captured"};
        var manual=preview.WristOffsets.Any(e=>WristPositionOffsets.Weight(e,time,start,end)>0);
        var reach=preview.Clip.Mapping?.Notes.FirstOrDefault(n=>n.StartsWith(HandCaptureRetargeter.ReachWarningPrefix,StringComparison.Ordinal));
        var text=$"Left wrist: {State(left)}   ·   Right wrist: {State(right)}"+(manual?"   ·   Manual correction":"")+(reach is not null?"   ·   Clip: arm reach limited":"");
        if(_trackingStatus.Text!=text)
        {
            _trackingStatus.Text=text;
            _trackingStatus.SetStyles($"color: {(reach is null&&left==JointEvidence.Reconstructed&&right==JointEvidence.Reconstructed?Theme.TextLight:Theme.Yellow).Hex};");
        }
        _trackingStatus.ToolTip=$"Evidence at animation time {time:F3} s. Reconstructed means a model prediction, not measured ground truth. "+
            "Unobserved motion is not newly captured; a previous pose may be retained. Inferred gaps are interpolated. Hidden shoulders and elbows are estimated by IK. "+
            "Manual and contact corrections do not turn missing observations into reconstructed motion."+(reach is not null?"\nClip-level check: "+reach:"");
        _trackingStatus.Visible=true;
    }
}
