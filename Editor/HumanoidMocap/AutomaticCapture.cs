using System;
using System.Collections.Generic;
using System.IO;
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
        var start = Number(_rangeStart, 0); double? end = string.IsNullOrWhiteSpace(_rangeEnd.Text) ? null : Number(_rangeEnd, 0);
        _processing = new CancellationTokenSource(); var token = _processing.Token;
        SetCaptureBusy(true); _retryCaptureButton.Visible = false;
        _exportMotionButton.Enabled = false; _rawMotion = null; _editedMotion = null; _motionPath = null;
        _editSession=null;_appliedCleanup=null;
        ++_motionLoadRevision;InvalidateMocapPreview();
        _targetHost.Layout.Clear(true);_mocapPreview=null;
        _targetHost.Layout.Add(new global::Editor.Label("Preparing animation…",_targetHost){Alignment=TextFlag.Center},1);
        ResetPlayback();RefreshContacts();_motionDetails.Text="";
        try
        {
            _captureStatus.Text = "Preparing video…";
            var metadata = await Task.Run(() => Mp4Metadata.Read(video), token);
            await EditorPipeline.SwitchToMainThread(); if (!this.IsValid()) return;
            if (metadata.Times.Length > 1800 && !end.HasValue)
                throw new InvalidOperationException("This video is longer than the current 1,800-frame limit. Select a shorter range in Advanced.");
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
                    ReceiveWorkerProgress,token);
            }
            else
            {
                motionPath = await NativeCapture.BodyAsync(video, start, end ?? metadata.Duration, metadata.Width, metadata.Height,
                    ReceiveWorkerProgress, token);
            }
            token.ThrowIfCancellationRequested();
            await EditorPipeline.SwitchToMainThread(); if (!this.IsValid()) return;
            Interlocked.Exchange(ref _workerMessage,null);
            _captureStatus.Text = "Cleaning motion…";
            var cleanup = new CleanupSettings { Root = Number(_rootSmooth, .1f), Arms = Number(_armSmooth, .1f), Fingers = Number(_fingerSmooth, .025f) };
            var cleanedPath = await Task.Run(() =>
            {
                var raw = MotionDocument.Parse(File.ReadAllBytes(motionPath));
                // GVHMR already has a temporal model; do not stack generic cleanup on it.
                var cleaned = firstPerson ? MotionCleanup.Apply(raw, cleanup) : raw.Copy();
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
                if (_queuedVideos.TryDequeue(out var queued)) { SetWorkspace(queued.FirstPerson); ImportVideoAndProcess(queued.Path,queued.Start,queued.End); }
            }
        }
    }

    void SetCaptureBusy(bool busy)
    {
        _uploadVideoButton.Enabled = !busy; _workspacePicker.Enabled = !busy;
        _cancelCaptureButton.Visible = busy; _advancedPanel.Enabled = !busy;
        UpdateExportAvailability();
    }
}
