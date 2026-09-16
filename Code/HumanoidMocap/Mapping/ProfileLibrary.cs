#nullable enable annotations

using System.Collections.Generic;

namespace HumanoidMocap.Mapping;

/// <summary>
/// Built-in preset profiles, embedded as C# data (the same data is written to
/// <c>Assets/humanoid_mocap/profiles/*.json</c> by a regenerate-and-diff test so the
/// shipped JSON can never drift from the code).
/// </summary>
public static class ProfileLibrary
{
    /// <summary>Mixamo / Adobe rigs: <c>mixamorig[N]:</c> namespace, <c>LeftArm</c> /
    /// <c>LeftForeArm</c> / <c>LeftHandIndex1..3</c> style names.</summary>
    public static Profile Mixamo { get; } = BuildMixamo();

    /// <summary>
    /// Reallusion ActorCore / AccuRig / Character Creator rigs (<c>CC_Base_*</c>).
    /// Empirical notes from <c>research/rig_actorcore.json</c>:
    /// <list type="bullet">
    /// <item><c>CC_Base_Hip</c> is the parent of BOTH <c>CC_Base_Pelvis</c> (leg branch) and
    /// <c>CC_Base_Waist</c> (spine branch), i.e. the LCA of legs+spine and the true animated
    /// hips root → it carries <see cref="BoneRole.Hips"/>; <c>CC_Base_Pelvis</c> is a
    /// leg-branch intermediate and stays unmapped.</item>
    /// <item>The neck chain is <c>CC_Base_NeckTwist01 → CC_Base_NeckTwist02 → CC_Base_Head</c>;
    /// despite the name, <c>NeckTwist01</c> IS the neck bone (there is no plain
    /// <c>CC_Base_Neck</c>), so it is the <see cref="BoneRole.Neck"/> alias. NeckTwist02 is
    /// left unmapped. All other Twist/ShareBone helpers are excluded (no aliases).</item>
    /// <item><c>CC_Base_L_ToeBase</c> is the toe role; the co-located
    /// <c>CC_Base_L_ToeBaseShareBone</c> is a helper and must never be mapped.</item>
    /// </list>
    /// </summary>
    public static Profile ActorCoreCc { get; } = BuildActorCoreCc();

    /// <summary>Unreal Engine mannequin (UE4/UE5): <c>pelvis</c>, <c>spine_01..05</c>,
    /// <c>clavicle_l</c>, <c>thumb_01_l</c>, UE5 <c>*_metacarpal_*</c>; <c>*_twist_*</c>
    /// bones have no aliases and are never mapped.</summary>
    public static Profile UeMannequin { get; } = BuildUeMannequin();

    /// <summary>Rokoko / Xsens style BVH rigs: plain <c>Hips</c>/<c>Spine..Spine4</c>/<c>
    /// LeftArm|LeftUpperArm</c> name variants, usually no fingers.</summary>
    public static Profile RokokoBvh { get; } = BuildRokokoBvh();

    /// <summary>
    /// SMPL body model family (AMASS exports, Meshcapade FBX rigs). Joint names per the
    /// published model (vchoutas/smplx <c>joint_names.py</c>, Meshcapade wiki):
    /// <c>pelvis</c>, sided <c>hip→knee→ankle→foot</c> legs (the "hip" joint IS the thigh;
    /// "ankle" is the foot, "foot" is the toe region) and <c>collar→shoulder→elbow→wrist</c>
    /// arms ("shoulder" is the upper arm, "wrist" is the hand; the <c>hand</c> joint is a
    /// finger stub and stays unmapped). Both spellings occur in the wild: <c>left_hip</c>
    /// (model joints) and <c>L_Hip</c> with gendered FBX prefixes <c>m_avg_</c>/<c>f_avg_</c>
    /// (SMPL Unity/FBX rigs). No fingers — that is SMPL-X (<see cref="SmplX"/>), kept as a
    /// separate preset so a finger-less SMPL rig still reaches full optional coverage.
    /// </summary>
    public static Profile Smpl { get; } = BuildSmpl(withFingers: false);

    /// <summary>
    /// SMPL-X: the SMPL body joints (<see cref="Smpl"/>) plus articulated hands —
    /// <c>left_thumb1..3</c>/<c>left_index1..3</c>-style finger joints per
    /// vchoutas/smplx <c>joint_names.py</c> (jaw/eye joints carry no humanoid role).
    /// Evaluated before <see cref="Smpl"/> so it wins the tie on SMPL-X rigs (both score
    /// the body fully; only this one maps the fingers).
    /// </summary>
    public static Profile SmplX { get; } = BuildSmpl(withFingers: true);

    /// <summary>
    /// NVIDIA SOMA uniform-proportion skeleton (SOMA/SEED BVH exports, e.g.
    /// github.com/NVIDIA/soma-retargeter <c>assets/motions/bvh</c>). Mixamo-like upper-body
    /// names, but: spine is <c>Spine1→Spine2→Chest</c> (no plain "Spine"), neck is
    /// <c>Neck1→Neck2</c>, the legs are <c>LeftLeg→LeftShin</c> — SOMA's <c>LeftLeg</c> is
    /// the THIGH (mixamo's is the calf), which is exactly why the mixamo preset must never
    /// claim these rigs — and the four fingers have FOUR segments where segment 1 is a
    /// metacarpal (<c>LeftHandIndex1..4</c>; mixamo's 1..3 are the phalanges), so the
    /// phalanx roles map to segments 2/3/4.
    /// </summary>
    public static Profile SomaBvh { get; } = BuildSomaBvh();

    /// <summary>
    /// Classic BVH / Character-Studio-friendly naming (MotionBuilder "Export BVH to
    /// Character Studio" convention, ACCAD-style mocap BVHs): <c>Hips</c>,
    /// <c>Chest[2..4]</c> spine, arms <c>Collar→Shoulder→Elbow→Wrist</c> (the "Shoulder"
    /// is the upper arm) and legs <c>Hip→Knee→Ankle→Toe</c> (the sided "Hip" is the
    /// thigh). No fingers.
    /// </summary>
    public static Profile ClassicBvh { get; } = BuildClassicBvh();

    /// <summary>
    /// 3ds Max Character Studio Biped rigs: every bone is "&lt;BipedName&gt; &lt;Part&gt;"
    /// where the biped name defaults to <c>Bip01</c> (3ds Max ≤2009) / <c>Bip001</c>
    /// (2010+) per the Autodesk "Naming the Biped" documentation; some exporters mangle
    /// the spaces to underscores (<c>Bip01_L_Thigh</c>). Biped names are user-editable
    /// (<c>BipTrump L Thigh</c> is valid), so the namespace accepts any alphanumeric name
    /// (alias comparison is separator-insensitive, so "L UpperArm" and
    /// "L_UpperArm" normalize identically). Sided bones use a bare mid-name <c>L/R</c>:
    /// <c>L Clavicle→L UpperArm→L Forearm→L Hand</c> arms,
    /// <c>L Thigh→L Calf→L Foot→L Toe0</c> legs. Fingers are numbered chains
    /// <c>L Finger0..4</c> (0 = thumb) with phalanx segments <c>Finger01/Finger02</c>
    /// etc. (MotionBuilder's "3ds Max Biped Template" characterization maps exactly these
    /// names). The COM root <c>Bip01</c> itself, <c>Footsteps</c>, toe segments
    /// <c>Toe01/Toe02</c> and <c>HorseLink</c> carry no aliases and are never mapped.
    /// </summary>
    public static Profile Biped { get; } = BuildBiped();

