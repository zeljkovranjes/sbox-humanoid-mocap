# Humanoid Mocap

A video-to-animation tool for the [s&box](https://sbox.game) editor.

- First Person and Third Person workspaces with shared import, mapping and export.
- Phone video uploads from Photos or Gallery: scan once and upload for one hour.
- Experimental local hand and finger reconstruction in C#, with estimated arm IK.
- s&box Human, classic Citizen and custom VMDL, FBX, GLB or glTF targets.
- Editable shoulder and elbow placement, viewmodel FOV and separate motion cleanup controls.
- Synchronized video and target-rig preview, with reversible motion edits.
- Compiled VMDL animation export that plays without reconstruction models.

Add the library to your s&box project and open **View → Humanoid Mocap**.
Keep only one copy of the library installed. Humanoid Retargeter can remain installed alongside it.

1. Click **Import Video…**, or **Receive from Phone** and scan the QR code on the same Wi-Fi network.
2. Choose **First Person** or **Third Person**, select a target rig and set the video range.
3. Click **Reconstruct hands**. The verified 7.8 MB hand model downloads on first use; footage stays on your PC.
4. Review the source video, bone mapping and target preview. Correct handedness and estimated arm placement as needed.
5. Apply cleanup, choose an output folder and filename, or select an existing VMDL.
6. Click **Convert All** and use the compiled sequences in your model or animgraph.

Keep the phone's upload page and the editor receiver open to send more videos without
scanning again. Pairing expires one hour after creation; choose **New one-hour pairing**
and rescan to reconnect. **Disconnect phones** or closing the receiver ends pairing
immediately. Uploads are local, limited to 2 GiB per file, and preserve previous files.
Guest Wi-Fi isolation or a firewall can block the connection.

This is an experimental development build. The available hand backend uses MediaPipe
landmarks; ACE-Ego-Hand is not implemented. The [C# body worker](InferenceWorker/README.md)
can reconstruct raw camera-relative GVHMR motion. Direct editor body capture and final
source contact correction are still being integrated.
Wrist depth, hidden elbows and shoulders are estimated. There is no calibrated world-space
recovery, automatic prop tracking or validated multi-camera fusion. Review tracking loss,
handedness, finger motion and contacts before exporting.

Custom models need a rig and skin weights. Automatic mapping is not perfect; check the
preview before exporting. Windows Media Foundation decodes footage directly from C#;
phone codec availability depends on Windows. A small browser page handles phone uploads.
There is no Python dependency or Python inference bridge.

See [third-party notices](THIRD_PARTY_NOTICES.md).

Package ident: `notpointless.chomnr_humanoid_mocap`
