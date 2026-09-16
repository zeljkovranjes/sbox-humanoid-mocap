# Drift reduction

The library combines local trajectory cleanup, contact constraints and target-proportion
IK. Each has a different role. Additional generic body smoothing is not enabled on top
of GVHMR's temporal model. Original footage, observations and reconstruction remain intact.

FPS wrist cleanup fits a short, symmetric local trajectory using actual timestamps.
It operates independently of wrist rotation, so rotating a hand does not disable position
cleanup. Constant-velocity motion, rapid translation, pronounced reversals, tracking-loss
boundaries and reviewed contact boundaries are protected. Position edits to observed
samples are capped at 3 mm. Finger cleanup remains separate and conservative. The default
root amount is now 0.25; saved settings remain respected. Reopen a capture and use
**Advanced → Apply adjustments** to rebuild from its original observations.

This reduces small tracking fluctuations. It cannot remove sustained depth bias or
recover world motion from camera-relative hands. MediaPipe still uses an assumed wrist
plane; native MANO backends still infer monocular depth. Confirmed contacts with explicit
object tracks provide stronger anchors; see [prop tracks](PROP_TRACKS.md).

Contact search now recognizes authored template metacarpals between observed wrists
and fingers. Incorrect missing labels previously blocked these suggestions on the default
MediaPipe skeleton. This repairs access to contact anchoring; it does not itself change
captured motion or reduce drift. Use **Advanced → Process again** for an older MediaPipe
capture to reuse cached observations with corrected labels, then review any new suggestions.

Reviewed rigid grips can also anchor wrist orientation relative to a moving prop bone.
This constrains rotational slip using an explicitly aligned object track, with the same
smooth activation/release as position contacts. The whole hand rotates together before
final arm IK, preserving local finger articulation and bone lengths. It is opt-in per
contact and does not estimate object motion or repair an inaccurate captured grip.

Optional, reviewed finger points add bounded target-rig hinge corrections after arm IK.
They follow the authoritative prop and any parent sliding keys, with smooth contact fades.
They preserve bone lengths and unselected finger motion; other target rigs ignore points
until their own placement is authored. These are editable geometric constraints, not skin
collision solving or additional reconstructed observations. See [prop tracks](PROP_TRACKS.md).

Third Person first preserves GVHMR's source temporal/contact processing. For an explicitly
stationary recording camera, **Refine · stationary camera** produces a separate estimated
world-relative result. Its `stationaryJoints` retain the model's contact probabilities,
separately from visibility, pose confidence and reviewed object contacts.

**Reduce foot drift** then applies those predictions on the final target proportions:

- Hysteresis and a minimum duration reject brief contact flicker.
- Robust interval anchors constrain ankles and toes, with smooth contact transitions.
- Toe-only contacts allow heel movement instead of forcing the entire foot flat.
- A shared root correction of at most 3 cm helps both legs reach their anchors.
- Final IK preserves bone lengths. No smoothing follows these constraints.

Disable **Reduce foot drift** to compare or reject incorrect inferred plants. It applies
only to world-relative motion carrying stationary-joint predictions. Camera-relative
body output retains its existing geometric foot correction. Older refined files lack
these predictions: restore the original capture and refine again with the updated worker.
The cached image/model predictions are reused; neural inference is not repeated.

