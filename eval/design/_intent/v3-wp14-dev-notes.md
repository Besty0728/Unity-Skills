# WP14 dev half: T1, T2, T9 to the v3 pack (wp11 notes)

Owner: wp11 (Betsy). Dev worktree only (`/Users/betsy/CodeSpace/Unity-Skills-v3`); the gate worktree is never edited.
Target: track `dev`, tier `standard`, caseVersion 2, `requires {"pipeline": "URP", "mode": "bypass", "dedicated": true,
"packages": []}`. Same conventions as the basic tasks: bilingual numbered R, every assertion on an R or a G, run-scene
save allowed (G2) plus a `save_run_scene` alternative, >= 5 negatives, ids through `V.object_id` / `V.address`.

## Runtime verification (SCENARIO) facts, from the code

- Runner (`usk_eval/attempt.py` `_run_scenario`, S9): after the static `verify`, `SCENARIO` (dict or `callable(ctx)`,
  ctx = VerifyContext, so handles, C0 and the probe are available) is sent as `editor_run_scenario(steps=json,
  executionMode, timeoutSeconds)` by the runner client; the job is awaited for timeout + 120 s; `ctx.scenario` =
  `{status, passed, error, interrupted, executionUncertain, steps: [{index, kind, skill, expected, actual, tolerance,
  passed}], compilation, logWindow}`; then `verify_runtime(ctx, card)`. A rejected start (SkillError) arrives as
  `status "rejected"`; transport failures make the attempt invalid (`scenario_infra`).
- Plugin (`RuntimeScenarioService.cs`): 1-100 steps, kinds invoke/read/assert/wait_frames/wait_seconds/wait_condition,
  at least one assert or wait_condition; invoke only `component_invoke_method`; read/assert/wait_condition skills must
  be ReadOnly. A step whose skill answers an error ends the scenario ("Step refused or failed"). The first failed
  assert ends it too, so later steps do not run: grade several facts from `read` steps in `verify_runtime`, keep the
  mandatory assert trivially true (`editor_get_state` / `isPlaying` == true).
- `assert` without `skill` checks the previous step's result; `select` is a Newtonsoft JSONPath; object comparison
  needs identical key counts, so select scalars.
- `passed` is true only if every assert passed AND the log window is complete AND has no Error/Exception/Assert
  entries AND no compilation is running. So `passed` doubles as "no runtime errors in Play Mode".
- Step args are static (no references to earlier step results): the SCENARIO callable resolves targets from the
  edit-mode scene first (e.g. the component holder found by verify) and passes their ids.
- `component_invoke_method` needs the editor already in the scenario's mode (the scenario enters Play first); it
  re-binds the target at execution, so edit-mode instanceIds are expected to address Play-mode objects. Unverified:
  confirm in the first live run that edit-mode instanceIds resolve in Play Mode.
- Unity 6000.4+: since 30a6f58f the core skills take a native `entityId` (component_* incl. `referenceEntityId`,
  component_invoke_method, gameobject_*, ui_*, editor_playmode_inspect), so same-name siblings are addressable
  by id there too. T1's solutions now pass `V.address(target, "reference")`; T1, T2 and T9 are tested offline on 6000.4 ids.

## Dev-track rules applied

- G4 [dev] allows C# under A/Scripts. `script_create` imports are journaled with source "editor", so
  `ctx.direct_writes()` lists them: the G4 assertion ignores `.cs` / `.cs.meta` under A and flags every other file
  written around the skills. Scripts outside A/Scripts are judged by the task's own "no other code" requirement.
- Static checks first (verify), runtime facts in one runtime assertion per task (verify_runtime).
- Lint rule 6 relaxed: private verifiers may read files (scripts, YAML); write-mode open and filesystem mutation
  calls stay forbidden (contract 5.5 forbids writing, not reading).

## T1 port (status: ready for the first live run, 2026-10-17)

