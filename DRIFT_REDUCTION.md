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

A hand lost for more than 0.1 second used to freeze in its last pose and snap to the next
observation. Such losses now hold, then glide into the reacquired pose with an eased
blend over at most 1.5 seconds. Bridged samples are labelled **inferred gap**, carry no
confidence, and are never counted as observed; contact-protected samples are left alone
and `BridgeSeconds = 0` restores the old behaviour. In `video_0`, where both hands leave
the image for about a second, the default WiLoR path previously showed crossed, frozen
arms in the middle of the loss; with the corrected right-hand track and the glide it shows
uncrossed arms moving toward where the hands reappear. The motion inside a loss is
unknown, so this is a presentable guess rather than recovered movement.

This reduces small tracking fluctuations. It cannot remove sustained depth bias or
recover world motion from camera-relative hands. MediaPipe still uses an assumed wrist
plane; native MANO backends still infer monocular depth. Confirmed contacts with explicit
object tracks provide stronger anchors; see [prop tracks](PROP_TRACKS.md).

Arm intersections are still a separate limitation. In the downloaded `video_0` excerpt,
the synchronized preview at 2 seconds shows intersecting forearms while both hands are
labeled `Unobserved` and retain their earlier poses. The MediaPipe backend puts both wrists
on an assumed depth plane; this cannot represent the correct depth ordering of crossing
arms. Independently solved target elbows do not enforce skin clearance. Smoothing does
not recover those missing observations or resolve the depth ambiguity.

Manual wrist-position intervals provide a reversible correction path under
**Advanced → First Person → Correct wrist…**. They offset a previously observed
wrist in capture-camera space, blend at the interval edges and run before prop
constraints and final target arm IK. Wrist rotation, finger articulation and limb
lengths are preserved. The original capture and its missing-data labels are unchanged.
Edits are stored per target/workspace, can be disabled or removed, and appear as blue
timeline bars. They do not estimate the correct depth or guarantee skin clearance.

The real 121-frame hand excerpt passed native editor correction, save/reopen,
target-isolation, disable/remove and compiled FBX playback checks on Human and Citizen.
Frames outside the edited interval were unchanged; wrist rotation and deforming-bone
lengths stayed within floating-point tolerance. Citizen's reach limit reduced the
requested displacement. Visual review still showed crossing arms: these checks verify
editable correction and export, not automatic removal of intersections.

A bounded, coupled elbow-swivel experiment preserved wrist poses, finger motion and
bone lengths, but failed visual acceptance. Estimated capsule overlap remained in all
41 originally overlapping Human frames and all 40 Citizen frames of the 121-frame
excerpt. Maximum proxy penetration changed from 4.82 to 4.30 cm for Human and 3.62 to
3.31 cm for Citizen, while the elbows visibly changed. Those radii are anatomical
estimates, not measured skin surfaces. Native export and playback passed, but that did
not resolve the intersections. This experiment is not enabled or shipped as a fix.
Original observations and exports remain available; no missing motion is presented as
recovered capture. See [hand backend comparisons](HAND_BACKENDS.md) for measured depth
and pose limitations of the alternatives.

A second experiment allowed paired wrist-depth corrections during the shared tracking
loss at frames 53–81 (1.77–2.70 seconds), with 0.2-second fades. It searched 81 depth
pairs per target, bounded to 20 cm per wrist, through the existing target IK. Estimated
arm-capsule overlaps fell from 41 to 20 frames on Human and 40 to 19 on Citizen over
the 121-frame clip. Maximum proxy penetration did not improve: 4.82 cm and 3.62 cm,
respectively. The search selected opposite depth orderings on the two rigs.

