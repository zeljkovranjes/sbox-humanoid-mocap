using System;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

/// <summary>A rounded dark-gray panel on the gray window, the same surface as the first-load drop box. Its column
/// layout holds an optional header row (icon, title, then controls) above the content.</summary>
sealed class MocapCard : Widget
{
    public MocapCard(Widget parent,bool row=false):base(parent){Layout=row?Layout.Row():Layout.Column();Layout.Margin=8;Layout.Spacing=8;}
    /// <summary>Adds the header row: an icon and a title, then whatever the caller adds to the returned row.</summary>
    public Layout Header(string icon,string title)
    {
        var row=Layout.AddRow();row.Spacing=6;
        row.Add(new CardIcon(this,icon));
        var label=row.Add(new Label(title,this));label.SetStyles("font-weight: 600;");
        return row;
    }
    protected override void OnPaint()
    {
        Paint.Antialiasing=true;
        Paint.SetPen(Theme.ControlBackground.Lighten(.25f),1);Paint.SetBrush(Theme.ControlBackground);
        Paint.DrawRect(LocalRect.Shrink(1),6);
    }
    sealed class CardIcon : Widget
    {
        readonly string icon;
        public CardIcon(Widget parent,string icon):base(parent){this.icon=icon;FixedSize=18;}
        protected override void OnPaint(){Paint.SetPen(Theme.Green);Paint.DrawIcon(LocalRect,icon,16);}
    }
}

/// <summary>A small round light before the status text: amber while working, green when ready, gray otherwise.</summary>
sealed class StatusDot : Widget
{
    Color color=Theme.TextLight;
    public StatusDot(Widget parent):base(parent){FixedSize=10;}
    public Color Color{get=>color;set{if(color==value)return;color=value;Update();}}
    protected override void OnPaint(){Paint.Antialiasing=true;Paint.ClearPen();Paint.SetBrush(color);Paint.DrawRect(LocalRect.Shrink(1),4);}
}
