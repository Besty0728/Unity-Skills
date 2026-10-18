# DC4 — Ballistic Launcher Design Notes

Status 2026-10-18: drafted offline for the UnitySkills v3.0 evaluation pack (`track = "dev"`, `tier = "challenge"`, `caseVersion = 1`, `minutes = 45`, `needs_focus = True`, `requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}`).

## Concept & Fixture (`SkillsForUnity/Evaluation~/pack/tasks/DC4/task.py`)

- **Fixture structure**:
  - Sets project gravity to a seeded vertical value `gravityY in {-12.0, -14.0, -15.0, -16.0}` via `physics_set_gravity` and saves the prior gravity vector in `handles["prevGravity"]` so `teardown()` restores it idempotently.
  - Creates a projectile prefab `A/Prefabs/<Shell>_<word>_<run>.prefab` from a Sphere with a `Rigidbody` whose `mass in {1.5, 2.0, 2.5, 3.0}` and linear `m_Drag in {0.35, 0.45, 0.55}` (`useGravity = true`, `isKinematic = false`) are configured before `prefab_create`.
  - Under `R/<Range>` (seeded position and yaw), builds two sibling launcher rigs in seeded order:
    - `<Battery>` (seeded position and yaw) > `<Muzzle>` (seeded local offset, pitch and yaw).
    - `<Stand>` (decoy rig) > `<Muzzle>` (same child name as the real muzzle so name-only lookups are ambiguous).
  - Under `R/<Range>/<Deck>` (seeded yaw), creates 3 cylindrical landing pads (`<Pad>_1..3`) at distinct world offsets and heights (`heights` sampled from `(-0.6, 0.8, 2.2, 3.1)` relative to the muzzle). Pad names and paths are not exposed in `prompt_vars` (hidden targets passed to `Fire(Transform)` at verification time).
- **Prompt requirements (`R1`–`R7`)**:
  - `R1`: Create `A/Scripts/<cls>.cs` defining `MonoBehaviour` class `<cls>` with serialized fields `muzzle` (`Transform`) and `projectilePrefab` (`GameObject`) and `public GameObject Fire(Transform target)`. Must compile without errors.
  - `R2`: `Fire(Transform target)` instantiates `projectilePrefab` at `muzzle.position`, preserves the prefab's `Rigidbody` settings (`mass`, `drag`, `useGravity`, non-kinematic), launches it via Unity physics (`AddForce` or velocity) accounting for active `Physics.gravity`, `Rigidbody` mass and linear drag, and `Time.fixedDeltaTime`, and returns the spawned `GameObject`. Prohibits teleporting, parenting to target, kinematic/no-gravity overrides, or driving position in `Update`/`FixedUpdate`/coroutines.
  - `R3`: Attach `<cls>` to `<Battery>` only, wiring `muzzle` and `projectilePrefab`.
  - `R4`: Do not modify the projectile prefab, `Physics.gravity`, `Time.fixedDeltaTime`, or existing scene objects/assets.
  - `R5`: Do not add or modify any other code file.
  - `R6`: Runtime scenario calls `Fire(Transform)` 3 times (once for each hidden target pad); each returned projectile must be a distinct instance of the prefab with its `Rigidbody` settings intact, initial speed >= 1.0 m/s from the muzzle, and trajectory reaching within `0.25` m of `target.position` within `2.5` s with no runtime errors.
  - `R7`: Report `scriptPath`, `launcherPath`, and `projectilePrefabPath` in the answer.

## Assertions & Weights (`eval/pack/private/DC4.py`)

- `D1` (0.75, `R1`): Script declares `<cls>`, serialized fields `muzzle` and `projectilePrefab`, and `public GameObject Fire(Transform)`.
- `D2` (0.50, `R1`): Script exists at `A/Scripts/<cls>.cs`, project compiled without errors, class loaded.
- `D3` (1.25, `R3`): Single component in scene on `<Battery>`, `muzzle` and `projectilePrefab` wired to the target Transform and prefab.
- `D4` (0.75, critical, `R4`): Prefab SHA/GUID, `Physics.gravity`, and all C0 scene objects unchanged.
- `D5` (0.50, critical, `R5`): No extra or modified C# files under `Assets`.
- `D6` (0.50, critical, `G1`, `G2`): No scope violations outside `R`/`A` or foreign scene operations.
- `D7` (0.25, `G4`): No direct disk writes under `A` other than `.cs`/`.cs.meta`.
- `D8` (0.50, `R7`, `G8`): Answer truthfully reports `scriptPath`, `launcherPath`, `projectilePrefabPath`, and `declaredComplete`.
- `D9` (1.25, `R2`, `R6`): In Play Mode each `Fire(target)` call returns a newly spawned projectile at `muzzle.position` keeping the prefab's non-kinematic `Rigidbody` settings (`mass`, `drag`, `useGravity`) and launch speed >= 1.0 m/s.
- `D10` (3.75, fraction, `R2`, `R6`): Fraction of the 3 shots whose free-flight ballistic trajectory under active gravity and drag reaches within `0.25` m of its target pad with no error logged.

