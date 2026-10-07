# Changelog

## 2026-10-07
### Fixed
- Legs no longer snap in body captures: a thigh, shin or foot that jumped in one frame (legs crossing, a kick, getting up off the floor) now moves smoothly over the frames around it. Planted feet stay where they were while the leg is smoothed.
- Feet no longer jump back into place when a planted foot lifts off or lands; the foot lock fades in and out along the captured path.
- Kip-ups and similar moves are no longer read as sitting on the floor, which pulled the hips down and folded the legs through themselves.
- Sitting down and getting up are eased over a third of a second instead of switching in a few frames.

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
