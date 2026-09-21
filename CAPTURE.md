# Recording a useful video

Choose **First Person** for arms and hands, or **Third Person** for a body animation.
This selects the output workflow. It does not require the camera to have been worn
by the performer. The preview's camera toggle does not change reconstruction.
Choose **Human** or **Citizen** at the preview's top right to change the character.
Citizen is Terry's `models/citizen/citizen.vmdl`. Both use their own skeleton and
proportions; switching reuses the captured motion and updates the target FBX export.

New hand captures widen the initial FPS preview when their stored camera geometry
needs it, up to 120°. Saved FOV adjustments still take priority. For older captures,
**Advanced → Process again** can rebuild camera metadata from cached predictions;
previous motion files remain intact. This improves framing only, not depth accuracy
or intersections caused by tracking errors.

Record in good light with a short exposure when possible. Keep the subject sharp and
avoid motion blur, digital zoom changes and sudden camera movement. Keep the original
video; trimming or conversion should create a new file. MP4 with H.264 is a practical
input for the installed Windows decoder. MOV/phone codec support depends on Windows.
Preview and reconstruction honor standard quarter-turn video orientation metadata
and exclude coded padding. Use square pixels; unusual crop or display transforms may
require exporting a standard copy. This image rotation is separate from the estimated
camera placement used to position reconstructed hands in the scene.

For hands, keep both wrists and fingers visible when the motion needs both hands.
Include an open hand and a relaxed closed hand at the beginning so handedness and
finger articulation are easy to review. Avoid an ultra-wide/fisheye lens unless you
can correct it with known calibration. The current automatic path estimates camera
intrinsics; it does not calibrate or undistort the lens. WildHands is designed for
egocentric footage and is the FPS default, with MediaPipe locating hands for its crops.
WiLoR is the heavier alternative; MediaPipe-only reconstruction remains optional.

With **Recording FOV** blank, native hand capture sizes the lens from the hands
themselves. A hand's size fixes its depth only in proportion to the unknown focal
length, so the clip's median hand is placed 0.45 m from the camera and its farthest
hands within 0.65 m. This suits a head- or chest-mounted camera; footage of someone
else's hands recorded from farther away is pulled to the same first-person distances.
The assumed angle is listed with the capture details in **Advanced**. It is a prior about where
first-person hands usually are, not lens calibration.

If you know the recording's horizontal field of view, set
**Advanced → First Person → Recording FOV (°)** and choose **Process again**.
This optional 20–150° setting changes
reconstruction depth; **Viewmodel FOV** changes only the preview. It is available for
WildHands, WiLoR and MobileHand, not MediaPipe-only or body capture. The value refers
to the actual video after cropping or stabilization, which can differ from a phone's
advertised lens FOV. It assumes a centered pinhole camera with square pixels, does not
undistort fisheye footage, and is recorded as an estimate rather than calibration.
Different settings create separate reconstruction caches and preserve the previous
motion. The setting is restored when that native capture reopens and cleared for a
different uploaded video. A lower reach error alone does not prove the lens estimate
or recovered 3D movement is correct.

For body capture, record one person with their feet and head inside the frame. A
stationary camera and an unobstructed view are useful for reviewing ground contact.
Automatic GVHMR jobs detect a prominent person and follow the estimated image-space
crop through the selected range. Record one clear foreground subject; ambiguous people
or prolonged tracking loss stop processing with a useful error. Crop tracking does not
recover camera motion, calibrated world scale or reliable world root motion. The default
output remains camera-relative.

The Third Person target preview removes the capture's constant camera offset and
places its lowest reconstructed joint at the target's rest ground. The half-metre
editor grid shows that fixed floor reference. This assumes the clip reaches ground
at some point; a clip that is entirely airborne needs a manual **Ground** adjustment.
Movement within the clip is retained, and the original camera-relative motion stays
unchanged. World-relative imported motion retains its authored placement.

If the recording camera stayed still, use **Advanced → Third Person → Refine · stationary camera**.
This applies GVHMR's source root/contact processing and limb IK to saved predictions,
then rebuilds the target preview. It does not repeat neural inference. The result is
estimated world-relative motion under your stationary-camera assumption; neither
camera motion nor metric scale has been measured. Do not apply it to moving-camera footage.
The button becomes **Restore original capture**. Raw camera-relative reconstruction
and predictions remain unchanged; adjustments are saved separately for each opened
motion file. Keep the original file at its recorded location for restoration.

Use short takes. A job currently allows at most 1,800 selected frames. Longer footage
can be processed in explicit ranges under Advanced; frames are not silently discarded.
On an iPhone or Android device, connect to the same network, choose **Upload from phone**,
scan the QR code, and select videos from Photos or Gallery. Keep that page open for
further uploads during the one-hour pairing. Footage stays on the PC.