The contact-based approach is consistent with the trajectory-refinement principle in
[WHAM](https://openaccess.thecvf.com/content/CVPR2024/papers/Shin_WHAM_Reconstructing_World-grounded_Humans_with_Accurate_3D_Motion_CVPR_2024_paper.pdf).
This implementation adds target-rig constraints to the existing GVHMR path; it does not
run WHAM or claim equivalence to its model. Full egocentric world recovery, such as
[HaWoR](https://www.openaccess.thecvf.com/content/CVPR2025/papers/Zhang_HaWoR_World-Space_Hand_Motion_Reconstruction_from_Egocentric_Videos_CVPR_2025_paper.pdf),
also needs camera/scale estimation. That capability is not supplied by these filters.

Measurements used the downloaded GVHMR tennis clip and HaWoR hand samples on the existing
Ryzen 7 7800X3D system. Body comparison uses the same 312-frame stationary-camera result,
same target proportions and the same 458 consecutive per-joint steps whose GVHMR
probabilities exceed 0.8. These are model-predicted static intervals, not ground truth.

| Target | Mean step before → after | Largest step before → after |
| --- | --- | --- |
| Human | 0.210 → 0.009 cm | 0.901 → 0.608 cm |
| Citizen | 0.248 → 0.007 cm | 1.237 → 0.500 cm |

The largest changes remain near contact transitions. Overall horizontal pelvis travel
remained 82.23 cm on Human and 71.90 cm on Citizen. The right forearm peak remained at
frame 134, about 4.49 s, with the same amplitude. Limb-length errors remained below
0.001 cm. Reduced contact motion is not proof of correct 3D pose, ground height or scale.
In-place export intentionally changes the reference frame; stationary-world slip numbers
must not be applied to its moving feet.

For FPS, the local linear-residual metric below compares the previous cleanup with the
new default. It is a jitter proxy, not measured world drift or ground-truth accuracy:

| Sample / observed hand | Previous → current mean residual |
| --- | --- |
| video_0 / left | 2.27 → 1.87 mm |
| video_0 / right | 8.89 → 8.66 mm |
| segment_037 / right | 3.84 → 3.66 mm |
| segment_018 / right | 2.12 → 1.87 mm |

All eight observed fast wrist samples in these checks retained their positions, and
timestamps were unchanged. No left-hand improvement is claimed for clips where that
hand was not detected. Depth errors, missing hands, incorrect contacts and source-pose
jumps still require review. The editor's synchronized video preview remains essential.

The same cleanup was replayed on saved native-model reconstruction: WiLoR's 15-frame
right-hand excerpt improved from 4.88 to 4.58 mm; WildHands' sparse 15-frame excerpt
improved from 4.12 to 3.93 mm across only three valid triples. Short observation runs use
a three-frame fallback. These small excerpts are not full-clip quality benchmarks, and
their cached predictions predate the corrected video decoder. MobileHand's newer
30-frame excerpt remained at 58.96 mm: its large depth jumps trigger the fast-motion
guard and are not solved by this filter. All 21 flagged rapid MobileHand samples were
preserved rather than silently classified as noise.

An additional perspective palm-fit experiment was rejected for automatic use. It fitted
wrist rotation and translation to six detector image landmarks. On a 30-frame MobileHand
excerpt, the midpoint-residual proxy fell from 58.96 to 9.51 mm, but full-clip tests exposed
regressions: maximum wrist speed rose from 11.26 to 40.40 m/s on `segment_037`, from
17.84 to 42.83 m/s on `segment_018`, and from 68.11 to 109.20 m/s on `video_0`.
Some fitted orientations also changed substantially. These results do not support enabling
the fit or stacking it with cleanup. No fitted transforms are applied by the library.

The native hand worker instead preserves detector image observations alongside the raw
model predictions and reports palm reprojection disagreement for review. This helps
identify captures needing correction without treating 2D agreement as correct 3D motion.
Fresh MobileHand runs on all three clips, short WildHands/WiLoR runs, cancellation/resume
and completed-cache reuse were checked. The MobileHand result passed native editor
preview, armature-only FBX export and compiled playback; synchronized visual review still
showed incorrect hand poses. These functional checks do not resolve that quality limitation.

A translation-only palm fit was also evaluated on the saved MediaPipe observations.
With estimated pinhole intrinsics, it accepted only 69 of 155 observed hands in `video_0`,
47 of 120 in `segment_037`, and 19 of 85 in `segment_018`. Maximum estimated wrist speeds
between consecutive accepted frames reached 11.38, 6.45 and 11.24 m/s respectively.
Those results do not justify replacing the default assumed wrist plane. The experiment
remains outside the library; no additional filtering or depth correction was enabled.
The [MediaPipe output definition](https://developers.google.com/edge/mediapipe/solutions/vision/hand_landmarker/python#handle_and_display_results)
describes hand-centered 3D landmarks; it does not supply calibrated camera translation.
