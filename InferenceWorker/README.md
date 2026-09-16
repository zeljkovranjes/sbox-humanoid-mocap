# Humanoid Mocap C# worker

Experimental local body and hand reconstruction. The worker is C# and calls native LibTorch and OpenCV directly. It does not run Python. Baked animations do not depend on the worker.

The editor starts this worker automatically for Third Person, or for the optional WildHands and WiLoR hand models. Select a workspace, upload a video, and inspect the result. **Advanced → Hand models…** changes the model used for subsequent FPS uploads. MediaPipe remains the lightweight default and runs directly in C# in the editor. ACE is not downloaded or executed.

First-time native worker setup requires .NET 10 SDK and the complete repository checkout. The editor publishes the included worker into `%LOCALAPPDATA%/sbox-humanoid-mocap/worker/<source fingerprint>`. Changes to its C# sources or pinned dependencies automatically select a new build; an interrupted build is retried. To use a prebuilt worker, set `HUMANOID_MOCAP_WORKER` to its executable. Explicitly configured builds are maintained by their owner. `HUMANOID_MOCAP_MODELS` optionally selects a shared model folder. Models are downloaded only for the selected backend and verified against pinned hashes.

Install the .NET 10 SDK on Windows x64. From the repository root:

```powershell
dotnet run --project InferenceWorker -- download-body-models models
dotnet run --project InferenceWorker -- body-capture body-job.json
```

The first command downloads and verifies 5,530,829,656 bytes of pinned body checkpoints/model data. NuGet also restores the pinned native CPU dependencies. These files stay local and are reused.

To download the four upstream example videos without running any reconstruction model:

```powershell
dotnet run --project InferenceWorker -- download-samples samples
```

This command checks pinned sizes and SHA-256 hashes, parses video metadata and decodes every frame. `samples/download-receipts.json` records each source URL, checksum, duration, dimensions, frame rate and decoded frame count. HTML responses and Git LFS pointers fail validation. Existing valid files are reused; originals are never replaced on a checksum failure. Open the downloaded footage in the editor and select the appropriate workspace. These are example inputs, not labeled ground-truth motion.

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

The command prints `HM_RESULT ` followed by the resulting `raw-body.hmotion` path. The editor opens it automatically; manual worker results can be opened through **Advanced → Open motion…**. Raw observations, image features and network predictions remain beside it. Ctrl+C or an input line containing `cancel` cancels between inference operations; rerunning the same request resumes completed frames. The editor allows eight queued uploads and one active job. Each job allows up to 1,800 selected frames and uses four CPU inference threads. The body worker loads its two vision models sequentially.

This body path is camera-relative and uses an explicit identity camera-rotation conditioning assumption. Camera intrinsics are estimated from the image dimensions. Automatic editor jobs use a full-frame crop for a single visible person; the CLI permits a fixed custom crop. Camera recovery, moving person crops and the upstream final source contact/limb IK are not included in this integration. Target foot correction is applied during retargeting. It does not reconstruct detailed fingers or object tracks. No calibrated metric scale or world-root-motion accuracy is claimed.

Tested on Ryzen 7 7800X3D with 32 GB RAM: 29 real tennis-video frames took about 124 seconds, including checkpoint loading, and peaked at 6.4 GB worker RAM. The native image models ran on CPU; GPU inference and VRAM usage have not been validated. A cached rerun completed in under three seconds. These measurements are not minimum hardware requirements.

The exported slice was imported, retargeted, previewed, compiled and played in s&box. It remains raw reconstruction requiring review and correction. See the repository's third-party notices and `Editor/HumanoidMocap/Inference/Gvhmr.LICENSE`.

For native hand capture, download only the selected model:

```powershell
dotnet run --project InferenceWorker -- download-hand-models models wildhands
dotnet run --project InferenceWorker -- hand-capture hand-job.json
```

Use `wilor` instead of `wildhands` for WiLoR. A hand job contains `Video`, `Models`, `Output`, `Start`, `End`, `Backend` and a `Camera` object with `Fx`, `Fy`, `Cx`, `Cy` and `Calibrated`. Focal lengths and principal point are in pixels. Editor jobs estimate a centered pinhole camera from image dimensions and set `Calibrated` to false. This is not lens calibration. Supply measured parameters through the CLI when available.

WildHands downloads 855,094,722 bytes and WiLoR downloads 2,564,989,533 bytes, plus the shared 7,819,105-byte MediaPipe crop detector. No separate MANO file is needed by these ports: the pinned checkpoints contain the model buffers used by the C# decoder. These files remain local and are not committed. The crop detector runs fresh landmark inference; missing observations remain marked as missing. The ports use MediaPipe crops rather than the original demos' detectors, so upstream accuracy results do not establish this pipeline's accuracy.

On the same Ryzen 7 system, 15 frames of `segment_037.mp4` took 10.8 seconds in WildHands inference with 1.38 GB peak worker RAM, and 31.2 seconds in WiLoR inference with 4.50 GB peak worker RAM. Inference timings exclude initial download/checkpoint loading; RAM includes the complete worker process. Native GPU inference has not been tested. Both produced moving armature-only FBX animations that compiled and played in s&box. These are small functional examples, not ground-truth accuracy or minimum-hardware measurements.
