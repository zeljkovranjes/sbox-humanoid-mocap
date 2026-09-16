using System;
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
    internal ContactEditorDialog OpenContactEditor(ContactInterval contact=null)
    {
        if(_editedMotion is null||_processing is not null)return null;
        var dialog=new ContactEditorDialog(this,contact);dialog.Show();return dialog;
    }

    internal async Task SaveContactAsync(MotionDocument expected,int index,ContactInterval replacement)
    {
        if(expected!=_editedMotion||_processing is not null)throw new InvalidOperationException("The capture changed. Reopen contact editing.");
        var candidate=ContactAuthoring.Replace(expected,index,replacement);
        if(new PropContactMotion(candidate).UnsupportedReason(replacement) is {} reason)throw new ArgumentException(reason);
        var source=_rawMotion.Copy();source.Contacts=candidate.Contacts;
        _editedMotion=_appliedCleanup is null?source:MotionCleanup.Apply(source,_appliedCleanup);
        RefreshContacts();await RefreshMocapPreviewAsync();
    }

    internal sealed class ContactEditorDialog : Dialog
    {
        internal readonly LineEdit Start,End,Target;
        internal readonly Label Status;
        internal readonly Button Save;
        internal readonly Button Place;
        internal readonly Checkbox HoldOrientation;
        internal readonly Checkbox Sliding;
        internal readonly MocapContactKeys Keys;
        internal FingerContactDialog FingerDialog;
        internal readonly Button Fingers;
        readonly MotionDocument _motion;
        readonly ContactInterval _draft;

        public ContactEditorDialog(RetargetWindow owner,ContactInterval contact):base(owner)
        {
            _motion=owner._editedMotion;
            var index=contact is null?-1:_motion.Contacts.IndexOf(contact);
            _draft=index<0?new(){Start=_motion.Frames[0].Time,End=_motion.Frames[^1].Time,Review=ContactReview.Suggested}:
                _motion.Copy().Contacts[index];
            Window.WindowTitle=index<0?"Add wrist contact":"Edit wrist contact";Window.SetWindowIcon("touch_app");
            Window.Size=new Vector2(560,430);Window.MinimumSize=new Vector2(520,420);
            SetStyles($"background-color: {Theme.WidgetBackground.Hex}; color: {Theme.Text.Hex};");
            Layout=Layout.Column();Layout.Margin=16;Layout.Spacing=10;
            Layout.Add(new Label("Place an object-local wrist anchor and review it against the video. Saved edits return to Suggested; confirm them in Contact review.",this){WordWrap=true});
            var hands=Layout.Add(new ComboBox(this));
            foreach(var bone in _motion.Bones.Where(b=>b.Role is BoneRole.HandL or BoneRole.HandR))
            {var name=bone.Name;if(string.IsNullOrEmpty(_draft.Bone))_draft.Bone=name;hands.AddItem(name,onSelected:()=>{if(_draft.Bone!=name)_draft.LocalRotation=null;_draft.Bone=name;},selected:name==_draft.Bone);}
            var props=Layout.Add(new ComboBox(this));
            foreach(var prop in _motion.Objects)foreach(var bone in prop.Bones)
            {
                var id=prop.Id;var name=bone.Name;if(string.IsNullOrEmpty(_draft.Object)){_draft.Object=id;_draft.ObjectBone=name;}
                props.AddItem(id+" / "+name,onSelected:()=>{if(_draft.Object!=id||_draft.ObjectBone!=name)_draft.LocalRotation=null;_draft.Object=id;_draft.ObjectBone=name;},
                    selected:id==_draft.Object&&(name==_draft.ObjectBone||string.IsNullOrEmpty(_draft.ObjectBone)&&bone.Parent<0));
            }
            LineEdit Input(string title,string value){var row=Layout.AddRow();row.Spacing=8;row.Add(new Label(title,this){FixedWidth=160});return row.Add(new LineEdit(this){Text=value},1);}
            Start=Input("Start at video (s)",_draft.Start.ToString("R",CultureInfo.InvariantCulture));
            End=Input("End at video (s)",_draft.End.ToString("R",CultureInfo.InvariantCulture));
            Target=Input("Local X, Y, Z (m)",string.Join(",",_draft.LocalTarget.Select(v=>v.ToString("R",CultureInfo.InvariantCulture))));
            HoldOrientation=Layout.Add(new Checkbox("Hold wrist orientation relative to prop"){Value=_draft.LocalRotation is not null});
            HoldOrientation.ToolTip="Optional for a rigid grip. Place an anchor below, then review and confirm. Captured finger articulation remains unchanged.";
            var place=Place=Layout.Add(new Button("Use wrist at interval midpoint","my_location"));
            place.ToolTip="Use the captured wrist at the nearest midpoint sample. This places a manual anchor; it does not detect a grip. Sliding position keys are preserved.";
            Status=new Label("",this){WordWrap=true};
            Status.SetStyles($"color: {Theme.Yellow.Hex};");
            Sliding=Layout.Add(new Checkbox("Sliding contact · animate the local wrist target"){Value=_draft.Sliding});
            Sliding.ToolTip="Uses at least two authored position keys. Turning this off keeps the keys for later and uses the fixed anchor above.";
            void RefreshMode()
            {
                _draft.Sliding=Sliding.Value;Keys.Visible=Sliding.Value;Target.ReadOnly=Sliding.Value;
                props.Enabled=hands.Enabled=_draft.TargetKeys.Count==0&&_draft.FingerTargets.Count==0;
                props.ToolTip=hands.ToolTip=!props.Enabled?"Remove the draft's position keys and finger points before changing their hand or object coordinate frame.":"";
                Window.Size=new Vector2(560,Sliding.Value?700:500);
            }
            Keys=Layout.Add(new MocapContactKeys(this,_motion,_draft,Read,()=>owner.PlaybackTime,message=>Status.Text=message,RefreshMode));
            Sliding.Clicked=RefreshMode;RefreshMode();
            var fingerActions=Layout.AddRow();fingerActions.Spacing=8;
            Fingers=fingerActions.Add(new Button("Finger contact points…","touch_app"),1);
            fingerActions.Add(new Button("Clear points","clear"){ToolTip="Remove all target-specific finger points from this draft. Cancel restores the saved contact.",Clicked=()=>{
                _draft.FingerTargets.Clear();RefreshMode();Status.Text="Finger points removed from this draft. Save to keep the change, or cancel to restore them.";
            }});
            Fingers.Clicked=()=>{try{Read();FingerDialog=new(owner,this,_draft,()=>{RefreshMode();Status.Text=$"{_draft.FingerTargets.Count} target-specific finger points. Save and review before confirming.";});FingerDialog.Show();}catch(Exception error){Status.Text=error.Message;}};
            Layout.Add(Status);
            place.Clicked=()=>{try{Read();var time=ContactAuthoring.PlaceAtWrist(_motion,_draft,HoldOrientation.Value);Target.Text=string.Join(",",_draft.LocalTarget.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));Status.Text=$"Manual anchor placed from the wrist at {time:F3} s.";}catch(Exception e){Status.Text=e.Message;}};
            var buttons=Layout.AddRow();buttons.AddStretchCell();buttons.Add(new Button("Cancel"){Clicked=Close});Save=buttons.Add(new Button.Primary("Save suggestion"));
            Save.Clicked=async ()=>{
                try{Read();if(Sliding.Value&&Keys.HasUnappliedChanges)throw new ArgumentException("Add or update the edited sliding key before saving.");
                    if(HoldOrientation.Value&&_draft.LocalRotation is null)throw new ArgumentException("Use wrist at interval midpoint to place the orientation anchor.");
                    if(!HoldOrientation.Value)_draft.LocalRotation=null;
                    _draft.Review=ContactReview.Suggested;_draft.Reason="Manually placed/edited wrist contact; requires review against the video.";
                    Save.Enabled=false;await owner.SaveContactAsync(_motion,index,_draft);await EditorPipeline.SwitchToMainThread();if(this.IsValid())Close();}
                catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid()){Status.Text=e.Message;Save.Enabled=true;}}
            };
        }
        void Read()
        {
            _draft.Start=double.Parse(Start.Text,CultureInfo.InvariantCulture);_draft.End=double.Parse(End.Text,CultureInfo.InvariantCulture);
            _draft.LocalTarget=MotionDocument.A(Vector(Target));
        }
    }
}