Files: `SkillsForUnity/Evaluation~/pack/tasks/T1/{task.py, prompt.zh-CN.md, prompt.en.md}`, `eval/pack/private/T1.py`,
`eval/pack/solutions/T1.py`; manifest entry dev/standard, seed 7607 (v1 public seed), minutes 40.

- Fixture as v1 with neutral names: decoy prefab folder `Legacy` -> `Common`, `R/Targets` -> `R/Range` (the field
  is called `target`, so the fixture avoids the word). Handles hold ids and paths only: turrets/barrels/dummies (ids,
  sibling order), the two script paths (class names = file names, carry the seeded token + run id), config asset
  path, the required prefab path and the sorted list of all three prefab paths. The turret to wire (largest world X)
  and the Respawn dummy are derived by the private verifier from C0 (`main_turret`, `respawn_dummy`).
- C0: go_state of turrets, barrels, dummies; sha + meta GUID of the three prefabs; the list of `.cs` files under Assets.
- Prompt: R1 scripts verbatim (code passed through placeholders `{cfg_src}` / `{ai_src}` as fenced blocks, so no
  braces in the template), R2 config asset, R3 component on the largest-X turret, R4 no other code, R5 nothing else,
  R6 Play Mode without errors and runtime values kept, R7 answer.
- Assertions (weights): B1 .75 R1 · B2 .5 R1 · B3 1 R4 crit · B4 .75 R2 · B5 1 R2 · B6 .75 R2 · B7 .75 R3 · B8 .5 R3 ·
  B9 1 R3 · B10 .5 R3 · B11 .5 R5 crit · B12 .25 G1,G2 crit · B13 .5 R7,G8 · B14 1 R6 (runtime) · B15 .25 G4.
  v1 -> v3: B1-B13 kept (v1 B12 lost its "no scene save" part), B14 runtime and B15 G4 are new; weights moved to B14.
- SCENARIO: wait 2 frames, read `component_get_serialized_properties` of the TurretAI holder (found statically:
  the main turret when it holds one, else the first holder; the main turret when nobody does), mandatory assert
  `editor_get_state.isPlaying == true`. B14 = scenario passed (no error logs) + runtime config MainTurretConfig of the
  config class + target a Transform + turnSpeed 90. Target identity stays static (B9).
- Negatives (12): wrong_turret {B7}, untagged_target {B9}, other_prefab {B6}, config_wrong_path {B4,B5,B6,B8},
  edited_script {B1}, editor_helper {B3}, turnspeed_unset {B10,B14}, all_three {B7}, moved_dummy {B11},
  wrong_report {B13}, outside_root {B12}, direct_file {B15}. Alternatives: property_setters, save_run_scene.
- Offline: test_pack_dev_tasks (model scripts/SO/prefabs/scenario) and test_pack_basic_flow (through the real runner:
  start dev/standard on a dedicated URP fake project, scenario job served at /jobs) are green.
- To confirm in the first live run: (1) the edit-mode instanceId addresses the Play-mode component in the scenario's
  read step; (2) `passed` is true for the reference on the URP editor (complete log window, no error logs);
  (3) script_create imports arrive as editor-source file records (exempt from B15, in scope for B12);
  (4) timing per case (two compiles + Play entry/exit + teardown recompile).

## T2 port (status: offline green, 2026-10-17)

Files: `pack/tasks/T2/{task.py, prompt.zh-CN.md, prompt.en.md}`, `eval/pack/private/T2.py`, `eval/pack/solutions/T2.py`;
manifest dev/standard after T1, seed 7708 (v1), minutes 40, `packages ["com.unity.ugui"]` (Button/Canvas/EventSystem;
TMP lives in ugui on 6000.x; 2022.3 also needs com.unity.textmeshpro, which the URP template has).

