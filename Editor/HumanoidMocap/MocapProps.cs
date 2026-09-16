using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using HumanoidMocap.Formats.Fbx;
using HumanoidMocap.Motion;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    async void PickPropAnimation()
    {
        if(_rawMotion is null||_processing is not null)return;
        var path=EditorUtility.OpenFileDialog("Import prop animation","FBX animation (*.fbx)",null);
        if(string.IsNullOrEmpty(path))return;
        var session=_editSession;
        _processing=new CancellationTokenSource();var token=_processing.Token;SetCaptureBusy(true);
        try
        {
            var fps=(float)_rawMotion.SourceFps;
            var scene=await Task.Run(()=>{
                if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("Prop FBX exceeds 64 MiB. Export only the selected prop, a simple contact mesh and its animation.");
                return FbxImporter.Import(File.ReadAllBytes(path),new(){SampleFps=fps,MaximumTransformSamples=FbxAnimationWriter.MaximumTransformSamples,CancellationToken=token});
            },token);
            await EditorPipeline.SwitchToMainThread();token.ThrowIfCancellationRequested();
            if(!this.IsValid()||session!=_editSession)return;
            if(scene.Clips.Count==0)throw new InvalidDataException("The FBX has no animation take. Export the prop's root and part animation.");
            new PropImportDialog(this,scene,path,session).Show();
        }
        catch(OperationCanceledException){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="Prop import cancelled.";}
        catch(Exception error){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=error.Message;}
        finally{await EditorPipeline.SwitchToMainThread();_processing.Dispose();_processing=null;if(this.IsValid()){SetCaptureBusy(false);StartNextQueuedVideo();}}
    }

    internal async Task ImportPropAsync(SourceScene scene,int take,string id,string sourcePath,double start,
        System.Numerics.Vector3 offset,System.Numerics.Quaternion rotation,float scale,MocapAdjustmentStore.Session expected)
    {
        if(_processing is not null||expected!=_editSession)throw new InvalidOperationException("The capture changed or is busy. Reopen prop import.");
        if(!_firstPerson||!HandCaptureRetargeter.Supports(_rawMotion)||_rawMotion.Space!=MotionSpace.CameraRelative)
            throw new InvalidOperationException("Prop import currently requires First Person camera-relative hand capture.");
        _processing=new CancellationTokenSource();var token=_processing.Token;SetCaptureBusy(true);
        try
        {
            var raw=_rawMotion;var contacts=_editedMotion.Copy().Contacts;var cleanup=_appliedCleanup;var path=_motionPath;
            var saved=await Task.Run(()=>{
                token.ThrowIfCancellationRequested();
                var prop=PropTrackImport.Convert(scene,take,id,raw.Space,start,offset,rotation,scale,token);prop.ModelPath=Path.GetFullPath(sourcePath);
                if(new FileInfo(sourcePath).Length>64*1024*1024)throw new InvalidDataException("Prop FBX exceeds 64 MiB.");
                var surfaces=FbxPropSurfaces.Read(File.ReadAllBytes(sourcePath),scene,scale,token);prop.Surfaces=surfaces.Surfaces;
                var composed=PropTrackImport.Add(raw,prop);composed.Contacts=contacts;
                composed.Diagnostics.Add($"Prop '{id}' imported from {Path.GetFileName(sourcePath)} / {scene.Clips[take].Name}; placement and timing supplied by the user. Not video object tracking.");
                composed.Diagnostics.Add($"Prop '{id}': {prop.Surfaces.Sum(s=>s.Triangles.Length/3)} rigid contact triangles imported; {surfaces.SkippedFaces} unsupported/nontriangular faces omitted. Contact geometry is used for suggestions, not exported as skin.");
                composed.Validate();var edited=cleanup is null?composed:MotionCleanup.Apply(composed,cleanup);
                token.ThrowIfCancellationRequested();
                var folder=Path.GetDirectoryName(path);var stem=Path.GetFileNameWithoutExtension(path)+"-props-"+Guid.NewGuid().ToString("N")[..8];
                var rawPath=Path.Combine(folder,stem+"-source.hmotion");var editedPath=Path.Combine(folder,stem+".hmotion");
                // New files only: never overwrite the reconstruction or an earlier edit.
                using(var stream=new StreamWriter(new FileStream(rawPath,FileMode.CreateNew,FileAccess.Write)))stream.Write(composed.ToJson());
                using(var stream=new StreamWriter(new FileStream(editedPath,FileMode.CreateNew,FileAccess.Write)))stream.Write(edited.ToJson());
                return (rawPath,editedPath);
            },token);
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid()||expected!=_editSession)return;
            await LoadMotionAsync(saved.editedPath,saved.rawPath,cleanup);
            if(!this.IsValid())return;
            if(_editSession==expected||_motionPath!=saved.editedPath)
                throw new InvalidOperationException("The imported capture was saved, but could not be opened: "+_captureStatus.Text);
            foreach(var pair in expected.State.Targets)_editSession.State.Targets[pair.Key]=pair.Value;
            RestoreTargetAdjustments();await RefreshMocapPreviewAsync();
            _captureStatus.Text="Prop animation imported into a separate capture. Review alignment, then add wrist contacts in Advanced.";
        }
        finally{await EditorPipeline.SwitchToMainThread();_processing.Dispose();_processing=null;if(this.IsValid()){SetCaptureBusy(false);StartNextQueuedVideo();}}
    }

    internal sealed class PropImportDialog : Dialog
    {
        internal readonly Checkbox Aligned;
        internal readonly Button Import;
        public PropImportDialog(RetargetWindow owner,SourceScene scene,string path,MocapAdjustmentStore.Session session):base(owner)
        {
            Window.WindowTitle="Import prop animation";Window.SetWindowIcon("view_in_ar");
            Window.Size=new Vector2(540,440);Window.MinimumSize=new Vector2(500,420);
            SetStyles($"background-color: {Theme.WidgetBackground.Hex}; color: {Theme.Text.Hex};");
            Layout=Layout.Column();Layout.Margin=16;Layout.Spacing=10;
            Layout.Add(new Label("Align this animation to the capture camera. The importer does not recover prop motion or camera calibration.",this){WordWrap=true});
            LineEdit Input(string title,string value){var row=Layout.AddRow();row.Spacing=8;row.Add(new Label(title,this){FixedWidth=150});return row.Add(new LineEdit(this){Text=value},1);}
            var name=Input("Prop name",Path.GetFileNameWithoutExtension(path));
            var row=Layout.AddRow();row.Spacing=8;row.Add(new Label("Animation take",this){FixedWidth=150});var picker=row.Add(new ComboBox(this),1);int take=0;
            for(var i=0;i<scene.Clips.Count;i++){var n=i;picker.AddItem(scene.Clips[i].Name,onSelected:()=>take=n,selected:i==0);}
            var start=Input("First frame at video (s)",owner._rawMotion.Frames[0].Time.ToString("R",CultureInfo.InvariantCulture));
            var offset=Input("Position X, Y, Z (m)","0,0,0");var angles=Input("Yaw, pitch, roll (°)","0,0,0");var scale=Input("Scale multiplier","1");
            Layout.Add(new Label("X right, Y up, −Z in front of camera. FBX axes and units are converted first; placement applies to the root. The take must cover the capture range.",this){WordWrap=true});
            var aligned=Aligned=Layout.Add(new Checkbox("I have aligned this track to the capture camera"));
            var status=Layout.Add(new Label("",this){WordWrap=true});status.SetStyles($"color: {Theme.Yellow.Hex};");
            var buttons=Layout.AddRow();buttons.AddStretchCell();buttons.Add(new Button("Cancel"){Clicked=Close});var add=Import=buttons.Add(new Button.Primary("Import"));
            add.Clicked=async ()=>{
                try
                {
                    if(!aligned.Value)throw new InvalidOperationException("Check camera-space placement before importing. Imported animation is not automatically aligned.");
                    var a=Vector(angles)*(MathF.PI/180);
                    var q=System.Numerics.Quaternion.CreateFromYawPitchRoll(a.X,a.Y,a.Z);
                    var t=double.Parse(start.Text,CultureInfo.InvariantCulture);var s=float.Parse(scale.Text,CultureInfo.InvariantCulture);
                    add.Enabled=false;await owner.ImportPropAsync(scene,take,name.Text,path,t,Vector(offset),q,s,session);
                    await EditorPipeline.SwitchToMainThread();if(this.IsValid())Close();
                }
                catch(Exception error){await EditorPipeline.SwitchToMainThread();if(this.IsValid()){status.Text=error.Message;add.Enabled=true;}}
            };
        }
    }
}
