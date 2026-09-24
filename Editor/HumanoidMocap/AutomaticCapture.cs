using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HumanoidMocap.Motion;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    readonly Queue<(string Path, bool FirstPerson, double Start, double? End)> _queuedVideos = new();
    string _workerMessage;
    void ReceiveWorkerProgress(string message)=>Interlocked.Exchange(ref _workerMessage,message);
    const int MaximumShots=16;const double MinimumShotSeconds=.5;
    /// <summary>The worker's note when a capture stopped at a cut; see the worker's ShotCutDetector.</summary>
    const string ShotCutPrefix="Footage cuts to another shot at ";
    const string ShotsPrefix="Shots start at ";
    static List<double> ShotStarts(IEnumerable<string> diagnostics)
    {
        var note=diagnostics.FirstOrDefault(d=>d.StartsWith(ShotsPrefix,StringComparison.Ordinal));
        if(note is null)return new List<double>();
        return note.Substring(ShotsPrefix.Length).TrimEnd('.',' ','s').Split(',').Select(v=>double.TryParse(v.Trim(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var t)?t:double.NaN).Where(double.IsFinite).ToList();
    }
    static double? NextShotStart(IEnumerable<string> diagnostics)
    {
        var note=diagnostics.FirstOrDefault(d=>d.StartsWith(ShotCutPrefix,StringComparison.Ordinal));
        if(note is null)return null;
        var text=note.Substring(ShotCutPrefix.Length);var end=text.IndexOf(" s",StringComparison.Ordinal);
        return end>0&&double.TryParse(text[..end],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var seconds)?seconds:null;
    }
    /// <summary>One shot: the capture, then world-relative refinement when its camera allows. A camera the
    /// worker measured as still gets world-relative root and foot-contact refinement; one whose rotation it
    /// followed gets the same from GVHMR's world rollout; one that could not be followed stays camera-relative.
    /// The untouched capture stays beside it and Advanced → Restore original capture reopens it.</summary>
    /// <returns>The shot's motion and the capture's own notes (refinement does not keep all of them).</returns>
    async Task<(string Path,List<string> Notes)> CaptureBodyShotAsync(string video,double start,double end,Mp4Metadata metadata,float? recordedFov,CancellationToken token)
    {
        var bodyPath=await NativeCapture.BodyAsync(video,start,end,metadata.Width,metadata.Height,recordedFov,ReceiveWorkerProgress,token);
        var diagnostics=await Task.Run(()=>MotionDocument.Parse(File.ReadAllBytes(bodyPath)).Diagnostics,token);
        if(diagnostics.Any(d=>d.StartsWith(StationaryCameraPrefix,StringComparison.Ordinal)))
            return (await NativeCapture.RefineBodyAsync(bodyPath,ReceiveWorkerProgress,token),diagnostics);
        if(diagnostics.Any(d=>d.StartsWith(FollowedCameraPrefix,StringComparison.Ordinal)))
            return (await NativeCapture.RefineBodyAsync(bodyPath,ReceiveWorkerProgress,token,followedCameraRotation:true),diagnostics);
        // Left relative to its camera: turn it upright with the network's gravity (see CaptureLevel).
        return (await Task.Run(()=>
        {
            var document=MotionDocument.Parse(File.ReadAllBytes(bodyPath));
            if(CaptureLevel.Apply(document)<=0)return bodyPath;
            var levelled=Path.Combine(Path.GetDirectoryName(bodyPath),"levelled-body.hmotion");
            File.WriteAllText(levelled,document.ToJson());return levelled;
        },token),diagnostics);
    }
    internal bool CaptureIsRunning => _processing is not null;
    internal string CaptureMotionPath => _motionPath;
    internal string CaptureMessage => _captureStatus.Text;
    internal bool CaptureCanExport => _exportMotionButton.Enabled;
    internal bool CapturePreviewReady => _mocapPreview.IsValid() && _mocapPreview.FrameCount > 0 && _mocapPreview.HasModel;
    internal bool CaptureWorkspaceFirstPerson => _firstPerson;
    internal bool CapturePreviewFirstPerson => _mocapPreview.IsValid() && _mocapPreview.FirstPerson;
    internal PreviewWidget CapturePreview => _mocapPreview;
    internal void CancelCapture() => _processing?.Cancel();

    public void ImportVideoAndProcess(string path) => ImportVideoAndProcess(path,0,null);

    /// <summary>Process an explicitly selected range; the ordinary upload processes the full video.</summary>
    public void ImportVideoAndProcess(string path, double start, double? end)
    {
        if (_processing is not null)
        {
            if (_queuedVideos.Count >= 8) { _captureStatus.Text = "Upload saved. Eight videos are already queued; open this video after processing finishes."; return; }
            _queuedVideos.Enqueue((path, _firstPerson,start,end));
            _captureStatus.Text = $"Video saved · {_queuedVideos.Count} queued.";
            return;
        }
        LoadVideo(path);
        _rangeStart.Text = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _rangeEnd.Text = end?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";
        _ = ProcessImportedVideoAsync();
    }

    async Task ProcessImportedVideoAsync()
    {
        if (_processing is not null) return;
        if (!File.Exists(_sourcePath.Text)) { _captureStatus.Text = "Upload a video first."; return; }
        var video = _sourcePath.Text; var firstPerson = _firstPerson;var handBackend=_handBackend;
        var recordingFovText=_recordingFov.Text;
        float? recordingFov=null;
        if(firstPerson&&handBackend!="mediapipe"&&!string.IsNullOrWhiteSpace(recordingFovText))
        {
            if(!float.TryParse(recordingFovText,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var degrees)||
                !float.IsFinite(degrees)||degrees<20||degrees>150)
            {_captureStatus.Text="Recording FOV must be a number from 20 to 150 degrees, or blank for automatic.";return;}
            recordingFov=degrees;
        }
        var start = Number(_rangeStart, 0); double? end = string.IsNullOrWhiteSpace(_rangeEnd.Text) ? null : Number(_rangeEnd, 0);
        _processing = new CancellationTokenSource(); var token = _processing.Token;
        SetCaptureBusy(true); _retryCaptureButton.Visible = false;
        _exportMotionButton.Enabled = false; _rawMotion = null; _editedMotion = null; _motionPath = null;
        _editSession=null;_appliedCleanup=null;
        ++_motionLoadRevision;InvalidateMocapPreview();
        _targetHost.Layout.Clear(true);_mocapPreview=null;
        _loader=_targetHost.Layout.Add(new ProcessingIndicator(_targetHost),1);_loader.SetMessage("Preparing video…");_loaderStatus=null;
        ResetPlayback();RefreshContacts();_motionDetails.Text="";
        try
        {
            _captureStatus.Text = "Preparing video…";
            // The lens the camera recorded (iPhones write it), read before any conversion drops the metadata.
            // A lens typed under Advanced still wins.
            float? recordedFov=null;
            try{recordedFov=await Task.Run(()=>Mp4Metadata.Read(video).HorizontalFov,token);}catch(FormatException){}catch(IOException){}
            if(recordedFov is float lensFov&&!(lensFov>=20&&lensFov<=150))recordedFov=null;
            if(firstPerson&&recordingFov is null)recordingFov=recordedFov;
            // Footage Windows cannot decode (iPhone HEVC, 10-bit exports) is converted once and used in its place.
            var playable=await NativeCapture.PlayableVideoAsync(video,ReceiveWorkerProgress,token);
            await EditorPipeline.SwitchToMainThread(); if (!this.IsValid()) return;
            if(playable!=video)
            {
                var fov=_recordingFov.Text;LoadVideo(playable);_recordingFov.Text=fov;
                _videoName.Text=Path.GetFileName(video)+" · converted";_videoName.ToolTip=video;video=playable;
            }
            var metadata = await Task.Run(() => Mp4Metadata.Read(video), token);
            await EditorPipeline.SwitchToMainThread(); if (!this.IsValid()) return;
            // A long video is not refused: the first 1,800 captured frames (about a minute) are processed
            // and the range shows it, so another part can be chosen under Advanced.
            string lengthNote=null;
            var available=metadata.CaptureTimes.Where(t=>t>=start).ToArray();
            if (available.Length > 1800 && !end.HasValue)
            {
                end=available[1800];
                _rangeEnd.Text=end.Value.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
                lengthNote=FormattableString.Invariant($"Long video: captured {start:0.#}–{end.Value:0.#} s of {metadata.Duration:0.#} s (1,800 frames at a time). Set another range under Advanced to capture a later part.");
            }
            string motionPath;
            if (firstPerson && handBackend=="mediapipe")
            {
                if (!File.Exists(_handModelPath))
                {
                    _captureStatus.Text = "Preparing hand capture · downloading the hand model…";
                    _handModelPath = await HandModelStore.EnsureAsync(token);
                    await EditorPipeline.SwitchToMainThread(); if (!this.IsValid()) return;
                }
                motionPath = await HandCaptureJob.RunAsync(video, _handModelPath, _target.Spec.Rig, start, end, _swapHands,
                    (done, total, message) => { if (this.IsValid()) _captureStatus.Text = message; }, token);
            }
            else if(firstPerson)
            {
                motionPath=await NativeCapture.HandsAsync(video,handBackend,start,end??metadata.Duration,metadata.Width,metadata.Height,
                    ReceiveWorkerProgress,token,recordingFov);
            }
            else
            {
                // Edited footage: the worker captures up to the first cut. Every following shot is captured
                // the same way, each with its own camera, and the shots are joined into one motion.
                var shotPaths=new List<string>();double shotEnd=end??metadata.Duration;var shotFailures=new List<string>();
                var (firstShot,firstNotes)=await CaptureBodyShotAsync(video,start,shotEnd,metadata,recordedFov,token);shotPaths.Add(firstShot);
                // The worker lists every shot's start once; each shot is then captured up to the next one.
                var starts=ShotStarts(firstNotes);
                var queue=new List<(double Start,double End)>();
                if(starts.Count>1)for(var i=1;i<starts.Count;i++)queue.Add((starts[i],i+1<starts.Count?starts[i+1]:shotEnd));
                else if(NextShotStart(firstNotes) is double next&&next>start)queue.Add((next,shotEnd));
                // The first capture skips a sliver of a shot at its start (a one-frame flicker read as a cut) and so
                // may already cover the next listed shot.
                var firstEnd=await Task.Run(()=>MotionDocument.Parse(File.ReadAllBytes(firstShot)).Frames[^1].Time,token);
                queue.RemoveAll(q=>q.End-q.Start<MinimumShotSeconds||q.Start<firstEnd);
                for(var i=0;i<queue.Count&&i<MaximumShots-1;i++)
                {
                    var (shotStart,shotStop)=queue[i];
                    ReceiveWorkerProgress(FormattableString.Invariant($"Capturing shot {i+2} of {queue.Count+1}, from {shotStart:0.00} s"));
                    try{shotPaths.Add((await CaptureBodyShotAsync(video,shotStart,shotStop,metadata,recordedFov,token)).Path);}
                    catch(OperationCanceledException){throw;}
                    // A shot the capture cannot follow (a close-up of the legs, nobody in view) is left out.
                    catch(Exception e){shotFailures.Add(FormattableString.Invariant($"The shot from {shotStart:0.00} s was left out: {e.Message}"));}
                }
                string shotFailure=shotFailures.Count>0?string.Join(" ",shotFailures):null;
                motionPath=shotPaths[0];
                if(shotPaths.Count>1||shotFailure is not null)
                {
                    var paths=shotPaths.ToArray();var failure=shotFailure;
                    motionPath=await Task.Run(()=>
                    {
                        var documents=paths.Select(p=>MotionDocument.Parse(File.ReadAllBytes(p))).ToList();
                        // Only the last shot still ends at a cut that was not followed.
                        for(var i=0;i<documents.Count-1;i++)documents[i].Diagnostics.RemoveAll(d=>d.StartsWith(ShotCutPrefix,StringComparison.Ordinal));
                        foreach(var document in documents)document.Diagnostics.RemoveAll(d=>d.StartsWith(ShotsPrefix,StringComparison.Ordinal));
                        var joined=MotionJoin.Join(documents);joined.OriginalReconstruction=null;
                        if(failure is not null)joined.Diagnostics.Add(failure);
                        var destination=Path.Combine(Path.GetDirectoryName(paths[0]),"joined-shots.hmotion");
                        File.WriteAllText(destination,joined.ToJson());return destination;
                    },token);
                }
            }
            token.ThrowIfCancellationRequested();
            await EditorPipeline.SwitchToMainThread(); if (!this.IsValid()) return;
            Interlocked.Exchange(ref _workerMessage,null);
            _captureStatus.Text = "Cleaning motion…";
            var cleanup = new CleanupSettings { Root = Number(_rootSmooth, .25f), Arms = Number(_armSmooth, .1f), Fingers = Number(_fingerSmooth, .025f) };
            cleanup.Smoothing = cleanup.PositionSmoothing = SmoothingStrength();
            // Body captures: foot contacts from UnderPressure replace the body network's stationary-foot guesses,
            // which miss exactly the planted feet that slide. Without the model the capture keeps those guesses.
            string contactModel=null;string contactNote=null;
            if(!firstPerson)
            {
                try{contactModel=await ContactModelStore.EnsureAsync(token);}
                catch(OperationCanceledException){throw;}
                catch(Exception e){contactNote="Foot contacts: the UnderPressure model could not be downloaded ("+e.Message+"); the body network's stationary-foot predictions are used instead.";}
            }
            var cleanedPath = await Task.Run(() =>
            {
                var raw = MotionDocument.Parse(File.ReadAllBytes(motionPath));
                // GVHMR already has a temporal model; do not stack generic cleanup on it.
                var cleaned = firstPerson ? MotionCleanup.Apply(raw, cleanup) : MotionCleanup.SmoothBody(MotionCleanup.RemoveSpikes(MotionCleanup.BridgeDoubtfulFrames(raw).Motion).Motion, cleanup.Smoothing);
                if(lengthNote is not null)cleaned.Diagnostics.Add(lengthNote);
                if(contactNote is not null)cleaned.Diagnostics.Add(contactNote);
                if(contactModel is not null&&new HumanoidMocap.Inference.UnderPressureContacts(contactModel).Estimate(cleaned,token) is { } contacts)
                {
                    cleaned.StationaryJoints.RemoveAll(t=>contacts.ContainsKey(t.Bone));
                    foreach(var (bone,values) in contacts)cleaned.StationaryJoints.Add(new StationaryJointTrack{Bone=bone,Source=HumanoidMocap.Inference.UnderPressureContacts.Source,Probability=values});
                    cleaned.Diagnostics.Add("Foot contacts: "+HumanoidMocap.Inference.UnderPressureContacts.Source+". Planted heels and toes are pinned to the floor while in contact.");
                }
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(Path.GetDirectoryName(motionPath), "automatic.edited.hmotion");
                File.WriteAllText(destination, cleaned.ToJson()); return destination;
            }, token);
            await LoadMotionAsync(cleanedPath,motionPath,firstPerson?cleanup:null);
        }
        catch (OperationCanceledException)
        {
            await EditorPipeline.SwitchToMainThread();
            if (this.IsValid()) { _captureStatus.Text = "Cancelled. Retry resumes cached reconstruction."; _retryCaptureButton.Visible = true; }
            _queuedVideos.Clear();
        }
        catch (Exception e)
        {
            await EditorPipeline.SwitchToMainThread();
            if (this.IsValid()) { _captureStatus.Text = e.Message; _retryCaptureButton.Visible = true; }
        }
        finally
        {
            Interlocked.Exchange(ref _workerMessage,null);
            await EditorPipeline.SwitchToMainThread(); _processing.Dispose(); _processing = null;
            if (this.IsValid())
            {
                SetCaptureBusy(false);
                StartNextQueuedVideo();
            }
        }
    }

    async Task ReinstallWorkerAsync()
    {
        if(_processing is not null){_captureStatus.Text="Finish or cancel the active capture before reinstalling the worker.";return;}
        _processing=new CancellationTokenSource();var token=_processing.Token;
        SetCaptureBusy(true);_retryCaptureButton.Visible=false;_captureStatus.Text="Reinstalling the inference worker…";
        try
        {
            await NativeCapture.ReinstallWorkerAsync(ReceiveWorkerProgress,token);
            await EditorPipeline.SwitchToMainThread();
            if(this.IsValid())_captureStatus.Text="Inference worker reinstalled.";
        }
        catch(OperationCanceledException)
        {
            await EditorPipeline.SwitchToMainThread();
            if(this.IsValid())_captureStatus.Text="Reinstall cancelled. The worker installs again with the next capture.";
        }
        catch(Exception e)
        {
            await EditorPipeline.SwitchToMainThread();
            if(this.IsValid())_captureStatus.Text=e.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _workerMessage,null);
            await EditorPipeline.SwitchToMainThread();_processing.Dispose();_processing=null;
            if(this.IsValid())SetCaptureBusy(false);
        }
    }

    void SetCaptureBusy(bool busy)
    {
        _uploadVideoButton.Enabled = !busy; _workspacePicker.Enabled = !busy; if(!busy)_workspacePicker.Visible = true;
        if(_loader.IsValid()){_loader.Busy=busy;_loaderStatus=null;}
        _cancelCaptureButton.Visible = busy; _advancedPanel.Enabled = !busy;
        _previewTargetPicker.Enabled=!busy;
        UpdateExportAvailability();
    }
    void StartNextQueuedVideo()
    {
        if(_queuedVideos.TryDequeue(out var queued))
        {SetWorkspace(queued.FirstPerson);ImportVideoAndProcess(queued.Path,queued.Start,queued.End);}
    }
}