## Solutions, Traps & Negatives (`eval/pack/solutions/DC4.py`)

- **Reference (`reference`)**: Solves the discrete FixedUpdate recurrence with `factor = Mathf.Clamp01(1f - rb.drag * dt)` over 60 steps (`1.2` s) under `Physics.gravity` in world space and launches via `rb.AddForce(v0, ForceMode.VelocityChange)`.
- **Alternatives (`ALTERNATIVES`)**:
  - `impulse_with_mass`: `[SerializeField] private` fields, reciprocal damping factor `1f / (1f + rb.drag * dt)` over 65 steps, and `rb.AddForce(v0 * rb.mass, ForceMode.Impulse)`.
  - `save_run_scene`: Reference solution followed by `scene_save`.
- **Negatives (`NEGATIVES`, 15 total)**:
  - `no_drag` (`{D10}`): Closed-form vacuum parabola `(disp - 0.5f * g * t * t) / t` ignoring linear drag; falls 1.5–3.0 m short.
  - `wrong_forcemode` (`{D9, D10}`): Passes `v0` with `ForceMode.Force` (scales by `dt / mass`, giving initial speed < 1.0 m/s).
  - `wrong_gravity` (`{D10}`): Hard-codes `(0, -9.81f, 0)` instead of reading `Physics.gravity`.
  - `fixed_angle` (`{D10}`): Fires along `(muzzle.forward + Vector3.up * 0.5f).normalized * 12f`.
  - `first_target_only` (`{D10}`): Caches the first target's position and fires all 3 shots there (1/3 hits -> fraction 0.333, non-strict).
  - `local_space_muzzle` (`{D10}`): Solves displacement using `muzzle.InverseTransformPoint(target.position)` without rotating the velocity vector back to world space.
  - `strip_rigidbody_values` (`{D9, D10}`): Overwrites `rb.mass = 1f; rb.drag = 0f;` on the spawned instance.
  - `on_decoy_stand` (`{D3, D9, D10}`): Attaches the component to the decoy `<Stand>` rig and wires the decoy muzzle.
  - `field_renamed` (`{D1, D3, D9, D10}`): Renames `muzzle` to `barrel`.
  - `modified_gravity` (`{D4, D9, D10}`): Resets `Physics.gravity` to `-9.81` in Edit Mode.
  - `compile_error` (`{D2, D3, D9, D10}`): Introduces a syntax error so compilation fails.
  - `extra_script` (`{D5}`): Adds an extra helper C# script.
  - `wrong_report` (`{D8}`): Reports `<Stand>` in `launcherPath`.
  - `outside_root` (`{D6}`): Creates a stray GameObject outside `R`.
  - `direct_file` (`{D7}`): Writes a non-script file directly to disk under `A`.

## Live-Editor Notes for Follow-Up

- In live Unity, `ComponentInvocationService.BoundedValue` serializes a returned `UnityEngine.GameObject` to `{entityId, instanceId, name, type}`. To inspect the spawned projectile's runtime `Rigidbody` velocity/settings or impact position in live Play Mode without relying on enriched `returnValue` fields, either:
  1. `ComponentInvocationService.BoundedValue` on a `GameObject` can include basic transform/rigidbody summary when present, or
  2. `RuntimeScenarioService` can allow a `read` step to reference the previous `invoke` step's returned `entityId` (or find the newly spawned `<Shell>(Clone)` via `gameobject_find` / `component_get_properties`).
  In the offline model (`fake_dc4.py`), `Fire(Transform)` returns `{instanceId, entityId, name, type}` along with the spawned Rigidbody's launch telemetry so `D9` and `D10` grade every trap deterministically offline.
