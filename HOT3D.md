# Importing HOT3D clips

The C# worker can import **annotated HOT3D-Clips Aria RGB archives**. This uses
published UmeTrack hand and rigid-object tracks, not this library's neural predictions.
It is a dataset import workflow; normal phone/video uploads still use the selected
reconstruction model. Full VRS sequences, Quest cameras, ARCTIC and HOI4D are not
supported by this importer.

From the repository root, run:

```powershell
dotnet run --project InferenceWorker -- import-hot3d hot3d-job.json
```

Use absolute paths in `hot3d-job.json`:

```json
{
  "Archive": "D:/Mocap/clip-001849.tar",
  "Output": "D:/Mocap/imports"
}
```

Open the reported `annotations.hmotion` using **Advanced → Open motion…**. Choose
Human or Citizen, review the synchronized video, and export the target armature.
No MANO files or neural checkpoints are needed for this import. The existing worker
setup supplies the native OpenCV dependency and Windows H.264 encoder.

The importer creates a 640×640 pinhole review video from the archive's per-frame
Fisheye624 calibration. Its fixed virtual view rotates RGB upright and tilts 30°
toward the table, with a 120° field of view. This is authored framing, not recovered
camera orientation. Hand and object tracks use the same camera-relative transform.
The original frame timestamps are retained relative to the first frame; the receipt
records the small difference from the encoded review-video timestamps.

Outputs include the hand skeleton, finger rotations, rigid-object root tracks and
camera intrinsics. Object tracks are labeled `Tracked`. Hand evidence is labeled
`Reconstructed` for the dataset's supplied fitted annotations; the backend and
diagnostics explicitly identify the import. Missing hand annotations remain unobserved.
Confidence is left unset. Modeled visibility, original calibration, world transforms,
hand profiles and absolute timestamps are retained separately in `source-annotations.json`.

Objects are previewed/exported as armatures. No object meshes or contact surfaces are
included, and no grips are asserted. Incomplete object tracks are omitted from active
export tracks with a diagnostic; their original annotations remain available. Duplicate
object identities are rejected instead of being associated by array order. Other cameras
are not fused, and this does not enable multi-camera capture or world-motion recovery.

The original archive is read only. Completed imports are reused after checksum
verification; changed outputs cause an error instead of being overwritten. Ctrl+C or
the worker's `cancel` stdin command cancels an import. Run the same command to restart
an interrupted import; video encoding restarts from the beginning. Work is limited to
2–1,800 consecutive frames, a 512 MiB archive, 256 MiB of selected archive data, bounded
image dimensions and at most 64 object tracks. Clips with timing gaps that cannot be
represented by the review video are rejected without resampling annotations.

The local verification clip is [HOT3D `001849`](https://huggingface.co/datasets/bop-benchmark/hot3d/resolve/30fe9674782f32e1e5edba98476b6ff4300132c5/train_aria/clip-001849.tar).
Its source SHA-256 is
`C3BFD5B26B1C80A4A8C038B1D26EF423464DE0B12EE42C6ADC67F0485DBD3775`.
The importer and independent landmark reader agree on both hands' shared joint
positions. This checks format conversion, not this library's reconstruction accuracy.
The 150-frame clip passed Human and Citizen preview, armature-only FBX export and
compiled s&box playback. All six object roots were present in Citizen's preview and
export, with matching sampled positions. Synchronized frames were visually reviewed.
Target proportions still change hand-to-object contact; no automatic grip quality is claimed.

On the tested Ryzen 7 7800X3D Windows PC, this 150-frame import took 13.35 seconds
and peaked at approximately 490 MiB worker RAM. Decoded review timestamps differed
from annotations by at most 1.12 ms. These are one-clip measurements, not minimum
hardware requirements; no neural model or GPU inference was used.