    /// <summary>
    /// DAZ/Poser classic naming (Poser 4 era figures, DAZ Generation-4 V4/M4, Genesis 1/2,
    /// MakeHuman's "Poser/DAZ names" BVH export — verified against the local
    /// <c>dev/corpus/unknown_rigs/makehuman_cmu_03_03_dazNames.bvh</c>): camel-case bones
    /// with a lower-case <c>l</c>/<c>r</c> side prefix — <c>hip</c> (the translating
    /// root), <c>abdomen[→abdomen2]→chest</c> spine, <c>neck</c>, <c>head</c>,
    /// <c>lCollar→lShldr→lForeArm→lHand</c> arms, <c>lThigh→lShin→lFoot→lToe</c> legs and
    /// <c>lThumb1..3/lIndex1..3/lMid1..3/lRing1..3/lPinky1..3</c> fingers. The
    /// <c>l/rButtock</c> thigh helpers and eye bones carry no aliases and stay unmapped.
    /// DAZ Genesis 3/8/9 renamed the skeleton (<c>abdomenLower</c>, <c>lShldrBend</c>, …)
    /// and is NOT covered by this preset.
    /// </summary>
    public static Profile DazPoser { get; } = BuildDazPoser();

    /// <summary>
    /// Blender Rigify human rigs, per the metarig definition in the rigify add-on
    /// (<c>rigify/metarigs/human.py</c>) and the Blender manual's basic.human reference:
    /// the spine chain is <c>spine→spine.001..spine.006</c> where <c>spine</c> IS the
    /// pelvis/hips bone (it sits at the pelvis and parents the thighs), spine.001–003 are
    /// the torso, spine.004/005 the two neck bones (004 carries <see cref="BoneRole.Neck"/>,
    /// 005 stays unmapped — same policy as ActorCore's NeckTwist02) and spine.006 is the
    /// head. Limbs: <c>shoulder.L→upper_arm.L→forearm.L→hand.L</c>,
    /// <c>thigh.L→shin.L→foot.L→toe.L</c>; fingers <c>thumb.01.L..03.L</c> and
    /// <c>f_index/f_middle/f_ring/f_pinky.01.L..03.L</c>. The <c>^DEF-</c> namespace
    /// pattern also matches rigify's generated deform skeleton (<c>DEF-spine.001</c>,
    /// <c>DEF-upper_arm.L</c>, …); the segmented deform twins (<c>DEF-upper_arm.L.001</c>),
    /// <c>palm.*</c>, <c>pelvis.L/R</c>, <c>heel.02.L</c>, face bones and the generated
    /// ORG-/MCH-/control bones have no aliases and are never mapped.
    /// </summary>
    public static Profile Rigify { get; } = BuildRigify();

    /// <summary>
    /// VRoid Studio / VRM avatars (UniVRM exports): <c>J_Bip_&lt;side&gt;_&lt;Part&gt;</c>
    /// bones where side is <c>C</c> (center), <c>L</c> or <c>R</c> — the standard VRoid
    /// skeleton behind the VRM humanoid spec (vrm-c/vrm-specification, humanoid bone map):
    /// <c>J_Bip_C_Hips/Spine/Chest/UpperChest/Neck/Head</c>,
    /// <c>J_Bip_L_Shoulder→UpperArm→LowerArm→Hand</c>,
    /// <c>J_Bip_L_UpperLeg→LowerLeg→Foot→ToeBase</c>, fingers
    /// <c>J_Bip_L_Thumb1..3/Index1..3/Middle1..3/Ring1..3/Little1..3</c> ("Little" is the
    /// pinky, per the VRM littleProximal/Intermediate/Distal humanoid bones). Secondary
    /// physics/adjust bones (<c>J_Sec_*</c>, <c>J_Adj_*</c>) and the <c>Root</c> bone have
    /// no aliases and are never mapped.
    /// </summary>
    public static Profile Vrm { get; } = BuildVrm();

    /// <summary>
    /// Blender Auto-Rig Pro humanoid FBX exports — bone names verified empirically against
    /// the local user repro <c>dev/corpus/todo/Defenses.fbx</c> (the PunchPerfect family):
    /// <c>.x</c> suffix marks center bones, <c>.l/.r</c> the sides, and the exported limb
    /// deform bones carry the <c>_stretch</c> twin name — <c>root.x</c> is the hips
    /// (under a ground bone <c>root</c>), <c>spine_01.x→spine_02.x→spine_03.x</c>,
    /// <c>neck.x</c>, <c>head.x</c>, arms <c>shoulder.l→arm_stretch.l→forearm_stretch.l→
    /// hand.l</c> (plain "arm", NOT "upperarm"), legs <c>thigh_stretch.l→leg_stretch.l→
    /// foot.l→toes_01.l</c> ("leg" is the calf). Fingers keep Auto-Rig Pro's <c>c_</c>
    /// control prefix on the exported deform chain: <c>c_thumb1.l..3.l</c>,
    /// <c>c_index/c_middle/c_ring/c_pinky1.l..3.l</c>. Leftover finger-tip markers
    /// (<c>mixamorig:LeftHandIndex4</c> in the repro) and <c>root</c> have no aliases.
    /// </summary>
    public static Profile AutoRigPro { get; } = BuildAutoRigPro();

    /// <summary>
    /// Xsens MVN exports (23-segment MVN body model; MVN Animate/Analyze FBX and BVH):
    /// anatomical vertebra names for the spine chain <c>Pelvis→L5→L3→T12→T8</c> (the four
    /// exported lumbar/thoracic segments of the MVN model), <c>Neck→Head</c>, arms
    /// <c>RightShoulder→RightUpperArm→RightForeArm→RightHand</c> and legs
    /// <c>RightUpperLeg→RightLowerLeg→RightFoot→RightToe</c>. Body-suit capture only — no
    /// finger segments (Xsens gloves ship as separate data), so the hands are chain tips.
    /// </summary>
    public static Profile XsensMvn { get; } = BuildXsensMvn();

    /// <summary>
    /// Perception Neuron / Axis Neuron BVH exports: mixamo-like limb and finger names
    /// (<c>RightArm→RightForeArm→RightHand</c>, <c>RightUpLeg→RightLeg→RightFoot</c>,
    /// <c>RightHandThumb1..3</c>) but a FOUR-bone spine (<c>Spine→Spine1..Spine3</c>), no
    /// toe joints (the feet are chain tips), and per-finger <c>RightInHandIndex</c>-style
    /// metacarpal helpers between the hand and the <c>RightHandIndex1..3</c> phalanges.
    /// The InHand metacarpals carry no aliases (barely animated palm helpers; mapping them
    /// as phalanges would shift every curl one joint outward — the SOMA finger bug class).
    /// The extra Spine3 is what lets this preset outscore mixamo on Neuron rigs (and
    /// mixamo's toes keep mixamo ahead on real Mixamo rigs).
    /// </summary>
    public static Profile PerceptionNeuron { get; } = BuildPerceptionNeuron();

