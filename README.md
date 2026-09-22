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
