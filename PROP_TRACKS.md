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
Sliding contacts retain their authored keys; changing those keys still requires a prepared
motion file. An edited interval must contain all existing sliding keys.

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

Suggestions require an observed wrist and at least three observed finger chains. The
solver tests palm-center proximity to actual triangles, geometric finger bend, relative
motion, persistence and release hysteresis. Missing observations and gaps over 0.1 s break
intervals. Near-equal distances to different objects/parts are left unconstrained and
reported as ambiguous. Existing intervals, including disabled ones, are preserved.
The search is cancellable and has a bounded work budget; it does not move any object.

New intervals stay yellow until confirmed. Their object-local wrist anchors retain the
initial wrist offset instead of placing the wrist itself on the prop surface. These are
heuristics, not calibrated confidence or proof of a grip. They may miss open-handed
contacts, sliding grasps or inaccurate hand/prop alignment. Contact surfaces are not a
hand mesh, and this pass does not solve finger penetration or palm orientation.

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
palm/finger constraints, collision resolution and ContactOpt refinement are not integrated.
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
The real 121-frame MediaPipe capture was also checked against the manually authored box:
it produced zero suggestions because its palms were outside the surface distance limit.
That search took about 0.021 s on the tested Ryzen 7 7800X3D. The native Citizen editor
imported all 12 box triangles, ran the search and compiled/played the final 99-bone export.
These checks establish the import/search/review path; they do not establish automatic
grip accuracy on real object capture or provide a penetration benchmark.
