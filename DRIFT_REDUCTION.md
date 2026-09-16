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
