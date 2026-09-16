# Hand backend options

MediaPipe remains the lightweight option. ACE-Ego-Hand is optional and must not become
a prerequisite for video import, hand reconstruction, preview or animation playback.
Open **Advanced → Hand models…** to select MediaPipe, MobileHand, WildHands or WiLoR for the next
FPS upload. Each selection prepares its model automatically on first use. Existing
reconstruction is preserved. ACE remains an informational optional entry and is never
selected, downloaded or loaded automatically. The badges are **Light** (MediaPipe), **Light*** (MobileHand), **Medium*** (WildHands), **Heavy*** (WiLoR)
and **Very heavy*** (ACE). An asterisk means the processing weight is an architectural
estimate, not a local memory measurement or accuracy rating.

| Backend | Status in Humanoid Mocap | Intended use and limits |
| --- | --- | --- |
| MediaPipe | Available, experimental C# implementation; 7.8 MB model | Lightweight hand landmarks and finger motion. Wrist depth and arm placement are estimated; this is not calibrated world tracking. |
| MobileHand | Available, experimental C# native CPU port; 15.2 MB checkpoint | Small MobileNetV3 hand-angle model. Real sample tests show substantial pose/depth jumps; not an accuracy replacement for ACE. |
| WildHands | Available, experimental C# native CPU port; 855 MB checkpoint | Egocentric wrist/finger rotations and shape. Uses MediaPipe hand crops and estimated camera intrinsics; review wrist depth and visibility. |
| WiLoR | Available, experimental C# native CPU port; 2.56 GB checkpoint | Wrist/finger rotations and shape with a larger transformer. Uses MediaPipe hand crops rather than the original detector. |
| ACE-Ego-Hand | Optional future backend; not integrated | Offline bimanual reconstruction through occlusion using the much larger Wan video backbone. Downloads and model loading must be opt-in. |

