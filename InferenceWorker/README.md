# Humanoid Mocap C# worker

Experimental local body and hand reconstruction. The worker is C# and calls native LibTorch and OpenCV directly. It does not run Python. Baked animations do not depend on the worker.

The separate [`import-hot3d` command](../HOT3D.md) imports published hand/object
annotations and produces a synchronized review video without running neural inference.
The editor invokes it when **Advanced → Open motion…** opens an annotated HOT3D Aria archive.

The editor starts this worker automatically for both workspaces and every available hand model. Select a workspace, upload a video, and inspect the result. FPS defaults to WildHands pose reconstruction with MediaPipe hand detection. **Advanced → Hand models…** selects WiLoR, MobileHand or optional MediaPipe-only reconstruction for subsequent FPS uploads. These all run in the separate C# worker. ACE is not downloaded or executed.

First-time native worker setup requires .NET 10 SDK and the complete repository checkout. The editor publishes the included worker into `%LOCALAPPDATA%/sbox-humanoid-mocap/worker/<source fingerprint>`. Changes to its C# sources or pinned dependencies automatically select a new build; an interrupted build is retried. To use a prebuilt worker, set `HUMANOID_MOCAP_WORKER` to its executable. Explicitly configured builds are maintained by their owner. `HUMANOID_MOCAP_MODELS` optionally selects a shared model folder. Models are downloaded only for the selected backend and verified against pinned hashes.

Install the .NET 10 SDK on Windows x64. From the repository root:

```powershell
dotnet run --project InferenceWorker -- download-body-models models
dotnet run --project InferenceWorker -- body-capture body-job.json
```

The first command downloads and verifies 5,542,819,815 bytes of pinned body checkpoints/model data, including the 11,990,159-byte person detector. NuGet also restores the pinned native CPU dependencies. These files stay local and are reused.

Example footage and measured limitations are linked in the [capture guide](../CAPTURE.md).
Test-video download tools, fixtures and verification reports are kept outside the
distributed library. They are not needed to process your own videos.

Example `body-job.json` (use your own absolute paths):

```json
{
  "Video": "D:/Videos/movement.mp4",
  "Models": "D:/Mocap/models",
  "Output": "D:/Mocap/jobs",
  "Start": 0,
  "End": 1
}
```

Range boundaries are seconds in the original video; the end is exclusive. MP4/MOV metadata and the installed Windows video decoder must support the input.

Without `PersonCrop`, a pinned MediaPipe person detector runs through native OpenCV DNN
before the expensive vision models. It selects a prominent subject and follows its
image-space center and scale. Ambiguous subjects, large jumps and tracking loss produce
an error instead of silently switching people. Short interior crop gaps up to 0.25 seconds
can be interpolated using timestamps; missing boundary crops require trimming the range.
Two centered five-frame averages stabilize the crop center and size before reconstruction,
following GVHMR's tracker preprocessing. A wider margin keeps the detected body circle
inside the vision models' central 3:4 input. These averages change image crops, not finished
joint animation, and introduce no causal delay. Boundary crops use replicated samples.
Detection scores and crop evidence remain separate from the reconstructed joints in
`reconstruction.json`. The detector is unloaded before ViTPose/HMR2 processing, and saved
detections are reused after cancellation. This is our single-subject crop tracker, not
GVHMR's original YOLO tracker or calibrated camera recovery.

For an explicit fixed crop, add `"PersonCrop": { "CenterX": 390, "CenterY": 410, "Size": 600 }`.
Coordinates are pixels in the visible, oriented video image, after coded padding is removed.
`Size` sets the square crop height; the vision model uses its central 3:4 region. Keep the
whole person inside that region throughout the selected range. A manual crop skips detection.

The command prints `HM_RESULT ` followed by the resulting `raw-body.hmotion` path. The editor opens it automatically; manual worker results can be opened through **Advanced → Open motion…**. Raw observations, image features and network predictions remain beside it. Ctrl+C or an input line containing `cancel` cancels between inference operations; rerunning the same request resumes completed frames. The editor allows eight queued uploads and one active job. Each job allows up to 1,800 selected frames and uses four CPU inference threads. The body worker loads its two vision models sequentially.

