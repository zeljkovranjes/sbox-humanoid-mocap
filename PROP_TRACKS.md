# Prop tracks

The supported path currently opens a prepared `.hmotion` through **Advanced → Open motion…**.
It does not automatically track props from video, import a separate prop FBX through the UI,
or reconstruct an object's geometry. Keep the original capture and prepare a separate file.

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
Changing interval boundaries and anchors currently requires editing the prepared motion
file; delete or move its adjustment sidecar to avoid restoring an earlier review state.

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