Actual wrist displacement reached 18.72 cm on Human and 18.48 cm on Citizen; the
correction alone introduced up to 1.44 m/s of wrist motion. Observed frames were
unchanged, and deforming-bone lengths and wrist orientation were preserved. Both
targets passed native preview, armature-only FBX export and compiled playback.
Synchronized visual inspection still showed intersections at the fade boundaries,
and Citizen's near-camera hand became much larger in the view. These results reject
automatic adoption: fewer capsule overlaps do not establish better captured motion
or clear skin surfaces. The search remains a local experiment. Existing manual
corrections remain available; no automatic depth displacement is enabled.

A skin-level inspection also confirmed the crossing in Human's rendered pose at 2 seconds.
The check used 2,372 vertices and 4,636 triangles from the installed source arm/hand meshes,
weighted by the native editor's posed bone transforms. Their reconstructed bind positions
matched compiled model vertices within 0.00055 inches. It found 425 opposite-arm triangle
crossing pairs. This is a surface-crossing count, not penetration depth, a complete
self-collision test, or direct GPU skin readback; source floating-point weights differ
slightly from the engine's quantized weights.

Citizen's corresponding check used 1,802 vertices and 3,504 triangles, matched bind
positions within 0.00059 inches, and found 1,152 crossing pairs. Visual inspection of
both native skin/wire overlays confirmed intersections. Counts depend on mesh topology
and must not be compared between rigs as a severity score.

The same inspection found that Human's model constraints alter forearm helper rotations
after pose overrides. Skin calculated from the library's unconstrained solved bones differed
from skin calculated from native bone transforms by up to 2.02 inches in that frame
(1.49 inches on Citizen).
Bone-position agreement alone therefore does not verify skin or rotation agreement.
Automatic collision correction remains unverified; the diagnostic does not change poses.

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
probabilities exceed 0.8. This comparison used the earlier fixed image crop. These are
model-predicted static intervals, not ground truth.

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

Automatic body capture now follows a prominent subject with a lightweight person detector.
Before ViTPose/HMR2 inference, its crop center and size receive two centered five-frame
averages, matching GVHMR's crop preprocessing. A wider margin contains the estimated
body circle inside the models' 3:4 input. Short interior tracking gaps are interpolated;
ambiguous subjects and longer losses stop with an actionable error. Raw detections and
their scores remain separate from these crop estimates and the resulting motion.

The first, unsmoothed crop experiment was rejected: peak raw camera-relative root speed
rose from 3.00 to 11.17 m/s on the tennis clip. Stabilized crops reduced it to 2.73 m/s.
All 5,288 joint observations with heatmap scores above 0.5 stayed inside the central 90%
of their crops. Neither crop coverage nor a lower speed proves correct depth or 3D motion.
The new reconstruction still contains a sharp left-elbow change near 2.68 seconds.
Additional broad smoothing is not enabled to conceal it.

On this new 312-frame result, stationary-camera source refinement followed by target foot
anchoring gave the following measurements over the same 372 predicted-static steps within
that result. The contact predictions differ from the earlier 458-step comparison above.

| Target | Mean step before → after target anchoring | Largest step before → after |
| --- | --- | --- |
| Human | 0.247 → 0.018 cm | 0.940 → 0.832 cm |
| Citizen | 0.297 → 0.014 cm | 0.931 → 0.512 cm |

Human pelvis travel changed from 74.85 to 74.82 cm, and Citizen from 65.45 to 65.39 cm.

