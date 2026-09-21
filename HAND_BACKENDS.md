# Hand backend options

WiLoR is the FPS default, with MediaPipe locating hands and supplying image crops.
WiLoR supplies wrist and finger pose; MediaPipe rotations are not blended into it.
WildHands uses the same detection/pose split when selected. MediaPipe-only reconstruction
remains an optional lightweight mode. Existing captures are preserved; select the model
and use **Advanced → Process again** to reconstruct the video with it.
ACE-Ego-Hand is optional and must not become
a prerequisite for video import, hand reconstruction, preview or animation playback.
Open **Advanced → Hand models…** to select MediaPipe, MobileHand, WildHands or WiLoR for the next
FPS upload. Each selection prepares its model automatically on first use. Existing
reconstruction is preserved. ACE remains an informational optional entry and is never
selected, downloaded or loaded automatically. The badges are **Light** (MediaPipe), **Light*** (MobileHand), **Medium*** (WildHands), **Heavy*** (WiLoR)
and **Very heavy*** (ACE). An asterisk means the processing weight is an architectural
estimate, not a local memory measurement or accuracy rating.

## Finger articulation check and the default model

A finger-focused clip was added to the local samples: the Dutch Sign Language manual
alphabet ([NGT handalfabet](https://commons.wikimedia.org/wiki/File:NGT_handalfabet.webm),
Vera de Kok, CC BY-SA 4.0), 26 labelled and clearly different poses of one right hand,
filmed from outside at 1280×720. `dev/samples/fetch_finger_sample.py` downloads it
against a pinned SHA-256 and prepares an H.264 copy. It has no 3D reference; results
were judged by projecting each reconstruction over the video and against MediaPipe's
image landmarks, which sat on the fingers in every inspected frame.

WildHands failed on it. Over 1,099 observed hands its projected palm direction differed
from the detected one by 37° at the median and 107° at p90, against 5–13° on the three
head-mounted clips and the HOT3D view; an open palm came out half-curled and rotated by
about a quarter turn. Because the C# network matches the upstream one, this is the
model outside its egocentric training domain rather than a port error. WiLoR on the
same frames matched the open palm, C, pointing D and fist E, with 12 px median palm
disagreement against WildHands' 74 px. On HOT3D WiLoR was also closer (wrist-relative
30.8 against 43.5–50.2 mm; palm direction 1.2° against 6.2°). Through the editor the
default path reproduced the signed G on Human, index extended with the other fingers
curled, and its 240-frame FBX compiled and played in s&box.

WiLoR is therefore the default, and WildHands is offered as the faster choice for
head-mounted footage only. The worker previously ran four inference threads whatever
the processor; it now uses one per two logical processors, between 2 and 12, or
`HUMANOID_MOCAP_THREADS`. On the Ryzen 7 7800X3D WiLoR took 1.54 s per hand per frame
with four threads and 0.64 s with eight. A four-second two-handed clip is about three
minutes. When a reconstructed palm, centred on the detected hand, still misses the
detected wrist and knuckles by more than 20% of palm span at the median, the capture
is marked **Review hand pose** and the status line suggests WiLoR. That fired for
WildHands on this clip and for none of the other five jobs checked. It compares two
estimates; it cannot certify a pose as correct. A recording in which one hand never
appears beside a well-tracked hand is reported as one-handed rather than as a fault.

The earlier default change separated hand detection from pose reconstruction; it was not
a claim that the WildHands port faithfully reproduces every FPS performance.
On the complete 121-frame `video_0` sample, it produced 157 observed hand instances
across 89 frames and still missed both hands during the shared occlusion. Native
editor preview, cleanup, armature export and compiled playback passed. Synchronized
visual review still showed pose/placement disagreement. MediaPipe supplies crop and
side estimates only; it does not overwrite WildHands finger rotations or fill missing poses.
WiLoR also completed the same clip and passed native preview/export/playback. Its closer
image-landmark agreement still did not resolve the visible placement/pose mismatch or
shared tracking loss. Both native backends remain experimental.

