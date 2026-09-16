using System;
using System.Globalization;
using System.Linq;
using Editor;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

/// <summary>Edits a dialog-owned draft. No capture changes until the dialog is saved.</summary>
sealed class MocapContactKeys : Widget
{
    readonly MotionDocument motion;
    readonly ContactInterval draft;
    readonly Action readInterval,changed;
    readonly Action<string> status;
    readonly Widget pickerHost;
    int selected=-1;
    string savedTime,savedPosition;
    internal readonly LineEdit Time,LocalPositionInput;
    internal readonly Button ApplyKey,RemoveKey,SampleWrist,NewKey;
    internal bool HasUnappliedChanges=>Time.Text!=savedTime||LocalPositionInput.Text!=savedPosition;

    public MocapContactKeys(Widget parent,MotionDocument motion,ContactInterval draft,Action readInterval,
        Func<double> playhead,Action<string> status,Action changed):base(parent)
    {
        this.motion=motion;this.draft=draft;this.readInterval=readInterval;this.status=status;this.changed=changed;
        Layout=Layout.Column();Layout.Spacing=6;
        Layout.Add(new Label("Sliding keys · object-local metres / video seconds",this));
        pickerHost=Layout.Add(new Widget(this));pickerHost.Layout=Layout.Column();
        LineEdit Input(string title){var row=Layout.AddRow();row.Spacing=8;row.Add(new Label(title,this){FixedWidth=160});return row.Add(new LineEdit(this),1);}
        Time=Input("Key time (s)");LocalPositionInput=Input("Local X, Y, Z (m)");
        var placement=Layout.AddRow();placement.Spacing=6;
        placement.Add(new Button("Use playhead","schedule"){Clicked=()=>Time.Text=playhead().ToString("R",CultureInfo.InvariantCulture)});
        SampleWrist=placement.Add(new Button("Sample captured wrist","my_location"));
        SampleWrist.ToolTip="Fill from the nearest observed wrist and prop sample. Click Add key or Update key to keep it. This does not detect sliding automatically.";
        SampleWrist.Clicked=()=>Try(()=>{
            readInterval();var sample=ContactAuthoring.SampleWristAnchor(motion,draft,double.Parse(Time.Text,CultureInfo.InvariantCulture));
            Time.Text=sample.Time.ToString("R",CultureInfo.InvariantCulture);LocalPositionInput.Text=Text(sample.Local.Pos);
            status("Observed wrist sampled. Add or update the key to keep this placement.");
        });
        var buttons=Layout.AddRow();buttons.Spacing=6;
        NewKey=buttons.Add(new Button("New key","add"));NewKey.Clicked=()=>{selected=-1;Refresh();};
        ApplyKey=buttons.Add(new Button("Add key","check"));
        ApplyKey.Clicked=()=>Try(()=>{
            readInterval();var values=LocalPositionInput.Text.Split(',').Select(v=>float.Parse(v.Trim(),CultureInfo.InvariantCulture)).ToArray();
            if(values.Length!=3)throw new ArgumentException("Enter three comma-separated position values.");
            selected=ContactAuthoring.SetSlidingKey(draft,selected,double.Parse(Time.Text,CultureInfo.InvariantCulture),new(values[0],values[1],values[2]));
            Refresh();changed();status($"{draft.TargetKeys.Count} sliding keys. Save and review before confirming.");
        });
        RemoveKey=buttons.Add(new Button("Remove key","delete"));
        RemoveKey.Clicked=()=>Try(()=>{
            if(selected<0)return;draft.TargetKeys.RemoveAt(selected);selected=-1;Refresh();changed();
            status("Key removed from this draft. Sliding requires at least two keys before saving.");
        });
        selected=draft.TargetKeys.Count>0?0:-1;Refresh();
    }
    void Try(Action action){try{action();}catch(Exception error){status(error.Message);}}
    static string Text(System.Numerics.Vector3 v)=>string.Join(",",new[]{v.X,v.Y,v.Z}.Select(x=>x.ToString("R",CultureInfo.InvariantCulture)));
    void Select(int index)
    {
        selected=index;ApplyKey.Text=index<0?"Add key":"Update key";RemoveKey.Enabled=index>=0;
        if(index>=0){var key=draft.TargetKeys[index];Time.Text=key.Time.ToString("R",CultureInfo.InvariantCulture);LocalPositionInput.Text=Text(MotionDocument.V(key.Position));}
        else{Time.Text=draft.Start.ToString("R",CultureInfo.InvariantCulture);LocalPositionInput.Text=Text(MotionDocument.V(draft.LocalTarget));}
        savedTime=Time.Text;savedPosition=LocalPositionInput.Text;
    }
    void Refresh()
    {
        pickerHost.Layout.Clear(true);var picker=pickerHost.Layout.Add(new ComboBox(this));
        picker.AddItem("New key…",onSelected:()=>Select(-1),selected:selected<0);
        for(var i=0;i<draft.TargetKeys.Count;i++)
        {var index=i;var key=draft.TargetKeys[i];picker.AddItem($"{key.Time:F3} s · {Text(MotionDocument.V(key.Position))}",onSelected:()=>Select(index),selected:i==selected);}
        Select(selected);
    }
}