Those tennis measurements assumed a still camera that the footage does not have. The
worker now tests this itself: corner features outside the followed person are tracked
from the first frame to every fifth frame, with forward-backward agreement, and the
camera counts as still only when the median background shift stays under 0.4% of image
width in every sample and at least 80% of samples are usable. The tennis clip moved
1.3% at the median and 2.2% at most, a slow pan, so it is no longer refined
automatically and its figures above describe an assumption rather than the recording.
Head-mounted clips measured 2–8%. A tripod recording of a karate kata
([Hean Shodan](https://commons.wikimedia.org/wiki/File:Karate,_Hean_Shodan.webm),
CC BY-SA 4.0, 640×480, 25 fps, first 12 s) measured 0.03% and 0.05%. This is an
image-motion test: it does not recover camera motion, and a moving background can
defeat it.

When the camera tests still, Third Person applies stationary-camera refinement and
**Reduce foot drift** without being asked; the untouched capture stays beside it and
**Restore original capture** reopens it. Two further corrections were needed on the
kata clip. Reconstructed world height wandered as the performer moved in depth, leaving
planted ankles anywhere from 6 to 20 cm above one another. Because a still camera sees a
level floor, the height of each predicted contact above the target's standing rest
height is taken as that drift, frame by frame, using the lowest planted joint so a
raised heel does not count. It is interpolated between contacts, averaged over a quarter
second, never allowed to push a foot joint below the floor, and removed from the root
before anchoring. Contacts more than 30 cm from the floor are treated as real elevation.
Planted anchors within 8 cm of the floor then take the floor height instead of their
interval median, which had left feet hovering.

Over the same 368 predicted-static steps on Human, 300 frames:

| Stage | Mean step, ankles / toes | Largest step | Lowest foot support above rest |
| --- | --- | --- | --- |
| Previous default (camera-relative) | 1.49–1.72 / 1.50–2.27 cm | 14.07 cm | not comparable |
| Refined, anchored, before levelling | 0.002 / 0.025–0.029 cm | 0.77 cm | 0.9–3.0 cm (hovering) |
| Refined, levelled and anchored (new default) | 0.007–0.054 / 0.051–0.108 cm | 2.04 cm | −0.9 to −0.7 cm |

Citizen's different proportions gave 0.004–0.082 cm mean and 1.83 cm largest on the same
steps, with limb lengths within 0.0001 cm. Levelling costs a little of the anchoring's stillness, because the root now moves
vertically inside long contacts, and lets toes dip up to 0.9 cm under their rest height
for 12–20 frames. Limb lengths stayed within 0.0001 cm. The largest single-frame joint
rotations (56° forearm, 43° hips) coincide with motion-blurred strikes and turns in the
video and are not smoothed. The native gate ran this path from upload to a 300-frame
FBX that compiled and played; five synchronized frames matched the performer's stances.
Static intervals are model predictions, and one clip is not a guarantee for other floors,
stairs or moving cameras.

Three retargeting faults made the kata character look wrong even with correct contacts.
It shrugged when the performer did not. Body captures had copied the reconstructed
clavicle's direction outright, but an SMPL clavicle starts low in the chest and points
upward even at rest, while Human's is nearly level. In the performer's neutral opening
stance her shoulder line sat 6° and 3° below her own skeleton's rest, yet Human's sat 12°
and 30° above its rest. The clavicle's change from the performer's own rest is now
measured in an anatomical chest frame (up, left-right, forward) carried by each
skeleton's chest bone and applied to the target's own rest clavicle; the arms keep their
solved world orientation, and depression below rest is limited to 4° because a T-pose
rest already holds shoulders higher than a standing body does. The same frame now
measures 5° below and 1° above rest, and the frame with a genuinely raised arm still
lifts that shoulder 23° (18° in the source). This supersedes the earlier
captured-direction policy for clavicles; explicit transfer modes are unaffected.
Its legs were splayed: the reconstructed performer's hip joints sit 0.15 leg-lengths apart
and Human's 0.25, so copying leg rotations carried both feet outward by the extra
half-width and a 0.62 leg-length stance became 0.73. Body captures now move each ankle
back along the pelvis' own lateral axis by that difference and re-solve the leg before
levelling and anchoring, which gives 0.64 on the same frame with limb lengths within
0.0001 cm and unchanged foot anchoring. Its fingers were a splayed claw, because a body
capture has no finger tracks and the rig stayed in its bind pose; such hands now hold one
authored, slightly curled resting shape, never applied when the source has finger tracks.
All three appear in the capture details. The arms in that clip's opening stance sit wider than
the performer's because GVHMR itself reconstructs the wrists 45 cm apart; that is left as
reconstructed.

A moving camera is no longer left camera-relative. In the role of GVHMR's SimpleVO,
corner features outside the person are followed between every sixth frame with
forward-backward agreement, each pair is fitted with the rotation homography K R K⁻¹
under the job's assumed lens, and the nearest rotation is chained and interpolated to
every frame. That rotation replaces the still-camera value in both places the C# port
already accepted one: the temporal network's conditioning and the gravity-view world
rollout. Pairs with under 40 agreeing features, or turning over 25°, claim no rotation,
and unless 80% of pairs solve the capture stays camera-relative.

This was checked against a known answer. The tripod kata clip was re-rendered through a
synthetic handheld camera (a ±5° pan with tilt, roll and shake, rendered at a different
focal length from the one the worker assumes, then cropped), so the still-camera result
of the original is the reference. The accumulated rotation was recovered within 0.7° on
average and 2.0° at worst over a 5.6° sweep, with 48 of 50 pairs solved.

| Handling of the handheld fixture | Pelvis path error, mean / max | Heading error, mean | Height error, mean | Travel (reference 8.53 m) |
| --- | --- | --- | --- | --- |
| Wrongly assumed still | 32.1 / 67.0 cm | 5.4° | 20.1 cm | 8.57 m |
| Rotation followed, root unanchored | 25.6 / 94.4 cm | 3.1° | 7.9 cm | 6.65 m |
| Rotation followed, root anchored | 21.5 / 71.1 cm | 3.1° | 2.2 cm | 7.69 m |

Paths were compared after one rigid 2D alignment. The anchored row applies when at least
80% of background features fit a single rotation, meaning little parallax and a camera
turning about a nearly fixed point: camera-space pelvis positions, turned back by the
followed rotation, then anchor the root exactly as for a still camera. With more parallax
the camera also travelled, its position is unknown, and the unanchored row applies. Part of
the remaining error is not camera handling at all, since the cropped fixture gives the
networks a different image from the reference. On the real tennis clip, a pan of under a
degree with all 52 pairs solved, predicted-static foot steps went from 0.71–0.92 cm per
frame camera-relative to 0.003–0.031 cm, with feet at floor height. Camera translation and
scene scale are never recovered; this is rotation following, not camera tracking.

Following the subject also changed. The face-and-hips person detector sees the whole
frame at 224 pixels; on the kata clip it flickered on the distant performer and handed the
track to a bystander walking past, ending the job at 4.4 s. It now only finds the
subject, looking also in a window around the last position and in overlapping tiles while
none is known. Each later crop comes from the previous frame's own 2D body joints, as
pose trackers do. A box around partly visible joints is smaller than the body, and a
smaller crop hides more joints: one blurred frame shrank a 292-pixel crop to 54 pixels
in five frames. Crop size may therefore shrink by at most 8% per frame and only while
twelve joints are confident, a failed frame is retried in a wider window before the
detector is asked, and a detector crop is held near the followed size. The 300 frames
then used the detector once, followed joints 297 times and widened twice, with at least
ten confident joints throughout. Losses longer than half a second still stop the job.
The right-arm serve peak stayed at export frame 134, about 4.49 seconds. Target anchoring
preserved its rotation, and measured limb-length errors stayed below 0.001 cm.
These numbers describe estimated contacts under the stationary-camera assumption;
they are not ground-truth foot-slip or world-drift accuracy.

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

Repeating the translation-only test with the calibrated HOT3D view and native model
poses did not justify adoption either. The robust palm fit accepted only 2/236 MobileHand,
46/236 WildHands and 35/236 WiLoR observations. On the same accepted samples, WildHands'
mean wrist disagreement improved but its worst error reached 1.57 m; WiLoR's p95 wrist
disagreement worsened and peak fitted wrist speed nearly doubled. The fit leaves pose
rotation fixed and uses no reference landmarks in optimization. It remains a local
experiment; calibration and lower average reprojection error alone do not make it reliable.

Recent papers and measured candidate decisions are recorded in [research notes](RESEARCH.md).
The C# FootMR trial did not justify a default backend change: its foot displacement was
worse on the same model-selected contact steps, and its arm-motion peak timing differed.
No additional filter is enabled merely because it lowers a jitter metric.

The native hand models also exposed a target-reach failure in `video_0`: all 157 observed
wrist targets from either WildHands or WiLoR exceeded both built-in rigs' arm reach.
Human's available shoulder-to-wrist reach was 51.34 cm and Citizen's was 44.86 cm.
WildHands' mean requested distances were 143.42/131.18 cm on Human; WiLoR's were
154.39/139.95 cm (left/right). Clamping preserves bone lengths but flattens wrist motion.
The preview now shows **Clip: arm reach limited** when observed targets are displaced by
more than 1 mm. Its tooltip reports the affected count and maximum displacement across
the clip. This measures target IK displacement, not reconstruction accuracy, and does
not modify the motion or turn an observation into a missing frame.

A uniform trajectory-scale trial removed reach clipping but failed visual acceptance:
the hands moved too close together and intersected. WiLoR's fitted Human scale was
0.1853; the synchronized 1-second preview showed overlapping hands. This adjustment
is not enabled or shipped. Better camera/depth handling remains necessary; successful
animation export does not resolve the mismatch.

Export resampling now treats timestamps within at most one microsecond (and at most
0.01% of the adjacent interval) as the same source sample. This fixes 100 ns decoder
rounding that previously mislabeled seven of this clip's 157 observed wrists as missing.
The pose, observation label and stationary-joint probability use the same endpoint rule.
Real intervals spanning missing data remain missing, and intermediate stationary-joint
probabilities still use the conservative minimum. Original timestamps remain unchanged.

An isolated WiLoR camera-sensitivity check reused all 121 frames of `video_0`'s native
predictions, keeping finger/wrist rotations, evidence and timestamps identical. Changing
the assumed focal length from 2,202.91 px to 600 px reduced Human's >1 mm reach-limited
wrist targets from 157/157 to 39/157, and maximum target IK displacement from 141.6 cm
to 6.2 cm. Citizen still limited 76/157, with a 9.6 cm maximum. Unlike uniform trajectory
scaling, this changes camera depth without shrinking lateral hand spacing. Native preview,
armature-only FBX export and compiled playback passed; synchronized visual review still
showed pose differences. These numbers measure target reach, not ground-truth accuracy.

The [HaWoR example's 600 px fallback](https://github.com/ThunderVVV/HaWoR/blob/main/scripts/scripts_test_video/hawor_video.py)
is an assumption, not provided calibration for this footage. Its frame extraction does
not resize the source. This experiment does not justify applying that focal length to
every video. The editor now accepts an optional recording FOV in Advanced while retaining
the existing automatic default. WildHands requires fresh inference when this changes,
because camera-ray encodings enter its image network; a WiLoR-only cached prediction
replay must not be used to simulate WildHands at another focal length.

Fresh WildHands inference through the editor at the same 600 px assumption produced
121 frames and the same 157 observed hand instances. Neither Human nor Citizen needed
reach clamping on those observed wrists. CPU detection/inference took 27.01 s with
1.70 GB peak worker working set on the Ryzen 7 7800X3D; this excludes setup, decode,
retargeting and export. The native gate verified FOV persistence, rejection of invalid
FOV without losing the current capture, and compiled 94-bone, 121-frame FBX playback.
The reviewed frames still disagree with the source hand poses and show target-mesh
deformation. Removing reach clamping is useful but does not establish acceptable final
capture quality or justify changing the default lens assumption.

Blank **Recording FOV** no longer means the image-diagonal focal length. Native hand
capture sizes the lens from the hands in the clip, places WildHands wrists on the
detected palm's viewing ray with a clip-wide depth factor, and keeps left/right tracks
physically consistent; [hand backend options](HAND_BACKENDS.md) records the method and
its reference measurements. On the same complete `video_0` WildHands capture, Human's
mean target IK wrist displacement fell from 92.1 / 79.8 cm (left / right, 157/157
wrists beyond reach) to 0.28 / 0.003 cm with 16/161 beyond reach and a 3.1 cm maximum.
The assumed lens was 118° horizontal. Citizen's 44.9 cm arms still fell short for 71/161
wrists, by up to 6.7 cm.

Hand capture therefore also shrinks toward the capture camera, by at most a quarter,
when more than one observed wrist in twenty lies beyond the target's arm reach. Every
wrist keeps its viewing ray, so the first-person picture does not change; this is not
the earlier rejected trajectory scale, whose fitted 0.19 overlapped the hands. With it,
Human shortened 6/161 wrists by at most 1.4 cm and Citizen 3/161 by at most 0.6 cm.
Shortfalls this small are recorded in the capture details without raising
**Clip: arm reach limited**, which still appears once more than 5% of wrists or any
wrist by 2 cm is affected. Clips with imported prop tracks are not scaled, because
props share the capture space. These are target IK measurements, not capture accuracy,
and the arms crossing while both hands are out of view is reduced only as far as the
corrected right-hand track allows; the hold itself remains an unobserved interval.

The preview now reapplies the exported pose to final render bones after model evaluation.
Human's stock constraints had changed 22 bones in a reviewed WildHands frame, including
pinky rotations by up to 24.5° and an elbow helper by 41.7°. Omitting helper overrides
did not remove that difference; the native constraints still ran. Direct final-transform
application retained the baked pose. A native Human gate checked every matched bone at
five clip positions: 470 comparisons after rendering, zero position difference and
maximum quaternion-dot error 1.2e-7. Source-float skinning driven by those native bones
also agreed with the solver within 0.000013 inches; this is not a GPU vertex readback.
Citizen passed the same check on the 312-frame GVHMR tennis clip: 475 bone comparisons,
zero position difference and maximum quaternion-dot error 1.2e-7. Its source-float
skinning comparison stayed within 0.000008 inches. Both native gates passed FBX import
and playback; synchronized video/preview frames and the skin overlays were inspected.

This fixes preview/export disagreement, not reconstruction drift or intersections.
The inspected FPS frames still have pose errors and occlusion gaps. The receiving
ModelDoc model can apply its own constraints to imported animation; Human's CopyPinky
must be disabled on a project-owned model to preserve independent captured pinkies.

Arms/hands-only target verification used s&box's shipped `first_person_arms_preview.fbx`
and the existing 121-frame WildHands reconstruction of `video_0`. The picker now accepts
its partial skeleton and uses the exact s&box finger aliases; the generic fallback had
shifted zero-based phalanges by one segment. Z-up centimetre targets also retain their
actual units through placement, contacts and FBX export. Previously that path used inch
conversion, which the native export comparison rejected.

The 70-bone skinned arms target passed native preview and compiled FBX playback. All 350
sampled render-bone comparisons retained the baked pose; FBX reimport differed by at most
0.000019 cm and quaternion-dot error 1.2e-7. A 44-bone detached-hand fixture extracted from
that rig also retained all 121 samples through retarget/export/reimport, including motion
on all 30 phalanges; its eight metacarpals stayed at rest. This detached fixture was an
offline armature test, not a second skinned-model playback test. The 190 motion tests
passed, including equivalent physical motion across Y-up cm, Z-up cm and engine inches.
Visual review still showed reconstruction disagreement and crossed held poses during
occlusion. These fixes establish usable target selection and export, not capture accuracy.
