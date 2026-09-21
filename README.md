# Humanoid Mocap

A video-to-animation tool for the [s&box](https://sbox.game) editor.

- First Person and Third Person workspaces with automatic processing after upload.
- Phone video uploads from Photos or Gallery, with one-hour QR pairing.
- Experimental local hand, finger and body reconstruction in C#.
- s&box Human, classic Citizen and custom VMDL, FBX, GLB or glTF targets.
- Estimated arm IK, optional corrections and root-motion controls.
- Synchronized video and animation preview with a target-bone overlay and ground grid.
- FBX export containing the armature and animated bones, without meshes or skin weights.

Add the library to your s&box project and open **View → Humanoid Mocap**.
Keep only one copy of the library installed. Humanoid Retargeter can remain installed alongside it.

1. Choose **First Person** or **Third Person**.
2. Click **Upload video…**, drag in a video, or choose **Upload from phone**.
3. Let local reconstruction finish; processing starts automatically.
4. Play the source video beside the animation and review the captured movement.
5. Switch **Human / Citizen** at the preview's top right. Use **Advanced** for custom targets, models or corrections.
6. Click **Export…** and use the FBX as an animation source in s&box ModelDoc.

**Upload from phone** opens a local receiver. Connect your iPhone or Android device to the same network, scan the QR code and choose a video from Photos or Gallery. Keep the phone page and receiver open for more uploads during the one-hour pairing. Choose **New one-hour pairing** after expiry. Closing the receiver or choosing **Disconnect phones** ends access immediately. Files stay on your PC; uploads are limited to 2 GiB and preserve previous files.

**First person / Third person** above the preview changes only the viewing camera. Drag to look around or orbit, and use reset-camera to restore framing. For camera-relative hand capture, FPS reset restores the configured capture-camera direction. Capture viewpoint is separate from output workspace: externally recorded movement can also produce an FPS animation. Hand-only reconstruction does not determine whether the recording camera was level or looking down; use **Advanced → First Person → Capture pitch** when placement needs correction.

The character selector shows **Human** or **Citizen**. Citizen uses Terry's classic `models/citizen/citizen.vmdl` and its own proportions. Switching rebuilds the preview and target export from the same capture; it does not rerun reconstruction. Each target retains its saved adjustments.

For your own FPS hands, choose **Advanced → Target → Custom FBX / GLB…** or **Custom VMDL…**. Hand capture accepts arms- or hands-only rigs without hips or legs. Map the wrists and finger chains; automatic matching uses recognized hand presets, with manual mapping for unfamiliar names. Existing arms use IK; detached hands receive wrist motion directly. Viewmodels without a head or hips use their authored origin as the assumed camera position and their left/right shoulder or wrist spacing to suggest facing. Review these editable assumptions. **Export…** writes that target's armature and animated bones.

The target must have the finger joints you want to export. Citizen's four-finger rig has no pinky chain; choose Human or a five-finger custom rig to retain pinky motion. The preview flags unmapped joints; hover its status text to see the missing hand or finger chains. The original capture keeps those tracks.

First-person preview hides the character's separate head mesh when supported, preventing it from clipping through the camera. Switching to third-person view restores it. This affects only preview visibility.

For native hand capture, **Advanced → First Person → Recording FOV (°)** optionally supplies the video's horizontal lens angle. Leave it blank to size the lens from the hands, which keeps them at first-person distances within arm's reach. Choose **Process again** to apply it; previous captures remain intact. This affects inferred depth and is separate from **Viewmodel FOV**, which only changes preview framing. See [capture instructions](CAPTURE.md) before adjusting it.

Third Person checks whether the recording camera stayed still by following background detail outside the person. When it did, root and foot-contact refinement is applied automatically from the saved predictions, reconstructed floor height is levelled, and the status line says so; **Advanced → Third Person → Restore original capture** reopens the untouched result. For a moving or handheld camera the capture stays camera-relative, and **Refine · stationary camera** remains available if you know better. **Reduce foot drift** keeps predicted stationary ankles and toes anchored on the final target rig. Review the result before export. **In place** removes horizontal travel. See [drift reduction and measured limits](DRIFT_REDUCTION.md).

FPS cleanup reduces small wrist-position fluctuations while preserving fast movement and captured finger detail. **Advanced → Contact review** imports prop FBX animation and lets you add, edit or suggest wrist contacts from imported rigid surfaces. Contacts appear beneath the playback slider; right-click an interval to review it or adjust its timing. The contact dialog also edits sliding position keys and optional finger contact points. Review yellow suggestions before confirming. Confirmed contacts constrain the hands, with optional wrist-orientation anchoring for rigid grips; prop armatures appear in preview and export with their animated bones. See [prop tracks](PROP_TRACKS.md) for alignment and remaining limitations; hand reconstruction alone does not recover object motion.

The default export uses the preview rig and applied corrections. **Advanced → Export captured skeleton** keeps the reconstruction's skeleton and sample timing. Both export bone positions and rotations. Keep the original video, `.hmotion` and `.hmotion.adjustments.json` files for reversible edits; confidence, observation labels and contact-review data are not FBX animation channels. Applied adjustments are remembered per target and workspace, and changes reuse cached reconstruction.

The preview displays the baked FBX pose, including independent pinky motion and helper bones. Constraints on the model receiving the FBX can change that pose during playback. In particular, Human's stock **CopyPinky** copies ring-finger motion onto the pinky; disable it on your project-owned model when using captured five-finger animation. Shipped engine models are left untouched.

FPS uploads default to **WiLoR for pose reconstruction, with MediaPipe locating the hands**. It was closest to the video for fingers and wrists in every comparison, from head-mounted and outside cameras, and is the slowest: allow roughly a second per hand per frame on an eight-core CPU, after a 2.56 GB first download. Use **Advanced → Hand models…** to choose the faster WildHands for head-mounted footage, MobileHand or the lightweight MediaPipe-only option. The status line asks for review when reconstructed hands do not match the hands found in the video. Existing captures stay unchanged; use **Process again** to reconstruct with the selected model. Badges describe processing cost, not accuracy. See [hand backend options](HAND_BACKENDS.md) for measured results and limitations.

All reconstruction runs locally in a separate C# worker. First-time setup requires the included worker source and .NET 10 SDK, or a configured prebuilt worker. Models download on first use for the selected backend. Cancellation preserves completed observations for retry. Exported animation playback needs neither the worker nor its models. See the [worker setup](InferenceWorker/README.md) and [capture instructions](CAPTURE.md).

Published HOT3D hand/object annotations can also be [imported](HOT3D.md) by selecting an annotated Aria `.tar` clip through **Advanced → Open motion…**. The C# worker prepares its preview and armature tracks. This separate dataset workflow does not run reconstruction on your footage.

Custom models need a rig and skin weights. Automatic mapping is not perfect; check the
preview before exporting. This library exports animation and armatures; it does not
automatically rig or skin a character. Phone codec support depends on Windows Media Foundation.

Third Person uses camera-relative GVHMR reconstruction, without detailed finger capture.
It finds one prominent subject, then follows that person's own 2D body joints from
frame to frame, so distant subjects and bystanders walking through do not end the job.
Ambiguous starts and losses longer than half a second still need a shorter or clearer recording.
Calibrated world recovery, automatic prop tracking and multi-camera fusion are not supported.

This is an experimental development build. Wrist depth, hidden elbows and shoulders
are estimated. Tracking loss, incorrect finger poses and motion jumps remain. The
main status flags missing or mostly untracked hands; Advanced shows coverage and gaps.
MobileHand has a smaller checkpoint but still shows pose and depth jumps. ACE-Ego-Hand
remains an optional planned backend; its inference is not implemented.
See [third-party notices](THIRD_PARTY_NOTICES.md).

Package ident: `notpointless.chomnr_humanoid_mocap`
