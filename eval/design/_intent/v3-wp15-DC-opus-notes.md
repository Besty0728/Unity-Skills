# WP15 — DC1 / DC3 / DC5 / DC7 design notes (wp15-opus, Claude Opus 5.5)

Status 2026-10-18: drafted offline in the dev worktree `eval-v3`, nothing committed, nothing live yet. All four are
dev / challenge, caseVersion 1, `requires {"pipeline": "URP", "mode": "bypass", "dedicated": true, "packages": [...]}`,
`needs_focus = True`. Offline tests: `eval/tests/runner/test_pack_dev_dc.py` on `fake_dev_dc.py` (pack_offline
checklist + SCENARIO on the model + verify_runtime, plus four extra seeds per task as reserved-seed stand-ins).
Conventions as DB/DS/T1/T9: class names carry a seeded word and the run id, SCENARIO is a callable returning
`{"executionMode": "play", "timeoutSeconds": N, "steps": [...]}` ending with the `editor_get_state.isPlaying` assert,
facts are graded from `read`/`invoke` steps in `verify_runtime`, a failed compilation turns the scenario into a single
edit-mode assert (Unity refuses Play Mode with compile errors), `passed:false` zeroes the runtime assertion, G4 = the
dev rule (`direct_writes()` minus `.cs(.meta)`), negatives' C# carries `// usk-fake: <variant>` for the offline model.

## DC5 — pause menu (concept D5; reviewed and revised from the earlier draft)

Fixture: `R/<Canvas>` (world-space canvas, seeded name) > `<Panel>` (inactive panel); `R/<Archive>/<Host>` and
`R/<Systems>[/<Group>]/<Host>`: two empty objects with the same name, the prompt names the second by path. The seed
picks every name, whether the named host sits one level deeper, and whether the canvas comes before or after the two
groups (the archive group always precedes the named one, so a lookup by name can hit the wrong host). Class
`PauseController_<word>_<runId>`; the agent creates the script, both buttons (UI skills), the component and two
persistent listeners. 35 minutes, `packages ["com.unity.ugui"]`.