The complete 121-frame WildHands `video_0` capture was also checked through retargeting
and FBX round-trip export on Human, Citizen, the engine's FPS arms FBX and a detached
hands-only FBX fixture. Across 5,966 observed proximal/middle finger-segment comparisons,
the largest direction change from reconstructed motion was below 0.001°. Local rotation
channels survived FBX export within floating-point tolerance. This checks transfer of
the predicted motion, not its agreement with the person in the video, terminal fingertip
placement, or mesh contact. Citizen has four finger chains and cannot receive pinky
motion. Missing target hand/finger mappings now produce a diagnostic; the raw capture
retains all tracks.

The native Citizen gate verified the visible unmapped-joint notice and its pinky
details, then compiled and played the complete 121-frame armature-only FBX. Export
round-trip position error was below 0.0001 cm. The compatible hands-only fixture
produced no omission warning. Visual review still showed reconstructed hand-pose
disagreement; these transfer checks do not resolve it.

FPS camera reset now respects the configured capture-camera facing. Previously it
aimed toward the clip-average wrist position, tilting the camera down into the full
character's torso and upper arms. Native Human and Citizen review at frames 30 and 90
of `video_0` showed the resulting cutouts disappear with the forward capture view.
Changing the near plane alone did not fix them, so its default remains unchanged.
Camera switching/reset preserved every solved bone transform, and both complete
121-frame exports compiled and played. This is a preview-camera correction; it does
not improve the reconstructed poses or establish freedom from mesh intersections.

The C# WildHands network now has independent reference parity. The pinned upstream
PyTorch modules were run on the exact tensors the C# worker prepared for a real
`video_0` frame. All 144 rotation-matrix entries, 10 shape values and 3 camera values
for both hands agreed within 0.0000008. This covers the two ResNet-50 encoders, camera
encodings, feature fusion and both iterative heads; image preparation was checked
separately above, and MANO decoding is shared with WiLoR. It shows the port reproduces
the published network, not that the network's predictions are correct.

That isolated the visible placement error to the model's translation output rather
than the port. On the calibrated HOT3D view, WildHands depth was a consistent 0.58–0.71
of the reference (median 0.63) while finger poses were usable, and its predicted
position reprojected 39 px from the detected hand in a 640 px image. WildHands wrists
are therefore placed differently from its raw translation:

- Depth is multiplied by one clip-wide factor so the median hand sits 0.45 m from the
  camera and the farthest within 0.65 m.
- Each wrist then moves sideways, at that depth, until the reconstructed palm's
  centroid projects onto the detected palm's centroid.

Rotations, finger poses and shape are untouched, and frames without a detection stay
unobserved. A fresh worker run on the HOT3D view with no calibration supplied gave
58.2 mm mean camera-wrist disagreement over 237 hands (p95 90.3 mm). The previous
placement gave 153.7 mm (p95 198.7 mm) even with the published calibration. Replaying
those calibrated predictions with the new placement gave 41.0 mm. On the three supplied clips,
median palm reprojection disagreement fell from 170 / 113 / 107 px to 23 / 31 / 20 px
at 1920 px width. WiLoR's translation already follows its image crop, so the sideways
move matters less: 48.0 to 40.8 mm mean on the cached calibrated predictions, with p95
94.3 to 96.6 mm. It is applied to every native backend so reconstructed hands sit on the
hands in the video; only WildHands receives the depth factor.

Several ways to push this below 25 mm were measured on the same clip and rejected.
A perspective fit of all 16 joints to the detected landmarks made depth worse (WiLoR
82.4 mm), because finger-pose error biases the fitted scale. Median filtering depth
over 5–15 frames changed the mean by under 1 mm; the depth error drifts slowly with
pose rather than flickering. Averaging with MediaPipe's hand-scale depth raised
WildHands from 34 to 42 mm, and averaging WildHands with WiLoR depth did not beat
WildHands alone. Compensating depth for each frame's predicted hand size changed
nothing, since predicted size varies by only 0.3–1.4%, and separate left/right depth
factors gave the same result as one. With perfect depth the placement would differ from the reference
by 16–22 mm, of which 12–18 mm is a constant image-vertical offset between the
reference wrist and the MediaPipe/MANO wrist. The remaining error is single-image
depth, about 5% of hand distance, and the prior itself: this clip's true median hand
distance was 0.44 m (0.42 m left, 0.46 m right) against the assumed 0.45 m. A 25 mm
absolute wrist target therefore needs a metric reference the video does not contain,
such as a known lens together with a measured hand, or a depth camera. One annotated clip is not dataset-wide accuracy, and
the distance prior is an assumption about first-person footage, not a measurement.

