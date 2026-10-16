# Task specs (P0, caseVersion 1 drafts)

Contract: see `eval/harness/task.py` (Task, Negative, Context) and the worked example `eval/tasks/s01.py`
(green selftest 13/13). Helpers: `eval/harness/checks.py`, `eval/harness/verify.py` (Scorecard.check /
.fraction, Probe, quat_from_euler, quat_close, parse_ref), `eval/harness/uyaml.py` (Unity YAML on disk),
probe findings `eval/probes.md`. Prompts are Chinese; R = ctx.R (object root), A = ctx.A (asset root).
Weights must sum to 10. Negatives must fail EXACTLY their expect set (selftest checks equality).
Negative answers should report actual state honestly unless the negative is about reporting.

Facts that change recipes (from S1 probes):
- `component_get_serialized_properties` does NOT list HingeJoint `m_ConnectedBody` (NextVisible). Read it with
  `component_get_properties` (`connectedBody` = name only). For same-name targets, AFTER all other checks
  (journal already collected) the verifier may rename candidates to unique names via `ctx.c.write("gameobject_rename",...)`
  and then read names — this happens in the run scene only.
- `physics_create_material` fails if the save folder does not exist (defect P8): fixture pre-creates folders.
- Layers: `scriptableobject_set_serialized_property{assetPath:"ProjectSettings/TagManager.asset",
  propertyPath:"layers.Array.data[N]", value}` writes; write "" to restore. Use a free slot found via `editor_get_layers`.
  Restore in `teardown(ctx)` (add the hook if needed; runner calls `F.restore_collision` for `layer_pairs`).
- Use built-in tags only (Respawn, Finish, Player, GameController, EditorOnly) — no tag creation.
- Built-in tags/layers OK; URP: default shader `Universal Render Pipeline/Lit`, color `_BaseColor`, `_Color` is a
  hidden legacy prop (writing it does not error) — trap. `material_set_emission` enables `_EMISSION`.
- `gameobject_duplicate` of a prefab instance loses the prefab link. `prefab_get_overrides` returns counts only
  and excludes m_Name; position offsets count as overrides. Variant asset values: parse the .prefab YAML on disk
  (`uyaml.load`, PrefabInstance `m_Modification.m_Modifications` with `propertyPath`/`value`).
- `gameobject_find` default limit 50 silently truncates; regex is case-sensitive; `batch_query_gameobjects`
  namePattern is case-INsensitive, includeInactive defaults false, queryJson is a JSON *string*.
- Button persistent calls readable: `m_OnClick.m_PersistentCalls.m_Calls.Array.data[i].m_Target` ("id:Type:name"),
  `.m_MethodName`, `.m_Mode` (6 = Bool, 1 = Void), `.m_Arguments.m_BoolArgument`, `.m_CallState` (2 RuntimeOnly).
  `event_add_listener(mode=<CallState>, argType="bool", boolArg)` targets by name; `event_set_listener(index, targetInstanceId)` retargets.
- Script compile: `script_create` returns jobId; `ctx.c.wait_job(id)` then `ctx.c.wait_idle()`; instanceIds survive reload.
- Verifier reads only read-only skills (`ctx.c.read`); fixture/solutions use `ctx.c.write` / `ctx.c.batch`.
- Every task needs a side-effect assertion using `ctx.violations(self)` (+ `ctx.saved_scenes()` = no scene saves)
  and an answer-consistency assertion. Override `allowed_change(ctx, rec)` for permitted ProjectSettings edits
  (e.g. TagManager/DynamicsManager in T3).
- Seeds: `ctx.rng` (random.Random(seed)); same seed must yield identical fixture (determinism check).

---