    /// <summary>
    /// Source engine ValveBiped skeletons (HL2/GMod humanoids, playermodels):
    /// <c>ValveBiped.Bip01_*</c> names — 3ds-Max-Biped-derived parts behind the fixed
    /// namespace, with underscores and a spine chain that SKIPS Spine3
    /// (<c>Spine→Spine1→Spine2→Spine4</c>), <c>Neck1</c>/<c>Head1</c>, arms
    /// <c>L_Clavicle→L_UpperArm→L_Forearm→L_Hand</c>, legs
    /// <c>L_Thigh→L_Calf→L_Foot→L_Toe0</c> and numbered finger chains
    /// <c>L_Finger0/01/02</c> (0 = thumb) … <c>L_Finger4/41/42</c> (pinky). Evaluated
    /// before <see cref="Biped"/> (same Bip01 ancestry; the plain-Biped preset must never
    /// claim a ValveBiped rig). Attachment/weapon helpers (<c>ValveBiped.forward</c>,
    /// <c>ValveBiped.Anim_Attachment_*</c>) have no aliases and are never mapped.
    /// </summary>
    public static Profile ValveBiped { get; } = BuildValveBiped();

    /// <summary>
    /// DAZ Genesis 3/8(.1) figures: renamed Genesis skeleton (NOT covered by
    /// <see cref="DazPoser"/>) — <c>hip</c> is the translating root and the LCA of the
    /// <c>pelvis</c> leg branch and the <c>abdomenLower→abdomenUpper→chestLower→chestUpper</c>
    /// spine (so <c>hip</c> carries <see cref="BoneRole.Hips"/> and <c>pelvis</c> stays
    /// unmapped, same policy as ActorCore's Hip/Pelvis pair). Neck chain
    /// <c>neckLower→neckUpper→head</c> (neckUpper unmapped, NeckTwist02 policy). Limbs use
    /// Bend/Twist pairs: the Bend bones (<c>lShldrBend</c>, <c>lForearmBend</c>,
    /// <c>lThighBend</c>) are the primary limb bones; the co-linear Twist roll helpers
    /// (<c>lShldrTwist</c>, <c>lForearmTwist</c>, <c>lThighTwist</c>) carry no aliases and
    /// are never mapped. Legs <c>lThighBend→lShin→lFoot→lToe</c> (<c>lMetatarsals</c> is an
    /// arch helper between foot and toe, unmapped). Fingers are the classic DAZ
    /// <c>lThumb1..3/lIndex1..3/lMid1..3/lRing1..3/lPinky1..3</c>. Genesis 9 renamed the
    /// skeleton again (<c>l_upperarm</c>, …) and is NOT covered by this preset.
    /// </summary>
    public static Profile DazGenesis { get; } = BuildDazGenesis();

    /// <summary>
    /// The s&amp;box citizen-family skeleton itself (<c>citizen.vmdl</c>,
    /// <c>citizen_human_*.vmdl</c> and every community model re-rigged on their skeleton):
    /// <c>pelvis</c>, <c>spine_0..2</c>, <c>neck_0</c>, <c>head</c>, reversed-word limb
    /// names <c>arm_upper_L→arm_lower_L→hand_L</c> / <c>leg_upper_L→leg_lower_L→
    /// ankle_L→ball_L</c>, <c>clavicle_L/R</c> and fingers
    /// <c>finger_&lt;name&gt;_{meta,0,1,2}_L</c> (meta = metacarpal, 0/1/2 =
    /// proximal/middle/distal). The alias table mirrors
    /// <see cref="Target.SboxBoneClassifier"/>'s curated role table — kept in sync by a
    /// test. This is what lets a COMPILED s&amp;box model picked as a custom conversion
    /// target (or used as a source) be recognized: the reversed word order
    /// (<c>arm_upper</c>, not <c>upper_arm</c>) defeats generic token matching, which
    /// scored the citizen rig at 5% and made every custom-model pick fail with "not
    /// recognized as humanoid". Twist/helper/IK/face bones (<c>*_twist*</c>,
    /// <c>*_helper*</c>, <c>eye_*</c>, …) have no aliases and are never mapped.
    /// </summary>
    public static Profile Sbox { get; } = BuildSbox();

    /// <summary>
    /// AdvancedSkeleton (Maya auto-rigger, ubiquitous in game rips and mobile-game rigs):
    /// <c>Root_M</c> hips, <c>Spine1_M(→Spine2_M)→Chest_M</c> spine, <c>Neck_M→Head_M</c>
    /// (small rigs parent <c>Head_M</c> straight to the chest with no neck),
    /// <c>Scapula→Shoulder→Elbow→Wrist</c> arms, <c>Hip→Knee→Ankle→Toes</c> legs and
    /// <c>&lt;Name&gt;Finger1..3</c> fingers, all sided <c>_L/_R</c> (center <c>_M</c>).
    /// Twist helpers (<c>ShoulderPart1</c>, <c>HipPart1</c>, …), <c>Cup</c> palm bones and
    /// the face rig carry no aliases. Real case: a Sonic mobile-game rip whose name stage
    /// scored below threshold — the topology fallback then mapped ARMS AND LEGS ONTO THE
    /// HEAD QUILLS (long symmetric chains), playing every clip as garbage.
    /// </summary>
    public static Profile AdvancedSkeleton { get; } = BuildAdvancedSkeleton();

    /// <summary>
    /// EA Sports <i>Fight Night</i> boxer skeleton (Fight Night Champion / Round 4 game
    /// rips — verified against the extracted <c>skeleton_boxer.json</c>, 293 bones). The
    /// joint names are the MotionBuilder/HumanIK convention mixamo also descends from
    /// (<c>Hips</c>, <c>LeftUpLeg→LeftLeg→LeftFoot→LeftToeBase</c>,
    /// <c>LeftShoulder→LeftArm→LeftForeArm→LeftHand</c>), which is why mixamo and
    /// perception_neuron both score in the 0.84 range on these rigs; three things make it
    /// its own preset:
    /// <list type="bullet">
    /// <item>a FOUR-bone spine <c>Spine→Spine1→Spine2→Spine3</c> (mixamo has three) under a
    /// two-bone neck <c>Neck→Neck1</c> — <c>Neck1</c> stays unmapped, the same policy as
    /// ActorCore's NeckTwist02 and rigify's spine.005.</item>
    /// <item>BOXING GLOVES instead of hands: the only real finger joints are
    /// <c>LeftHandThumb1→LeftHandThumb2</c> (two segments, no distal) and the fused
    /// <c>LeftInHandMiddle→LeftHandMiddle1→LeftHandMiddle2</c>. Index, ring and pinky exist
    /// only as glove SHELL bones (<c>LeftHandIndexGlove</c>, <c>LeftInHandRingGlove</c>,
    /// <c>LeftHandPinkyGlove</c>) — mesh helpers, not joints, so they carry no aliases.
    /// Declaring only the roles the rig really has is what lets it reach full optional
    /// coverage (same reason <see cref="Smpl"/> is split from <see cref="SmplX"/>) while
    /// mixamo's 30 declared finger roles drag its score down on the same skeleton.</item>
    /// <item><c>LeftInHandMiddle</c> IS the metacarpal and is mapped to the Meta role
    /// rather than a phalanx — mapping a metacarpal as a phalanx shifts every curl one
    /// joint outward (the SOMA finger bug; see <see cref="SomaBvh"/>). It is genuinely
    /// animated here: the extracted punch packages key it on every clip as the fist.</item>
    /// </list>
    /// The rig also carries a large muscle/jiggle simulation layer (<c>Muscle_*</c>,
    /// <c>*_Jiggle</c>, <c>Offset_*</c>, <c>*_Helper</c>, <c>*Twist*</c>, <c>Gut_*</c>,
    /// <c>Stomach*</c>), a full face rig under <c>Face</c>, and the root chain
    /// <c>Reference→AITrajectory→Hips</c> — none of which carry aliases.
    /// </summary>
    public static Profile FightNight { get; } = BuildFightNight();

