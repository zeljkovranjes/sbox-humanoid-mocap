# Humanoid Mocap

A video-to-animation tool for the [s&box](https://sbox.game) editor.

- First Person and Third Person workspaces with automatic processing after upload.
- Phone video uploads from Photos or Gallery: scan once and upload for one hour.
- Experimental local hand and finger reconstruction in C#, with estimated arm IK.
- s&box Human, classic Citizen and custom VMDL, FBX, GLB or glTF targets.
- Optional corrections and target selection under Advanced.
- Synchronized video and target-rig preview, with reversible motion edits.
- FBX export with the armature and animated bones, without meshes or skin weights.

Add the library to your s&box project and open **View → Humanoid Mocap**.
Keep only one copy of the library installed. Humanoid Retargeter can remain installed alongside it.

1. Choose **First Person** or **Third Person**.
2. Click **Upload video…**, or **Upload from phone** and scan the QR code on the same Wi-Fi network.
3. Processing starts automatically. Review the animation when it finishes.
4. Click **Export…** to save the armature and bone movement as FBX.

The compact window opens at 960 × 640 and can shrink to 760 × 480. Drag a video into
the import area or use either upload button. **Advanced** opens a separate adjustments window.
Above the animation, **First person / Third person** switches only the preview camera;
an FPS capture stays an FPS capture in either view. Drag in the preview to look around
or orbit, and use the reset-camera button to restore framing. The first-person camera
frames the captured hands from eye height without changing their animation.

The default export uses the preview rig, corrections and baked bone animation.
Edits under Advanced affect export after they have been applied to the preview.
**Advanced → Export captured skeleton** keeps the reconstruction's skeleton and sample timing. Both contain bone
positions and rotations. Keep the `.hmotion` document for confidence, visibility,
contact review and reversible edits; those details are not animation channels in FBX.
Applied adjustments save beside the motion as `.hmotion.adjustments.json`, with
separate settings per target rig and workspace. Keep that file and the original
reconstruction so reopening can rebuild cleanup without filtering it twice.
The exported FBX can be added as an animation source in s&box ModelDoc.

Keep the phone's upload page and the editor receiver open to send more videos without
scanning again. Pairing expires one hour after creation; choose **New one-hour pairing**
and rescan to reconnect. **Disconnect phones** or closing the receiver ends pairing
immediately. Uploads are local, limited to 2 GiB per file, and preserve previous files.
Guest Wi-Fi isolation or a firewall can block the connection.

This is an experimental development build. MediaPipe is the default lightweight hand
backend; WildHands and WiLoR are optional C# native CPU choices under Advanced. ACE-Ego-Hand is an optional planned backend, not a requirement; its C#
integration is not implemented. See [hand backend options](HAND_BACKENDS.md) for the
lighter reconstruction candidates and their current status. The [C# body worker](InferenceWorker/README.md)
provides Third Person camera-relative GVHMR motion. Initial worker setup needs the
included C# worker source and .NET 10 SDK, or a prebuilt worker. Models download locally
on first use. The upstream final source contact correction is not included; target foot
correction is applied during retargeting.
Wrist depth, hidden elbows and shoulders are estimated. There is no calibrated world-space
recovery, automatic prop tracking or validated multi-camera fusion. Review tracking loss,
handedness, finger motion and contacts before exporting.
The main status flags missing or mostly untracked hands; Advanced reports observed
frames and unresolved gaps. FPS shoulders and clavicles use the target rig's proportions;
the hand backend does not measure your shoulder height or width. For body clips, **In place**
removes horizontal travel; it does not turn camera-relative reconstruction into world tracking.

Custom models need a rig and skin weights. Automatic mapping is not perfect; check the
preview before exporting. Windows Media Foundation decodes footage directly from C#;
phone codec availability depends on Windows. A small browser page handles phone uploads.
There is no Python dependency or Python inference bridge.

See [third-party notices](THIRD_PARTY_NOTICES.md).
See [capture instructions](CAPTURE.md) for recording and reviewing footage.

Package ident: `notpointless.chomnr_humanoid_mocap`