## S03' Reparent keeping LOCAL pose (basic)
Fixture: R/Warehouse at (2,0,1); Warehouse/Loose at (-3,1,0); Warehouse/Props at (5,2,-1), rot (0,θ,0) θ∈[25,65] seeded,
uniform scale 1.5. Loose/Lamp local pos (0.5,1.2,-0.3), local rot (10,20,0), local scale 0.8, components Light+BoxCollider,
child Bulb local (0,0.4,0). Props has Shelf and Crate; Shelf has a decoy child also named Lamp.
Prompt: move R/Warehouse/Loose/Lamp under R/Warehouse/Props as LAST child; local position/rotation/scale relative to new
parent must equal the old local values (keep local numbers, not world); children/components unchanged; touch nothing else.
Schema: {newPath, localPosition[3], localEulerAngles[3], localScale[3], declaredComplete}
B1 2.0 same id, parent is Props; B2 2.0 m_LocalPosition == C0; B3 2.0 local rotation quat == C0; B4 1.0 m_LocalScale == C0;
B5 1.0 Bulb id + local TRS, Lamp component types/order/enabled unchanged; B6 1.0 (critical) other nodes incl. decoy Lamp unchanged,
Props original children order unchanged and Lamp last; B7 0.5 answer consistent; B8 0.5 journal clean.
Ref: set_parent → set_transform localPosX/Y/Z → component_set_property Transform localEulerAngles "10,20,0" → localScale.
Neg: only_reparent {B2,B3,B4}; world_rot (rotX.. on set_transform) {B3}; recreate {B1,B5}; first_child (sibling 0) {B6};
edit_bulb {B5}; move_decoy (moves Shelf/Lamp instead) → choose expect set from actual run (likely {B1,B6,…}).
Alt: write m_LocalRotation quaternion via component_set_serialized_property.

## S04' Disable one of same-name siblings (basic)
Fixture: R/Hall/Markers with 5 children named Marker (seeded order); exactly one has AudioSource; another is already inactive.
Decoys: Marker_Archive (AudioSource), marker (lowercase, AudioSource), R/Hall/Backup/Marker (AudioSource).
Prompt: disable (activeSelf=false) only the Marker under R/Hall/Markers that has an AudioSource; nothing else changes.
Schema: {disabledInstanceId, disabledPath, declaredComplete}
C1 4.0 target inactive; C2 2.0 other 4 Markers keep C0 active states; C3 1.0 decoys unchanged; C4 1.5 (critical) ids/names/parents/order/components unchanged;
C5 0.5 answer id == target; C6 1.0 journal: only target active change.
Ref: batch_query_components/QG to find AudioSource holder under Markers → gameobject_set_active{instanceId,false}.
Neg: by_name (gameobject_set_active name=Marker hits first) {C1,C2,...}; all {C2}; reactivate_inactive {C2}; rename {C4,…}; archive {C1,C3};
wrong_report {C5}. Alt: gameobject_find{name:"Marker",component:"AudioSource",limit:500} + path filter.

## S06' Backslash nested material path, assign only to target, shared original untouched (basic)
Fixture: R/Display/DisplayCube and ControlCube share A/Shared/PanelBase.mat (URP Lit, _BaseColor 0.5 gray). A/Materials/Nested absent.
Prompt: create material BluePanel, save dir given Windows-style `{A with backslashes}\Materials\Nested`; main color RGBA (0.1,0.4,0.8,1);
assign only to DisplayCube; ControlCube look and PanelBase must not change.
Schema: {materialPath (forward slashes), colorProperty, readback[4], declaredComplete}
D1 2.0 A/Materials/Nested/BluePanel.mat exists & folders registered; D2 2.5 URP Lit + _BaseColor == (0.1,0.4,0.8,1);
D3 2.0 DisplayCube slot0 is new mat; D4 2.0 (critical) PanelBase unchanged & ControlCube still PanelBase; D5 0.5 answer; D6 1.0 no
folder with literal backslash in name, no assets outside A, journal clean.
Ref: material_create{savePath with backslashes} → material_set_color{path, _BaseColor} → material_assign.
Neg: color_on_go (material_set_color on DisplayCube edits shared) {D4,…}; only__Color {D2}; both_cubes {D4}; wrong_folder {D1,D3};
in_memory (no savePath) {D1,D3}. Alt: forward slashes; material_duplicate PanelBase→BluePanel then recolor.