    /// <summary>All built-in presets, in detection order (first wins score ties — see
    /// <see cref="SmplX"/> vs <see cref="Smpl"/>; <see cref="ValveBiped"/> is evaluated
    /// before <see cref="Biped"/> and <see cref="DazGenesis"/> before
    /// <see cref="DazPoser"/> within their families). <see cref="FightNight"/> follows
    /// <see cref="Mixamo"/>/<see cref="PerceptionNeuron"/> so those keep any tie on the
    /// HumanIK-named rigs they own (it outscores them outright on a real boxer rig).</summary>
    public static IReadOnlyList<Profile> All { get; } =
        new[]
        {
            Sbox, Mixamo, ActorCoreCc, UeMannequin, XsensMvn, PerceptionNeuron, FightNight,
            RokokoBvh, SmplX, Smpl, SomaBvh, ClassicBvh, ValveBiped, Biped, DazGenesis,
            DazPoser, Rigify, Vrm, AutoRigPro, AdvancedSkeleton,
        };

    // ---------------------------------------------------------------- ea fight night

    private static Profile BuildFightNight()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Hips" },
            [BoneRole.Spine0] = new[] { "Spine" },
            [BoneRole.Spine1] = new[] { "Spine1" },
            [BoneRole.Spine2] = new[] { "Spine2" },
            [BoneRole.Spine3] = new[] { "Spine3" },
            // Neck→Neck1→Head: Neck IS the neck bone; Neck1 stays unmapped.
            [BoneRole.Neck] = new[] { "Neck" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, nameSide) in Sides())
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}Shoulder" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}Arm" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}ForeArm" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}Hand" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}UpLeg" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}Leg" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}Foot" };
            aliases[Role("Toe", roleSide)] = new[] { $"{nameSide}ToeBase" };

            // Gloved hand: a two-segment thumb and one fused middle finger carrying the
            // metacarpal. No distal phalanx anywhere and no index/ring/pinky joints — the
            // *Glove bones of those names are mesh shells and must never be mapped.
            aliases[Role("ThumbProx", roleSide)] = new[] { $"{nameSide}HandThumb1" };
            aliases[Role("ThumbMid", roleSide)] = new[] { $"{nameSide}HandThumb2" };
            aliases[Role("MiddleMeta", roleSide)] = new[] { $"{nameSide}InHandMiddle" };
            aliases[Role("MiddleProx", roleSide)] = new[] { $"{nameSide}HandMiddle1" };
            aliases[Role("MiddleMid", roleSide)] = new[] { $"{nameSide}HandMiddle2" };
        }
        return new Profile("fight_night", new string[0], aliases);
    }

    // ---------------------------------------------------------------- advanced skeleton

    private static Profile BuildAdvancedSkeleton()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Root_M" },
            [BoneRole.Spine0] = new[] { "Spine1_M" },
            [BoneRole.Spine1] = new[] { "Spine2_M" },
            [BoneRole.Spine2] = new[] { "Chest_M" },
            [BoneRole.Neck] = new[] { "Neck_M" },
            [BoneRole.Head] = new[] { "Head_M" },
        };
        foreach (var side in new[] { "L", "R" })
        {
            aliases[Role("Clavicle", side)] = new[] { $"Scapula_{side}" };
            aliases[Role("UpperArm", side)] = new[] { $"Shoulder_{side}" };
            aliases[Role("LowerArm", side)] = new[] { $"Elbow_{side}" };
            aliases[Role("Hand", side)] = new[] { $"Wrist_{side}" };
            aliases[Role("UpperLeg", side)] = new[] { $"Hip_{side}" };
            aliases[Role("LowerLeg", side)] = new[] { $"Knee_{side}" };
            aliases[Role("Foot", side)] = new[] { $"Ankle_{side}" };
            aliases[Role("Toe", side)] = new[] { $"Toes_{side}" };

            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                aliases[Role($"{finger}Prox", side)] = new[] { $"{finger}Finger1_{side}" };
                aliases[Role($"{finger}Mid", side)] = new[] { $"{finger}Finger2_{side}" };
                aliases[Role($"{finger}Dist", side)] = new[] { $"{finger}Finger3_{side}" };
            }
        }

        return new Profile("advanced_skeleton", new string[0], aliases);
    }

    // ---------------------------------------------------------------- sbox

    private static Profile BuildSbox()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "pelvis" },
            [BoneRole.Spine0] = new[] { "spine_0" },
            [BoneRole.Spine1] = new[] { "spine_1" },
            [BoneRole.Spine2] = new[] { "spine_2" },
            [BoneRole.Spine3] = new[] { "spine_3" },
            [BoneRole.Neck] = new[] { "neck_0" },
            [BoneRole.Head] = new[] { "head" },
        };
        foreach (var side in new[] { "L", "R" })
        {
            aliases[Role("Clavicle", side)] = new[] { $"clavicle_{side}" };
            aliases[Role("UpperArm", side)] = new[] { $"arm_upper_{side}" };
            aliases[Role("LowerArm", side)] = new[] { $"arm_lower_{side}" };
            aliases[Role("Hand", side)] = new[] { $"hand_{side}" };
            aliases[Role("UpperLeg", side)] = new[] { $"leg_upper_{side}" };
            aliases[Role("LowerLeg", side)] = new[] { $"leg_lower_{side}" };
            aliases[Role("Foot", side)] = new[] { $"ankle_{side}" };
            aliases[Role("Toe", side)] = new[] { $"ball_{side}" };

            foreach (var (finger, rolePrefix) in new[]
            {
                ("thumb", "Thumb"), ("index", "Index"), ("middle", "Middle"),
                ("ring", "Ring"), ("pinky", "Pinky"),
            })
            {
                aliases[Role($"{rolePrefix}Meta", side)] = new[] { $"finger_{finger}_meta_{side}" };
                aliases[Role($"{rolePrefix}Prox", side)] = new[] { $"finger_{finger}_0_{side}" };
                aliases[Role($"{rolePrefix}Mid", side)] = new[] { $"finger_{finger}_1_{side}" };
                aliases[Role($"{rolePrefix}Dist", side)] = new[] { $"finger_{finger}_2_{side}" };
            }
        }

        return new Profile("sbox", new string[0], aliases);
    }

    // ---------------------------------------------------------------- mixamo

    private static Profile BuildMixamo()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Hips" },
            [BoneRole.Spine0] = new[] { "Spine" },
            [BoneRole.Spine1] = new[] { "Spine1" },
            [BoneRole.Spine2] = new[] { "Spine2" },
            [BoneRole.Neck] = new[] { "Neck" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, nameSide) in Sides())
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}Shoulder" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}Arm" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}ForeArm" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}Hand" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}UpLeg" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}Leg" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}Foot" };
            aliases[Role("Toe", roleSide)] = new[] { $"{nameSide}ToeBase" };

            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                aliases[Role($"{finger}Prox", roleSide)] = new[] { $"{nameSide}Hand{finger}1" };
                aliases[Role($"{finger}Mid", roleSide)] = new[] { $"{nameSide}Hand{finger}2" };
                aliases[Role($"{finger}Dist", roleSide)] = new[] { $"{nameSide}Hand{finger}3" };
            }
        }
        // Both ':' (FBX namespace) and '_' (namespace mangled by some exporters) forms occur
        // in the wild; some Mixamo downloads ship with no namespace at all, which still
        // matches because the aliases are the bare names.
        return new Profile("mixamo", new[] { "^mixamorig[0-9]*:", "^mixamorig[0-9]*_" }, aliases);
    }

    // ---------------------------------------------------------------- actorcore / cc

    private static Profile BuildActorCoreCc()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Hip" },
            [BoneRole.Spine0] = new[] { "Waist" },
            [BoneRole.Spine1] = new[] { "Spine01" },
            [BoneRole.Spine2] = new[] { "Spine02" },
            [BoneRole.Neck] = new[] { "NeckTwist01" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var roleSide in new[] { "L", "R" })
        {
            var nameSide = roleSide; // CC bones use the bare side letter: CC_Base_L_Thigh.
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}_Clavicle" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}_Upperarm" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}_Forearm" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}_Hand" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}_Thigh" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}_Calf" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}_Foot" };
            aliases[Role("Toe", roleSide)] = new[] { $"{nameSide}_ToeBase" };

            foreach (var (role, cc) in new[]
            {
                ("Thumb", "Thumb"), ("Index", "Index"), ("Middle", "Mid"), ("Ring", "Ring"), ("Pinky", "Pinky"),
            })
            {
                aliases[Role($"{role}Prox", roleSide)] = new[] { $"{nameSide}_{cc}1" };
                aliases[Role($"{role}Mid", roleSide)] = new[] { $"{nameSide}_{cc}2" };
                aliases[Role($"{role}Dist", roleSide)] = new[] { $"{nameSide}_{cc}3" };
            }
        }
        return new Profile("actorcore_cc", new[] { "^CC_Base_" }, aliases);
    }

    // ---------------------------------------------------------------- ue mannequin

    private static Profile BuildUeMannequin()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "pelvis" },
            [BoneRole.Spine0] = new[] { "spine_01" },
            [BoneRole.Spine1] = new[] { "spine_02" },
            [BoneRole.Spine2] = new[] { "spine_03" },
            [BoneRole.Spine3] = new[] { "spine_04" },
            [BoneRole.Spine4] = new[] { "spine_05" },
            [BoneRole.Neck] = new[] { "neck_01" },
            [BoneRole.Head] = new[] { "head" },
        };
        foreach (var (roleSide, s) in new[] { ("L", "l"), ("R", "r") })
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"clavicle_{s}" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"upperarm_{s}" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"lowerarm_{s}" };
            aliases[Role("Hand", roleSide)] = new[] { $"hand_{s}" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"thigh_{s}" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"calf_{s}" };
            aliases[Role("Foot", roleSide)] = new[] { $"foot_{s}" };
            aliases[Role("Toe", roleSide)] = new[] { $"ball_{s}" };

            foreach (var (role, ue) in new[]
            {
                ("Thumb", "thumb"), ("Index", "index"), ("Middle", "middle"), ("Ring", "ring"), ("Pinky", "pinky"),
            })
            {
                // UE5 mannequin adds metacarpals for the four fingers (not the thumb).
                if (role != "Thumb")
                    aliases[Role($"{role}Meta", roleSide)] = new[] { $"{ue}_metacarpal_{s}" };
                aliases[Role($"{role}Prox", roleSide)] = new[] { $"{ue}_01_{s}" };
                aliases[Role($"{role}Mid", roleSide)] = new[] { $"{ue}_02_{s}" };
                aliases[Role($"{role}Dist", roleSide)] = new[] { $"{ue}_03_{s}" };
            }
        }
        return new Profile("ue_mannequin", new string[0], aliases);
    }

    // ---------------------------------------------------------------- rokoko / xsens bvh

    private static Profile BuildRokokoBvh()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Hips" },
            // Spine naming varies (Spine, Spine1..Spine4); ordered alias preference plus the
            // used-bone exclusion in the detector shifts the chain up when "Spine" is absent.
            [BoneRole.Spine0] = new[] { "Spine", "Spine1" },
            [BoneRole.Spine1] = new[] { "Spine1", "Spine2" },
            [BoneRole.Spine2] = new[] { "Spine2", "Spine3" },
            [BoneRole.Spine3] = new[] { "Spine3", "Spine4" },
            [BoneRole.Spine4] = new[] { "Spine4" },
            [BoneRole.Neck] = new[] { "Neck", "Neck1" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, nameSide) in Sides())
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}Shoulder", $"{nameSide}Collar" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}Arm", $"{nameSide}UpperArm" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}ForeArm", $"{nameSide}LowerArm" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}Hand" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}UpLeg", $"{nameSide}Thigh", $"{nameSide}UpperLeg" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}Leg", $"{nameSide}Shin", $"{nameSide}LowerLeg" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}Foot" };
            aliases[Role("Toe", roleSide)] = new[] { $"{nameSide}Toe", $"{nameSide}ToeBase" };
        }
        return new Profile("rokoko_bvh", new string[0], aliases);
    }

    // ---------------------------------------------------------------- xsens mvn

    private static Profile BuildXsensMvn()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Pelvis" },
            // MVN's exported spine segments are the anatomical vertebra levels L5/L3/T12/T8.
            [BoneRole.Spine0] = new[] { "L5" },
            [BoneRole.Spine1] = new[] { "L3" },
            [BoneRole.Spine2] = new[] { "T12" },
            [BoneRole.Spine3] = new[] { "T8" },
            [BoneRole.Neck] = new[] { "Neck" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, nameSide) in Sides())
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}Shoulder" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}UpperArm" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}ForeArm" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}Hand" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}UpperLeg" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}LowerLeg" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}Foot" };
            aliases[Role("Toe", roleSide)] = new[] { $"{nameSide}Toe" };
        }
        // Body-suit capture: no finger segments (see the property remarks).
        return new Profile("xsens_mvn", new string[0], aliases);
    }

    // ---------------------------------------------------------------- perception neuron

    private static Profile BuildPerceptionNeuron()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Hips" },
            [BoneRole.Spine0] = new[] { "Spine" },
            [BoneRole.Spine1] = new[] { "Spine1" },
            [BoneRole.Spine2] = new[] { "Spine2" },
            [BoneRole.Spine3] = new[] { "Spine3" },
            [BoneRole.Neck] = new[] { "Neck" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, nameSide) in Sides())
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}Shoulder" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}Arm" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}ForeArm" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}Hand" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}UpLeg" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}Leg" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}Foot" };
            // No toe joints in Axis Neuron exports; the feet are chain tips.

            // Phalanges only: the LeftInHandIndex-style metacarpal helpers between hand
            // and phalanges carry no role (see the property remarks).
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                aliases[Role($"{finger}Prox", roleSide)] = new[] { $"{nameSide}Hand{finger}1" };
                aliases[Role($"{finger}Mid", roleSide)] = new[] { $"{nameSide}Hand{finger}2" };
                aliases[Role($"{finger}Dist", roleSide)] = new[] { $"{nameSide}Hand{finger}3" };
            }
        }
        return new Profile("perception_neuron", new string[0], aliases);
    }

    // ---------------------------------------------------------------- valvebiped

    private static Profile BuildValveBiped()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Pelvis" },
            [BoneRole.Spine0] = new[] { "Spine" },
            [BoneRole.Spine1] = new[] { "Spine1" },
            [BoneRole.Spine2] = new[] { "Spine2" },
            // The stock HL2 chain skips Spine3 (Spine2's child IS Spine4, the chest);
            // ordered preference + used-bone exclusion also absorbs a variant that has
            // both: Spine3→Spine3 and Spine4→Spine4.
            [BoneRole.Spine3] = new[] { "Spine3", "Spine4" },
            [BoneRole.Spine4] = new[] { "Spine4" },
            [BoneRole.Neck] = new[] { "Neck1" },
            [BoneRole.Head] = new[] { "Head1" },
        };
        foreach (var s in new[] { "L", "R" })
        {
            aliases[Role("Clavicle", s)] = new[] { $"{s}_Clavicle" };
            aliases[Role("UpperArm", s)] = new[] { $"{s}_UpperArm" };
            aliases[Role("LowerArm", s)] = new[] { $"{s}_Forearm" };
            aliases[Role("Hand", s)] = new[] { $"{s}_Hand" };
            aliases[Role("UpperLeg", s)] = new[] { $"{s}_Thigh" };
            aliases[Role("LowerLeg", s)] = new[] { $"{s}_Calf" };
            aliases[Role("Foot", s)] = new[] { $"{s}_Foot" };
            aliases[Role("Toe", s)] = new[] { $"{s}_Toe0" };

            // Biped-style numbered finger chains behind the ValveBiped namespace:
            // Finger0 is the thumb; segments append the phalanx digit (Finger0 →
            // Finger01 → Finger02, Finger1 → Finger11 → …).
            foreach (var (finger, n) in new[]
            {
                ("Thumb", 0), ("Index", 1), ("Middle", 2), ("Ring", 3), ("Pinky", 4),
            })
            {
                aliases[Role($"{finger}Prox", s)] = new[] { $"{s}_Finger{n}" };
                aliases[Role($"{finger}Mid", s)] = new[] { $"{s}_Finger{n}1" };
                aliases[Role($"{finger}Dist", s)] = new[] { $"{s}_Finger{n}2" };
            }
        }
        // The fixed "ValveBiped.Bip01_" namespace: anchored, so plain "Bip01 ..." Character
        // Studio rigs never strip it (and the Biped preset's "^Bip\d+[ _]" never matches
        // the ValveBiped prefix — the two families cannot cross-claim).
        return new Profile("valvebiped", new[] { @"^ValveBiped\.Bip01_" }, aliases);
    }

    // ---------------------------------------------------------------- daz genesis 3/8

    private static Profile BuildDazGenesis()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            // "hip" is the translating root and LCA of the pelvis (leg branch) and the
            // abdomen (spine branch); "pelvis" is a leg-branch intermediate and stays
            // unmapped — same policy as ActorCore's CC_Base_Hip/CC_Base_Pelvis pair.
            [BoneRole.Hips] = new[] { "hip" },
            [BoneRole.Spine0] = new[] { "abdomenLower" },
            [BoneRole.Spine1] = new[] { "abdomenUpper" },
            [BoneRole.Spine2] = new[] { "chestLower" },
            [BoneRole.Spine3] = new[] { "chestUpper" },
            // neckLower→neckUpper→head: neckLower IS the neck; neckUpper stays unmapped
            // (same policy as ActorCore's NeckTwist02 / rigify's spine.005).
            [BoneRole.Neck] = new[] { "neckLower" },
            [BoneRole.Head] = new[] { "head" },
        };
        foreach (var s in new[] { "L", "R" })
        {
            var p = s == "L" ? "l" : "r"; // lower-case side prefix: lShldrBend, rThighBend
            aliases[Role("Clavicle", s)] = new[] { $"{p}Collar" };
            // Bend bones are the primary limb bones; the co-linear *Twist roll helpers
            // have no aliases and are never mapped.
            aliases[Role("UpperArm", s)] = new[] { $"{p}ShldrBend" };
            aliases[Role("LowerArm", s)] = new[] { $"{p}ForearmBend" };
            aliases[Role("Hand", s)] = new[] { $"{p}Hand" };
            aliases[Role("UpperLeg", s)] = new[] { $"{p}ThighBend" };
            aliases[Role("LowerLeg", s)] = new[] { $"{p}Shin" };
            aliases[Role("Foot", s)] = new[] { $"{p}Foot" };
            // lMetatarsals sits between foot and toe (arch helper, unmapped).
            aliases[Role("Toe", s)] = new[] { $"{p}Toe" };

            foreach (var (role, daz) in new[]
            {
                ("Thumb", "Thumb"), ("Index", "Index"), ("Middle", "Mid"), ("Ring", "Ring"), ("Pinky", "Pinky"),
            })
            {
                aliases[Role($"{role}Prox", s)] = new[] { $"{p}{daz}1" };
                aliases[Role($"{role}Mid", s)] = new[] { $"{p}{daz}2" };
                aliases[Role($"{role}Dist", s)] = new[] { $"{p}{daz}3" };
            }
        }
        return new Profile("daz_genesis", new string[0], aliases);
    }

    // ---------------------------------------------------------------- smpl / smpl-x

    private static Profile BuildSmpl(bool withFingers)
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Pelvis" },
            [BoneRole.Spine0] = new[] { "Spine1" },
            [BoneRole.Spine1] = new[] { "Spine2" },
            [BoneRole.Spine2] = new[] { "Spine3" },
            [BoneRole.Neck] = new[] { "Neck" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, abbr, word) in new[] { ("L", "L", "left"), ("R", "R", "right") })
        {
            // Both documented spellings per role: abbreviated FBX-rig names ("L_Hip") and
            // spelled model joint names ("left_hip"). Comparison is separator-insensitive.
            aliases[Role("Clavicle", roleSide)] = new[] { $"{abbr}_Collar", $"{word}_collar" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{abbr}_Shoulder", $"{word}_shoulder" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{abbr}_Elbow", $"{word}_elbow" };
            aliases[Role("Hand", roleSide)] = new[] { $"{abbr}_Wrist", $"{word}_wrist" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{abbr}_Hip", $"{word}_hip" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{abbr}_Knee", $"{word}_knee" };
            aliases[Role("Foot", roleSide)] = new[] { $"{abbr}_Ankle", $"{word}_ankle" };
            aliases[Role("Toe", roleSide)] = new[] { $"{abbr}_Foot", $"{word}_foot" };

            if (!withFingers)
                continue;

            // SMPL-X finger joints (left_index1..3 etc., per vchoutas/smplx joint_names.py).
            foreach (var finger in new[] { "thumb", "index", "middle", "ring", "pinky" })
            {
                var name = char.ToUpperInvariant(finger[0]) + finger[1..];
                aliases[Role($"{name}Prox", roleSide)] = new[] { $"{word}_{finger}1" };
                aliases[Role($"{name}Mid", roleSide)] = new[] { $"{word}_{finger}2" };
                aliases[Role($"{name}Dist", roleSide)] = new[] { $"{word}_{finger}3" };
            }
        }
        // Gendered SMPL FBX rigs prefix every bone (m_avg_L_Hip, f_avg_Pelvis).
        return new Profile(withFingers ? "smpl_x" : "smpl", new[] { "^m_avg_", "^f_avg_" }, aliases);
    }

    // ---------------------------------------------------------------- nvidia soma bvh

    private static Profile BuildSomaBvh()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Hips" },
            [BoneRole.Spine0] = new[] { "Spine1" },
            [BoneRole.Spine1] = new[] { "Spine2" },
            [BoneRole.Spine2] = new[] { "Chest" },
            [BoneRole.Neck] = new[] { "Neck1" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, nameSide) in Sides())
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}Shoulder" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}Arm" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}ForeArm" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}Hand" };
            // SOMA's "Leg" is the thigh, "Shin" the calf — the decisive difference from
            // mixamo, where "Leg" is the calf under "UpLeg".
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}Leg" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}Shin" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}Foot" };
            aliases[Role("Toe", roleSide)] = new[] { $"{nameSide}ToeBase" };

            // Mixamo-style finger NAMES but not mixamo segmentation: SOMA fingers have four
            // segments where segment 1 is a metacarpal (measured on the repro BVH: Index1
            // sits 3.2 cm from the wrist at the palm base, then a 6.4 cm metacarpal to the
            // Index2 knuckle, then 3.7/2.3 cm phalanges to Index3/Index4) — so 2/3/4 are the
            // phalanges. Mapping 1..3 as Prox/Mid/Dist (mixamo's segmentation) shifted every
            // curl one joint outward and dropped the distal curl entirely (frozen fingers).
            // The thumb is three segments plus *End, mapped 1..3 like mixamo's; *End tip
            // markers carry no role.
            aliases[Role("ThumbProx", roleSide)] = new[] { $"{nameSide}HandThumb1" };
            aliases[Role("ThumbMid", roleSide)] = new[] { $"{nameSide}HandThumb2" };
            aliases[Role("ThumbDist", roleSide)] = new[] { $"{nameSide}HandThumb3" };
            foreach (var finger in new[] { "Index", "Middle", "Ring", "Pinky" })
            {
                aliases[Role($"{finger}Meta", roleSide)] = new[] { $"{nameSide}Hand{finger}1" };
                aliases[Role($"{finger}Prox", roleSide)] = new[] { $"{nameSide}Hand{finger}2" };
                aliases[Role($"{finger}Mid", roleSide)] = new[] { $"{nameSide}Hand{finger}3" };
                aliases[Role($"{finger}Dist", roleSide)] = new[] { $"{nameSide}Hand{finger}4" };
            }
        }
        return new Profile("soma_bvh", new string[0], aliases);
    }

    // ---------------------------------------------------------------- classic bvh

    private static Profile BuildClassicBvh()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Hips" },
            [BoneRole.Spine0] = new[] { "Chest" },
            [BoneRole.Spine1] = new[] { "Chest2" },
            [BoneRole.Spine2] = new[] { "Chest3" },
            [BoneRole.Spine3] = new[] { "Chest4" },
            [BoneRole.Neck] = new[] { "Neck" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var (roleSide, nameSide) in Sides())
        {
            aliases[Role("Clavicle", roleSide)] = new[] { $"{nameSide}Collar" };
            aliases[Role("UpperArm", roleSide)] = new[] { $"{nameSide}Shoulder" };
            aliases[Role("LowerArm", roleSide)] = new[] { $"{nameSide}Elbow" };
            aliases[Role("Hand", roleSide)] = new[] { $"{nameSide}Wrist" };
            aliases[Role("UpperLeg", roleSide)] = new[] { $"{nameSide}Hip" };
            aliases[Role("LowerLeg", roleSide)] = new[] { $"{nameSide}Knee" };
            aliases[Role("Foot", roleSide)] = new[] { $"{nameSide}Ankle" };
            aliases[Role("Toe", roleSide)] = new[] { $"{nameSide}Toe" };
        }
        return new Profile("classic_bvh", new string[0], aliases);
    }

    // ---------------------------------------------------------------- 3ds max biped

    private static Profile BuildBiped()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "Pelvis" },
            [BoneRole.Spine0] = new[] { "Spine" },
            [BoneRole.Spine1] = new[] { "Spine1" },
            [BoneRole.Spine2] = new[] { "Spine2" },
            [BoneRole.Spine3] = new[] { "Spine3" },
            [BoneRole.Neck] = new[] { "Neck" },
            [BoneRole.Head] = new[] { "Head" },
        };
        foreach (var s in new[] { "L", "R" })
        {
            aliases[Role("Clavicle", s)] = new[] { $"{s} Clavicle" };
            aliases[Role("UpperArm", s)] = new[] { $"{s} UpperArm" };
            aliases[Role("LowerArm", s)] = new[] { $"{s} Forearm" };
            aliases[Role("Hand", s)] = new[] { $"{s} Hand" };
            aliases[Role("UpperLeg", s)] = new[] { $"{s} Thigh" };
            aliases[Role("LowerLeg", s)] = new[] { $"{s} Calf" };
            aliases[Role("Foot", s)] = new[] { $"{s} Foot" };
            aliases[Role("Toe", s)] = new[] { $"{s} Toe0" };

            // Numbered finger chains: Finger0 is the thumb; segment names append the
            // phalanx digit (Finger0 → Finger01 → Finger02, Finger1 → Finger11 → ...).
            foreach (var (finger, n) in new[]
            {
                ("Thumb", 0), ("Index", 1), ("Middle", 2), ("Ring", 3), ("Pinky", 4),
            })
            {
                aliases[Role($"{finger}Prox", s)] = new[] { $"{s} Finger{n}" };
                aliases[Role($"{finger}Mid", s)] = new[] { $"{s} Finger{n}1" };
                aliases[Role($"{finger}Dist", s)] = new[] { $"{s} Finger{n}2" };
            }
        }
        // Biped's leading name is user-editable (Bip01, Bip001, BipTrump, ...).
        // Requiring a trailing separator keeps the bare COM root untouched.
        return new Profile("biped", new[] { @"^Bip[A-Za-z0-9]+[ _]" }, aliases);
    }

    // ---------------------------------------------------------------- daz / poser

    private static Profile BuildDazPoser()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "hip" },
            [BoneRole.Spine0] = new[] { "abdomen" },
            // Poser classic / DAZ Gen4 spine is abdomen→chest; DAZ Genesis 1/2 inserts
            // abdomen2. Ordered preference + used-bone exclusion handles both: without
            // abdomen2 the chest falls back to Spine1 and Spine2 stays unmapped.
            [BoneRole.Spine1] = new[] { "abdomen2", "chest" },
            [BoneRole.Spine2] = new[] { "chest" },
            [BoneRole.Neck] = new[] { "neck" },
            [BoneRole.Head] = new[] { "head" },
        };
        foreach (var s in new[] { "L", "R" })
        {
            var p = s == "L" ? "l" : "r"; // lower-case side prefix: lShldr, rThigh
            aliases[Role("Clavicle", s)] = new[] { $"{p}Collar" };
            aliases[Role("UpperArm", s)] = new[] { $"{p}Shldr" };
            aliases[Role("LowerArm", s)] = new[] { $"{p}ForeArm" };
            aliases[Role("Hand", s)] = new[] { $"{p}Hand" };
            aliases[Role("UpperLeg", s)] = new[] { $"{p}Thigh" };
            aliases[Role("LowerLeg", s)] = new[] { $"{p}Shin" };
            aliases[Role("Foot", s)] = new[] { $"{p}Foot" };
            aliases[Role("Toe", s)] = new[] { $"{p}Toe" };

            foreach (var (role, daz) in new[]
            {
                ("Thumb", "Thumb"), ("Index", "Index"), ("Middle", "Mid"), ("Ring", "Ring"), ("Pinky", "Pinky"),
            })
            {
                aliases[Role($"{role}Prox", s)] = new[] { $"{p}{daz}1" };
                aliases[Role($"{role}Mid", s)] = new[] { $"{p}{daz}2" };
                aliases[Role($"{role}Dist", s)] = new[] { $"{p}{daz}3" };
            }
        }
        return new Profile("daz_poser", new string[0], aliases);
    }

    // ---------------------------------------------------------------- blender rigify

    private static Profile BuildRigify()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            // rigify's "spine" bone sits AT the pelvis and parents both thighs — it is
            // the hips, not a spine link (rigify/metarigs/human.py).
            [BoneRole.Hips] = new[] { "spine" },
            [BoneRole.Spine0] = new[] { "spine.001" },
            [BoneRole.Spine1] = new[] { "spine.002" },
            [BoneRole.Spine2] = new[] { "spine.003" },
            // spine.004 + spine.005 are the two neck bones, spine.006 the head;
            // spine.005 stays unmapped (same policy as ActorCore's NeckTwist02).
            [BoneRole.Neck] = new[] { "spine.004" },
            [BoneRole.Head] = new[] { "spine.006" },
        };
        foreach (var s in new[] { "L", "R" })
        {
            aliases[Role("Clavicle", s)] = new[] { $"shoulder.{s}" };
            aliases[Role("UpperArm", s)] = new[] { $"upper_arm.{s}" };
            aliases[Role("LowerArm", s)] = new[] { $"forearm.{s}" };
            aliases[Role("Hand", s)] = new[] { $"hand.{s}" };
            aliases[Role("UpperLeg", s)] = new[] { $"thigh.{s}" };
            aliases[Role("LowerLeg", s)] = new[] { $"shin.{s}" };
            aliases[Role("Foot", s)] = new[] { $"foot.{s}" };
            aliases[Role("Toe", s)] = new[] { $"toe.{s}" };

            foreach (var (role, rigify) in new[]
            {
                ("Thumb", "thumb"), ("Index", "f_index"), ("Middle", "f_middle"),
                ("Ring", "f_ring"), ("Pinky", "f_pinky"),
            })
            {
                aliases[Role($"{role}Prox", s)] = new[] { $"{rigify}.01.{s}" };
                aliases[Role($"{role}Mid", s)] = new[] { $"{rigify}.02.{s}" };
                aliases[Role($"{role}Dist", s)] = new[] { $"{rigify}.03.{s}" };
            }
        }
        // The generated deform skeleton prefixes every deform bone with "DEF-"; its
        // segmented limb twins ("DEF-upper_arm.L.001") keep their numeric suffix after
        // stripping and therefore never collide with the whole-bone aliases.
        return new Profile("rigify", new[] { "^DEF-" }, aliases);
    }

    // ---------------------------------------------------------------- vroid / vrm

    private static Profile BuildVrm()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "J_Bip_C_Hips" },
            [BoneRole.Spine0] = new[] { "J_Bip_C_Spine" },
            [BoneRole.Spine1] = new[] { "J_Bip_C_Chest" },
            [BoneRole.Spine2] = new[] { "J_Bip_C_UpperChest" },
            [BoneRole.Neck] = new[] { "J_Bip_C_Neck" },
            [BoneRole.Head] = new[] { "J_Bip_C_Head" },
        };
        foreach (var s in new[] { "L", "R" })
        {
            aliases[Role("Clavicle", s)] = new[] { $"J_Bip_{s}_Shoulder" };
            aliases[Role("UpperArm", s)] = new[] { $"J_Bip_{s}_UpperArm" };
            aliases[Role("LowerArm", s)] = new[] { $"J_Bip_{s}_LowerArm" };
            aliases[Role("Hand", s)] = new[] { $"J_Bip_{s}_Hand" };
            aliases[Role("UpperLeg", s)] = new[] { $"J_Bip_{s}_UpperLeg" };
            aliases[Role("LowerLeg", s)] = new[] { $"J_Bip_{s}_LowerLeg" };
            aliases[Role("Foot", s)] = new[] { $"J_Bip_{s}_Foot" };
            aliases[Role("Toe", s)] = new[] { $"J_Bip_{s}_ToeBase" };

            foreach (var (role, vrm) in new[]
            {
                ("Thumb", "Thumb"), ("Index", "Index"), ("Middle", "Middle"),
                ("Ring", "Ring"), ("Pinky", "Little"),
            })
            {
                aliases[Role($"{role}Prox", s)] = new[] { $"J_Bip_{s}_{vrm}1" };
                aliases[Role($"{role}Mid", s)] = new[] { $"J_Bip_{s}_{vrm}2" };
                aliases[Role($"{role}Dist", s)] = new[] { $"J_Bip_{s}_{vrm}3" };
            }
        }
        return new Profile("vrm", new string[0], aliases);
    }

    // ---------------------------------------------------------------- auto-rig pro

    private static Profile BuildAutoRigPro()
    {
        var aliases = new Dictionary<BoneRole, string[]>
        {
            [BoneRole.Hips] = new[] { "root.x" },
            [BoneRole.Spine0] = new[] { "spine_01.x" },
            [BoneRole.Spine1] = new[] { "spine_02.x" },
            [BoneRole.Spine2] = new[] { "spine_03.x" },
            [BoneRole.Neck] = new[] { "neck.x" },
            [BoneRole.Head] = new[] { "head.x" },
        };
        foreach (var s in new[] { "L", "R" })
        {
            var p = s == "L" ? "l" : "r";
            aliases[Role("Clavicle", s)] = new[] { $"shoulder.{p}" };
            aliases[Role("UpperArm", s)] = new[] { $"arm_stretch.{p}" };
            aliases[Role("LowerArm", s)] = new[] { $"forearm_stretch.{p}" };
            aliases[Role("Hand", s)] = new[] { $"hand.{p}" };
            aliases[Role("UpperLeg", s)] = new[] { $"thigh_stretch.{p}" };
            aliases[Role("LowerLeg", s)] = new[] { $"leg_stretch.{p}" };
            aliases[Role("Foot", s)] = new[] { $"foot.{p}" };
            aliases[Role("Toe", s)] = new[] { $"toes_01.{p}" };

            // Exported finger deform bones keep ARP's c_ control prefix (Defenses.fbx).
            foreach (var finger in new[] { "thumb", "index", "middle", "ring", "pinky" })
            {
                var role = char.ToUpperInvariant(finger[0]) + finger[1..];
                aliases[Role($"{role}Prox", s)] = new[] { $"c_{finger}1.{p}" };
                aliases[Role($"{role}Mid", s)] = new[] { $"c_{finger}2.{p}" };
                aliases[Role($"{role}Dist", s)] = new[] { $"c_{finger}3.{p}" };
            }
        }
        return new Profile("auto_rig_pro", new string[0], aliases);
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<(string RoleSide, string NameSide)> Sides()
    {
        yield return ("L", "Left");
        yield return ("R", "Right");
    }

    private static BoneRole Role(string baseName, string side)
        => System.Enum.Parse<BoneRole>(baseName + side);
}
