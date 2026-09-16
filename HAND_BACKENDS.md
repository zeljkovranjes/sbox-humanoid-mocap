# Hand backend options

MediaPipe remains the lightweight option. ACE-Ego-Hand is optional and must not become
a prerequisite for video import, hand reconstruction, preview or animation playback.
Open **Advanced → Hand models…** to select MediaPipe, WildHands or WiLoR for the next
FPS upload. Each selection prepares its model automatically on first use. Existing
reconstruction is preserved. ACE remains an informational optional entry and is never
selected, downloaded or loaded automatically. The badges are **Light** (MediaPipe), **Medium*** (WildHands), **Heavy*** (WiLoR)
and **Very heavy*** (ACE). An asterisk means the processing weight is an architectural
estimate, not a local memory measurement or accuracy rating.

| Backend | Status in Humanoid Mocap | Intended use and limits |
| --- | --- | --- |
| MediaPipe | Available, experimental C# implementation; 7.8 MB model | Lightweight hand landmarks and finger motion. Wrist depth and arm placement are estimated; this is not calibrated world tracking. |
| WildHands | Available, experimental C# native CPU port; 855 MB checkpoint | Egocentric wrist/finger rotations and shape. Uses MediaPipe hand crops and estimated camera intrinsics; review wrist depth and visibility. |
| WiLoR | Available, experimental C# native CPU port; 2.56 GB checkpoint | Wrist/finger rotations and shape with a larger transformer. Uses MediaPipe hand crops rather than the original detector. |
| ACE-Ego-Hand | Optional future backend; not integrated | Offline bimanual reconstruction through occlusion using the much larger Wan video backbone. Downloads and model loading must be opt-in. |

WildHands provides the intermediate processing option between MediaPipe and larger models.
Its authors report a model about ten times smaller than HaMeR and better results on
three of six compared metrics. That comparison is **against HaMeR, not ACE-Ego-Hand**.
The released demo was inspected at commit
`f99dfea0d1fce970aed2d31d1018eda280e05f47`: two ResNet-50 encoders, camera-conditioned
features, separate iterative hand heads, and MANO decoding. Its official
`fetch_models.sh` links the trained WildHands checkpoint; it is not bundled here.
See the [paper and project](https://ap229997.github.io/projects/hands/) and
[pinned demo source](https://github.com/ap229997/hands/tree/f99dfea0d1fce970aed2d31d1018eda280e05f47).

Fast-HaMeR is another research candidate. The authors report roughly 35% of HaMeR's
model size, 1.5 times faster inference and a 0.4 mm accuracy difference on HO3D-v2.
Those numbers do not establish comparable egocentric video or occlusion performance.
The inspected download script fetches the original HaMeR demo assets; a reproducible
download for the distilled student checkpoint still needs to be verified.
See [Fast-HaMeR](https://github.com/hunainahmedj/Fast-HaMeR).

No lightweight replacement has been verified here to match ACE's accuracy. The
[ACE paper](https://arxiv.org/html/2608.20308v1) reports substantial differences during
occlusion and out-of-view intervals. Its HaWoR comparison disables SLAM and motion
infilling, so that row must not be interpreted as a test of the complete HaWoR pipeline.
See also the official [ACE repository](https://github.com/ggxxii/ACE-Ego-Hand) and
[WiLoR repository](https://github.com/rolpotamias/WiLoR).

These integrations use C# and native inference libraries without a Python bridge.
MediaPipe, WildHands and WiLoR have produced real reconstructed motion, target-rig
previews and moving FBX animations imported by s&box. The native hand models share the
7.8 MB MediaPipe crop detector. Missing detections stay explicit; the models do not
recover prop tracks or calibrated camera motion. See the [worker instructions](InferenceWorker/README.md)
for pinned setup, cache/resume behavior and measured processing costs. Functional
verification does not establish parity with the original demos or ground-truth 3D accuracy.

All hand backends now keep a hand-and-wrist source skeleton. MediaPipe fits landmark
rotations to fixed canonical finger lengths and assumes wrist depth on an image plane.
Estimated shoulders and elbows are added on the selected target through IK. Captured-skeleton
export contains the hand source; ordinary export contains the preview rig and baked movement.
Source observations are retained separately, so target and correction changes do not rerun inference.
