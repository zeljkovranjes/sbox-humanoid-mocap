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

**First person / Third person** above the preview changes only the viewing camera. Drag to look around or orbit, and use reset-camera to restore framing. Capture viewpoint is separate from output workspace: externally recorded movement can also produce an FPS animation. Hand-only reconstruction does not determine whether the recording camera was level or looking down; use **Advanced → First Person → Capture pitch** when placement needs correction.

The character selector shows **Human** or **Citizen**. Citizen uses Terry's classic `models/citizen/citizen.vmdl` and its own proportions. Switching rebuilds the preview and target export from the same capture; it does not rerun reconstruction. Each target retains its saved adjustments.

For third-person recordings made with a stationary camera, **Advanced → Third Person → Refine · stationary camera** reuses saved predictions for root and limb-contact correction. It opens a separate result; the same button restores the original capture. **Reduce foot drift** keeps predicted stationary ankles and toes anchored on the final target rig. Review the result before export. **In place** removes horizontal travel. See [drift reduction and measured limits](DRIFT_REDUCTION.md).

FPS cleanup reduces small wrist-position fluctuations while preserving fast movement and captured finger detail. **Advanced → Contact review** imports prop FBX animation and lets you add, edit or suggest wrist contacts from imported rigid surfaces. Contacts appear beneath the playback slider; right-click an interval to review it or adjust its timing. The contact dialog also edits sliding position keys. Review yellow suggestions before confirming. Confirmed contacts constrain the hands, with optional wrist-orientation anchoring for rigid grips; prop armatures appear in preview and export with their animated bones. See [prop tracks](PROP_TRACKS.md) for alignment and remaining limitations; hand reconstruction alone does not recover object motion.

The default export uses the preview rig and applied corrections. **Advanced → Export captured skeleton** keeps the reconstruction's skeleton and sample timing. Both export bone positions and rotations. Keep the original video, `.hmotion` and `.hmotion.adjustments.json` files for reversible edits; confidence, observation labels and contact-review data are not FBX animation channels. Applied adjustments are remembered per target and workspace, and changes reuse cached reconstruction.

Use **Advanced → Hand models…** to choose MediaPipe, MobileHand, WildHands or WiLoR for subsequent FPS uploads. MediaPipe is the lightweight default. Badges describe processing cost, not accuracy. See [hand backend options](HAND_BACKENDS.md) for measured results and limitations.

All reconstruction runs locally in a separate C# worker. First-time setup requires the included worker source and .NET 10 SDK, or a configured prebuilt worker. Models download on first use for the selected backend. Cancellation preserves completed observations for retry. Exported animation playback needs neither the worker nor its models. See the [worker setup](InferenceWorker/README.md) and [capture instructions](CAPTURE.md).

Custom models need a rig and skin weights. Automatic mapping is not perfect; check the
preview before exporting. This library exports animation and armatures; it does not
automatically rig or skin a character. Phone codec support depends on Windows Media Foundation.

Third Person uses camera-relative GVHMR reconstruction, without detailed finger capture.
Calibrated world recovery, automatic prop tracking and multi-camera fusion are not supported.

This is an experimental development build. Wrist depth, hidden elbows and shoulders
are estimated. Tracking loss, incorrect finger poses and motion jumps remain. The
main status flags missing or mostly untracked hands; Advanced shows coverage and gaps.
MobileHand has a smaller checkpoint but still shows pose and depth jumps. ACE-Ego-Hand
remains an optional planned backend; its inference is not implemented.
See [third-party notices](THIRD_PARTY_NOTICES.md).

Package ident: `notpointless.chomnr_humanoid_mocap`