The default body path is camera-relative and uses an explicit identity camera-rotation conditioning assumption. Camera intrinsics are estimated from the image dimensions. Automatic editor jobs use the single-person crop tracker; the CLI also permits a fixed custom crop. Camera recovery is not included. Target foot correction is applied during retargeting. It does not reconstruct detailed fingers or object tracks. No calibrated metric scale or world-root-motion accuracy is claimed.

For footage recorded with a stationary camera, **Advanced → Third Person → Refine · stationary camera**
applies the pinned upstream root/contact processing and two-iteration source-limb CCD.
The resulting motion also retains the six stationary-joint probability tracks. The
editor uses foot/toe tracks for final target-proportion anchoring when **Reduce foot drift**
is enabled. These tracks are separate from pose confidence and object-contact reviews.
It produces estimated world-relative motion under that explicit assumption. The button
then restores the original capture. Moving-camera footage requires a different camera
recovery path and must not use this option.

The equivalent command is `dotnet run --project InferenceWorker -- body-refine refinement-job.json`:

```json
{
  "Motion": "D:/Mocap/jobs/<capture-key>/raw-body.hmotion",
  "Models": "D:/Mocap/models",
  "Output": "D:/Mocap/refinements",
  "AssumeStationaryCamera": true
}
```

Keep `raw-predictions.json` beside the original motion. A new result requires the same
verified `smplx/SMPLX_NEUTRAL.npz` used by capture. No neural network is loaded, footage
is not reprocessed, and the original files are never rewritten. Results are cached by
input hashes, original location, model and refinement version. The output folder holds
the raw gravity/velocity rollout, refined `contact-body.hmotion` and a checksum receipt.
Cancellation leaves the original intact; rerunning finishes this inexpensive stage.
Missing or changed originals and changed cached results produce an error. Preserve
these files when moving a project; the restore reference currently uses an absolute path.

On the tested Ryzen 7 system, refinement version `gvhmr-stationary-contact-ccd-v5` processed
the full 312-frame tennis capture in 0.73 seconds and peaked at 242 MB worker RAM,
excluding editor preview/export. This is one measured
CPU run, not a minimum requirement. Affected joints are labeled as IK-generated;
static probabilities do not become per-joint confidence or observed object contacts.

Tested on Ryzen 7 7800X3D with 32 GB RAM: all 312 tennis-video frames took approximately 18.6 minutes with automatic stabilized crops and peaked at 6.12 GB worker RAM. Person detection took 12.6 seconds, pose inference 713.3 seconds, image features 389.9 seconds, and temporal inference/decoding 2.5 seconds. Other verification overlapped this run, so it is not a controlled speed benchmark. The native image models ran on CPU; GPU inference and VRAM usage have not been validated. An earlier fixed-crop run took about 20 minutes and 5.82 GB RAM. These measurements are not minimum hardware requirements.

Complete Human and Citizen animations have been retargeted, previewed, compiled and played in s&box. Foot drift and occasional pose jumps remain; these functional checks do not establish 3D accuracy or solved contacts. See [drift measurements](../DRIFT_REDUCTION.md), the repository's third-party notices and `Editor/HumanoidMocap/Inference/Gvhmr.LICENSE`.

For hand capture, download only the selected model. For optional MediaPipe-only reconstruction:

```powershell
dotnet run --project InferenceWorker -- download-hand-models models mediapipe
dotnet run --project InferenceWorker -- landmark-capture landmark-job.json
```

Example `landmark-job.json`:

```json
{
  "Video": "D:/Videos/hands.mp4",
  "Model": "D:/Mocap/models/hand_landmarker.task",
  "Output": "D:/Mocap/jobs",
  "Template": "D:/Mocap/Assets/humanoid_mocap/target_rig_sbox.json",
  "Start": 0,
  "End": 1,
  "SwapHands": false
}
```

