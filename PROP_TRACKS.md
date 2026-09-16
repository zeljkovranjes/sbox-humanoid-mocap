# Prop tracks

Open **Advanced → Contact review → Import prop FBX…** after reconstructing a First Person
clip. Choose a rigged prop's animation, select its take, and set the first frame's video
time, position, rotation and scale. FBX units and axes are converted before these placement
adjustments. Camera coordinates are metres, X right, Y up and −Z in front of the camera.
Check the alignment against the source video; the importer does not determine calibration.

Import creates a separate capture beside the current motion file, preserving the original
reconstruction, existing contacts and target adjustments. The prop take must cover the
entire capture range; missing motion is not extrapolated. Import one root per file, with
articulated parts below it. Import independently moving detachable objects separately.
Input FBX is limited to 64 MiB and animation sampling to four million bone transforms.
The main **Cancel** button cancels sampling. Rigging/skin weights are not modified or
exported. Eligible rigid mesh triangles are retained only as contact surfaces.

Use **Add contact…** to choose a wrist, prop bone, video interval and object-local anchor.
**Use wrist at interval midpoint** places a manual anchor from the nearest captured sample;
it refuses missing wrist or object observations. Save, seek to the interval, and confirm
only after reviewing the result. The pencil button edits an existing interval/anchor and
returns it to Suggested. These edits are saved in the motion's adjustment sidecar.
Contact intervals also appear below the main playback slider, in separate left/right
hand lanes: yellow with `?` means Suggested, green with `✓` means Confirmed, and gray
with `×` means Disabled. Click to seek, double-click a single interval to edit it, or
right-click for editing, confirmation, disabling and **Start/End at playhead**. Overlapping
intervals each get their own entries in the menu. Timing edits return the contact to
Suggested and rebuild from preserved observations; review again before confirming.
The strip is hidden when no contacts exist. Timing edits cannot remove existing sliding
target keys; the editor reports that conflict rather than discarding the keys.
An edited interval must contain all existing sliding keys.

Enable **Sliding contact** in the contact dialog to edit a moving wrist target. Choose
**New key**, enter its video time and object-local position, then **Add key**. **Use
playhead** fills the time; **Sample captured wrist** fills the nearest observed wrist's
position relative to the selected prop bone. Sampling is a placement aid, not automatic
sliding detection. Select an existing key to update its time/position or remove it.
Use **Update key** before saving; saving reports unapplied field edits rather than discarding them.
At least two keys are required, with distinct times inside the contact interval. Targets
interpolate between keys and hold outside their first/last key within the interval.

Turning sliding off retains the keys and uses the fixed anchor above. Remove all draft
keys before changing the hand or prop bone, since their coordinates belong to that binding.
Cancel discards the dialog's edits. **Save suggestion** stores them in the adjustment
sidecar and requires review again, including when the original contact was confirmed.
The editor supports up to 4,096 manually added keys per contact.

For a rigid grip, enable **Hold wrist orientation relative to prop**, then use
**Use wrist at interval midpoint** to capture the orientation anchor as well. Save and
confirm after review. The wrist follows the chosen object's articulated bone rotation;
local finger articulation remains captured unless explicitly constrained below. This can reduce rotational slip when an
aligned prop track is available. Leave it off for a hand turning freely against a prop.
Switching the wrist or prop bone requires placing the orientation anchor again. Clearing
the option and saving restores captured wrist rotation. Existing contacts keep their
previous position-only behavior.

**Finger contact points…** in the contact dialog adds optional corrections on the selected
target rig. Choose a finger and an editable distal-bone-local point in metres. **Use bone
endpoint** fills the rig's endpoint metadata where available; it is a placement guide,
not a measured fingertip pad or skin surface. Custom rigs without this metadata need an
explicit point. **Use preview point** places an object-local target from the baked preview
at the selected video time. This is manual authoring, not inferred finger contact.

