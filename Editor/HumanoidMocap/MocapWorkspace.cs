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
    LineEdit _sourcePath, _rangeStart, _rangeEnd, _fov, _rootSmooth, _armSmooth, _fingerSmooth, _smoothing;
    LineEdit _shoulderL, _shoulderR, _elbowL, _elbowR, _reach, _facing, _ground, _viewPitch;
    LineEdit _capturePosition, _captureYaw, _capturePitch, _viewNear, _recordingFov;
    bool _captureFacesSubject,_settingFacingControl;
    Checkbox _facingControl;
    MocapVideoWidget _video;
    PreviewWidget _mocapPreview;
    FloatSlider _timeline;
    MotionDocument _rawMotion, _editedMotion;
    string _motionPath;
    string _handModelPath;
    string _handBackend="wilor";
    bool _swapHands;
    Checkbox _swapHandsControl;
    Checkbox _inPlaceControl;
    Checkbox _stabilizeFeetControl;
    bool _showTargetBones=true;
    CancellationTokenSource _processing;
    SegmentedControl _workspacePicker;
    Widget _advancedPanel;
    Label _motionDetails;
    Button _exportMotionButton, _uploadVideoButton, _phoneVideoButton, _cancelCaptureButton, _retryCaptureButton;
    bool _exportCaptured;
    bool _previewFirstPerson = true;
    SegmentedControl _viewPicker;
    Widget _welcome, _previewArea, _transportBar;
    Label _videoName;
    Dialog _adjustments;

    // Direct reuse of retargeter Group/Theme controls and sbox-public's SegmentedControl.
    void BuildMocapUi()
    {
        Layout.Margin=12;Layout.Spacing=10;
        var header=Layout.AddRow();header.Spacing=8;
        _workspacePicker=header.Add(new SegmentedControl(this){FixedWidth=280});
        _workspacePicker.AddOption("First Person","pan_tool");_workspacePicker.AddOption("Third Person","directions_walk");
        _workspacePicker.OnSelectedChanged=_=>SetWorkspace(_workspacePicker.SelectedIndex==0);
        header.AddStretchCell();
        var advanced=header.Add(new Button("Advanced","tune"));
        advanced.Clicked=ShowAdjustments;
        _exportMotionButton=header.Add(new Button.Primary("Export…"){Icon="file_download",Enabled=false});
        _exportMotionButton.Clicked=()=>PickMotionExport(!_exportCaptured);
        _exportMotionButton.ToolTip="Export the armature and animated bones as FBX.";

        var upload=Layout.AddRow();upload.Spacing=8;
        _uploadVideoButton=upload.Add(new Button.Primary("Upload video…"){Icon="video_file"});
        _uploadVideoButton.Clicked=()=>{
            var path=EditorUtility.OpenFileDialog("Upload video","Video (*.mp4 *.mov)",null);
            if(!string.IsNullOrEmpty(path))ImportVideoAndProcess(path);
        };
        _phoneVideoButton=upload.Add(new Button("Upload from phone","qr_code_2"));
        _phoneVideoButton.Clicked=()=>new PhoneUploadDialog(this,ImportVideoAndProcess).Show();
        _sourcePath=new LineEdit(this){Visible=false,ReadOnly=true};
        _videoName=upload.Add(new Label("",this),1);
        _videoName.SetStyles($"color: {Theme.TextLight.Hex};");

        _welcome=Layout.Add(new VideoDropArea(this,ImportVideoAndProcess),1);
        _previewArea=Layout.Add(new Widget(this){Visible=false},1);
        _previewArea.Layout=Layout.Row();_previewArea.Layout.Spacing=10;
        var sourcePanel=_previewArea.Layout.Add(new Widget(this),1);sourcePanel.Layout=Layout.Column();sourcePanel.Layout.Spacing=6;
        sourcePanel.Layout.Add(new Label("Source video",sourcePanel){FixedHeight=28});
        _videoHost=sourcePanel.Layout.Add(new Widget(sourcePanel),1);_videoHost.Layout=Layout.Column();
        var animationPanel=_previewArea.Layout.Add(new Widget(this),1);animationPanel.Layout=Layout.Column();animationPanel.Layout.Spacing=6;
        var viewBar=animationPanel.Layout.AddRow();viewBar.Spacing=4;
        _viewPicker=viewBar.Add(new SegmentedControl(animationPanel),1);
        _viewPicker.AddOption("First person","videocam");_viewPicker.AddOption("Third person","3d_rotation");
        _viewPicker.ToolTip="Preview camera only. Switching views does not change the capture or animation.";
        _viewPicker.OnSelectedChanged=_=>SetPreviewView(_viewPicker.SelectedIndex==0);
        // Native IconButton centers its glyph. Button reserves a trailing text gap
        // even with an empty label, shifting this icon two pixels to the left.
        viewBar.Add(new IconButton("center_focus_strong",()=>_mocapPreview?.ResetView(),animationPanel)
            {FixedSize=28,IconSize=16,ToolTip="Reset preview camera"});
        _previewTargetPicker=viewBar.Add(new ComboBox(animationPanel){FixedWidth=104,ToolTip="Preview and export character. Citizen is Terry (citizen.vmdl). Changing character reuses the captured motion."});
        _previewTargetPicker.AddItem("Human",onSelected:()=>SelectBuiltinPreviewTarget(false),selected:true);
        _previewTargetPicker.AddItem("Citizen",onSelected:()=>SelectBuiltinPreviewTarget(true),description:"Terry · models/citizen/citizen.vmdl");
        _previewTargetPicker.AddItem("Human arms",onSelected:()=>SelectFirstPersonArms(false),description:"First-person viewmodel · "+TargetPickers.HumanArmsPackage);
        _previewTargetPicker.AddItem("Citizen arms",onSelected:()=>SelectFirstPersonArms(true),description:"First-person viewmodel, 4 fingers · "+TargetPickers.CitizenArmsPackage);
        _previewTargetPicker.AddItem("Custom",enabled:false);
        _targetHost=animationPanel.Layout.Add(new Widget(animationPanel),1);_targetHost.Layout=Layout.Column();
        _targetHost.Layout.Add(new Label("Preparing animation…",_targetHost){Alignment=TextFlag.Center},1);

        _trackingStatus=Layout.Add(new Label("",this){Visible=false,FixedHeight=18});
        _transportBar=Layout.Add(new Widget(this){Visible=false});_transportBar.Layout=Layout.Row();
        var transport=_transportBar.Layout;transport.Spacing=8;
        // IconButton centers the play/pause glyph; a Button keeps a text gap even with no label.
        _playButton=transport.Add(new IconButton("play_arrow",TogglePlayback,this){FixedSize=28,IconSize=16,ToolTip="Play / pause"});
        var tracks=transport.Add(new Widget(this),1);tracks.Layout=Layout.Column();tracks.Layout.Spacing=2;
        _timeline=tracks.Layout.Add(new FloatSlider(tracks));_timeline.Minimum=0;_timeline.Maximum=1;
        _timeline.OnValueEdited=()=>SeekPlaybackFraction(_timeline.Value);
        _contactTimeline=tracks.Layout.Add(new MocapContactTimeline(tracks){Seek=SeekPlaybackFraction,ChangeRange=(expected,index,start,end)=>_=ChangeContactRangeAsync(expected,index,start,end),
            EditWrist=(expected,edit)=>{var index=_wristOffsets.IndexOf(edit);if(expected==_editedMotion&&index>=0)OpenWristOffsetEditor(index);},
            Edit=(expected,index)=>{if(expected==_editedMotion&&_processing is null)OpenContactEditor(expected.Contacts[index]);},
            Review=(expected,index,review)=>{if(expected==_editedMotion&&_processing is null)_=ReviewContactAsync(expected.Contacts[index],review);}});
        _clock=transport.Add(new Label("0.00 s",this){MinimumWidth=80});
        var bones=transport.Add(new Checkbox("Bones"){Value=true});
        bones.Clicked=()=>{_showTargetBones=bones.Value;if(_mocapPreview.IsValid())_mocapPreview.ShowTargetBones=bones.Value;};

        var status=Layout.AddRow();status.Spacing=8;
        _captureStatus=status.Add(new Label("Choose a workspace, then upload a video.",this){WordWrap=true,MinimumHeight=24},1);
        _cancelCaptureButton=status.Add(new Button("Cancel","cancel"){Visible=false,Clicked=CancelCapture});
        _retryCaptureButton=status.Add(new Button("Retry","refresh"){Visible=false,Clicked=()=>_=ProcessImportedVideoAsync()});

        _advancedPanel=new Widget(this){Visible=false};_advancedPanel.Layout=Layout.Column();_advancedPanel.Layout.Spacing=10;
        var advancedTop=_advancedPanel.Layout.AddRow();advancedTop.Spacing=8;
        var models=advancedTop.Add(new Button("Hand models…","memory"));
        models.Clicked=()=>new HandBackendDialog(this,SelectHandModel,_handBackend).Show();
        advancedTop.Add(new Button("Reinstall worker","download"){ToolTip="Delete the local inference worker and download it again from GitHub. Use this if captures stop starting or the worker seems damaged.",Clicked=()=>_=ReinstallWorkerAsync()});
        advancedTop.Add(new Label("Target:",this));
        var target=_advancedTargetPicker=advancedTop.Add(new ComboBox(this));
        target.AddItem("s&box Human","person",()=>SelectBuiltinPreviewTarget(false),selected:true);
        target.AddItem("s&box Citizen","person",()=>SelectBuiltinPreviewTarget(true));
        target.AddItem("s&box Human arms","pan_tool",()=>SelectFirstPersonArms(false));
        target.AddItem("s&box Citizen arms","pan_tool",()=>SelectFirstPersonArms(true));
        target.AddItem("Custom VMDL…","folder_open",()=>{if(!_updatingTargetPickers){PickCustomModelTarget();RefreshTargetPickers();}});
        target.AddItem("Custom FBX / GLB…","folder_open",()=>{if(!_updatingTargetPickers){PickCustomFbxTarget();RefreshTargetPickers();}});
        var captured=advancedTop.Add(new Checkbox("Export captured skeleton"));
        captured.ToolTip="Skip target retargeting when exporting. Captured hands do not include estimated arms.";
        captured.Clicked=()=>{_exportCaptured=captured.Value;UpdateExportAvailability();};
        advancedTop.AddStretchCell();
        advancedTop.Add(new Button("Open motion…","folder_open"){ToolTip="Open a saved .hmotion capture or import an annotated HOT3D Aria .tar clip.",Clicked=()=>{
            var file=EditorUtility.OpenFileDialog("Open motion or HOT3D clip","Humanoid Motion or HOT3D (*.hmotion *.tar)",null);
            if(!string.IsNullOrEmpty(file))_=OpenCaptureFileAsync(file);
        }});
        var range=_advancedPanel.Layout.AddRow();range.Spacing=8;
        _rangeStart=Field(range,"Start (s)","0");_rangeEnd=Field(range,"End (s)","");
        _swapHandsControl=range.Add(new Checkbox("Swap hands (MediaPipe)"){Enabled=_handBackend=="mediapipe"});
        _swapHandsControl.ToolTip="Correct MediaPipe handedness for mirrored footage. Native MANO models currently use their detected side.";
        _swapHandsControl.Clicked=()=>_swapHands=_swapHandsControl.Value;
        range.Add(new Button("Process again","refresh"){Clicked=()=>_=ProcessImportedVideoAsync()});
        _firstOptions=_advancedPanel.Layout.Add(new Group(this){Title="First Person · estimated arm rig",Icon="pan_tool"});
        _firstOptions.Layout=Layout.Column();_firstOptions.Layout.Margin=new Sandbox.UI.Margin(12,30,12,12);_firstOptions.Layout.Spacing=12;
        var arms=_firstOptions.Layout.AddRow();arms.Spacing=16;
        var left=arms.AddColumn();left.Spacing=6;
        _shoulderL=Field(left,"Left shoulder (m)","0.18,1.45,0");_elbowL=Field(left,"Left elbow target","0.45,1.1,0.15");
        var right=arms.AddColumn();right.Spacing=6;
        _shoulderR=Field(right,"Right shoulder (m)","-0.18,1.45,0");_elbowR=Field(right,"Right elbow target","-0.45,1.1,0.15");
        var placementRow=_firstOptions.Layout.AddRow();placementRow.Spacing=16;
        var camera=placementRow.AddColumn();camera.Spacing=6;
        _reach=Field(camera,"Reach fraction","0.995");_fov=Field(camera,"Viewmodel FOV","75");
        _recordingFov=Field(camera,"Recording FOV (°)","");
        _recordingFov.PlaceholderText="Automatic";
        _recordingFov.ToolTip="Optional horizontal field of view of the recorded video, 20–150 degrees. Blank sizes the lens automatically so the hands sit at a typical first-person distance within arm's reach; enter your lens value if you know it. Applies to WildHands, WiLoR and MobileHand on Process again; changes reconstructed depth, not just the preview. This is not calibration or fisheye correction.";
        _viewPitch=Field(camera,"Camera pitch","0");
        _viewNear=Field(camera,"Near clip (cm)","15");
        _viewNear.ToolTip="Hide nearby head geometry on full-body targets. Reduce this distance to inspect hands close to the viewmodel camera.";
        var placement=placementRow.AddColumn();placement.Spacing=6;
        _capturePosition=Field(placement,"Capture origin (m)","0,1.65,0");
        _captureYaw=Field(placement,"Capture yaw","180");_capturePitch=Field(placement,"Capture pitch","0");
        _capturePosition.ToolTip="Place camera-relative hand tracks in the target rig. This editable placement is not recovered camera motion.";
        _captureYaw.ToolTip="Capture-camera placement in degrees; separate from the preview camera and viewmodel FOV.";
        _capturePitch.ToolTip="Recorded camera tilt: negative looks down, positive looks up. A level assumption can raise the arms when the original video looks down. This is an editable placement assumption, not measured camera tracking.";
        var tiltPresets=placement.AddRow();tiltPresets.Spacing=4;
        tiltPresets.Add(new Button("Level","horizontal_rule"){Clicked=()=>_=SetCaptureTiltAsync(0),ToolTip="Place the capture as a level camera. Reuses reconstruction."});
        tiltPresets.Add(new Button("Looking down","south_east"){Clicked=()=>_=SetCaptureTiltAsync(-45),ToolTip="Assume a camera tilted 45 degrees down. Changes target arm placement and the exported animation; keeps captured hand detail. Adjust Capture pitch for your footage."});
        _facingControl=placement.Add(new Checkbox("Filmed facing the performer"){ToolTip="On: the camera stood in front of the person, so their right hand is on the left of the picture. Off: the camera was worn on their head or chest. Guessed from which side of the picture each hand is on; change it if the arms come out crossed or mirrored. Reuses reconstruction."});
        _facingControl.Clicked=()=>{if(!_settingFacingControl)_=SetCaptureFacingAsync(_facingControl.Value);};
        _firstOptions.ToolTip="Shoulders and hidden elbows are estimated. These controls apply to target arm correction when hand tracks are present.";
        _wristOffsetRows=_firstOptions.Layout.AddColumn();_wristOffsetRows.Spacing=6;

        _thirdOptions=_advancedPanel.Layout.Add(new Group(this){Title="Third Person · ground and facing",Icon="directions_walk"});
        _thirdOptions.Layout=Layout.Column();_thirdOptions.Layout.Margin=new Sandbox.UI.Margin(14,30,14,12);_thirdOptions.Layout.Spacing=10;
        var groundOptions=_thirdOptions.Layout.AddRow();groundOptions.Spacing=24;
        _ground=Field(groundOptions,"Ground offset (m)","0");_facing=Field(groundOptions,"Facing (degrees)","0");
        var inPlace=_inPlaceControl=groundOptions.Add(new Checkbox("In place"));
        inPlace.ToolTip="Remove horizontal travel from the baked animation. Camera-relative capture remains camera-relative.";
        inPlace.Clicked=()=>{_rootMotion=inPlace.Value?HumanoidMocap.Cleanup.RootMotionMode.InPlace:HumanoidMocap.Cleanup.RootMotionMode.Off;_=RefreshMocapPreviewAsync();};
        var contactOptions=_thirdOptions.Layout.AddRow();contactOptions.Spacing=12;
        _stabilizeFeetControl=contactOptions.Add(new Checkbox("Reduce foot drift"){Value=true,Enabled=false,
            ToolTip="Keep predicted stationary ankles and toes anchored on the final target rig. Applies to world-relative captures with backend stationary-joint probabilities. Preserves toe-only pivots and bone lengths; predictions still need review."});
        _stabilizeFeetControl.Clicked=()=>_=RefreshMocapPreviewAsync();
        _bodyRefinementButton=contactOptions.Add(new Button("Refine · stationary camera","auto_fix_high")
        {
            Enabled=false,
            ToolTip="Use only when the recording camera stayed still. Reuse saved GVHMR predictions to refine root and limb contacts. Keeps the original motion and opens a separate result; review before exporting.",
            Clicked=()=>_=RefineStationaryBodyAsync()
        });
        _thirdOptions.Visible=false;


        var cleanup=_advancedPanel.Layout.AddRow();cleanup.Spacing=8;
        _rootSmooth=Field(cleanup,"Root cleanup","0.25");_armSmooth=Field(cleanup,"Arms","0.10");_fingerSmooth=Field(cleanup,"Fingers","0.025");
        _smoothing=Field(cleanup,"Smoothing","7");_smoothing.ToolTip="Zero-phase smoothing strength, 0.5 to 10 (higher is smoother); 0 turns it off. 7 matches Rokoko's default.";
        cleanup.Add(new Button("Apply adjustments","check"){Clicked=()=>_=ProcessMotionAsync()});
        var contacts=_advancedPanel.Layout.Add(new Group(this){Title="Contact review",Icon="touch_app"});
        contacts.Layout=Layout.Column();contacts.Layout.Margin=new Sandbox.UI.Margin(8,38,8,8);_contactRows=contacts.Layout;
        _motionDetails=_advancedPanel.Layout.Add(new Label(this){WordWrap=true});
        RefreshContacts();
    }

    LineEdit Field(Layout layout,string title,string value)
    {
        var row=layout.AddRow();row.Spacing=6;row.Add(new Label(title,this));
        return row.Add(new LineEdit(this){Text=value,MinimumWidth=65},1);
    }
    internal void SelectHandModel(HandModelChoice choice)
    {
        if(_processing is not null){_captureStatus.Text="Finish or cancel the active capture before changing models.";return;}
        if(choice.Backend is not ("mediapipe" or "mobilehand" or "wildhands" or "wilor"))throw new NotSupportedException("This hand backend is not available.");
        _handBackend=choice.Backend;_handModelPath=choice.ModelPath;
        _swapHandsControl.Enabled=_firstPerson&&_handBackend=="mediapipe";
        _recordingFov.Enabled=_handBackend!="mediapipe";
    }
    public void SetWorkspace(bool firstPerson)
    {
        var changed=_firstPerson!=firstPerson;
        _firstPerson=firstPerson;_firstOptions.Visible=firstPerson;_thirdOptions.Visible=!firstPerson;
        _swapHandsControl.Enabled=firstPerson&&_handBackend=="mediapipe";
        if(_workspacePicker.SelectedIndex!=(firstPerson?0:1))_workspacePicker.SelectedIndex=firstPerson?0:1;
        if(changed)
        {
            // First Person animates the viewmodel arms games use; Third Person the whole character.
            // Only the default follows the workspace: a target the user picked is kept.
            var path=_target?.PreviewModelPath;
            if(!_targetChosen&&firstPerson&&(path==RetargetTargetSpec.SboxHumanMalePath||path==RetargetTargetSpec.SboxCitizenPath))
            {UseFirstPersonArms(path==RetargetTargetSpec.SboxCitizenPath);}
            else if(!_targetChosen&&!firstPerson&&IsFirstPersonArms(path))
            {UseBuiltinTarget(path==TargetPickers.CitizenArmsPath);}
            FitMocapPlacementToTarget();SetPreviewView(firstPerson);RefreshContacts();_=RefreshMocapPreviewAsync();
        }
    }
    public void SetPreviewView(bool firstPerson)
    {
        _previewFirstPerson=firstPerson;
        if(_viewPicker.SelectedIndex!=(firstPerson?0:1))_viewPicker.SelectedIndex=firstPerson?0:1;
        if(_mocapPreview.IsValid())_mocapPreview.FirstPerson=firstPerson;
    }
    internal void ShowAdjustments()
    {
        if(_adjustments.IsValid()){_adjustments.Show();_adjustments.Window.Raise();return;}
        _adjustments=new Dialog(this);_adjustments.Window.Title="Mocap adjustments";
        _adjustments.Layout=Layout.Column();_adjustments.Layout.Margin=12;
        var scroll=_adjustments.Layout.Add(new ScrollArea(_adjustments),1);
        _advancedPanel.Parent=scroll;scroll.Canvas=_advancedPanel;_advancedPanel.Visible=true;
        // Preserve the settings widgets when the user closes this separate window.
        _adjustments.Window.DeleteOnClose=false;
        _adjustments.Window.MinimumSize=new Vector2(760,460);_adjustments.Window.Size=new Vector2(800,580);_adjustments.Show();
    }
    /// <summary>The Smoothing field: 0 (off) or 0.5 to 10, clamped; blank or invalid keeps the default.</summary>
    float SmoothingStrength()
    {
        var value=Number(_smoothing,7);if(!float.IsFinite(value))return 7;
        return value<=0?0:Math.Clamp(value,.5f,10);
    }
    public void LoadVideo(string path)
    {
        // A lens override belongs to this recording. Never carry it into a new
        // upload, including queued phone videos from another camera.
        if(!string.Equals(_sourcePath.Text,path,StringComparison.OrdinalIgnoreCase))_recordingFov.Text="";
        _welcome.Visible=false;_previewArea.Visible=true;_transportBar.Visible=true;
        Update();
        _videoName.Text=Path.GetFileName(path);_videoName.ToolTip=path;
        _sourcePath.Text=path;_videoHost.Layout.Clear(true);
        _video=_videoHost.Layout.Add(new MocapVideoWidget(_videoHost,path),1);
        _video.TogglePlayback=TogglePlayback;
        _video.Show();
        ResetPlayback();
        _captureStatus.Text="Inspect source visibility and lens distortion. No camera calibration is assumed.";
    }
    public async Task LoadMotionAsync(string path,string originalPath=null,CleanupSettings initialCleanup=null)
    {
        var revision=++_motionLoadRevision;
        try
        {
            var loaded=await Task.Run(()=>{var session=MocapAdjustmentStore.Load(path,originalPath,initialCleanup);return(session,quality:MotionDiagnostics.Analyze(session.Raw));});
            var doc=loaded.session.Edited;
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid()||revision!=_motionLoadRevision)return;
            InvalidateMocapPreview();
            _editSession=loaded.session;_rawMotion=loaded.session.Raw;_editedMotion=doc;_motionPath=path;
            RefreshBodyRefinementButton();
            _appliedCleanup=loaded.session.State.Cleanup;ResetAdjustmentFields();
            if(File.Exists(doc.SourceVideo))LoadVideo(doc.SourceVideo);
            else
            {
                _videoHost.Layout.Clear(true);_video=null;_videoName.Text="Source video unavailable";_videoName.ToolTip=doc.SourceVideo;
                _sourcePath.Text=doc.SourceVideo;_videoHost.Layout.Add(new Label("The source video could not be found.\nAnimation playback is still available.",_videoHost){Alignment=TextFlag.Center,WordWrap=true},1);
                ResetPlayback();
            }
            RestoreRecordingFov(doc);
            RefreshContacts();
            var missingHands=loaded.quality.Tracks.Where(t=>t.Role is HumanoidMocap.Mapping.BoneRole.HandL or HumanoidMocap.Mapping.BoneRole.HandR)
                .Where(t=>HandCaptureRetargeter.Supports(doc)&&t.Reconstructed<doc.Frames.Count/2d)
                .Select(t=>$"{(t.Role==HumanoidMocap.Mapping.BoneRole.HandL?"Left":"Right")} hand {(t.Reconstructed==0?"not detected":"mostly untracked")}").ToArray();
            // A hand that never appears beside a well-tracked one is a one-handed recording, not a tracking fault.
            var handTracks=loaded.quality.Tracks.Where(t=>t.Role is HumanoidMocap.Mapping.BoneRole.HandL or HumanoidMocap.Mapping.BoneRole.HandR).ToArray();
            var absent=handTracks.Where(t=>t.Reconstructed==0).ToArray();
            var oneHanded=HandCaptureRetargeter.Supports(doc)&&handTracks.Length==2&&absent.Length==1&&missingHands.Length==1&&
                handTracks.Single(t=>t.Reconstructed>0).Reconstructed>=doc.Frames.Count/2d;
            if(oneHanded)missingHands=Array.Empty<string>();
            _captureStatus.Text=missingHands.Length==0?$"Ready · {doc.Frames.Count} frames{(oneHanded?$", {(absent[0].Role==HumanoidMocap.Mapping.BoneRole.HandL?"right":"left")} hand only":"")}. Review the animation, then export."
                :$"Review needed · {string.Join("; ",missingHands)}. See Advanced for tracking coverage.";
            if(missingHands.Length==0&&doc.Diagnostics.Any(d=>d.StartsWith("Review hand pose:",StringComparison.Ordinal)))
                _captureStatus.Text="Review needed · Reconstructed hands do not match the hands found in the video."+
                    (doc.Backend.StartsWith("WiLoR",StringComparison.Ordinal)?" Check the video against the preview before exporting.":" Choose Advanced → Hand models… → WiLoR, then Process again.");
            else if(missingHands.Length==0&&doc.Diagnostics.Any(d=>d.StartsWith("Review wrist placement:",StringComparison.Ordinal)))
                _captureStatus.Text="Review needed · Hand projection disagrees with detected image landmarks. See Advanced before exporting.";
            if(missingHands.Length==0&&doc is {Space:MotionSpace.WorldRelative,OriginalReconstruction:not null,StationaryJoints.Count:>0})
                _captureStatus.Text=$"Ready · {doc.Frames.Count} frames · {(doc.Corrections.Any(c=>c.Type.Contains("followed-camera",StringComparison.Ordinal))?"moving camera followed":"still camera")}, foot contacts anchored. Review the animation, then export.";
            // Written by the worker when edited footage cut to another shot; see InferenceWorker/ShotCutDetector.cs.
            if(doc.Diagnostics.FirstOrDefault(d=>d.StartsWith("Footage cuts to another shot",StringComparison.Ordinal)) is { } cut)
                _captureStatus.Text+=" "+cut;
            // Written by the worker when the performer entered late or left the picture; see InferenceWorker/BodyCapture.cs.
            if(doc.Diagnostics.FirstOrDefault(d=>d.StartsWith("Performer not in view for the whole video",StringComparison.Ordinal)) is { } partlyVisible)
                _captureStatus.Text+=" "+partlyVisible;
            if(doc.Diagnostics.FirstOrDefault(d=>d.StartsWith("Long video:",StringComparison.Ordinal)) is { } longVideo)
                _captureStatus.Text+=" "+longVideo;
            _motionDetails.Text=$"{doc.Backend} · {doc.Space}. "+loaded.quality.HandSummary+" "+string.Join(" ",doc.Diagnostics);
            if(loaded.session.Notice is { } notice)_captureStatus.Text+=" "+notice;
            await RefreshMocapPreviewAsync();
        }
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid()&&revision==_motionLoadRevision)_captureStatus.Text=e.Message;}
    }
    void RestoreRecordingFov(MotionDocument motion)
    {
        _recordingFov.Text="";_recordingFov.PlaceholderText="Automatic";
        if(!new[]{"WildHands /","WiLoR /","MobileHand /"}.Any(prefix=>motion.Backend.StartsWith(prefix,StringComparison.Ordinal)))return;
        var camera=motion.Cameras.SingleOrDefault(c=>c.Id=="video");
        if(camera?.ImageWidth is not int width||camera.ImageHeight is not int height||camera.Intrinsics is not {Length:9} k||
            camera.Calibrated||camera.Distortion?.Any(v=>v!=0)==true||k[0]!=k[4]||k[2]!=width*.5f||k[5]!=height*.5f)return;
        var automatic=CaptureCameraFraming.EstimatedFocalLength(width,height);
        if(Math.Abs(k[0]-automatic)<automatic*1e-6f)return;
        var fov=2*MathF.Atan(width/(2*k[0]))*180/MathF.PI;
        // A lens the worker sized from the hands is still the automatic choice: show it, but keep the
        // field blank so Process again estimates afresh instead of freezing this value as if typed.
        if(camera.Source?.StartsWith("Pinhole camera sized from typical first-person hand distance",StringComparison.Ordinal)==true)
        {if(float.IsFinite(fov))_recordingFov.PlaceholderText=FormattableString.Invariant($"Automatic · {fov:F0}°");return;}
        if(float.IsFinite(fov)&&fov>=20&&fov<=150)_recordingFov.Text=fov.ToString("G9",CultureInfo.InvariantCulture);
    }

    async Task BuildMocapPreviewAsync(int revision)
    {
        if(_editedMotion is null || _target is null)return;
        _previewPending=true;_bakedPreview=null;UpdateTrackingStatus();UpdateExportAvailability();
        try
        {
            if(_fbxPreviewTask is not null)await _fbxPreviewTask;
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid()||revision!=_previewRevision)return;
            var target=_target;var spec=target.Spec;var motion=_editedMotion;
            var bytes=Encoding.UTF8.GetBytes(motion.ToJson());var corrections=CaptureTargetCorrections();var rootMotion=_rootMotion;
            var session=_editSession;var cleanup=_appliedCleanup;var targetKey=MocapAdjustmentStore.TargetKey(spec,_firstPerson);
            var edit=new MocapAdjustmentStore.TargetEdit{Corrections=corrections,RootMotion=rootMotion,
                Fov=Number(_fov,75),ViewPitch=Number(_viewPitch,0),NearClip=Number(_viewNear,15)};
            var result=await Task.Run(()=>Retargeter.Convert(new RetargetRequest{SourceData=bytes,SourceFileName="capture.hmotion",FootPlantCleanup=!corrections.FirstPerson,ArmEffectorIk=false,MocapCorrections=corrections,RootMotion=rootMotion},spec));
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid()||revision!=_previewRevision)return;
            var clip=result.Clips.FirstOrDefault(c=>c.Success);
            if(clip is null)throw new InvalidOperationException("No convertible motion. Check the bone mapping.");
            _targetHost.Layout.Clear(true);
            _mocapPreview=_targetHost.Layout.Add(new PreviewWidget(_targetHost,spec.Rig,target.PreviewModelPath,target.PreviewPositionScale,spec.UpAxis),1);
            _mocapPreview.Playing=false;_mocapPreview.FirstPerson=_previewFirstPerson;_mocapPreview.ViewmodelFov=edit.Fov;_mocapPreview.ViewmodelPitch=edit.ViewPitch;
            _mocapPreview.ShowTargetBones=_showTargetBones;
            var handCoverage=motion.Bones.Select((b,i)=>(b,i))
                .Where(x=>x.b.Role is HumanoidMocap.Mapping.BoneRole.HandL or HumanoidMocap.Mapping.BoneRole.HandR)
                .Select(x=>(Role:x.b.Role.Value,Count:motion.Frames.Count(f=>f.Evidence[x.i]==JointEvidence.Reconstructed))).ToArray();
            var strongest=handCoverage.Select(x=>x.Count).DefaultIfEmpty(0).Max();
            // A briefly detected hand spends most of the clip held or at rest.
            // It must not pull the FPS camera away from the consistently tracked hand.
            _mocapPreview.FramingHands=handCoverage.Where(x=>x.Count>0&&x.Count>=strongest*.5f).Select(x=>x.Role).ToArray();
            _mocapPreview.ViewmodelNearClipCm=edit.NearClip;
            // A camera that faced the performer is not where a first-person view looks from.
            if(HandCaptureRetargeter.Supports(motion))_mocapPreview.CaptureView=corrections.CaptureFacesSubject?null:corrections;
            var props=new PropContactMotion(motion);var propPlacement=CapturePlacement.ForTarget(spec.UpAxis,corrections);
            var supportsProps=HandCaptureRetargeter.Supports(motion)&&corrections.FirstPerson&&rootMotion==HumanoidMocap.Cleanup.RootMotionMode.Off
                &&motion.Objects.All(p=>p.Space==motion.Space);
            if(supportsProps){_mocapPreview.CaptureProps=props;_mocapPreview.PropPlacement=propPlacement;_mocapPreview.ContactTargetKey=corrections.ContactTargetKey;}
            _welcome.Visible=false;_previewArea.Visible=true;_transportBar.Visible=true;
            Update();
            _previewFps=clip.Fps;_mocapPreview.SetClip(clip);_mocapPreview.ResetView();
            _mocapPreview.Show();_targetHost.Update();
            _bakedPreview=new BakedPreview(clip,spec,motion.Space.ToString(),revision,corrections.FirstPerson,props,propPlacement,supportsProps,motion,corrections.WristOffsets);
            SynchronizePreview();
            RefreshContacts();
            SaveAppliedAdjustments(session,targetKey,edit,cleanup,motion);
        }
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid()&&revision==_previewRevision)_captureStatus.Text=e.Message;}
        finally
        {
            await EditorPipeline.SwitchToMainThread();
            if(this.IsValid()&&revision==_previewRevision){_previewPending=false;UpdateExportAvailability();}
        }
    }
    async Task ProcessMotionAsync()
    {
        if(_rawMotion is null || _processing is not null)return;
        _processing=new CancellationTokenSource();var token=_processing.Token;
        SetCaptureBusy(true);
        try
        {
            var settings=new CleanupSettings{Root=Number(_rootSmooth,.25f),Arms=Number(_armSmooth,.1f),Fingers=Number(_fingerSmooth,.025f)};
            settings.Smoothing=settings.PositionSmoothing=SmoothingStrength();
            var raw=_rawMotion;var contacts=_editedMotion.Copy().Contacts;var session=_editSession;
            var doc=await Task.Run(()=>{token.ThrowIfCancellationRequested();var copy=raw.Copy();copy.Contacts=contacts;var edited=MotionCleanup.Apply(copy,settings);token.ThrowIfCancellationRequested();return edited;},token);
            await EditorPipeline.SwitchToMainThread();if(!this.IsValid())return;token.ThrowIfCancellationRequested();
            if(session!=_editSession)return;
            _editedMotion=doc;_appliedCleanup=settings;
            _captureStatus.Text="Adjustments applied. Original observations are preserved.";
            await RefreshMocapPreviewAsync();
        }
        catch(OperationCanceledException){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="Cancelled. Original motion preserved.";}
        catch(Exception e){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=e.Message;}
        finally
        {
            await EditorPipeline.SwitchToMainThread();_processing.Dispose();_processing=null;
            if(this.IsValid())
            {
                SetCaptureBusy(false);
                if(_queuedVideos.TryDequeue(out var queued)){SetWorkspace(queued.FirstPerson);ImportVideoAndProcess(queued.Path,queued.Start,queued.End);}
            }
        }
    }
    static float Number(LineEdit field,float fallback)=>float.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&float.IsFinite(n)?n:fallback;
    Task SetCaptureTiltAsync(float degrees)
    {
        _capturePitch.Text=degrees.ToString(CultureInfo.InvariantCulture);
        return RefreshMocapPreviewAsync();
    }
    /// <summary>Re-place the capture camera for the chosen viewpoint, starting from this target's defaults.</summary>
    Task SetCaptureFacingAsync(bool faces)
    {
        if(_target is null)return Task.CompletedTask;
        var settings=TargetCorrectionSettings.ForRig(_target.Spec.Rig,_target.Spec.UpAxis);
        if(faces)CaptureViewpoint.PlaceFacingCamera(settings);
        _captureFacesSubject=faces;ShowCapturePlacement(settings);
        return RefreshMocapPreviewAsync();
    }
    void ShowCapturePlacement(TargetCorrectionSettings settings)
    {
        _capturePosition.Text=FormattableString.Invariant($"{settings.CaptureCameraPosition.X:0.######},{settings.CaptureCameraPosition.Y:0.######},{settings.CaptureCameraPosition.Z:0.######}");
        _captureYaw.Text=settings.CaptureCameraYawDegrees.ToString("G9",CultureInfo.InvariantCulture);_capturePitch.Text="0";
        SyncFacingControl();
    }
    void SyncFacingControl()
    {
        if(!_facingControl.IsValid())return;
        _settingFacingControl=true;try{_facingControl.Value=_captureFacesSubject;}finally{_settingFacingControl=false;}
    }
    void FitMocapPlacementToTarget()
    {
        if(_target is null||_shoulderL is null)return;
        _ground.Text="0";_facing.Text="0";_reach.Text="0.995";
        _fov.Text=(_firstPerson&&_editedMotion is not null?CaptureCameraFraming.InitialHorizontalFov(_editedMotion):75).ToString(CultureInfo.InvariantCulture);
        _viewPitch.Text="0";_viewNear.Text="15";
        _rootMotion=HumanoidMocap.Cleanup.RootMotionMode.Off;_inPlaceControl.Value=false;
        _stabilizeFeetControl.Value=true;
        _wristOffsets.Clear();++_wristEditRevision;
        var settings=TargetCorrectionSettings.ForRig(_target.Spec.Rig,_target.Spec.UpAxis);
        // Footage filmed facing the performer: put the capture camera in front of the character, looking back.
        _captureFacesSubject=_firstPerson&&_rawMotion is not null&&HandCaptureRetargeter.Supports(_rawMotion)&&CaptureViewpoint.FacesSubject(_rawMotion);
        if(_captureFacesSubject)CaptureViewpoint.PlaceFacingCamera(settings);
        // These editable values also feed the solver; retain sub-millimetre rig
        // precision instead of shortening the shoulder span through display rounding.
        string Coordinates(System.Numerics.Vector3 v)=>FormattableString.Invariant($"{v.X:0.######},{v.Y:0.######},{v.Z:0.######}");
        _shoulderL.Text=Coordinates(settings.LeftShoulder);_shoulderR.Text=Coordinates(settings.RightShoulder);
        _elbowL.Text=Coordinates(settings.LeftElbow);_elbowR.Text=Coordinates(settings.RightElbow);
        _capturePosition.Text=Coordinates(settings.CaptureCameraPosition);
        _captureYaw.Text=settings.CaptureCameraYawDegrees.ToString("G9",CultureInfo.InvariantCulture);_capturePitch.Text="0";
        RestoreTargetAdjustments();
        SyncFacingControl();
        RefreshWristOffsetRows();
    }

    TargetCorrectionSettings CaptureTargetCorrections() => new()
    {
        FirstPerson=_firstPerson,LeftShoulder=Vector(_shoulderL),RightShoulder=Vector(_shoulderR),
        ContactTargetKey=_target is null?"":MocapAdjustmentStore.TargetKey(_target.Spec,_firstPerson),
        LeftElbow=Vector(_elbowL),RightElbow=Vector(_elbowR),Reach=Number(_reach,.995f),
        GroundOffset=Number(_ground,0),FacingDegrees=Number(_facing,0),
        StabilizeFeet=_stabilizeFeetControl.Value,
        WristOffsets=_wristOffsets.Select(e=>e.Copy()).ToList(),
        CaptureCameraPosition=Vector(_capturePosition),CaptureCameraYawDegrees=Number(_captureYaw,180),CaptureCameraPitchDegrees=Number(_capturePitch,0),
        CaptureFacesSubject=_captureFacesSubject
    };
    static System.Numerics.Vector3 Vector(LineEdit edit)
    {
        var parts=edit.Text.Split(',');
        if(parts.Length!=3)throw new ArgumentException("Enter three metre coordinates separated by commas.");
        return new(float.Parse(parts[0],CultureInfo.InvariantCulture),float.Parse(parts[1],CultureInfo.InvariantCulture),float.Parse(parts[2],CultureInfo.InvariantCulture));
    }
    void RefreshContacts()
    {
        RefreshWristOffsetRows();
        _contactTimeline?.SetMotion(_editedMotion,_wristOffsets);
        _contactRows.Clear(true);
        var actions=_contactRows.AddRow();actions.Spacing=8;
        var supported=_firstPerson&&_editedMotion is not null&&HandCaptureRetargeter.Supports(_editedMotion)&&_editedMotion.Space==MotionSpace.CameraRelative;
        actions.Add(new Button("Import prop FBX…","view_in_ar"){Enabled=supported,Clicked=PickPropAnimation,
            ToolTip="Import animated prop bones with explicit camera-space alignment into a separate capture."});
        actions.Add(new Button("Add contact…","add"){Enabled=supported&&_editedMotion.Objects.Count>0,Clicked=()=>OpenContactEditor()});
        actions.Add(new Button("Suggest contacts","auto_fix_high"){Enabled=supported&&_editedMotion.Objects.Any(p=>p.Surfaces.Any(s=>s.Triangles.Length>0)),Clicked=()=>_=SuggestPropContactsAsync(),
            ToolTip="Use imported rigid surfaces, finger bend and relative motion to propose intervals. Suggestions require review."});
        actions.AddStretchCell();
        if(_editedMotion is { Objects.Count: >0 })
            _contactRows.Add(new Label("Prop armatures · "+string.Join(", ",_editedMotion.Objects.Select(p=>$"{p.Id} ({p.Source})")),this){WordWrap=true});
        if(_editedMotion is null || _editedMotion.Contacts.Count==0)
        {
            _contactRows.Add(new Label("No contact suggestions. A hand track alone does not recover prop motion.",this));return;
        }
        foreach(var contact in _editedMotion.Contacts)
        {
            var row=_contactRows.AddRow();row.Spacing=8;
            var label=row.Add(new Label($"{contact.Start:F2}–{contact.End:F2}s · {contact.Bone} → {contact.Object} · {contact.Review}"+(contact.LocalRotation is null?"":" · Rotation held"),this),1);
            if(contact.FingerTargets.Count>0)
            {
                var key=_target is null?"":MocapAdjustmentStore.TargetKey(_target.Spec,_firstPerson);
                label.Text+=$" · Finger points {contact.FingerTargets.Count(t=>t.TargetKey==key)}/{contact.FingerTargets.Count} for this target";
            }
            label.SetStyles($"color: {(contact.Review==ContactReview.Suggested?Theme.Yellow:Theme.TextLight).Hex};");
            var reason=new PropContactMotion(_editedMotion).UnsupportedReason(contact);
            label.ToolTip=reason??contact.Reason+(contact.LocalRotation is null?" Wrist position only; captured wrist rotation is preserved.":" Wrist position and orientation follow the prop bone.")+" Unconstrained fingers keep captured articulation. Optional finger points apply bounded hinge corrections on their authored target only. Pink markers show finger points, yellow/green markers their reviewed targets. Reach limits may leave a residual gap.";
            row.Add(new IconButton("play_arrow",()=>{
                var range=PlaybackRange;SeekPlaybackFraction(range.Last>range.Start?(float)(((contact.Start+contact.End)*.5-range.Start)/(range.Last-range.Start)):0);
            },this){FixedSize=24,IconSize=16,ToolTip="Review the middle of this interval"});
            var confirm=row.Add(new Button("Confirm","check"));confirm.Clicked=()=>_=ReviewContactAsync(contact,ContactReview.Confirmed);
            confirm.Enabled=reason is null;confirm.ToolTip=reason??"Apply the object-local wrist target to the target arm solve.";
            var disable=row.Add(new Button("Disable","block"));disable.Clicked=()=>_=ReviewContactAsync(contact,ContactReview.Disabled);
            row.Add(new IconButton("edit",()=>OpenContactEditor(contact),this){FixedSize=24,IconSize=16,Enabled=reason is null,ToolTip="Edit interval and wrist anchor"});
        }
    }
    [EditorEvent.Frame]
    public void MocapTick()
    {
        if(!this.IsValid())return;
        _video?.Present();
        if(Interlocked.Exchange(ref _workerMessage,null) is { } message)_captureStatus.Text=message;
        TickPlayback();
    }
}
