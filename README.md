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

**Upload from phone** opens a local receiver. Connect your iPhone or Android device to the same network, scan the QR code and choose a video from Photos or Gallery. Keep the phone page and receiver open for more uploads during the one-hour pairing. Choose **New one-hour pairing** after expiry. Closing the receiver or choosing **Disconnect phones** ends access immediately. Files stay on your PC; uploads are limited to 2 GiB and preserve previous files. If the phone page never loads, check that the receiver shows your Wi-Fi or Ethernet address rather than a WSL or VPN adapter, that guest or client isolation is off on the router, and that Windows Firewall allows the s&box editor on both private and public networks (Windows asks the first time; a dismissed prompt blocks it).

**First person / Third person** above the preview changes only the viewing camera. Drag to look around or orbit, and use reset-camera to restore framing. For camera-relative hand capture, FPS reset restores the configured capture-camera direction. Capture viewpoint is separate from output workspace: externally recorded movement can also produce an FPS animation. Hand-only reconstruction does not determine whether the recording camera was level or looking down; use **Advanced → First Person → Capture pitch** when placement needs correction.

The character selector shows **Human** or **Citizen**. Citizen uses Terry's classic `models/citizen/citizen.vmdl` and its own proportions. Switching rebuilds the preview and target export from the same capture; it does not rerun reconstruction. Each target retains its saved adjustments.

For your own FPS hands, choose **Advanced → Target → Custom FBX / GLB…** or **Custom VMDL…**. Hand capture accepts arms- or hands-only rigs without hips or legs. Map the wrists and finger chains; automatic matching uses recognized hand presets, with manual mapping for unfamiliar names. Existing arms use IK; detached hands receive wrist motion directly. Viewmodels without a head or hips use their authored origin as the assumed camera position and their left/right shoulder or wrist spacing to suggest facing. Review these editable assumptions. **Export…** writes that target's armature and animated bones.

The target must have the finger joints you want to export. Citizen's four-finger rig has no pinky chain; choose Human or a five-finger custom rig to retain pinky motion. The preview flags unmapped joints; hover its status text to see the missing hand or finger chains. The original capture keeps those tracks.

First-person preview hides the character's separate head mesh when supported, preventing it from clipping through the camera. Switching to third-person view restores it. This affects only preview visibility.

For native hand capture, **Advanced → First Person → Recording FOV (°)** optionally supplies the video's horizontal lens angle. Leave it blank to size the lens from the hands, which keeps them at first-person distances within arm's reach. Choose **Process again** to apply it; previous captures remain intact. This affects inferred depth and is separate from **Viewmodel FOV**, which only changes preview framing.

Third Person checks whether the recording camera stayed still by following background detail outside the person. When it did, root and foot-contact refinement is applied automatically from the saved predictions, reconstructed floor height is levelled, and the status line says so; **Advanced → Third Person → Restore original capture** reopens the untouched result. A moving or handheld camera has its rotation followed from the same background detail and supplied to the body model, and the result gets the same foot anchoring and level floor; when the background shows the camera only turned in place, as a standing operator's does, travel is anchored too. Camera travel itself is not recovered, so a camera that walks with the subject gives the model's own estimate of distance. Footage with too little background detail to follow stays camera-relative. **Reduce foot drift** keeps predicted stationary ankles and toes anchored on the final target rig. Review the result before export. **In place** removes horizontal travel.

FPS cleanup reduces small wrist-position fluctuations while preserving fast movement and captured finger detail. **Advanced → Contact review** imports prop FBX animation and lets you add, edit or suggest wrist contacts from imported rigid surfaces. Contacts appear beneath the playback slider; right-click an interval to review it or adjust its timing. The contact dialog also edits sliding position keys and optional finger contact points. Review yellow suggestions before confirming. Confirmed contacts constrain the hands, with optional wrist-orientation anchoring for rigid grips; prop armatures appear in preview and export with their animated bones. Hand reconstruction alone does not recover object motion.

The default export uses the preview rig and applied corrections. **Advanced → Export captured skeleton** keeps the reconstruction's skeleton and sample timing. Both export bone positions and rotations. Keep the original video, `.hmotion` and `.hmotion.adjustments.json` files for reversible edits; confidence, observation labels and contact-review data are not FBX animation channels. Applied adjustments are remembered per target and workspace, and changes reuse cached reconstruction.

The preview displays the baked FBX pose, including independent pinky motion and helper bones. Constraints on the model receiving the FBX can change that pose during playback. In particular, Human's stock **CopyPinky** copies ring-finger motion onto the pinky; disable it on your project-owned model when using captured five-finger animation. Shipped engine models are left untouched.