Apply the point to the contact draft, then save the parent contact. Pink preview markers
show finger points; yellow target markers need review and turn green after confirmation.
The connecting line shows residual error. Inspect them against the source video and mesh.
Each confirmed point applies a bounded hinge correction to its proximal, middle and distal
joints after arm IK. The default limit is 25° per joint relative to the captured target pose,
editable up to 45°. This is a correction bound, not a measured anatomical joint limit.
Bone lengths, wrist pose and unselected fingers stay unchanged. Contact fades apply before
and after the interval; no smoothing follows the constraints.

Finger targets move with their prop bone. With a sliding wrist contact, they also follow
the change in its object-local key position from the point's placement time. Targets are
stored for the selected rig geometry, mapping and output workspace. Switching to another
rig keeps those entries but does not apply them; author and review points for that target
separately. Contact review shows how many entries match the current target. Remove one
through the finger dialog or use **Clear points** to remove all target entries from the
draft. Cancel restores the saved contact. A contact supports up to ten target-specific
finger entries; large correction jobs report a work-budget error instead of running unbounded.

The point solver can reduce a local gap, but does not model the hand's skin, solve all
finger collisions, guarantee a correct grip, or recover missing finger observations.
It may reach its correction bound and leave a gap. It does not move the authoritative prop.

You can also open a prepared `.hmotion` through **Advanced → Open motion…**. Neither path
automatically tracks props from video or reconstructs an object's geometry.

