# Recording a useful video

Choose **First Person** for arms and hands, or **Third Person** for a body animation.
This selects the output workflow. It does not require the camera to have been worn
by the performer. The preview's camera toggle does not change reconstruction.

Record in good light with a short exposure when possible. Keep the subject sharp and
avoid motion blur, digital zoom changes and sudden camera movement. Keep the original
video; trimming or conversion should create a new file. MP4 with H.264 is a practical
input for the installed Windows decoder. MOV/phone codec support depends on Windows.

For hands, keep both wrists and fingers visible when the motion needs both hands.
Include an open hand and a relaxed closed hand at the beginning so handedness and
finger articulation are easy to review. Avoid an ultra-wide/fisheye lens unless you
can correct it with known calibration. The current automatic path estimates camera
intrinsics; it does not calibrate or undistort the lens. WildHands is designed for
egocentric footage. MediaPipe is the small default; WiLoR is the heavier alternative.

For body capture, record one person with their feet and head inside the frame. A
stationary camera and an unobstructed view are useful for reviewing ground contact.
Automatic GVHMR jobs currently use a full-frame crop and do not track a person moving
out of that crop. They output camera-relative motion. Camera motion, calibrated world
scale and reliable world root motion are not recovered by this integration.

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

If the performer lowers their hands but the target raises its arms, check camera
placement. Hand-only reconstruction cannot tell whether the camera was level or looking
down. Under **Advanced → First Person**, **Looking down** applies a 45-degree downward
capture tilt; **Level** restores zero tilt. Fine-tune **Capture pitch** if needed
(negative means down). This reuses reconstruction and changes the target animation
and export. It is a user correction, not recovered camera orientation. The separate
preview camera toggle only changes how you view the result.

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

For the four upstream example videos, run `dotnet run --project InferenceWorker -- download-samples samples`
from the repository root. This downloads and fully decodes the footage, records its source and metadata,
and does not execute a reconstruction model. See the [worker guide](InferenceWorker/README.md) for details.

The downloaded examples were inspected and processed on a Ryzen 7 7800X3D with 32 GB RAM.
MediaPipe processed the complete short hand clips: `video_0.mp4` (121 frames),
`segment_018.mp4` (120 frames), and `segment_037.mp4` (120 frames). Fresh reconstruction
took approximately 16, 17 and 18 seconds respectively with the Release C# verifier
after CPU crop and video-tracking corrections. Peak process RAM across this run was
1.31 GB. These are observed CPU costs, not minimum requirements or a controlled
speed comparison with previous Debug runs.
The GVHMR example used the first second of `tennis.mp4`; full-length tennis accuracy
has not been validated. Preview/export checks include native FBX compilation and
animated playback in s&box.

Review these examples critically. After the correction, the plate-handling clip had
77 left-hand and 75 right-hand observed frames out of 121. The cupboard clip had only
2 left-hand and 84 right-hand observed frames out of 120. The cooking clip had all
120 right-hand frames but no left-hand detection; part of that hand lies outside the image.
Acceptance decreased in the first two clips compared with the earlier pipeline;
matching the reference preprocessing has not established a capture-quality improvement.
These counts describe model acceptance, not verified accuracy. Abrupt rotation changes
also remain in some predictions. No model
here has been verified to match ACE's accuracy. Contact with plates, containers or
utensils is not an automatic prop track or a solved grip. Reconstruction, temporal
synchronization and successful export do not establish correct 3D motion.

MobileHand is another lightweight option under **Advanced → Hand models…**. All three
hand samples completed its C# inference, cleanup, target preview, armature-only export
and native s&box playback checks. It shares MediaPipe's crop detector, so the missing-hand
counts above also apply. Its small checkpoint does not solve those detection failures.
The sampled overlays showed finger misalignment, and measured pose/depth jumps remain
large. Use it as an experimental alternative and inspect the result before exporting.