- Fixture as v1 (GameController_<k> compiled at build, DebugCanvas created first, R/Archive/MenuCanvas without a Canvas,
  three Menu objects tagged Untagged/Finish/Player in a seeded order, no EventSystem). Handles: ids and paths only; the
  Finish menu is derived from the C0 tags (`finish_menu`). C0 compares go_state without world position/rotation plus
  local TRS (a Screen Space canvas follows the Game view size).
- t2-verifier-fix applied: D3 is existential over every TextMeshProUGUI below the button; `wrong_text` negative,
  `later_matching_tmp` alternative.
- Assertions D1-D13 as v1 (D12 lost its "no scene save" part), D14 runtime (R6), D15 G4. Answer key
  `listeners[].gameObjectInstanceId` (v1's targetInstanceId is on the role blacklist).
- SCENARIO: wait 2 frames, invoke the private `UnityEngine.UI.Button.Press` on the graded button through
  component_invoke_method (Bind includes private methods), read the three menus (gameobject_get_info.isActive) and the
  controller's startCount, isPlaying assert. D14 = passed + only the Finish menu inactive + startCount 1.
- Negatives (16): debug_canvas {D1}, wrong_menu {D8,D14}, order_swapped {D8,D9}, set_active_true {D8,D14},
  two_event_systems {D6}, no_image {D2}, default_size {D5}, centre_anchors {D4}, start_twice {D7,D14},
  extra_canvas {D11}, editor_and_runtime {D10}, listeners_off {D10,D14}, wrong_text {D3}, outside_root {D12},
  direct_file {D15}, wrong_report {D13}. Alternatives: serialized_calls (referencePath, works on 6000.4),
  later_matching_tmp, save_run_scene.
- Live prerequisites: Active Input Handling must include the old Input Manager (StandaloneInputModule throws every
  frame under "Input System Package (New)" only, which fails D14 for everyone); TMP Essential Resources imported.
  To confirm live: Press() invocation through the scenario, instanceIds valid in Play Mode (as T1).

## T9 port (status: offline green, 2026-10-17)

Files: `pack/tasks/T9/...`, `eval/pack/private/T9.py`, `eval/pack/solutions/T9.py`; dev/standard after T2, seed 8213.

- Fixture as v1 (Patrol_<k> and PatrolRoute_<k> compiled at build; waypoint references set by referencePath, so the
  build works on 6000.4). C0: go_state of guards/waypoints/Route, per Guard {speed, laps, waypoint owner}, Route's
  serialized values, the Patrol GUID, [path, sha] of every .cs.
- Prompt states what v1 left implicit: defaults kept (R1), data saved under the old names keeps loading, also for
  scenes/prefabs opened later (R2, grounds the FormerlySerializedAs check), no skill writes of the four field names
  (R3), edit the file in place / GUID (R4).
- Assertions C1-C11 from v1 (C9 split: objects/Route vs scope C10), C12 runtime, C13 G4. C7 reads
  `ctx.write_records()` (no Play Mode or runner records) and only property_changed from REST on the Patrol class.
- SCENARIO: reads every Guard's component in Play Mode. While the last compilation failed Unity refuses Play Mode, so
  the scenario is then one edit-mode isPlaying assert that fails at once instead of waiting 90 s (suggested for T1).
- Negatives (14): no_attribute {C2,C4,C5,C12}, rename_and_reset {C2,C7}, claims_preserved {C2,C4,C5,C11,C12},
  compile_error {C3,C4,C5,C11,C12}, attribute_typo {C2,C4,C12}, awake_reset {C12} (runtime only), default_changed
  {C1}, laps_changed {C6,C12}, editor_helper {C8}, route_renamed_too {C8,C9}, moved_waypoint {C9}, outside_root {C10},
  direct_file {C13}, wrong_report {C11}. Alternatives: edit_file_and_refresh, literal_edits, save_run_scene.
- G4 assertions of T1/T2/T9 all exempt `.cs(.meta)` anywhere under A (script_create imports are editor-source);
  code outside A/Scripts is judged by the task's own "no other code" requirement.