**Suggest contacts** is available when the imported prop includes contact surfaces.
Include a triangulated, low-poly mesh in the FBX: either parent a rigid mesh to an
animated bone, or weight every triangle's three vertices fully to the same bone.
Mixed weights, nontriangular faces, additive skin, blend shapes and animated, mirrored or nonuniform bone scale
are omitted rather than approximated. The import diagnostics report skipped faces.
Contact geometry is limited to 100,000 vertices and 100,000 triangles across the capture.
It is used for proximity tests; preview and exported FBX still contain prop armatures.
Rigid skin bind conversion uses the transform relationship documented in Autodesk's
[FBX SDK example](https://help.autodesk.com/cloudhelp/2020/ENU/FBX-API-Reference/cpp_ref/_view_scene_2_draw_scene_8cxx-example.html),
with geometric transforms and fixed uniform bind scale included.

Suggestions require an observed wrist and at least three observed finger chains.
Fixed template metacarpals may connect those observations, but remain labeled `Authored`;
they cannot count as observed fingers. Older MediaPipe captures with incorrect missing
metacarpal labels require **Advanced → Process again**, which reuses cached observations
and preserves the old motion file. Then reopen/import the aligned prop track as needed.
The solver tests palm-center proximity to actual triangles, geometric finger bend, relative
motion, persistence and release hysteresis. Missing observations and gaps over 0.1 s break
intervals. Near-equal distances to different objects/parts are left unconstrained and
reported as ambiguous. Existing intervals, including disabled ones, are preserved.
The search is cancellable and has a bounded work budget; it does not move any object.

New intervals stay yellow until confirmed. Their object-local wrist anchors retain the
initial wrist offset instead of placing the wrist itself on the prop surface. These are
heuristics, not calibrated confidence or proof of a grip. They may miss open-handed
contacts, sliding grasps or inaccurate hand/prop alignment. Contact surfaces are not a
hand mesh, and suggestions do not solve finger penetration or choose orientation anchors.

Use the First Person workspace with camera-relative hand capture. Add `objects` and
`contacts` to the document. Each object needs a unique `id`, explicit `source`
(`Tracked`, `ImportedAnimation`, `CalibratedMarker` or `Manual`), `space`, `bones` and
`frames`. A source label records provenance; it does not validate a tracker or marker setup.
The optional `modelPath` is metadata; this implementation renders the prop armature, not its mesh.

Object bones use the same local-transform schema as the captured skeleton: metres,
right-handed X-right/Y-up coordinates, XYZW unit quaternions, unique names and parents
before children. Each object has one root. Articulated parts are children of that root.
Object frames use original video timestamps, with positions, rotations and evidence for
every bone. Use `Authored` evidence for manually animated channels, `Unobserved` for
missing observations, and preserve available backend confidence without inventing it.
Do not use assumed world tracks as camera-relative tracks without alignment.

`parentObject` optionally identifies an earlier object whose root parents this object's
root. An independently moving detachable object uses its own root track and no parent.
Changing attachment ownership over time is not implemented; provide the resulting
independent trajectory for a detachment or reload instead.

A contact binds a captured wrist bone to an object bone. Empty `objectBone` selects the
object root. For example, adapt the names and times below to your actual tracks:

```json
{
  "bone": "HandR",
  "object": "prop",
  "objectBone": "grip",
  "start": 1.0,
  "end": 2.5,
  "localTarget": [0.0, 0.0, 0.0],
  "sliding": false,
  "review": "Suggested",
  "reason": "Imported grip interval; inspect against the video."
}
```

**Contact review** can seek to the middle of an interval, confirm it or disable it.
Suggested intervals remain yellow and do not constrain the wrist. Confirmation applies
an 80 ms smooth activation/release. The target arm solver preserves captured wrist orientation
unless a reviewed orientation anchor is set, and preserves unconstrained finger articulation and limb lengths.
Unreachable targets leave a gap rather than
stretching an arm. Object tracks remain authoritative when both hands touch the same prop.
Overlapping contacts blend their goals independently of list order.

The optional `localRotation` field is an XYZW unit quaternion giving the captured wrist's
orientation relative to the object bone. Omit it or set it to null for a position-only
contact. Orientation constraints use the same smooth activation/release and suspend
when the prop is unavailable. They do not rotate the prop to follow the hand. During
sliding contacts, position keys may move while this orientation stays fixed relative to
the object; animated orientation keys are not currently supported.

For sliding, set `sliding` to true and supply at least two `targetKeys`, each containing
`time` and an object-bone-local `position`. Key times must increase within the interval.
Targets interpolate between keys and hold at their first/last key inside the interval.
When editing contact data directly in a prepared motion file, delete or move its adjustment
sidecar to avoid restoring an earlier review state.

Cyan prop armatures follow the same camera placement as the hands. Their root and part
tracks are included in the bone-only FBX as `hm_prop_<object index>_<bone name>`.
Exports flatten inter-object parenting into independent animated roots; the `.hmotion`
retains original hierarchy/provenance. Missing object observations are hidden in preview
and suspend the affected wrist constraint. Export requires complete object tracks over
the selected motion range; it reports gaps instead of dropping the objects silently.

Target prop export requires First Person hand capture with root-motion removal off.
Captured-skeleton export retains objects in source coordinates. Body-prop constraints,
full palm/skin collision constraints and ContactOpt refinement are not integrated. Optional
authored finger-point constraints use the limited geometric solver described above.
Surface-based wrist suggestions require imported geometry and user review; they are not
an object tracker.

Verification used real MediaPipe hand reconstruction plus an explicitly authored moving
four-bone test prop. Both hands followed their targets, the native preview drew the prop,
and the combined 98-bone FBX compiled and played in s&box. This is a functional constraint
test, not evidence of automatically recovered grips or accurate 3D prop motion.

The editor import path was also exercised with that authored prop exported as FBX and
the original 121-frame MediaPipe capture. Native Human and Citizen previews imported
the animation, added a contact, saved/reopened its edits, and compiled/played combined
98- and 99-bone exports. Editing a confirmed interval returned it to Suggested after
reopening. Prop positions in exported FBX matched preview placement within 0.00001 cm;
this measures export consistency, not reconstruction accuracy. The original capture
remained byte-for-byte unchanged. Both import/contact dialogs were visually reviewed.

Surface suggestions were tested on controlled moving-object fixtures for positive grips,
finger opening, missing observations, competing objects and preserved wrist offsets.
An earlier check of the real 121-frame MediaPipe capture against an authored box produced
zero suggestions. The original explanation attributed this to surface distance, but
incorrect missing metacarpal labels actually prevented finger evaluation. That result
does not establish a distance-based rejection. The native Citizen editor did verify
import of all 12 triangles and compilation/playback of the combined 99-bone export.

After correcting those labels, all three real hand captures were replayed from cached
observations. Every position, rotation and timestamp stayed exact. An explicitly authored
plane following the reconstructed right palm produced one reviewable interval in each
of `segment_037` and `segment_018`, and none in `video_0`. The previous labels produced
no usable contact samples in these same fixtures. This checks observation availability
and suggestion behavior; the plane is controlled test geometry, not the filmed object.
These checks do not establish automatic grip accuracy or provide a penetration benchmark.
The corrected `segment_037` fixture also passed suggestion, confirmation, Human preview
and 120-frame, 95-bone FBX compilation/playback in the native editor. Synchronized source
and target images were inspected. The original motion file remained byte-for-byte unchanged;
source reconstruction still misses the left hand and does not accurately recover every pose.

Orientation anchoring was checked with the real 121-frame MediaPipe sample and the
explicitly authored prop track on both Human and Citizen. The setting survived editing,
confirmation and reopening. Across 31 fully active contact frames, maximum exported
wrist-to-prop rotation change was below 0.00006 degrees on both targets. Their combined
98- and 99-bone FBX animations compiled and played in s&box. This measures constraint and
export consistency, not whether the authored grip matches the filmed object. Numerical
checks also cover activation/release, missing prop observations, equivalent quaternion
signs, overlapping contacts, two hands sharing a prop, fixed bone lengths and preserved
local finger motion at different target proportions.

The contact timeline was checked in the native Citizen editor with the same real capture
and authored prop. Seeking and timing/review callbacks, save/reopen behavior and unchanged
raw capture passed; the 99-bone animation compiled and played after the edit. Yellow,
gray and green timeline states were visually inspected. Physical mouse gestures were
not automated by this gate.

Sliding-key editing was exercised using `segment_037` hand reconstruction and the
explicitly authored control plane. Native dialog checks covered add, update, remove,
fixed/sliding toggling, incomplete-key rejection, save/reopen and cancelled edits.
Human and Citizen previews exported 120-frame, 95- and 96-bone FBX animations that
compiled and played in s&box. The dialog and synchronized preview images were inspected.
The raw capture stayed unchanged. A 2 cm authored slide exported on Citizen matched
its target within 0.0001 cm across 54 reachable contact frames. Another 25 frames
were reach-limited, leaving up to 7.36 cm of target gap while retaining arm lengths.
Those frames matched the closest allowed reach position within 0.0001 cm. This measures
solver/export consistency on a controlled fixture, not real capture accuracy. Review
placement and reach when a hand cannot follow its prop; smoothing cannot fix that gap.

The optional finger-point solver was checked on the same real hand reconstruction and
authored control plane. On Human, mean index-point gap across 79 fully active frames
fell from 2.39 to 1.15 cm. On Citizen with the 2 cm authored slide, it fell from 3.75 to
2.07 cm. No active-frame gap increased beyond 0.001 cm numerical tolerance. Per-joint corrections stayed
within 25°; local bone positions, wrists and unselected bones stayed unchanged. Point
placement, Suggested save/reopen, confirmation and visible markers passed in the native
editor. These are controlled point-target residuals, not skin penetration, grip accuracy
or measured 3D reconstruction error. The targets remain intentionally approximate.
Both 120-frame target animations compiled and played in s&box after armature-only FBX
export. Numerical tests also cover contact fades, missing prop observations, mismatched
target keys, overlapping goals, sliding offsets and preservation of unselected bones.
