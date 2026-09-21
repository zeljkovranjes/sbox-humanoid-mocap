using System;
using System.Linq;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    internal sealed record HandModelChoice(string Backend,string ModelPath=null);
    /// <summary>Exposed ListView and ControlSheet components from sbox-public's
    /// model and graph inspectors. Inspecting a backend never loads its model.</summary>
    internal sealed class HandBackendDialog : Dialog
    {
        sealed record Backend(string Id,string Name,string Weight,Color Color,string Status,string Download,string Output,string Requirements,string Limit);
        readonly Backend[] _backends =
        {
            new("mediapipe","MediaPipe only","Light",Theme.Green,"Optional · experimental C#","7.8 MB","Hand landmarks and fitted finger motion","C# worker prepared on first use","Lightweight alternative. Wrist depth uses an assumed plane; tracking loss holds the last pose. Not the default FPS pose model."),
            new("mobilehand","MobileHand","Light*",Theme.Green,"Experimental C# / native CPU","15.2 MB + 7.8 MB crop detector","Wrist and finger rotations, hand shape","C# worker prepared on first use","Small image model. Tests show pose jumps and unstable depth. Occluded hands depend on the crop detector. Review carefully before export."),
            new("wildhands","WildHands","Medium*",Color.Lerp(Theme.Blue,Theme.Text,.4f),"Faster · head-mounted footage only · experimental C# / native CPU","855 MB + 7.8 MB crop detector","WildHands pose · MediaPipe hand detection","C# worker prepared on first use","About four times faster than WiLoR, but trained on head-mounted footage: on hands filmed from outside it produced wrong finger poses and hand orientation. Use only for first-person recordings and review against the video."),
            new("wilor","WiLoR","Heavy*",Theme.Yellow,"FPS default · experimental C# / native CPU","2.56 GB + 7.8 MB crop detector","WiLoR pose · MediaPipe hand detection","C# worker prepared on first use","MediaPipe locates the hands; WiLoR reconstructs wrist and finger rotations. Closest to the video in every comparison here, from head-mounted and outside cameras. Slowest option: a third of a second to a second per hand per frame, depending on the processor. Depth and occluded hands remain estimates."),
            new("ace","ACE-Ego-Hand","Very heavy*",Theme.Red,"Optional · integration pending · untested","Not measured","Temporal hand reconstruction","Wan backbone and hand model assets","Optional integration. Its model has not been run on this PC. Selecting this entry only shows information.")
        };
        readonly Widget _inspector;
        readonly Action<HandModelChoice> _selectModel;
        readonly ListView _list;

        public HandBackendDialog(Widget parent,Action<HandModelChoice> selectModel,string selectedBackend="wilor"):base(parent)
        {
            _selectModel=selectModel;
            Window.WindowTitle="Hand backends";
            Window.SetWindowIcon("memory");
            Window.MinimumSize=new Vector2(700,430);
            Layout=Layout.Column();Layout.Margin=8;Layout.Spacing=8;
            SetStyles($"background-color: {Theme.WidgetBackground.Hex}; color: {Theme.Text.Hex};");
            var splitter=Layout.Add(new Splitter(this){IsHorizontal=true},1);
            var browser=new Widget(this){MinimumWidth=260,Layout=Layout.Column()};
            browser.Layout.Margin=4;browser.Layout.Spacing=4;
            browser.Layout.Add(new Label("Hand models",browser){FixedHeight=Theme.RowHeight});
            _list=browser.Layout.Add(new ListView(browser)
            {
                ItemSize=new Vector2(0,36),ItemSpacing=0,Margin=0,MultiSelect=false,
                ItemPaint=PaintBackend,ItemSelected=item=>Inspect((Backend)item)
            },1);
            _list.SetItems(_backends);
            var note=browser.Layout.Add(new Label("* Estimated processing cost.\nNot an accuracy rating.",browser){WordWrap=true,MinimumHeight=36});
            note.SetStyles($"color: {Theme.TextLight.Hex};");
            splitter.AddWidget(browser);splitter.SetStretch(0,0);splitter.SetCollapsible(0,false);
            _inspector=new Widget(this){MinimumWidth=390,Layout=Layout.Column()};
            _inspector.Layout.Margin=12;_inspector.Layout.Spacing=12;
            splitter.AddWidget(_inspector);splitter.SetStretch(1,1);splitter.SetCollapsible(1,false);
            var footer=Layout.AddRow();footer.Spacing=8;
            var local=footer.Add(new Label("Processing stays on this computer.",this));
            local.SetStyles($"color: {Theme.TextLight.Hex};");
            footer.AddStretchCell();
            footer.Add(new Button.Primary("Done"){MinimumWidth=80}).Clicked=Close;
            var selected=_backends.FirstOrDefault(b=>b.Id==selectedBackend)??_backends[0];
            _list.SelectItem(selected);Inspect(selected);
            Window.Size=new Vector2(760,450);
        }

        // Native selection/hover painting from ModelInspector's animation list.
        void PaintBackend(VirtualWidget item)
        {
            if(item.Object is not Backend backend)return;
            var rect=item.Rect;
            Paint.Antialiasing=true;Paint.ClearPen();
            if(Paint.HasSelected||Paint.HasMouseOver)
            {
                Paint.SetBrush(Theme.Primary.WithAlpha(Paint.HasSelected ? .5f : .25f));
                Paint.DrawRect(rect,2);
            }
            var badgeWidth=7.2f*backend.Weight.Length+18;
            var badge=new Rect(rect.Right-badgeWidth-6,rect.Top+8,badgeWidth,20);
            Chip.DrawPill(badge,backend.Weight,backend.Color);
            Paint.SetDefaultFont();Paint.SetPen(Theme.Text.WithAlpha(Paint.HasSelected?1:.8f));
            var name=rect.Shrink(8,0);name.Right=badge.Left-6;
            Paint.DrawText(name,backend.Name,TextFlag.LeftCenter);
        }

        void Inspect(Backend backend)
        {
            if(_inspector is null)return;
            var content=_inspector.Layout;content.Clear(true);
            content.Add(new Label.Subtitle(backend.Name));
            var sheet=new ControlSheet();content.Add(sheet);
            sheet.SetMinimumColumnWidth(0,82);sheet.VerticalSpacing=8;
            void Property(int row,string name,string value)
            {
                var label=new Label(name,_inspector){MinimumHeight=Theme.RowHeight};
                label.SetStyles($"color: {Theme.TextLight.Hex}; background-color: transparent;");
                sheet.AddCell(0,row,label);
                sheet.AddCell(1,row,new Label(value,_inspector){WordWrap=true,MinimumHeight=Theme.RowHeight});
            }
            Property(0,"Status",backend.Status);
            Property(1,"Download",backend.Download);
            Property(2,"Output",backend.Output);
            Property(3,"Requires",backend.Requirements);
            var limit=content.Add(new Label(backend.Limit,_inspector){WordWrap=true,MinimumHeight=58});
            limit.SetStyles($"color: {Theme.TextLight.Hex};");
            content.AddStretchCell();
            if(backend==_backends[0])
            {
                var controls=content.AddRow();controls.Spacing=6;
                controls.Add(new Button("Choose model file…","folder_open")).Clicked=()=>
                {
                    var picker=new FileDialog(this){Title="Select MediaPipe hand model"};
                    picker.SetModeOpen();picker.SetFindExistingFile();picker.SetNameFilter("MediaPipe task (*.task)");
                    if(picker.Execute()&&!string.IsNullOrWhiteSpace(picker.SelectedFile))
                    {_selectModel(new("mediapipe",picker.SelectedFile));Close();}
                };
                var automatic=controls.Add(new Button("Use MediaPipe only","download"));
                automatic.ToolTip="Download the verified MediaPipe model on the next reconstruction if needed.";
                automatic.Clicked=()=>{_selectModel(new("mediapipe"));Close();};
                controls.AddStretchCell();
            }
            else if(backend.Id is "mobilehand" or "wildhands" or "wilor")
            {
                var use=content.Add(new Button.Primary("Use "+backend.Name){Icon="check"});
                use.ToolTip="Use this model on the next upload. Missing weights download locally on first use; existing reconstruction is preserved.";
                use.Clicked=()=>{_selectModel(new(backend.Id));Close();};
            }
            else content.Add(new Label("Optional integration pending. No model will be loaded.",_inspector){WordWrap=true,MinimumHeight=28});
        }

        internal void InspectBackend(int index)=>_list.SelectItem(_backends[index]);
    }
}
