using System;
using System.IO;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

// First-load surface, styled after Humanoid Rigger's ModelDropArea: a dark-gray
// rounded box on the gray window, centered prompt and a green choose button.
sealed class VideoDropArea : Widget
{
    readonly Action<string> import;
    int hover;
    public VideoDropArea(Widget parent, Action<string> import, Action chooseVideo, Action phoneUpload) : base(parent)
    {
        this.import=import;AcceptDrops=true;Layout=Layout.Column();Layout.Margin=12;Layout.Spacing=8;
        Layout.AddStretchCell();
        var row=Layout.AddRow();row.AddStretchCell();var center=row.AddColumn();center.Spacing=12;
        center.Add(new VideoIcon(this));
        center.Add(new Label.Subtitle("Video to motion"){Alignment=TextFlag.Center});
        var help=center.Add(new Label("Please drag and drop a video here (.mp4, .mov)",this){Alignment=TextFlag.Center});
        help.SetStyles($"color: {Theme.TextLight.Hex};");
        center.Add(new Label("or",this){Alignment=TextFlag.Center});
        var choice=center.AddRow();choice.Spacing=8;choice.AddStretchCell();
        choice.Add(new Button.Primary("Choose Video"){Icon="video_file",Tint=Theme.Green,MinimumWidth=140,FixedHeight=32,Clicked=chooseVideo});
        choice.Add(new Button("From phone","qr_code_2"){FixedHeight=32,Clicked=phoneUpload});
        choice.AddStretchCell();
        row.AddStretchCell();Layout.AddStretchCell();
    }
    static bool Supported(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v";
    public override void OnDragHover(DragEvent e)
    {bool valid=e.Data.HasFileOrFolder&&Supported(e.Data.FileOrFolder);hover=valid?1:-1;if(valid)e.Action=DropAction.Link;Update();}
    public override void OnDragDrop(DragEvent e)
    {hover=0;if(e.Data.HasFileOrFolder&&Supported(e.Data.FileOrFolder)){e.Action=DropAction.Link;import(e.Data.FileOrFolder);}Update();}
    public override void OnDragLeave(){hover=0;Update();}
    protected override void OnPaint()
    {
        Paint.SetPen(hover==1?Theme.Green:hover<0?Theme.Red:Theme.ControlBackground.Lighten(.2f),1);
        Paint.SetBrush(hover==1?Theme.Green.WithAlpha(.06f):Paint.HasMouseOver?Theme.ControlBackground.Lighten(.3f):Theme.ControlBackground);
        Paint.DrawRect(LocalRect.Shrink(1),4);
    }
    sealed class VideoIcon : Widget
    {
        public VideoIcon(Widget parent):base(parent){FixedHeight=48;}
        protected override void OnPaint(){Paint.SetPen(Theme.TextLight);Paint.DrawIcon(new Rect((Width-40)*.5f,4,40,40),"video_file",40);}
    }
}
