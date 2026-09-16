using System;
using System.Globalization;
using System.Linq;
using Editor;
using Sandbox;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Motion;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    internal sealed class FingerContactDialog : Dialog
    {
        internal readonly LineEdit Point,Anchor,Limit,Time;
        internal readonly Button Sample,ApplyPoint,Remove;
        internal readonly Label Status;
        BoneRole role;

        internal FingerContactDialog(RetargetWindow owner,Widget parent,ContactInterval draft,Action changed):base(parent)
        {
            var preview=owner._bakedPreview??throw new ArgumentException("Wait for the target preview before placing finger contacts.");
            var motion=owner._editedMotion;var key=MocapAdjustmentStore.TargetKey(preview.Target,true);
            var rig=preview.Target.Rig;var objectId=draft.Object;var objectBone=draft.ObjectBone;var hand=draft.Bone;
            var left=motion.Bones.Single(b=>b.Name==hand).Role==BoneRole.HandL;
            Window.WindowTitle="Finger contact point";Window.SetWindowIcon("touch_app");Window.Size=new Vector2(560,480);Window.MinimumSize=new Vector2(520,460);
            SetStyles($"background-color: {Theme.WidgetBackground.Hex}; color: {Theme.Text.Hex};");
            Layout=Layout.Column();Layout.Margin=16;Layout.Spacing=10;
            Layout.Add(new Label("Optional correction for this target rig only. Bone endpoints are placement guides, not skin surfaces. Save the parent contact, inspect its point markers, then confirm.",this){WordWrap=true});
            var picker=Layout.Add(new ComboBox(this));
            LineEdit Input(string title,string value){var row=Layout.AddRow();row.Spacing=8;row.Add(new Label(title,this){FixedWidth=180});return row.Add(new LineEdit(this){Text=value},1);}
            Point=Input("Distal-local point (m)","0,0,0");Anchor=Input("Object-local target (m)","0,0,0");Limit=Input("Max change per joint (°)","25");
            Time=Input("Sample at video (s)",Math.Clamp(owner.PlaybackTime,draft.Start,draft.End).ToString("R",CultureInfo.InvariantCulture));
            var placement=Layout.AddRow();placement.Spacing=8;
            placement.Add(new Button("Use bone endpoint","my_location"){Clicked=()=>Try(()=>{
                var point=FingerContactCorrection.BoneEndpoint(rig,role,preview.Target.UpAxis)??throw new ArgumentException("This rig has no endpoint metadata. Enter a distal-bone-local point explicitly.");
                Point.Text=Coordinates(point);Status.Text="Bone endpoint filled. This is not a measured fingertip pad.";
            })});
            Sample=placement.Add(new Button("Use preview point","touch_app"));
            Sample.Clicked=()=>Try(()=>{
                Check();var time=double.Parse(Time.Text,CultureInfo.InvariantCulture);
                if(!double.IsFinite(time)||time<draft.Start||time>draft.End)throw new ArgumentException("Sample inside the contact interval.");
                var frame=Math.Clamp((int)Math.Round((time-motion.Frames[0].Time)*preview.Clip.Fps),0,preview.Clip.SolvedFrames.Count-1);
                var sampled=Math.Min(motion.Frames[0].Time+frame/(double)preview.Clip.Fps,motion.Frames[^1].Time);
                if(sampled<draft.Start||sampled>draft.End)throw new ArgumentException("Nearest animation sample is outside the contact interval.");
                var world=new Pose(preview.Clip.SolvedFrames[frame]).ToWorld(rig.Skeleton);
                var targetPoint=XForm.Compose(world[rig.BoneForRole(role).Value],new(Vector(Point)*preview.Placement.Units,System.Numerics.Quaternion.Identity)).Pos;
                var props=new PropContactMotion(motion);var track=motion.Objects.Single(p=>p.Id==objectId);
                var b=string.IsNullOrEmpty(objectBone)?0:track.Bones.FindIndex(b=>b.Name==objectBone);
                if(b<0||!props.TrySample(objectId,sampled,out var transforms,out var available)||!available[b])throw new ArgumentException("Prop track unavailable at this time.");
                var inObject=XForm.Compose(preview.Placement.Transform(transforms[b]).Inverse(),new(targetPoint,System.Numerics.Quaternion.Identity)).Pos/preview.Placement.Units;
                Anchor.Text=Coordinates(inObject);Time.Text=sampled.ToString("R",CultureInfo.InvariantCulture);
                Status.Text="Target placed from the preview pose. Apply it to the draft, save and review against the video.";
            });
            Status=Layout.Add(new Label("",this){WordWrap=true});Status.SetStyles($"color: {Theme.Yellow.Hex};");
            var buttons=Layout.AddRow();Remove=buttons.Add(new Button("Remove point","delete"));buttons.AddStretchCell();buttons.Add(new Button("Cancel"){Clicked=Close});
            ApplyPoint=buttons.Add(new Button.Primary("Apply to contact draft"));
            ApplyPoint.Clicked=()=>Try(()=>{
                Check();var point=new FingerContactTarget{Role=role,TargetKey=key,LocalPoint=MotionDocument.A(Vector(Point)),LocalTarget=MotionDocument.A(Vector(Anchor)),MaximumDegrees=float.Parse(Limit.Text,CultureInfo.InvariantCulture)};
                var referenceTime=double.Parse(Time.Text,CultureInfo.InvariantCulture);
                if(!double.IsFinite(referenceTime)||referenceTime<draft.Start||referenceTime>draft.End)throw new ArgumentException("Place the point at a time inside the contact interval.");
                point.SlideOrigin=MotionDocument.A(ContactSolver.LocalTarget(draft,referenceTime));
                var next=draft.FingerTargets.Where(t=>t.TargetKey!=key||t.Role!=role).Append(point).ToList();
                var candidate=motion.Copy();var contact=new ContactInterval{Bone=hand,Object=objectId,ObjectBone=objectBone,Start=draft.Start,End=draft.End,FingerTargets=next};
                candidate.Contacts.Clear();candidate.Contacts.Add(contact);candidate.Validate();
                draft.FingerTargets=next;changed();Close();
            });
            Remove.Clicked=()=>Try(()=>{Check();draft.FingerTargets.RemoveAll(t=>t.TargetKey==key&&t.Role==role);changed();Close();});
            foreach(var name in new[]{"Index","Middle","Ring","Pinky","Thumb"})
            {
                var r=Enum.Parse<BoneRole>(name+"Dist"+(left?"L":"R"));if(rig.BoneForRole(r) is null)continue;
                picker.AddItem(name,onSelected:()=>Select(r),selected:name=="Index");
            }
            Select(Enum.Parse<BoneRole>("IndexDist"+(left?"L":"R")));
            void Select(BoneRole selected)
            {
                role=selected;var existing=draft.FingerTargets.FirstOrDefault(t=>t.TargetKey==key&&t.Role==role);
                var initial=existing is null?FingerContactCorrection.BoneEndpoint(rig,role,preview.Target.UpAxis)??System.Numerics.Vector3.Zero:MotionDocument.V(existing.LocalPoint);
                var referenceTime=double.TryParse(Time.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var typed)&&double.IsFinite(typed)&&typed>=draft.Start&&typed<=draft.End
                    ?typed:Math.Clamp(owner.PlaybackTime,draft.Start,draft.End);
                Time.Text=referenceTime.ToString("R",CultureInfo.InvariantCulture);
                Point.Text=Coordinates(initial);Anchor.Text=existing is null?"0,0,0":Coordinates(FingerContactCorrection.TargetAt(draft,existing,referenceTime));
                Limit.Text=(existing?.MaximumDegrees??25).ToString(CultureInfo.InvariantCulture);Remove.Enabled=existing is not null;
                Status.Text=existing is null?"Place an object-local target before adding a contact point.":"Editing a saved point for this target rig.";
            }
            void Check()
            {
                if(owner._bakedPreview!=preview||owner._editedMotion!=motion||draft.Object!=objectId||draft.ObjectBone!=objectBone||draft.Bone!=hand)
                    throw new ArgumentException("The preview or contact binding changed. Reopen finger point editing.");
            }
            void Try(Action action){try{action();}catch(Exception error){Status.Text=error.Message;}}
        }
        static string Coordinates(System.Numerics.Vector3 v)=>string.Join(",",new[]{v.X,v.Y,v.Z}.Select(x=>x.ToString("R",CultureInfo.InvariantCulture)));
    }
}
