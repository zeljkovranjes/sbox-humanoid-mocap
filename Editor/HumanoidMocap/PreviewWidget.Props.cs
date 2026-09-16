using System;
using HumanoidMocap.Motion;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class PreviewWidget
{
    public PropContactMotion CaptureProps { get; set; }
    public CapturePlacement PropPlacement { get; set; }
    public int PropBoneOverlayCount { get; private set; }

    void DrawPropBones()
    {
        PropBoneOverlayCount=0;
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
        }
    }
}