With **Recording FOV** blank, every native backend now sizes the lens from the hands
instead of assuming a focal length equal to the image diagonal. That older assumption
(a 47° lens for 1920×1080) put `video_0`'s wrists 1.2–1.9 m from the camera, beyond
either built-in rig's reach, so arm IK flattened all of them. WildHands needs the
lens before inference because camera rays enter its network, so up to 48 evenly spaced
frames get an untracked MediaPipe pass first; WiLoR and MobileHand instead use their
own hand scale after inference, which costs nothing extra. On the HOT3D view with a
known 184.75 px focal length the estimates were 243 px (MediaPipe pass) and 193 px
(WiLoR scale); the diagonal rule gives 905 px. The supplied clips gave 118°, 67° and
71° horizontal. The pre-pass added about 8 s to a 121-frame WildHands job.

| Backend | Status in Humanoid Mocap | Intended use and limits |
| --- | --- | --- |
| MediaPipe | Available, experimental C# implementation; 7.8 MB model | Lightweight hand landmarks and finger motion. Wrist depth and arm placement are estimated; this is not calibrated world tracking. |
| MobileHand | Available, experimental C# native CPU port; 15.2 MB checkpoint | Small MobileNetV3 hand-angle model. Real sample tests show substantial pose/depth jumps; not an accuracy replacement for ACE. |
| WildHands | Available, experimental C# native CPU port; 855 MB checkpoint | Faster option for head-mounted footage. Wrong finger poses and orientation on hands filmed from outside. Uses MediaPipe hand crops and an estimated lens. |
| WiLoR | FPS default, experimental C# native CPU port; 2.56 GB checkpoint | Wrist/finger rotations and shape with a larger transformer; closest to the video in every comparison here. Slowest. Uses MediaPipe hand crops rather than the original detector. |
| ACE-Ego-Hand | Optional future backend; not integrated | Offline bimanual reconstruction through occlusion using the much larger Wan video backbone. Downloads and model loading must be opt-in. |