## S08 Read-only regex + component filter (basic)
Fixture: R/Candidates 3 levels; 12 names match ^Enemy_[0-9]{2}$, 8 of them have BoxCollider (incl. one disabled BoxCollider, one inactive
object, a same-name pair Enemy_03 in different subgroups). Decoys: Enemy_7, Enemy_123, enemy_04 (BoxCollider), Enemy_05_old (BoxCollider),
Enemy_06 with BoxCollider on its child, Enemy_08 with MeshCollider, R/Reserve/Enemy_09 (BoxCollider).
Prompt: list descendants of R/Candidates (incl. inactive) whose name fully matches regex (case-sensitive) and that themselves have a BoxCollider
(enabled or not): full path, instanceId, total. Read-only.
Schema: {total, matches:[{path, instanceId}], declaredComplete}
E1 2.5 no misses (fraction); E2 2.5 no extras/dupes; E3 1.5 path↔id correct; E4 0.5 total; E5 1.5 (critical) zero changes (journal empty);
E6 1.0 no write skills (telemetry/transcript: for scripted solvers use journal only).
Ref: compute from fixture facts via read skills. Neg: include_lowercase {E2}; skip_inactive {E1,E4}; swap_ids {E3}; wrong_total {E4};
writes (rename something) {E5,E6}; include_child_collider {E2}. Alt: gameobject_find useRegex + component filter.

## D01 Cross-page inventory (basic)
Fixture: R/Inventory: 63 objects Slot_<seeded suffix> (non-consecutive), depths 4–6, 7 inactive, 2 same-name pairs.
Decoys: slot_lower, OldSlot_9, Slot-12, Slots_3, R/Vault/Slot_Q. Use gameobject_create_batch (≤50 per call).
Prompt: inventory descendants of R/Inventory (incl. inactive) whose name starts with "Slot_" (case-sensitive): path, instanceId, total. Read-only.
F1 3.0 no misses (fraction); F2 3.0 no extras; F3 2.0 path↔id; F4 1.0 total==63; F5 0.5 (critical) zero changes; F6 0.5 no writes.
Neg: first_50 {F1,F4}; include_lower {F2}; skip_inactive {F1,F4}; include_vault {F2}; dedupe_names {F1,F4}; swap_ids {F3}; writes {F5,F6}.

## T1 Turret (scripts + SO + component references) (composite, compile)
Fixture: R/Turrets: 3 same-name Turret at world x −4,0,4 (seeded sibling order), each with child Barrel. R/Targets: 3 same-name Dummy,
exactly one tag Respawn, others Untagged/Player. A/Prefabs/Projectile_<k>.prefab (create GO → prefab_create → delete scene instance);
decoys A/Prefabs/Projectile_<k>_Old.prefab, A/Legacy/Projectile_<k>.prefab. k = seeded token.
Prompt: create two given scripts verbatim in A/Scripts (TurretConfig_<k>: ScriptableObject {float fireRate=1; int burstCount=1; GameObject projectilePrefab}
and TurretAI_<k>: MonoBehaviour {TurretConfig_<k> config; Transform target; float turnSpeed=30}); after compile create
A/Data/MainTurretConfig.asset (fireRate 2.5, burstCount 3, projectilePrefab = A/Prefabs/Projectile_<k>.prefab); add TurretAI_<k> only to the
Turret with largest world X: config = that asset, target = Transform of the Dummy tagged Respawn, turnSpeed 90.
18 assertions (weights in planner draft): script contents ×2, classes loaded, no compile errors, no extra .cs / editor code (critical),
SO path+type, fireRate, burstCount, projectilePrefab path (dependency tree / YAML guid), exactly one TurretAI on right turret, others unchanged,
config ref, target ref by id (serialized m_Target style read of field `target`), turnSpeed, dummies unchanged, prefabs unchanged, journal clean, answer.
Neg: wrong turret; target untagged dummy; legacy prefab; SO wrong path; edited script content; extra MenuItem script; turnSpeed unset; all three turrets.
Alt: component_set_property{assetPath} for config.

