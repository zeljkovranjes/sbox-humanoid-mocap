# Humanoid Mocap C# worker

Experimental local body reconstruction with GVHMR, HMR2 and ViTPose-H. The worker is C# and calls native LibTorch and OpenCV directly. It does not run Python. Baked animations do not depend on the worker.

Install the .NET 10 SDK on Windows x64. From the repository root:

```powershell
dotnet run --project InferenceWorker -- download-body-models models
dotnet run --project InferenceWorker -- body-capture body-job.json
```

The first command downloads and verifies 5,530,829,656 bytes of pinned body checkpoints/model data. NuGet also restores the pinned native CPU dependencies. These files stay local and are reused.

Example `body-job.json` (use your own absolute paths and person crop):

```json
{
  "Video": "D:/Videos/movement.mp4",
  "Models": "D:/Mocap/models",
  "Output": "D:/Mocap/jobs",
  "Start": 0,
  "End": 1,
  "PersonCrop": { "CenterX": 390, "CenterY": 410, "Size": 600 }
}
```

Range boundaries are seconds in the original video; the end is exclusive. Crop coordinates are pixels in the original video. `Size` describes a square around the person. Keep the person inside it throughout the selected range. The crop is currently manual and constant, not automatically tracked. MP4/MOV metadata and the installed Windows video decoder must support the input.

The command prints the resulting `raw-body.hmotion` path. Import this file into Humanoid Mocap, select a target rig, inspect the animation, and export it. Raw observations, image features and network predictions remain beside it. Ctrl+C cancels between inference operations; rerunning the same request resumes completed frames. Up to 1,800 selected frames are allowed. Only one vision model is loaded at a time, using four CPU threads.

This first body path is camera-relative and uses an explicit identity camera-rotation conditioning assumption. Camera intrinsics are estimated from the image dimensions. Camera recovery, moving person crops, source contact/limb IK and direct editor job controls are still being integrated. It does not reconstruct detailed fingers or object tracks. No calibrated metric scale or world-root-motion accuracy is claimed.

Tested on Ryzen 7 7800X3D with 32 GB RAM: 29 real tennis-video frames took about 124 seconds, including checkpoint loading, and peaked at 6.4 GB worker RAM. The native image models ran on CPU; GPU inference and VRAM usage have not been validated. A cached rerun completed in under three seconds. These measurements are not minimum hardware requirements.

The exported slice was imported, retargeted, previewed, compiled and played in s&box. It remains raw reconstruction requiring review and correction. See the repository's third-party notices and `Editor/HumanoidMocap/Inference/Gvhmr.LICENSE`.
