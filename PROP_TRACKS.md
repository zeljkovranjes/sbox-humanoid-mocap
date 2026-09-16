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
The main **Cancel** button cancels sampling. Meshes and skin weights are not imported.

Use **Add contact…** to choose a wrist, prop bone, video interval and object-local anchor.
**Use wrist at interval midpoint** places a manual anchor from the nearest captured sample;
it refuses missing wrist or object observations. Save, seek to the interval, and confirm
only after reviewing the result. The pencil button edits an existing interval/anchor and
returns it to Suggested. These edits are saved in the motion's adjustment sidecar.
Sliding contacts retain their authored keys; changing those keys still requires a prepared
motion file. An edited interval must contain all existing sliding keys.

You can also open a prepared `.hmotion` through **Advanced → Open motion…**. Neither path
automatically tracks props from video or reconstructs an object's geometry.

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
an 80 ms smooth activation/release. The target arm solver preserves wrist orientation,
finger articulation and limb lengths. Unreachable targets leave a gap rather than
stretching an arm. Object tracks remain authoritative when both hands touch the same prop.
Overlapping contacts blend their goals independently of list order.

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
automatic surface sampling/contact suggestions, palm/finger constraints, mesh collision
and ContactOpt refinement are not integrated. The mathematical suggestion API still
requires actual surface samples and user review; it is not an object tracker.

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