## T2 UGUI button wiring (composite, fixture compiles GameController_<k>)
Fixture: GameController_<k> {int startCount; public void StartGame()} script; decoy canvas R/UI/DebugCanvas created first, target R/UI/MenuCanvas;
R/Archive/MenuCanvas (no Canvas comp). Three Menu objects: under MenuCanvas tag Finish, under DebugCanvas Untagged, under Archive Player.
R/Systems/GameController with the component. No EventSystem.
Prompt: StartButton under R/UI/MenuCanvas, TMP text "Start Game", anchors min=max=(0.5,0), pivot (0.5,0), anchoredPosition (0,120), size 320×80;
create R/UI/EventSystem with EventSystem + StandaloneInputModule; onClick persistent listeners in order: Finish-tagged Menu SetActive(false),
then GameController_<k>.StartGame(); both runtime.
20 assertions per planner draft (button unique & parent, components, TMP text, rect values, event system, call count 2, call0 target id/method/mode/bool,
call1 target/method/mode Void(1), call state, decoys unchanged, no extra canvases/buttons, journal, answer).
Neg: button under DebugCanvas; call0 → wrong Menu; order swapped; SetActive(true); two EventSystems; size wrong; call state Off.
Alt: write persistent calls via serialized properties.

## T3 Physics bridge (composite; ProjectSettings: layers + collision pair)
Fixture: free user layer slots L1/L2 named Ev<k>Plank/Ev<k>Guard written into TagManager (restore in teardown); R/Bridge/AnchorLeft cube with
kinematic Rigidbody; R/Bridge/Planks: 5 same-name Plank at world x 1.1..5.5, seeded sibling order ≠ x order; each BoxCollider; the 3rd by x has
SphereCollider BEFORE BoxCollider (cube → remove Box → add Sphere → add Box); decoy R/Bridge/Spare/Plank. Pre-create A/Physics.
Prompt: number planks 1–5 by world X; add Rigidbody(mass 2)+HingeJoint each; plank1 connectedBody = AnchorLeft Rigidbody, plank k = plank k−1;
axis (0,0,1); PhysicMaterial BridgeGrip_<k> (dyn 0.8, static 0.9, bounce 0) at A/Physics/, only on the planks' BoxColliders; set planks' layer
(not children) to L1; disable collision L1×L2 only (this setting change is allowed).
20 assertions per planner draft; connectedBody identity: post-hoc unique rename then component_get_properties.
Neg: physics_set_material (hits Sphere on plank3); chain by sibling order; collision toggled wrong pair; recursive layer; Spare edited;
material created twice ("… 1"); axis (1,0,0).

## T4 Multi-slot material + shared trap (composite)
Fixture: A/Shared/BodyPaint.mat (_BaseColor 0.2 gray), A/Shared/Trim.mat; R/Showroom/Car and CarTwin cubes, both m_Materials [BodyPaint, Trim]
(Array.size=2 via component_set_serialized_property); decoy A/Legacy/GlowOld.mat.
Prompt: ① Car body color (URP main color) → (0.8,0.1,0.1,1) without changing CarTwin, BodyPaint or Trim; ② new emissive URP Lit Glow_<k> at
A/Materials/Glow_<k>.mat, emission on, HDR (2,1,0), replace Car slot 1. Car keeps 2 slots.
17 assertions per planner draft (slot count, slot0 new saved asset with color/shader/under A, BodyPaint unchanged critical, Trim unchanged,
twin slots, slot1 Glow (name+unique+journal objectReference.path), glow path/shader, _EMISSION, _EmissionColor, giFlags, Car preserve,
other assets, journal, answer).
Neg: recolor BodyPaint; material_assign Glow (slot 0); 3 slots; keyword off; Glow in memory; twin edited; _Color only.