`Template` is the library's canonical source skeleton, independent of the eventual
target character. `End: null` processes the remaining range, within the same 1,800-frame
limit. The editor supplies these paths automatically. Observation JSON remains readable,
but decoder/model changes create a new cache key and require fresh inference. Earlier
jobs and raw motion remain on disk. A per-job file lock prevents simultaneous writers. Completed observations
are saved every ten frames and on cancellation; abrupt process termination can require
recomputing up to nine frames. `worker-job.json` records process ID, cumulative processing
costs, current-session times and peak worker RAM. Old editor receipts are preserved.
MediaPipe does not invoke the native hand/body networks. No model is loaded during
a completed-cache replay; source skeleton fitting still runs from the stored observations.

Preview and inference share the same decoder: it removes Media Foundation's display
padding and applies quarter-turn orientation metadata before reconstruction. Camera
intrinsics and manual crop coordinates use these displayed dimensions. Decoder version
`wmf-visible-oriented-v2` is included in every backend's cache key and motion provenance.
Standard MP4 track rotations are supported; fractional aperture offsets, perspective,
mirrored or scaled track transforms require conversion to a standard video first.
Pixel-aspect correction for anamorphic video is not implemented; use square pixels.

For MobileHand, WildHands or WiLoR:

```powershell
dotnet run --project InferenceWorker -- download-hand-models models wildhands
dotnet run --project InferenceWorker -- hand-capture hand-job.json
```

Use `mobilehand` or `wilor` instead of `wildhands` for those models. A hand job contains `Video`, `Models`, `Output`, `Start`, `End`, `Backend` and a `Camera` object with `Fx`, `Fy`, `Cx`, `Cy` and `Calibrated`. Focal lengths and principal point are in pixels. Editor jobs set `Calibrated` to false and, unless a Recording FOV was entered, add `"EstimateFocal": true`. The worker then replaces the focal length with one sized from the clip's hands (median hand 0.45 m from the camera, farthest within 0.65 m) when it finds at least eight hand samples; `Camera` remains the fallback. WildHands needs the lens before inference, so it first runs untracked MediaPipe detection on up to 48 evenly spaced frames; WiLoR and MobileHand use their own predicted hand scale afterwards. The chosen camera is stored with the job so a resumed WildHands job keeps its completed frames' camera encodings. This is a first-person prior, not lens calibration. Omit `EstimateFocal` and supply measured parameters through the CLI when available.

MobileHand downloads 15,152,098 bytes, WildHands 855,094,722 bytes and WiLoR 2,564,989,533 bytes, plus the shared 7,819,105-byte MediaPipe crop detector. No separate MANO file is needed by these ports: the pinned checkpoints contain the model buffers used by the C# decoder. These files remain local and are not committed. The crop detector runs fresh landmark inference; missing observations remain marked as missing. The ports use MediaPipe crops rather than the original demos' detectors, so upstream accuracy results do not establish this pipeline's accuracy.

MobileHand's pinned FreiHAND checkpoint has SHA-256
`8587d8aae909c77fa07f382f6648eae4e366b3b4755aa993ca7711bfb35904cf`.
Its 277 tensors are read without executable pickle loading. On the same Ryzen 7,
the complete `video_0`, `segment_018` and `segment_037` samples took 16.74, 15.23
and 16.41 seconds of crop detection plus hand inference, respectively. Peak whole-worker
RAM was 1.04, 1.41 and 1.43 GB; initialization, video decoding, retargeting and downloads
are excluded from those timings. No GPU was used. All 361 frames were retained.
Large pose/depth jumps remain; these are throughput measurements, not evidence of
accurate capture or minimum hardware requirements. The model's original weak camera
uses millimetres projected into 224-pixel crops; depth is estimated from its scale.

On the same Ryzen 7 system, 15 frames of `segment_037.mp4` took 10.8 seconds in WildHands inference with 1.38 GB peak worker RAM, and 31.2 seconds in WiLoR inference with 4.50 GB peak worker RAM. Inference timings exclude initial download/checkpoint loading; RAM includes the complete worker process. Native GPU inference has not been tested. Both produced moving armature-only FBX animations that compiled and played in s&box. These are small functional examples, not ground-truth accuracy or minimum-hardware measurements.

