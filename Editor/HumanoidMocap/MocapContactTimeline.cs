using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

// Uses the exposed Widget/Paint/Menu APIs and Theme colors used by sbox-public's
// scrub bars. No MovieMaker session or engine-internal timeline dependency.
sealed class MocapContactTimeline : Widget
{
    MotionDocument motion;
    IReadOnlyList<WristPositionOffset> wristOffsets=Array.Empty<WristPositionOffset>();
    double playhead;
    public Action<float> Seek { get; set; }
    public Action<MotionDocument,int> Edit { get; set; }
    public Action<MotionDocument,int,ContactReview> Review { get; set; }
    public Action<MotionDocument,int,double,double> ChangeRange { get; set; }
    public Action<MotionDocument,WristPositionOffset> EditWrist { get; set; }

    public MocapContactTimeline(Widget parent):base(parent)
    {
        FixedHeight=36;MouseTracking=true;Visible=false;
        ToolTip="Contact intervals: yellow needs review, green confirmed, gray disabled. Click to seek; double-click to edit; right-click for review and timing.";
    }
    public void SetMotion(MotionDocument value,IReadOnlyList<WristPositionOffset> edits=null)
    {motion=value;wristOffsets=edits?.ToArray()??Array.Empty<WristPositionOffset>();Visible=value is not null&&(value.Contacts.Count>0||wristOffsets.Count>0);Update();}
    public void SetPlayhead(double time)
    {if(playhead==time)return;playhead=time;Update();}
    float X(double time)
    {
        if(motion is null)return 6;
        var start=motion.Frames[0].Time;var duration=motion.Frames[^1].Time-start;
        return 6+(float)(duration>0?Math.Clamp((time-start)/duration,0,1):0)*Math.Max(1,Width-12);
    }
    float Fraction(float x)=>Math.Clamp((x-6)/Math.Max(1,Width-12),0,1);
    int Lane(ContactInterval contact)=>motion.Bones.FirstOrDefault(b=>b.Name==contact.Bone)?.Role==BoneRole.HandR?1:0;
    string Side(ContactInterval contact)=>motion.Bones.FirstOrDefault(b=>b.Name==contact.Bone)?.Role switch
        {BoneRole.HandL=>"L",BoneRole.HandR=>"R",_=>contact.Bone};
    internal Rect ContactRect(int index)
    {
        var c=motion.Contacts[index];var start=X(c.Start);var end=X(c.End);
        return new(start,2+Lane(c)*18,Math.Max(2,end-start),14);
    }
    int[] Hits(Vector2 position)=>motion is null?Array.Empty<int>():Enumerable.Range(0,motion.Contacts.Count)
        .Where(i=>ContactRect(i).Grow(2).IsInside(position)).ToArray();
    internal Rect WristRect(int index)
    {
        var edit=wristOffsets[index];var start=X(edit.Start);var end=X(edit.End);
        return new(start,15+(edit.Hand==BoneRole.HandR?18:0),Math.Max(2,end-start),3);
    }
    int[] WristHits(Vector2 position)=>Enumerable.Range(0,wristOffsets.Count).Where(i=>WristRect(i).Grow(2).IsInside(position)).ToArray();
    protected override void OnPaint()
    {
        Paint.ClearPen();Paint.SetBrush(Theme.WindowBackground);Paint.DrawRect(LocalRect,3);
        if(motion is null)return;
        for(var i=0;i<motion.Contacts.Count;i++)
        {
            var c=motion.Contacts[i];var rect=ContactRect(i);
            var color=c.Review==ContactReview.Suggested?Theme.Yellow:c.Review==ContactReview.Confirmed?Theme.Green:Theme.TextLight;
            Paint.SetPen(color,1);Paint.SetBrush(color.WithAlpha(c.Review==ContactReview.Disabled?.1f:.25f));Paint.DrawRect(rect,2);
            var state=c.Review==ContactReview.Suggested?"?":c.Review==ContactReview.Confirmed?"✓":"×";
            var prefix=Side(c)+" "+state;
            var text=prefix+" · "+c.Object;
            if(Paint.MeasureText(text).x>rect.Width-6)text=prefix;
            if(Paint.MeasureText(text).x<=rect.Width-6)Paint.DrawText(rect.Shrink(3,0),text,TextFlag.LeftCenter);
        }
        for(var i=0;i<wristOffsets.Count;i++)
        {
            Paint.ClearPen();Paint.SetBrush(wristOffsets[i].Enabled?Theme.Blue:Theme.TextLight.WithAlpha(.35f));Paint.DrawRect(WristRect(i),1);
        }
        Paint.SetPen(Theme.Text.WithAlpha(.8f),1);var x=X(playhead);Paint.DrawLine(new Vector2(x,0),new Vector2(x,Height));
    }
    protected override void OnMouseMove(MouseEvent e)
    {
        var hits=Hits(e.LocalPosition);var wrists=WristHits(e.LocalPosition);Cursor=CursorShape.Finger;
        ToolTip=hits.Length+wrists.Length==0?"Click to seek. Yellow contacts need review; green are confirmed; blue marks manual wrist correction; gray is disabled.":
            string.Join("\n",hits.Select(i=>{var c=motion.Contacts[i];return $"{c.Bone} → {c.Object} · {c.Start:F3}–{c.End:F3} s · {c.Review}";})
                .Concat(wrists.Select(i=>{var c=wristOffsets[i];return $"{(c.Hand==BoneRole.HandL?"Left":"Right")} wrist · {c.Start:F3}–{c.End:F3} s · {(c.Enabled?"Manual position correction":"Disabled correction")}";})))+
            "\nClick to seek; double-click to edit; right-click to review or change timing.";
    }
    protected override void OnMousePress(MouseEvent e)
    {
        var hits=Hits(e.LocalPosition);var wrists=WristHits(e.LocalPosition);var expected=motion;
        if(expected is null)return;
        if(e.LeftMouseButton)
        {
            Seek?.Invoke(Fraction(e.LocalPosition.x));
            if(e.IsDoubleClick&&hits.Length+wrists.Length==1)
            {if(wrists.Length==1)EditWrist?.Invoke(expected,wristOffsets[wrists[0]]);else Edit?.Invoke(expected,hits[0]);}
            e.Accepted=true;
        }
        else if(e.RightMouseButton&&hits.Length+wrists.Length>0)
        {
            var menu=new Menu();var time=Math.Clamp(playhead,expected.Frames[0].Time,expected.Frames[^1].Time);
            Seek?.Invoke(Fraction(X(time)));
            foreach(var index in hits)
            {
                var c=expected.Contacts[index];var supported=new PropContactMotion(expected).UnsupportedReason(c) is null;
                menu.AddHeading($"{c.Bone} → {c.Object} · {c.Review}");
                menu.AddOption("Edit contact…","edit",()=>Edit?.Invoke(expected,index)).Enabled=supported;
                menu.AddOption("Confirm","check",()=>Review?.Invoke(expected,index,ContactReview.Confirmed)).Enabled=supported;
                menu.AddOption("Disable","block",()=>Review?.Invoke(expected,index,ContactReview.Disabled));
                menu.AddOption($"Start at playhead ({time:F3} s)","first_page",()=>ChangeRange?.Invoke(expected,index,time,c.End)).Enabled=supported&&time<c.End;
                menu.AddOption($"End at playhead ({time:F3} s)","last_page",()=>ChangeRange?.Invoke(expected,index,c.Start,time)).Enabled=supported&&time>c.Start;
            }
            foreach(var index in wrists)
            {
                var edit=wristOffsets[index];menu.AddHeading($"{(edit.Hand==BoneRole.HandL?"Left":"Right")} wrist · manual correction");
                menu.AddOption("Edit wrist correction…","edit_location",()=>EditWrist?.Invoke(expected,edit));
            }
            menu.OpenAtCursor();e.Accepted=true;
        }
    }
}

public sealed partial class RetargetWindow
{
    MocapContactTimeline _contactTimeline;
    async Task ChangeContactRangeAsync(MotionDocument expected,int index,double start,double end)
    {
        try
        {
            if(expected!=_editedMotion||_processing is not null)return;
            var contact=expected.Copy().Contacts[index];contact.Start=start;contact.End=end;
            contact.Review=ContactReview.Suggested;contact.Reason="Interval edited on the timeline; review against the video.";
            await SaveContactAsync(expected,index,contact);
        }
        catch(Exception error){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=error.Message;}
    }
}
