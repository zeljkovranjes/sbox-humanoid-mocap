#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;
using HumanoidMocap.Target;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Solve;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// The geometric retarget solver (design §5): world-rotation deltas conjugated through
/// canonical anatomical frames, with arc-length spine interpolation, hip-height-scaled pelvis
/// translation, and curl/splay finger redistribution (<see cref="FingerSolver"/>).
/// </summary>
/// <remarks>
/// <para><b>Formulation: absolute canonical-orientation matching.</b> Both rests are first
/// T-pose-normalized (<see cref="RestNormalizer"/>) and canonical frames are built on the
/// <i>normalized</i> rests. A source bind that is not an anatomical pose (SOMA-style
/// uniform-skeleton stick binds) is first rebuilt from the clip's first frame — the solver
/// passes that frame as the reference pose, so deltas (and the pelvis travel) are then
/// measured from the normalized first-frame rest instead; anatomical binds ignore it
/// (see <see cref="RestNormalizer"/> remarks). Per frame and mapped role, the source bone's <i>current</i>
/// canonical frame orientation is <c>ΔR(f) · C_src</c> with
/// <c>ΔR(f) = R_srcWorld(f) · R_srcNormRest⁻¹</c> (the canonical frame rides the bone). The
/// target bone is rotated so its current canonical frame has the <b>same orientation in
/// character-frame coordinates</b> (<c>Q</c> = the rig's character basis, forward/up derived
/// from rest geometry):
/// <c>R_tgtWorld(f) = Q_tgt · Q_src⁻¹ · ΔR(f) · C_src · C_tgt⁻¹ · R_tgtNormRest</c>.</para>
/// <para>This matches worldspace anatomical <i>directions</i> exactly (the canonical X axis is
/// the chain-child direction), rather than preserving each rig's idiosyncratic rest offsets:
/// a naive rest-relative delta (<c>C_tgt·ΔC·C_tgt⁻¹·R_rest</c>) carries the full rest-pose
/// direction mismatch between rigs into every frame — measured at 52° on the s&amp;box rig's
/// curled finger rest vs Mixamo's straight fingers. Note ΔR is measured from the
/// <i>normalized</i> rest, and the source's rest anatomy enters only through <c>C_src</c>,
/// built on that same normalized rest.</para>
/// <para><b>Per-role transfer modes.</b> The argument inverts for roles whose rest direction
/// is <i>anatomy</i>, not pose. Shoulder girdle / neck carriage (rest clavicle directions
/// diverge 6–28° from the s&amp;box rig's): absolute matching drags the target's shoulders to
/// the source's rest line and hunches the neck, so <see cref="RoleTransferMode.DeltaFromRest"/>
/// roles instead replay the source's canonical-space delta from its own normalized rest onto
/// the target's normalized rest: <c>R_tgtWorld(f) = C_tgt · ΔC(f) · C_tgt⁻¹ · R_tgtNormRest</c>
/// with <c>ΔC(f) = C_src⁻¹·ΔR(f)·C_src</c> (the NECK additionally transports the pure-pitch
/// carriage divergence inside that constant with the body heading — a world-fixed divergence
/// axis reads as lateral neck tilt once the clip's heading turns away from the rest heading;
/// see <see cref="Plan.TryAddDirect"/>). Feet (rest foot→toe directions diverge 11–44°
/// from the s&amp;box rig's steep ankle): absolute matching pitched/yawed planted feet by
/// that divergence ("feet bent upward/inward"), while canonical-frame remapping would tilt
/// the rotation <i>axes</i> by it (measured up to 47° planted-pitch error on a CMU-style
/// rig) — so feet default to <see cref="RoleTransferMode.CharacterDeltaFromRest"/>, which
/// replays the delta with its world axes intact:
/// <c>R_tgtWorld(f) = M · ΔR(f) · M⁻¹ · R_tgtNormRest</c> with <c>M = Q_tgt·Q_src⁻¹</c>.
/// The head (rest neck→head directions span 0–27° forward lean across neutral-rest rigs —
/// head-joint placement is anatomy too) likewise defaults to
/// <see cref="RoleTransferMode.CharacterDeltaFromRest"/>: the target keeps its own neutral
/// skull attitude and replays the source's attitude changes.
/// All three modes differ only in the constant pre/postmultipliers around <c>ΔR(f)</c>
/// (see <see cref="SolveOptions.DefaultTransferModes"/> for the default role set); with
/// source == target every mode collapses to <c>ΔR(f)·R_normRest</c>, so the round-trip
/// identity below holds regardless. Under the DEFAULT modes
/// (<see cref="SolveOptions.TransferModes"/> = null) two fallbacks adjust the defaults per
/// rig pair: feet fall back to canonical delta when the source foot direction is a
/// <i>virtual</i> character-forward extension (no mapped toe) but the target's is real
/// anatomy (<see cref="CanonicalFrames.HasVirtualPrimary"/>), and the head falls back to
/// <see cref="RoleTransferMode.AbsoluteDirection"/> (gaze follows the source) when the
/// source's normalized rest head attitude is implausible as a <i>neutral</i> carriage — a
/// posed bind, whose rest-relative deltas would constantly tip the output head (measured
/// ~12° "looking up at an angle" plus a lateral tilt on a fighting-stance bind whose rest
/// head leans 40.7° forward / 16.9° sideways; neutral rests measure −3..27° forward,
/// ≤ 3° lateral). An explicit (non-null) mode map disables both heuristics along with the
/// defaults: the caller's entries are exact, roles absent from the map are absolute. The
/// residual planted-stance offset a source with a non-stance rest pose leaves behind on the
/// FEET (the delta modes reference the REST) is removed by the
/// <see cref="Cleanup.FootGroundAlign"/> cleanup pass, not the solver.</para>
/// <para>Identity proof (citizen round-trip): with source == target, <c>Q_tgt = Q_src</c>,
/// <c>C_tgt = C_src</c> and the normalized rests coincide, so
/// <c>R_tgt(f) = ΔR(f)·R_normRest = R_src(f)</c> exactly.</para>
/// <para><b>Spine.</b> When source and target map the same spine role set the chain transfers
/// 1:1 like any body bone. Otherwise both chains are parametrized by normalized arc length at
/// rest (hips→chest, extended to the neck/head anchor when mapped) and each target spine bone
/// Slerps the <i>absolute</i> character-space canonical orientations of its two bracketing
/// source spine bones (UE "Interpolated").</para>
/// <para><b>Positions.</b> Target bones keep their rest local translations (bone lengths are
/// never modified) except the hips: the pelvis travel from the normalized source rest is
/// re-expressed in a gravity-aligned (yaw-only) source character basis, scaled
/// (horizontal/vertical, default = hip height ratio), and re-expressed in the target world
/// through the target's gravity-aligned basis. Translations must NOT ride through the full
/// anatomical frames: their pitch (the rest pose's lean — ~12° on the CMU corpus rig) pours
/// horizontal travel into world vertical, measured as ±45 cm of spurious pelvis height over
/// a level 3.8 m walk. For sources whose rest pose carries
/// no authored world placement (<see cref="SourceScene.RestPlacementAuthored"/> = false —
/// BVH, whose motion lives in absolute capture-volume coordinates unrelated to the
/// OFFSET-built rest skeleton), the travel reference additionally absorbs a per-clip
/// placement offset: the frame-0 horizontal hips offset from the rest hips and the
/// motion-ground ↔ rest-ground vertical gap. The output then starts over the target origin
/// with ground contact at the target's ground, while all within-clip motion (walks, jumps)
/// is preserved exactly — without this, a mocap subject standing at stage position
/// (347, 86) with hips 107 cm above the capture floor solves to a character floating a
/// full hip height in the air, several meters off origin ("skywalking").</para>
/// <para>Bones with <see cref="BoneClass.ConstraintDriven"/> or <see cref="BoneClass.IkBaked"/>
/// and unmapped animated bones keep their rest locals every frame (IK baking is a separate,
/// later pass). The output asserts finiteness; solving is deterministic.</para>
/// </remarks>
public sealed class GeometricSolver : IRetargetSolver
{
    /// <inheritdoc />
    public Clip Solve(SourceScene source, MappingResult sourceMap, TargetRig target, SolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceMap);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new SolveOptions();