If scanning cannot open the page, select the PC's **Wi-Fi or Ethernet** connection
in the receiver's network dropdown and scan the newly generated code. The default
now prefers the physical LAN; WSL, Hyper-V and VPN adapters are labeled virtual and
usually cannot be reached by a phone. The PC can use Ethernet while the phone uses
Wi-Fi on the same router. Keep the receiver open and use the complete `http://` link,
including its port and pairing fragment. Guest-network isolation, a VPN or a firewall
can still prevent a connection; no cloud relay is used.

After processing, review the source video beside the animated skeleton. Check wrist
orientation, fast gestures, tracking loss and reacquisition. Hidden shoulders/elbows
are estimated with IK; the body backend does not provide detailed finger capture.
Props visible in the video are not automatically tracked. Inspect contacts manually
before using the motion with a weapon or another object.

Hand captures show each wrist's evidence below the preview for the frame actually
displayed. **Not observed** warns that the pose is not newly captured; an earlier pose
may be retained. **Inferred gap** identifies interpolated motion. **Reconstructed**
means a model prediction, not verified accuracy. Active manual wrist offsets are marked
separately and do not clear missing-data warnings. These labels describe wrist tracks;
they do not certify finger accuracy or the estimated shoulders and elbows.

If the performer lowers their hands but the target raises its arms, check camera
placement. Hand-only reconstruction cannot tell whether the camera was level or looking
down. Under **Advanced → First Person**, **Looking down** applies a 45-degree downward
capture tilt; **Level** restores zero tilt. Fine-tune **Capture pitch** if needed
(negative means down). This reuses reconstruction and changes the target animation
and export. It is a user correction, not recovered camera orientation. The separate
preview camera toggle only changes how you view the result.

For a misplaced tracked wrist or an intersecting interval, use **Advanced → First
Person → Correct wrist…**. Choose the hand, video start/end times and a position
offset in centimetres: X right, Y up, +Z toward the capture camera. The offset blends
at the interval edges. It preserves captured wrist rotation and finger articulation;
arm reach limits and confirmed prop contacts take priority. Review against the video:
this is a manual edit, including when the tracker has lost the hand, not recovered
depth or automatic collision correction. A never-observed hand cannot be created.

Blue timeline bars mark manual wrist edits. Double-click a bar to edit it, or use the
saved correction's visibility/delete buttons to disable or remove it. Corrections are
saved for the selected target and workspace and included in target FBX export.
The captured-skeleton export bypasses target corrections. Changing these edits reuses
reconstruction and leaves original motion files and missing-data labels intact.

Applied corrections are saved beside the opened `.hmotion` as `.hmotion.adjustments.json`.
Capture placement, arm settings, ground/facing and viewmodel settings are remembered
separately for each target rig and workspace. Cleanup and reviewed contacts belong to
the clip. Reopening rebuilds from the original reconstruction; changing cleanup does
not repeatedly filter the previous result. Automatic captures retain a reference to
their raw output. Manually opened legacy motion files without adjustment metadata
use that file as their starting point, even if it was already cleaned elsewhere.
The adjustment file currently references the original by absolute path: keep the raw
file in place. Missing or changed originals produce an error instead of silently
using already-cleaned motion. Unapplied field edits are not saved.

Export saves an FBX armature and animated bones. It does not export a mesh, skin weights
or a newly rigged character. Keep the raw `.hmotion` and video for later corrections;
baked FBX playback does not require the C# worker or neural model downloads.

Third Person capture transfers the reconstructed collarbone directions while retaining
the target's bone lengths and attachment positions. This avoids lowering the Human's
shoulders by replaying SMPL's raised zero-pose collarbone offset onto an already flatter
target rig. Body turns also stay separate from shoulder-relative motion. The preview's
bone overlay follows the rendered model's joints and omits the chest-to-clavicle parenting
links, which previously looked like collarbones starting too low. Reopen the raw capture
to rebuild an existing result with this correction; reconstruction is not required again.