Revision against the draft:
- BOM on all three .py files; prompts state the serialized-field rule as "(public, or marked [SerializeField])" (the
  verifier always accepted both), "The project must compile without errors" (P2's requirement), and R2 now says
  TogglePause follows the actual state "whichever call brought it into its current state" (a stale private flag left
  by Resume() was graded nowhere and implied nowhere before).
- Runtime sequence extended: start, pause x3, resume, pause, resume = 7 readings (the draft never pressed pause after
  resume and never read after the third pause click); R7 lists the clicks exactly. The scenario ends with
  `Time.set_timeScale(1)` so a negative that leaves the game paused does not leak timeScale 0 into later cases (if
  Unity resets timeScale on Play entry anyway, the step is harmless).
- Invocations use `UnityEngine.UI.Button` (a bare `Button` is ambiguous for ComponentInvocationService.ResolveType:
  `UnityEngine.UIElements.Button` has the same short name; DB5 still uses the bare name — flagged to the lead).
- Buttons are located twice: strictly (direct child of canvas / panel) for P4, leniently (the unique Button object of
  that name anywhere under the canvas) for P5, P9 and the scenario, so a misplaced button costs P4 only.
- P9 checks the answer against the actual state (the object that really carries the component), not the named host;
  `on_archive` therefore fails P3/P5/P6 and not P9. The draft's expected sets for `on_archive` ({P3,P6}) and
  `resume_on_canvas` ({P4}, which would also have failed P5/P9/P10 with the strict lookup) were wrong.
- C0 keeps the systems subtree too (host compared without its component list, canvas/panel without children).
- Rule-7 warning fixed: `field_renamed` fails P1 (and P3), `compile_error` fails P2 (and P3, P5, P10).

Assertions: P1 1.0 R1 script shape · P2 0.5 R1 compiled · P3 1.25 R3 one holder on the named host, panel ref · P4 0.75
R4 placement · P5 1.5 R5 one persistent listener per button, right method/target/state · P6 0.75 crit R6 · P7 0.5 crit
G1/G2 · P8 0.25 G4 · P9 0.5 R8/G8 · P10 3.0 R2/R7 fraction of the 7 readings (0 when the scenario did not pass).

Negatives (16) and why each fails exactly its set: runtime_listener {P5} (AddListener in Start: serialized onClick of
the pause button empty, behaviour still right); on_archive {P3,P5,P6} (component and listeners on the same-named
object; behaviour right; answer truthful); swapped_methods {P5,P10}; panel_unset {P3,P10} (null-guarded, panel never
shows); paused_start {P10} (first reading); timescale_only {P10}; stale_flag {P10} (third and fifth pause clicks
resume instead); resume_unwired {P5,P10}; resume_on_canvas {P4} (lenient lookup keeps the rest green); panel_activated
{P6,P10} (initial reading); field_renamed {P1,P3}; compile_error {P2,P3,P5,P10}; extra_script {P6}; wrong_report
{P9}; outside_root {P7}; direct_file {P8}. Alternatives: by_ids ([SerializeField] private field, listeners by
targetEntityId/targetInstanceId, EditorAndRuntime), save_run_scene.

Not verifiable offline (first live run): `Button.OnSubmit(null)` through component_invoke_method on a button inside an
inactive panel (expected: Press() returns early, no exception); the static `UnityEngine.Time.get_timeScale` /
`set_timeScale` invocations in Play Mode (ResolveType must find exactly one `UnityEngine.Time`); whether timeScale set
in Play Mode survives Play exit (the cleanup step makes this moot for completed scenarios; a scenario that stops early
can still leave 0 behind if Unity keeps it); edit-mode instanceIds addressing Play-mode objects (as T1).
Expected: 15–25 minutes for a strong agent; pass rate 35–60% (same-named host, persistent wiring by id/path, stale
toggle flag).

## DC1 — inventory stacking bug fix (concept D1; public draft revised, private + solutions new)

Fixture: `A/Scripts/Inventory_<w>_<run>.cs` (the buggy inventory) and `InventoryWatcher_<w>_<run>.cs` (counts the
Changed event in `notifications`), compiled at build with script_create_batch; `R/<Area>[/<Shelf>]/<Box>_<w>` (cube)
carries both components; one or two prop cubes next to it. Scene values from `spec(seed)` (public and a verbatim
private copy, pinned by a test over 300 seeds): maxStack 12/15/20, slotCount 4/5, initial stacks `[main, 3|5|7]`
plus, for half the seeds, a second item before or after it; two item names the scene does not hold. Code defaults are
the prompt example's 10/3, so "fixed for the example" and "reset to defaults" both show. The seed also picks every
name, the box depth and the object order; class names carry the run id (no residue check needed any more).
30 minutes.

Changes against the draft: run id in class names; the prompt splits R1 (in place: path + GUID, compiles) from R2
(public API exact: names, parameter types and order, return types, `event` stays an event, no other public member,
private members allowed) and states CountOf (R5), Remove's no-stack case and that new stacks go to the end (R3); R6
says the check uses the stored maxStack/slotCount/stacks; R8 says every call is followed by state and counter reads.
The draft's `_residue` check and the unused `{scripts}` placeholder are gone.

Bugs planted (original source): Add merges into the first stack without the cap and creates at most one uncapped new
stack; counts of 0 or less are accepted (and raise Changed; Add(x, 0) on a new item even creates an empty stack);
Remove takes from the first stack only and never raises Changed. CountOf is correct.

Runtime (K6 Add, K7 Remove + CountOf, each a fraction, 0 unless the scenario passed): `plan(ctx)` picks 13 calls on the
reference trajectory so each one exercises its rule (top-up + new stack, Add 0, Add -4, top-up of the second stack,
fill every free slot with leftover, full inventory returns everything, top-up with leftover, partial removal from the
back, a removal spanning two stacks, removals of nothing and of 0, removal of every stack of an item, new stacks after
removals) plus 3 CountOf calls; a test pins the intended effect of every call over 200 seeds. Every Add/Remove is
followed by a read of the inventory and of the watcher (46 steps), and each call is graded against the state read just
before it with the stored maxStack/slotCount: return value, stacks after, Changed delta. So a wrong Add does not spoil
the Remove checks, and a runtime override of maxStack does not move the yardstick.

Static: K1 0.75 R1 (GUID, single holder = the box, watcher still there) · K2 1.0 R2 (`script_analyze`: fields exactly
{maxStack:int, slotCount:int, stacks:List<Stack>}, no public properties, methods exactly CountOf/Add/Remove with
(string[,int]) → int; source has `public event Action Changed` and the [Serializable] nested Stack) · K3 0.5 R1
compiled · K4 0.75 crit R7 (watcher content + GUID, no added .cs) · K5 0.75 crit R7 (stored inventory values, watcher
component values, every object state) · K6 3.0 · K7 2.25 · K8 0.5 R9/G8 (answer vs the actual holder) · K9 0.25 crit
G1/G2 · K10 0.25 G4.

Negatives (16): overflow_merge {K6}; negative_add {K6} (only 0 refused: -4 drains the second stack); example_only
{K6} (cap 10); remove_silent {K7}; remove_front {K7} (Remove(main, 3) on [M, M] leaves [M-3, M]); event_per_stack
{K6,K7}; signature_changed {K2,K6,K7} (Add(string, uint): the (string, int) invoke cannot bind, scenario stops);
event_as_field {K2} (watcher still compiles, behaviour unchanged); watcher_edited {K4}; scene_values_changed {K5,K6}
(maxStack 10 stored); compile_error {K3,K6,K7} (old assembly stays loaded, so K1/K2 hold); recreated {K1,K5,K6,K7}
(delete + create: new GUID, the scene component goes missing); extra_script {K4}; wrong_report {K8}; outside_root
{K9}; direct_file {K10}. Alternatives: rewrite_file (whole file written to disk + asset_refresh), private_helpers,
save_run_scene.

Not verifiable offline: the serialized reader's values for `stacks.Array.size` / `.item` / `.count` in Play Mode
(assumed int / string as in edit mode); `script_analyze` type names (`List<Stack>`, `int`, `string`) as read from
PerceptionSkills.GetFriendlyTypeName; that the recreated script really gets a new GUID and leaves a missing component
(AssetDatabase.DeleteAsset removes the .meta); 46 steps with 16 child invocation jobs within 180 s; component_invoke_method
with `parameterTypes ["System.String","System.Int32"]` resolving through ResolveType. Expected: 15–25 minutes; pass
rate 30–55% (exact Add top-up order, Remove from the back, event once per call, the example trap).

## DC7 — asmdef repair (concept D8; public draft reworked, private + solutions new)

Runner question (compile errors at issue / freeze), from `usk_eval/session.py` `editor_checks`, `environment.busy`,
`rest.wait_idle` and `attempt._run_scenario`: N1 refuses only a *busy* editor (isCompiling / isUpdating /
domainReloadPending, waited up to 90 s), N5 and S2–S9 only `wait_idle` on the same flags; compile *errors* are never
checked. So a fixture that leaves the run folder's own assemblies broken is supported: the plugin and every other
assembly stay loaded (Unity skips the domain reload while any assembly fails), the agent's fix reloads the domain,
S9 starts the scenario only when not compiling (with errors left, Unity refuses Play Mode, so SCENARIO turns into the
single edit-mode assert, T9 pattern), and S11 deletes the run folder, which removes the broken assemblies; the next
N1 waits out that compilation. The build confines the errors: everything lives under `A/Scripts/<Game>/` behind
.asmdef files, so only the gameplay assembly fails (Combat and Report), which a model test pins. Caveats for live:
while the errors last, no other script change in the project loads (intended here); an editor restart mid-attempt
with errors may open Safe Mode; the build does not assert that compilation failed (lastCompilation read right after
the refresh could be stale), the offline model pins it instead.

Fixture (files written to disk, one asset_refresh, job + wait_idle): `<Core>/<Token>.<Core>.asmdef` + `Stats_<k>.cs`
(hp), `<Data>/<Token>.<Data>.asmdef` + `Tuning_<k>.cs` (const factor), `<Play>/<Token>.<Play>.asmdef` (no references)
+ `Combat_<k>.cs` (power = hp x factor in Awake) and `<Play>[/<Middle>]/<Tools>/Report_<k>.cs` (MenuItem, uses
Stats). Two references are missing, and the prompt does not name them (the agent reads the compile errors). Scene:
`R/<Arena>` > fighter + one more object, seeded order. hp 8/10/12/14 and factor 2..5 from `spec(seed)` (verbatim copy
in the private module, pinned by a test); every folder/assembly/class/object name, the extra folder level and the
order from the seed. The asmdef template is text (its keys would hit the role blacklist as dict keys; C0 keeps the
asmdefs as JSON text for the same reason). `#if` guards: explicitly not allowed (R3), and R4 forbids content changes.
40 minutes.

Changes against the draft: second missing reference (Data); R1 no longer names the missing reference; R2 = keep
path/GUID/name, only add references (compared as JSON with Unity defaults filled, formatting free), runtime scripts
stay; R3 = the editor script ends up governed by a new `<Token>.Editor` asmdef (includePlatforms only Editor,
references core) — move it or place the asmdef over its folder; R4 = no content change, no new code file.

Assertions: A1 1.0 R1 compiled (project + combat + editor script) · A2 1.5 R2 · A3 1.75 R3 (governing asmdef computed
from disk: nearest folder with an .asmdef, or an .asmref naming one; editor script found by its C0 GUID) · A4 0.75
crit R4 (every C0 script found by GUID with its C0 content; no other .cs) · A5 1.0 R5 · A6 0.5 crit R7 · A7 0.5 crit
G1/G2 · A8 0.25 G4 (exempts .cs/.asmdef/.asmref(.meta) and folders under A/Scripts) · A9 0.5 R8/G8 (truthful: the
actual new asmdef or "" when none; the editor script's current path, or a .cs with its content when the GUID is gone;
the actual holder) · A10 2.25 R6 (power in Play Mode on the actual holder, passed).

Negatives (13): deleted_asmdef {A2} (the gameplay scripts fall into Assembly-CSharp, which auto-references core and
data, so it compiles); if_guard {A3,A4}; editor_no_platform {A3}; editor_folder_only {A3} (an `Editor` folder inside
the asmdef folder stays in that assembly); copied_report {A3,A4} (delete + create: new GUID, a new .cs); missing_data_ref
{A1,A5,A10}; not_attached {A5,A10}; changed_settings {A2} (allowUnsafeCode); wrong_fighter {A5,A6}; extra_script {A4};
wrong_report {A9}; outside_root {A7}; direct_file {A8}. Alternatives: guid_references (`GUID:<guid>` everywhere),
asmdef_in_tools (the asmdef placed over the script's folder, no move), save_run_scene.

Not verifiable offline: Unity's compile errors for the fixture exactly as modelled (CS0246 in Combat and Report only);
an empty Editor asmdef (no scripts yet) compiling cleanly; `asset_move` of a script triggering the recompilation
wait_idle sees; journal source of .asmdef files written directly and imported by the agent's own asset_refresh (either
way exempt). Expected: 20–30 minutes; pass rate 35–60% (finding both references from the errors, the Editor-folder
trap, keeping GUIDs, compile waits).

## DC3 — safe rename and move (concept D3; new)

Skills for the move (read from the docs and the [UnitySkill] declarations): `asset_move(sourcePath,
destinationPath)` (AssetDatabase.MoveAsset, keeps the GUID, parent folder must exist), `script_move(scriptPath,
newFolder)` (creates the folder), `script_rename(scriptPath, newName)` (file only, the class inside is not renamed),
`asset_create_folder(folderPath)` (one level, parent must exist), `script_delete` / `script_create` (the trap: new
GUID). Reads: `script_analyze(scriptName)` (public API of a loaded type, fullName with namespace),
`prefab_get_serialized_properties(prefabPath, componentType, gameObjectPath)` (in-memory prefab asset).

Fixture: `A/Scripts/<OldDir>/<Old>_<run>.cs` (speed float default 1.5, Transform[] route, pause float default 0.5,
`RouteLength()`, `LapSeconds()` = length / speed + pause x count), compiled at build. `R/<Route>` > WP_1..WP_3;
`R/<Squad>` > two plain holders (routes [WP1,WP2,WP3] and [WP3,WP1]), the instance of `A/<Dir>/<Unit>.prefab` (made
with prefab_create from a scene object whose route points at its own children P0/P1; its speed overridden after the
creation) and an instance of `A/<Dir>/<Unit>_<Kind>.prefab` (prefab_create_variant, speed overridden on the variant
with prefab_set_serialized_property). All values written with component_set_serialized_property (records prefab
instance modifications). Speeds and pauses are distinct samples that never equal the defaults. The target namespace
and class come from `names(seed)` (verbatim private copy, pinned); proposed public seed 12354 gives `Game.AI.Patroller`
as the concept asks; other seeds give e.g. `Game.Units.Warden`. Field-name pairs, folders, objects, positions and
the order of the squad's children are seeded too. The new class has no run id, so the build refuses (build_failed)
when a `.cs` under Assets already declares it (residue of a crashed run). 45 minutes.

Prompt: R1 class -> `<ns>.<Class>`, file to `A/Scripts/<ns path>/<Class>.cs`, keep the GUID (move/rename, not
delete + create), compiles; R2 two field renames, keep types/defaults/the pause field/the two methods; R3 the six
instances listed by path keep their values (incl. the instance override and the variant override), also for files
opened later (grounds FormerlySerializedAs, as in T9); R4 no skill writes of the five field names; R5 no other code,
objects or assets; R6 LapSeconds() on the four scene instances equals what their data gives; R7 answer: path, GUID,
full class name.

Assertions: Q1 1.0 R1 GUID at the new path, old file gone · Q2 0.75 R1 namespace + class + file name + compiled +
script_analyze fullName (the file is found by the original GUID, so "renamed in place" fails Q1 only) · Q3 0.75 R2
(declarations, defaults, old names gone, methods public) · Q4 0.75 R3 attributes · Q5 1.5 R3 four scene holders
(speed, pause, waypoint owners in order) · Q6 1.25 R3 prefab + variant via prefab_get_serialized_properties (waypoint
names) · Q7 0.5 R4 (property_changed from REST on the old/new class with a watched field) · Q8 0.5 crit R5 (other
scripts' sha, no added .cs, object states minus the holders' component lists) · Q9 0.25 crit G1/G2 · Q10 0.5 R7/G8
(truthful: the reported file's GUID and declared full name) · Q11 2.0 R6 fraction over the four holders
(LapSeconds() within 1e-3 relative of the C0 value + stored values in Play Mode) · Q12 0.25 G4.

Negatives (13): delete_recreate {Q1,Q5,Q6,Q11}; no_formerly {Q4,Q5,Q6,Q11} (defaults come back; arrays empty, so
LapSeconds is 0 rather than an exception); rename_and_reset {Q4,Q7}; not_moved {Q1}; namespace_missing {Q2} (the
scenario addresses the short class name); default_changed {Q3}; behaviour_changed {Q11} (no pause term);
compile_error {Q2,Q11} (a broken edit after the finished change: the good assembly stays loaded); extra_script {Q8};
renamed_squad {Q8}; wrong_report {Q10}; outside_root {Q9}; direct_file {Q12}. Alternatives: filesystem_move (new text
in the old file, .cs + .meta moved on disk, one refresh: one compilation), script_move_rename, save_run_scene.

Not verifiable offline (live checks, most important first): (1) the reference's asset_move leaves the class name and
the file name apart until the new text is imported, so for one compilation every instance has a missing script; the
model assumes Unity keeps the components' serialized data meanwhile (Unity's usual behaviour for missing scripts) —
if the live run shows data loss, swap reference and filesystem_move and state the order in the prompt; (2)
FormerlySerializedAs on the prefab-instance override and the variant override (property modifications saved under the
old name) — assumed handled by the prefab system; (3) component_get_serialized_properties / gameobject_find /
component_invoke_method resolving the short name `Patroller` of a namespaced class; (4) prefab_get_serialized_properties
values for references inside the prefab ("<id>:UnityEngine.Transform:P0"); (5) whether a script change reimports the
prefabs (journal noise only: imports by the editor under A are not direct writes unless they fall in the submit
refresh window). Expected: 20–35 minutes; pass rate 25–50% (FormerlySerializedAs on both fields, namespace, GUID-keeping
move, the variant).
