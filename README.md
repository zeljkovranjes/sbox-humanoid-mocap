# Humanoid Mocap

An editor tool that reconstructs body, hand and finger motion from a video on your own PC and retargets it onto the s&box Human, the classic Citizen or a custom humanoid model. Open **View → Humanoid Mocap**, upload a video from disk or your phone, review the capture beside the source and export it as FBX for ModelDoc. Inference runs on the CPU in a separate worker program that is downloaded with its models (several GB) the first time you capture. Windows x64 only.

## Requirements

- Windows x64. The worker runs on the CPU; no GPU is needed.
- Internet the first time you capture: the inference worker (about 175 MB) and the models (several GB) download once.
- Optional: [Humanoid Retargeter](https://github.com/zeljkovranjes/humanoid-retargeter) (`notpointless.chomnr_humanoid_retargeter`) for **Reconstruct with AI** (NVIDIA MotionBricks); its model (about 730 MB) downloads once on request.
- Only when building the worker from source: the .NET 10 SDK.

## Install

Search for **Humanoid Mocap** in the s&box library manager, or add `notpointless.chomnr_humanoid_mocap`. Keep only one copy of the library installed; Humanoid Retargeter can stay installed alongside it.

## Quick start

### In the editor

1. Open **View → Humanoid Mocap** and choose **First Person** or **Third Person**.
2. Click **Upload video…**, drag in a video, or choose **Upload from phone** (Photos or Gallery, one-hour QR pairing).
3. Processing starts automatically; let the local reconstruction finish.
4. Play the source video beside the animation and review the captured movement.
5. Switch **Human / Citizen** at the preview's top right. Use **Advanced** for custom VMDL, FBX, GLB or glTF targets, corrections and root-motion controls.
6. Click **Export…** and use the FBX as an animation source in ModelDoc.

### In code

The tool is used from the editor. The retargeting core it is built on is public and engine-free (bytes in, DMX and VMDL text out):

```csharp
using HumanoidMocap.Core;

var target = RetargetTargetSpec.SboxDefault( rigJson ); // text of Assets/data/humanoid_mocap/target_rig_sbox.json
var result = Retargeter.Convert( new RetargetRequest { SourceData = fbxBytes, SourceFileName = "walk.fbx" }, target );
```

## Options

| Option | Default | What it does |
|---|---|---|
| `HUMANOID_MOCAP_WORKER` (environment variable) | not set | Path to a prebuilt `HumanoidMocap.Worker.exe` to use instead of downloading or building one. |
| `HUMANOID_MOCAP_MODELS` (environment variable) | `%LOCALAPPDATA%\sbox-humanoid-mocap\models` | Folder the worker downloads its models into and loads them from. A source checkout uses `dev\data\models` when that folder exists. |

## How it works

The editor window copies the video into the project's temporary files and hands a job file to a separate C# worker process (`worker/InferenceWorker`), so heavy inference never runs inside the editor. The worker detects the person, estimates body pose (GVHMR) and hands (MediaPipe, MobileHand, WildHands or WiLoR), and writes a motion document. An installed library downloads the prebuilt worker from the GitHub release; a source checkout builds it with `dotnet publish` and rebuilds it whenever its sources change. The engine-free core in `Code/HumanoidMocap/Core` then retargets the motion onto the chosen skeleton, cleans contacts and exports FBX. Depth from Record3D LiDAR videos, `.r3d` files and iPhone Cinematic mode is used to place the body and hands by measured distance.

## Multiplayer

Editor-only tool. Nothing runs in a game session or is networked; exported animations are ordinary model animations.

## Limitations

- Reconstruction is experimental: foot drift and occasional pose jumps remain, and accuracy is not measured in 3D.
- FBX export contains the armature and animated bones only, no meshes or skin weights.
- Windows only. Videos Windows cannot decode (for example iPhone HEVC) are converted to H.264 first, which takes extra time.
- Processing is slow on long clips (minutes per few hundred frames) and needs several GB of RAM.
- Reconstruct with AI needs Humanoid Retargeter installed.

## Development

- `sbox-check` (structure, docs, and `dev\HumanoidMocap.Core.csproj`, which compiles `Code/HumanoidMocap/Core` as plain .NET).
- Code and Editor against the engine DLLs: `dotnet build dev\editor-rig\EditorUiCompileCheck.csproj` (it also builds `CodeCompileCheck.csproj`). The s&box whitelist is only checked by the editor itself.
- Worker: `dotnet build worker\InferenceWorker`.
- Tests: `dotnet test tests\PosedLocomotion` and `dotnet test tests\Mocap.Worker.Tests`. The worker tests need the local data in `dev\data\samples` and `dev\verification`.
- Local tools and benchmarks are in `dev\` (see `dev\README.md`); large data lives in `dev\data`, research notes in `docs\research`.

## License

No license file yet. Third-party code and model terms are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); some upstream models (GVHMR, SMPL-X) are for non-commercial research use only.