## T5 Prefab variant replacement (composite)
Fixture: A/Prefabs/Crate.prefab (cube, BoxCollider, Rigidbody mass 5, child Label); instances a,b,c under R/Depot/Row with children order
[Pallet, a, b, Pallet2, c]; Row at (3,0,2) rot (0,15,0); crates world rot Y 30/75/120 seeded; different scales; b overrides mass 9, scale 1.2.
Prompt: create variant A/Prefabs/Crate_Heavy.prefab with root Rigidbody mass 50 in the asset; replace the Crate with world rot Y≈75° by a
Crate_Heavy instance with same world pos/rot/scale, parent, sibling index, name "Crate"; delete old; never apply others' overrides; Crate.prefab unchanged.
16 assertions per planner draft (variant mass via YAML, base unchanged critical via disk hash + guid, old gone, sibling index, prefabPath, TRS, …).
Neg: mass as instance override; apply b; new instance at end; rotation not copied; duplicate instead of instantiate; old not deleted; wrong crate.

## T7 Large-set incremental repair (composite)
Fixture: R/Yard rows A–F, ~120 objects; 73 match ^Crate_[A-F][0-9]{2}$ (case-sensitive), 9 inactive (6 self, 3 parent); 21 lack BoxCollider
(6 have Sphere/Mesh collider instead), 2 have disabled BoxCollider; 30 have Rigidbody, 11 with mass≠4.5; two Crate_B07 in different rows;
12 decoys (crate_A01, Crate_a01, Crate_G01, Crate_A1, Crate_A012, Crate_A01_old, CrateA01, Crate-A01, …) and R/Archive/Crate_A01.
Prompt: per planner (add BoxCollider only where none, fix mass only where Rigidbody exists, report addedBoxCollider ids, fixedMass ids, counts).
15 assertions per planner draft with fraction-scored coverage. Journal must be collected during the run (Collector already polls).

## T9 Field rename keeping serialized values (composite, compile)
Fixture: A/Scripts/Patrol_<k>.cs {float speed=1; Transform waypoint; int laps=1}; on R/Guards/Guard_1..3 with speed 3.25/7.5/11.125,
waypoint → R/Waypoints/W1..W3, laps 2/3/4.
Prompt: rename speed→moveSpeed, waypoint→patrolTarget keeping class/other fields; existing values must survive without re-setting them via skills.
16 assertions per planner draft (FormerlySerializedAs present, values preserved by id, no manual property_changed on those fields (journal),
no other scripts/editor code critical, compile ok). Neg: rename without attribute; rename + manual reset; delete & recreate script;
compile error; attribute typo; editor helper script. Alt: edit the file + asset_refresh (allowed only inside A).

## T10 Asset reorganisation keeping GUIDs (composite)
Fixture: A/Incoming/{Batch01,Batch02,Vendor/Old,Misc} with 35 URP materials (seeded names; Brick.mat in Batch01 and Batch02; A/Materials/B/Bolt.mat
pre-exists and Incoming/Vendor/Old/Bolt.mat collides; some lowercase initials; no case-only collisions); Misc/Grip.physicMaterial (non-material);
decoy A/Elsewhere/Brick.mat; R/Props with 12 renderers referencing 10 of the materials (some in slot 1).
Prompt: move all materials under A/Incoming into A/Materials/<UPPER first letter>/, collisions → _2, _3… processing in ordinal order of original
full path; keep GUIDs and renderer references; don't move non-materials; delete Incoming folders that became empty (Incoming too if empty);
report mapping.
15 assertions per planner draft (GUID set, guid→expected path, collision naming, renderer refs unchanged, folders, Bolt untouched, journal in A only critical).
Neg: copy+delete; overwrite Bolt; move Grip; leave empty Batch01; delete non-empty Incoming; wrong order; move Elsewhere.