The inspected upstream samples are HaWoR's [video_0.mp4](https://raw.githubusercontent.com/ThunderVVV/HaWoR/main/example/video_0.mp4),
[segment_018.mp4](https://raw.githubusercontent.com/ThunderVVV/HaWoR/main/example/segment_018.mp4),
[segment_037.mp4](https://raw.githubusercontent.com/ThunderVVV/HaWoR/main/example/segment_037.mp4),
and GVHMR's [tennis.mp4](https://raw.githubusercontent.com/zju3dv/GVHMR/main/docs/example_video/tennis.mp4).
The local verification pipeline downloaded, checksummed and fully decoded them.
Footage, download tools and verification outputs are excluded from the distributed library.

The downloaded examples were inspected and processed on a Ryzen 7 7800X3D with 32 GB RAM.
MediaPipe processed the complete short hand clips: `video_0.mp4` (121 frames),
`segment_018.mp4` (120 frames), and `segment_037.mp4` (120 frames). Earlier Release C# worker measurements
took approximately 13, 13 and 20 seconds respectively
after visible-aperture and orientation correction. Peak worker RAM reached 1.30 GB.
These are observed CPU costs, not minimum requirements or a controlled speed comparison;
editor verification overlapped part of the run.
GVHMR also processed all 312 frames of `tennis.mp4` (10.41 seconds between its first
and last sample). With automatic stabilized crops, CPU reconstruction took about
18.6 minutes and peaked at 6.12 GB
worker RAM. The complete motion was checked on Human and Citizen proportions; the
full Human and Citizen animations passed native FBX compilation
and animated playback in s&box. Synchronized source/target frames were visually reviewed.
These checks do not establish full-length 3D accuracy.

Foot drift remains. During consecutive frames where GVHMR's static-joint probability
exceeds 0.8, Human foot joints still moved up to 6.77 cm per frame in the retargeted
camera-relative output. This is a diagnostic of remaining motion, not a calibrated
world-space slip measurement. The current geometric foot detector finds too few
stable intervals in this clip to resolve those contacts automatically.

The optional stationary-camera refinement is intended to reduce that residual motion.
Its output labels limb transforms affected by IK separately from reconstruction, and
still requires review against the video. Contact probabilities are predictions of
stationary joints, not observed grips or proof of correct world-space motion. Target
proportions can change contacts after source refinement. No additional generic body
smoothing is enabled automatically.

With **Reduce foot drift** enabled, final target ankle/toe anchors now use the retained
GVHMR stationary-joint predictions. On this tennis clip, average foot-joint movement
during those same predicted intervals fell from 0.247 to 0.018 cm per frame on Human,
and from 0.297 to 0.014 cm on Citizen. Largest steps fell from 0.94 to 0.83 cm and from
0.93 to 0.51 cm respectively. This comparison uses the current automatic-crop reconstruction
and the same stationary-camera motion
before and after target anchoring, not the camera-relative default. Residual drift and
inaccurate heights remain. The fast right forearm peak remained at frame 134 (about
4.49 seconds). Final target correction keeps limb lengths fixed instead of stretching.
The complete refined clip passed native FBX compilation and animated playback on both
Human and Citizen; restoring the original and reopening the cached refinement also passed.
See [drift reduction](DRIFT_REDUCTION.md) for FPS cleanup, comparison metrics and limits.

Review these examples critically. With the current detector and identity tracking, the plate-handling clip had
78 left-hand and 79 right-hand observed frames out of 121, with both present in 68 frames. The cupboard clip had
0 left-hand and 85 right-hand observed frames out of 120. The cooking clip had all
120 right-hand frames but no left-hand detection; part of that hand lies outside the image.
Correct image geometry has not established a capture-quality improvement;
model acceptance remains mixed and some previously accepted hands are now missed.
These counts describe model acceptance, not verified accuracy. Abrupt rotation changes
also remain in some predictions. No model
here has been verified to match ACE's accuracy. Contact with plates, containers or
utensils is not an automatic prop track or a solved grip. Reconstruction, temporal
synchronization and successful export do not establish correct 3D motion.

MobileHand is another lightweight option under **Advanced → Hand models…**. All three
hand samples completed its C# inference, cleanup, target preview, armature-only export
and native s&box playback checks with the preceding decoder. The corrected decoder also
completed a fresh 30-frame cooking clip. It shares MediaPipe's crop detector and its
missing-hand limitations. Its small checkpoint does not solve those detection failures.
The sampled overlays showed finger misalignment, and measured pose/depth jumps remain
large. Use it as an experimental alternative and inspect the result before exporting.

All four complete videos passed visible-dimension and timestamp checks against an
independent decoder. Metadata-only copies of the cooking clip exercised 0°, 90°, 180°
and 270° display rotation without changing compressed footage. A portrait copy also
passed automatic reconstruction, synchronized preview and native FBX playback in s&box.
These are orientation fixtures, not a physical iPhone/Android recording or connection test.

Visual review of the cooking sample with Citizen exposed the head intersecting the
first-person camera and appearing as a floating fragment beside the hands. First-person
preview now hides a model's separate Head body group when that group supports an empty
choice, and restores its previous selection in third-person view. Native before/after
renders confirmed removal of the Citizen fragment. Both Citizen and Human passed
visibility restoration checks without changing the sampled hand pose, other mesh groups
or raw capture. Their 120-frame armature exports also compiled and played in s&box.
Custom models without that body-group structure retain their original visibility.
