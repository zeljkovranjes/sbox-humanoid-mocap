using System;
using System.Linq;
using HumanoidMocap.Maths;
using HumanoidMocap.Motion;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class PreviewWidget
{
    public PropContactMotion CaptureProps { get; set; }
    public CapturePlacement PropPlacement { get; set; }
    public int PropBoneOverlayCount { get; private set; }
    public string ContactTargetKey { get; set; }
    public int FingerContactOverlayCount { get; private set; }

    void DrawPropBones()
    {
        PropBoneOverlayCount=0;
        FingerContactOverlayCount=0;
        if(CaptureProps is null||_clip is null||CaptureProps.Objects.Count==0)return;
        var time=Math.Min(CaptureProps.StartTime+CurrentFrame/(double)_clip.Fps,CaptureProps.EndTime);
        UpdateGizmoInputs(false);
        using(Gizmo.Scope("HumanoidMocap.PropBones"))
        {
            Gizmo.Transform=Transform.Zero;Gizmo.Draw.IgnoreDepth=true;Gizmo.Draw.LineThickness=2;
            Gizmo.Draw.Color=new Color(.25f,.85f,1f,1f);
            foreach(var prop in CaptureProps.Objects)
            {
                if(!CaptureProps.TrySample(prop.Id,time,out var world,out var available))continue;
                for(var b=0;b<world.Length;b++)
                {
                    if(!available[b])continue;
                    var point=RigWorldToEngine(PropPlacement.Transform(world[b])).Position;
                    Gizmo.Draw.SolidSphere(point,.18f,8,8);
                    var parent=prop.Bones[b].Parent;
                    if(parent>=0)Gizmo.Draw.Line(RigWorldToEngine(PropPlacement.Transform(world[parent])).Position,point);
                    PropBoneOverlayCount++;
                }
            }
            foreach(var contact in CaptureProps.Contacts.Where(c=>c.Review!=ContactReview.Disabled&&time>=c.Start&&time<=c.End))
            {
                if(!CaptureProps.TrySample(contact.Object,time,out var propWorld,out var available))continue;
                var prop=CaptureProps.Objects.Single(p=>p.Id==contact.Object);
                var part=string.IsNullOrEmpty(contact.ObjectBone)?0:prop.Bones.FindIndex(b=>b.Name==contact.ObjectBone);
                if(part<0||!available[part])continue;
                foreach(var target in contact.FingerTargets.Where(t=>t.TargetKey==ContactTargetKey))
                {
                    if(_rig.BoneForRole(target.Role) is not int bone)continue;
                    var finger=RigWorldToEngine(XForm.Compose(_worldScratch[bone],new(MotionDocument.V(target.LocalPoint)*PropPlacement.Units,System.Numerics.Quaternion.Identity))).Position;
                    var goal=RigWorldToEngine(PropPlacement.Transform(XForm.Compose(propWorld[part],new(FingerContactCorrection.TargetAt(contact,target,time),System.Numerics.Quaternion.Identity)))).Position;
                    Gizmo.Draw.Color=contact.Review==ContactReview.Confirmed?new Color(.3f,1f,.4f):new Color(1f,.85f,.2f);
                    Gizmo.Draw.Line(finger,goal);Gizmo.Draw.SolidSphere(goal,.14f,8,8);
                    Gizmo.Draw.Color=new Color(1f,.35f,.8f);Gizmo.Draw.SolidSphere(finger,.12f,8,8);FingerContactOverlayCount++;
                }
            }
        }
    }
}