        if (options.ClipIndex < 0 || options.ClipIndex >= source.Clips.Count)
            throw new ArgumentOutOfRangeException(nameof(options), options.ClipIndex,
                $"ClipIndex out of range; the source has {source.Clips.Count} clip(s).");
        var clip = source.Clips[options.ClipIndex];

        var plan = new Plan(source, clip, sourceMap, target, options);
        var output = new Clip(options.ClipName ?? clip.Name, clip.Fps, clip.Looping);
        foreach (var frame in clip.Frames)
            output.Frames.Add(plan.SolveFrame(frame));
        return output;
    }

    // ================================================================ solve plan

    /// <summary>Everything built once per solve; <see cref="SolveFrame"/> is then pure
    /// per-frame math over preallocated scratch buffers.</summary>
    internal sealed class Plan
    {
        private static readonly BoneRole[] SpineRoles =
        {
            BoneRole.Spine0, BoneRole.Spine1, BoneRole.Spine2, BoneRole.Spine3, BoneRole.Spine4,
        };

        private readonly SkeletonModel _src;
        private readonly SkeletonModel _tgt;
        private readonly XForm[] _srcNormRest;
        private readonly XForm[] _tgtNormRest;
        private readonly CanonicalFrames _srcCanon;
        private readonly CanonicalFrames _tgtCanon;
        private readonly Quaternion _chrSrcInv;
        private readonly Quaternion _chrTgt;

        /// <summary>Premultiplier <c>Q_tgt · Q_src⁻¹</c> (character-frame change of basis) —
        /// a solve-level constant shared by every direct entry.</summary>
        private readonly Quaternion _basisChange;
        private readonly float _scaleH;
        private readonly float _scaleV;
        private readonly int _srcHips = -1;
        private readonly int _tgtHips = -1;

        /// <summary>Source-world point pelvis travel is measured FROM. Normally the
        /// normalized-rest hips position; for sources without an authored rest placement
        /// (<see cref="SourceScene.RestPlacementAuthored"/> = false, i.e. BVH) it
        /// additionally absorbs the clip's placement offset — the frame-0 horizontal root
        /// offset and the motion-ground ↔ rest-ground gap (see the class remarks).</summary>
        private readonly Vector3 _hipsTravelRef;

        /// <summary>Gravity-aligned (yaw-only) source/target bases for the PELVIS TRAVEL
        /// re-expression: character forward flattened onto the world ground plane, up snapped
        /// to the nearest world axis. The anatomical frames (<see cref="_chrSrcInv"/>/
        /// <see cref="_chrTgt"/>) may be pitched by the rest pose's lean — the CMU corpus
        /// rig's rest leans ~12° — and rotating TRANSLATIONS through that tilt pours forward
        /// travel into world vertical (measured on cmu_01_01: a level 3.8 m walk gained
        /// ±45 cm of spurious pelvis height, feet sinking 43 cm through the floor). Facing
        /// (yaw) alignment is kept; gravity must stay gravity.</summary>
        private readonly Quaternion _travelSrcInv;
        private readonly Quaternion _travelTgt;

        // Source world-delta slots: which source bones need ΔR computed each frame.
        private readonly List<(int SrcBone, Quaternion NormRestRotInv)> _slots = new();
        private readonly Dictionary<int, int> _slotByBone = new();

        private readonly struct DirectEntry
        {
            /// <summary>Slot of the source ΔR.</summary>
            public required int Slot { get; init; }

            public required int TgtBone { get; init; }

            /// <summary>Premultiplier: <c>Q_tgt · Q_src⁻¹</c> (character-frame change of
            /// basis, the Plan-level <see cref="_basisChange"/>) for
            /// <see cref="RoleTransferMode.AbsoluteDirection"/> and
            /// <see cref="RoleTransferMode.CharacterDeltaFromRest"/> entries,
            /// <c>C_tgt · C_src⁻¹</c> for <see cref="RoleTransferMode.DeltaFromRest"/>.</summary>
            public required Quaternion Pre { get; init; }

            /// <summary>Postmultiplier: <c>C_src · C_tgt⁻¹ · R_tgtNormRest</c> for the two
            /// canonical-frame modes, <c>Q_src · Q_tgt⁻¹ · R_tgtNormRest</c> for
            /// <see cref="RoleTransferMode.CharacterDeltaFromRest"/>.</summary>
            public required Quaternion B { get; init; }

            /// <summary>Slot of the transport bone's ΔR when this entry replays
            /// heading-aware (the <see cref="RoleTransferMode.CharacterDeltaFromRest"/>
            /// head: the source neck, falling back to the hips; the
            /// <see cref="RoleTransferMode.DeltaFromRest"/> neck: the hips), else null and
            /// the entry is the constant-folded product above.</summary>
            public int? HeadingSlot { get; init; }

            /// <summary>Constant carriage divergence: the pure lateral-axis PITCH between
            /// the rigs' rest neck→head lean angles (how much more/less the target's
            /// neutral carriage leans than the source's). Re-applied per frame about the
            /// transport bone's carried-yaw lateral axis — see <see cref="TryAddDirect"/>.
            /// Identity when <see cref="HeadingSlot"/> is null.</summary>
            public Quaternion Div { get; init; }
        }

        private readonly struct SpineEntry
        {
            public required int TgtBone { get; init; }
            public required int LoSlot { get; init; }
            public required int HiSlot { get; init; }
            public required float T { get; init; }
            public required Quaternion CsLo { get; init; }
            public required Quaternion CsHi { get; init; }

            /// <summary>Postmultiplier <c>C_tgt⁻¹ · R_tgtNormRest</c>.</summary>
            public required Quaternion CtInvRest { get; init; }
        }

        private readonly List<DirectEntry> _direct = new();
        private readonly List<SpineEntry> _spine = new();
        private readonly FingerSolver? _fingers;

        // Per-frame scratch.
        private readonly XForm[] _srcWorld;
        private readonly Quaternion[] _deltas;
        private readonly bool[] _solved;
        private readonly Quaternion[] _rot;
        private readonly XForm[] _tgtWorld;

        private readonly IReadOnlyDictionary<BoneRole, RoleTransferMode> _modes;

        /// <summary>True when the caller supplied an explicit <see cref="SolveOptions.TransferModes"/>
        /// map: the map is then exact and every fallback heuristic (the virtual-foot delta
        /// fallback below) is disabled — see <see cref="SolveOptions.TransferModes"/>.</summary>
        private readonly bool _explicitModes;
        private readonly bool _groundedLegDirections;

        public Plan(SourceScene source, Clip clip, MappingResult srcMap, TargetRig rig, SolveOptions options)
        {
            var src = source.Skeleton;
            _src = src;
            _tgt = rig.Skeleton;
            _explicitModes = options.TransferModes is not null;
            _groundedLegDirections = options.GroundedLegDirections;
            _modes = options.TransferModes ?? SolveOptions.DefaultTransferModes;

            // Non-anatomical binds (SOMA uniform-skeleton sticks — see RestNormalizer
            // remarks) carry their real rest orientation in the motion data, so the clip's
            // first frame serves as the rest reference; anatomical binds ignore it.
            var srcReferencePose = clip.Frames.Count > 0 ? clip.Frames[0] : null;

            var tgtMap = rig.ToMappingResult();
            // Grounded source motion uses the file's declared vertical. Inferring up
            // from a crouched rest can turn forward root travel into upward motion.
            var fileUp = AxisVector(source.UpAxis, source.UpAxisSign);
            var anatomicalUp = CharacterFrame.Compute(src, srcMap, src.RestWorld).Up;
            // Preserve the anatomical calibration of upright binds. Only replace it
            // when body lean would make the gravity-axis inference select another axis.
            Vector3? sourceUp = _groundedLegDirections && Vector3.Dot(anatomicalUp, fileUp) < 0.70710678f
                ? fileUp : null;
            var (srcNorm, _) = RestNormalizer.Normalize(src, srcMap, srcReferencePose, sourceUp);
            var (tgtNorm, _) = RestNormalizer.Normalize(_tgt, tgtMap);
            _srcNormRest = srcNorm.WorldRest;
            _tgtNormRest = tgtNorm.WorldRest;
            _srcCanon = CanonicalFrames.Build(src, srcMap, _srcNormRest, sourceUp);
            _tgtCanon = CanonicalFrames.Build(_tgt, tgtMap, _tgtNormRest);

            _chrSrcInv = Quaternion.Conjugate(
                MathQ.BasisFromForwardUp(_srcCanon.CharacterForward, _srcCanon.CharacterUp));
            _chrTgt = MathQ.BasisFromForwardUp(_tgtCanon.CharacterForward, _tgtCanon.CharacterUp);
            _basisChange = MathQ.Normalize(_chrTgt * _chrSrcInv);
            _travelSrcInv = Quaternion.Conjugate(GravityAlignedBasis(_srcCanon));
            _travelTgt = GravityAlignedBasis(_tgtCanon);

            var ratio = _srcCanon.HipHeight > 1e-3f ? _tgtCanon.HipHeight / _srcCanon.HipHeight : 1f;
            if (!float.IsFinite(ratio) || ratio <= 0f)
                ratio = 1f;
            _scaleH = options.HipScaleHorizontal ?? ratio;
            _scaleV = options.HipScaleVertical ?? ratio;

            if (srcMap.RoleToBone.TryGetValue(BoneRole.Hips, out var srcHips))
                _srcHips = srcHips;
            _tgtHips = rig.BoneForRole(BoneRole.Hips) ?? -1;

            if (_srcHips >= 0)
            {
                _hipsTravelRef = _srcNormRest[_srcHips].Pos;
                if (!source.RestPlacementAuthored)
                    _hipsTravelRef += ClipPlacementOffset(source, clip);
            }

            // Body roles (everything but spine + fingers), in target bone order (deterministic).
            for (var i = 0; i < _tgt.Count; i++)
            {
                if (rig.RoleOf(i) is not BoneRole role)
                    continue;
                if (SpineRoles.Contains(role) || FingerSolver.IsFingerRole(role))
                    continue;
                TryAddDirect(role, srcMap, rig);
            }

            BuildSpine(srcMap, rig);

            if (options.TransferFingers)
            {
                _fingers = FingerSolver.Build(
                    srcMap, _srcCanon, _srcNormRest, rig.BoneForRole, _tgtCanon, _tgtNormRest,
                    _tgt.RestWorld,
                    _chrSrcInv, _chrTgt,
                    RegisterSlot, role => TryAddDirect(role, srcMap, rig));
            }

            _srcWorld = new XForm[_src.Count];
            _deltas = new Quaternion[_slots.Count];
            _solved = new bool[_tgt.Count];
            _rot = new Quaternion[_tgt.Count];
            _tgtWorld = new XForm[_tgt.Count];
        }

        // ------------------------------------------------------------ build helpers

        /// <summary>
        /// Clip placement offset for sources whose rest carries no authored world placement
        /// (BVH — see <see cref="SourceScene.RestPlacementAuthored"/>): the source-world
        /// translation separating the CLIP's placement from the REST skeleton's. Horizontal
        /// part: the frame-0 hips offset from the rest hips (an absolute capture-volume
        /// position — the subject stood wherever it stood on the stage); vertical part: the
        /// motion ground (lowest joint reached over the whole clip) minus the rest ground
        /// (lowest rest joint). Subtracting it from the travel reference re-centers the
        /// clip's start over the target origin and puts ground contact at the target's own
        /// ground — while every within-clip displacement (walks, jumps) is preserved exactly
        /// (the offset is one constant for the whole clip). Measured on the repro:
        /// Armchair1.bvh starts at (347, 107, 86) with rest ground −107 → the solved pelvis
        /// hovered a full hip height above the s&amp;box rig, 3.5 grid-widths off origin.
        /// </summary>
        private Vector3 ClipPlacementOffset(SourceScene source, Clip clip)
        {
            if (clip.Frames.Count == 0 || clip.Frames[0].Length != _src.Count)
                return Vector3.Zero;

            var up = AxisVector(source.UpAxis, source.UpAxisSign);
            var world = new XForm[_src.Count];
            float motionGround = float.MaxValue;
            var firstHips = Vector3.Zero;
            for (var f = 0; f < clip.Frames.Count; f++)
            {
                var locals = clip.Frames[f];
                if (locals.Length != _src.Count)
                    return Vector3.Zero; // malformed frame — SolveFrame reports it properly
                for (var i = 0; i < _src.Count; i++)
                {
                    var parent = _src[i].ParentIndex;
                    world[i] = parent < 0 ? locals[i] : XForm.Compose(world[parent], locals[i]);
                    motionGround = MathF.Min(motionGround, Vector3.Dot(world[i].Pos, up));
                }
                if (f == 0)
                    firstHips = world[_srcHips].Pos;
            }

            var restGround = float.MaxValue;
            foreach (var x in _srcNormRest)
                restGround = MathF.Min(restGround, Vector3.Dot(x.Pos, up));

            var horizontal = firstHips - _srcNormRest[_srcHips].Pos;
            horizontal -= up * Vector3.Dot(horizontal, up);
            var offset = horizontal + up * (motionGround - restGround);
            return float.IsFinite(offset.X + offset.Y + offset.Z) ? offset : Vector3.Zero;
        }

        private static Vector3 AxisVector(int axis, int sign) => axis switch
        {
            0 => new Vector3(sign, 0f, 0f),
            2 => new Vector3(0f, 0f, sign),
            _ => new Vector3(0f, sign, 0f),
        };

        /// <summary>The yaw-only travel basis of a rig (see <see cref="_travelSrcInv"/>):
        /// character up snapped to the nearest signed world axis (every corpus rest is
        /// within ~12° of one), character forward flattened onto the world ground plane.
        /// Falls back to the full anatomical frame when the rest faces straight up/down
        /// (a flattened forward would be degenerate — no meaningful yaw exists).</summary>
        private static Quaternion GravityAlignedBasis(CanonicalFrames canon)
        {
            var up = canon.CharacterUp;
            var absX = MathF.Abs(up.X);
            var absY = MathF.Abs(up.Y);
            var absZ = MathF.Abs(up.Z);
            var snapped = absX >= absY && absX >= absZ
                ? new Vector3(MathF.Sign(up.X), 0f, 0f)
                : absY >= absZ
                    ? new Vector3(0f, MathF.Sign(up.Y), 0f)
                    : new Vector3(0f, 0f, MathF.Sign(up.Z));
            var forward = canon.CharacterForward
                - snapped * Vector3.Dot(canon.CharacterForward, snapped);
            if (forward.LengthSquared() < 1e-6f)
                return MathQ.BasisFromForwardUp(canon.CharacterForward, canon.CharacterUp);
            return MathQ.BasisFromForwardUp(forward, snapped);
        }

        private int RegisterSlot(int srcBone)
        {
            if (_slotByBone.TryGetValue(srcBone, out var slot))
                return slot;
            slot = _slots.Count;
            _slots.Add((srcBone, Quaternion.Conjugate(_srcNormRest[srcBone].Rot)));
            _slotByBone[srcBone] = slot;
            return slot;
        }

        private void TryAddDirect(BoneRole role, MappingResult srcMap, TargetRig rig)
        {
            if (!srcMap.RoleToBone.TryGetValue(role, out var srcBone))
                return;
            if (rig.BoneForRole(role) is not int tgtBone)
                return;
            if (!_srcCanon.Has(role) || !_tgtCanon.Has(role))
                return;

            var cs = _srcCanon.WorldFrameOf(role);
            var ct = _tgtCanon.WorldFrameOf(role);
            if (!_modes.TryGetValue(role, out var mode))
                mode = RoleTransferMode.AbsoluteDirection;

            // Feet whose SOURCE direction is a virtual character-forward extension (no mapped
            // toe) while the target's is real anatomy fall back to canonical delta transfer:
            // any direction-matching against that arbitrary virtual axis is meaningless
            // (measured: constant ~41° dorsiflex / heel-standing on the toe-less
            // makehuman/daz rig under absolute matching). With real anatomy on both sides
            // feet take the CharacterDeltaFromRest default instead. Same-rig round trips
            // have equal virtual flags on both sides, so this never fires there.
            // HEURISTIC, defaults only: an explicit TransferModes map is a contract — the
            // caller's entries (and absences = absolute) must win, so the fallback never
            // overrides it (see SolveOptions.TransferModes).
            if (!_explicitModes
                && role is BoneRole.FootL or BoneRole.FootR
                && _srcCanon.HasVirtualPrimary(role) && !_tgtCanon.HasVirtualPrimary(role))
            {
                mode = RoleTransferMode.DeltaFromRest;
            }

            // Head whose SOURCE rest attitude is implausible as a NEUTRAL carriage (a posed
            // bind: e.g. a fighting-stance rest with the head chin-down and tilted) falls
            // back to absolute gaze matching: the delta default replays attitude changes
            // from the rest, so a posed rest reference constantly tips the output head by
            // the pose (measured mean −12° pitch — "looking up at an angle" — plus a
            // lateral tilt, on a rig whose rest head leans 40.7° forward / 16.9° sideways
            // vs −3..27° forward / ≤ 3° lateral across every neutral-rest corpus rig).
            // Absolute matching needs REAL skull-base geometry on both sides — a virtual
            // primary would impose an arbitrary character axis (the virtual-foot lesson).
            // HEURISTIC, defaults only (see above / SolveOptions.TransferModes).
            if (!_explicitModes
                && role == BoneRole.Head
                && !_srcCanon.HasVirtualPrimary(role) && !_tgtCanon.HasVirtualPrimary(role)
                && IsPosedRestHead(_srcCanon))
            {
                mode = RoleTransferMode.AbsoluteDirection;
            }
            // Heading-aware head replay. The plain CharacterDeltaFromRest product
            // Q·ΔR·Q⁻¹·R_tgtRest applies the constant source↔target head-carriage
            // divergence (the rigs' differing neutral neck→head lean, D below) in the
            // REST heading's frame — it rides along with the world delta, so when the
            // clip holds a pitch while the character faces away from its rest heading
            // the divergence counter-rotates and reflects into a real pitch error of up
            // to 2×D (measured −26.6° chin-up plateaus on a tumbling clip whose rig
            // diverges 13.4° from the citizen; pure yaw is exact, error 0 at rest
            // facing). Fix: transport D with the head's own carried yaw — conjugate it by
            // the yaw of the CURRENT source head direction (rest-relative), mapped to the
            // target side:
            //     W_t = Y·D·Y⁻¹ · Q·ΔR · Q⁻¹·D⁻¹·R_tgtRest,  Y = Q·Yaw(λ(f)−λ0)·Q⁻¹
            // where λ(f) is the carried head direction's yaw about character up.
            // Properties: exact at ΔR=I (rest preserved, D cancels), identical to the
            // old product for pure yaw (head turning with the body) and for pitches at
            // rest facing (D is lateral and commutes with lateral-axis rotations),
            // collapses to the same round-trip identity when source==target (D=I), and
            // removes the 2× reflection at turned facings. Transport frame choices that
            // measurably FAIL: the full hips ΔR rides the step cycle's hip pitch/roll
            // into the divergence axis (median 8.4° per-step head wobble on a plain CMU
            // walk, 22° peaks seated — a user-visible "weird neck"); the hips yaw twist
            // distorts head YAW whenever the head looks off-heading (−12° → −21° mean on
            // a curving walk whose head leads the turn) because pitching about an axis
            // not perpendicular to the head direction leaks into yaw. The head's own
            // carried yaw is the axis that by construction changes lean only (hips yaw
            // twist remains as the near-vertical fallback). Feet share the world-axes
            // replay but their pitch/roll is re-anchored by FootGroundAlign/FootPlant
            // cleanups — left as is.
            // D is the PURE PITCH between the rigs' rest head-lean angles (the audit's
            // "expected constant lean offset"), NOT the full canonical-frame difference
            // ct·cs⁻¹·Q⁻¹: the full form also carries the frames' yaw/roll construction
            // residue, and yaw-conjugating that residue measurably worsened head yaw
            // tracking (−11.9° → −19.7° mean on a curving CMU walk). Only the anatomical
            // lean difference is heading-dependent; everything else stays in the constant
            // product exactly as before.
            // The NECK's DeltaFromRest constant has the same structure and the same defect:
            // ct·cs⁻¹ factors exactly into D·K (D = the pure-pitch carriage divergence
            // between the rigs' rest neck→head leans about the target lateral axis, K the
            // frames' construction residue — measured 24.90° pitch / 0.00° residue on the
            // rokoko-class Armchair1 rig vs the citizen, 13.4°/0.5° on CMU), so the old
            // product W = D·(K·ΔR·K⁻¹)·D⁻¹·R_tgtRest re-applies D about the REST heading's
            // lateral axis. A clip whose body heading is yawed λ away from the rest heading
            // reads that pitch as heading-relative LATERAL tilt ≈ D·sin λ (measured on
            // Armchair1, seated facing ~125° off rest: mean +13.3° / worst 85.7° lateral
            // neck-segment residual, correlation 0.87 with D·sin λ — the user-visible
            // "neck sticking out to the left"; pre-existing since before the head-carriage
            // wave). Same fix, same machinery: transport D with the carried heading yaw,
            // W = Y·D·Y⁻¹·K·ΔR·K⁻¹·D⁻¹·R_tgtRest (Pre absorbs D⁻¹, B is unchanged since
            // K⁻¹·D⁻¹ = cs·ct⁻¹). Identical to the old product at rest facing, exact at
            // ΔR = I, and the same round-trip identity (D = I when source == target).
            int? headingSlot = null;
            var div = Quaternion.Identity;
            var headReplay = role == BoneRole.Head && mode == RoleTransferMode.CharacterDeltaFromRest;
            // The neck's carriage divergence D compares the rigs' rest neck→head leans —
            // defined only when BOTH sides map a Head. Without one (e.g. the impossible-
            // head veto) the neck's frame X falls back to the inherited spine→neck
            // segment, and measuring THAT against the source's skull-base segment
            // fabricated a ~19° constant pitch — on a rig whose neck bone skins the whole
            // torso it read as a hump grafted onto the character's back.
            var neckReplay = role == BoneRole.Neck && mode == RoleTransferMode.DeltaFromRest
                && srcMap.RoleToBone.ContainsKey(BoneRole.Head)
                && rig.BoneForRole(BoneRole.Head) is not null;
            if ((headReplay || neckReplay)
                && srcMap.RoleToBone.TryGetValue(BoneRole.Hips, out var srcHipsBone))
            {
                // Transport frame: the HEAD uses the source NECK's yaw twist (carriage
                // divergence is a neck-frame property — the neck turns fully with the body
                // and carries most of the head's look-around yaw); neck-less rigs use the
                // hips. The NECK itself uses the HIPS yaw twist (body heading): its rest
                // lean is TORSO anatomy, and transporting it with the neck's own yaw was
                // measured to over-rotate the tilt axis whenever the head looks off-body
                // (+12.0° lateral seg residual at a 44°-yawed look on Armchair1 vs +5.5°
                // with the body heading; clip means 3.0° vs 0.5°).
                headingSlot = RegisterSlot(
                    headReplay && srcMap.RoleToBone.TryGetValue(BoneRole.Neck, out var srcNeckBone)
                        ? srcNeckBone
                        : srcHipsBone);
                var srcDir = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, cs));
                var tgtDir = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, ct));
                var srcLean = MathF.Atan2(
                    Vector3.Dot(srcDir, _srcCanon.CharacterForward),
                    Vector3.Dot(srcDir, _srcCanon.CharacterUp));
                var tgtLean = MathF.Atan2(
                    Vector3.Dot(tgtDir, _tgtCanon.CharacterForward),
                    Vector3.Dot(tgtDir, _tgtCanon.CharacterUp));
                var lateral = Vector3.Normalize(
                    Vector3.Cross(_tgtCanon.CharacterUp, _tgtCanon.CharacterForward));
                var divergence = tgtLean - srcLean;
                // A human head/neck carriage never differs by anything near 45° - beyond
                // that the anatomical direction derivation failed on non-human geometry
                // (measured: a cartoon character whose skull runs along the bone read a
                // ~90° "lean difference" and played every clip staring at the sky). A
                // failed measurement must not correct anything.
                if (MathF.Abs(divergence) > MathF.PI * 0.25f)
                    divergence = 0f;
                div = Quaternion.CreateFromAxisAngle(lateral, divergence);
            }

            _direct.Add(new DirectEntry
            {
                Slot = RegisterSlot(srcBone),
                TgtBone = tgtBone,
                Pre = mode switch
                {
                    // div is identity except for the heading-transported NECK (see above),
                    // where Pre must be K = D⁻¹·ct·cs⁻¹ so the per-frame premultiplier
                    // Y·D·Y⁻¹ re-applies the divergence about the CURRENT heading.
                    RoleTransferMode.DeltaFromRest => MathQ.Normalize(
                        Quaternion.Conjugate(div) * ct * Quaternion.Conjugate(cs)),
                    // Leg directions share the pelvis travel's gravity-aligned basis.
                    // A hunched torso is not a tilted floor: carrying its rest lean into
                    // a staggered stance lifts one planted foot above the other.
                    RoleTransferMode.AbsoluteDirection when _groundedLegDirections
                        && role is BoneRole.UpperLegL or BoneRole.UpperLegR
                            or BoneRole.LowerLegL or BoneRole.LowerLegR
                        => MathQ.Normalize(_travelTgt * _travelSrcInv),
                    _ => _basisChange,
                },
                B = mode == RoleTransferMode.CharacterDeltaFromRest
                    ? headingSlot is null
                        ? MathQ.Normalize(Quaternion.Conjugate(_basisChange) * _tgtNormRest[tgtBone].Rot)
                        : MathQ.Normalize(
                            Quaternion.Conjugate(_basisChange) * Quaternion.Conjugate(div)
                            * _tgtNormRest[tgtBone].Rot)
                    : MathQ.Normalize(cs * Quaternion.Conjugate(ct) * _tgtNormRest[tgtBone].Rot),
                HeadingSlot = headingSlot,
                Div = div,
            });
        }

        /// <summary>
        /// Plausibility band of a NEUTRAL rest head attitude (the rest neck→head direction
        /// against character up). Measured across 13 neutral-rest corpus rigs the forward
        /// lean spans −2.9°…27.4° (head-joint placement anatomy: mocap-style BVH rigs sit
        /// near 0°, character rigs at 21–27°, the s&amp;box rig at 25.5°) and the lateral
        /// lean stays within ±2.8° (bilateral symmetry — no rig convention tilts a neutral
        /// head sideways). The posed fighting-stance bind that motivated the gate measures
        /// 40.7° forward / 16.9° lateral (Defenses.fbx).
        /// </summary>
        private const float HeadNeutralFwdLeanMinDeg = -8f;
        private const float HeadNeutralFwdLeanMaxDeg = 33f;
        private const float HeadNeutralLatLeanMaxDeg = 6f;

        /// <summary>True when the rig's rest head attitude falls outside the neutral-carriage
        /// plausibility band above — i.e. its bind pose carries a posed (chin-down / tilted)
        /// head that makes rest-relative head deltas read constantly tipped. Requires a real
        /// head primary (callers gate on <see cref="CanonicalFrames.HasVirtualPrimary"/>).</summary>
        private static bool IsPosedRestHead(CanonicalFrames canon)
        {
            var dir = Vector3.Transform(Vector3.UnitX, canon.WorldFrameOf(BoneRole.Head));
            var up = canon.CharacterUp;
            var fwd = canon.CharacterForward;
            var lat = Vector3.Cross(up, fwd);
            const float toDeg = 180f / MathF.PI;
            var fwdLean = MathF.Atan2(Vector3.Dot(dir, fwd), Vector3.Dot(dir, up)) * toDeg;
            var latLean = MathF.Atan2(Vector3.Dot(dir, lat), Vector3.Dot(dir, up)) * toDeg;
            return fwdLean < HeadNeutralFwdLeanMinDeg
                || fwdLean > HeadNeutralFwdLeanMaxDeg
                || MathF.Abs(latLean) > HeadNeutralLatLeanMaxDeg;
        }

        private void BuildSpine(MappingResult srcMap, TargetRig rig)
        {
            var srcSpine = SpineRoles
                .Where(r => srcMap.RoleToBone.ContainsKey(r) && _srcCanon.Has(r))
                .ToArray();
            var tgtSpine = SpineRoles
                .Where(r => rig.BoneForRole(r) is not null && _tgtCanon.Has(r))
                .ToArray();
            if (srcSpine.Length == 0 || tgtSpine.Length == 0)
                return;

            if (srcSpine.SequenceEqual(tgtSpine))
            {
                // Same chain shape: degenerate to 1:1 (preserves per-bone detail exactly,
                // and makes the same-rig round-trip identity).
                foreach (var role in srcSpine)
                    TryAddDirect(role, srcMap, rig);
                return;
            }

            var srcU = ArcParams(
                srcSpine.Select(r => _srcNormRest[srcMap.RoleToBone[r]].Pos).ToArray(),
                ChainEndAnchor(r => srcMap.RoleToBone.TryGetValue(r, out var b) ? b : null, _srcNormRest));
            var tgtU = ArcParams(
                tgtSpine.Select(r => _tgtNormRest[rig.BoneForRole(r)!.Value].Pos).ToArray(),
                ChainEndAnchor(rig.BoneForRole, _tgtNormRest));

            for (var k = 0; k < tgtSpine.Length; k++)
            {
                var role = tgtSpine[k];
                var tgtBone = rig.BoneForRole(role)!.Value;
                var (lo, hi, t) = Bracket(srcU, tgtU[k]);

                var ct = _tgtCanon.WorldFrameOf(role);
                _spine.Add(new SpineEntry
                {
                    TgtBone = tgtBone,
                    LoSlot = RegisterSlot(srcMap.RoleToBone[srcSpine[lo]]),
                    HiSlot = RegisterSlot(srcMap.RoleToBone[srcSpine[hi]]),
                    T = t,
                    CsLo = _srcCanon.WorldFrameOf(srcSpine[lo]),
                    CsHi = _srcCanon.WorldFrameOf(srcSpine[hi]),
                    CtInvRest = MathQ.Normalize(Quaternion.Conjugate(ct) * _tgtNormRest[tgtBone].Rot),
                });
            }
        }

        /// <summary>The neck (or head) rest position extends the spine chain so the last spine
        /// bone gets an arc parameter &lt; 1, comparable across rigs.</summary>
        private static Vector3? ChainEndAnchor(Func<BoneRole, int?> boneForRole, XForm[] worldRest)
        {
            foreach (var role in new[] { BoneRole.Neck, BoneRole.Head })
            {
                if (boneForRole(role) is int bone)
                    return worldRest[bone].Pos;
            }
            return null;
        }

        /// <summary>Normalized cumulative arc-length parameters of a chain's bones
        /// (first bone = 0; the optional end anchor counts toward the total length).</summary>
        private static float[] ArcParams(Vector3[] points, Vector3? endAnchor)
        {
            var u = new float[points.Length];
            var cum = 0f;
            for (var i = 1; i < points.Length; i++)
            {
                cum += (points[i] - points[i - 1]).Length();
                u[i] = cum;
            }

            var total = cum + (endAnchor is { } anchor ? (anchor - points[^1]).Length() : 0f);
            if (total <= 1e-6f)
                return u; // degenerate chain: all parameters 0

            for (var i = 0; i < u.Length; i++)
                u[i] /= total;
            return u;
        }

        private static (int Lo, int Hi, float T) Bracket(float[] knots, float u)
        {
            if (knots.Length == 1 || u <= knots[0])
                return (0, 0, 0f);
            if (u >= knots[^1])
                return (knots.Length - 1, knots.Length - 1, 0f);
            for (var j = 0; j + 1 < knots.Length; j++)
            {
                if (u > knots[j + 1])
                    continue;
                var span = knots[j + 1] - knots[j];
                return (j, j + 1, span > 1e-6f ? (u - knots[j]) / span : 0f);
            }
            return (knots.Length - 1, knots.Length - 1, 0f); // unreachable
        }

        // ------------------------------------------------------------ per frame

        public XForm[] SolveFrame(XForm[] srcLocals)
        {
            ArgumentNullException.ThrowIfNull(srcLocals);
            var outLocals = new XForm[_tgt.Count];
            SolveFrameInto(srcLocals, outLocals);
            return outLocals;
        }

        /// <summary>
        /// Allocation-free frame solve used by the runtime streaming API. The plan owns and
        /// reuses its FK/delta scratch buffers; the caller owns the destination buffer.
        /// </summary>
        internal void SolveFrameInto(ReadOnlySpan<XForm> srcLocals, Span<XForm> outLocals)
        {
            if (srcLocals.Length != _src.Count)
                throw new ArgumentException(
                    $"Frame has {srcLocals.Length} bone transforms but the source skeleton has {_src.Count}.",
                    nameof(srcLocals));
            if (outLocals.Length < _tgt.Count)
                throw new ArgumentException(
                    $"Destination has {outLocals.Length} bone transforms but the target skeleton has {_tgt.Count}.",
                    nameof(outLocals));

            // Source FK (frame locals live in the original rest hierarchy).
            for (var i = 0; i < _src.Count; i++)
            {
                var parent = _src[i].ParentIndex;
                _srcWorld[i] = parent < 0 ? srcLocals[i] : XForm.Compose(_srcWorld[parent], srcLocals[i]);
            }

            // World rotation deltas from the normalized source rest.
            for (var k = 0; k < _slots.Count; k++)
            {
                var (bone, restInv) = _slots[k];
                _deltas[k] = MathQ.Normalize(_srcWorld[bone].Rot * restInv);
            }

            Array.Clear(_solved, 0, _solved.Length);

            foreach (var d in _direct)
            {
                var r = d.Pre * _deltas[d.Slot] * d.B;
                if (d.HeadingSlot is int headingSlot)
                {
                    // Carriage-divergence transport for the head/neck (see TryAddDirect): the
                    // pure-pitch divergence D is re-applied about the lateral axis of the
                    // transport bone's carried yaw (its ΔR's twist about character up —
                    // stable at any pitch, unlike direction-projection yaw which is
                    // noise near vertical).
                    MathQ.SwingTwist(
                        _deltas[headingSlot], _srcCanon.CharacterUp, out _, out var facing);
                    var yawT = _basisChange * facing * Quaternion.Conjugate(_basisChange);
                    r = yawT * d.Div * Quaternion.Conjugate(yawT) * r;
                }
                _rot[d.TgtBone] = MathQ.Normalize(r);
                _solved[d.TgtBone] = true;
            }

            foreach (var s in _spine)
            {
                // Absolute character-space canonical orientations of the bracketing source
                // spine bones, Slerped at the target bone's arc parameter.
                var aLo = MathQ.Normalize(_chrSrcInv * _deltas[s.LoSlot] * s.CsLo);
                var dc = s.T <= 0f
                    ? aLo
                    : Quaternion.Slerp(aLo, MathQ.Normalize(_chrSrcInv * _deltas[s.HiSlot] * s.CsHi), s.T);
                _rot[s.TgtBone] = MathQ.Normalize(_chrTgt * dc * s.CtInvRest);
                _solved[s.TgtBone] = true;
            }

            _fingers?.Apply(_deltas, _solved, _rot);

            // Pelvis translation: character-frame re-expression with hip-height scaling.
            // Travel is measured from _hipsTravelRef — the normalized-rest hips, plus the
            // clip placement offset on unplaced (BVH) sources (see ClipPlacementOffset).
            Vector3? hipsPos = null;
            if (_srcHips >= 0 && _tgtHips >= 0 && _solved[_tgtHips])
            {
                // Yaw-only bases here: rotating the travel through the anatomical frames'
                // rest-lean pitch would pour horizontal travel into world vertical
                // (see _travelSrcInv).
                var v = Vector3.Transform(
                    _srcWorld[_srcHips].Pos - _hipsTravelRef, _travelSrcInv);
                v = new Vector3(v.X * _scaleH, v.Y * _scaleH, v.Z * _scaleV); // chr Z = up
                hipsPos = _tgtNormRest[_tgtHips].Pos + Vector3.Transform(v, _travelTgt);
            }

            // Compose output locals top-down over the target hierarchy.
            for (var i = 0; i < _tgt.Count; i++)
            {
                var bone = _tgt[i];
                var parent = bone.ParentIndex;
                if (!_solved[i])
                {
                    _tgtWorld[i] = parent < 0
                        ? bone.RestLocal
                        : XForm.Compose(_tgtWorld[parent], bone.RestLocal);
                    outLocals[i] = bone.RestLocal;
                    continue;
                }

                var pos = i == _tgtHips && hipsPos is { } hp
                    ? hp
                    : parent < 0
                        ? bone.RestLocal.Pos
                        : _tgtWorld[parent].TransformPoint(bone.RestLocal.Pos);
                _tgtWorld[i] = new XForm(pos, _rot[i]);
                outLocals[i] = parent < 0 ? _tgtWorld[i] : XForm.ToLocal(_tgtWorld[parent], _tgtWorld[i]);
            }

            ValidateFinite(outLocals);
        }

        private static void ValidateFinite(ReadOnlySpan<XForm> locals)
        {
            foreach (var x in locals)
            {
                var sum = x.Pos.X + x.Pos.Y + x.Pos.Z + x.Rot.X + x.Rot.Y + x.Rot.Z + x.Rot.W;
                if (!float.IsFinite(sum))
                    throw new InvalidOperationException(
                        "Retarget solve produced a non-finite transform — geometry or input data is degenerate.");
            }
        }
    }
}
