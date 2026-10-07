# Changelog

## 2026-10-06
### Breaking
- Code namespaces moved under `HumanoidMocap.Core`: `HumanoidMocap.Cleanup`, `.Dl`, `.Formats` (and `.Formats.Ant/Bvh/Dmx/Fbx/Gltf/Renderware`), `.Inference`, `.Mapping`, `.Maths`, `.Motion`, `.Skeleton`, `.Solve` and `.Target` are now `HumanoidMocap.Core.<same>`. Update your `using` lines.
- `HumanoidMocap.Retargeter`, `RetargetRequest`, `RetargetTargetSpec`, `RetargetResult`, `RetargetBatchResult`, `ClipResult`, `InspectResult`, `MappingReportInfo`, `ResolvedSource`, `BatchOptions`, `SolverKind`, `TargetUpAxis` and `CoreInfo` are now in `HumanoidMocap.Core`.
- Editor namespaces: `HumanoidMocap.Editor` is now `HumanoidMocap.EditorTools` (`HumanoidMocap.Editor.PhoneQr` is `HumanoidMocap.EditorTools.PhoneQr`), the editor inference code in `HumanoidMocap.Inference` is now `HumanoidMocap.EditorTools.Inference` (including `RawHandSample`, formerly `HumanoidMocap.Editor.RawHandSample`), and `HumanoidMocap.MotionBricks` is now `HumanoidMocap.EditorTools.MotionBricks`.
- The shipped target rigs and profiles moved from `Assets/humanoid_mocap/` to `Assets/data/humanoid_mocap/`. Code that loads `humanoid_mocap/target_rig_sbox.json` by path should use `data/humanoid_mocap/target_rig_sbox.json`.
### Changed
- The package follows the workspace layout: retargeting core in `Code/HumanoidMocap/Core`, editor code directly in `Editor/`, the inference worker in `worker/InferenceWorker`, tests in `tests/`. Behaviour is unchanged.
- A source checkout now keeps its downloaded models in `dev/data/models` (previously `models/`). Installed libraries still use `%LOCALAPPDATA%\sbox-humanoid-mocap\models`.
- The worker is rebuilt once after updating a source checkout, because its source files moved.
### Fixed
- Reconstruct with AI finds the restructured Humanoid Retargeter again (it looks its types up under their new `HumanoidRetargeter.Core` names).
