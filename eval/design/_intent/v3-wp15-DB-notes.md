# WP15 — DB1 / DB3 / DB5 design notes (wp15-oc-a)

Status 2026-10-17: drafted offline in worktree `eval-v3`. Dev/basic, caseVersion 1, requires
`{"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": false, "packages": []}`, 15 minutes each.
Offline tests: `eval/tests/runner/test_pack_dev_db.py` on `fake_dev_db.FakeEditorDB` (pack_offline harness plus the
private SCENARIO run on the model). Nothing committed; the shared manifest is integrated by wp11-tasks.

## Conventions shared by the three tasks (synced with wp11's T1 port)
- The agent writes one script `A/Scripts/<Class>.cs`; the class name carries a seeded word (`Spinner_Kestrel`) so
  it cannot collide with a type of a user project (basic allows `dedicated:false`). Field, method and default values
  are fixed by the prompt; the scene value differs from the code default so "default only" is caught.
- Static assertions read the script through `script_read` (no `open()` in the private module), the compile state
  through `debug_check_compilation` + `script_get_compile_feedback`, holders through `gameobject_find(component=)`,
  values through `component_get_serialized_properties`; "no other code file" walks `Assets/**/*.cs` against C0.
- SCENARIO is a callable returning `{"executionMode": "play", "timeoutSeconds": N, "steps": [...]}`; reads are
  post-processed in `verify_runtime`, the mandatory assert is `editor_get_state` select `isPlaying` expected `true`.
  `passed:true` already implies no Error/Exception log, so the runtime assertion folds "no error logged" (an R line
  says so explicitly) into the behavioural check.
- G4 (dev): `direct_writes()` minus `.cs`/`.cs.meta` records, like T1's B15.
- Reference solutions: `script_create(content=...)` → `ctx.wait_job(jobId)` → `ctx.wait_idle()` → `component_add` →
  `component_set_property` / `component_set_serialized_property` (→ `event_add_listener` for DB5).

## DB1 — Spinner
Fixture: `R/Yard/Mill_<a|b>` each with `Blades` (cube, tilted about X by 20/30/40 degrees) and `Shaft`; the subject
Blades is seeded. `spec(seed)`: speed 30/45/60, wait 1.5/2.0/2.5 s; code default fixed at 15 (chosen so that the
folded quaternion angle of default x wait never lands within tolerance of speed x wait).
Assertions C1 script shape + default (1.5) · C2 compiled (1.0) · C3 single holder on the subject with the stored
speed (2.0) · C4 no other code (0.75, critical) · C5 other objects unchanged (0.75, critical) · C6 runtime (2.5) ·
C7 answer (0.5) · C8 G1/G2 (0.5, critical) · C9 G4 (0.5).
Runtime: two Transform reads around `wait_seconds`; angle = quaternion angle between the local rotations, expected
speed x wait with tolerance max(10, 12%); axis of inverse(q1) * q2 must be the object's local Y (|y| >= 0.98), which
the tilt makes distinguishable from world Y.
Negatives: wrong_blades {C3,C5,C6}; default_only {C3,C6}; per_frame (no deltaTime, 60 fps in the model) {C6};
world_axis {C6}; wrong_default {C1}; compile_error {C2,C3,C6,C7}; extra_script {C4}; two_components {C3,C6};
wrong_report {C7}; outside_root {C8}; direct_file {C9}. Alternatives: quaternion_rotation (`localRotation *=
AngleAxis`, `[SerializeField] public`), save_run_scene.
Live uncertainties: the step overhead between the two reads (each read is one editor tick) and real frame rates for
per_frame (its folded angle is pseudo-random; a rare coincidence inside the tolerance would make that negative pass).

## DB3 — Blinker
Fixture: `R/Hall/Lamp_<a|b>`, two point lights (gameobject_create + component_add Light), both enabled; subject
seeded. `spec(seed)`: interval 0.8/1.0/1.2 s; code default fixed 0.5.
Assertions D1..D9 mirror DB1 (D5 also checks both lights still enabled). Runtime: the Light's `enabled` flag as
`component_list` reports it (the serialized `m_Enabled` is hidden from `NextVisible`, so the serialized reader is not
used), sampled right after start and at 0.5, 1.5, 2.5, 3.5 intervals → on, on, off, on, off (tolerant to roughly a
third of an interval of scheduling drift).
Negatives: wrong_lamp {D3,D5,D6}; default_only {D3,D6}; toggle_on_start {D6}; half_period {D6}; wrong_default {D1};
compile_error {D2,D3,D6,D7}; extra_script {D4}; two_components (toggles cancel) {D3,D6}; wrong_report {D7};
outside_root {D8}; direct_file {D9}. Alternatives: invoke_repeating, save_run_scene.
Live uncertainties: `wait_seconds` uses wall-clock ticks while the script uses game time (equal at timeScale 1); the
first read lands two frames after Play starts.

## DB5 — ClickCounter
Fixture: `R/Canvas_<a>` (ui_create_canvas, reparented under R) with `Btn_<b>`, `Btn_<c>` (ui_create_button) and
`Count_<d>` (ui_create_text "0"); subject button seeded; class `ClickCounter_<e>`.
Assertions E1 script shape (class, `Text label`, `public void OnClick()`) (1.5) · E2 compiled (0.75) · E3 holder +
label references the Text component of the label object (1.5) · E4 exactly one persistent listener: method OnClick,
targetType the class, state RuntimeOnly/EditorAndRuntime, bound to the button (path or entityId) (1.75) · E5 other
button's onClick empty, label still "0", other objects unchanged, no other code (0.75, critical) · E6 runtime (2.25)
· E7 answer (0.5) · E8 G1/G2 (0.5, critical) · E9 G4 (0.5).
Runtime: `component_invoke_method(Button.OnSubmit, arguments "[null]")` twice (the pointer-free click path: Press()
→ onClick.Invoke; null is accepted for the BaseEventData parameter by ConvertArgument), then Text `m_Text` == "2".
Negatives: runtime_add_listener {E4}; wrong_button {E3,E4,E5,E6}; no_label {E3,E6}; off_state {E4,E6}; wrong_method
{E4,E6}; text_not_updated {E6}; both_buttons {E5}; wrong_field_name {E1,E3}; compile_error {E2,E3,E4,E6,E7};
extra_script {E5}; wrong_report {E7}; outside_root {E8}; direct_file {E9}. Alternatives: editor_and_runtime,
save_run_scene.
Live uncertainties: `OnSubmit(null)` through component_invoke_method in Play Mode (fallback: invoke
`ClickCounter.OnClick` directly and rely on E4 for the wiring); `event_get_listeners.targetType` is assumed to be the
component's type name; `label` written with `component_set_serialized_property(referencePath, objectType="Text")`.

## Offline model (fake_dev_db.py)
`FakeEditorDB` adds UGUI creation, persistent listeners, `script_read`, `Button.OnSubmit`, Light/Text/Button
serialized reads and a clock: waits advance game time (60 fps for frame counts). A script without a marker behaves
as the task asks (speed x dt about local Y; Light toggle every interval starting one interval in; OnClick counts and
writes every assigned Text field, an unassigned one flags a runtime error like Unity's NullReferenceException). The
negatives' sources carry a `// usk-fake: <variant>` comment the model reads (per_frame, world_axis, toggle_on_start,
half_period, runtime_listener, no_text_write) — the same convention wp15-oc-b uses for DS1/DS3. It models the
solutions, not C#.
