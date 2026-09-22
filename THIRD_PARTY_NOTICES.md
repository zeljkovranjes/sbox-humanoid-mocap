# Source provenance

The C# HOT3D-Clips importer adapts UmeTrack forward kinematics and Fisheye624
projection from Meta's [hand_tracking_toolkit](https://github.com/facebookresearch/hand_tracking_toolkit/tree/950d64f7e8d2ba1fd38cd2ceede6608a8fa7f5aa)
at `950d64f7e8d2ba1fd38cd2ceede6608a8fa7f5aa`, under Apache 2.0.
Copyright (c) Meta Platforms, Inc. and affiliates. The license is retained in
`InferenceWorker/Hot3d.LICENSE`. Archive fields follow the
[HOT3D-Clips format](https://github.com/facebookresearch/hot3d/blob/146b34afef8c1a32adeef7e981c070109f225c87/hot3d/clips/README.md)
at `146b34afef8c1a32adeef7e981c070109f225c87`. Source clips, hand profiles and object
annotations remain separate downloads; this repository does not bundle them.

The C# person detector adapts preprocessing, anchor layout and output decoding from
[OpenCV Zoo's MediaPipe person detector](https://github.com/opencv/opencv_zoo/tree/47534e27c9851bb1128ccc0102f1145e27f23f98/models/person_detection_mediapipe)
at `47534e27c9851bb1128ccc0102f1145e27f23f98`. It runs the published ONNX model through
OpenCV DNN on CPU. The Apache 2.0 license is retained in
`InferenceWorker/PersonDetector.LICENSE`; weights are downloaded separately and verified
against the upstream Git LFS SHA-256. Subject association and conservative gap handling
are this library's implementation, not GVHMR's original YOLO tracker.

The C# MobileHand network, 23-angle pose basis and MANO buffer conventions follow
[gmntu/mobilehand](https://github.com/gmntu/mobilehand) at
`51c112364013b803c38955b55a1572b0d402894c`, by Lim Guan Ming, Jatesiktat Prayook
and Ang Wei Tech (ICONIP 2020). The checkout has no top-level license file.
Its MobileNetV3 source credits d-li14/mobilenetv3.pytorch and rwightman/gen-efficientnet-pytorch.
The pretrained FreiHAND checkpoint is downloaded separately from that pinned revision;
neither its weights nor MANO buffers are committed. The legacy data reader follows
PyTorch 1.5.1's documented serialization structure and never executes pickle callables.

The core retargeting formats, skeleton/mapping/cleanup/export implementation, editor widgets, settings and verification patterns were copied and adapted from the user's local `humanoid-retargeter` at commit `69ccfcd76f6aecca443f055be912ac3831a64003`. Namespaces, registrations, resource paths and package identifiers were changed to avoid collisions. The original repository was not modified.

The editor uses exposed s&box APIs and the native Group, Button, Theme, SegmentedControl and VideoWidget conventions inspected in Facepunch/sbox-public at `95eb0e4c1409d684de8bec636732c0548b7733fe`. It does not package engine-internal assemblies or pretend internal APIs are public.

`MocapVideoWidget` adapts the aspect-fit Pixmap painting convention in `game/addons/tools/Code/Widgets/VideoWidget.cs` from that revision. Timestamped video decoding uses direct C# Windows Media Foundation calls; presentation uses the exposed s&box Pixmap APIs and the same dark background as Humanoid Rigger's viewport.

The hand model browser uses the exposed ListView, Splitter and ControlSheet components. Its compact row selection and hover painting are adapted from `game/addons/tools/Code/Inspectors/ModelInspector.cs`; the inspector organization follows `game/editor/ActionGraph/Code/Properties.cs` in the same sbox-public revision. The retargeter's shared pill painter supplies the processing-weight badges.

The compact floating window and centered video import area reuse the UI conventions of the user's local `sbox-humanoid-rigger` at `6e7304bf1d5941037f50496eccf0a7e03ddf9e10`. Its rigging or skin-weight implementation is not included.

The C# QR generator in `Editor/HumanoidMocap/PhoneQr` comes from [manuelbl/QrCodeGenerator](https://github.com/manuelbl/QrCodeGenerator), v2.0.7, commit `f414417201fa7c639463730fe41099528f8026cb`. Original copyright headers and the adjacent MIT license are retained; only the namespace is adapted.

The experimental hand model is downloaded separately from Google's official MediaPipe model storage. Its URL, SHA-256 and byte count are pinned in the model preparation code. It is not ACE-Ego-Hand, and the managed interpreter is not an official MediaPipe implementation.

The MediaPipe tracked-hand crop rotation, landmark projection, image sampling and video association follow the upstream hand-landmarker graph and CPU image converter conventions. The palm-box/keypoint merge in `PalmDetectionFilter.cs` is adapted from MediaPipe's weighted `NonMaxSuppressionCalculator` and hand-detector graph at `e053c10c0e6c0b30486f43cb349d6daa0ff57d60`. The Apache 2.0 license is retained in `Editor/HumanoidMocap/Inference/MediaPipe.LICENSE`.

The C# WildHands network, input crops and camera-conditioned hand heads follow [ap229997/hands](https://github.com/ap229997/hands) at `f99dfea0d1fce970aed2d31d1018eda280e05f47`. The pinned demo checkout did not contain a top-level license file. Its checkpoint is downloaded separately from the upstream download script's URL; it is not bundled.

The C# WiLoR architecture, iterative decoder and crop conventions follow [rolpotamias/WiLoR](https://github.com/rolpotamias/WiLoR) at `fcb911312a38fa8badd30d9656a167485d61b8f9`. The original terms are retained in `InferenceWorker/WiLoR.LICENSE`. Its checkpoint is pinned to Hugging Face revision `99fe3d7acff8104ecca1055df7467709506c2fa6`. The shared C# MANO decoder reads data buffers from these local checkpoints; model data is not committed. Native hand ports use MediaPipe crops and do not claim parity with the upstream detection pipelines.

ACE-Ego-Hand, GVHMR and other research checkouts, MANO data, footage and checkpoints are local development inputs, not redistributed package assets. Those inputs and development records stay outside this repository.

The C# GVHMR temporal network, output statistics, decoder, SMPL-X skeleton calculations and contact processing in `Editor/HumanoidMocap/Inference`, and the HMR2/ViTPose architecture and input/output processing in `InferenceWorker`, are adapted from [zju3dv/GVHMR](https://github.com/zju3dv/GVHMR) at `ee960bb6e2ea2d381aa97f08e9b71ef320b624b1`. The upstream research/noncommercial terms are retained in `Gvhmr.LICENSE`. Checkpoints and SMPL-X model data are downloaded separately and are not committed. Full video-backend parity and final contact correction remain unverified.

The C# worker restores [TorchSharp](https://github.com/dotnet/TorchSharp) 0.107.0, native LibTorch CPU 2.10.0, [OpenCvSharp](https://github.com/shimat/opencvsharp) managed/Windows runtime 4.13.0.20260627, and [ONNX Runtime](https://github.com/microsoft/onnxruntime) with [DirectML](https://github.com/microsoft/DirectML) 1.24.4 (MIT) from NuGet. The graphics-card graph is assembled on the user's machine from the downloaded checkpoints and cached beside them; no converted weights are distributed. Their package licenses and native third-party notices apply. Native binaries are not copied into this source repository.

Windows Media Foundation API signatures and GUIDs follow Windows SDK 10.0.26100.0. The source-reader implementation calls the OS directly; see [Microsoft's Source Reader documentation](https://learn.microsoft.com/en-us/windows/win32/medfound/processing-media-data-with-the-source-reader).

`Code/HumanoidMocap/Motion/MocapSmooth.cs` ports the zero-phase Butterworth and Gaussian smoothing of [Dylanyz/MocapSmooth](https://github.com/Dylanyz/MocapSmooth) (`Tools/smooth_core.py`), Apache 2.0, which reproduces Rokoko Studio's filter.