After correcting the shared palm detector's resize operation, both models were rerun
on the first 30 frames of `video_0.mp4`, with both hands observed throughout that range.
WildHands recorded 12.47 seconds of model inference and 1.49 GB peak worker RAM;
WiLoR recorded 87.23 seconds and 3.84 GB. These timings include crop detection and
hand inference, and exclude initialization, decoding and retargeting. Both fresh jobs passed native target preview,
arm-length checks, armature-only FBX export and compiled animation playback. The
detector implementation version is part of the job cache key and motion provenance.

With the subsequent CPU crop/video-tracking update (`managed-hands-v7-video-tracking`),
the same 30-frame interval recorded 9.70 seconds / 1.47 GB for WildHands and
82.85 seconds / 5.35 GB for WiLoR. One frame lacked one hand. Both passed native
preview/export/playback checks again. These are individual runs, not controlled
cross-model speed or quality benchmarks.

The current native hand cache version, `native-mano-v4-image-observations`, retains all
21 detector image landmarks for each observed hand alongside its native prediction.
Old cache directories remain intact. Target/correction changes and completed-cache
replays do not load the neural models. Palm reprojection disagreement is reported as
a derived diagnostic, separately from detector presence and any confidence fields.

MediaPipe fitting writes `raw-hands-v7-camera.hmotion` (or `raw-hands-v7-camera-swapped.hmotion`).
It labels the canonical metacarpals as authored rest anatomy when their hand is observed,
so they no longer block contact search through otherwise observed fingers. Existing v5/v6
motion files remain intact. **Advanced → Process again** rebuilds this metadata using
cached observations without loading the neural models; **Apply adjustments** retains
the currently selected source file.

Hand motion now stores the pixel dimensions associated with its camera intrinsics.
Native MANO fitting writes `raw-hands-v5-camera.hmotion`, preserving the previous
`raw-hands.hmotion`. This metadata update reuses cached predictions and does not change
bone motion. MediaPipe's camera describes its authored wrist plane and is explicitly
uncalibrated. It does not estimate lens calibration or recover wrist depth.

Fresh runs with the corrected video decoder recorded the following on the Ryzen 7
7800X3D, 32 GB RAM, using four CPU inference threads and no GPU:

| Backend / clip | Frames | Detection + inference | Peak worker RAM |
| --- | --- | --- | --- |
| MobileHand / video_0 | 121 | 13.49 s | 1.43 GB |
| MobileHand / segment_018 | 120 | 11.99 s | 1.42 GB |
| MobileHand / segment_037 | 120 | 13.56 s | 1.44 GB |
| WildHands / segment_037 excerpt | 15 | 4.61 s | 1.44 GB |
| WiLoR / segment_037 excerpt | 15 | 24.30 s | 5.98 GB |

GB uses decimal bytes. Timings exclude setup, video decoding, retargeting and export.
These runs used an estimated pinhole camera on the 1920×1080 footage; no lens calibration
or GPU-memory measurement was performed. Median native-palm disagreement with detector
image landmarks was 77.3, 137.8 and 153.0 pixels for the three MobileHand clips, 277.1
pixels for WildHands and 35.2 pixels for WiLoR. These are disagreement measurements
between estimators, not a ground-truth accuracy ranking or minimum hardware requirements.

The FPS-default verification ran WildHands on all 121 frames of `video_0` on the Ryzen 7
7800X3D CPU: 23.39 seconds of detection/inference and 1.62 GB peak worker RAM. It produced
157 hand observations across 89 frames. Median/p95 palm projection disagreement with
MediaPipe was 170.3/263.5 pixels. Human preview, cleanup, 121-frame armature-only FBX
export and compiled playback passed; visual pose and placement mismatch remained.
This test used estimated 1920×1080 pinhole intrinsics, not measured lens calibration.
Changing the default does not establish a quality improvement or recover missed hands.

WiLoR processed the same 121-frame clip and the same 157 detected hands in 224.94 seconds
of detection/inference, with 6.01 GB peak worker RAM on that CPU. Median/p95 palm projection
disagreement was 27.9/55.8 pixels. Its closer image agreement does not establish correct
3D pose, depth, or accuracy during the shared detection gap. Its Human preview, cleanup,
121-frame armature-only FBX export and compiled playback also passed. Synchronized
visual review still showed placement/pose mismatch and held hands during tracking loss.
No GPU inference was used.
