using System;
using System.IO;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

// Same centered import surface and native theme as Humanoid Rigger's ModelDropArea.
sealed class VideoDropArea : Widget
{
    readonly Action<string> import;
    bool hovering;
    public VideoDropArea(Widget parent, Action<string> import) : base(parent)
    {
        this.import=import;AcceptDrops=true;Layout=Layout.Column();Layout.Margin=20;Layout.Spacing=10;
        Layout.AddStretchCell();
        Layout.Add(new VideoIcon(this));
        Layout.Add(new Label.Subtitle("Video to motion"){Alignment=TextFlag.Center});
        var help=Layout.Add(new Label("Drop a video here, or upload from your computer or phone.",this){Alignment=TextFlag.Center,WordWrap=true});
        help.SetStyles($"color: {Theme.TextLight.Hex};");
        Layout.AddStretchCell();
    }
    static bool Supported(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v";
    public override void OnDragHover(DragEvent e)
    {hovering=e.Data.HasFileOrFolder&&Supported(e.Data.FileOrFolder);if(hovering)e.Action=DropAction.Link;Update();}
    public override void OnDragDrop(DragEvent e)
    {hovering=false;if(e.Data.HasFileOrFolder&&Supported(e.Data.FileOrFolder)){e.Action=DropAction.Link;import(e.Data.FileOrFolder);}Update();}
    public override void OnDragLeave(){hovering=false;Update();}
    protected override void OnPaint()
    {
        Paint.SetPen(hovering?Theme.Blue:Theme.Border,1);
        Paint.SetBrush(hovering?Theme.WindowBackground.LerpTo(Theme.Blue,.12f):Theme.WindowBackground);
        Paint.DrawRect(LocalRect.Shrink(1),6);
    }
    sealed class VideoIcon : Widget
    {
        public VideoIcon(Widget parent):base(parent){FixedHeight=44;}
        protected override void OnPaint(){Paint.SetPen(Theme.TextLight);Paint.DrawIcon(new Rect((Width-36)*.5f,4,36,36),"video_file",36);}
    }
}
