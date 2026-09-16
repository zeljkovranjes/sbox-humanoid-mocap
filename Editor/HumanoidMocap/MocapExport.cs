using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using HumanoidMocap.Formats.Fbx;
using HumanoidMocap.Motion;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    bool _exportingMotion;

    async void PickMotionExport(bool retarget)
    {
        var dialog = new FileDialog(null) { Title = "Export armature and animation", DefaultSuffix = ".fbx" };
        dialog.SelectFile("capture.fbx"); dialog.SetFindFile(); dialog.SetModeSave(); dialog.SetNameFilter("FBX animation (*.fbx)");
        string path;
        try { if (!dialog.Execute()) return; path = dialog.SelectedFile; }
        finally { dialog.Destroy(); }
        if (!Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase)) path += ".fbx";
        try
        {
            await ExportMotionFbxAsync(path, retarget);
            await EditorPipeline.SwitchToMainThread();
            if (this.IsValid()) _captureStatus.Text = $"Exported {Path.GetFileName(path)} · armature and bone animation.";
        }
        catch (Exception e) { await EditorPipeline.SwitchToMainThread(); if (this.IsValid()) _captureStatus.Text = e.Message; }
    }

    internal async Task ExportMotionFbxAsync(string path, bool retarget)
    {
        if (_exportingMotion || _processing is not null) throw new InvalidOperationException("Wait for the current operation to finish.");
        if (_editedMotion is null) throw new InvalidOperationException("Reconstruct or open motion before exporting.");
        if (retarget && _target is null) throw new InvalidOperationException("Select a target rig to retarget the animation.");
        var destination = Path.GetFullPath(path);
        if (_target?.ModelFilePath is { } modelPath && string.Equals(destination, Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a new animation filename to preserve the original target model.");
        _exportingMotion = true;
        UpdateExportAvailability();
        try
        {
            while(retarget)
            {
                await EditorPipeline.SwitchToMainThread();
                if(!_previewPending)break;
                await _mocapPreviewTask;
            }
            await EditorPipeline.SwitchToMainThread();
            if (!this.IsValid()) return;
            var motion = _editedMotion;var preview=_bakedPreview;
            if(retarget&&preview is null)throw new InvalidOperationException("Wait for a valid animation preview before exporting.");
            var bytes = await Task.Run(() =>
            {
                if (!retarget) return MotionFbxExport.Write(motion);
                // Export the exact baked transforms shown in the viewport. Unapplied
                // edits in Advanced must not silently change the exported animation.
                var clip=preview.Clip;var target=preview.Target;
                if(preview.Props.Objects.Count>0&&!preview.SupportsProps)
                    throw new InvalidOperationException("Target prop export currently needs First Person workspace, camera-relative hand capture and root-motion removal off. Export the captured skeleton to retain objects in source coordinates.");
                var sourceTimes=Enumerable.Range(0,clip.SolvedFrames.Count).Select(i=>Math.Min(preview.Props.StartTime+i/(double)clip.Fps,preview.Props.EndTime)).ToArray();
                var combined=PropAnimation.Append(target.Rig.Skeleton,clip.SolvedFrames,sourceTimes,preview.Props,preview.Placement);
                return FbxAnimationWriter.Write(combined.Skeleton, clip.ClipName, combined.Frames,
                    Enumerable.Range(0, clip.SolvedFrames.Count).Select(i => i / (double)clip.Fps).ToArray(), clip.Fps,
                    target.UpAxis == TargetUpAxis.YUpCm ? 1 : 2, target.UpAxis == TargetUpAxis.YUpCm ? 1 : 2.54,
                    preview.Space + "; retargeted");
            });
            // Stage next to the destination. A failed write leaves any existing export intact.
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes);
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { await EditorPipeline.SwitchToMainThread(); _exportingMotion = false;if(this.IsValid())UpdateExportAvailability(); }
    }
}
