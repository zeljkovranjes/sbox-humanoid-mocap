using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Editor.Widgets;
using Sandbox;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    bool _firstPerson = true;
    Widget _firstOptions, _thirdOptions, _videoHost, _targetHost;
    Layout _contactRows;
    Label _captureStatus, _clock;
    LineEdit _sourcePath, _rangeStart, _rangeEnd, _fov, _rootSmooth, _armSmooth, _fingerSmooth;
    LineEdit _shoulderL, _shoulderR, _elbowL, _elbowR, _reach, _facing, _ground, _viewPitch;
    VideoWidget _video;
    PreviewWidget _mocapPreview;
    FloatSlider _timeline;
    MotionDocument _rawMotion, _editedMotion;
    string _motionPath;
    string _handModelPath;
    bool _swapHands;
    CancellationTokenSource _processing;

    // Direct reuse of retargeter Group/Theme controls and sbox-public's SegmentedControl.
    void BuildMocapUi()
    {
        var workspace=Layout.AddRow();workspace.Margin=8;workspace.Spacing=8;
        var pages=workspace.Add(new SegmentedControl(this));
        pages.AddOption("First Person","pan_tool");pages.AddOption("Third Person","directions_walk");
        pages.OnSelectedChanged=_=>SetWorkspace(pages.SelectedIndex==0);
        workspace.AddStretchCell();
        workspace.Add(new Label("Capture viewpoint:",this));
        var viewpoint=workspace.Add(new ComboBox(this));
        viewpoint.AddItem("Unknown / uncalibrated",selected:true);
        viewpoint.AddItem("Egocentric");viewpoint.AddItem("External camera");
        viewpoint.ToolTip="Capture viewpoint does not choose the output workspace. Calibration is stored with the motion document.";

        var source=Layout.AddRow();source.Margin=8;source.Spacing=8;
        var import=source.Add(new Button.Primary("Import Video…"){Icon="video_file"});
        import.Clicked=()=>
        {
            var path=EditorUtility.OpenFileDialog("Import video","Video (*.mp4 *.mov *.webm)",null);
            if(!string.IsNullOrEmpty(path))LoadVideo(path);
        };
        var phone=source.Add(new Button("Receive from Phone","qr_code_2"));
        phone.Clicked=()=>new PhoneUploadDialog(this,LoadVideo).Show();
        _sourcePath=source.Add(new LineEdit(this){PlaceholderText="Original footage is preserved"},1);
        source.Add(new Label("Range (s):",this));_rangeStart=source.Add(new LineEdit(this){Text="0",FixedWidth=55});
        _rangeEnd=source.Add(new LineEdit(this){Text="",PlaceholderText="End",FixedWidth=55});
        var load=source.Add(new Button("Open Motion…","folder_open"));
        load.Clicked=()=>
        {
            var path=EditorUtility.OpenFileDialog("Open reconstructed motion","Humanoid Motion (*.hmotion)",null);
            if(!string.IsNullOrEmpty(path))_ = LoadMotionAsync(path);
        };

        var views=Layout.AddRow();views.Margin=8;views.Spacing=8;
        _videoHost=views.Add(new Group(this){Title="Source video",Icon="movie",MinimumHeight=210},1);
        _videoHost.Layout=Layout.Column();_videoHost.Layout.Margin=new Sandbox.UI.Margin(4,26,4,4);
        _videoHost.Layout.Add(new Label("Import footage to inspect lens distortion and tracking visibility.",this));
        _targetHost=views.Add(new Group(this){Title="Target rig · synchronized preview",Icon="accessibility_new",MinimumHeight=210},1);
        _targetHost.Layout=Layout.Column();_targetHost.Layout.Margin=new Sandbox.UI.Margin(4,26,4,4);
        _targetHost.Layout.Add(new Label("Open a motion document to preview on the selected target.",this));
        var transport=Layout.AddRow();transport.Margin=8;transport.Spacing=8;
        var play=transport.Add(new Button("","play_arrow"){FixedWidth=28});
        play.Clicked=()=>_video?.Player?.TogglePause();
        _timeline=transport.Add(new FloatSlider(this),1);_timeline.Minimum=0;_timeline.Maximum=1;
        _timeline.OnValueEdited=()=>
        {
            if(_video?.Player is { } player)player.Seek(_timeline.Value*player.Duration);
            SynchronizePreview();
        };
        _clock=transport.Add(new Label("0.00 s",this){MinimumWidth=80});
        var refresh=transport.Add(new Button("Update target preview","refresh"));refresh.Clicked=()=>_ = RefreshMocapPreviewAsync();

        _firstOptions=Layout.Add(new Group(this){Title="First Person · estimated arm rig",Icon="pan_tool"});
        _firstOptions.Layout=Layout.Row();_firstOptions.Layout.Margin=new Sandbox.UI.Margin(14,30,14,12);_firstOptions.Layout.Spacing=24;
        var left=_firstOptions.Layout.AddColumn();left.Spacing=6;
        _shoulderL=Field(left,"Left shoulder (m)","0.18,1.45,0");_elbowL=Field(left,"Left elbow target","0.45,1.1,0.15");
        var right=_firstOptions.Layout.AddColumn();right.Spacing=6;
        _shoulderR=Field(right,"Right shoulder (m)","-0.18,1.45,0");_elbowR=Field(right,"Right elbow target","-0.45,1.1,0.15");
        var camera=_firstOptions.Layout.AddColumn();camera.Spacing=6;
        _reach=Field(camera,"Reach fraction","0.995");_fov=Field(camera,"Viewmodel FOV","75");
        _viewPitch=Field(camera,"Camera pitch","35");
        _firstOptions.ToolTip="Shoulders and hidden elbows are estimated. These controls apply to target arm correction when hand tracks are present.";

        _thirdOptions=Layout.Add(new Group(this){Title="Third Person · ground and facing",Icon="directions_walk"});
        _thirdOptions.Layout=Layout.Row();_thirdOptions.Layout.Margin=new Sandbox.UI.Margin(14,30,14,12);_thirdOptions.Layout.Spacing=24;
        _ground=Field(_thirdOptions.Layout,"Ground offset (m)","0");_facing=Field(_thirdOptions.Layout,"Facing (degrees)","0");
        _thirdOptions.Visible=false;

        var process=Layout.AddRow();process.Margin=8;process.Spacing=8;
        _rootSmooth=Field(process,"Root cleanup","0.10");_armSmooth=Field(process,"Arms","0.10");_fingerSmooth=Field(process,"Fingers","0.025");
        var cleanup=process.Add(new Button("Apply cleanup","auto_fix_high"));cleanup.Clicked=()=>_ = ProcessMotionAsync();
        var cancel=process.Add(new Button("Cancel","cancel"));cancel.Clicked=()=>_processing?.Cancel();
        var model=process.Add(new Button("Hand model…","memory"));
        model.Clicked=()=>_handModelPath=EditorUtility.OpenFileDialog("Select hand_landmarker.task","MediaPipe task (*.task)",null);
        var reconstruct=process.Add(new Button.Primary("Reconstruct hands"){Icon="motion_photos_on"});
        reconstruct.Clicked=()=>_=ReconstructHandsAsync();
        reconstruct.ToolTip="Experimental C# landmark inference. Downloads the verified 7.8 MB hand model on first use. Footage stays local. This is not ACE-Ego-Hand or GVHMR.";
        var swap=process.Add(new Checkbox("Swap hands"));swap.Clicked=()=>_swapHands=swap.Value;
        var contacts=Layout.Add(new Group(this){Title="Prop contacts · suggestions require review",Icon="touch_app"});
        contacts.Layout=Layout.Column();contacts.Layout.Margin=new Sandbox.UI.Margin(14,30,14,12);_contactRows=contacts.Layout;
        _captureStatus=Layout.Add(new Label("Experimental C# hand landmarks · ACE-Ego-Hand and GVHMR ports are not installed.",this));
        _captureStatus.SetStyles($"color: {Theme.Yellow.Hex}; margin: 8px;");
        RefreshContacts();
    }

    LineEdit Field(Layout layout,string title,string value)
    {
        var row=layout.AddRow();row.Spacing=6;row.Add(new Label(title,this));
        return row.Add(new LineEdit(this){Text=value,MinimumWidth=65},1);
    }
    public void SetWorkspace(bool firstPerson)
    {
        _firstPerson=firstPerson;_firstOptions.Visible=firstPerson;_thirdOptions.Visible=!firstPerson;
        if(_mocapPreview.IsValid())_mocapPreview.FirstPerson=firstPerson;
    }
    public void LoadVideo(string path)
    {
        _sourcePath.Text=path;_videoHost.Layout.Clear(true);
        _video=_videoHost.Layout.Add(new VideoWidget(_videoHost,null),1);
        var relative=VideoFiles.CacheVideo(path);
        _video.Player.Play(global::Editor.FileSystem.ProjectTemporary,relative);
        _video.Player.Muted=true;
        _captureStatus.Text="Inspect source visibility and lens distortion. No camera calibration is assumed.";
    }
    public async Task LoadMotionAsync(string path)
    {
        try
        {
            var doc=await Task.Run(()=>MotionDocument.Parse(File.ReadAllBytes(path)));
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid())return;
            _rawMotion=doc;_editedMotion=doc.Copy();_motionPath=path;
            if(File.Exists(doc.SourceVideo))LoadVideo(doc.SourceVideo);
            _entries.RemoveAll(e=>string.Equals(e.FilePath,path,StringComparison.OrdinalIgnoreCase));
            await AddFilesAsync(new[]{path});RefreshContacts();
            _captureStatus.Text=$"{doc.Backend} · {doc.Space} · {doc.Frames.Count} samples. "+string.Join(" ",doc.Diagnostics.Take(2));
            await RefreshMocapPreviewAsync();
        }
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=e.Message;}
    }
    async Task RefreshMocapPreviewAsync()
    {
        if(_editedMotion is null || _target is null)return;
        try
        {
            var target=_target;var bytes=Encoding.UTF8.GetBytes(_editedMotion.ToJson());var corrections=CaptureTargetCorrections();
            var result=await Task.Run(()=>Retargeter.Convert(new RetargetRequest{SourceData=bytes,SourceFileName="capture.hmotion",FootPlantCleanup=!corrections.FirstPerson,ArmEffectorIk=false,MocapCorrections=corrections},target.Spec));
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid())return;
            var clip=result.Clips.FirstOrDefault(c=>c.Success);
            if(clip is null)throw new InvalidOperationException("No convertible motion. Check the bone mapping.");
            _targetHost.Layout.Clear(true);
            _mocapPreview=_targetHost.Layout.Add(new PreviewWidget(_targetHost,target.Spec.Rig,target.PreviewModelPath,target.PreviewPositionScale,target.Spec.UpAxis),1);
            _mocapPreview.Playing=false;_mocapPreview.FirstPerson=_firstPerson;_mocapPreview.ViewmodelFov=Number(_fov,75);_mocapPreview.ViewmodelPitch=Number(_viewPitch,35);
            _mocapPreview.SetClip(clip);SynchronizePreview();
        }
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=e.Message;}
    }
    async Task ProcessMotionAsync()
    {
        if(_rawMotion is null || _processing is not null)return;
        _processing=new CancellationTokenSource();var token=_processing.Token;
        try
        {
            var settings=new CleanupSettings{Root=Number(_rootSmooth,.1f),Arms=Number(_armSmooth,.1f),Fingers=Number(_fingerSmooth,.025f)};
            var raw=_rawMotion;var contacts=_editedMotion.Contacts;
            var doc=await Task.Run(()=>{token.ThrowIfCancellationRequested();var copy=raw.Copy();copy.Contacts=contacts;var edited=MotionCleanup.Apply(copy,settings);token.ThrowIfCancellationRequested();return edited;},token);
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid())return;token.ThrowIfCancellationRequested();
            _editedMotion=doc;
            var destination=Path.Combine(Path.GetDirectoryName(_motionPath),Path.GetFileNameWithoutExtension(_motionPath)+".edited.hmotion");
            File.WriteAllText(destination,doc.ToJson());
            _entries.RemoveAll(e=>string.Equals(e.FilePath,_motionPath,StringComparison.OrdinalIgnoreCase)||string.Equals(e.FilePath,destination,StringComparison.OrdinalIgnoreCase));
            await AddFilesAsync(new[]{destination});
            _captureStatus.Text="Cleanup saved as a separate motion document. Original observations are preserved.";
            await RefreshMocapPreviewAsync();
        }
        catch(OperationCanceledException){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="Cancelled. Original motion preserved.";}
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=e.Message;}
        finally{_processing.Dispose();_processing=null;}
    }
    async Task ReconstructHandsAsync()
    {
        if(_processing is not null)return;
        if(!File.Exists(_sourcePath.Text)){_captureStatus.Text="Import a local video first.";return;}
        if(_target is null){_captureStatus.Text="Select a target rig first.";return;}
        _processing=new CancellationTokenSource();
        try
        {
            if(!File.Exists(_handModelPath))
            {
                _captureStatus.Text="Downloading and verifying the 7.8 MB hand model. Your footage stays local.";
                _handModelPath=await HandModelStore.EnsureAsync(_processing.Token);
                await EditorPipeline.SwitchToMainThread();
                if(!this.IsValid())return;
            }
            var path=await HandCaptureJob.RunAsync(_sourcePath.Text,_handModelPath,_target.Spec.Rig,Number(_rangeStart,0),
                string.IsNullOrWhiteSpace(_rangeEnd.Text)?null:Number(_rangeEnd,0),_swapHands,
                (done,total,message)=>{if(this.IsValid())_captureStatus.Text=$"{message} · {done}/{total}";},_processing.Token);
            await LoadMotionAsync(path);
        }
        catch(OperationCanceledException){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="Cancelled. Restart reconstruction to resume cached observations.";}
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=e.Message;}
        finally{_processing.Dispose();_processing=null;}
    }
    static float Number(LineEdit field,float fallback)=>float.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&float.IsFinite(n)?n:fallback;
    TargetCorrectionSettings CaptureTargetCorrections() => new()
    {
        FirstPerson=_firstPerson,LeftShoulder=Vector(_shoulderL),RightShoulder=Vector(_shoulderR),
        LeftElbow=Vector(_elbowL),RightElbow=Vector(_elbowR),Reach=Number(_reach,.995f),
        GroundOffset=Number(_ground,0),FacingDegrees=Number(_facing,0)
    };
    static System.Numerics.Vector3 Vector(LineEdit edit)
    {
        var parts=edit.Text.Split(',');
        if(parts.Length!=3)throw new ArgumentException("Enter three metre coordinates separated by commas.");
        return new(float.Parse(parts[0],CultureInfo.InvariantCulture),float.Parse(parts[1],CultureInfo.InvariantCulture),float.Parse(parts[2],CultureInfo.InvariantCulture));
    }
    void RefreshContacts()
    {
        _contactRows.Clear(true);
        if(_editedMotion is null || _editedMotion.Contacts.Count==0)
        {
            _contactRows.Add(new Label("No contact suggestions. A hand track alone does not recover prop motion.",this));return;
        }
        foreach(var contact in _editedMotion.Contacts)
        {
            var row=_contactRows.AddRow();row.Spacing=8;
            var label=row.Add(new Label($"{contact.Start:F2}–{contact.End:F2}s · {contact.Bone} → {contact.Object} · {contact.Review}",this),1);
            label.SetStyles($"color: {(contact.Review==ContactReview.Suggested?Theme.Yellow:Theme.TextLight).Hex};");
            var confirm=row.Add(new Button("Confirm","check"));confirm.Clicked=()=>{contact.Review=ContactReview.Confirmed;RefreshContacts();};
            var disable=row.Add(new Button("Disable","block"));disable.Clicked=()=>{contact.Review=ContactReview.Disabled;RefreshContacts();};
        }
    }
    [EditorEvent.Frame]
    public void MocapTick()
    {
        if(!this.IsValid() || _video?.Player is null)return;
        var p=_video.Player;_clock.Text=$"{p.PlaybackTime:F2} s";
        if(p.Duration>0)_timeline.Value=(float)(p.PlaybackTime/p.Duration);
        SynchronizePreview();
    }
    void SynchronizePreview()
    {
        if(!_mocapPreview.IsValid() || _editedMotion is null)return;
        var time=_video?.Player?.PlaybackTime??(_editedMotion.Frames[0].Time+_timeline.Value*(_editedMotion.Frames[^1].Time-_editedMotion.Frames[0].Time));
        _mocapPreview.Scrub((int)Math.Round((time-_editedMotion.Frames[0].Time)*_editedMotion.SourceFps));
    }
}
