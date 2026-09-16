# Research notes

Last checked: 16 September 2026. These are candidates and experiments, not advertised
library capabilities. Research is revisited when a concrete tracking failure or backend
decision calls for it. Published results do not establish performance on our clips or hardware.

| Work | Relevance to Humanoid Mocap | Current decision |
| --- | --- | --- |
| [HTD-Refine, CVPR 2026](https://arxiv.org/abs/2605.26879) | Refines existing human reconstruction using predicted velocity and acceleration, aiming to preserve dynamics as well as positions. | Promising body refinement candidate. The [released implementation](https://github.com/ant-research/HTD-Refine/tree/2fcd6ddef3c4eb75a636062245f80a0136c09b7e) needs PVA-Net, initial body motion and consistent camera information. Its demo currently expects 30 FPS. A C# port, timestamp handling and measured benefit are still required. |
| [FootMR, 3DV 2026](https://arxiv.org/abs/2603.09681) | Predicts residual ankle rotations from foot observations and knee/ankle motion. | C# trial completed on the 312-frame tennis clip, including the retrained body network from the [pinned source](https://github.com/twehrbein/FootMR/tree/9c5b4123b344d74926822c20f182b2af4494dc41). Results below do not justify changing the default backend. |
| [MoPO, 2026](https://arxiv.org/abs/2605.09856) | Combines temporal occlusion detection with learned motion completion and pose refinement. | Relevant to incorrect observations during occlusion. Its learned predictor and fusion cannot be replaced by ordinary interpolation and called equivalent. A usable checkpoint and reproducible C# inference path have not been verified. |
| [EgoHandICL, ICLR 2026](https://arxiv.org/abs/2601.19850) | Uses retrieved context for egocentric hand reconstruction. | Research candidate. The [released instructions](https://github.com/Nicous20/EgoHandICL/tree/16e5690224fa718c1ddc40fc2972f6551b584f8b) expect prepared dataset assets and baseline MANO predictions. It is not yet an arbitrary-video replacement for our hand backends. |
| [A2P, CVPR 2026](https://arxiv.org/abs/2503.17788v3) | Combines image-prior alignment with collision-guided diffusion for interacting hands under occlusion. | Relevant to the observed hand intersections. The [official project page](https://gaogehan.github.io/A2P/) currently links the paper and supplementary material, but no inference repository or checkpoints. No C# inference path, video-temporal behavior or performance on our clips has been verified. It is not integrated. |
| [Contact-Aware Retargeting of Skinned Motion, ICCV 2021](https://arxiv.org/abs/2109.07431v1) | Uses skeleton and character geometry to preserve self-contact and reduce interpenetration. | Supports evaluating actual target surfaces alongside skeleton constraints. Our arm-capsule experiments are not an implementation of its geometry-conditioned network or optimization, and do not establish skin clearance. |

Candidate changes must be checked against actual reconstruction, target-rig playback and
export. Checks cover tracking loss, fast motion, contact transitions, bone lengths, timing,
resource use and repeatable setup. Lower jitter alone does not justify adoption. Camera
assumptions, inferred motion and backend observations remain distinct. Production inference
stays in C#, with native libraries where needed.

The latest intersection experiment searched paired camera-depth wrist offsets only
during a shared tracking gap. Native Human and Citizen playback confirmed that reducing
an arm-capsule overlap score can still leave visible intersections and alter apparent
hand size substantially. It is not enabled. The measured results and limits are recorded
in [drift reduction](DRIFT_REDUCTION.md). Further collision work needs target hand and
skin geometry, contact preservation and temporal checks, rather than another arm-only
overlap score. Neither a plausible collision-free pose nor a learned prior proves that
unobserved motion was recovered correctly.

A recent body experiment retried six tennis frames at three crop scales. The two incorrectly
placed left wrists remained wrong across the alternative scales. Their averaged heatmaps
also lacked a second local maximum above 0.1. This does not justify automatic crop retries
or claiming that peak selection recovers the missing hand evidence. No such correction is
enabled in the library; the original observations are retained.

The FootMR experiment used actual downloaded weights and whole-body observations,
followed by C# temporal inference, ankle refinement, stationary-camera processing and
target correction. Its normalization statistics matched the pinned GVHMR source.
An independent C# native-tensor implementation agreed with the managed ankle network
within 0.000001 across five numerical controls, including attention-window boundaries.
That verifies those equations; it is not upstream end-to-end output parity or 3D accuracy.

Whole-body observations took 770.2 seconds on the Ryzen 7 7800X3D with four CPU threads;
that process peaked at 5.74 GB RAM. Cached image features were reused. The body network
took 1.82 seconds including loading; skeleton loading and ankle refinement took another
1.41 seconds, and contact processing 0.12 seconds. These are measured stages, not a fresh
end-to-end processing time. GPU inference was not tested. The whole-body checkpoint is
2,549,087,447 bytes and the FootMR checkpoint 178,872,284 bytes. The whole-body detector
could replace the existing 17-joint image pose model in a future integration; the two
need not run together. The full capture path is still computationally heavy.

Both backends were evaluated on the **same 372 steps** selected by the original GVHMR
static-joint predictions. After each backend's source processing and the library's target
correction, foot-joint displacement was:

| Target | Current GVHMR mean / maximum | FootMR trial mean / maximum |
| --- | --- | --- |
| Human | 0.0181 / 0.8321 cm | 0.0730 / 4.8191 cm |
| Citizen | 0.0139 / 0.5125 cm | 0.0654 / 4.0218 cm |

These are model-selected contact intervals under an assumed stationary camera, not
annotated contacts or measured ground-truth slip. Selecting each backend's own predicted
contacts gave FootMR more favorable numbers, but also changed which steps were measured;
that is insufficient evidence of improvement. The serve's largest right-forearm rotation
step moved from export frame 134 to 136, approximately 67 ms. Without reference motion,
this does not establish which timing is more accurate. Limb lengths and timestamps were
preserved. The new image detector corrected one of the two wrong wrist frames; the other
still put the ball hand near the racket hand.

The trial passed native Human preview, armature-only FBX export, compilation and playback
for all 312 frames. Selected synchronized video/animation frames were visually reviewed;
Human and Citizen retargeting were measured. An ankle-only trial on the original body
predictions also failed to establish a foot-displacement improvement. FootMR remains a
local experiment, with no new default filter or model download added to the library.
Further adoption needs more representative foot-contact footage and reference evidence.

See [drift reduction](DRIFT_REDUCTION.md) for implemented techniques and measured outcomes,
and [hand backends](HAND_BACKENDS.md) for available models and their current limitations.

## Detector-guided wrist placement experiment

A fixed-depth alternative to the earlier unconstrained translation fit was evaluated
on the same 236 HOT3D hand observations for WildHands (840px preparation) and WiLoR.
It adjusted camera X/Y against the detector's wrist and four knuckle pixels using a
robust reprojection fit. Native depth, wrist rotation, finger rotations and shape were
unchanged. Reference annotations were used only to evaluate the results.

WildHands mean wrist disagreement increased from 153.7 to 160.3 mm and p95 from 198.5
to 207.0 mm. Its mean wrist-velocity disagreement decreased from 0.225 to 0.205 m/s,
but that did not compensate for the worse placement. WiLoR mean wrist disagreement
decreased from 48.0 to 40.5 mm, while p95 increased from 93.9 to 95.7 mm and mean
wrist-velocity disagreement increased from 0.140 to 0.149 m/s. Velocity comparisons
use the same 231 consecutive observed pairs and supplied timestamps; lower speed by
itself is not an accuracy measure. UmeTrack/MANO landmark definitions differ.

This correction was not adopted. It illustrates why closer 2D agreement or lower
motion noise alone is insufficient evidence of better 3D capture. It remains an
isolated C# experiment; existing reconstruction and target motion are unchanged.

## Recording-camera estimation candidate

The recording-FOV sensitivity results make automatic lens estimation worth evaluating.
[GeoCalib (ECCV 2024)](https://arxiv.org/abs/2409.06704) estimates intrinsics and gravity
from image cues, with geometric optimization. The inspected
[official implementation](https://github.com/cvg/GeoCalib/tree/97b8968e7798a66bf04fcf791fb535624241bda7)
uses an MSCAN backbone, up/latitude decoders and a Levenberg–Marquardt optimizer; its
inference wrapper resizes images and can optimize shared intrinsics across a batch.
This could help estimate a constant recording lens from several frames, separately
from the target rig and preview camera. It is a candidate, not an integrated feature.

The official v1.0 pinhole checkpoint is 116,074,121 bytes, SHA-256
`86d6aeacd8bbd974c59ce39f61854e00d36911c732ad89be471476fd708722ac`.
The existing C# checkpoint reader identified 889 tensors under its `model` component.
A local C# / native TorchSharp evaluation now runs the MSCAN backbone, both field
decoders and seven-step NMF inference. All inference weights are consumed; batch-count
buffers are excluded. The experimental pinhole solver uses the upstream confidence-
weighted Huber objective for up vectors and sine latitude, with roll/pitch/log-focal
updates instead of the upstream spherical-manifold optimizer. Independent tensor-by-
tensor equivalence has not been established. No Python process or bridge was used.

The solver recovered 27 synthetic camera configurations, covering 35°, 75° and 120°
horizontal fields of view with different roll/pitch angles. On the upstream church
example, the C# estimate was 553.1 px versus the notebook's recorded 551.4 px; this is a
useful comparison with an upstream result, not proof of complete numerical parity.

Six HOT3D reference images were rectified using their supplied FISHEYE624 calibration
into known 60° and 80° pinhole views. All output pixels sampled inside the original
images. Estimated focal lengths were 5.3–35.4% above the known virtual-camera values;
field-of-view errors ranged from 2.9° to 13.8°. The earlier 120° reference views included
large black regions and produced larger errors, so they are reported separately.
These are three moments from one recording, not a dataset-wide calibration benchmark.

Three frames each from `video_0`, `segment_018`, `segment_037` and `tennis` were also
processed. The tennis estimates stayed between 31.4° and 33.1°, while `video_0` varied
from 55.2° to 141.1° and `segment_037` from 48.9° to 107.8°. Those clips have no supplied
calibration here; stability alone does not establish accuracy. Treating these
framewise estimates as changing intrinsics could itself cause wrist-depth drift.

On the Ryzen 7 7800X3D with four CPU inference threads, preprocessing plus network
inference took 0.88–1.56 seconds per sampled video frame; fitting took 0.14–0.68 seconds.
Peak process working set reached 2,073,812,992 bytes over the 15-frame video evaluation.
This was CPU-only inference; no VRAM measurement or minimum hardware claim is made.
Measurements exclude decode, checkpoint loading, JSON reports and mocap reconstruction.

**Not adopted as automatic recording-lens correction.** The observed errors and
within-clip variation do not justify changing the default or adding a model download.
The evaluation code, weights and reports remain local. Further adoption needs validated
temporal/shared-intrinsics and distortion handling, numerical verification, and a
measured improvement in the resulting hand motion. Focal length and gravity remain
model estimates; they do not establish measured scale, camera trajectory or correct
3D hand motion.