FPS uploads default to **WiLoR for pose reconstruction, with MediaPipe locating the hands**. It was closest to the video for fingers and wrists in every comparison, from head-mounted and outside cameras, and is the slowest: about a third of a second per hand per frame on a recent eight-core CPU, up to a second on older ones, after a 2.56 GB first download. It also keeps reconstructing a hand the detector loses while it is partly hidden in plain view. Use **Advanced → Hand models…** to choose the faster WildHands for head-mounted footage, MobileHand or the lightweight MediaPipe-only option. The status line asks for review when reconstructed hands do not match the hands found in the video. Existing captures stay unchanged; use **Process again** to reconstruct with the selected model. Badges describe processing cost, not accuracy.

## Setup and requirements

All reconstruction runs locally in a separate C# worker; nothing is uploaded. It needs Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download). The first upload builds the worker from the included `InferenceWorker` source (a minute or two, once per library update) and downloads the models it needs, with checksums verified: about 5.5 GB for Third Person, 2.6 GB for First Person with WiLoR (also used for fingers in Third Person), 0.9 GB for WildHands, 8 MB for MediaPipe only. Set `HUMANOID_MOCAP_WORKER` to use a prebuilt worker and `HUMANOID_MOCAP_MODELS` for an existing model folder.

The vision transformers run on the graphics card when there is a DirectX 12 GPU with at least 3 GB of its own memory (AMD, NVIDIA or Intel), and on the processor otherwise. On a Ryzen 7 7800X3D with a Radeon RX 9070, a 28-second Third Person clip takes about a minute (twelve on the processor alone) and a 4-second First Person clip about 18 seconds. Hand detection uses native kernels on either device. The first capture on a new PC prepares the graphics-card models from the downloaded checkpoints, about 1.3 GB each beside the models. Set `HUMANOID_MOCAP_DEVICE=cpu` to force the processor. Processors without native bfloat16 (AVX-512 BF16 or AMX) run the processor path roughly twice as slowly. Start with clips of 5–15 seconds; one job accepts up to 1,800 frames. Cancelling keeps finished frames, and **Retry** resumes. Exported animation playback needs neither the worker nor its models.

## Recording tips

- Good light and a short exposure matter most: motion blur is the commonest cause of bad frames. Avoid digital zoom changes. MP4 (H.264) is the safest format; phone rotation metadata, variable frame rates and dropped frames are honoured.
- **Frame rate and length:** 30 fps is ideal. Faster footage (60 fps, slow motion) is sampled down to about 30 frames per second automatically. A capture covers up to 1,800 frames, about a minute; a longer video captures its first minute, and **Advanced** lets you pick another range.
- **iPhone footage:** iPhones record HEVC (H.265) by default, which Windows decodes only with the **HEVC Video Extensions** from the Microsoft Store. Install that, or set **Settings → Camera → Formats → Most Compatible** to record H.264. An undecodable video is reported immediately, before anything is downloaded.
- **Third Person:** one person, whole body in frame, from head to feet. Use a tripod or a steady surface when you can: a still camera is detected automatically and gets anchored feet and a level floor. If you film handheld, stand in one place and turn to follow the subject with some textured background in view; walking with the camera makes travel distance unreliable. Bystanders in the background are fine; two people of similar size at the start are not.
- **Third Person fingers** are captured only when the hands are large enough in the picture (a forearm of roughly 36 pixels or more). Film closer or at higher resolution if fingers matter; otherwise the hands hold a relaxed pose.
- **First Person:** keep wrists and fingers in view, and start with an open hand. A head- or chest-mounted camera and footage of someone else's hands both work with the default model. Which of the two it is is read from which side of the picture each hand is on; footage filmed facing the performer is placed in front of the character automatically. If that guess is wrong, change **Advanced → First Person → Capture yaw** by 180° and adjust the capture position. With **Recording FOV** blank the lens is sized so the hands sit at typical first-person distances; enter the real horizontal field of view if you know it.
- A hand that is partly hidden is still followed; a hand that leaves the picture or is fully covered is bridged with an eased guess and labelled **inferred gap**. Re-record if an important moment falls inside one.

Published HOT3D hand/object annotations can also be imported by selecting an annotated Aria `.tar` clip through **Advanced → Open motion…**. The C# worker prepares its preview and armature tracks. This separate dataset workflow does not run reconstruction on your footage.

Custom models need a rig and skin weights. Automatic mapping is not perfect; check the
preview before exporting. This library exports animation and armatures; it does not
automatically rig or skin a character. Phone codec support depends on Windows Media Foundation.

Third Person uses GVHMR body reconstruction, with WiLoR supplying finger motion when the hands are large enough in the picture.
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