MobileHand follows the [released FreiHAND implementation](https://github.com/gmntu/mobilehand/tree/51c112364013b803c38955b55a1572b0d402894c).
Its original 39 parameters, including 23 joint angles, are preserved in the reconstruction
cache. C# reads its legacy checkpoint as data only; no Python or pickle callable executes.
The video integration uses a square MediaPipe crop with 1.5× padding and reflected left
hands. This is our crop policy, not verified parity with an upstream video tracker.
The three downloaded hand clips completed real inference, but local rotation steps reached
approximately 180 degrees and estimated wrist speeds reached 68 m/s in the latest full-clip runs. Visual review also showed
misaligned fingers. Conservative cleanup did not remove these failures. MobileHand is
not the default, and no accuracy equivalence with larger models is claimed.

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

WildHands now follows the demo's initial 840-pixel square preparation before the
224-pixel dataset crop and blur. Previously this first resize was omitted, changing
the effective image filter. Boxes and intrinsics follow both transforms; no frames
are dropped. Twelve crop checks on the three supplied videos matched the isolated
preparation reference, including off-center intrinsics and missing-hand inputs.
This is preprocessing verification, not independent parity for the complete network.

On the calibrated HOT3D `001849` view with the same 236 detected hands, this change
reduced mean wrist-relative landmark disagreement from 50.2 to 43.5 mm and p95 from
89.8 to 78.6 mm. Mean camera-wrist disagreement changed from 158.6 to 153.7 mm.
Temporal results were mixed: mean wrist-relative velocity disagreement decreased
slightly, while p95 increased from 472 to 486 mm/s. UmeTrack/MANO landmark definitions
differ; these are one-clip reference comparisons, not dataset-wide accuracy claims.

The complete `video_0` editor run retained 121 frames and 157 observed hand instances,
then passed target preview, armature export and compiled FBX playback. With the same
600 px **assumed** focal length, median palm reprojection disagreement stayed at
162.8 px and p95 increased from 210 to 221 px. Visible pose errors and tracking loss
remain. CPU detection/inference took 28.27 s with 1.70 GB peak worker RAM on the Ryzen
7 7800X3D, four threads; setup, decode, retarget and export are excluded. The new
`wildhands-demo840-v1` cache version preserves previous results. Use **Process again**
to apply it; repeated jobs reuse predictions, and WiLoR/MobileHand cache keys are unchanged.

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

The tracker now retains left/right identity through weak classification disagreements
when both hands have fresh observations and remain close to their separate prior tracks.
Previously, two detections labelled as the same hand could discard a valid hand and send
the other hand's motion to its arm. Close crossings, large jumps, new palm detections,
low presence and strong classification disagreements keep the fresh classifier result.
This is a bounded tracking heuristic, not recovered occlusion or an extra motion filter.
Identity assignment edits no landmark coordinates; retained tracks can change subsequent
crop selection and its new neural predictions. Saved handedness is the model probability for the
assigned side; values below 0.5 expose disagreement rather than invent confidence.
The detector version changed, so processing again preserves older files and creates
a new observation cache. Native hand models share this detector behavior.

Two further physical-consistency rules follow that step. In `video_0` the right hand
left the image and the side classifier then called the remaining left hand "right" with
0.97 probability; a fresh palm detection next duplicated that hand under the other
label. The stolen region ended the real right-hand track, and the right wrist was held
at the left hand's position through the following one-second occlusion, crossing the
arms. Now a hand resting on one established track is not relabelled as the other hand
while that hand's track was at least a palm and a half away one frame earlier, whatever
the classifier reports, and two observations whose landmarks coincide collapse to one,
keeping the label of the track it continues. Reacquisition, close or crossing hands and
single-track history still use the classifier. On `video_0` both hands keep their sides
through frames 40–47, the right hand is followed five frames longer, and observed hand
instances rose from 157 to 161. Landmarks are never edited and no observation is created.
The detector version changed again, so processing again creates a new cache.

A local test used all 150 frames of HOT3D clip `001849`, with its per-frame Aria
Fisheye624 calibration and a fixed, authored virtual pinhole view. Comparison against
the [UmeTrack reference annotations](https://github.com/facebookresearch/hand_tracking_toolkit/tree/950d64f7e8d2ba1fd38cd2ceede6608a8fa7f5aa)
found 234 → 236 same-side detections out of 300 fully in-view reference hands, and
mean image-landmark disagreement changed from 6.20 → 4.74 pixels. The two erroneous
identity frames retained both hands. Mean wrist-relative 3D disagreement remained
about 84 mm, without rotation or scale alignment, and the prolonged detection loss
remained. Landmark definitions differ between the models. This is one annotated clip,
not dataset-wide accuracy or a calibrated wrist-depth result. That benchmark remains
local verification. The separate [HOT3D annotation importer](HOT3D.md) now lets the
worker prepare annotated Aria clips for editor preview/export; it is not an inference backend.

All hand backends now keep a hand-and-wrist source skeleton. MediaPipe fits landmark
rotations to fixed canonical finger lengths and assumes wrist depth on an image plane.
Estimated shoulders and elbows are added on the selected target through IK. Captured-skeleton
export contains the hand source; ordinary export contains the preview rig and baked movement.
Source observations are retained separately, so target and correction changes do not rerun inference.

An additional local comparison ran all four available backends on the same 150-frame
HOT3D Aria clip, `clip-001849`. Its published Fisheye624 calibration rectified the RGB
images to a fixed, authored 640×640, 120-degree virtual camera. The camera view was not
fitted to reference hands. All native models reused the same 236 detected hands out of
300 reference hands; missing detections were not filled or reference-guided. Results
were compared with UmeTrack annotations using 19 wrist-relative joints, translation
alignment only, and no rotation or scale fitting. Native models used clip-average
predicted shape as in production. Landmark definitions differ between representations.

| Backend | Wrist-relative disagreement, mean / p95 | Absolute camera-wrist disagreement, mean / p95 |
| --- | --- | --- |
| MediaPipe | 84.1 / 141.7 mm | Not evaluated: assumed wrist plane |
| MobileHand | 95.7 / 184.9 mm | 329.3 / 485.6 mm |
| WildHands | 50.2 / 89.8 mm | 158.6 / 200.0 mm |
| WiLoR | 30.8 / 51.1 mm | 48.0 / 93.9 mm |

These are one-clip disagreements with published annotations, not dataset-wide accuracy
or metrology. WiLoR gave the closest agreement here, with visibly imperfect fingers;
WildHands still had substantial wrist-placement errors. MobileHand's smaller model did
not provide comparable pose quality. No default model was changed on this evidence.

On the Ryzen 7 7800X3D, four CPU inference threads, native model inference plus crop
preparation took 2.37 s for MobileHand, 23.91 s for WildHands and 330.31 s for WiLoR.
Peak process RAM was 0.59, 1.16 and 4.71 GB respectively. These runs reused detections;
each additionally spent about 70–73 s rectifying source frames, and model loading took
0.15, 1.76 and 6.42 s. They are measured stages, not fresh end-to-end times or minimum
hardware requirements. Other editor/video work overlapped portions of the runs; GPU
inference was not tested. Checkpoint sizes remain those in the backend table above.

The full WildHands/Human and WiLoR/Citizen results passed native preview, armature-only
FBX compilation and animated playback. Synchronized images were visually inspected;
the wide-angle source also exposed clipping in the narrower default FPS preview.
New captures retain camera image dimensions, and the initial FPS view widens up to
120 degrees when the stored pinhole geometry needs it. Existing saved FOV choices are
respected. This changes the view only, not wrist placement or reconstructed motion;
unknown dimensions and unrectified lens distortion keep the ordinary default.
The derived review video uses nominal 30 fps, with a measured maximum 1.13 ms difference
from original timestamps; motion preserves the original sample times. This functional
verification does not make the reconstruction accurate or collision-free.

The [sample archive](https://huggingface.co/datasets/bop-benchmark/hot3d/resolve/30fe9674782f32e1e5edba98476b6ff4300132c5/train_aria/clip-001849.tar)
has SHA-256 `c3bfd5b26b1c80a4a8c038b1d26ef423464de0b12ee42c6adc67f0485dbd3775`.
The reference reader follows hand-tracking-toolkit commit
`950d64f7e8d2ba1fd38cd2ceede6608a8fa7f5aa`. Rectification, reference comparison, reports
and footage remain local verification assets, outside the distributed library.

MediaPipe finger fitting now carries the parent segment's orientation and applies the
minimum swing needed to match each observed direction. This avoids the old palm-axis
singularity when a finger points across the palm. Axial finger twist is estimated,
not measured by the landmarks. Replaying all three saved samples preserved 5,370
segment directions within 0.00027 degrees, along with wrist transforms, timestamps
and observation labels. Rotation steps above 90 degrees changed from 106 to 55 in
`video_0`, from 30 to 28 in `segment_018`, and remained zero in `segment_037`.
Some individual joints worsened and large prediction jumps remain. These are fitting
checks, not 3D accuracy measurements. Reprocessing reuses cached neural observations
and writes `raw-hands-v7-camera.hmotion`, preserving previous fitted motion files.

Canonical metacarpals use fixed template anatomy, labeled `Authored` while their hand
is observed. They are neither measured joints nor inferred motion. Missing hands retain
`Unobserved` labels. This lets contact search evaluate observed fingers through the
template hierarchy without treating template bones as observations. For older captures,
use **Advanced → Process again**; cached neural observations are reused. Applying
adjustments alone does not replace the old source labels. Across all 361 frames of the
three samples, this label correction left every position, rotation and timestamp exact.
