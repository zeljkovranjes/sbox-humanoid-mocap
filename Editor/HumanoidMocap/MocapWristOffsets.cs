using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    readonly List<WristPositionOffset> _wristOffsets=new();
    Layout _wristOffsetRows;
    int _wristEditRevision;

    internal WristOffsetDialog OpenWristOffsetEditor(int index=-1)
    {
        if(_processing is not null||_editedMotion is null||!HandCaptureRetargeter.Supports(_editedMotion)||_editedMotion.Frames.Count<2)return null;
        _playing=false;UpdateTransport();
        var dialog=new WristOffsetDialog(this,index);dialog.Show();return dialog;
    }

    async Task SaveWristOffsetAsync(MotionDocument expected,int revision,int index,WristPositionOffset replacement)
    {
        if(expected!=_editedMotion||revision!=_wristEditRevision||_processing is not null)
            throw new InvalidOperationException("The capture or target changed. Reopen wrist correction.");
        var candidate=_wristOffsets.Select(e=>e.Copy()).ToList();
        if(index < -1||index>=candidate.Count)throw new ArgumentOutOfRangeException(nameof(index));
        if(replacement is null){if(index<0)return;candidate.RemoveAt(index);}
        else if(index<0)candidate.Add(replacement.Copy());else candidate[index]=replacement.Copy();
        WristPositionOffsets.Validate(candidate);
        var start=expected.Frames[0].Time;var end=expected.Frames[^1].Time;
        foreach(var edit in candidate)
        {
            var bone=expected.Bones.FindIndex(b=>b.Role==edit.Hand);
            if(bone<0||edit.Start<start||edit.End>end)throw new ArgumentException("Choose a wrist and interval inside this capture.");
            if(edit.Enabled&&!expected.Frames.Any(f=>f.Time<=edit.Start&&f.Evidence[bone]==JointEvidence.Reconstructed))
                throw new ArgumentException("Start after this hand has been observed. A correction cannot create a never-tracked hand.");
        }
        var previous=_wristOffsets.Select(e=>e.Copy()).ToArray();var appliedRevision=++_wristEditRevision;
        _wristOffsets.Clear();_wristOffsets.AddRange(candidate);RefreshContacts();
        var previewTask=RefreshMocapPreviewAsync();var previewRevision=_previewRevision;
        await previewTask;await EditorPipeline.SwitchToMainThread();
        if(!this.IsValid()||_wristEditRevision!=appliedRevision||_editedMotion!=expected||_previewRevision!=previewRevision)return;
        if(_bakedPreview is null)
        {
            var error=_captureStatus.Text;_wristOffsets.Clear();_wristOffsets.AddRange(previous);++_wristEditRevision;
            RefreshContacts();await RefreshMocapPreviewAsync();throw new InvalidOperationException(error);
        }
        _captureStatus.Text=_adjustmentSaveError??"Manual wrist correction saved for this target. Review the interval before exporting; original capture is unchanged.";
    }

    async Task ChangeWristOffsetAsync(int index,bool remove)
    {
        try
        {
            var replacement=remove?null:_wristOffsets[index].Copy();if(replacement is not null)replacement.Enabled=!replacement.Enabled;
            await SaveWristOffsetAsync(_editedMotion,_wristEditRevision,index,replacement);
        }
        catch(Exception error){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=error.Message;}
    }

    void RefreshWristOffsetRows()
    {
        if(_wristOffsetRows is null)return;
        _wristOffsetRows.Clear(true);
        var available=_editedMotion is {Frames.Count:>1}&&HandCaptureRetargeter.Supports(_editedMotion)&&_processing is null;
        _wristOffsetRows.Add(new Button("Correct wrist…","edit_location"){Enabled=available,Clicked=()=>OpenWristOffsetEditor(),
            ToolTip="Apply a reversible position offset over a video interval for this target. No prop or model rerun needed. Does not recover missing motion."});
        for(var i=0;i<_wristOffsets.Count;i++)
        {
            var index=i;var edit=_wristOffsets[i];var row=_wristOffsetRows.AddRow();row.Spacing=6;
            row.Add(new Label($"{(edit.Hand==BoneRole.HandL?"Left":"Right")} · {edit.Start:F2}–{edit.End:F2}s · {(edit.Enabled?"Manual offset":"Disabled")}",this),1);
            row.Add(new IconButton("play_arrow",()=>{
                var range=PlaybackRange;SeekPlaybackFraction((float)(((edit.Start+edit.End)*.5-range.Start)/(range.Last-range.Start)));
            },this){FixedSize=24,IconSize=16,ToolTip="Review this interval"});
            row.Add(new IconButton("edit",()=>OpenWristOffsetEditor(index),this){FixedSize=24,IconSize=16,Enabled=available,ToolTip="Edit wrist correction"});
            row.Add(new IconButton(edit.Enabled?"visibility_off":"visibility",()=>_=ChangeWristOffsetAsync(index,false),this){FixedSize=24,IconSize=16,Enabled=available,ToolTip=edit.Enabled?"Disable correction":"Enable correction"});
            row.Add(new IconButton("delete",()=>_=ChangeWristOffsetAsync(index,true),this){FixedSize=24,IconSize=16,Enabled=available,ToolTip="Remove correction; captured motion is preserved"});
        }
    }

    internal sealed class WristOffsetDialog : Dialog
    {
        internal readonly LineEdit Start,End,Offset,Fade;
        internal readonly Label Status;
        internal readonly Button Save;
        internal readonly ComboBox Hands;
        internal BoneRole SelectedHand;
        public WristOffsetDialog(RetargetWindow owner,int index):base(owner)
        {
            var motion=owner._editedMotion;var revision=owner._wristEditRevision;
            var draft=index<0?new WristPositionOffset{Start=Math.Max(motion.Frames[0].Time,owner.PlaybackTime-.15),
                End=Math.Min(motion.Frames[^1].Time,owner.PlaybackTime+.15)}:owner._wristOffsets[index].Copy();
            SelectedHand=draft.Hand;
            if(!motion.Bones.Any(b=>b.Role==SelectedHand))SelectedHand=motion.Bones.First(b=>b.Role is BoneRole.HandL or BoneRole.HandR).Role.Value;
            Window.WindowTitle="Correct wrist position";Window.SetWindowIcon("edit_location");
            Window.Size=new Vector2(500,360);Window.MinimumSize=new Vector2(460,340);
            SetStyles($"background-color: {Theme.WidgetBackground.Hex}; color: {Theme.Text.Hex};");
            Layout=Layout.Column();Layout.Margin=16;Layout.Spacing=10;
            Layout.Add(new Label("Manual position edit for this target. Fingers and wrist rotation stay captured. Confirmed prop contacts and arm reach limits take priority.",this){WordWrap=true});
            Hands=Layout.Add(new ComboBox(this));
            foreach(var role in new[]{BoneRole.HandL,BoneRole.HandR})if(motion.Bones.Any(b=>b.Role==role))
                Hands.AddItem(role==BoneRole.HandL?"Left wrist":"Right wrist",onSelected:()=>SelectedHand=role,selected:role==SelectedHand);
            string Number(double v)=>v.ToString("R",CultureInfo.InvariantCulture);
            LineEdit Input(string label,string value){var row=Layout.AddRow();row.Spacing=8;row.Add(new Label(label,this){FixedWidth=160});return row.Add(new LineEdit(this){Text=value},1);}
            Start=Input("Start at video (s)",Number(draft.Start));End=Input("End at video (s)",Number(draft.End));
            Offset=Input("Offset X, Y, Z (cm)",string.Join(",",draft.Offset.Select(v=>Number(v*100))));
            Fade=Input("Blend at edges (s)",Number(draft.FadeSeconds));
            Layout.Add(new Label("X right · Y up · +Z toward the capture camera. This is authored correction, including during tracking loss.",this){WordWrap=true});
            Status=Layout.Add(new Label("",this){WordWrap=true});Status.SetStyles($"color: {Theme.Yellow.Hex};");
            var buttons=Layout.AddRow();buttons.AddStretchCell();buttons.Add(new Button("Cancel"){Clicked=Close});Save=buttons.Add(new Button.Primary("Apply correction"));
            Save.Clicked=async ()=>{
                try
                {
                    var offset=Offset.Text.Split(',').Select(v=>float.Parse(v.Trim(),CultureInfo.InvariantCulture)/100).ToArray();
                    if(offset.Length!=3)throw new ArgumentException("Enter three comma-separated centimetre offsets.");
                    draft.Offset=offset;draft.Hand=SelectedHand;
                    draft.Start=double.Parse(Start.Text,CultureInfo.InvariantCulture);draft.End=double.Parse(End.Text,CultureInfo.InvariantCulture);
                    draft.FadeSeconds=double.Parse(Fade.Text,CultureInfo.InvariantCulture);
                    Save.Enabled=false;await owner.SaveWristOffsetAsync(motion,revision,index,draft);await EditorPipeline.SwitchToMainThread();if(this.IsValid())Close();
                }
                catch(Exception error){await EditorPipeline.SwitchToMainThread();if(this.IsValid()){Status.Text=error.Message;Save.Enabled=true;}}
            };
        }
    }
}
