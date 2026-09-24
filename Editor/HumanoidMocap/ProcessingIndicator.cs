using System;
using System.Text.RegularExpressions;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

/// <summary>Stands in the preview's place while a capture runs, at the preview's size: a spinner with "Processing…"
/// and the current step, or, while models download, a progress bar with the file and how much is left in all.
/// Styled like the first-load drop box.</summary>
sealed class ProcessingIndicator : Widget
{
    static readonly Regex Download=new(@"^Downloading (?<name>.+?) · (?<percent>\d+)%(?: · (?<left>.+) left)?$",RegexOptions.CultureInvariant);
    string _status="";string _file;float? _progress;string _left;
    /// <summary>False once the capture has stopped: the last message stays, without the spinner.</summary>
    public bool Busy { get; set; }=true;
    public ProcessingIndicator(Widget parent):base(parent){MinimumSize=new Vector2(120,120);}

    /// <summary>A worker or editor progress line; download lines drive the bar, anything else is the step shown under the spinner.</summary>
    public void SetMessage(string message)
    {
        if(Download.Match(message) is { Success: true } m)
        {
            _file=m.Groups["name"].Value;_progress=Math.Clamp(int.Parse(m.Groups["percent"].Value)/100f,0,1);
            _left=m.Groups["left"].Success?m.Groups["left"].Value:null;
        }
        else{_progress=null;_file=null;_left=null;_status=message;}
        Update();
    }
    /// <summary>Called every editor frame by the workspace, to turn the spinner.</summary>
    public void Tick(){if(Busy&&_progress is null&&Visible)Update();}

    protected override void OnPaint()=>DrawInto(LocalRect);
    /// <summary>Paints the current state into <paramref name="area"/>; also used to render it to an image.</summary>
    public void DrawInto(Rect area)
    {
        Paint.Antialiasing=true;
        Paint.SetPen(Theme.ControlBackground.Lighten(.2f),1);Paint.SetBrush(Theme.ControlBackground);
        Paint.DrawRect(area.Shrink(1),4);
        var center=area.Center;var width=Math.Min(320f,area.Width-40f);
        if(_progress is float progress&&Busy)
        {
            Paint.SetDefaultFont(10,600);Paint.SetPen(Theme.Text);
            Paint.DrawText(new Rect(center.x-width/2,center.y-44,width,22),"Downloading models",TextFlag.Center);
            var bar=new Rect(center.x-width/2,center.y-8,width,10);
            Paint.ClearPen();Paint.SetBrush(Color.White.WithAlpha(.08f));Paint.DrawRect(bar,5);
            if(progress>0){Paint.SetBrush(Theme.Green);Paint.DrawRect(new Rect(bar.Left,bar.Top,Math.Max(10,bar.Width*progress),bar.Height),5);}
            Paint.SetDefaultFont();Paint.SetPen(Theme.TextLight);
            Paint.DrawText(new Rect(center.x-width/2,center.y+10,width,20),$"{_file} · {progress*100:0}%",TextFlag.Center);
            if(_left is not null)Paint.DrawText(new Rect(center.x-width/2,center.y+30,width,20),$"{_left} left to download",TextFlag.Center);
            return;
        }
        if(Busy)
        {
            var angle=(float)(RealTime.Now*300%360);
            Paint.SetPen(Color.White.WithAlpha(.08f),4);Paint.DrawArc(new Vector2(center.x,center.y-30),new Vector2(18,18),0,360);
            Paint.SetPen(Theme.Green,4);Paint.DrawArc(new Vector2(center.x,center.y-30),new Vector2(18,18),angle,100);
            Paint.SetDefaultFont(10,600);Paint.SetPen(Theme.Text);
            Paint.DrawText(new Rect(center.x-width/2,center.y+2,width,22),"Processing…",TextFlag.Center);
        }
        Paint.SetDefaultFont();Paint.SetPen(Theme.TextLight);
        Paint.DrawText(new Rect(center.x-width/2,center.y+(Busy?26:-20),width,40),_status,TextFlag.Center|TextFlag.WordWrap);
    }
}