MobileHand follows the [released FreiHAND implementation](https://github.com/gmntu/mobilehand/tree/51c112364013b803c38955b55a1572b0d402894c).
Its original 39 parameters, including 23 joint angles, are preserved in the reconstruction
cache. C# reads its legacy checkpoint as data only; no Python or pickle callable executes.
The video integration uses a square MediaPipe crop with 1.5× padding and reflected left
hands. This is our crop policy, not verified parity with an upstream video tracker.
The three downloaded hand clips completed real inference, but local rotation steps reached
approximately 180 degrees and estimated wrist speeds reached 68 m/s in the latest full-clip runs. Visual review also showed
misaligned fingers. Conservative cleanup did not remove these failures. MediaPipe stays
the default, and no accuracy equivalence with larger models is claimed.

The ACE K-free checkpoint was verified against its published SHA-256 and all 844
projector, ray-head and backbone-delta tensors were read through the C# checkpoint
reader. This is checkpoint compatibility testing, **not ACE inference**. The C# Wan
backbone, video VAE and caption-conditioning path are still missing, so ACE remains
unavailable for reconstruction. The upstream inference entry point is Python; this
library does not launch it as a bridge.

WildHands provides the intermediate processing option between MediaPipe and larger models.
Its authors report a model about ten times smaller than HaMeR and better results on
three of six compared metrics. That comparison is **against HaMeR, not ACE-Ego-Hand**.
The released demo was inspected at commit
`f99dfea0d1fce970aed2d31d1018eda280e05f47`: two ResNet-50 encoders, camera-conditioned
features, separate iterative hand heads, and MANO decoding. Its official
`fetch_models.sh` links the trained WildHands checkpoint; it is not bundled here.
See the [paper and project](https://ap229997.github.io/projects/hands/) and
[pinned demo source](https://github.com/ap229997/hands/tree/f99dfea0d1fce970aed2d31d1018eda280e05f47).

Fast-HaMeR is another research candidate. The authors report roughly 35% of HaMeR's
model size, 1.5 times faster inference and a 0.4 mm accuracy difference on HO3D-v2.
Those numbers do not establish comparable egocentric video or occlusion performance.
The inspected download script fetches the original HaMeR demo assets; a reproducible
download for the distilled student checkpoint still needs to be verified.
See [Fast-HaMeR](https://github.com/hunainahmedj/Fast-HaMeR).

No lightweight replacement has been verified here to match ACE's accuracy. The
[ACE paper](https://arxiv.org/html/2608.20308v1) reports substantial differences during
occlusion and out-of-view intervals. Its HaWoR comparison disables SLAM and motion
infilling, so that row must not be interpreted as a test of the complete HaWoR pipeline.
See also the official [ACE repository](https://github.com/ggxxii/ACE-Ego-Hand) and
[WiLoR repository](https://github.com/rolpotamias/WiLoR).

These integrations use C# and native inference libraries without a Python bridge.
All available models reconstruct in a separate local C# worker. MediaPipe uses its
managed interpreter there. Decoder/model changes invalidate reconstruction caches;
previous raw observations and motion files are preserved.
Native hand caches now also retain the detector's per-frame image landmarks. The worker
compares the reconstructed palm's projection with those landmarks and reports median
and p95 disagreement in pixels. A median exceeding 20% of the detected palm span adds
a wrist-placement review warning. This heuristic compares two estimates; it is neither
3D confidence nor proof that either estimate is correct. It does not modify the motion.
MediaPipe, WildHands and WiLoR have produced real reconstructed motion, target-rig
previews and moving FBX animations imported by s&box. The native hand models share the
7.8 MB MediaPipe crop detector. Missing detections stay explicit; the models do not
recover prop tracks or calibrated camera motion. See the [worker instructions](InferenceWorker/README.md)
for pinned setup, cache/resume behavior and measured processing costs. Functional
verification does not establish parity with the original demos or ground-truth 3D accuracy.

The managed MediaPipe kernels were compared against native TensorFlow Lite 2.17.1
using identical tensors from the sample footage. This caught and corrected a bilinear
resize option error in the palm detector. Reconstruction caches now include the detector
implementation version, including the crops used by WildHands and WiLoR. Kernel agreement
does not establish parity for the complete MediaPipe tracking pipeline or fix every missed hand.

The CPU image crop now follows MediaPipe's integer-pixel sampling and border rules;
hand regions use repeated edge pixels, while palm detection uses black padding.
Weighted palm-box/keypoint merging and prior-landmark crop priority follow the upstream
video graph. Nine crops from the downloaded footage matched the OpenCV reference
exactly for full-image detector inputs and within two color-byte values for rotated
crops. These focused checks do not establish complete graph parity. Full-clip detection
coverage remains mixed, including fewer accepted hands in two samples; see [capture results](CAPTURE.md).

All hand backends now keep a hand-and-wrist source skeleton. MediaPipe fits landmark
rotations to fixed canonical finger lengths and assumes wrist depth on an image plane.
Estimated shoulders and elbows are added on the selected target through IK. Captured-skeleton
export contains the hand source; ordinary export contains the preview rig and baked movement.
Source observations are retained separately, so target and correction changes do not rerun inference.

MediaPipe finger fitting now carries the parent segment's orientation and applies the
minimum swing needed to match each observed direction. This avoids the old palm-axis
singularity when a finger points across the palm. Axial finger twist is estimated,
not measured by the landmarks. Replaying all three saved samples preserved 5,370
segment directions within 0.00027 degrees, along with wrist transforms, timestamps
and observation labels. Rotation steps above 90 degrees changed from 106 to 55 in
`video_0`, from 30 to 28 in `segment_018`, and remained zero in `segment_037`.
Some individual joints worsened and large prediction jumps remain. These are fitting
checks, not 3D accuracy measurements. Reprocessing reuses cached neural observations
and writes `raw-hands-v6.hmotion`, preserving previous fitted motion files.

Canonical metacarpals use fixed template anatomy, labeled `Authored` while their hand
is observed. They are neither measured joints nor inferred motion. Missing hands retain
`Unobserved` labels. This lets contact search evaluate observed fingers through the
template hierarchy without treating template bones as observations. For older captures,
use **Advanced → Process again**; cached neural observations are reused. Applying
adjustments alone does not replace the old source labels. Across all 361 frames of the
three samples, this label correction left every position, rotation and timestamp exact.
